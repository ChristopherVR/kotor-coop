using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — duels (docs/research/pvp-plan.md). The host holds the one <see cref="PvpDuelRound"/>,
/// judges it every tick from the players' published state, and broadcasts it as a level (twice a
/// second and on change) along with the last few messages for players. Its own player's requests
/// go straight in; a client's arrive as <see cref="PvpIntentPacket"/>s.
/// </summary>
public sealed partial class CoopHost
{
    private const int PvpStateIntervalMs = 500;

    /// <summary>A peer stays "aware of duels" this long after it last said so.</summary>
    private const int PvpAwareHoldMs = 6_000;

    /// <summary>A message is repeated in the state packet for this long, then forgotten.</summary>
    private const int PvpNoticeLifeMs = 6_000;

    private readonly PvpDuelRound _pvp = new();
    private readonly List<(PvpNotice Notice, long AtMs)> _pvpRecent = [];
    private readonly List<PvpNotice> _pvpLocalNotices = [];
    private uint _pvpNoticeSerial;
    private bool _pvpChanged;
    private long _lastPvpSentMs;
    private (PvpPhase Phase, uint Round) _pvpLastPhase;

    public bool PvpEnabled => _settings.Behaviour.Has(BehaviourFlags.Pvp);

    public bool PvpArenaEnabled => _settings.Behaviour.Has(BehaviourFlags.PvpArena);

    public bool PvpArenaReady => _settings.PvpArenaReady;

    public PvpView PvpState
    {
        get
        {
            lock (_gate)
            {
                return new PvpView(_pvp.Phase, _pvp.RoundId, _pvp.Challenger, _pvp.Target, _pvp.Arena);
            }
        }
    }

    public void RequestPvp(PvpOp op, uint target = 0)
    {
        lock (_gate)
        {
            ApplyPvpOp(LocalPeerId, op, target, SessionClock.NowMs);
        }
    }

    public IReadOnlyList<PvpNotice> DrainPvpNotices()
    {
        lock (_gate)
        {
            if (_pvpLocalNotices.Count == 0)
            {
                return [];
            }

            PvpNotice[] drained = [.. _pvpLocalNotices];
            _pvpLocalNotices.Clear();
            return drained;
        }
    }

    /// <summary>Caller holds <see cref="_gate"/>.</summary>
    private void ApplyPvpOp(uint origin, PvpOp op, uint target, long now)
    {
        SyncPvpSettings();

        switch (op)
        {
            case PvpOp.Challenge:
                _pvp.Challenge(origin, target, PvpParticipantOf(origin, now), PvpParticipantOf(target, now), now);
                break;

            case PvpOp.ChallengeArena:
                _pvp.Challenge(
                    origin, target, PvpParticipantOf(origin, now), PvpParticipantOf(target, now), now,
                    arena: true, host: PvpParticipantOf(LocalPeerId, now));
                break;

            case PvpOp.Accept:
                _pvp.Accept(
                    origin, PvpParticipantOf(_pvp.Challenger, now), PvpParticipantOf(_pvp.Target, now), now,
                    PvpParticipantOf(LocalPeerId, now));
                break;

            case PvpOp.Decline:
                _pvp.Decline(origin);
                break;

            case PvpOp.Yield:
                _pvp.Yield(origin);
                break;

            case PvpOp.OptOut:
                _pvp.SetOptOut(origin, true);
                break;

            case PvpOp.OptIn:
                _pvp.SetOptOut(origin, false);
                break;
        }

        CollectPvpNotices(now);
    }

    /// <summary>The settings the round reads, refreshed before it is asked anything. Caller
    /// holds <see cref="_gate"/>.</summary>
    private void SyncPvpSettings()
    {
        _pvp.Enabled = PvpEnabled;
        _pvp.ArenaEnabled = PvpArenaEnabled;
        _pvp.HostPeerId = LocalPeerId;
    }

    /// <summary>What the round needs to know about <paramref name="peer"/> right now. Caller
    /// holds <see cref="_gate"/>.</summary>
    private PvpParticipant PvpParticipantOf(uint peer, long now)
    {
        if (peer == LocalPeerId)
        {
            return ParticipantFrom(_localState, _hasLocalState, aware: true, arenaReady: PvpArenaReady);
        }

        return _peers.TryGetValue(peer, out PeerConnection? connection)
            ? ParticipantFrom(
                connection.LastState, present: true, aware: now < connection.PvpAwareUntilMs,
                arenaReady: now < connection.PvpArenaAwareUntilMs)
            : default;
    }

    private static PvpParticipant ParticipantFrom(in PlayerState state, bool present, bool aware, bool arenaReady) => new(
        Present: present && state.Module.Length > 0 && !state.IsAway,
        Aware: aware,
        Busy: state.IsInDialogue || state.IsInMinigame || state.IsInSoloSegment,
        Module: state.Module,
        Health: state.Health,
        MaxHealth: state.MaxHealth,
        Downed: state.IsDowned,
        ArenaReady: arenaReady);

    /// <summary>Moves what the round said into the numbered recent list (for the broadcast)
    /// and the host's own queue. Caller holds <see cref="_gate"/>.</summary>
    private void CollectPvpNotices(long now)
    {
        foreach (PvpNotice said in _pvp.DrainNotices())
        {
            PvpNotice numbered = said with { Serial = ++_pvpNoticeSerial };
            if (numbered.ForPeer == LocalPeerId)
            {
                _pvpLocalNotices.Add(numbered);
            }
            else
            {
                _pvpRecent.Add((numbered, now));
            }

            _pvpChanged = true;
        }

        _pvpRecent.RemoveAll(entry => now - entry.AtMs > PvpNoticeLifeMs);
    }

    private void HandlePvpIntent(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPvpIntent(span) is not { } packet)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (packet.OriginPeerId == LocalPeerId
                || !_peers.TryGetValue(packet.OriginPeerId, out PeerConnection? peer) || !peer.EndPoint.Equals(from))
            {
                return;
            }

            if (packet.Op is PvpOp.Aware or PvpOp.AwareArena)
            {
                peer.PvpAwareUntilMs = now + PvpAwareHoldMs;

                // AwareArena says "duels AND I have the arena module"; plain Aware takes the
                // second half back, so a player who removed the module stops being offered.
                peer.PvpArenaAwareUntilMs = packet.Op == PvpOp.AwareArena ? now + PvpAwareHoldMs : 0;
                return;
            }

            // Wrap-safe "newer than the last one acted on": the sender repeats a request for
            // two seconds, so the same serial must do nothing the second time.
            if (peer.PvpSerialKnown && (int)(packet.Serial - peer.PvpLastSerial) <= 0)
            {
                return;
            }

            peer.PvpSerialKnown = true;
            peer.PvpLastSerial = packet.Serial;
            ApplyPvpOp(packet.OriginPeerId, packet.Op, packet.TargetPeerId, now);
        }
    }

    /// <summary>Judges the round and sends it. Runs every tick from the broadcast pass.</summary>
    public string PvpLastEnding
    {
        get
        {
            lock (_gate)
            {
                return _pvp.LastEnding;
            }
        }
    }

    private void BroadcastPvp(long now, List<PeerConnection> recipients)
    {
        PvpStatePacket packet;
        lock (_gate)
        {
            SyncPvpSettings();
            _pvp.Tick(now, id => PvpParticipantOf(id, now));
            CollectPvpNotices(now);

            if (_pvpLastPhase != (_pvp.Phase, _pvp.RoundId))
            {
                _pvpLastPhase = (_pvp.Phase, _pvp.RoundId);
                _pvpChanged = true;
            }

            bool idleAndQuiet = _pvp.Phase == PvpPhase.Idle && _pvpRecent.Count == 0;
            if (recipients.Count == 0 || (!_pvpChanged && (idleAndQuiet || now - _lastPvpSentMs < PvpStateIntervalMs)))
            {
                return;
            }

            _pvpChanged = false;
            _lastPvpSentMs = now;
            packet = new PvpStatePacket(
                _sessionId, _pvp.Phase, _pvp.RoundId, _pvp.Challenger, _pvp.Target,
                [.. _pvpRecent.Select(entry => entry.Notice).TakeLast(PvpStatePacket.MaxNotices)],
                _pvp.Arena);
        }

        int length = PacketCodec.Write(_sendBuffer, packet);
        if (length == 0)
        {
            return;
        }

        foreach (PeerConnection peer in recipients)
        {
            _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
        }
    }
}
