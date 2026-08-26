using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session.Channels;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — NPC barks: any instance can produce one, everyone else should hear it. Unreliable
/// — a lost bark costs a line, never consistency.
/// </summary>
public sealed partial class CoopHost
{
    // ---- NPC barks ----------------------------------------------------------------
    //
    // Any instance can produce one and everybody else should hear it, so the host both
    // consumes barks (its own player should see a client's droid beep) and relays them to
    // the other clients. Unreliable by design: a lost bark costs a line.

    private readonly RelayQueue<BarkPacket> _barks = new();

    public void PublishBark(string module, uint speaker, byte[] text, byte[] soundResRef, uint flags, uint serial)
    {
        BarkPacket packet = new(LocalPeerId, module, speaker, text, soundResRef, flags, serial);
        lock (_gate)
        {
            _barks.EnqueueOutbox(packet, LocalPeerId);
        }
    }

    public IReadOnlyList<BarkPacket> DrainBarks(string module)
    {
        lock (_gate)
        {
            IReadOnlyList<BarkPacket> all = _barks.DrainInbox();
            if (all.Count == 0)
            {
                return [];
            }

            List<BarkPacket> ready = [];
            foreach (BarkPacket bark in all)
            {
                // Ids are per module load, so a bark from somewhere else names nothing here.
                if (string.Equals(bark.Module, module, StringComparison.OrdinalIgnoreCase))
                {
                    ready.Add(bark);
                }
            }

            return ready;
        }
    }

    private void HandleBark(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadBark(span) is not { } bark)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (!_peers.ContainsKey(bark.PeerId))
            {
                return; // only a known peer, and it cannot speak for another
            }

            _barks.EnqueueInbox(bark);
            _barks.EnqueueOutbox(bark, bark.PeerId); // everyone but the sender
        }
    }

    private void BroadcastBarks(List<PeerConnection> recipients)
    {
        List<(BarkPacket Packet, uint ExceptPeerId)> pending;
        lock (_gate)
        {
            pending = _barks.DrainOutbox();
        }

        if (pending.Count == 0)
        {
            return;
        }

        foreach ((BarkPacket packet, uint except) in pending)
        {
            int length = PacketCodec.Write(_sendBuffer, packet);
            if (length <= 0)
            {
                continue;
            }

            foreach (PeerConnection peer in recipients)
            {
                if (peer.Id != except)
                {
                    _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
                }
            }
        }
    }
}
