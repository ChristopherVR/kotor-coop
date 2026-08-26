using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — Player chat: local lines queued for the tick loop to send, and lines received
/// from the host queued for this client's own display.
/// </summary>
public sealed partial class CoopClient
{
    private readonly Queue<ChatMessage> _chatInbox = new();
    private readonly Queue<string> _chatOutbox = new();

    public void SendChat(string text)
    {
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        // Queued for the tick loop, which owns the send buffer.
        lock (_gate)
        {
            _chatOutbox.Enqueue(text);
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

    private void FlushChat()
    {
        while (true)
        {
            string text;
            lock (_gate)
            {
                if (_chatOutbox.Count == 0)
                {
                    return;
                }

                text = _chatOutbox.Dequeue();
            }

            int length = PacketCodec.Write(
                _sendBuffer, new ChatPacket(_localPeerId, _settings.PlayerName, text));
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    private void HandleChat(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadChat(span) is { } chat)
        {
            lock (_gate)
            {
                _chatInbox.Enqueue(new ChatMessage(chat.FromPeerId, chat.Name, chat.Text));
            }
        }
    }
}
