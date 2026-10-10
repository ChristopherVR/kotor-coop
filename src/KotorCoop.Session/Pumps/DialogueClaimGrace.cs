namespace KotorCoop.Session.Pumps;

/// <summary>
/// How long a joiner waits before claiming a conversation that has just started in its own game.
///
/// A scene's script starts its conversation in every game at once, and the token goes to the
/// first claim the host hears. On the Star Forge (2026-10-10, the K1 Full mod build) the joiner's
/// engine reached Carth's confrontation 0.3-0.4 s before the host's in two runs out of three; the
/// joiner took the token, the host became a follower, and the host's choices were dragged back to
/// the joiner's line, so the scene looped on its first reply. The host runs the world, so its copy
/// of a world scene is the one everyone should follow. Holding a joiner's NEW claim for a moment
/// lets the host's claim land first when the host's world started the same conversation; the
/// joiner then adopts its own copy as a follow, the path a joined conversation already takes.
///
/// Nothing else changes for the joiner: its conversation runs locally from the first frame, and a
/// conversation it clicked into is simply claimed a second later. The host never waits, and a
/// joiner that already holds the token keeps claiming it without a pause.
/// </summary>
public sealed class DialogueClaimGrace
{
    /// <summary>Comfortably more than the 0.3-0.4 s the two games' starts were measured apart
    /// for an ordinary conversation, and short against a conversation's first line.</summary>
    public const long GraceMs = 1000;

    /// <summary>For a conversation a script forced on the player. The joiner's game, loading the
    /// same module faster, reached the Star Forge confrontation up to 2 s ahead of the host's.
    /// A forced conversation can never be one the joiner clicked into (a click cannot set the
    /// flag; see DialogueForceFlagHook), so waiting longer here cannot cost a click race.</summary>
    public const long ForcedGraceMs = 5000;

    private long? _startedAtMs;

    /// <summary>
    /// Whether this tick's local conversation may be published as a claim.
    /// <paramref name="localActive"/> is the debounced "we are in a conversation of our own";
    /// <paramref name="weHoldToken"/> is whether the session's token is already ours.
    /// </summary>
    public bool AllowClaim(bool isWorldAuthority, bool localActive, bool weHoldToken, long nowMs, bool forced = false)
    {
        if (!localActive)
        {
            _startedAtMs = null;
            return false;
        }

        if (isWorldAuthority || weHoldToken)
        {
            return true;
        }

        _startedAtMs ??= nowMs;
        return nowMs - _startedAtMs.Value >= (forced ? ForcedGraceMs : GraceMs);
    }
}
