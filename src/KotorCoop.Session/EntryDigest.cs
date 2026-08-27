using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// A cheap, order-independent 64-bit content fingerprint for a key/value store.
///
/// WHY THIS EXISTS. Every world-progression store (<see cref="EventFlagStore"/>,
/// <see cref="JournalStore"/>, <see cref="GroundItemStore"/>, <see cref="ContainerLootStore"/>)
/// already carries a <c>Version</c> counter as its divergence tripwire, and the counter has a
/// blind spot: it counts LOCAL changes, not content. Two stores that diverged during an outage
/// — one lost an op, the other applied a different one — can end up with the exact same change
/// COUNT while holding different values, and a tie never asks for a resync. See
/// docs/research/disconnect-reconnect.md §4.1.
///
/// The fix is a fingerprint that actually depends on the content. XOR-summing a per-entry
/// FNV-1a hash makes it ORDER-INDEPENDENT (the two peers never agree on insertion order) and
/// lets every store maintain it INCREMENTALLY in O(1) per change: XOR is its own inverse, so
/// "this entry changed from A to B" is exactly <c>digest ^= Hash(key, A); digest ^= Hash(key,
/// B);</c> — no store ever needs to rehash its whole content to keep the digest current.
///
/// Carried beside the counter on the wire (same field width cost the doc's §4.1 already
/// budgeted for: 8 bytes, not a protocol redesign), so a mismatch is judged as
/// <c>version != version || digest != digest</c> — the counter still catches the common case
/// cheaply by eye, and the digest catches the tie the counter cannot.
/// </summary>
public static class EntryDigest
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    /// <summary>The digest of an empty store. XOR-identity, so removing every entry one at a
    /// time by XOR-ing out its own hash always lands back here.</summary>
    public const ulong Empty = 0UL;

    private static ulong MixByte(ulong h, byte b) => (h ^ b) * Prime;

    private static ulong MixUInt32(ulong h, uint value)
    {
        h = MixByte(h, (byte)value);
        h = MixByte(h, (byte)(value >> 8));
        h = MixByte(h, (byte)(value >> 16));
        h = MixByte(h, (byte)(value >> 24));
        return h;
    }

    /// <summary>Mixes a string case-INsensitively (each char folded to uppercase before
    /// hashing), for callers whose store key or value compares that way — journal quest tags,
    /// which <see cref="JournalStore"/> keys with <see cref="StringComparer.OrdinalIgnoreCase"/>.
    /// Two peers naming the same tag in different casing must hash to the same contribution or
    /// the digest would disagree about a store the two sides actually agree on.</summary>
    private static ulong MixStringIgnoreCase(ulong h, string value)
    {
        foreach (char c in value)
        {
            char upper = char.ToUpperInvariant(c);
            h = MixByte(h, (byte)upper);
            h = MixByte(h, (byte)(upper >> 8));
        }

        return h;
    }

    /// <summary>One flag: id plus its signed byte value.</summary>
    public static ulong Flag(uint flagId, sbyte value)
    {
        ulong h = MixUInt32(Offset, flagId);
        return MixByte(h, unchecked((byte)value));
    }

    /// <summary>One journal entry: tag (case-folded) plus its stage.</summary>
    public static ulong Journal(string questTag, int state)
    {
        ulong h = MixStringIgnoreCase(Offset, questTag);
        return MixUInt32(h, unchecked((uint)state));
    }

    /// <summary>
    /// One ground item: id plus everything that makes it THIS item rather than a fresh one off
    /// the blueprint — resref and the identity fields <see cref="Protocol.ItemIdentity"/> carries.
    /// Position and module are deliberately excluded: they are fixed once at drop time by a
    /// single reliable op, so they never diverge independently of the id, and keeping floats out
    /// of the hash avoids a digest that can never quite agree over a lossy round trip.
    /// </summary>
    public static ulong GroundItem(uint itemId, string resref, ItemIdentity identity)
    {
        ulong h = MixUInt32(Offset, itemId);
        h = MixStringIgnoreCase(h, resref);
        h = MixUInt32(h, identity.StackSize);
        h = MixByte(h, identity.Charges);
        h = MixByte(h, identity.MaxCharges);
        h = MixUInt32(h, identity.Upgrades);
        h = MixByte(h, identity.Flags);
        return h;
    }

    /// <summary>One container-loot removal: (container, item) pair.</summary>
    public static ulong ContainerLootRemoval(uint containerId, uint itemId)
    {
        ulong h = MixUInt32(Offset, containerId);
        return MixUInt32(h, itemId);
    }

    /// <summary>One party-pool stack: the content key plus its current aggregate quantity.
    /// Unlike <see cref="GroundItem"/> there is no id to mix in — the key IS the identity, and
    /// two peers that agree on "resref X, this identity, N units" agree completely.</summary>
    public static ulong PartyPoolEntry(Protocol.PartyPoolStackKey key, int count)
    {
        ulong h = MixStringIgnoreCase(Offset, key.Resref);
        h = MixByte(h, key.Charges);
        h = MixByte(h, key.MaxCharges);
        h = MixUInt32(h, key.Upgrades);
        h = MixByte(h, key.Flags);
        return MixUInt32(h, unchecked((uint)count));
    }

    /// <summary>One store stock entry: the store's object id, the content key, plus its current
    /// aggregate quantity — <see cref="PartyPoolEntry"/> widened by the store id, for the
    /// identical reason <see cref="Protocol.StoreStackKey"/> widens
    /// <see cref="Protocol.PartyPoolStackKey"/> (docs/research/store-sync.md).</summary>
    public static ulong StoreEntry(Protocol.StoreStackKey key, int count)
    {
        ulong h = MixUInt32(Offset, key.StoreObjectId);
        h = MixStringIgnoreCase(h, key.Resref);
        h = MixByte(h, key.Charges);
        h = MixByte(h, key.MaxCharges);
        h = MixUInt32(h, key.Upgrades);
        h = MixByte(h, key.Flags);
        return MixUInt32(h, unchecked((uint)count));
    }

    /// <summary>One creature-liveness entry: id plus its reported state. XOR-summed over the
    /// WHOLE publish cycle's entries (every page, before pagination splits them across
    /// datagrams) by <c>CreatureLivenessPaging.Paginate</c>, so every page of one cycle
    /// carries the same digest.</summary>
    public static ulong CreatureLiveness(uint objectId, CreatureLivenessState state)
    {
        ulong h = MixUInt32(Offset, objectId);
        return MixByte(h, (byte)state);
    }
}
