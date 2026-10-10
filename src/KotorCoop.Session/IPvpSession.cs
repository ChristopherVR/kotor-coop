// Duels between players (docs/research/pvp-plan.md): the host holds the round, everyone asks it.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>The duel round as one player's game sees it: which phase, and who is in it.</summary>
/// <param name="Boss">The round is the boss fight (<c>chungus</c>): every player is in it, all on
/// one side, in the arena. <paramref name="Challenger"/> is the host and there is no target.</param>
public readonly record struct PvpView(
    PvpPhase Phase, uint RoundId, uint Challenger, uint Target, bool Arena = false, bool Boss = false)
{
    public static PvpView Idle => default;

    /// <summary>An arena round is past the question: the party is heading to the arena, fighting
    /// in it, or coming back. Saving stays blocked for everyone for all of it, because the
    /// arena module would otherwise be baked into the campaign save.</summary>
    public bool ArenaActive =>
        Arena && Phase is PvpPhase.Entering or PvpPhase.Countdown or PvpPhase.Live or PvpPhase.Leaving;

    /// <summary>One of the two players in the round (whatever the phase).</summary>
    public bool IsDuellist(uint peer) =>
        Phase != PvpPhase.Idle && (peer == Challenger || peer == Target);

    /// <summary>The player is pinned at 1 hp minimum and (once live, in a duel) hostile to the
    /// other. In a boss fight that is every player.</summary>
    public bool IsFighting(uint peer) =>
        Phase is PvpPhase.Countdown or PvpPhase.Live && (Boss || peer == Challenger || peer == Target);

    /// <summary>The boss fight is on its way in, under way, or on its way out.</summary>
    public bool BossActive => Boss && ArenaActive;

    /// <summary>The other duellist, or 0 when <paramref name="peer"/> is not in the round.</summary>
    public uint OpponentOf(uint peer) =>
        peer == Challenger ? Target : peer == Target ? Challenger : 0;
}

public interface IPvpSession
{
    /// <summary>Whether this session allows duels: the host's <c>pvp = duel</c>, adopted by a
    /// client the way every host-authoritative behaviour flag is.</summary>
    bool PvpEnabled => false;

    /// <summary>Whether this session also allows arena fights (<c>pvp = arena</c>), adopted by a
    /// client from the host.</summary>
    bool PvpArenaEnabled => false;

    /// <summary>Whether THIS install had the arena module when the game started, so this player
    /// can be taken to it.</summary>
    bool PvpArenaReady => false;

    /// <summary>Whether THIS install can load the arena boss (and the arena), so this player can
    /// be taken to the boss fight.</summary>
    bool BossReady => false;

    /// <summary>The round, as this player's game should act on it. A client that has not heard
    /// from the host for a few seconds reads this as idle, so a lost host never strands a
    /// player pinned.</summary>
    PvpView PvpState => PvpView.Idle;

    /// <summary>Host: how the last round ended, both duellists' health/max/down as judged, for
    /// the log. Empty on a client.</summary>
    string PvpLastEnding => string.Empty;

    /// <summary>Asks the host for something (a challenge to <paramref name="target"/>, an
    /// answer, a yield, or opting out). Delivered with a short resend; the answer comes back as
    /// notices.</summary>
    void RequestPvp(PvpOp op, uint target = 0) { }

    /// <summary>Lines for this player since the last call, oldest first.</summary>
    IReadOnlyList<PvpNotice> DrainPvpNotices() => [];

    /// <summary>The boss's powers used since the last call, oldest first, for this game to play
    /// out (sound, and the result on its own player). Every game gets every one.</summary>
    IReadOnlyList<BossCue> DrainBossCues() => [];

    /// <summary>Host only: the boss uses a power. Said to every player.</summary>
    void AnnounceBossCue(BossCue cue) { }

    /// <summary>Host only: the host's game saw the boss go down.</summary>
    void ReportBossDown() { }
}
