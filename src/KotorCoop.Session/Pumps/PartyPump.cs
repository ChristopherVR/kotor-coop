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
    public void PumpParty(SessionCapabilities caps, string localModule)
    {
        // session.Overrides.PartySync: a client that negotiated a different party_sync than
        // the host adopts the host's value at runtime — docs/research/mod-compatibility.md's
        // "Behaviour-flag negotiation" — rather than keep reconciling (or not) against its own
        // local coop.ini/CLI setting.
        bool syncParty = session.Overrides.PartySync ?? settings.SyncParty;
        if (!syncParty || !bridge.SupportsPartySync || (caps & SessionCapabilities.Party) == 0)
        {
            return;
        }

        // A stunt module's party table is a cutscene reshuffle, not the real roster — see
        // StuntModuleRule. Neither publish it as the host (a client would tear its real
        // companions down to match) nor mirror the host's real companions ONTO it as a client
        // (that would be writing them into whichever cutscene actors the stunt module actually
        // holds). Simply hold: the session's last real PublishParty stays exactly as it was,
        // so a peer still following normally keeps the last genuine positions until this
        // instance is back in a real module.
        if (StuntModuleRule.IsStuntModule(localModule))
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
            // own AI exactly as in single-player. companion_gear_sync (roadmap item 12,
            // docs/research/companion-state-sync.md) additionally reads each companion's real
            // equipped gear onto the same snapshot — off by default, so ordinary sessions pay
            // nothing extra here.
            session.PublishParty(bridge.ReadPartyMembers(settings.SyncCompanionGear));
            return;
        }

        // A client: freeze the local party AI and mirror the host's companions. The freeze
        // is re-asserted inside TryWritePartyMembers every pass. (The input control-lock that
        // stops the client controlling those companions runs every tick in the fast path.)
        // companion_gear_sync hands the host's published gear on to the plugin too; anti-echo
        // is structural here — a client never calls ReadPartyMembers to publish its own.
        bridge.TryWritePartyMembers(session.GetPartyMembers(), settings.SyncCompanionGear);
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
    public void PumpPartyRoster(SessionCapabilities caps, string localModule)
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
        if (!(session.Overrides.PartySync ?? settings.SyncParty) || (caps & SessionCapabilities.Party) == 0)
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

        // Same "hold, don't clear" treatment as the transient case just above, for the same
        // reason: a stunt module's roster is a cutscene reshuffle (see StuntModuleRule), not a
        // real recruit/dismiss, and publishing it (or reconciling a client's plugin against it)
        // would tell every follower the party just changed when nothing really did.
        if (StuntModuleRule.IsStuntModule(localModule))
        {
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

    /// <summary>Per-peer XP baselines (host side): a peer's first report after it joins never
    /// raises the host; only what it earns afterwards is folded in. See
    /// <see cref="ExperienceMerge"/>.</summary>
    private readonly ExperienceMerge _experience = new();

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
    /// stealth award only one instance ran — and leaves the rest alone. Since 2026-09-18 the
    /// max is taken against a per-peer BASELINE (<see cref="ExperienceMerge"/>): a peer's first
    /// report after joining never raises the host, only what it earns after that does.
    /// </summary>
    public void PumpProgression(SessionCapabilities caps, string localModule)
    {
        if (!settings.SyncProgression
            || !bridge.SupportsProgressionSync
            || (caps & SessionCapabilities.Progression) == 0)
        {
            return;
        }

        // A stunt module's party table reads gold as 0 (see StuntModuleRule) — not the party
        // going broke, a cutscene-only world with no real purse. Skip both directions
        // symmetrically: as the host, publishing that 0 would clamp every peer's purse to
        // nothing; as a client, REPORTING it would tell the host this instance just spent its
        // entire balance, and ADOPTING the host's real number back onto this fake world would
        // be a write with no meaning here anyway. ObserveLocalGold is skipped along with
        // everything else, so the eventual real reading (once this instance is back in an
        // ordinary module) is compared against the last genuine baseline, not this 0.
        if (StuntModuleRule.IsStuntModule(localModule))
        {
            return;
        }

        if (!bridge.TryReadProgression(out uint rawGold, out uint experience))
        {
            return;
        }

        // A wrapped overdraft (store-sync.md 2026-09-03) must not reach the ledger as
        // earnings; the party is broke, not rich, and the host writes that back below.
        uint gold = GoldReadingRule.Sanitise(rawGold);
        if (gold != rawGold && _lastObservedGold >= 0)
        {
            log($"purse: party gold read {rawGold} - a wrapped overdraft, treating it as 0");
        }

        ObserveLocalGold(gold);

        if (session.IsEnemyAuthority)
        {
            PumpProgressionHost(gold, rawGold, experience);
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
                // Re-baseline to what the engine will actually hold, not what was asked for:
                // the write clamps at the engine ceiling, and a baseline above it would read
                // back as a phantom spend the size of the difference.
                _lastObservedGold = Math.Min(hostGold, GoldReadingRule.Max);
            }
        }
    }

    /// <summary>
    /// The host: spend every client's outstanding contribution against the party table, then
    /// publish the result.
    /// </summary>
    private void PumpProgressionHost(uint gold, uint rawGold, uint experience)
    {
        long purse = gold;
        uint mergedExperience = experience;

        foreach (PeerProgression report in session.GetProgressionReports())
        {
            // Max-merge against a per-peer BASELINE, not the raw report: a veteran character
            // joining an earlier save must not lift the host to its own total on the first
            // tick (docs/review-seamless-2026-09-18.md §2). ExperienceMerge keeps the
            // "never below the host's own reading" property of the bare max.
            mergedExperience = _experience.Fold(
                mergedExperience, report.PeerId, report.Generation, report.Experience);

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
        // would wrap to something enormous. And it cannot hold more than the engine's own
        // ceiling: SetGold clamps there, so a purse above it would read back as a spend.
        purse = Math.Clamp(purse, 0, GoldReadingRule.Max);

        // Compared against the RAW reading: a sanitised wrap is a purse the table does not
        // yet hold, and must be written even when nobody contributed this tick.
        if ((purse != rawGold || mergedExperience != experience)
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

    /// <summary>
    /// Shares each active companion's experience, keyed by NPC roster slot — the companion
    /// sibling of <see cref="PumpProgression"/>'s PC-experience half. coop.ini
    /// <c>companion_xp_sync</c>, ON by default (live-verified 2026-08-27 — see
    /// docs/research/companion-state-sync.md).
    ///
    /// No ledger, unlike gold: experience only ever goes up and both engines already grant
    /// kill XP to every ACTIVE party member independently
    /// (<c>CSWPartyTable::DistributeExperience</c>, docs/research/progression-sync.md §2), so a
    /// plain per-slot max-merge is enough — the same reasoning <see cref="PumpProgression"/>'s
    /// own doc comment gives for the PC's XP.
    ///
    /// Gated on BOTH <see cref="SessionCapabilities.Progression"/> (a content-independent
    /// scalar channel) and <see cref="SessionCapabilities.Party"/> (the slot keying needs the
    /// same save both instances loaded) — <see cref="PumpProgression"/> only needs the former
    /// because the PC's own single scalar carries no roster key at all.
    /// </summary>
    public void PumpCompanionExperience(SessionCapabilities caps)
    {
        if (!settings.SyncCompanionXp
            || !bridge.SupportsCompanionProgressionSync
            || (caps & SessionCapabilities.Progression) == 0
            || (caps & SessionCapabilities.Party) == 0)
        {
            return;
        }

        uint[] localXp = new uint[ProtocolConstants.PartyRosterSlots];
        if (!bridge.TryReadCompanionExperience(localXp, out ushort localMask))
        {
            return;
        }

        if (session.IsEnemyAuthority)
        {
            PumpCompanionExperienceHost(localMask, localXp);
            return;
        }

        // A client: report our own reading, then mirror whatever the host makes of it.
        session.ReportCompanionExperience(localMask, localXp);

        if (!session.TryGetCompanionExperience(out ushort hostMask, out uint[] hostXp))
        {
            return;
        }

        // Never below our own reading: a max on both ends means neither instance can pull the
        // other backwards while a report is in flight — same guarantee PumpProgression already
        // gives the PC's own XP.
        uint[] merged = new uint[ProtocolConstants.PartyRosterSlots];
        ushort mergedMask = 0;
        CompanionExperienceMerge.MergeInto(ref mergedMask, merged, localMask, localXp);
        CompanionExperienceMerge.MergeInto(ref mergedMask, merged, hostMask, hostXp);

        if (mergedMask != 0)
        {
            bridge.TryWriteCompanionExperience(merged, mergedMask);
        }
    }

    /// <summary>
    /// The host: merge every fresh client report upward by max against its own reading, apply
    /// the result to its own party table (a client-only script grant otherwise never reaches
    /// the host's own companion), then publish the merged truth.
    /// </summary>
    private void PumpCompanionExperienceHost(ushort localMask, uint[] localXp)
    {
        uint[] merged = new uint[ProtocolConstants.PartyRosterSlots];
        ushort mergedMask = 0;
        CompanionExperienceMerge.MergeInto(ref mergedMask, merged, localMask, localXp);

        foreach (PeerCompanionXp report in session.GetCompanionExperienceReports())
        {
            CompanionExperienceMerge.MergeInto(ref mergedMask, merged, report.PresentMask, report.Xp);
        }

        if (mergedMask != 0)
        {
            bridge.TryWriteCompanionExperience(merged, mergedMask);
        }

        session.PublishCompanionExperience(mergedMask, merged);
    }
}
