using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// The Jedi-choice relay (docs/research/class-and-power-grants.md, coop.ini
/// <c>class_grant_sync</c>): a script's <c>AddMultiClass(nClass, GetPC())</c> — Master Dorak's
/// Jedi path choice on Dantooine, or a peer's own class-choice popup pick — runs on the engine
/// that owns it only, so no OTHER player's character learns the choice happened
/// (docs/review-seamless-2026-09-18.md blocker #1). The plugin captures the call at the VM
/// handler (<c>AddMultiClassHook</c> → block region 0x4C20), this announces it reliably
/// (<see cref="ClassGrantPacket"/>), and every OTHER receiver asks its own plugin to check
/// whether IT needs the SAME popup — never to copy the sender's exact class
/// (docs/research/class-and-power-grants.md's 2026-09-25 section: one player's pick is never
/// forced onto another).
///
/// Same three-part shape as <see cref="TransitionPump.PumpGalaxyDeparture"/>: announce our
/// own edge (baselined on the first read, since the block outlives sessions in the same
/// process), take every peer's, and drive one check-and-maybe-prompt at a time through
/// request + ack + retry. Unlike a departure a Jedi choice never expires and needs no
/// same-module test — see <see cref="ClassGrantReplayRule"/>.
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

        if (ClassGrantReplayRule.Decide(local.Module, bridge.IsInDialogue, midTransition, local.IsInCombat) == ClassGrantReplayPath.Wait)
        {
            return;
        }

        ClassGrantPacket grant = _current;
        _appliedBaseline = bridge.TryReadClassGrantStatus(out uint applied, out _) ? applied : 0;

        // The class id travels with the request only for the plugin's log line and this one's
        // own diagnostics — since 2026-09-25 the receiver never forces it onto the local player
        // (docs/research/class-and-power-grants.md): it checks whether THIS character already
        // has a Jedi class and, if not, shows its own popup instead.
        if (!bridge.TryRequestClassGrant(grant.ClassId))
        {
            log($"could not hand {PeerLabel(grant.OriginPeerId, peers)}'s Jedi choice to the plugin (no block?)");
            _current = null;
            return;
        }

        _attempts++;
        _inFlight = true;
        _requestedAtMs = now;
        log($"{PeerLabel(grant.OriginPeerId, peers)}'s Jedi choice happened (grant #{grant.OpSequence}) - "
            + $"checking whether this character needs to choose too (attempt {_attempts}/{ClassGrantReplayRule.MaxAttempts})");
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
                if (status == ClassGrantReplayRule.StatusPrompted)
                {
                    log($"this character is being asked to choose a Jedi class of their own (from {leader}'s Jedi choice, grant #{grant.OpSequence})");
                }
                else if (status == ClassGrantReplayRule.StatusAlreadyHas)
                {
                    log($"this character already has a Jedi class; {leader}'s grant #{grant.OpSequence} needed nothing");
                }
                else
                {
                    // StatusApplied: only the local addclass dev verb's own engine-call path
                    // produces this any more, never the network relay - kept for completeness.
                    log($"this character is now a {name} (from {leader}'s grant #{grant.OpSequence})");
                }

                return;

            case ClassGrantReplayOutcome.GiveUp:
                _current = null;
                log($"giving up on {leader}'s Jedi-choice grant #{grant.OpSequence}: plugin status {status} after {_attempts} attempt(s)");
                bridge.PostChatLine($"could not check whether you need to choose a Jedi class (status {status})");
                return;

            default:
                _retryAtMs = now + ClassGrantReplayRule.RetryMs;
                log($"the plugin did not answer the Jedi-choice check - retrying in {ClassGrantReplayRule.RetryMs} ms");
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
