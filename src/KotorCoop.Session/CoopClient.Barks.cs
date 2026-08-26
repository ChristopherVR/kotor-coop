using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — NPC barks: this client's own barks sent to the host, and barks relayed from
/// other peers applied here. Unreliable — a lost bark costs a line, never consistency.
/// </summary>
public sealed partial class CoopClient
{
    // ---- NPC barks ----------------------------------------------------------------
    //
    // A client both sends the barks its own instance produced and applies the ones the host
    // relays from everyone else. Unreliable: a lost bark costs a line, not consistency.

    private readonly Queue<BarkPacket> _barkOutbox = new();
    private readonly Queue<BarkPacket> _barkInbox = new();

    public void PublishBark(string module, uint speaker, byte[] text, byte[] soundResRef, uint flags, uint serial)
    {
        lock (_gate)
        {
            _barkOutbox.Enqueue(new BarkPacket(_localPeerId, module, speaker, text, soundResRef, flags, serial));
        }
    }

    public IReadOnlyList<BarkPacket> DrainBarks(string module)
    {
        lock (_gate)
        {
            if (_barkInbox.Count == 0)
            {
                return [];
            }

            List<BarkPacket> ready = [];
            while (_barkInbox.Count > 0)
            {
                BarkPacket bark = _barkInbox.Dequeue();

                // Ids are per module load, so a bark from elsewhere names nothing here.
                if (string.Equals(bark.Module, module, StringComparison.OrdinalIgnoreCase))
                {
                    ready.Add(bark);
                }
            }

            return ready;
        }
    }

    private void FlushBarks()
    {
        while (true)
        {
            BarkPacket bark;
            lock (_gate)
            {
                if (_barkOutbox.Count == 0)
                {
                    return;
                }

                bark = _barkOutbox.Dequeue();
            }

            int length = PacketCodec.Write(_sendBuffer, bark);
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    private void HandleBark(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadBark(span) is { } bark)
        {
            lock (_gate)
            {
                _barkInbox.Enqueue(bark);
            }
        }
    }
}
