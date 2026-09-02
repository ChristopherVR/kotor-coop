namespace KotorCoop.Session;

/// <summary>
/// What a raw party-gold READING means before <see cref="Pumps.PartyPump"/>'s ledger is allowed
/// to see it, separated so it is unit-tested — the same split <see cref="HostLossRule"/> uses.
///
/// The engine's gold is a plain <c>uint</c> in the party table, and <c>CSWSCreature::SetGold</c>
/// clamps it at <see cref="Max"/> (999,999,999) on the way UP only: a caller that hands it a
/// negative total — <c>CSWGuiStore::BuyItem</c> computing <c>cache - price</c> against a stale
/// cached gold, the case docs/research/store-sync.md's 2026-09-03 live pass reproduced — stores
/// the two's-complement wrap, and the table reads back as just under <c>UINT32_MAX</c>.
///
/// Fed to the ledger raw, that wrap became "the party earned four billion" on the host and,
/// mirrored through the clamped write on the client, a four-billion "spend" report back — and
/// both purses settled at 999,999,999 forever. A reading above <see cref="Max"/> is therefore
/// not a balance the engine can legitimately hold; it is a wrapped overdraft, and the only sane
/// balance for an overdrawn party is zero.
/// </summary>
public static class GoldReadingRule
{
    /// <summary>The engine's own ceiling — <c>SetGold</c> clamps here (0x3B9AC9FF).</summary>
    public const uint Max = 999_999_999;

    /// <summary>True when <paramref name="raw"/> is a value <c>SetGold</c> could never have
    /// produced through its clamp: a negative total that wrapped.</summary>
    public static bool IsWrapped(uint raw) => raw > Max;

    /// <summary>The balance the ledger should work from: the reading itself, or 0 for a wrap
    /// (the party spent more than it had; it now has nothing).</summary>
    public static uint Sanitise(uint raw) => IsWrapped(raw) ? 0 : raw;
}
