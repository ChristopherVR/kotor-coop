using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// The plain-language line for each <see cref="PvpNoticeCode"/> (docs/research/pvp-product.md
/// section 2). Wording rules are the project's: say what happened and what is safe, no jargon,
/// no blame. Kept pure so every code has a test that it says something and names the right
/// player.
/// </summary>
public static class PvpText
{
    public const string UsageLine =
        "duel <name> challenges a player. They type accept or decline. yield stops a duel. duel off refuses all challenges.";

    public const string ArenaUsageLine =
        "arena <name> challenges a player to a duel in the arena. They type accept or decline, the same as a duel.";

    public const string OptedOutLine = "You will not get duel challenges. Type duel on to allow them again.";

    public const string OptedInLine = "You can be challenged to a duel again.";

    public const string HostOffLine =
        "The host has not turned on duels. Ask them, or see the pvp setting.";

    public const string SavingBlockedLine = "You cannot save during a duel. Finish it or type yield first.";

    public const string SavingBlockedInArenaLine =
        "You cannot save while the arena fight is on or in the arena. Wait until everyone is back.";

    /// <summary>The line a notice becomes. <paramref name="otherName"/> is the player the notice
    /// is about (<see cref="PvpNotice.Other"/>); <paramref name="aboutSelf"/> is true when that
    /// player is the reader (the "you are too hurt" case).</summary>
    public const string BossNotInstalledLine =
        "Chungus is not installed in this game yet. Run the kotor-coop setup again, then restart the game.";

    public const string BossUsageLine =
        "chungus takes every player to the arena to fight Chungus together. Type yield to call it off.";

    /// <summary>The line for one of the boss's powers. <paramref name="targetName"/> is who it is
    /// aimed at ("you" for the reader); it is ignored by the powers that hit everyone.</summary>
    public static string FormatBossPower(BossCue cue, string targetName, bool aimedAtReader) => cue.Power switch
    {
        BossPower.Snap => "Chungus snaps his fingers. Everyone loses half their health. \"Perfectly balanced, as all things should be.\"",
        BossPower.Blast => aimedAtReader
            ? "Chungus fires the Power Stone at you."
            : $"Chungus fires the Power Stone at {targetName}.",
        BossPower.Lift => "Chungus folds space. Everyone is thrown somewhere else, and so is he.",
        BossPower.Gizka => aimedAtReader
            ? "Chungus rewrites reality. You are a gizka for a while."
            : $"Chungus rewrites reality. {targetName} is a gizka for a while.",
        BossPower.Fright => "Chungus stares into your minds. Everyone is frightened.",
        BossPower.Drain => aimedAtReader
            ? "Chungus grips your soul and heals himself."
            : $"Chungus grips {targetName}'s soul and heals himself.",
        BossPower.Slow => "Chungus stops time. Nobody can move but him.",
        BossPower.Entrance => "Chungus has arrived.",
        BossPower.Defeat => "Chungus falls over.",
        // He does it every few seconds: seen, not announced.
        BossPower.Blink => string.Empty,
        _ => string.Empty,
    };

    public static string Format(PvpNoticeCode code, string otherName, bool aboutSelf = false) => code switch
    {
        PvpNoticeCode.ChallengeSent =>
            $"You challenged {otherName} to a duel. Waiting for an answer.",
        PvpNoticeCode.Challenged =>
            $"{otherName} challenges you to a duel. Type accept or decline. No answer in 30 seconds counts as no.",
        PvpNoticeCode.Declined => $"{otherName} said no. Nothing happened.",
        PvpNoticeCode.TimedOut => $"{otherName} did not answer. Nothing happened.",
        PvpNoticeCode.NotEnabled => HostOffLine,
        PvpNoticeCode.NotAccepting => $"{otherName} is not accepting duels.",
        PvpNoticeCode.Unsupported =>
            $"{otherName}'s game does not support duels yet. They need the latest version.",
        PvpNoticeCode.Busy => aboutSelf
            ? "You are in a conversation or a minigame. Try again in a moment."
            : $"{otherName} is in a conversation or a minigame. Try again in a moment.",
        PvpNoticeCode.DuelInProgress =>
            "A duel is already under way, or a challenge is waiting for an answer. Try again in a moment.",
        PvpNoticeCode.NotHere => $"{otherName} is not in this area. Go to them first.",
        PvpNoticeCode.TooHurt => aboutSelf
            ? "You are too hurt to duel. Heal up to more than half health first."
            : $"{otherName} is too hurt to duel right now.",
        PvpNoticeCode.TooSoon => $"You challenged {otherName} a moment ago. Give it a minute.",
        PvpNoticeCode.Countdown =>
            $"Duel with {otherName} starts in 5. Type yield at any time to stop.",
        PvpNoticeCode.Started => $"Fight! Duel with {otherName}.",
        PvpNoticeCode.Won =>
            $"You won the duel against {otherName}. You are back to how you were before it. Nothing was lost.",
        PvpNoticeCode.Lost =>
            $"{otherName} won the duel. You are back to how you were before it. Nothing was lost.",
        PvpNoticeCode.WonByYield =>
            $"{otherName} yielded. You won. You are back to how you were before the duel.",
        PvpNoticeCode.YouYielded =>
            $"You yielded to {otherName}. You are back to how you were before the duel.",
        PvpNoticeCode.WonByLeave =>
            $"{otherName} left, so the duel is over. You are back to how you were before it.",
        PvpNoticeCode.Draw =>
            $"The duel with {otherName} ended in a draw. You are back to how you were before it.",
        PvpNoticeCode.Cancelled =>
            $"The duel with {otherName} was called off. Nothing was lost.",
        PvpNoticeCode.NoSuchPlayer => "Nobody here has that name. Type who to see the players.",
        PvpNoticeCode.NothingToDo => "There is no duel or challenge for that right now.",
        PvpNoticeCode.ArenaNotEnabled =>
            "The host has not turned on the arena. Ask them to set pvp = arena, or fight with duel.",
        PvpNoticeCode.ArenaMissing => aboutSelf
            ? "You do not have the arena installed. Run the kotor-coop setup again, then restart the game."
            : $"{otherName} does not have the arena installed yet, or has an older version. They need to run the kotor-coop setup again.",
        PvpNoticeCode.ArenaHostApart =>
            $"{otherName}, the host, has to be in this area too so everyone can be moved together.",
        PvpNoticeCode.ArenaEntering =>
            $"Arena duel with {otherName}. Moving everyone to the arena now. Type yield to call it off.",
        PvpNoticeCode.ArenaReturning =>
            "The arena fight is over. Taking everyone back to where they were. Hold on.",
        PvpNoticeCode.ArenaFailed =>
            $"Getting everyone into the arena took too long, so the fight with {otherName} was called off. Nothing was lost.",
        PvpNoticeCode.BossEntering => aboutSelf
            ? "You called for Chungus. Moving everyone to the arena now. Type yield to call it off."
            : $"{otherName} called for Chungus. Moving everyone to the arena now. Type yield to call it off.",
        PvpNoticeCode.BossNotReady => aboutSelf
            ? "You cannot go to the arena right now. Finish what you are doing, stand with the host, and make sure the arena is installed (run the kotor-coop setup again)."
            : $"{otherName} cannot go to the arena right now. They have to be in the host's area, not in a conversation, and have the arena installed.",
        PvpNoticeCode.BossHostOnly =>
            $"Only {otherName}, the host, can call for Chungus here. The host can let everyone do it with pvp = arena.",
        PvpNoticeCode.BossCountdown =>
            "Chungus arrives in 5. Nobody can die in this fight. Type yield at any time to stop.",
        PvpNoticeCode.BossStarted => "Fight! Beat Chungus before he beats all of you.",
        PvpNoticeCode.BossWon =>
            "Chungus is beaten. You are back to how you were before the fight. Nothing was lost.",
        PvpNoticeCode.BossLost =>
            "Chungus beat everyone. You are back to how you were before the fight. Nothing was lost.",
        PvpNoticeCode.BossCancelled => otherName.Length == 0 || otherName == "Someone"
            ? "The fight with Chungus was called off. Nothing was lost."
            : $"{otherName} called off the fight with Chungus. Nothing was lost.",
        PvpNoticeCode.BossTimedOut =>
            "The fight with Chungus went on too long, so it was called off. Nothing was lost.",
        _ => string.Empty,
    };
}
