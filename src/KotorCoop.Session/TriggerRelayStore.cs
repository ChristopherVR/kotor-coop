using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>One client's request that the host fire a generic trigger's OnEnter script on
/// its behalf.</summary>
/// <param name="TriggerId">The trigger's engine object id — the same number in both
/// instances for the same reason encounter ids are (Handoff 4.5): <c>CSWSArea::LoadGIT</c>
/// walks the module's GIT lists, TriggerList included, in a fixed order from a monotonic
/// counter.</param>
/// <param name="PeerId">Who is standing in it, so the host can find that peer's phantom — the
/// body <c>CSWSTrigger::EventHandler</c>'s own object-id argument is reproduced with.</param>
public readonly record struct TriggerFireRequest(uint TriggerId, uint PeerId);

/// <summary>
/// The host's record of which generic triggers each client is currently standing inside, and
/// which triggers this host has already fired per module.
///
/// Unlike <see cref="EncounterTriggerStore"/>, a generic trigger has NO engine-level one-shot
/// flag to lean on for idempotence — <c>CSWSEncounter::SpawnIfAppropriate</c> refuses a
/// second call for free because <c>started</c> is a real field the engine itself tests and
/// sets, but <c>CSWSTrigger</c> keeps no such bit for its OnEnter script (see
/// docs/research/trigger-relay.md §3): the SCRIPT decides whether re-entering does anything,
/// the same way it would for two different real players walking through in vanilla KOTOR.
/// So this store keeps its OWN per-module "already asked for" set and marks a trigger fired
/// the moment it hands out a request for it — OPTIMISTICALLY, before any confirmation the
/// engine call actually ran, on purpose: the alternative is an unbounded retry loop chasing a
/// transient refusal, and <see cref="EncounterFireResult.NotArmed"/>'s "deliberately silent,
/// no retry" precedent already accepts that trade for the sibling channel.
///
/// A LEVEL in the client-report direction, exactly like <see cref="EncounterTriggerStore"/>:
/// a client resends its current set every tick and stale reports simply age out.
/// </summary>
public sealed class TriggerRelayStore
{
    /// <summary>Same freshness window as <see cref="EncounterTriggerStore"/> — plenty of
    /// slack for a dropped datagram at the default 30 Hz tick rate while still meaning "the
    /// player is there now".</summary>
    public const long DefaultFreshnessMs = 2000;

    /// <summary>Requests handled in one pass. The plugin's request region holds 4 slots
    /// (<see cref="ProtocolConstants.MaxTriggerRelayEntries"/>); this matches.</summary>
    public const int MaxRequests = 4;

    private readonly record struct PeerReport(
        string Module, uint[] TriggerIds, uint[] NativeFiredIds, long ReceivedAtMs);

    private readonly Dictionary<uint, PeerReport> _byPeer = [];

    /// <summary>Per-module set of trigger ids this host has already asked its engine to fire.
    /// Never cleared except by <see cref="ForgetModule"/> — a module the host has left is not
    /// coming back with a fresh GIT, so its trigger ids mean nothing new, but there is no
    /// reason to carry the memory of every module visited in one long session either.
    ///
    /// SHARED between the two directions this store now covers (protocol 36): a trigger id
    /// claimed here by <see cref="Collect"/> (a client's watch report, fired FOR that peer's
    /// phantom by this host) and one claimed by <see cref="ReportNativeFires"/> (any peer's OWN
    /// engine already having fired it natively) are the SAME set, checked before either path
    /// claims an id — the one dedup docs/research/trigger-relay.md's symmetry pass asked for,
    /// applying "both ways" by construction rather than by two separate bookkeeping tables that
    /// could disagree.</summary>
    private readonly Dictionary<string, HashSet<uint>> _firedByModule =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per-module: which peer's OWN engine natively fired a given trigger id, for the
    /// host's own rebroadcast (<see cref="GetNativeFires"/>). A subset of
    /// <see cref="_firedByModule"/>'s keys — every id here is also in the fired-set, but not
    /// every fired-set id has a recorded origin (a plain <see cref="Collect"/>-driven fire has
    /// none; only <see cref="ReportNativeFires"/> populates this).</summary>
    private readonly Dictionary<string, Dictionary<uint, uint>> _nativeOriginByModule =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Lock _gate = new();
    private readonly long _freshnessMs;

    public TriggerRelayStore(long freshnessMs = DefaultFreshnessMs) => _freshnessMs = freshnessMs;

    /// <summary>Records a client's current set. An empty set is stored, not ignored — it is
    /// how "I walked back out" arrives, matching <see cref="EncounterTriggerStore.Report"/>.
    /// </summary>
    public void Report(TriggerRelayPacket packet, long nowMs)
    {
        if (packet.PeerId == 0)
        {
            // 0 is the host's own broadcast sentinel (see the packet's own doc comment); a
            // report claiming to be from peer 0 is either malformed or spoofed, never real.
            return;
        }

        uint[] ids = [.. packet.TriggerIds
            .Where(id => id is not 0 and not 0x7F000000)
            .Distinct()
            .Take(ProtocolConstants.MaxTriggerRelayEntries)];

        uint[] nativeIds = [.. packet.NativeFiredIds
            .Where(id => id is not 0 and not 0x7F000000)
            .Distinct()
            .Take(ProtocolConstants.MaxTriggerNativeFires)];

        lock (_gate)
        {
            _byPeer[packet.PeerId] =
                new PeerReport(packet.Module ?? string.Empty, ids, nativeIds, nowMs);
        }

        if (nativeIds.Length > 0)
        {
            ClaimNativeFires(packet.PeerId, packet.Module ?? string.Empty, nativeIds);
        }
    }

    /// <summary>
    /// The host's own local counterpart to the peer-&gt;host half of <see cref="Report"/> — no
    /// packet, no network round trip, because the host talks to its own store directly. Same
    /// claim path as a reported peer's native fires, keyed on <paramref name="peerId"/>
    /// (normally <c>CoopHost.HostPeerId</c>).
    /// </summary>
    public void ReportNativeFires(uint peerId, string module, IReadOnlyList<uint> triggerIds)
    {
        if (peerId == 0 || triggerIds.Count == 0)
        {
            return;
        }

        uint[] ids = [.. triggerIds
            .Where(id => id is not 0 and not 0x7F000000)
            .Distinct()
            .Take(ProtocolConstants.MaxTriggerNativeFires)];

        if (ids.Length > 0)
        {
            ClaimNativeFires(peerId, module ?? string.Empty, ids);
        }
    }

    /// <summary>
    /// Folds newly-claimed native fires into the SHARED fired-set (so <see cref="Collect"/>
    /// never re-offers one of these as a watch-driven request) and records their origin for
    /// <see cref="GetNativeFires"/>'s rebroadcast. An id already in the fired-set — whichever
    /// path claimed it first, a watch-driven <see cref="Collect"/> or an earlier native report
    /// — is left alone: first claim wins, exactly like <see cref="Collect"/> already does for
    /// two peers standing in the same polygon.
    /// </summary>
    private void ClaimNativeFires(uint originPeerId, string module, IReadOnlyList<uint> triggerIds)
    {
        lock (_gate)
        {
            if (!_firedByModule.TryGetValue(module, out HashSet<uint>? fired))
            {
                fired = [];
                _firedByModule[module] = fired;
            }

            if (!_nativeOriginByModule.TryGetValue(module, out Dictionary<uint, uint>? origins))
            {
                origins = [];
                _nativeOriginByModule[module] = origins;
            }

            foreach (uint id in triggerIds)
            {
                if (!fired.Add(id))
                {
                    continue;
                }

                origins[id] = originPeerId;
            }
        }
    }

    /// <summary>
    /// Every trigger this store currently knows was fired NATIVELY (as opposed to by
    /// <see cref="Collect"/>'s relay path) in <paramref name="module"/>, paired with the peer
    /// whose own engine fired it — a LEVEL, resent by the host's broadcast every tick like
    /// <see cref="GetFiredIds"/>, so a lost datagram costs one tick, never a permanently missed
    /// relay.
    /// </summary>
    public IReadOnlyList<TriggerFireRequest> GetNativeFires(string module)
    {
        lock (_gate)
        {
            if (!_nativeOriginByModule.TryGetValue(module ?? string.Empty, out Dictionary<uint, uint>? origins))
            {
                return [];
            }

            List<TriggerFireRequest> result = new(origins.Count);
            foreach ((uint triggerId, uint originPeerId) in origins)
            {
                result.Add(new TriggerFireRequest(triggerId, originPeerId));
            }

            return result;
        }
    }

    /// <summary>Drops a peer's reports outright — used on disconnect, so nothing it last said
    /// can still fire after it has gone.</summary>
    public void Forget(uint peerId)
    {
        lock (_gate)
        {
            _byPeer.Remove(peerId);
        }
    }

    /// <summary>Drops a module's fired-set — used when nobody is left standing in it, so a
    /// long session does not grow this dictionary without bound.</summary>
    public void ForgetModule(string module)
    {
        lock (_gate)
        {
            _firedByModule.Remove(module ?? string.Empty);
            _nativeOriginByModule.Remove(module ?? string.Empty);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _byPeer.Clear();
            _firedByModule.Clear();
            _nativeOriginByModule.Clear();
        }
    }

    /// <summary>
    /// What the host should ask its plugin to fire right now: fresh reports only, from peers
    /// standing in <paramref name="module"/>, with every trigger id already in that module's
    /// fired-set removed — and every id this call DOES return is added to the fired-set
    /// before returning, so the very next call (even a millisecond later, from the same
    /// still-standing client) will not hand it out again. Deduplicated by TRIGGER, like
    /// <see cref="EncounterTriggerStore.Collect"/>: two players in the same polygon want the
    /// scene to start once.
    /// </summary>
    public IReadOnlyList<TriggerFireRequest> Collect(string module, long nowMs)
    {
        module ??= string.Empty;
        List<TriggerFireRequest> requests = [];
        HashSet<uint> seenThisPass = [];

        lock (_gate)
        {
            if (!_firedByModule.TryGetValue(module, out HashSet<uint>? fired))
            {
                fired = [];
                _firedByModule[module] = fired;
            }

            foreach (uint peerId in _byPeer.Keys.Order())
            {
                PeerReport report = _byPeer[peerId];

                if (nowMs - report.ReceivedAtMs > _freshnessMs
                    || !string.Equals(report.Module, module, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (uint id in report.TriggerIds)
                {
                    if (requests.Count >= MaxRequests)
                    {
                        return requests;
                    }

                    if (fired.Contains(id) || !seenThisPass.Add(id))
                    {
                        continue;
                    }

                    fired.Add(id);
                    requests.Add(new TriggerFireRequest(id, peerId));
                }
            }
        }

        return requests;
    }

    /// <summary>The trigger ids this host has already fired in <paramref name="module"/>, for
    /// the host's own broadcast — see <see cref="TriggerRelayPacket"/>'s host-&gt;all
    /// direction. Snapshot copy; never the live set.</summary>
    public IReadOnlyList<uint> GetFiredIds(string module)
    {
        lock (_gate)
        {
            return _firedByModule.TryGetValue(module ?? string.Empty, out HashSet<uint>? fired)
                ? [.. fired]
                : [];
        }
    }

    /// <summary>Every module this store currently holds a fired-set for, so the host's
    /// broadcast loop knows which modules to announce without needing every peer's module up
    /// front.</summary>
    public IReadOnlyCollection<string> ModulesWithFired
    {
        get
        {
            lock (_gate)
            {
                return [.. _firedByModule.Keys];
            }
        }
    }
}
