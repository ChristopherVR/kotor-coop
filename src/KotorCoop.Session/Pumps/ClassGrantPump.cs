using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// The class-grant relay (docs/research/class-and-power-grants.md, coop.ini
/// <c>class_grant_sync</c>): a script's <c>AddMultiClass(nClass, GetPC())</c> — Master Dorak's
/// Jedi path choice on Dantooine — runs on the engine that owns the conversation only, so the
/// other player's character never became a Jedi (docs/review-seamless-2026-09-18.md blocker
/// #1). The plugin captures the call at the VM handler (<c>AddMultiClassHook</c> → block region
/// 0x4C20), this announces it reliably (<see cref="ClassGrantPacket"/>), and every receiver asks
/// its own plugin to run the SAME handler on its own character (<c>Odyssey.AddMultiClass</c>,
/// <c>ClassGrantController</c>).
///
/// Same three-part shape as <see cref="TransitionPump.PumpGalaxyDeparture"/>: announce our
/// own edge (baselined on the first read, since the block outlives sessions in the same
/// process), take every peer's, and drive one apply at a time through request + ack + retry.
/// Unlike a departure a grant never expires and needs no same-module test — see
/// <see cref="ClassGrantReplayRule"/>.
/// </summary>
internal sealed class ClassGrantPump(
    IGameBridge bridge, ICoopSession session, AgentSettings settings, Action<string> log)
{
    private uint _announcedSerial;

    private bool _baselined;

    /// <summary>(origin, sequence) pairs already queued here — the session layer filters a
    /// client's duplicates, but the host's inbox can still see one retransmit arrive before
    /// its ack lands, and applying a class twice is a wasted round trip at best.</summary>
    private readonly HashSet<(uint Origin, uint Sequence)> _seen = [];

    private readonly Queue<ClassGrantPacket> _pending = new();

    // ---- an apply in flight ----------------------------------------------------------

    private ClassGrantPacket? _current;

    private bool _inFlight;

    private long _requestedAtMs;

    /// <summary>The plugin's "applied" echo BEFORE our request went in; the ack is that value
    /// changing.</summary>
    private uint _appliedBaseline;

    private int _attempts;

    private long _retryAtMs;

    public int PendingCount => _pending.Count + (_current is null ? 0 : 1);

    public void Pump(in PlayerState local, IReadOnlyList<RemotePeer> peers, bool changedModule, long moduleSettledAtMs)
    {
        if (!settings.ClassGrantSync)
        {
            return;
        }

        PublishOwn();
        TakePeerGrants(peers);

        if (_inFlight)
        {
            PollApply(peers);
            return;
        }

        TryApply(local, peers, changedModule, moduleSettledAtMs);
    }

    private void PublishOwn()
    {
        // ⚠ Baseline on the FIRST read whatever it says (the same lesson the galaxy-map relay
        // learnt live, 2026-09-03): the bridge reports "nothing" for a serial of zero, and a
        // fresh block holds zero, so a baseline taken only on a successful read would make the
        // first real grant the baseline and never announce it.
        bool readable = bridge.TryReadClassGrant(out uint serial, out int classId);
        if (!_baselined)
        {
            _baselined = true;
            _announcedSerial = readable ? serial : 0;
            return;
        }

        if (!readable || serial == _announcedSerial)
        {
            return;
        }

        _announcedSerial = serial;
        string name = CreatureClassNames.Display(classId);
        log($"announcing class grant #{serial}: a script made this character a {name}");
        session.PublishClassGrant(classId);
        bridge.PostChatLine($"you are now a {name} — sharing the class with your party");
    }

    private void TakePeerGrants(IReadOnlyList<RemotePeer> peers)
    {
        foreach (ClassGrantPacket packet in session.DrainClassGrants())
        {
            if (!_seen.Add((packet.OriginPeerId, packet.OpSequence)))
            {
                continue;
            }

            _pending.Enqueue(packet);
            log($"{PeerLabel(packet.OriginPeerId, peers)}'s character became a "
                + $"{CreatureClassNames.Display(packet.ClassId)} (grant #{packet.OpSequence}) - queued for this character");
        }
    }

    private void TryApply(in PlayerState local, IReadOnlyList<RemotePeer> peers, bool changedModule, long moduleSettledAtMs)
    {
        if (_current is null)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            _current = _pending.Dequeue();
            _attempts = 0;
            _retryAtMs = 0;
        }

        long now = SessionClock.NowMs;
        if (now < _retryAtMs)
        {
            return;
        }

        // Same "mid our own transition" reading TransitionPump's replay uses: the load itself,
        // and the settle window after an arrival.
        bool midTransition = ModuleNameGuard.IsFallbackModuleName(local.Module)
            || (changedModule && now - moduleSettledAtMs < settings.ModuleSettleMs);

        if (ClassGrantReplayRule.Decide(local.Module, bridge.IsInDialogue, midTransition) == ClassGrantReplayPath.Wait)
        {
            return;
        }

        ClassGrantPacket grant = _current;
        _appliedBaseline = bridge.TryReadClassGrantStatus(out uint applied, out _) ? applied : 0;

        if (!bridge.TryRequestClassGrant(grant.ClassId))
        {
            log($"could not hand {PeerLabel(grant.OriginPeerId, peers)}'s class grant to the plugin (no block?)");
            _current = null;
            return;
        }

        _attempts++;
        _inFlight = true;
        _requestedAtMs = now;
        log($"applying {PeerLabel(grant.OriginPeerId, peers)}'s class grant #{grant.OpSequence} "
            + $"({CreatureClassNames.Display(grant.ClassId)}) to this character (attempt {_attempts}/{ClassGrantReplayRule.MaxAttempts})");
    }

    private void PollApply(IReadOnlyList<RemotePeer> peers)
    {
        if (_current is not { } grant)
        {
            _inFlight = false;
            return;
        }

        long now = SessionClock.NowMs;
        bool acked = bridge.TryReadClassGrantStatus(out uint applied, out uint status)
            && applied != _appliedBaseline;

        if (!acked)
        {
            if (now - _requestedAtMs < ClassGrantReplayRule.AckTimeoutMs)
            {
                return;
            }

            log($"the plugin did not answer the class-grant request within {ClassGrantReplayRule.AckTimeoutMs} ms - treating it as refused");
            status = 0;
        }

        _inFlight = false;
        string leader = PeerLabel(grant.OriginPeerId, peers);
        string name = CreatureClassNames.Display(grant.ClassId);

        switch (ClassGrantReplayRule.Settle(status, _attempts))
        {
            case ClassGrantReplayOutcome.Done:
                _current = null;
                if (status == ClassGrantReplayRule.StatusAlreadyHas)
                {
                    log($"this character already had {name}; {leader}'s grant #{grant.OpSequence} needed nothing");
                }
                else
                {
                    log($"this character is now a {name} (from {leader}'s grant #{grant.OpSequence})");
                    bridge.PostChatLine($"{leader} became a {name} — so did you. Level up to take the class.");
                }

                return;

            case ClassGrantReplayOutcome.GiveUp:
                _current = null;
                log($"giving up on {leader}'s class grant #{grant.OpSequence} ({name}): plugin status {status} after {_attempts} attempt(s)");
                bridge.PostChatLine($"could not add {name} to your character (status {status})");
                return;

            default:
                _retryAtMs = now + ClassGrantReplayRule.RetryMs;
                log($"the engine refused adding {name} - retrying in {ClassGrantReplayRule.RetryMs} ms");
                return;
        }
    }

    private static string PeerLabel(uint peerId, IReadOnlyList<RemotePeer> peers)
    {
        for (int i = 0; i < peers.Count; i++)
        {
            if (peers[i].PeerId == peerId && peers[i].Name is { Length: > 0 } name)
            {
                return name;
            }
        }

        return peerId == CoopHost.HostPeerId ? "the host" : $"peer {peerId}";
    }
}
