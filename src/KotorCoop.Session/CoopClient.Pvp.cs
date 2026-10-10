using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — duels (docs/research/pvp-plan.md). Requests go to the host as
/// <see cref="PvpIntentPacket"/>s repeated for two seconds; the host's round arrives as a level
/// (<see cref="PvpStatePacket"/>) and is what this player's game acts on.
/// </summary>
public sealed partial class CoopClient
{
    private const int PvpResendIntervalMs = 250;
    private const int PvpResendWindowMs = 2_000;
    private const int PvpAwareIntervalMs = 2_000;

    /// <summary>No round news for this long reads as "idle": a host that went quiet in the middle
    /// of a duel must not leave this player pinned and fighting. The host repeats a live round
    /// twice a second, so this is eight lost packets in a row.</summary>
    private const int PvpStaleMs = 4_000;

    private PvpView _pvpView;
    private long _pvpViewAtMs;
    private uint _pvpNoticesSeenSerial;
    private uint _pvpNoticeSession;
    private readonly List<PvpNotice> _pvpInbox = [];
    private readonly List<BossCue> _bossCues = [];

    // The request being repeated. Seeded from the clock so a rejoin (which keeps its peer id)
    // never restarts below a serial the host has already seen.
    private uint _pvpSerial = (uint)(Environment.TickCount64 & 0x7FFFFFFF);
    private PvpIntentPacket? _pvpOutgoing;
    private long _pvpOutgoingFirstMs;
    private long _pvpOutgoingLastMs;
    private long _lastPvpAwareMs;

    public bool PvpEnabled => _overrides.Pvp ?? _settings.Behaviour.Has(BehaviourFlags.Pvp);

    public bool PvpArenaEnabled => _overrides.PvpArena ?? _settings.Behaviour.Has(BehaviourFlags.PvpArena);

    public bool PvpArenaReady => _settings.PvpArenaReady;

    public bool BossReady => _settings.BossReady;

    public int BossAppearanceRow => _settings.BossAppearanceRow;

    public PvpView PvpState
    {
        get
        {
            lock (_gate)
            {
                return SessionClock.NowMs - _pvpViewAtMs > PvpStaleMs ? PvpView.Idle : _pvpView;
            }
        }
    }

    public void RequestPvp(PvpOp op, uint target = 0)
    {
        lock (_gate)
        {
            // Said here, not sent: with duels off a request would only come back as the same
            // refusal a round trip later (or, on an older host, never).
            if (!PvpEnabled && op is PvpOp.Challenge or PvpOp.ChallengeArena or PvpOp.Accept)
            {
                _pvpInbox.Add(new PvpNotice(0, _localPeerId, PvpNoticeCode.NotEnabled, 0));
                return;
            }

            if (PvpEnabled && !PvpArenaEnabled && op == PvpOp.ChallengeArena)
            {
                _pvpInbox.Add(new PvpNotice(0, _localPeerId, PvpNoticeCode.ArenaNotEnabled, 0));
                return;
            }

            _pvpOutgoing = new PvpIntentPacket(_hostSessionId, _localPeerId, ++_pvpSerial, op, target);
            _pvpOutgoingFirstMs = SessionClock.NowMs;
            _pvpOutgoingLastMs = 0;
        }
    }

    public IReadOnlyList<PvpNotice> DrainPvpNotices()
    {
        lock (_gate)
        {
            if (_pvpInbox.Count == 0)
            {
                return [];
            }

            PvpNotice[] drained = [.. _pvpInbox];
            _pvpInbox.Clear();
            return drained;
        }
    }

    public IReadOnlyList<BossCue> DrainBossCues()
    {
        lock (_gate)
        {
            if (_bossCues.Count == 0)
            {
                return [];
            }

            BossCue[] drained = [.. _bossCues];
            _bossCues.Clear();
            return drained;
        }
    }

    private void FlushPvp(long now)
    {
        PvpIntentPacket packet;
        lock (_gate)
        {
            if (_localPeerId == 0 || _hostSessionId == 0)
            {
                return;
            }

            if (_pvpOutgoing is { } pending && now - _pvpOutgoingFirstMs <= PvpResendWindowMs)
            {
                if (now - _pvpOutgoingLastMs < PvpResendIntervalMs)
                {
                    return;
                }

                _pvpOutgoingLastMs = now;
                packet = pending with { HostSessionId = _hostSessionId, OriginPeerId = _localPeerId };
            }
            else
            {
                _pvpOutgoing = null;

                // Nothing to ask: say "my game knows about duels", so the host does not turn a
                // challenge to this player away as an older build.
                // "I have the arena" is said whatever the pvp setting is: the host may start the
                // boss fight (chungus) without it, and has to know who can be taken along.
                if ((!PvpEnabled && !PvpArenaReady) || now - _lastPvpAwareMs < PvpAwareIntervalMs)
                {
                    return;
                }

                _lastPvpAwareMs = now;
                packet = new PvpIntentPacket(
                    _hostSessionId, _localPeerId, 0,
                    PvpArenaReady ? PvpOp.AwareArena : PvpOp.Aware,
                    PvpArenaReady && BossReady ? PvpAwareBits.Boss : 0);
            }
        }

        int length = PacketCodec.Write(_sendBuffer, packet);
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    private void HandlePvpState(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPvpState(span) is not { } packet)
        {
            return;
        }

        ApplyPvpState(packet, now);
    }

    internal void ApplyPvpState(PvpStatePacket packet, long now)
    {
        lock (_gate)
        {
            if (packet.HostSessionId != _hostSessionId)
            {
                return;
            }

            _pvpView = new PvpView(
                packet.Phase, packet.RoundId, packet.Challenger, packet.Target, packet.Arena, packet.Boss);
            _pvpViewAtMs = now;

            // The host numbers its messages from 1 each session, so a new host session restarts the
            // high-water mark. There is deliberately no "first packet only seeds it" rule: the first
            // packet a client ever hears can be the one carrying its first message, and a message is
            // only ever shown to the id it is addressed to, six seconds at most after it was said.
            if (packet.HostSessionId != _pvpNoticeSession)
            {
                _pvpNoticeSession = packet.HostSessionId;
                _pvpNoticesSeenSerial = 0;
            }

            foreach (PvpNotice notice in packet.Notices.OrderBy(n => n.Serial))
            {
                if ((int)(notice.Serial - _pvpNoticesSeenSerial) <= 0)
                {
                    continue;
                }

                _pvpNoticesSeenSerial = notice.Serial;
                // Peer 0 is everyone: the boss fight's lines and the boss's powers.
                if (notice.ForPeer == _localPeerId || notice.ForPeer == 0)
                {
                    _pvpInbox.Add(notice);
                    if (notice.Code == PvpNoticeCode.BossPower)
                    {
                        _bossCues.Add(BossCue.Unpack(notice.Other));
                    }
                }
            }
        }
    }
}
