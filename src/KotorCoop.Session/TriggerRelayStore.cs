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

    private readonly record struct PeerReport(string Module, uint[] TriggerIds, long ReceivedAtMs);

    private readonly Dictionary<uint, PeerReport> _byPeer = [];

    /// <summary>Per-module set of trigger ids this host has already asked its engine to fire.
    /// Never cleared except by <see cref="ForgetModule"/> — a module the host has left is not
    /// coming back with a fresh GIT, so its trigger ids mean nothing new, but there is no
    /// reason to carry the memory of every module visited in one long session either.</summary>
    private readonly Dictionary<string, HashSet<uint>> _firedByModule =
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

        lock (_gate)
        {
            _byPeer[packet.PeerId] = new PeerReport(packet.Module ?? string.Empty, ids, nowMs);
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
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _byPeer.Clear();
            _firedByModule.Clear();
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
