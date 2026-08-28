namespace KotorCoop.Session;

/// <summary>
/// How a joining player gets a character into the host's world — coop.ini <c>join_mode</c>.
/// </summary>
public enum JoinMode
{
    /// <summary>
    /// THE DEFAULT, and exactly today's behaviour: the joiner already has a save of their own.
    /// Its PC is what gets carried, either straight (the two saves happen to be identical) or
    /// through world adoption (they are not, and <c>auto_adopt_world</c> is on).
    /// </summary>
    Save,

    /// <summary>
    /// The first-time joiner: no save, no character, nothing to carry. They go through the
    /// retail New Game → character-creation screens, and the character chargen produces is
    /// carried into the host's world instead of into the Endar Spire's own story start.
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

    /// <summary>Send the player through retail chargen first, keep the character it writes to
    /// <c>TEMP:pifo</c>, and adopt the host's world carrying THAT.</summary>
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
/// WHY THIS IS A RULE. The three inputs interact in ways that are easy to get subtly wrong and
/// impossible to see from a stack trace: a joiner with no save at all used to be handed to the
/// ordinary "load slot 0" path, which fails at the file system and leaves them at the menu with
/// a diagnostic about a missing SAVEGAME.sav rather than an offer to make a character; and
/// <c>join_mode = newcharacter</c> has to win over an existing save, because a player who asked
/// for a new character means it. Pure and separately tested for the same reason every other
/// <c>*Rule</c> here is: the live path costs two full module loads to exercise.
/// </summary>
public static class JoinModeRule
{
    /// <summary>The coop.ini value that selects <see cref="JoinMode.NewCharacter"/>. Anything
    /// else — including the key being absent, empty or misspelled — is
    /// <see cref="JoinMode.Save"/>, because the default has to be the behaviour that already
    /// shipped.</summary>
    public const string NewCharacterKeyword = "newcharacter";

    /// <summary>The coop.ini value that selects <see cref="JoinMode.Save"/>, accepted
    /// explicitly so a player can write down the default rather than deleting the line.
    /// </summary>
    public const string SaveKeyword = "save";

    /// <summary>
    /// Parses coop.ini's <c>join_mode</c>. Tolerant on purpose — a typo must degrade to today's
    /// behaviour, never to a chargen the player did not ask for. <c>new_character</c>,
    /// <c>new-character</c> and <c>new character</c> are all accepted as the same word, because
    /// all three are what people actually type.
    /// </summary>
    public static JoinMode Parse(string? value)
    {
        if (value is null)
        {
            return JoinMode.Save;
        }

        string normalised = value.Trim().Replace("_", string.Empty)
            .Replace("-", string.Empty).Replace(" ", string.Empty);

        return normalised.Equals(NewCharacterKeyword, StringComparison.OrdinalIgnoreCase)
            ? JoinMode.NewCharacter
            : JoinMode.Save;
    }

    /// <summary>The coop.ini spelling of a mode, for logs and for round-tripping a written
    /// file.</summary>
    public static string Format(JoinMode mode) =>
        mode == JoinMode.NewCharacter ? NewCharacterKeyword : SaveKeyword;

    /// <summary>
    /// What the joiner should do.
    /// </summary>
    /// <param name="mode">coop.ini <c>join_mode</c>.</param>
    /// <param name="hasOwnCharacter">Whether this install has a save with a PC in it to carry
    /// (<c>PlayerIfo.TryExtractPlayer</c> would succeed), or an equivalent session-saved
    /// sidecar.</param>
    /// <param name="saveIdentityMatchesHost">Whether this install's world already fingerprints
    /// the same as the host's. False when unknown — the caller cannot know it at the main menu,
    /// and "unknown" must not be treated as "fine".</param>
    /// <param name="autoAdoptEnabled">coop.ini <c>auto_adopt_world</c>, ON by default.</param>
    /// <param name="reason">A one-line explanation, for the console and the log.</param>
    public static JoinAction Decide(
        JoinMode mode,
        bool hasOwnCharacter,
        bool saveIdentityMatchesHost,
        bool autoAdoptEnabled,
        out string reason)
    {
        // The explicit ask wins over everything, including an existing save. A player who set
        // join_mode = newcharacter wants a new character; silently loading yesterday's would be
        // the mod overruling them.
        if (mode == JoinMode.NewCharacter)
        {
            reason = "join_mode = newcharacter: creating a character, then adopting the host's world";
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

        // The first-time joiner who never set join_mode. Falling through to "load slot 0" here
        // is what used to fail at the file system; making a character is the only thing that
        // can actually work, so offer it rather than an error.
        reason = "no character on this install: creating one, then adopting the host's world";
        return JoinAction.CreateCharacterThenAdopt;
    }
}
