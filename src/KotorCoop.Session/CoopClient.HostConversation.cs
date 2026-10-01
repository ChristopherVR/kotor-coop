using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>CoopClient - the host-start conversation relay (see IHostConversationSession).</summary>
public sealed partial class CoopClient
{
    /// <inheritdoc />
    public void RequestHostConversation(string module, string tag, string resref, uint requestSerial)
    {
        if (requestSerial == 0)
        {
            return;
        }

        // NOT _sendBuffer: called from the plugin-pump thread - see RequestPartySelect's remark.
        SendFromCaller(
            new HostConversationRequestPacket(_localPeerId, requestSerial, module, tag, resref),
            static (buffer, packet) => PacketCodec.Write(buffer, packet));
    }
}
