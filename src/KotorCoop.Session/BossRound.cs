using KotorCoop.Protocol;
using KotorCoop.Session.Pumps;

namespace KotorCoop.Session;

/// <summary>
/// The host's boss fight (<c>chungus</c>; docs/research/chungus-boss.md): every player against
/// one host-spawned boss in the arena module. It borrows the arena round's shape
/// (<see cref="PvpDuelRound"/>): <see cref="PvpPhase.Entering"/> while the host's single warp
/// takes the party in, a <see cref="PvpPhase.Countdown"/>, <see cref="PvpPhase.Live"/> while the
/// boss stands, and <see cref="PvpPhase.Leaving"/> while the host's single warp takes everyone
/// home. Nothing is asked of anyone: there is no challenge to accept.
///
/// Everyone is pinned at 1 hit point minimum from the countdown on, so nobody really dies and
/// nothing reaches the campaign. The fight ends when the boss is down (the host's game says so,
/// <see cref="BossDown"/>), when every player is beaten down at the same time, when anyone types
/// <c>yield</c>, when the host leaves the arena, or after <see cref="FightLimitMs"/>.
///
/// Like the duel round it never reads the network or the game: the host hands it each player's
/// picture every tick, which is what keeps it testable.
/// </summary>
public sealed class BossRound
{
    public const long CountdownMs = 5_000;

    /// <summary>The longest a fight may run before it is called a draw and everyone goes home.</summary>
    public const long FightLimitMs = 10 * 60_000;

    /// <summary>Every player has to stay beaten this long for it to count: a heal in the same
    /// breath should not be too late.</summary>
    public const long AllDownHoldMs = 2_500;

    /// <summary>A pinned player sits at exactly 1 hit point. Anything at or below this share of
    /// their maximum counts as beaten too, for a game whose pin has not caught up.</summary>
    public const int BeatenPercent = 3;

    public const uint FirstRoundId = 0x4000_0001;

    private readonly List<PvpNotice> _outbox = [];
    private readonly HashSet<uint> _players = [];
    private readonly Dictionary<uint, long> _absentSinceMs = [];
    private long _phaseDeadlineMs;
    private long _arenaSinceMs;
    private long _allDownSinceMs;
    private long _lastNowMs;
    private bool _bossDown;

    public PvpPhase Phase { get; private set; }

    public uint RoundId { get; private set; }

    /// <summary>The host's own peer id: the one whose game warps the party and owns the boss.</summary>
    public uint HostPeerId { get; set; }

    public bool Active => Phase != PvpPhase.Idle;

    /// <summary>The players taken along when the fight began.</summary>
    public IReadOnlyCollection<uint> Players => _players;

    public string LastEnding { get; private set; } = string.Empty;

    public static bool IsBeaten(in PvpParticipant p) =>
        p.Downed || p.Health <= 1 || (p.MaxHealth > 0 && (long)p.Health * 100 <= (long)p.MaxHealth * BeatenPercent);

    public IReadOnlyList<PvpNotice> DrainNotices()
    {
        if (_outbox.Count == 0)
        {
            return [];
        }

        PvpNotice[] drained = [.. _outbox];
        _outbox.Clear();
        return drained;
    }

    private void Say(uint forPeer, PvpNoticeCode code, uint other = 0) =>
        _outbox.Add(new PvpNotice(0, forPeer, code, other));

    private void SayAll(PvpNoticeCode code, uint other = 0) => Say(0, code, other);

    /// <summary>
    /// <c>chungus</c> from <paramref name="asker"/>. <paramref name="everyone"/> is every connected
    /// player (the host included) with their current picture. <paramref name="duelBusy"/> is a
    /// duel or a challenge already under way; <paramref name="joinersMayStart"/> is the host's
    /// <c>pvp = arena</c> (without it only the host may start one).
    /// </summary>
    public void Start(
        uint asker, IReadOnlyList<(uint Peer, PvpParticipant State)> everyone, long nowMs,
        bool duelBusy, bool joinersMayStart)
    {
        _lastNowMs = nowMs;
        if (Active || duelBusy)
        {
            Say(asker, PvpNoticeCode.DuelInProgress);
            return;
        }

        if (asker != HostPeerId && !joinersMayStart)
        {
            Say(asker, PvpNoticeCode.BossHostOnly, HostPeerId);
            return;
        }

        PvpParticipant host = everyone.FirstOrDefault(e => e.Peer == HostPeerId).State;
        foreach ((uint peer, PvpParticipant state) in everyone)
        {
            // One warp takes the party, so everyone has to be with the host, free to go, and
            // able to load the arena.
            bool apart = !string.Equals(state.Module, host.Module, StringComparison.OrdinalIgnoreCase)
                || state.Module.Length == 0
                || string.Equals(state.Module, PvpDuelRound.ArenaModule, StringComparison.OrdinalIgnoreCase);
            if (!state.Present || state.Busy || state.Downed || !state.BossReady || apart)
            {
                Say(asker, PvpNoticeCode.BossNotReady, peer);
                return;
            }
        }

        _players.Clear();
        foreach ((uint peer, _) in everyone)
        {
            _players.Add(peer);
        }

        // Its own range, so a duel's round 3 and a boss fight's round 3 are never the same number
        // to a game that tells rounds apart by id (the arena flow, the pin).
        RoundId = Math.Max(RoundId, FirstRoundId - 1) + 1;
        LastEnding = $"boss round {RoundId}: still running";
        Phase = PvpPhase.Entering;
        _phaseDeadlineMs = nowMs + PvpDuelRound.ArenaEnterTimeoutMs;
        _arenaSinceMs = 0;
        _allDownSinceMs = 0;
        _bossDown = false;
        _absentSinceMs.Clear();
        SayAll(PvpNoticeCode.BossEntering, asker);
    }

    /// <summary><c>yield</c> from any player in the fight calls it off for everyone.</summary>
    public bool Yield(uint peer)
    {
        if (Phase is PvpPhase.Idle or PvpPhase.Leaving || !_players.Contains(peer))
        {
            return false;
        }

        LastEnding = $"boss round {RoundId}: peer {peer} called it off";
        SayAll(PvpNoticeCode.BossCancelled, peer);
        BeginLeaving();
        return true;
    }

    /// <summary>The host's game saw the boss die (or vanish after it had been seen).</summary>
    public void BossDown()
    {
        if (Phase == PvpPhase.Live)
        {
            _bossDown = true;
        }
    }

    /// <summary>The host uses a power: said to everyone, played out by each game.</summary>
    public void Announce(BossCue cue)
    {
        if (Phase is PvpPhase.Countdown or PvpPhase.Live && cue.IsKnown)
        {
            SayAll(PvpNoticeCode.BossPower, cue.Pack());
        }
    }

    public void PeerLeft(uint peer) => _players.Remove(peer);

    /// <summary>One look at the world. <paramref name="lookup"/> gives the current picture of a
    /// peer; one it cannot find is treated as gone.</summary>
    public void Tick(long nowMs, Func<uint, PvpParticipant> lookup)
    {
        _lastNowMs = nowMs;
        switch (Phase)
        {
            case PvpPhase.Entering:
                TickEntering(nowMs, lookup);
                break;

            case PvpPhase.Countdown:
            case PvpPhase.Live:
                TickFight(nowMs, lookup);
                break;

            case PvpPhase.Leaving:
                TickLeaving(nowMs, lookup);
                break;
        }
    }

    private void TickEntering(long nowMs, Func<uint, PvpParticipant> lookup)
    {
        if (nowMs >= _phaseDeadlineMs)
        {
            LastEnding = $"boss round {RoundId}: the party did not all reach the arena in time";
            SayAll(PvpNoticeCode.ArenaFailed);
            BeginLeaving();
            return;
        }

        DropTheGone(nowMs, lookup);
        if (!_players.Contains(HostPeerId))
        {
            Reset();
            return;
        }

        bool allIn = _players.All(peer => PvpDuelRound.InArena(lookup(peer)));
        if (!allIn)
        {
            _arenaSinceMs = 0;
            return;
        }

        if (_arenaSinceMs == 0)
        {
            _arenaSinceMs = nowMs;
            return;
        }

        if (nowMs - _arenaSinceMs >= PvpDuelRound.ArenaSettleMs)
        {
            Phase = PvpPhase.Countdown;
            _phaseDeadlineMs = nowMs + CountdownMs;
            SayAll(PvpNoticeCode.BossCountdown);
        }
    }

    private void TickFight(long nowMs, Func<uint, PvpParticipant> lookup)
    {
        DropTheGone(nowMs, lookup);

        // The host owns the boss and the way home: without it in the ring there is no fight.
        PvpParticipant host = lookup(HostPeerId);
        if (!_players.Contains(HostPeerId) || (host.Present && !PvpDuelRound.InArena(host)))
        {
            LastEnding = $"boss round {RoundId}: the host left the arena";
            SayAll(PvpNoticeCode.BossCancelled);
            BeginLeaving();
            return;
        }

        if (Phase == PvpPhase.Countdown)
        {
            if (nowMs >= _phaseDeadlineMs)
            {
                Phase = PvpPhase.Live;
                _phaseDeadlineMs = nowMs + FightLimitMs;
                SayAll(PvpNoticeCode.BossStarted);
            }

            return;
        }

        if (_bossDown)
        {
            LastEnding = $"boss round {RoundId}: the boss was beaten";
            SayAll(PvpNoticeCode.BossWon);
            BeginLeaving();
            return;
        }

        if (nowMs >= _phaseDeadlineMs)
        {
            LastEnding = $"boss round {RoundId}: out of time";
            SayAll(PvpNoticeCode.BossTimedOut);
            BeginLeaving();
            return;
        }

        // Beaten only when every player who is still here is down at once, and stays down.
        List<PvpParticipant> present = [.. _players.Select(lookup).Where(p => p.Present)];
        bool allDown = present.Count > 0 && present.All(p => IsBeaten(p));
        if (!allDown)
        {
            _allDownSinceMs = 0;
            return;
        }

        if (_allDownSinceMs == 0)
        {
            _allDownSinceMs = nowMs;
            return;
        }

        if (nowMs - _allDownSinceMs >= AllDownHoldMs)
        {
            LastEnding = $"boss round {RoundId}: every player was beaten ({present.Count})";
            SayAll(PvpNoticeCode.BossLost);
            BeginLeaving();
        }
    }

    private void TickLeaving(long nowMs, Func<uint, PvpParticipant> lookup)
    {
        if (nowMs >= _phaseDeadlineMs)
        {
            LastEnding += "; the way back timed out";
            Reset();
            return;
        }

        bool allOut = _players.Append(HostPeerId).Distinct().All(peer => IsOut(peer, lookup(peer), nowMs));
        if (!allOut)
        {
            _arenaSinceMs = 0;
            return;
        }

        if (_arenaSinceMs == 0)
        {
            _arenaSinceMs = nowMs;
            return;
        }

        if (nowMs - _arenaSinceMs >= PvpDuelRound.ArenaSettleMs)
        {
            LastEnding += "; everyone is back";
            Reset();
        }
    }

    /// <summary>A player who has been missing for longer than a module load takes is out of the
    /// fight; the rest carry on.</summary>
    private void DropTheGone(long nowMs, Func<uint, PvpParticipant> lookup)
    {
        foreach (uint peer in _players.ToList())
        {
            if (IsGone(peer, lookup(peer), nowMs))
            {
                _players.Remove(peer);
            }
        }
    }

    private bool IsGone(uint peer, in PvpParticipant p, long nowMs)
    {
        if (p.Present)
        {
            _absentSinceMs.Remove(peer);
            return false;
        }

        if (!_absentSinceMs.TryGetValue(peer, out long since))
        {
            _absentSinceMs[peer] = nowMs;
            return false;
        }

        return nowMs - since >= PvpDuelRound.ArenaAbsentGraceMs;
    }

    private bool IsOut(uint peer, in PvpParticipant p, long nowMs) =>
        IsGone(peer, p, nowMs)
        || (p.Present && p.Module.Length > 0 && !ModuleNameGuard.IsFallbackModuleName(p.Module)
            && !PvpDuelRound.InArena(p));

    private void BeginLeaving()
    {
        Phase = PvpPhase.Leaving;
        _phaseDeadlineMs = _lastNowMs + PvpDuelRound.ArenaLeaveTimeoutMs;
        _arenaSinceMs = 0;
        _allDownSinceMs = 0;
        _absentSinceMs.Clear();
        SayAll(PvpNoticeCode.ArenaReturning);
    }

    /// <summary>The session ended: drop the round without a word.</summary>
    public void Clear()
    {
        Reset();
        _outbox.Clear();
    }

    private void Reset()
    {
        Phase = PvpPhase.Idle;
        _players.Clear();
        _phaseDeadlineMs = 0;
        _arenaSinceMs = 0;
        _allDownSinceMs = 0;
        _bossDown = false;
        _absentSinceMs.Clear();
    }
}
