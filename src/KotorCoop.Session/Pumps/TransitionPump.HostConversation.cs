using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

// The host-start conversation relay (docs/research/dialogue-sync.md): a joiner whose engine
// refused a host-only conversation start (HostLedConversationRule) asks the host to start it.
// Two independent halves in one file, the same shape as TransitionPump.PartySelect.cs: the
// CLIENT half forwards the plugin's capture; the HOST half validates and hands it to the host
// plugin. One-shot, no response - the joiner learns the outcome by watching the conversation
// open (the host owns the token; the ordinary auto-join machinery takes it from there).
internal sealed partial class TransitionPump
{
    private uint _hostConversationBaselineSerial;
    private bool _hostConversationBaselined;
    private long _hostConversationNextSendMs;

    /// <summary>One request per this long, even if the plugin publishes faster.</summary>
    internal const int HostConversationDebounceMs = 3_000;

    public void PumpHostConversation(in PlayerState local, IReadOnlyList<RemotePeer> peers)
    {
        PumpHostConversationClient(local);
        PumpHostConversationHost(local);
    }

    private void PumpHostConversationClient(in PlayerState local)
    {
        bool readable = bridge.TryReadHostConversationClientCapture(
            out uint serial, out string tag, out string resref);

        // The block outlives sessions in this process: a leftover serial from before this
        // session is a baseline, not a fresh capture (see PumpPartySelectClient).
        if (!_hostConversationBaselined)
        {
            _hostConversationBaselined = true;
            _hostConversationBaselineSerial = readable ? serial : 0;
            return;
        }

        if (!readable || serial == _hostConversationBaselineSerial)
        {
            return;
        }

        _hostConversationBaselineSerial = serial;

        // Only ever relay a conversation the shared list says is host-led.
        if (!HostLedConversationRule.IsHostLed(resref, tag))
        {
            log($"host-conversation: not relaying '{tag}'/'{resref}' - not a host-led conversation");
            return;
        }

        long now = SessionClock.NowMs;
        if (now < _hostConversationNextSendMs)
        {
            return;
        }

        _hostConversationNextSendMs = now + HostConversationDebounceMs;
        session.RequestHostConversation(local.Module, tag, resref, serial);
        log($"host-conversation: asked the host to start '{tag}' (resref='{resref}')");
    }

    private void PumpHostConversationHost(in PlayerState local)
    {
        bool weAreHost = session.IsEnemyAuthority;

        foreach (HostConversationRequestPacket request in session.DrainHostConversationRequests())
        {
            if (!weAreHost)
            {
                continue;
            }

            if (!string.Equals(request.Module, local.Module, StringComparison.OrdinalIgnoreCase))
            {
                // The sender has moved on (or we have): a stale request is not actionable.
                log($"host-conversation: dropping peer {request.PeerId}'s request #{request.RequestSerial} "
                    + $"for '{request.Tag}' - it was filed in {request.Module}, the host is in {local.Module}");
                continue;
            }

            if (!HostLedConversationRule.IsHostLed(request.Resref, request.Tag))
            {
                log($"host-conversation: dropping peer {request.PeerId}'s request #{request.RequestSerial} - "
                    + $"'{request.Tag}'/'{request.Resref}' is not a host-led conversation");
                continue;
            }

            if (bridge.TryRequestHostConversation(request.PeerId, request.Tag, request.Resref))
            {
                log($"host-conversation: peer {request.PeerId} asked the host to start '{request.Tag}' "
                    + $"(resref='{request.Resref}')");
            }
            else
            {
                log($"host-conversation: could not hand peer {request.PeerId}'s request to the plugin (no block?)");
            }
        }
    }
}
