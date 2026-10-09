namespace KotorCoop.Protocol;

/// <summary>What a player asks of the host's duel round (docs/research/pvp-plan.md).</summary>
public enum PvpOp : byte
{
    None = 0,

    /// <summary><c>duel &lt;name&gt;</c>: the target peer id rides in the packet.</summary>
    Challenge = 1,

    /// <summary><c>accept</c>.</summary>
    Accept = 2,

    /// <summary><c>decline</c>.</summary>
    Decline = 3,

    /// <summary><c>yield</c>: ends a duel or withdraws a challenge.</summary>
    Yield = 4,

    /// <summary><c>duel off</c>: refuse every challenge from now on.</summary>
    OptOut = 5,

    /// <summary><c>duel on</c>: take challenges again.</summary>
    OptIn = 6,

    /// <summary>"My game knows about duels." Sent every two seconds while the host allows
    /// them; a peer that never says it (an older build) is told no cleanly instead of being
    /// left to time out. Idempotent, so the host does not look at its serial.</summary>
    Aware = 7,
}

/// <summary>Where the host's single duel round is.</summary>
public enum PvpPhase : byte
{
    Idle = 0,

    /// <summary>A challenge is out and waiting for the target (30 seconds).</summary>
    Challenged = 1,

    /// <summary>Accepted: five seconds to get ready. Both players are already pinned at 1 hp
    /// minimum; nobody is hostile yet.</summary>
    Countdown = 2,

    /// <summary>The fight. Each duellist's stand-in is hostile to the other for as long as
    /// this lasts.</summary>
    Live = 3,
}

/// <summary>Every line a player can be told about a duel. The text lives in
/// <c>KotorCoop.Session.PvpText</c>; the wire carries only the code and who it is about.</summary>
public enum PvpNoticeCode : byte
{
    None = 0,

    /// <summary>To the challenger: the challenge went out.</summary>
    ChallengeSent = 1,

    /// <summary>To the target: someone challenged you.</summary>
    Challenged = 2,

    /// <summary>To the challenger: the target said no.</summary>
    Declined = 3,

    /// <summary>To the challenger: no answer in 30 seconds.</summary>
    TimedOut = 4,

    /// <summary>To the asker: the host has duels off.</summary>
    NotEnabled = 5,

    /// <summary>To the challenger: the target typed <c>duel off</c>.</summary>
    NotAccepting = 6,

    /// <summary>To the challenger: the target's game is an older build.</summary>
    Unsupported = 7,

    /// <summary>To the challenger: the target is in a conversation, a minigame or away.</summary>
    Busy = 8,

    /// <summary>To the asker: another duel, or a challenge, is already under way.</summary>
    DuelInProgress = 9,

    /// <summary>To the challenger: the target is in another area.</summary>
    NotHere = 10,

    /// <summary>To the challenger: <c>Other</c> is too hurt to start (below half health).</summary>
    TooHurt = 11,

    /// <summary>To the challenger: the same target was challenged less than a minute ago.</summary>
    TooSoon = 12,

    /// <summary>To both: the countdown began.</summary>
    Countdown = 13,

    /// <summary>To both: fight.</summary>
    Started = 14,

    /// <summary>To the winner: <c>Other</c> was beaten down.</summary>
    Won = 15,

    /// <summary>To the loser: <c>Other</c> beat you.</summary>
    Lost = 16,

    /// <summary>To the winner: <c>Other</c> yielded.</summary>
    WonByYield = 17,

    /// <summary>To the yielder: you gave up.</summary>
    YouYielded = 18,

    /// <summary>To the one left: <c>Other</c> left the game or the area mid-duel.</summary>
    WonByLeave = 19,

    /// <summary>To both: both reached the line in the same instant.</summary>
    Draw = 20,

    /// <summary>To both: the duel was called off (a conversation, a minigame, an area change)
    /// and nothing counts.</summary>
    Cancelled = 21,

    /// <summary>To the asker: nobody by that name is here.</summary>
    NoSuchPlayer = 22,

    /// <summary>To the asker: the request does not fit this moment (accept with nothing to
    /// accept, yield outside a duel).</summary>
    NothingToDo = 23,
}

/// <summary>One message for one player. <paramref name="Serial"/> numbers them in the host's
/// order so a receiver shows each once even though every state packet repeats the recent
/// ones.</summary>
public readonly record struct PvpNotice(uint Serial, uint ForPeer, PvpNoticeCode Code, uint Other);

/// <summary>One duel request, client to host. The host acts on a serial once; the sender
/// repeats the packet for a couple of seconds so a lost datagram costs nothing.</summary>
public sealed record PvpIntentPacket(uint HostSessionId, uint OriginPeerId, uint Serial, PvpOp Op, uint TargetPeerId);

/// <summary>
/// The host's duel round, as a level: <paramref name="Phase"/> and the two duellists
/// (<paramref name="Challenger"/> is the one who asked, <paramref name="Target"/> the one who
/// was asked; both 0 while idle), plus the last few <see cref="PvpNotice"/>s. Receivers drive
/// their own game from the phase (pin, snapshot, hostile body, restore) and show a notice once
/// per serial.
/// </summary>
public sealed record PvpStatePacket(
    uint HostSessionId, PvpPhase Phase, uint RoundId, uint Challenger, uint Target,
    IReadOnlyList<PvpNotice> Notices)
{
    public const int MaxNotices = 8;
}
