using KotorCoop.Protocol;

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
public readonly record struct PvpParticipant(
    bool Present, bool Aware, bool Busy, string Module, int Health, int MaxHealth, bool Downed);

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

    /// <summary>A duellist at or below this share of their maximum hit points has lost.</summary>
    public const int EndThresholdPercent = 25;

    /// <summary>Both players must be above this share to start, or a duel could be over
    /// before the first blow (a player at 30% would lose at once to 25%).</summary>
    public const int MinStartPercent = 50;

    private readonly List<PvpNotice> _outbox = [];
    private readonly Dictionary<(uint Challenger, uint Target), long> _lastChallengeMs = [];
    private readonly HashSet<uint> _optedOut = [];
    private long _phaseDeadlineMs;

    /// <summary>The host's <c>pvp = duel</c>. Off refuses every challenge, and a round that is
    /// running when it goes off is called off.</summary>
    public bool Enabled { get; set; }

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

    /// <summary><c>duel &lt;name&gt;</c>.</summary>
    public void Challenge(
        uint challenger, uint target, PvpParticipant asker, PvpParticipant other, long nowMs)
    {
        if (!Enabled)
        {
            Say(challenger, PvpNoticeCode.NotEnabled);
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

        if (_lastChallengeMs.TryGetValue((challenger, target), out long last)
            && nowMs - last < RepeatChallengeGapMs)
        {
            Say(challenger, PvpNoticeCode.TooSoon, target);
            return;
        }

        _lastChallengeMs[(challenger, target)] = nowMs;
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

        if (!StillFits(challenger, target))
        {
            Cancel();
            return;
        }

        RoundId++;
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
        if (Phase == PvpPhase.Idle || (peer != Challenger && peer != Target))
        {
            Say(peer, PvpNoticeCode.NothingToDo);
            return;
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
        Reset();
    }

    /// <summary>
    /// One look at the world. <paramref name="lookup"/> returns the current picture of a peer
    /// (host included); a peer it cannot find is treated as gone.
    /// </summary>
    public void Tick(long nowMs, Func<uint, PvpParticipant> lookup)
    {
        if (Phase == PvpPhase.Idle)
        {
            return;
        }

        PvpParticipant a = lookup(Challenger);
        PvpParticipant b = lookup(Target);

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

            Reset();
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
        if (aOut && bOut)
        {
            Say(Challenger, PvpNoticeCode.Draw, Target);
            Say(Target, PvpNoticeCode.Draw, Challenger);
            Reset();
        }
        else if (aOut || bOut)
        {
            uint loser = aOut ? Challenger : Target;
            uint winner = aOut ? Target : Challenger;
            Say(winner, PvpNoticeCode.Won, loser);
            Say(loser, PvpNoticeCode.Lost, winner);
            Reset();
        }
    }

    /// <summary>The session ended (or the host went): drop the round without a word.</summary>
    public void Clear()
    {
        Reset();
        _outbox.Clear();
        _optedOut.Clear();
        _lastChallengeMs.Clear();
    }

    private static bool StillFits(PvpParticipant a, PvpParticipant b) =>
        !a.Busy && !b.Busy
        && a.Module.Length > 0
        && string.Equals(a.Module, b.Module, StringComparison.OrdinalIgnoreCase);

    private void Cancel()
    {
        Say(Challenger, PvpNoticeCode.Cancelled, Target);
        Say(Target, PvpNoticeCode.Cancelled, Challenger);
        Reset();
    }

    private void Reset()
    {
        Phase = PvpPhase.Idle;
        Challenger = 0;
        Target = 0;
        _phaseDeadlineMs = 0;
    }
}
