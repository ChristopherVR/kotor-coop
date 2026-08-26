using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — Player chat: local lines queued for the tick loop to relay, and lines received
/// from clients queued for both this host's own display and relay to everyone else.
/// </summary>
public sealed partial class CoopHost
{
    /// <summary>Chat received from clients, for the host's own display.</summary>
    private readonly Queue<ChatMessage> _chatInbox = new();

    /// <summary>Chat to send from the tick loop (own lines and relays), with who to skip.</summary>
    private readonly Queue<(ChatPacket Packet, uint ExcludePeerId)> _chatOutbox = new();

    public void SendChat(string text)
    {
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        // Queued, not sent here: SendChat is called from the agent thread, but the send
        // buffer belongs to the tick loop. The host's own line is echoed locally by the
        // caller, so every client is a recipient (exclude only the host itself).
        lock (_gate)
        {
            _chatOutbox.Enqueue((new ChatPacket(HostPeerId, LocalName, text), HostPeerId));
        }
    }

    public IReadOnlyList<ChatMessage> DrainChat()
    {
        lock (_gate)
        {
            if (_chatInbox.Count == 0)
            {
                return [];
            }

            ChatMessage[] drained = [.. _chatInbox];
            _chatInbox.Clear();
            return drained;
        }
    }

    /// <summary>Flushes queued chat from the tick loop, so the send buffer is single-threaded.</summary>
    private void FlushChat(List<PeerConnection> recipients)
    {
        while (true)
        {
            (ChatPacket Packet, uint ExcludePeerId) item;
            lock (_gate)
            {
                if (_chatOutbox.Count == 0)
                {
                    return;
                }

                item = _chatOutbox.Dequeue();
            }

            int length = PacketCodec.Write(_sendBuffer, item.Packet);
            if (length == 0)
            {
                continue;
            }

            foreach (PeerConnection peer in recipients)
            {
                if (peer.Id != item.ExcludePeerId)
                {
                    _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
                }
            }
        }
    }

    private void HandleChat(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadChat(span) is not { } chat)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            // Only a known peer may chat, and it cannot spoof another's id.
            if (!_peers.ContainsKey(chat.FromPeerId))
            {
                return;
            }

            _chatInbox.Enqueue(new ChatMessage(chat.FromPeerId, chat.Name, chat.Text));
            // Relay to the others in the star; the sender already sees its own line.
            _chatOutbox.Enqueue((chat, chat.FromPeerId));
        }
    }
}
