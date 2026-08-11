namespace KotorCoop.Protocol;

/// <summary>
/// The slice of a player's avatar that is replicated every tick. This is deliberately
/// small: it is the "phantom body" layer, equivalent to what FromSoftware's netcode
/// already replicates for summoned phantoms. World progression is a separate channel
/// (see <see cref="FlagOpPacket"/>).
/// </summary>
public readonly record struct PlayerState(
    Vector3f Position,
    float Facing,
    string Module,
    int Health,
    uint Animation,
    uint Appearance = 0,
    int MaxHealth = 0,
    string EquippedBody = "",
    string EquippedRightWeapon = "",
    string EquippedLeftWeapon = "",
    uint Flags = 0,
    ushort OneShotAnimation = 0,
    byte OneShotSequence = 0)
{
    // ⚠ <see cref="OneShotAnimation"/> is an EDGE, not a level. Weapon draws, attack swings,
    // flourishes and item use are fire-and-forget: they finish between two ticks and some
    // never touch an object field at all, so they cannot be sampled — the plugin captures
    // them by hooking the engine's dispatch and carries a sequence alongside, which is what
    // makes a repeated identical flourish replicate instead of being swallowed as "no
    // change". See docs/research/animation-and-minimap.md.

    /// <summary>The player is in a conversation. Enemy publication freezes for them, and
    /// peers' UIs can say "talking" instead of showing a statue.</summary>
    public const uint FlagInDialogue = 1;

    /// <summary>The player has the game paused (their own pause, not one mirrored from a
    /// peer — mirrored pauses are excluded at the source or two instances would latch
    /// each other paused forever).</summary>
    public const uint FlagPaused = 2;

    /// <summary>The player's character is in combat (creature +0x4E0).</summary>
    public const uint FlagInCombat = 4;

    /// <summary>
    /// The player is DOWNED — at zero hit points and in the engine's "temp dead" state,
    /// which party members enter instead of dying. They are not out of the session: the
    /// engine stands them up once the area is clear, and a peer who reaches them can revive
    /// them sooner. Rides the existing per-tick state, so no extra channel.
    /// </summary>
    public const uint FlagDowned = 8;

    /// <summary>
    /// This player has just RELEASED a shared pause they did not start.
    ///
    /// Needed because a release is not observable as the absence of <see cref="FlagPaused"/>:
    /// a mirroring player never had that flag set in the first place (mirrored pauses are
    /// excluded at the source), so "peer is not paused" is indistinguishable from "peer has
    /// unpaused us both". Without this bit only the ORIGINATOR could end a shared pause, and
    /// the other player pressing pause unfroze their own game while their partner stayed
    /// frozen — reported from play, and reproduced: instance 1 held its pause after instance
    /// 2 released.
    ///
    /// An EDGE published as a level for a short window, like the one-shot animations: a
    /// release that is lost is lost forever, while a couple of ticks of repetition converges.
    /// </summary>
    public const uint FlagPauseReleased = 16;

    /// <summary>True while this player is asking to end a shared pause they did not start.</summary>
    public bool HasReleasedPause => (Flags & FlagPauseReleased) != 0;

    public static PlayerState Empty { get; } = new(Vector3f.Zero, 0f, string.Empty, 0, 0);

    /// <summary>Rejects NaN/infinite reads, which is what a bad offset profile produces.</summary>
    public bool IsPlausible => Position.IsFinite && float.IsFinite(Facing);

    public bool IsInDialogue => (Flags & FlagInDialogue) != 0;

    public bool IsPaused => (Flags & FlagPaused) != 0;

    public bool IsInCombat => (Flags & FlagInCombat) != 0;

    public bool IsDowned => (Flags & FlagDowned) != 0;
}

/// <summary>A remote player as seen locally, after interpolation.</summary>
public sealed record RemotePeer(
    uint PeerId,
    string Name,
    PlayerState State)
{
    /// <summary>
    /// True when the peer is in the same module (area) as the local player. KOTOR loads
    /// one module at a time, so a peer elsewhere must not be drawn.
    /// </summary>
    public bool IsInModule(string module) =>
        string.Equals(State.Module, module, StringComparison.OrdinalIgnoreCase);
}
