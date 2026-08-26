using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — Peer effects: the non-damage sibling of phantom damage above (stun, paralyze,
/// knockdown, Force powers landing on a peer's phantom) — a queue of discrete
/// events, not a running total, and unreliable like barks.
/// </summary>
public sealed partial class CoopClient
{
    // ---- peer effects (non-damage combat effects) ---------------------------------
    //
    // Non-damage sibling of phantom damage above — see docs/research/effect-sync.md. A queue
    // of discrete events (send once, gone), not a running total, exactly like barks: this
    // instance both sends what its own hook captured and applies what the host relays from
    // other clients.

    private readonly Queue<PeerEffectPacket> _peerEffectOutbox = new();
    private readonly Queue<PeerEffectPacket> _peerEffectInbox = new();
    private uint _peerEffectSequence;

    public void PublishPeerEffect(uint targetPeerId, PeerEffectKind kind, uint durationMs, uint magnitude)
    {
        if (targetPeerId == 0 || _localPeerId == 0)
        {
            return;
        }

        lock (_gate)
        {
            _peerEffectOutbox.Enqueue(
                new PeerEffectPacket(_localPeerId, targetPeerId, kind, durationMs, magnitude, ++_peerEffectSequence));
        }
    }

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
        if (PacketCodec.ReadPeerEffect(span) is not { } packet || packet.TargetPeerId != _localPeerId)
        {
            return;
        }

        lock (_gate)
        {
            _peerEffectInbox.Enqueue(packet);
        }
    }

    private void SendPeerEffects()
    {
        List<PeerEffectPacket> pending;
        lock (_gate)
        {
            if (_peerEffectOutbox.Count == 0)
            {
                return;
            }

            pending = [.. _peerEffectOutbox];
            _peerEffectOutbox.Clear();
        }

        foreach (PeerEffectPacket packet in pending)
        {
            int length = PacketCodec.Write(_sendBuffer, packet);
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }
}
