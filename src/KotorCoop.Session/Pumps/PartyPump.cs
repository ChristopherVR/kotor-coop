using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// Keeps the party companions single and coherent (who they are, where they are) and shares
/// progression (gold, experience). The host owns the companions and the purse; every client
/// freezes its own copies and mirrors the host's. See docs/research/party-reconciliation.md.
/// </summary>
internal sealed class PartyPump(IGameBridge bridge, ICoopSession session, AgentSettings settings, Action<string> log)
{
    /// <summary>
    /// Keeps the party companions single and coherent. The host owns them — its own engine
    /// AI drives Trask/Carth normally, and it just publishes where they are. Every client
    /// freezes its own copies (solo mode) and mirrors the host's, so the companions are one
    /// synchronised set rather than two that drift apart. Host-authoritative, so exactly one
    /// instance's AI is ever in charge.
    /// </summary>
    public void PumpParty(SessionCapabilities caps)
    {
        if (!settings.SyncParty || !bridge.SupportsPartySync || (caps & SessionCapabilities.Party) == 0)
        {
            return;
        }

        if (session.IsEnemyAuthority)
        {
            // Hold the last snapshot rather than publishing while the host itself is in a
            // minigame — same reasoning as PumpEnemies: whatever the engine reports for the
            // companions during a swoop/turret sequence is not the shared-world party.
            // MinigameRule.ShouldSuppressPresencePublish covers the enemy AND party census
            // alike (see its doc comment) and ignores both of its toggles by design, so a
            // default instance answers it correctly.
            if (default(MinigameRule).ShouldSuppressPresencePublish(bridge.IsInMinigame))
            {
                return;
            }

            // The host: publish, do not freeze. Its companions follow through the game's
            // own AI exactly as in single-player.
            session.PublishParty(bridge.ReadPartyMembers());
            return;
        }

        // A client: freeze the local party AI and mirror the host's companions. The freeze
        // is re-asserted inside TryWritePartyMembers every pass. (The input control-lock that
        // stops the client controlling those companions runs every tick in the fast path.)
        bridge.TryWritePartyMembers(session.GetPartyMembers());
    }

    /// <summary>
    /// Keeps the companion ROSTER — who is recruited and who is walking with the player — the
    /// same on both instances. <see cref="PumpParty"/> carries where the companions are; this
    /// carries which companions exist.
    ///
    /// The failure it fixes: a conversation that recruits or dismisses a companion calls the
    /// engine's AddPartyMember / RemovePartyMember, and those ran on whichever instance was
    /// executing the dialogue's scripts. The other instance's party table never heard, and the
    /// roster is the KEY the companion mirror resolves by — PartyPresenceController looks up
    /// "the host's NPC slot 2" in its OWN party table — so after a divergence the mirror is
    /// either driving nothing or driving the wrong body.
    ///
    /// Host-authoritative, matching every other companion channel. The host publishes a level;
    /// a client hands that level to its plugin, which reconciles through the engine's own
    /// roster calls on the frame hook. Runs on the combat pump rather than the 30 Hz tick: a
    /// roster changes a few times per playthrough, and each reconciliation is a VM dispatch.
    /// </summary>
    public void PumpPartyRoster(SessionCapabilities caps)
    {
        // ⚠ `settings.SyncParty` and the `Party` capability bit are session-lifetime facts —
        // once either says "off", it stays off until something re-negotiates the session, so
        // clearing the block immediately below is correct. `bridge.SupportsPartySync`
        // (`_player is not null`) is NOT session-lifetime — it reads false for roughly a
        // second on EVERY module transition, while this instance's own player object is torn
        // down and rebuilt. Clearing the block for that alone was live-reproduced: a cold
        // two-instance session, one ordinary door crossing, and the CLIENT's own log showed
        // "party roster: reconciling against a peer party of 0x001" immediately followed
        // (same transition, ~8s later) by "party roster: no peer roster to reconcile against"
        // — the exact clear this method would write for a host that never stopped publishing
        // anything, if it treated the two conditions the same. A held-stale value is exactly
        // what `CoopClient.TryGetPartyRoster`'s own doc comment already says the reader wants
        // ("deliberately NOT aged out... a stale one is still the last thing the host said").
        // See docs/research/party-reconciliation.md.
        if (!settings.SyncParty || (caps & SessionCapabilities.Party) == 0)
        {
            // ⚠ CLEAR ON THE WAY OUT, do not just stop writing. A level the plugin is still
            // holding from before the channel was disabled would keep being reconciled against
            // forever — the exact "a stale authority outlives the thing that authorised it"
            // failure the lease channels are shaped to avoid.
            bridge.WriteHostPartyRoster(false, PartyRoster.Empty);
            return;
        }

        if (!bridge.SupportsPartySync)
        {
            // Transient: hold whatever is already in the block (host roster, local roster,
            // both) rather than clearing either — the very next pump, once the player object
            // resolves again, picks back up exactly where the last real read left off.
            return;
        }

        if (session.IsEnemyAuthority)
        {
            // The host: publish its own table, and never reconcile against anyone. Writing the
            // cleared word here is what makes "am I being told a roster" the plugin's whole
            // test — it does not need to know which end of the session it is on.
            if (bridge.TryReadLocalPartyRoster(out PartyRoster local))
            {
                session.PublishPartyRoster(local);
            }

            bridge.WriteHostPartyRoster(false, PartyRoster.Empty);
            return;
        }

        // A client. TryGetPartyRoster is false until the first level has arrived, and that
        // distinction is load-bearing: an empty roster and an unheard-of one are the same two
        // masks, and applying the second would dismiss the player's whole party.
        bridge.WriteHostPartyRoster(
            session.TryGetPartyRoster(out PartyRoster host), host);
    }

    /// <summary>The local gold reading the last pass, so a change made BY THE GAME can be told
    /// from one this agent wrote. -1 until the first observation, which only baselines.</summary>
    private long _lastObservedGold = -1;

    /// <summary>This instance's contribution to the shared purse: everything its own engine
    /// has put in and taken out since the ledger started.</summary>
    private uint _goldEarned;
    private uint _goldSpent;

    /// <summary>Bumped whenever the totals above restart, so the host can tell a RESET from a
    /// stale datagram. Seeded from the clock because an agent re-attaching mid-session must
    /// not reuse the epoch its predecessor's totals were counted in.</summary>
    private readonly uint _progressionGeneration = unchecked((uint)Environment.TickCount64);

    /// <summary>What each peer's ledger had already been spent against this purse, so an
    /// increase is applied once. Keyed by peer, valued by (generation, earned, spent).</summary>
    private readonly Dictionary<uint, (uint Generation, uint Earned, uint Spent)> _appliedProgression = [];

    /// <summary>
    /// Shares progression. The host's party table IS the purse; clients report what their own
    /// engine put in and took out, the host spends each increase against its table, and the
    /// resulting balance is broadcast absolutely for everyone to mirror.
    ///
    /// ⚠ GOLD AND EXPERIENCE DO NOT TRAVEL THE SAME WAY, and the asymmetry is the whole
    /// design. Gold is the one shared value that goes DOWN, so it cannot be merged as a level
    /// — max would resurrect anything anyone spent, and last-writer-wins is what stopped a
    /// client buying anything at all (their purchase was restored by the host's next
    /// snapshot). It travels as a cumulative ledger instead, the shape the damage channel
    /// already proved.
    ///
    /// Experience is the opposite. It cannot be a ledger, because both engines ALREADY grant
    /// kill XP independently: a client's mirrored enemy dies through the engine's real damage
    /// pipeline (<c>KillCreature</c> applies a lethal effect), so <c>OnApplyDeath</c> and
    /// <c>ApplyDeathExperience</c> run on the mirror exactly as on the owner. Forwarding a
    /// delta would count every kill in the game twice. It is absolute and merges by max, which
    /// carries the thing that genuinely diverges — script-granted XP, from a conversation or a
    /// stealth award only one instance ran — and leaves the rest alone.
    /// </summary>
    public void PumpProgression(SessionCapabilities caps)
    {
        if (!settings.SyncProgression
            || !bridge.SupportsProgressionSync
            || (caps & SessionCapabilities.Progression) == 0)
        {
            return;
        }

        if (!bridge.TryReadProgression(out uint gold, out uint experience))
        {
            return;
        }

        ObserveLocalGold(gold);

        if (session.IsEnemyAuthority)
        {
            PumpProgressionHost(gold, experience);
            return;
        }

        // A client: report our own contribution, then mirror whatever the host makes of it.
        session.ReportProgression(_progressionGeneration, _goldEarned, _goldSpent, experience);

        if (session.TryGetProgression(out uint hostGold, out uint hostExperience))
        {
            // Never below our own XP: a max on both ends means neither instance can pull the
            // other backwards while a report is in flight.
            if (bridge.TryWriteProgression(hostGold, Math.Max(hostExperience, experience)))
            {
                _lastObservedGold = hostGold;
            }
        }
    }

    /// <summary>
    /// The host: spend every client's outstanding contribution against the party table, then
    /// publish the result.
    /// </summary>
    private void PumpProgressionHost(uint gold, uint experience)
    {
        long purse = gold;
        uint mergedExperience = experience;

        foreach (PeerProgression report in session.GetProgressionReports())
        {
            mergedExperience = Math.Max(mergedExperience, report.Experience);

            if (!_appliedProgression.TryGetValue(report.PeerId, out (uint Generation, uint Earned, uint Spent) applied)
                || applied.Generation != report.Generation)
            {
                // First sight of this peer's ledger, or the ledger restarted. Adopt the
                // totals WITHOUT spending them: a re-attached agent re-reports everything it
                // ever counted, and applying that as new activity would hand the party a
                // second copy of a session's earnings.
                _appliedProgression[report.PeerId] = (report.Generation, report.GoldEarned, report.GoldSpent);
                continue;
            }

            // Only increases. A total that went backwards inside one generation is a
            // reordered datagram, and the next one supersedes it.
            long earned = report.GoldEarned > applied.Earned ? report.GoldEarned - applied.Earned : 0;
            long spent = report.GoldSpent > applied.Spent ? report.GoldSpent - applied.Spent : 0;

            if (earned == 0 && spent == 0)
            {
                continue;
            }

            purse += earned - spent;

            _appliedProgression[report.PeerId] = (
                report.Generation,
                Math.Max(applied.Earned, report.GoldEarned),
                Math.Max(applied.Spent, report.GoldSpent));

            log($"purse: peer {report.PeerId} contributed +{earned} -{spent}, party gold {gold} -> {purse}");
        }

        // The party cannot owe money: the engine's gold is unsigned, and a negative write
        // would wrap to something enormous.
        purse = Math.Clamp(purse, 0, uint.MaxValue);

        if ((purse != gold || mergedExperience != experience)
            && bridge.TryWriteProgression((uint)purse, mergedExperience))
        {
            // Our own write is not the game earning or spending; re-baseline so the next
            // observation measures only what the engine did.
            _lastObservedGold = purse;
        }

        session.PublishProgression((uint)purse, mergedExperience);
    }

    /// <summary>
    /// Turns the local gold READING into ledger entries. The engine has no "gold changed"
    /// event, so a change is inferred from the level — which means our own writes have to be
    /// excluded, or applying the purse would read as the party earning it all over again.
    /// Every write above re-baselines for exactly that reason.
    /// </summary>
    private void ObserveLocalGold(uint gold)
    {
        if (_lastObservedGold < 0)
        {
            // First sight: baseline only. Everything the party already had belongs to the
            // save, not to anyone's contribution — counting it as "earned" would have the
            // host add a whole playthrough's takings to its own purse.
            _lastObservedGold = gold;
            return;
        }

        long delta = gold - _lastObservedGold;
        _lastObservedGold = gold;

        if (delta > 0)
        {
            _goldEarned += (uint)delta;
        }
        else if (delta < 0)
        {
            _goldSpent += (uint)-delta;
        }
    }
}
