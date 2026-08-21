namespace KotorCoop.Memory;

/// <summary>
/// One non-damage effect <c>PhantomEffectHook</c> caught landing on a peer's phantom — stun,
/// paralyze, knockdown, slow, frightened, sleep, choke, or Force push/whirlwind. See
/// docs/research/effect-sync.md for the generic dispatcher this rides on and why the wire
/// carries a small numeric <see cref="Kind"/> rather than the engine's own effect type and
/// sub-code directly.
///
/// <see cref="PhantomId"/> is THIS instance's local object id for the phantom that was hit —
/// resolved to a peer id (through the same phantom-slot table <c>PhantomHitHook</c> uses)
/// before this ever reaches the wire. <see cref="Serial"/> makes a repeat of the same effect
/// (a second Stun landing seconds later) visible as a new event rather than a duplicate to be
/// swallowed.
/// </summary>
public readonly record struct PeerEffectRecord(
    uint PhantomId, uint Kind, uint DurationMs, uint Magnitude, uint Serial);
