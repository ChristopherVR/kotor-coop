namespace KotorCoop.Session;

/// <summary>
/// How a joining player gets a character into the host's world — coop.ini <c>join_mode</c>.
/// </summary>
public enum JoinMode
{
    /// <summary>
    /// THE DEFAULT since 2026-09-03: the mod owns the joiner's co-op character. The FIRST join
    /// on an install always goes through the game's own New Game / character-creation screens
    /// (there is no co-op character yet, whatever single-player saves happen to be on disk);
    /// the character that comes out is written to the <c>saves\kotorcoop-character.*</c>
    /// sidecar, and EVERY later join carries that sidecar character into the host's world —
    /// never a single-player save's PC. The sidecar is refreshed on every in-game save the
    /// client makes while connected, so it always holds the latest co-op progression.
    /// </summary>
    Auto,

    /// <summary>
    /// The pre-2026-09-03 behaviour, kept as an explicit opt-out: the joiner already has a save
    /// of their own. Its PC is what gets carried, either straight (the two saves happen to be
    /// identical) or through world adoption (they are not, and <c>auto_adopt_world</c> is on).
    /// </summary>
    Save,

    /// <summary>
    /// Force character creation on this join even when a co-op character already exists — the
    /// "reroll" switch. The player goes through the retail New Game → character-creation
    /// screens, the new character replaces the sidecar and is carried into the host's world
    /// instead of into the Endar Spire's own story start.
    /// </summary>
    NewCharacter,
}

/// <summary>What the join should actually DO at the point the client is about to enter a
/// world. One value per genuinely different code path, so the decision is testable without a
/// game.</summary>
public enum JoinAction
{
    /// <summary>Load the joiner's own save and play. The world is already the host's by
    /// construction (identical saves), or the player has opted out of adoption and accepts the
    /// degraded, object-id-keyed-channels-off session <c>ApplyWorldMismatch</c> hands them.
    /// </summary>
    LoadOwnSave,

    /// <summary>Fetch the host's world, stage it, and log in carrying the character lifted out
    /// of the joiner's OWN save — <c>WorldAdoption.TryAdoptAsync</c>, the `agent adopt`
    /// path.</summary>
    AdoptWithOwnCharacter,

    /// <summary>Fetch the host's world, stage it, and log in carrying the mod-owned co-op
    /// character from the <c>saves\kotorcoop-character.*</c> sidecar — regardless of what
    /// other saves the install has. <see cref="JoinMode.Auto"/>'s every-join-after-the-first
    /// path.</summary>
    AdoptWithCoopCharacter,

    /// <summary>Send the player through retail chargen first, keep the character it produces
    /// (the sidecar), and adopt the host's world carrying THAT.</summary>
    CreateCharacterThenAdopt,

    /// <summary>Nothing can be done: no character to carry and no way to make one. The joiner
    /// stays at the main menu with a reason, rather than being dropped into a world as a blank
    /// level-1 stranger.</summary>
    Refuse,
}

/// <summary>
/// Picks the join path from the config, what the joiner has on disk, and whether their world
/// already agrees with the host's.
///
/// WHY THIS IS A RULE. The inputs interact in ways that are easy to get subtly wrong and
/// impossible to see from a stack trace: a joiner with no save at all used to be handed to the
/// ordinary "load slot 0" path, which fails at the file system and leaves them at the menu with
/// a diagnostic about a missing SAVEGAME.sav rather than an offer to make a character;
/// <c>join_mode = newcharacter</c> has to win over an existing character, because a player who
/// asked for a new character means it; and in the default mode a single-player save on the
/// same install must NEVER be mistaken for the co-op character. Pure and separately tested for
/// the same reason every other <c>*Rule</c> here is: the live path costs two full module loads
/// to exercise.
/// </summary>
public static class JoinModeRule
{
    /// <summary>The coop.ini value that selects <see cref="JoinMode.NewCharacter"/>.</summary>
    public const string NewCharacterKeyword = "newcharacter";

    /// <summary>The coop.ini value that selects <see cref="JoinMode.Save"/> — the explicit
    /// opt-out back to "carry the PC of my own most recent save".</summary>
    public const string SaveKeyword = "save";

    /// <summary>The coop.ini value that selects <see cref="JoinMode.Auto"/>, accepted
    /// explicitly so a player can write down the default rather than deleting the line.
    /// Anything else — including the key being absent, empty or misspelled — is also
    /// <see cref="JoinMode.Auto"/>.</summary>
    public const string AutoKeyword = "auto";

    /// <summary>
    /// Parses coop.ini's <c>join_mode</c>. Tolerant on purpose — a typo must degrade to the
    /// default, never to a mode the player did not ask for. <c>new_character</c>,
    /// <c>new-character</c> and <c>new character</c> are all accepted as the same word, because
    /// all three are what people actually type.
    /// </summary>
    public static JoinMode Parse(string? value)
    {
        if (value is null)
        {
            return JoinMode.Auto;
        }

        string normalised = value.Trim().Replace("_", string.Empty)
            .Replace("-", string.Empty).Replace(" ", string.Empty);

        if (normalised.Equals(NewCharacterKeyword, StringComparison.OrdinalIgnoreCase))
        {
            return JoinMode.NewCharacter;
        }

        return normalised.Equals(SaveKeyword, StringComparison.OrdinalIgnoreCase)
            ? JoinMode.Save
            : JoinMode.Auto;
    }

    /// <summary>The coop.ini spelling of a mode, for logs and for round-tripping a written
    /// file.</summary>
    public static string Format(JoinMode mode) => mode switch
    {
        JoinMode.NewCharacter => NewCharacterKeyword,
        JoinMode.Save => SaveKeyword,
        _ => AutoKeyword,
    };

    /// <summary>
    /// What the joiner should do.
    /// </summary>
    /// <param name="mode">coop.ini <c>join_mode</c>.</param>
    /// <param name="hasCoopCharacter">Whether the mod-owned co-op character exists on this
    /// install (<c>ClientCharacterPersistence.TryReadSidecar</c> succeeds).</param>
    /// <param name="hasOwnCharacter">Whether this install has a save with a PC in it to carry
    /// (<c>PlayerIfo.TryExtractPlayer</c> would succeed on its most recent save). Only
    /// consulted in <see cref="JoinMode.Save"/> — in the default mode a single-player save is
    /// deliberately NOT a character to join with.</param>
    /// <param name="saveIdentityMatchesHost">Whether this install's world already fingerprints
    /// the same as the host's. False when unknown — the caller cannot know it at the main menu,
    /// and "unknown" must not be treated as "fine".</param>
    /// <param name="autoAdoptEnabled">coop.ini <c>auto_adopt_world</c>, ON by default.</param>
    /// <param name="reason">A one-line explanation, for the console and the log.</param>
    public static JoinAction Decide(
        JoinMode mode,
        bool hasCoopCharacter,
        bool hasOwnCharacter,
        bool saveIdentityMatchesHost,
        bool autoAdoptEnabled,
        out string reason)
    {
        // The explicit ask wins over everything, including an existing character of either
        // kind. A player who set join_mode = newcharacter wants a new character; silently
        // loading yesterday's would be the mod overruling them.
        if (mode == JoinMode.NewCharacter)
        {
            reason = "join_mode = newcharacter: creating a character, then adopting the host's world";
            return JoinAction.CreateCharacterThenAdopt;
        }

        // The default. Adoption is the only mechanism that carries a character into another
        // install's world, so with auto_adopt_world off the mode cannot do its job at all and
        // degrades to the pre-adoption behaviour below rather than to a refusal — the player
        // who turned adoption off asked for exactly that behaviour.
        if (mode == JoinMode.Auto && autoAdoptEnabled)
        {
            if (hasCoopCharacter)
            {
                reason = "carrying this install's co-op character (saves\\kotorcoop-character.pifo) into the host's world";
                return JoinAction.AdoptWithCoopCharacter;
            }

            reason = "no co-op character on this install yet: creating one, then adopting the host's world";
            return JoinAction.CreateCharacterThenAdopt;
        }

        if (saveIdentityMatchesHost)
        {
            reason = "the save already matches the host's world";
            return JoinAction.LoadOwnSave;
        }

        if (!autoAdoptEnabled)
        {
            if (!hasOwnCharacter)
            {
                reason = "no save to load and auto_adopt_world is off - nothing to join with";
                return JoinAction.Refuse;
            }

            reason = "auto_adopt_world is off: loading this install's own save "
                + "(object-id-keyed channels will be off while the worlds differ)";
            return JoinAction.LoadOwnSave;
        }

        if (hasOwnCharacter)
        {
            reason = "the worlds differ: adopting the host's world, carrying this save's character";
            return JoinAction.AdoptWithOwnCharacter;
        }

        // join_mode = save on an install with nothing to load. Falling through to "load slot
        // 0" here is what used to fail at the file system; making a character is the only
        // thing that can actually work, so offer it rather than an error.
        reason = "no character on this install: creating one, then adopting the host's world";
        return JoinAction.CreateCharacterThenAdopt;
    }
}
