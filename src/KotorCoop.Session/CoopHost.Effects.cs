using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — Peer effects: the non-damage sibling of phantom damage above (stun, paralyze,
/// knockdown, Force powers landing on a peer's phantom) — a queue of discrete
/// events, not a running total, and unreliable like barks.
/// </summary>
public sealed partial class CoopHost
{
    // ---- peer effects (non-damage combat effects) ---------------------------------
    //
    // The non-damage sibling of phantom damage, above: stun, paralyze, knockdown, slow,
    // frightened, sleep, choke, Force push/whirlwind landing on a peer's phantom, relayed to
    // the player that phantom stands for. A QUEUE, not a running total — each is a discrete
    // event with its own duration, and unlike damage there is nothing to sum. Unreliable, like
    // barks: a lost one costs a crowd-control tick, never a desync. See
    // docs/research/effect-sync.md.

    private readonly Queue<PeerEffectPacket> _peerEffectInbox = new();
    private readonly Queue<(PeerEffectPacket Packet, uint ExceptPeer)> _peerEffectOutbox = new();

    public void PublishPeerEffect(uint targetPeerId, PeerEffectKind kind, uint durationMs, uint magnitude)
    {
        if (targetPeerId == 0)
        {
            return;
        }

        PeerEffectPacket packet = new(LocalPeerId, targetPeerId, kind, durationMs, magnitude, ++_peerEffectSequence);

        lock (_gate)
        {
            if (targetPeerId == LocalPeerId)
            {
                _peerEffectInbox.Enqueue(packet);
            }
            else
            {
                _peerEffectOutbox.Enqueue((packet, LocalPeerId));
            }
        }
    }

    private uint _peerEffectSequence;

    public IReadOnlyList<PeerEffectPacket> DrainPeerEffects()
    {
        lock (_gate)
        {
            if (_peerEffectInbox.Count == 0)
            {
                return [];
            }

            List<PeerEffectPacket> ready = [.. _peerEffectInbox];
            _peerEffectInbox.Clear();
            return ready;
        }
    }

    private void HandlePeerEffect(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPeerEffect(span) is not { } packet)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (!_peers.ContainsKey(packet.FromPeerId))
            {
                return; // only a known peer, and it cannot speak for another
            }

            if (packet.TargetPeerId == LocalPeerId)
            {
                _peerEffectInbox.Enqueue(packet);
            }
            else if (packet.TargetPeerId != 0)
            {
                // Relayed to the addressee only — everyone else has no phantom for this
                // target and nothing to do with it.
                _peerEffectOutbox.Enqueue((packet, 0));
            }
        }
    }

    private void BroadcastPeerEffects(List<PeerConnection> recipients)
    {
        List<(PeerEffectPacket Packet, uint ExceptPeer)> pending;
        lock (_gate)
        {
            if (_peerEffectOutbox.Count == 0)
            {
                return;
            }

            pending = [.. _peerEffectOutbox];
            _peerEffectOutbox.Clear();
        }

        foreach ((PeerEffectPacket packet, uint except) in pending)
        {
            foreach (PeerConnection peer in recipients)
            {
                if (peer.Id != packet.TargetPeerId || peer.Id == except)
                {
                    continue;
                }

                int length = PacketCodec.Write(_sendBuffer, packet);
                if (length > 0)
                {
                    _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
                }
            }
        }
    }
}
