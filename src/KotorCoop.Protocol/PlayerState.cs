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
    uint Flags = 0)
{
    /// <summary>The player is in a conversation. Enemy publication freezes for them, and
    /// peers' UIs can say "talking" instead of showing a statue.</summary>
    public const uint FlagInDialogue = 1;

    /// <summary>The player has the game paused (their own pause, not one mirrored from a
    /// peer — mirrored pauses are excluded at the source or two instances would latch
    /// each other paused forever).</summary>
    public const uint FlagPaused = 2;

    /// <summary>The player's character is in combat (creature +0x4E0).</summary>
    public const uint FlagInCombat = 4;

    public static PlayerState Empty { get; } = new(Vector3f.Zero, 0f, string.Empty, 0, 0);

    /// <summary>Rejects NaN/infinite reads, which is what a bad offset profile produces.</summary>
    public bool IsPlausible => Position.IsFinite && float.IsFinite(Facing);

    public bool IsInDialogue => (Flags & FlagInDialogue) != 0;

    public bool IsPaused => (Flags & FlagPaused) != 0;

    public bool IsInCombat => (Flags & FlagInCombat) != 0;
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
