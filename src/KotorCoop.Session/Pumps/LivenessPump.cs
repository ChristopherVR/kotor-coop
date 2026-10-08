using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

// Creature liveness reconciliation: "we don't want instances of NPCs alive in one host and
// not another." Creature death is host-authoritative for the 32-slot combat census
// (CombatPump mirrors owner health and kills), but several paths leave a body alive on one
// side and dead/gone on the other: beyond the census cap, killed by SCRIPT (DestroyObject, a
// cutscene death, SetPlotFlag then a kill), killed while the peers were in different modules
// or disconnected, or killed by the client's own engine locally under local_combat before the
// host's engine confirmed. See docs/research/creature-liveness.md.
internal sealed class LivenessPump(IGameBridge bridge, ICoopSession session, Action<string> log)
{
    /// <summary>Owner-authority side only: last known state per id in
    /// <see cref="_livenessCensusModule"/>, from the full (never capped, never distance-shed)
    /// area scan — unlike the 32-slot combat census, an id that drops out of THIS scan really
    /// is gone, not merely far from a player.</summary>
    private readonly Dictionary<uint, CreatureLivenessState> _livenessKnown = [];

    private string _livenessCensusModule = string.Empty;

    /// <summary>Owner-authority side only: ids reported Gone, and when this pass first
    /// noticed. Retained past detection so a lost broadcast cycle does not cost a peer the
    /// verdict forever; see <see cref="MaxRetainedGone"/>.</summary>
    private readonly Dictionary<uint, long> _livenessGoneSince = [];

    private long _lastLivenessScanMs;

    /// <summary>How often the full-area liveness scan actually runs — independent of, and
    /// much less frequent than, the 150ms combat cadence this method is called on, because
    /// <see cref="IGameBridge.ReadCreatureLiveness"/> walks every creature in the module, not a
    /// shared-block read.</summary>
    private const int LivenessScanIntervalMs = 2000;

    /// <summary>How many "gone" verdicts the owner keeps re-reporting for the current module.
    /// They used to expire after 15 s, but a mirror does not reconcile while it is in a
    /// conversation, and cutscenes are conversations whose scripts destroy their actors at the
    /// end: on 2026-10-08 the joiner sat in one past the window and kept nine
    /// <c>end_cut2_*</c> actors the host had destroyed, for good. Verdicts now last until the
    /// module changes (ids are reassigned then, and the tables are cleared), and only the
    /// oldest beyond this many are dropped, which bounds memory in a module with a lot of
    /// destruction.</summary>
    private const int MaxRetainedGone = 64;

    /// <summary>Mirrors <c>SharedBlock.LivenessCapacity</c> in the plugin — kept as a plain
    /// literal here rather than a cross-project reference, the same "shared by value, not by
    /// reference" rule the plugin's own block-layout constants already follow. NOT part of the
    /// version 80 "no cap really" pass (that grew the 32-slot combat census/presence/
    /// damage-ledger/materialised-owner-id tables to 128; liveness already had its own
    /// generous 96-slot capacity plus wire paging and block-side rotation, and stays at 96) —
    /// see docs/research/creature-liveness.md's cap-removal section.</summary>
    private const int SharedBlockLivenessCapacity = 96;

    /// <summary>Rotates the "alive" tail <see cref="LivenessBlockPage"/> hands the plugin
    /// across pumps when the accumulated set does not fit the block's capacity in one pass.</summary>
    private int _livenessBlockCursor;

    /// <summary>Mirror side: hostiles this world has and the host never did; see
    /// <see cref="LocalOnlyHostileTracker"/>. Scanned at the owner's own cadence.</summary>
    private readonly LocalOnlyHostileTracker _localOnly = new();
    private List<uint> _localOnlyIds = [];
    private long _lastLocalHostileScanMs;
    private readonly HashSet<uint> _localOnlyLogged = [];
    private bool _hasWorldLoadSerial;
    private uint _worldLoadSerial;
    private uint _nativeLivenessEpoch;
    private bool _mirrorLocalLoadQuarantined;
    private string _observedLocalModule = string.Empty;

    /// <summary>
    /// Moves creature-liveness state in whichever direction this peer is responsible for —
    /// the same owner/mirror split <see cref="CombatPump.PumpEnemies"/> uses, but deliberately
    /// NOT gated on <c>IsInDialogue</c>/<c>IsInMinigame</c> on the owner side: those are
    /// exactly when a SCRIPT is most likely to kill or destroy something, and catching that is
    /// this channel's whole reason to exist. The 32-slot combat census pauses there because a
    /// paused census would drive a pacified fight onto a peer; this channel drives nothing at
    /// all, it only reports and (on the mirror) corrects a body that has fallen out of step.
    /// </summary>
    public void PumpCreatureLiveness(in PlayerState local, SessionCapabilities caps)
    {
        if (bridge.IsWorldBetweenLives || !bridge.TryReadWorldLoadSerial(out uint worldSerial)
            || !bridge.TryReadLivenessEpoch(out uint nativeEpoch) || nativeEpoch == 0)
        {
            bridge.TryWriteCreatureLiveness([]);
            return;
        }
        if (!bridge.TryReadCurrentModule(out string currentModule) || ModuleNameGuard.IsFallbackModuleName(currentModule))
        {
            bridge.TryWriteCreatureLiveness([]);
            return;
        }
        // Do not adopt an old epoch with a new module label during ordinary travel.
        // That torn baseline would misclassify the following stable tick as a same-area load.
        if (!bridge.TryReadWorldLoadSerial(out uint stableSerial) || stableSerial != worldSerial
            || !bridge.TryReadLivenessEpoch(out uint stableEpoch) || stableEpoch != nativeEpoch
            || !bridge.TryReadCurrentModule(out string stableModule)
            || !string.Equals(currentModule, stableModule, StringComparison.OrdinalIgnoreCase))
        {
            bridge.TryWriteCreatureLiveness([]);
            return;
        }
        if (_hasWorldLoadSerial && (_worldLoadSerial != worldSerial || _nativeLivenessEpoch != nativeEpoch))
        {
            _livenessKnown.Clear();
            _livenessGoneSince.Clear();
            _lastLivenessScanMs = 0;
            _livenessBlockCursor = 0;
            ClearLocalOnly();
            bridge.TryWriteCreatureLiveness([]);
            if (session.IsEnemyAuthority) session.PublishCreatureLiveness(local.Module, [], worldSerial, nativeEpoch);
            else
            {
                InvalidateMirror(_worldLoadSerial != worldSerial
                    || string.Equals(_observedLocalModule, currentModule, StringComparison.OrdinalIgnoreCase));
            }
        }
        _worldLoadSerial = worldSerial;
        _nativeLivenessEpoch = nativeEpoch;
        _observedLocalModule = currentModule;
        _hasWorldLoadSerial = true;
        if (!bridge.SupportsEnemySync || (caps & SessionCapabilities.Enemies) == 0 || _mirrorLocalLoadQuarantined)
        {
            bridge.TryWriteCreatureLiveness([]);
            return;
        }

        // Same same-breath module discipline PumpEnemies uses: the label and the data it
        // describes must come from the same read, or a stale label can name entries that
        // belong to a different world entirely.
        string censusModule = bridge.TryReadCurrentModule(out string liveModule) ? liveModule : local.Module;
        bool moduleClean = censusModule.Length > 0
            && !ModuleNameGuard.IsFallbackModuleName(censusModule)
            && string.Equals(censusModule, local.Module, StringComparison.OrdinalIgnoreCase);

        if (!moduleClean)
        {
            bridge.TryWriteCreatureLiveness([]);
            return;
        }

        if (session.IsEnemyAuthority)
        {
            long nowMs = Environment.TickCount64;
            if (nowMs - _lastLivenessScanMs < LivenessScanIntervalMs)
            {
                return;
            }

            _lastLivenessScanMs = nowMs;
            PublishCreatureLiveness(censusModule, nowMs);
            return;
        }

        // Mirror side: hand whatever the owner published for THIS module to the plugin's
        // reconcile controller. Ordered so a truncation at the block's capacity never drops
        // the entries that actually need acting on (dead/gone) ahead of the ones that merely
        // confirm agreement (alive) — see LivenessBlockPage's own doc.
        IReadOnlyList<CreatureLivenessEntry> received = session.GetCreatureLiveness(censusModule);
        bool beforeSerialReadable = bridge.TryReadWorldLoadSerial(out uint beforeWriteSerial);
        bool beforeEpochReadable = bridge.TryReadLivenessEpoch(out uint beforeWriteEpoch);
        bool beforeModuleReadable = bridge.TryReadCurrentModule(out string beforeWriteModule);
        if (!beforeEpochReadable || beforeWriteEpoch != nativeEpoch || !beforeSerialReadable || beforeWriteSerial != worldSerial
            || !beforeModuleReadable || !string.Equals(beforeWriteModule, censusModule, StringComparison.OrdinalIgnoreCase))
        {
            bridge.TryWriteCreatureLiveness([]);
            InvalidateMirror((beforeSerialReadable && beforeWriteSerial != worldSerial)
                || (beforeEpochReadable && beforeWriteEpoch != nativeEpoch && beforeModuleReadable
                    && string.Equals(beforeWriteModule, censusModule, StringComparison.OrdinalIgnoreCase)));
            return;
        }
        if (received.Count > 0)
        {
            received = WithLocalOnlyHostiles(received);
            (List<CreatureLivenessEntry> page, int nextCursor) =
                LivenessBlockPage.Select(received, SharedBlockLivenessCapacity, _livenessBlockCursor);
            _livenessBlockCursor = nextCursor;
            bridge.TryWriteCreatureLiveness(page, censusModule, worldSerial, nativeEpoch);
            bool afterSerialReadable = bridge.TryReadWorldLoadSerial(out uint afterWriteSerial);
            bool afterModuleReadable = bridge.TryReadCurrentModule(out string afterWriteModule);
            bool afterEpochReadable = bridge.TryReadLivenessEpoch(out uint afterWriteEpoch);
            if (!afterSerialReadable || afterWriteSerial != worldSerial || !afterModuleReadable
                || !string.Equals(afterWriteModule, censusModule, StringComparison.OrdinalIgnoreCase)
                || !afterEpochReadable || afterWriteEpoch != nativeEpoch)
            {
                bridge.TryWriteCreatureLiveness([]);
                InvalidateMirror((afterSerialReadable && afterWriteSerial != worldSerial)
                    || (afterEpochReadable && afterWriteEpoch != nativeEpoch && afterModuleReadable
                        && string.Equals(afterWriteModule, censusModule, StringComparison.OrdinalIgnoreCase)));
            }
        }
        else
        {
            bridge.TryWriteCreatureLiveness([]);
            _localOnly.Clear();
            _localOnlyIds = [];
        }

        session.ReportLivenessAnomalyCount(bridge.ReadLivenessAnomalyCount());
    }

    private void ClearLocalOnly()
    {
        _localOnly.Clear();
        _localOnlyIds = [];
        _lastLocalHostileScanMs = 0;
        _localOnlyLogged.Clear();
    }

    /// <summary>The host's view plus a Gone verdict for every hostile only this world has, so the
    /// plugin's ordinary destroy path removes it (its guards already skip stand-ins, the partner's
    /// body and the player).</summary>
    private IReadOnlyList<CreatureLivenessEntry> WithLocalOnlyHostiles(IReadOnlyList<CreatureLivenessEntry> received)
    {
        long nowMs = Environment.TickCount64;
        if (nowMs - _lastLocalHostileScanMs >= LivenessScanIntervalMs)
        {
            _lastLocalHostileScanMs = nowMs;
            HashSet<uint> hostIds = new(received.Count);
            foreach (CreatureLivenessEntry entry in received)
            {
                hostIds.Add(entry.ObjectId);
            }

            _localOnlyIds = _localOnly.Update(bridge.ReadLocalHostileCreatureIds(), hostIds, nowMs);
            List<uint> fresh = _localOnlyIds.Where(_localOnlyLogged.Add).ToList();
            if (fresh.Count > 0)
            {
                log($"liveness: {fresh.Count} hostile(s) exist only here, the host never had them "
                    + $"(a local script spawned them): {string.Join(", ", fresh.Select(id => $"0x{id:X}"))} - removing");
            }
        }

        if (_localOnlyIds.Count == 0)
        {
            return received;
        }

        List<CreatureLivenessEntry> merged = new(received.Count + _localOnlyIds.Count);
        merged.AddRange(_localOnlyIds.Select(id => new CreatureLivenessEntry(id, CreatureLivenessState.Gone)));
        merged.AddRange(received);
        return merged;
    }

    private void InvalidateMirror(bool localLoad)
    {
        ClearLocalOnly();
        if (localLoad)
        {
            _mirrorLocalLoadQuarantined = true;
            session.InvalidateCreatureLivenessForLocalLoad();
        }
        else session.InvalidateCreatureLivenessForWorldTransition();
    }

    /// <summary>
    /// Owner-authority side: scans the full area, derives Gone for anything that dropped out
    /// of the scan without ever going through it, and publishes the whole picture.
    /// </summary>
    private void PublishCreatureLiveness(string censusModule, long nowMs)
    {
        if (!string.Equals(censusModule, _livenessCensusModule, StringComparison.OrdinalIgnoreCase))
        {
            // Ids are reassigned per module load: anything remembered from the last one
            // names a different creature now, or nothing.
            _livenessCensusModule = censusModule;
            _livenessKnown.Clear();
            _livenessGoneSince.Clear();
        }

        List<CreatureLivenessEntry> scan = bridge.ReadCreatureLiveness();
        if (!bridge.TryReadWorldLoadSerial(out uint afterSerial) || afterSerial != _worldLoadSerial
            || !bridge.TryReadLivenessEpoch(out uint afterEpoch) || afterEpoch != _nativeLivenessEpoch
            || !bridge.TryReadCurrentModule(out string afterModule)
            || !string.Equals(afterModule, censusModule, StringComparison.OrdinalIgnoreCase))
        {
            session.PublishCreatureLiveness(censusModule, [], _worldLoadSerial, _nativeLivenessEpoch);
            return;
        }

        HashSet<uint> seenThisPass = new(scan.Count);
        foreach (CreatureLivenessEntry entry in scan)
        {
            seenThisPass.Add(entry.ObjectId);
            _livenessKnown[entry.ObjectId] = entry.State;
            _livenessGoneSince.Remove(entry.ObjectId);
        }

        // Anything remembered but not seen this pass is destroyed. Unlike the 32-slot combat
        // census, this scan is never capped or distance-shed, so an id that disappears from
        // it really is gone rather than merely far from a player.
        List<uint>? newlyGone = null;
        foreach (uint id in _livenessKnown.Keys)
        {
            if (seenThisPass.Contains(id) || _livenessGoneSince.ContainsKey(id))
            {
                continue;
            }

            (newlyGone ??= []).Add(id);
        }

        if (newlyGone is not null)
        {
            foreach (uint id in newlyGone)
            {
                _livenessGoneSince[id] = nowMs;
            }

            log($"liveness: {newlyGone.Count} creature(s) newly gone this pass: "
                + string.Join(", ", newlyGone.Select(id => $"0x{id:X}")));
        }

        // Keep every "gone" verdict for the rest of the module (see MaxRetainedGone): a mirror
        // that was in a long conversation when the destroy happened still needs it afterwards.
        // Past the cap, the oldest go first.
        if (_livenessGoneSince.Count > MaxRetainedGone)
        {
            foreach ((uint id, _) in _livenessGoneSince.OrderBy(pair => pair.Value)
                         .Take(_livenessGoneSince.Count - MaxRetainedGone).ToList())
            {
                _livenessGoneSince.Remove(id);
                _livenessKnown.Remove(id);
            }
        }

        List<CreatureLivenessEntry> full = new(scan.Count + _livenessGoneSince.Count);
        full.AddRange(scan);
        foreach (uint id in _livenessGoneSince.Keys)
        {
            full.Add(new CreatureLivenessEntry(id, CreatureLivenessState.Gone));
        }

        session.PublishCreatureLiveness(censusModule, full, _worldLoadSerial, _nativeLivenessEpoch);
    }
}
