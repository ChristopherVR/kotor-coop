using System.Linq;
using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

// Moves enemy state between the game and the session in whichever direction this peer is
// responsible for. Exactly one peer owns enemies; it publishes what its own engine says, and
// everyone else applies that and is prevented from killing anything locally. See
// docs/research/enemy-materialisation.md and docs/research/combat-when-apart.md.
internal sealed partial class CombatPump
{
    /// <summary>Owner-authority side only: enemy ids read as alive on the last clean census in
    /// <see cref="_deathCensusModule"/>. An id that drops out of this set with a
    /// <c>Dead</c> reading is a genuine death; one that drops out silently (out of census
    /// range, cap-shed) is not — see <see cref="DetectMaterialisedDeaths"/>.</summary>
    private readonly HashSet<uint> _knownAliveEnemies = [];

    /// <summary>The module <see cref="_knownAliveEnemies"/> was built for. Object ids are
    /// reassigned per module load, so a module change must clear the set rather than reading
    /// last module's ids as freshly dead this module.</summary>
    private string _deathCensusModule = string.Empty;

    /// <summary>Owner side: every peer's cumulative damage, folded into one monotonic total
    /// per enemy for our own engine to spend.</summary>
    private readonly PeerDamageLedger _peerDamage = new();

    /// <summary>
    /// Moves enemy state between the game and the session in whichever direction this peer
    /// is responsible for. The two directions carry deliberately different things. DOWN from
    /// the owner comes health, absolutely, because it is the owner's truth about a creature.
    /// UP from a peer comes DAMAGE DEALT, cumulatively, because a peer's contribution is not a
    /// fact about the creature at all — it is a fact about the peer, and trying to express it
    /// as health meant sampling a number the owner's own mirror kept overwriting.
    /// </summary>
    public void PumpEnemies(
        in PlayerState local, IReadOnlyList<RemotePeer> peersHere, SessionCapabilities caps)
    {
        if (!bridge.SupportsEnemySync || (caps & SessionCapabilities.Enemies) == 0)
        {
            return;
        }

        // ⚠ The module label must be read in the same breath as the world data it labels.
        // `local.Module` is RETAINED across a failed read, and the census and damage
        // channels read or write the LIVE world — so around a module transition the two
        // can belong to different worlds. Measured on the tar_m03aa warp: for a few
        // seconds after arrival the owner published a fresh Taris census labelled
        // END_M01AA, which sailed through the module gate of a client genuinely standing
        // on the Endar Spire and drove Taris ids onto its creatures. A skewed or
        // unreadable label skips the world-labelled operations below — they are levels,
        // so the next clean tick republishes them. (The encounter-set publish is NOT
        // gated on this: it must keep superseding with an empty set on a fallback name.)
        string censusModule = bridge.TryReadCurrentModule(out string liveModule)
            ? liveModule
            : local.Module;
        bool moduleClean = censusModule.Length > 0
            && !ModuleNameGuard.IsFallbackModuleName(censusModule)
            && string.Equals(censusModule, local.Module, StringComparison.OrdinalIgnoreCase);

        // Where the other players are, so the census cap sheds by distance to the nearest
        // of us rather than to whoever happens to be publishing. `peersHere` is already
        // module-filtered, which is what makes these positions comparable at all.
        Vector3f[] otherPlayers = new Vector3f[peersHere.Count];
        for (int i = 0; i < peersHere.Count; i++)
        {
            otherPlayers[i] = peersHere[i].State.Position;
        }

        if (session.IsEnemyAuthority)
        {
            // The owner's own engine fires its own encounters; only the peers that had
            // theirs disarmed need the watch.
            bridge.SetEncounterWatch(false);
            triggerPump.PumpEncounterRequests(local);
            bridge.SetTriggerWatch(false);
            triggerPump.PumpTriggerFiring(local);

            // Census and damage carry the module label; a dirty label skips them (see the
            // same-breath note above).
            if (!moduleClean)
            {
                return;
            }

            // Hold the last snapshot rather than publishing while in a conversation. The
            // dialogue system wipes every hostile's action queue in this instance on every
            // tick, so what we would send is a pacified fight — and the peer, who is not
            // talking, would have their own fight go quiet for the duration.
            //
            // The same holds for a minigame: whatever the engine's creature census reports
            // during a swoop race or a turret sequence is not the shared-world fight, and
            // publishing it would send every peer's mirror chasing a fight that is not
            // happening. See PlayerState.FlagInMinigame and docs/research/minigames.md.
            // MinigameRule.ShouldSuppressPresencePublish ignores both of its toggles by
            // design (see MinigameRuleTests) so a default instance answers it correctly.
            if (!bridge.IsInDialogue && !default(MinigameRule).ShouldSuppressPresencePublish(bridge.IsInMinigame))
            {
                List<EnemyState> ownedEnemies = bridge.ReadEnemies(otherPlayers);
                session.PublishEnemies(censusModule, ownedEnemies);
                DetectMaterialisedDeaths(censusModule, ownedEnemies);
            }

            // Where a peer's damage becomes real. Its copies are pinned at 1 hp, so its
            // whole fight would be cosmetic unless this side spends the damage it has
            // dealt — as REAL engine damage on our own copy, which is also how the kill
            // happens: an enemy whose accumulated damage exceeds its health dies here, with
            // the corpse, loot, XP and OnDeath the engine runs for any other death.
            // Published every pass, even when it is empty. The epoch travels with it, and
            // the plugin needs to hear about a module change BEFORE the first damage in the
            // new module rather than after — otherwise it would spend the new totals against
            // the old module's applied figures, which name different creatures.
            bridge.TryPublishDamageOwed(
                _peerDamage.Epoch,
                _peerDamage.Merge(censusModule, session.GetEnemyDamage(censusModule)));

            return;
        }

        // This peer mirrors the owner's enemies, so its own encounters must never fire:
        // an encounter spawn here would shift the object-id counter and break the
        // id-identity the whole enemy channel keys on. Cheap once done — the bridge
        // remembers the module and the call is a no-op until it changes.
        int disabled = bridge.DisableLocalEncounters();
        if (disabled > 0)
        {
            log($"disabled {disabled} local encounter(s); the enemy owner's spawns are authoritative");
        }

        // Disarmed does not have to mean unreachable. The plugin watches which activate
        // areas this player is standing in and the owner fires them on our behalf, so
        // walking into an ambush works for whoever walks in — see
        // docs/research/client-encounters.md.
        bridge.SetEncounterWatch(true);
        session.PublishEncounterTriggers(
            local.Module,
            // "areaXX" means the module name was unreadable, which means a transition is
            // mid-flight — the same guard PumpTravel needs. Reporting then would name this
            // area's encounter ids under a module string that matches nothing, and the ids
            // themselves are about to be reassigned. Report an empty set instead of
            // reporting nothing, so a set published a moment ago is superseded rather than
            // left to age out on the host.
            ModuleNameGuard.IsFallbackModuleName(local.Module) ? [] : bridge.ReadEnteredEncounters());

        // Generic triggers, same shape, same fallback-module guard. The plugin's own watch
        // already excludes traps, area transitions and anything failing the coop.ini
        // allowlist pattern, so everything the bridge hands back here is a candidate story
        // trigger. One more filter happens here, purely to save bandwidth: a trigger id the
        // host has already told us it fired (GetFiredTriggerIds) is dropped before it is
        // even reported again — the host's own store would no-op it anyway, but there is no
        // reason to keep asking.
        bridge.SetTriggerWatch(true);
        if (ModuleNameGuard.IsFallbackModuleName(local.Module))
        {
            session.PublishTriggerEntries(local.Module, []);
        }
        else
        {
            IReadOnlyList<uint> entered = bridge.ReadEnteredTriggers();
            if (entered.Count > 0)
            {
                IReadOnlyList<uint> alreadyFired = session.GetFiredTriggerIds(local.Module);
                entered = alreadyFired.Count == 0
                    ? entered
                    : [.. entered.Where(id => !alreadyFired.Contains(id))];
            }

            session.PublishTriggerEntries(local.Module, entered);
        }

        // Protocol 36's reverse relay: publish this client's own natively-fired triggers, and
        // fire (locally) any OTHER peer's — including the host's — that this engine has not
        // yet been asked to run. See TriggerPump.PumpTriggerFiring's own doc comment.
        triggerPump.PumpTriggerFiring(local);

        // DIAGNOSTIC (temporary, kept until the moduleClean/exclusion interaction below is
        // confirmed live): see docs/research/combat-when-apart.md.
        log($"local combat diag (agent): moduleClean={moduleClean} censusModule='{censusModule}' "
            + $"local.Module='{local.Module}'");

        // The enemy apply and the damage report are world-labelled; a dirty label skips
        // them (see the same-breath note above).
        if (!moduleClean)
        {
            return;
        }

        // Module-gated: the owner's ids only mean anything while we are where they were
        // read. Apart, this is empty and our own copies are left alone rather than driven
        // onto whatever local creatures share those ids.
        //
        // ⚠ And an empty list is WRITTEN, not skipped. Skipping was right when empty only
        // ever meant "the host has not sent one yet"; with the gate it also means "these ids
        // do not belong here", and leaving the previous snapshot in the shared block would
        // let the plugin keep driving the old module's ids onto the new module's creatures —
        // exactly the thing the gate exists to stop. Writing a count of zero clears them.
        //
        // The plugin's own local_combat exclusion filters here, not before — the list itself
        // must still exist upstream (the owner keeps publishing normally; only THIS
        // instance's own presence array omits it), so a bounded timeout or the fight ending
        // cleanly resumes normal mirroring on the very next pump with no separate "un-exclude"
        // round trip.
        IReadOnlyList<EnemyState> enemies = session.GetEnemies(local.Module);
        IReadOnlyList<uint> excludedIds = bridge.ReadLocalCombatExcludedIds();
        if (excludedIds.Count > 0)
        {
            List<EnemyState> filtered = new(enemies.Count);
            foreach (EnemyState enemy in enemies)
            {
                if (!excludedIds.Contains(enemy.ObjectId))
                {
                    filtered.Add(enemy);
                }
            }

            // DIAGNOSTIC (temporary): confirms the exclusion filter is actually removing every
            // excluded id before assuming a bug lies elsewhere. See
            // docs/research/combat-when-apart.md.
            log($"local combat diag (agent): excludedIds=[{string.Join(",", excludedIds.Select(id => $"0x{id:X}"))}] "
                + $"enemies.Count={enemies.Count} filtered.Count={filtered.Count} "
                + $"(removed {enemies.Count - filtered.Count})");

            enemies = filtered;
        }

        bool wrote = bridge.TryWriteEnemies(enemies);
        if (excludedIds.Count > 0)
        {
            log($"local combat diag (agent): TryWriteEnemies returned {wrote}, wrote {enemies.Count} enemies");
        }

        // Report what we have DEALT, so our fight counts.
        //
        // ⚠ Not what we have OBSERVED. This used to send our copies' health and let the
        // owner take min(), and it lost most of every fight: the owner writes its own value
        // back onto our copy several times a second, so a hit had to win a race against the
        // next mirror pass to be sampled at all. Measured — a client attacking one soldier
        // for ninety seconds moved it from 15 hit points to 11 and then never again. A
        // cumulative sum of damage dealt cannot be raced, because nothing ever reads it back
        // out of the game.
        //
        // Not during dialogue, for the same reason the census pauses: the local fight is
        // disarmed and unrepresentative. Nothing is lost — the totals are cumulative, so
        // whatever accrued arrives whole on the next tick that is allowed to send.
        if (!bridge.IsInDialogue)
        {
            (uint generation, List<EnemyDamageEntry> dealt) = bridge.ReadDamageDealt();
            if (dealt.Count > 0)
            {
                session.ReportEnemyDamage(local.Module, generation, dealt);
            }
        }
    }

    /// <summary>
    /// Owner-authority side: notices which enemies just died and captures their real corpses'
    /// actual loot for <see cref="ICoopSession.PublishMaterialisedLoot"/> — the automatic wire
    /// docs/research/enemy-materialisation.md §6. A death is an id dropping out of
    /// <see cref="_knownAliveEnemies"/> WITH a <c>Dead</c> reading, not merely dropping out of
    /// the census — the census sheds by distance and cap, and a creature that is merely far
    /// away must never be read as a kill.
    ///
    /// One <see cref="IGameBridge.ReadPossessedItems"/> sweep for however many died on this
    /// tick, not one per creature — see that method's own doc for why a per-death call would
    /// be a real cost against an ambush wave dying together.
    /// </summary>
    private void DetectMaterialisedDeaths(string module, IReadOnlyList<EnemyState> enemies)
    {
        if (!string.Equals(module, _deathCensusModule, StringComparison.OrdinalIgnoreCase))
        {
            // A fresh module: ids are reassigned per load, so anything remembered from the
            // last one names a different creature now (or nothing at all). Seed from this
            // census rather than reading every currently-alive id as freshly dead.
            _deathCensusModule = module;
            _knownAliveEnemies.Clear();
            foreach (EnemyState enemy in enemies)
            {
                if (!enemy.Dead)
                {
                    _knownAliveEnemies.Add(enemy.ObjectId);
                }
            }

            return;
        }

        HashSet<uint>? newlyDead = null;
        foreach (EnemyState enemy in enemies)
        {
            if (enemy.Dead)
            {
                if (_knownAliveEnemies.Remove(enemy.ObjectId))
                {
                    (newlyDead ??= []).Add(enemy.ObjectId);
                }
            }
            else
            {
                _knownAliveEnemies.Add(enemy.ObjectId);
            }
        }

        if (newlyDead is null)
        {
            return;
        }

        log($"materialised loot: {newlyDead.Count} enemy death(s) noticed this pass: "
            + string.Join(", ", newlyDead.Select(id => $"0x{id:X}")));

        Dictionary<uint, List<MaterialisedLootItem>> byOwner = [];
        foreach ((uint possessor, string resref, ItemIdentity identity) in
            bridge.ReadPossessedItems(newlyDead))
        {
            if (resref.Length == 0)
            {
                continue;
            }

            if (!byOwner.TryGetValue(possessor, out List<MaterialisedLootItem>? items))
            {
                items = [];
                byOwner[possessor] = items;
            }

            items.Add(new MaterialisedLootItem(resref, identity));
        }

        foreach (uint deadId in newlyDead)
        {
            if (byOwner.TryGetValue(deadId, out List<MaterialisedLootItem>? items) && items.Count > 0)
            {
                session.PublishMaterialisedLoot(deadId, items);
                log($"materialised loot: captured {items.Count} item(s) off 0x{deadId:X}'s corpse, publishing");
            }
            else
            {
                log($"materialised loot: 0x{deadId:X} died with nothing to capture");
            }
        }
    }
}
