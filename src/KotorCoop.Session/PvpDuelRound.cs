using KotorCoop.Protocol;
using KotorCoop.Session.Pumps;

namespace KotorCoop.Session;

/// <summary>What the round needs to know about one player to judge a challenge or a fight. The
/// host builds it from the player's published state (<see cref="PlayerState"/>) every tick; the
/// round never reads the network or the game itself, which is what keeps it testable.</summary>
/// <param name="Present">Connected and publishing.</param>
/// <param name="Aware">Their build knows about duels (it said so; see <see cref="PvpOp.Aware"/>).
/// The host is always aware.</param>
/// <param name="Busy">In a conversation, a minigame, a story-forced solo segment or away from
/// the world: no duel starts and a running one is called off.</param>
/// <param name="Module">The area they stand in; two players in different areas cannot fight.</param>
/// <param name="Health">Current hit points; <paramref name="MaxHealth"/> 0 means unknown.</param>
/// <param name="Downed">Already on the floor in the engine's temp-dead state.</param>
/// <param name="ArenaReady">Their game can go to the arena: a build that knows arenas, with the
/// generated arena module in its modules folder (<see cref="PvpOp.AwareArena"/>).</param>
public readonly record struct PvpParticipant(
    bool Present, bool Aware, bool Busy, string Module, int Health, int MaxHealth, bool Downed,
    bool ArenaReady = false);

/// <summary>
/// The host's duel round (docs/research/pvp-plan.md): one at a time, between two consenting
/// players, held in one place so neither player's game has to be trusted to say who won.
///
/// <list type="number">
/// <item><b>Challenge.</b> <c>duel &lt;name&gt;</c>. Refused (with a plain line to the asker) if
/// the host has duels off, the other player's build does not know about duels, they typed
/// <c>duel off</c>, they are busy or in another area, either of them is below half health, a
/// round is already running, or the same target was challenged less than a minute ago.</item>
/// <item><b>Answer.</b> <c>accept</c> / <c>decline</c> from the target, or silence: 30 seconds
/// counts as no. The challenger can withdraw with <c>yield</c>.</item>
/// <item><b>Countdown</b> (5 s). Both players are pinned at 1 hp minimum from here, so the
/// engine never plays a real death; nobody is hostile yet.</item>
/// <item><b>Live.</b> The two stand-ins are hostile to each other. The round ends the moment
/// a duellist is at or below 25% of their maximum hit points, yields, leaves, goes down, or
/// either of them is pulled into a conversation, a minigame or another area (called off,
/// nothing counts). Both reaching the line in one look is a draw.</item>
/// </list>
///
/// An ARENA round (<c>arena &lt;name&gt;</c>, phases 3 and 4 of the plan) is the same fight with
/// two more phases around it. After the answer comes <see cref="PvpPhase.Entering"/>: the host's
/// game takes the whole party into the generated <c>k_coop_arena</c> module in ONE warp (the
/// retail engine crashes on rapid warps, so there is exactly one each way) and the round waits
/// until both duellists and the host have arrived and settled. Then the usual countdown and fight
/// in the arena. When the fight ends, for any reason, the round goes to
/// <see cref="PvpPhase.Leaving"/>: the host takes everyone back in one warp and the round only
/// goes idle once everyone is out of the arena (or a minute has passed). Nothing about the fight
/// itself changes; the pin, the hostile flip and the restore are the same.
///
/// Nobody dies and nothing is kept: the round only decides the phase. Restoring hit points and
/// Force points, unpinning, and flipping the bodies back are each player's own game reacting
/// to the phase going back to <see cref="PvpPhase.Idle"/> (<c>PvpDuelController</c> in the
/// plugin), so a host that drops mid-duel cannot strand anyone pinned: the session teardown
/// lowers the same level.
/// </summary>
public sealed class PvpDuelRound
{
    public const long ChallengeTimeoutMs = 30_000;
    public const long CountdownMs = 5_000;
    public const long RepeatChallengeGapMs = 60_000;

    /// <summary>The module name the arena lives in; a participant standing in it is "in the arena".</summary>
    public const string ArenaModule = "k_coop_arena";

    /// <summary>How long the host's single warp into the arena may take before the round is
    /// called off (two loading screens on slow machines, plus the follow).</summary>
    public const long ArenaEnterTimeoutMs = 60_000;

    /// <summary>How long everyone must have been in the arena (or, leaving, out of it) before the
    /// round moves on: the load has finished and the engine is quiet again.</summary>
    public const long ArenaSettleMs = 3_000;

    /// <summary>How long the way back may take before the round lets go anyway.</summary>
    public const long ArenaLeaveTimeoutMs = 60_000;

    /// <summary>A player whose state is missing this long during the warps is treated as gone.
    /// A module load blanks a player's published state for several seconds.</summary>
    public const long ArenaAbsentGraceMs = 20_000;

    /// <summary>A duellist at or below this share of their maximum hit points has lost.</summary>
    public const int EndThresholdPercent = 25;

    /// <summary>Both players must be above this share to start, or a duel could be over
    /// before the first blow (a player at 30% would lose at once to 25%).</summary>
    public const int MinStartPercent = 50;

    private readonly List<PvpNotice> _outbox = [];
    private readonly Dictionary<(uint Challenger, uint Target), long> _lastChallengeMs = [];
    private readonly HashSet<uint> _optedOut = [];
    private readonly Dictionary<uint, long> _absentSinceMs = [];
    private long _phaseDeadlineMs;
    private long _arenaSinceMs;
    private long _lastNowMs;

    /// <summary>The host's <c>pvp = duel</c>. Off refuses every challenge, and a round that is
    /// running when it goes off is called off.</summary>
    public bool Enabled { get; set; }

    /// <summary>The host's <c>pvp = arena</c>: arena challenges are allowed (duels must be too).</summary>
    public bool ArenaEnabled { get; set; }

    /// <summary>The host's own peer id: the host is the one who warps, so an arena round looks at
    /// it even when it is not a duellist.</summary>
    public uint HostPeerId { get; set; }

    /// <summary>The standing round is fought in the arena module.</summary>
    public bool Arena { get; private set; }

    public PvpPhase Phase { get; private set; }

    /// <summary>Counts up with every accepted challenge, so a player's game can tell round 4
    /// from round 5 even if the phase looks the same.</summary>
    public uint RoundId { get; private set; }

    public uint Challenger { get; private set; }

    public uint Target { get; private set; }

    public bool IsDuellist(uint peer) =>
        Phase != PvpPhase.Idle && (peer == Challenger || peer == Target);

    /// <summary>Whether <paramref name="peer"/> is pinned and (when live) hostile to the other.</summary>
    public bool IsFighting(uint peer) =>
        Phase is PvpPhase.Countdown or PvpPhase.Live && (peer == Challenger || peer == Target);

    public uint OpponentOf(uint peer) =>
        peer == Challenger ? Target : peer == Target ? Challenger : 0;

    public bool IsOptedOut(uint peer) => _optedOut.Contains(peer);

    /// <summary>True when a duellist's health is at or below the line. Unknown maximum never
    /// ends a round by itself (the players can still yield).</summary>
    public static bool IsBeaten(int health, int maxHealth) =>
        maxHealth > 0 && (long)health * 100 <= (long)maxHealth * EndThresholdPercent;

    /// <summary>True when a player has enough health to start. Unknown maximum passes.</summary>
    public static bool CanStart(int health, int maxHealth) =>
        maxHealth <= 0 || (long)health * 100 > (long)maxHealth * MinStartPercent;

    /// <summary>Standing in the arena module, as the world reports it.</summary>
    public static bool InArena(in PvpParticipant p) =>
        p.Present && string.Equals(p.Module, ArenaModule, StringComparison.OrdinalIgnoreCase);

    /// <summary>Everything the round wants to say since the last call, in order. The session
    /// numbers them and delivers them.</summary>
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

    public void SetOptOut(uint peer, bool optedOut)
    {
        if (optedOut)
        {
            _optedOut.Add(peer);
        }
        else
        {
            _optedOut.Remove(peer);
        }
    }

    /// <summary>A peer left the session: forget their opt-out and call off anything they were in.</summary>
    public void PeerLeft(uint peer)
    {
        _optedOut.Remove(peer);
        foreach ((uint Challenger, uint Target) key in _lastChallengeMs.Keys
            .Where(k => k.Challenger == peer || k.Target == peer).ToList())
        {
            _lastChallengeMs.Remove(key);
        }
    }

    /// <summary><c>duel &lt;name&gt;</c>, or <c>arena &lt;name&gt;</c> when <paramref name="arena"/>
    /// (with <paramref name="host"/> the host's own picture, which an arena challenge also needs
    /// unless the host is one of the two).</summary>
    public void Challenge(
        uint challenger, uint target, PvpParticipant asker, PvpParticipant other, long nowMs,
        bool arena = false, PvpParticipant host = default)
    {
        _lastNowMs = nowMs;
        if (!Enabled)
        {
            Say(challenger, PvpNoticeCode.NotEnabled);
            return;
        }

        if (arena && !ArenaEnabled)
        {
            Say(challenger, PvpNoticeCode.ArenaNotEnabled);
            return;
        }

        if (challenger == target || !other.Present)
        {
            Say(challenger, PvpNoticeCode.NoSuchPlayer);
            return;
        }

        if (Phase != PvpPhase.Idle)
        {
            Say(challenger, PvpNoticeCode.DuelInProgress);
            return;
        }

        if (!other.Aware)
        {
            Say(challenger, PvpNoticeCode.Unsupported, target);
            return;
        }

        if (_optedOut.Contains(target))
        {
            Say(challenger, PvpNoticeCode.NotAccepting, target);
            return;
        }

        if (other.Busy || asker.Busy)
        {
            Say(challenger, PvpNoticeCode.Busy, asker.Busy ? challenger : target);
            return;
        }

        if (!string.Equals(asker.Module, other.Module, StringComparison.OrdinalIgnoreCase)
            || asker.Module.Length == 0)
        {
            Say(challenger, PvpNoticeCode.NotHere, target);
            return;
        }

        if (!CanStart(asker.Health, asker.MaxHealth) || asker.Downed)
        {
            Say(challenger, PvpNoticeCode.TooHurt, challenger);
            return;
        }

        if (!CanStart(other.Health, other.MaxHealth) || other.Downed)
        {
            Say(challenger, PvpNoticeCode.TooHurt, target);
            return;
        }

        if (arena)
        {
            // Everyone who has to be in the arena needs its module: the two players, and the host
            // (whose single warp takes the party). The host also has to be where they are, or
            // that warp would not be the whole party's.
            PvpParticipant hostView = challenger == HostPeerId ? asker : target == HostPeerId ? other : host;
            if (!asker.ArenaReady)
            {
                Say(challenger, PvpNoticeCode.ArenaMissing, challenger);
                return;
            }

            if (!other.ArenaReady)
            {
                Say(challenger, PvpNoticeCode.ArenaMissing, target);
                return;
            }

            if (!hostView.ArenaReady)
            {
                Say(challenger, PvpNoticeCode.ArenaMissing, HostPeerId);
                return;
            }

            if (!string.Equals(hostView.Module, asker.Module, StringComparison.OrdinalIgnoreCase))
            {
                Say(challenger, PvpNoticeCode.ArenaHostApart, HostPeerId);
                return;
            }
        }

        if (_lastChallengeMs.TryGetValue((challenger, target), out long last)
            && nowMs - last < RepeatChallengeGapMs)
        {
            Say(challenger, PvpNoticeCode.TooSoon, target);
            return;
        }

        _lastChallengeMs[(challenger, target)] = nowMs;
        Arena = arena;
        Phase = PvpPhase.Challenged;
        Challenger = challenger;
        Target = target;
        _phaseDeadlineMs = nowMs + ChallengeTimeoutMs;
        Say(challenger, PvpNoticeCode.ChallengeSent, target);
        Say(target, PvpNoticeCode.Challenged, challenger);
    }

    /// <summary><c>accept</c>: only the target of a standing challenge.</summary>
    public void Accept(uint peer, PvpParticipant challenger, PvpParticipant target, long nowMs)
    {
        if (Phase != PvpPhase.Challenged || peer != Target)
        {
            Say(peer, PvpNoticeCode.NothingToDo);
            return;
        }

        _lastNowMs = nowMs;
        if (!StillFits(challenger, target))
        {
            Cancel();
            return;
        }

        RoundId++;
        LastEnding = $"round {RoundId}: still running";

        if (Arena)
        {
            // The host's game now warps the party to the arena; the countdown waits for them.
            Phase = PvpPhase.Entering;
            _phaseDeadlineMs = nowMs + ArenaEnterTimeoutMs;
            _arenaSinceMs = 0;
            _absentSinceMs.Clear();
            Say(Challenger, PvpNoticeCode.ArenaEntering, Target);
            Say(Target, PvpNoticeCode.ArenaEntering, Challenger);
            return;
        }

        Phase = PvpPhase.Countdown;
        _phaseDeadlineMs = nowMs + CountdownMs;
        Say(Challenger, PvpNoticeCode.Countdown, Target);
        Say(Target, PvpNoticeCode.Countdown, Challenger);
    }

    /// <summary><c>decline</c>: only the target of a standing challenge.</summary>
    public void Decline(uint peer)
    {
        if (Phase != PvpPhase.Challenged || peer != Target)
        {
            Say(peer, PvpNoticeCode.NothingToDo);
            return;
        }

        Say(Challenger, PvpNoticeCode.Declined, Target);
        Reset();
    }

    /// <summary><c>yield</c>: ends a fight (the yielder loses), or withdraws a challenge.</summary>
    public void Yield(uint peer)
    {
        // Once the way back has begun there is nothing left to give up.
        if (Phase is PvpPhase.Idle or PvpPhase.Leaving || (peer != Challenger && peer != Target))
        {
            Say(peer, PvpNoticeCode.NothingToDo);
            return;
        }

        if (Phase != PvpPhase.Challenged)
        {
            LastEnding = $"round {RoundId}: peer {peer} yielded";
        }

        if (Phase == PvpPhase.Challenged)
        {
            if (peer == Challenger)
            {
                Say(Challenger, PvpNoticeCode.Cancelled);
                Say(Target, PvpNoticeCode.Cancelled);
                Reset();
            }
            else
            {
                Decline(peer);
            }

            return;
        }

        uint winner = OpponentOf(peer);
        Say(peer, PvpNoticeCode.YouYielded, winner);
        Say(winner, PvpNoticeCode.WonByYield, peer);
        End();
    }

    /// <summary>
    /// One look at the world. <paramref name="lookup"/> returns the current picture of a peer
    /// (host included); a peer it cannot find is treated as gone.
    /// </summary>
    public void Tick(long nowMs, Func<uint, PvpParticipant> lookup)
    {
        _lastNowMs = nowMs;
        if (Phase == PvpPhase.Idle)
        {
            return;
        }

        PvpParticipant a = lookup(Challenger);
        PvpParticipant b = lookup(Target);

        // The way back runs even if duels were switched off meanwhile: it is what un-strands
        // the party.
        if (Phase == PvpPhase.Leaving)
        {
            TickLeaving(nowMs, a, b, lookup);
            return;
        }

        if (!Enabled)
        {
            Cancel();
            return;
        }

        if (Phase == PvpPhase.Challenged)
        {
            if (!a.Present || !b.Present)
            {
                Cancel();
            }
            else if (nowMs >= _phaseDeadlineMs)
            {
                Say(Challenger, PvpNoticeCode.TimedOut, Target);
                Reset();
            }

            return;
        }

        if (Phase == PvpPhase.Entering)
        {
            TickEntering(nowMs, a, b, lookup);
            return;
        }

        // Countdown or Live. A player who is gone forfeits; everything else that no longer
        // fits calls it off with nothing counted.
        if (!a.Present || !b.Present)
        {
            uint leaver = !a.Present ? Challenger : Target;
            uint stayer = leaver == Challenger ? Target : Challenger;
            if (lookup(stayer).Present)
            {
                Say(stayer, PvpNoticeCode.WonByLeave, leaver);
            }

            LastEnding = $"round {RoundId}: peer {leaver} left";
            End();
            return;
        }

        if (!StillFits(a, b))
        {
            Cancel();
            return;
        }

        if (Phase == PvpPhase.Countdown)
        {
            if (nowMs >= _phaseDeadlineMs)
            {
                Phase = PvpPhase.Live;
                Say(Challenger, PvpNoticeCode.Started, Target);
                Say(Target, PvpNoticeCode.Started, Challenger);
            }

            return;
        }

        bool aOut = a.Downed || IsBeaten(a.Health, a.MaxHealth);
        bool bOut = b.Downed || IsBeaten(b.Health, b.MaxHealth);
        if (aOut || bOut)
        {
            LastEnding = $"round {RoundId}: challenger {a.Health}/{a.MaxHealth}{(a.Downed ? " downed" : string.Empty)}, "
                + $"target {b.Health}/{b.MaxHealth}{(b.Downed ? " downed" : string.Empty)}";
        }

        if (aOut && bOut)
        {
            Say(Challenger, PvpNoticeCode.Draw, Target);
            Say(Target, PvpNoticeCode.Draw, Challenger);
            End();
        }
        else if (aOut || bOut)
        {
            uint loser = aOut ? Challenger : Target;
            uint winner = aOut ? Target : Challenger;
            Say(winner, PvpNoticeCode.Won, loser);
            Say(loser, PvpNoticeCode.Lost, winner);
            End();
        }
    }

    /// <summary>Arena only: the host's game is taking everyone in. Wait for the three (the two
    /// duellists and the host) to be in the module and quiet, then start the countdown.</summary>
    private void TickEntering(long nowMs, PvpParticipant a, PvpParticipant b, Func<uint, PvpParticipant> lookup)
    {
        bool aGone = IsGone(Challenger, a, nowMs);
        bool bGone = IsGone(Target, b, nowMs);
        if (aGone || bGone)
        {
            uint leaver = aGone ? Challenger : Target;
            uint stayer = leaver == Challenger ? Target : Challenger;
            Say(stayer, PvpNoticeCode.WonByLeave, leaver);
            LastEnding = $"round {RoundId}: peer {leaver} left on the way in";
            End();
            return;
        }

        if (a.Busy || b.Busy)
        {
            Cancel();
            return;
        }

        if (nowMs >= _phaseDeadlineMs)
        {
            Say(Challenger, PvpNoticeCode.ArenaFailed, Target);
            Say(Target, PvpNoticeCode.ArenaFailed, Challenger);
            LastEnding = $"round {RoundId}: the party did not all reach the arena in time";
            End();
            return;
        }

        PvpParticipant host = lookup(HostPeerId);
        if (!(InArena(a) && InArena(b) && InArena(host)))
        {
            _arenaSinceMs = 0;
            return;
        }

        if (_arenaSinceMs == 0)
        {
            _arenaSinceMs = nowMs;
            return;
        }

        if (nowMs - _arenaSinceMs >= ArenaSettleMs)
        {
            Phase = PvpPhase.Countdown;
            _phaseDeadlineMs = nowMs + CountdownMs;
            Say(Challenger, PvpNoticeCode.Countdown, Target);
            Say(Target, PvpNoticeCode.Countdown, Challenger);
        }
    }

    /// <summary>Arena only: the way back. Idle once the two duellists and the host are all out of
    /// the arena (or gone) and quiet, or when the minute is up.</summary>
    private void TickLeaving(long nowMs, PvpParticipant a, PvpParticipant b, Func<uint, PvpParticipant> lookup)
    {
        PvpParticipant host = lookup(HostPeerId);
        bool allOut = IsOut(Challenger, a, nowMs) && IsOut(Target, b, nowMs) && IsOut(HostPeerId, host, nowMs);

        if (nowMs >= _phaseDeadlineMs)
        {
            LastEnding += "; the way back timed out";
            Reset();
            return;
        }

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

        if (nowMs - _arenaSinceMs >= ArenaSettleMs)
        {
            LastEnding += "; everyone is back";
            Reset();
        }
    }

    /// <summary>Gone for good: not published for the whole grace, which outlasts a module load.</summary>
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

        return nowMs - since >= ArenaAbsentGraceMs;
    }

    private bool IsOut(uint peer, in PvpParticipant p, long nowMs) =>
        IsGone(peer, p, nowMs)
        || (p.Present && p.Module.Length > 0 && !ModuleNameGuard.IsFallbackModuleName(p.Module) && !InArena(p));

    /// <summary>The session ended (or the host went): drop the round without a word.</summary>
    /// <summary>How the last round ended (health/max/down of each side as judged), for the log.</summary>
    public string LastEnding { get; private set; } = string.Empty;

    public void Clear()
    {
        Reset();
        _outbox.Clear();
        _optedOut.Clear();
        _lastChallengeMs.Clear();
    }

    private bool StillFits(PvpParticipant a, PvpParticipant b)
    {
        if (a.Busy || b.Busy || a.Module.Length == 0)
        {
            return false;
        }

        // Fighting in the arena (countdown and live) means both are IN it; before the warp the
        // challenge only needed them to share an area.
        return Arena && Phase is PvpPhase.Countdown or PvpPhase.Live
            ? InArena(a) && InArena(b)
            : string.Equals(a.Module, b.Module, StringComparison.OrdinalIgnoreCase);
    }

    private void Cancel()
    {
        LastEnding = $"round {RoundId}: called off (busy, apart or gone)";
        Say(Challenger, PvpNoticeCode.Cancelled, Target);
        Say(Target, PvpNoticeCode.Cancelled, Challenger);
        End();
    }

    /// <summary>The fight, or the way in, is over. A plain duel goes idle; an arena round goes
    /// back out first.</summary>
    private void End()
    {
        if (Arena && Phase is PvpPhase.Entering or PvpPhase.Countdown or PvpPhase.Live)
        {
            BeginLeaving();
            return;
        }

        Reset();
    }

    private void BeginLeaving()
    {
        Phase = PvpPhase.Leaving;
        _phaseDeadlineMs = _lastNowMs + ArenaLeaveTimeoutMs;
        _arenaSinceMs = 0;
        _absentSinceMs.Clear();
        Say(Challenger, PvpNoticeCode.ArenaReturning, Target);
        Say(Target, PvpNoticeCode.ArenaReturning, Challenger);
    }

    private void Reset()
    {
        Phase = PvpPhase.Idle;
        Challenger = 0;
        Target = 0;
        Arena = false;
        _phaseDeadlineMs = 0;
        _arenaSinceMs = 0;
        _absentSinceMs.Clear();
    }
}
