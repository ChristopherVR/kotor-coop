using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

// The party-select relay: a client's own suppressed CSWGuiPartySelection screen, asked of the
// host instead of just silently converging (docs/research/galaxy-map-and-party-select.md's
// 2026-09-25 relay section). Two independent halves in one file, the same shape as
// TransitionPump.GalaxyDeparture.cs: CLIENT captures+sends+shows a notice; HOST receives,
// applies eligibility, opens through the plugin, and answers.
internal sealed partial class TransitionPump
{
    // ---- client half: this instance's own captured panel ---------------------------------

    private uint _partySelectBaselineSerial;
    private bool _partySelectBaselined;

    /// <summary>The request currently awaiting an answer, or 0. Only one at a time — a second
    /// capture before this one resolves replaces it (the player has moved on to a fresher
    /// screen; an answer for the old serial is simply ignored once it arrives).</summary>
    private uint _partySelectActiveRequestSerial;

    private int _partySelectAttempts;
    private long _partySelectNextSendAtMs;

    private const int PartySelectResendMs = 3_000;
    private const int PartySelectMaxAttempts = 3;

    public void PumpPartySelect(in PlayerState local, IReadOnlyList<RemotePeer> peers)
    {
        PumpPartySelectClient(local, peers);
        PumpPartySelectHost(local, peers);
    }

    private void PumpPartySelectClient(in PlayerState local, IReadOnlyList<RemotePeer> peers)
    {
        bool readable = bridge.TryReadPartySelectClientCapture(
            out uint serial, out string exitScript, out int forceNpc1, out int forceNpc2);

        // Same baselining rule PublishOwnGalaxyDeparture uses: the block outlives sessions in
        // this process, so a leftover serial from before this session must not be treated as a
        // fresh capture the moment the agent attaches.
        if (!_partySelectBaselined)
        {
            _partySelectBaselined = true;
            _partySelectBaselineSerial = readable ? serial : 0;
        }
        else if (readable && serial != _partySelectBaselineSerial)
        {
            _partySelectBaselineSerial = serial;
            _partySelectActiveRequestSerial = serial;
            _partySelectAttempts = 0;
            _partySelectNextSendAtMs = 0;

            string host = PeerLabel(CoopHost.HostPeerId, peers);
            log($"party-select: asking {host} to choose the party (exit_script='{exitScript}', "
                + $"forceNpc1={forceNpc1}, forceNpc2={forceNpc2})");
            bridge.PostChatLine($"asked {host} to choose the party");
        }

        if (_partySelectActiveRequestSerial == 0)
        {
            return;
        }

        long now = SessionClock.NowMs;
        if (now >= _partySelectNextSendAtMs)
        {
            if (_partySelectAttempts >= PartySelectMaxAttempts)
            {
                log($"party-select: request #{_partySelectActiveRequestSerial} got no answer - giving up");
                bridge.PostChatLine("the host did not answer - ask again");
                _partySelectActiveRequestSerial = 0;
            }
            else
            {
                session.RequestPartySelect(
                    local.Module, exitScript, forceNpc1, forceNpc2, _partySelectActiveRequestSerial);
                _partySelectAttempts++;
                _partySelectNextSendAtMs = now + PartySelectResendMs;
            }
        }

        foreach (PartySelectResponsePacket response in session.DrainPartySelectResponses())
        {
            if (response.RequestSerial != _partySelectActiveRequestSerial)
            {
                // An answer for a request this player has already abandoned (timed out, or
                // superseded by a fresher capture) — nothing to react to.
                continue;
            }

            switch (response.Status)
            {
                case PartySelectResponseStatus.Opened:
                    log("party-select: the host's own screen is open");
                    break;

                case PartySelectResponseStatus.Confirmed:
                    log("party-select: the host confirmed a party");
                    _partySelectActiveRequestSerial = 0;
                    break;

                case PartySelectResponseStatus.Cancelled:
                    bridge.PostChatLine("the host chose not to change the party");
                    _partySelectActiveRequestSerial = 0;
                    break;

                case PartySelectResponseStatus.Refused:
                    bridge.PostChatLine($"the host could not open the party screen ({response.Detail})");
                    _partySelectActiveRequestSerial = 0;
                    break;

                case PartySelectResponseStatus.AlreadyPending:
                    bridge.PostChatLine("the host is already choosing a party - try again shortly");
                    _partySelectActiveRequestSerial = 0;
                    break;
            }
        }
    }

    private static string PeerLabel(uint peerId, IReadOnlyList<RemotePeer> peers)
    {
        foreach (RemotePeer peer in peers)
        {
            if (peer.PeerId == peerId && peer.Name is { Length: > 0 } name)
            {
                return name;
            }
        }

        return peerId == CoopHost.HostPeerId ? "the host" : $"peer {peerId}";
    }

    // ---- host half: open a peer's requested screen here -----------------------------------

    private readonly record struct PartySelectHostState(uint PeerId, uint RequestSerial, bool AwaitingOutcome);

    private PartySelectHostState? _partySelectHostActive;
    private uint _partySelectHostAppliedBaseline;
    private uint _partySelectHostOutcomeBaseline;

    private void PumpPartySelectHost(in PlayerState local, IReadOnlyList<RemotePeer> peers)
    {
        bool weAreHost = session.IsEnemyAuthority;

        foreach (PartySelectRequestPacket request in session.DrainPartySelectRequests())
        {
            bool sameModule = string.Equals(request.Module, local.Module, StringComparison.OrdinalIgnoreCase);
            bool alreadyPending = _partySelectHostActive is not null;

            if (!PartySelectHostRequestRule.ShouldAccept(weAreHost, sameModule, alreadyPending))
            {
                if (weAreHost && sameModule && alreadyPending)
                {
                    log($"party-select: refusing peer {request.PeerId}'s request #{request.RequestSerial} - "
                        + "already handling another one");
                    session.SendPartySelectResponse(
                        request.PeerId,
                        new PartySelectResponsePacket(
                            request.RequestSerial, 1, PartySelectResponseStatus.AlreadyPending, "busy with another request"));
                }
                else
                {
                    log($"party-select: ignoring peer {request.PeerId}'s request #{request.RequestSerial} "
                        + $"(sameModule={sameModule}, weAreHost={weAreHost})");
                }

                continue;
            }

            _partySelectHostActive = new PartySelectHostState(request.PeerId, request.RequestSerial, false);
            _partySelectHostAppliedBaseline = bridge.TryReadPartySelectHostStatus(out uint applied, out _) ? applied : 0;

            if (!bridge.TryRequestPartySelectHostOpen(request.ExitScript, request.ForceNpc1, request.ForceNpc2))
            {
                log($"party-select: could not hand request #{request.RequestSerial} to the plugin (no block?)");
                session.SendPartySelectResponse(
                    request.PeerId,
                    new PartySelectResponsePacket(
                        request.RequestSerial, 1, PartySelectResponseStatus.Refused, "no plugin"));
                _partySelectHostActive = null;
                continue;
            }

            log($"party-select: opening peer {request.PeerId}'s request #{request.RequestSerial} "
                + $"(exit_script='{request.ExitScript}')");
        }

        if (_partySelectHostActive is not { } active)
        {
            return;
        }

        if (!active.AwaitingOutcome)
        {
            PollHostOpen(active, peers);
        }
        else
        {
            PollHostOutcome(active);
        }
    }

    private void PollHostOpen(PartySelectHostState active, IReadOnlyList<RemotePeer> peers)
    {
        if (!bridge.TryReadPartySelectHostStatus(out uint applied, out uint status)
            || applied == _partySelectHostAppliedBaseline)
        {
            return;
        }

        if (status == 1) // PartySelectHostStatusCode.Opened
        {
            _partySelectHostOutcomeBaseline =
                bridge.TryReadPartySelectHostOutcome(out uint serial, out _) ? serial : 0;
            _partySelectHostActive = active with { AwaitingOutcome = true };

            session.SendPartySelectResponse(
                active.PeerId,
                new PartySelectResponsePacket(active.RequestSerial, 1, PartySelectResponseStatus.Opened, string.Empty));
            bridge.PostChatLine($"{PeerLabel(active.PeerId, peers)} wants to leave - choose your party");
        }
        else
        {
            session.SendPartySelectResponse(
                active.PeerId,
                new PartySelectResponsePacket(
                    active.RequestSerial, 1, PartySelectResponseStatus.Refused, "busy"));
            _partySelectHostActive = null;
        }
    }

    private void PollHostOutcome(PartySelectHostState active)
    {
        if (!bridge.TryReadPartySelectHostOutcome(out uint serial, out uint outcome)
            || serial == _partySelectHostOutcomeBaseline)
        {
            return;
        }

        PartySelectResponseStatus status = outcome == 1 // PartySelectOutcomeCode.Confirmed
            ? PartySelectResponseStatus.Confirmed
            : PartySelectResponseStatus.Cancelled;

        session.SendPartySelectResponse(
            active.PeerId, new PartySelectResponsePacket(active.RequestSerial, 2, status, string.Empty));

        log($"party-select: request #{active.RequestSerial} finished ({status})");
        _partySelectHostActive = null;
    }
}
