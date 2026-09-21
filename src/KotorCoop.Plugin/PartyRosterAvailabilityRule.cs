namespace KotorCoop.Plugin;

/// <summary>
/// Pure mask decisions for the recruited/available half of the party roster channel.
///
/// The wire roster has one availability bit for each of K1's nine fixed NPC slots. A client
/// can therefore converge a host-confirmed falling edge by calling the engine's verified
/// <c>RemoveAvailableNPC</c> VM command. This rule keeps that decision bounded to real slots
/// and lets the native wrapper remain outside the test runner; an initial host zero is not an
/// edge.
/// </summary>
internal static class PartyRosterAvailabilityRule
{
    public const int RosterSlots = 9;
    public const uint RosterMask = (1u << RosterSlots) - 1u;
    public const uint UnknownMask = uint.MaxValue;

    /// <summary>
    /// Availability bits present locally but cleared by the authoritative host. A slot the host
    /// still reports in its active party is retained until the host's two masks settle; clearing
    /// it locally while its active seat is still authoritative would create an invalid table.
    /// </summary>
    public static uint PendingRemovals(
        uint localAvailable,
        uint hostAvailable,
        uint hostParty = 0) =>
        (localAvailable & ~hostAvailable & ~hostParty) & RosterMask;

    /// <summary>Bits that became available in the local engine since the previous stable read.</summary>
    public static uint NewlyAvailable(uint previousLocalAvailable, uint currentLocalAvailable) =>
        (currentLocalAvailable & ~previousLocalAvailable) & RosterMask;

    /// <summary>
    /// Detects a host-confirmed availability falling edge. An initial host snapshot of zero is
    /// deliberately not a removal: it may simply be the first level read before this client has
    /// learned the host's recruited companions.
    /// </summary>
    public static uint ConfirmedHostRemovals(uint previousHostAvailable, uint currentHostAvailable) =>
        previousHostAvailable == UnknownMask
            ? 0
            : (previousHostAvailable & ~currentHostAvailable) & RosterMask;

    /// <summary>
    /// Carries confirmed falling edges while cancelling a slot the host has re-added. The caller
    /// keeps the result until the local availability bit is cleared through the engine routine.
    /// </summary>
    public static uint UpdateHostRemovalEdges(
        uint priorEdges,
        uint previousHostAvailable,
        uint currentHostAvailable) =>
        (priorEdges | ConfirmedHostRemovals(previousHostAvailable, currentHostAvailable))
            & ~currentHostAvailable
            & RosterMask;

    /// <summary>Returns the first slot in a pending mask, or -1 when it is empty.</summary>
    public static int LowestPendingSlot(uint pendingMask)
    {
        pendingMask &= RosterMask;
        for (int slot = 0; slot < RosterSlots; slot++)
        {
            if ((pendingMask & (1u << slot)) != 0)
            {
                return slot;
            }
        }

        return -1;
    }
}
