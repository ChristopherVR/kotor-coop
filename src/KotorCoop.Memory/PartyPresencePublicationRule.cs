namespace KotorCoop.Memory;

/// <summary>
/// The publication protocol for one agent-written party-presence slot.
/// The word at slot+0x24 is a sequence lock: an odd value means that the agent is
/// publishing the preceding payload, and an unchanged even value before and after a
/// read means the payload was not torn.
/// </summary>
internal static class PartyPresencePublicationRule
{
    /// <summary>Gets the first odd generation for a write following a stable generation.</summary>
    public static uint BeginWrite(uint lastPublishedGeneration)
    {
        // The normal input is a completed (even) generation, but an interrupted writer can
        // leave an odd marker behind; advance to the next distinct odd marker in either case.
        // Checked arithmetic is deliberately avoided: FFFFFFFE -> FFFFFFFF -> 0 is a valid
        // wrap and 0 is a stable value just like every other even generation.
        uint odd = unchecked(lastPublishedGeneration + 1u);
        return (odd & 1u) == 0 ? unchecked(odd + 1u) : odd;
    }

    /// <summary>Gets the stable generation that completes <paramref name="oddGeneration"/>.</summary>
    public static uint CompleteWrite(uint oddGeneration) => unchecked(oddGeneration + 1u);

    public static bool IsStable(uint generation) => (generation & 1u) == 0;

    /// <summary>Accepts a payload only when both sequence samples identify the same stable write.</summary>
    public static bool IsStableRead(uint before, uint after) =>
        IsStable(before) && before == after;
}
