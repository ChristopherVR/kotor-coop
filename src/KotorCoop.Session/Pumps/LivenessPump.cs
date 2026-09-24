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
    /// verdict forever; see <see cref="LivenessGoneRetentionMs"/>.</summary>
    private readonly Dictionary<uint, long> _livenessGoneSince = [];

    private long _lastLivenessScanMs;

    /// <summary>How often the full-area liveness scan actually runs — independent of, and
    /// much less frequent than, the 150ms combat cadence this method is called on, because
    /// <see cref="IGameBridge.ReadCreatureLiveness"/> walks every creature in the module, not a
    /// shared-block read.</summary>
    private const int LivenessScanIntervalMs = 2000;

    /// <summary>How long a "gone" verdict is still worth reporting after the cycle that first
    /// detected it. Generous headroom past a few missed 2-second broadcast cycles without
    /// holding every destroyed id in the module for the rest of the session.</summary>
    private const long LivenessGoneRetentionMs = 15_000;

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
        if (!bridge.SupportsEnemySync || (caps & SessionCapabilities.Enemies) == 0)
        {
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
        if (received.Count > 0)
        {
            (List<CreatureLivenessEntry> page, int nextCursor) =
                LivenessBlockPage.Select(received, SharedBlockLivenessCapacity, _livenessBlockCursor);
            _livenessBlockCursor = nextCursor;
            bridge.TryWriteCreatureLiveness(page);
        }

        session.ReportLivenessAnomalyCount(bridge.ReadLivenessAnomalyCount());
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

        // A "gone" verdict past its retention window has almost certainly been delivered by
        // now; holding it forever would grow these tables without bound over a long session
        // in a module that sees a lot of destruction.
        if (_livenessGoneSince.Count > 0)
        {
            List<uint>? expired = null;
            foreach ((uint id, long since) in _livenessGoneSince)
            {
                if (nowMs - since > LivenessGoneRetentionMs)
                {
                    (expired ??= []).Add(id);
                }
            }

            if (expired is not null)
            {
                foreach (uint id in expired)
                {
                    _livenessGoneSince.Remove(id);
                    _livenessKnown.Remove(id);
                }
            }
        }

        List<CreatureLivenessEntry> full = new(scan.Count + _livenessGoneSince.Count);
        full.AddRange(scan);
        foreach (uint id in _livenessGoneSince.Keys)
        {
            full.Add(new CreatureLivenessEntry(id, CreatureLivenessState.Gone));
        }

        session.PublishCreatureLiveness(censusModule, full);
    }
}
