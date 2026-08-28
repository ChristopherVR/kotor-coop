// PartyPoolIdentityMatcher.cs - the pure charge-clamp/identity-match rules
// Odyssey.PartyPool.cs's RemovePartyPoolStack uses. Extracted here, free of unsafe
// pointers/SharedBlockView, matching PartyPoolStackMatcher.cs's own precedent, so both get a
// place to be tested directly rather than trusted to eyes on the pointer-walking code around
// them. See docs/research/party-pool-sync.md §20.
namespace KotorCoop.Plugin;

/// <summary>
/// Decides whether a live pool item's non-stack identity (charges/max charges/upgrades/the
/// identified-stolen-non-equippable flag bits) satisfies a removal request's own key — and, on
/// the way there, repairs the one field an ambient-script deposit can genuinely make
/// unreadable: MaxCharges.
///
/// Stack size/count is DELIBERATELY not a parameter anywhere in this class: it is a quantity
/// the caller subtracts from, never part of what makes two stacks "the same" — matching
/// <c>PartyPoolStackKey</c>'s own shape (resref plus these four fields, no count) and the design
/// note that a stack's count is the multiset's VALUE, never part of its KEY
/// (docs/research/party-pool-sync.md §2.1).
/// </summary>
public static class PartyPoolIdentityMatcher
{
    /// <summary>
    /// "Match any value for this field" — the SAME value as <see cref="SharedBlock.CheatFieldUnchanged"/>
    /// (declared again here, not referenced, only because <c>SharedBlock.Cheats.cs</c> is a
    /// <c>partial struct</c> member list and this class stays a plain static utility outside
    /// it), reused as a wildcard rather than a second, differently-named sentinel. A human's own
    /// bare <c>poolremove &lt;resref&gt;</c> has no charges/upgrades/flags to give, and coercing
    /// that to a literal 0 (the pre-§20 behaviour) demanded an exact match against whatever an
    /// ambient script's own deposit actually carries — almost never 0
    /// (docs/research/party-pool-sync.md §19.2's own live "identity-key mismatch").
    /// </summary>
    public const uint Wildcard = SharedBlock.CheatFieldUnchanged;

    /// <summary>
    /// Charges/MaxCharges as the ENGINE'S OWN "what a save/load round trip would preserve" rule
    /// sees them (docs/research/item-identity.md §1.1 and its "oddity" note): both fields are
    /// persisted as GFF BYTES, so a raw value above 255 was never a real charge count — it is
    /// uninitialised memory the game happened to leave behind (measured live: "charges
    /// 50/1768092" on a body whose template never fully loaded). MaxCharges falls back to
    /// Charges when it cannot be trusted, the same default the engine's own loader applies when
    /// a <c>.uti</c> declares no MaxCharges field at all (true of every base-game blueprint).
    ///
    /// This is the EXACT clamp <c>KotorCoop.Memory.ItemIdentityLayout.Decode</c> already applies
    /// on the scan side that builds a <c>PartyPoolStackKey</c> for the wire — duplicated here
    /// (the plugin cannot reference that assembly: NativeAOT win-x86 vs the agent's x64) so a
    /// removal derives the IDENTICAL key for the SAME live item a scan would have reported.
    /// Without this, a wire-driven remove (whose key came from a scan that safely clamped a
    /// garbage MaxCharges down to the Charges value) could never match the live item again,
    /// because a fresh read of that same item would still show the raw garbage.
    /// </summary>
    public static (uint Charges, uint MaxCharges) SafeChargeFields(uint rawCharges, uint rawMaxCharges)
    {
        uint safeCharges = rawCharges <= byte.MaxValue ? rawCharges : 0;
        uint safeMaxCharges = rawMaxCharges <= byte.MaxValue ? rawMaxCharges : safeCharges;
        return (safeCharges, safeMaxCharges);
    }

    /// <summary>
    /// True when a live item's (already safe-clamped) Charges/MaxCharges plus its raw Upgrades
    /// mask and wire-shape identity flags satisfy a removal request's own fields. Each
    /// REQUESTED field equal to <see cref="Wildcard"/> matches unconditionally; every other
    /// field needs an exact match. <paramref name="itemWireFlags"/>/<paramref name="requestedWireFlags"/>
    /// are both expected already masked to the three identity bits
    /// (<c>SharedBlock.ItemIdentityIdentified</c>/<c>Stolen</c>/<c>NonEquippable</c>) — this
    /// method does no engine-bit translation of its own, staying free of any
    /// <c>SharedBlock</c>/engine-offset dependency.
    /// </summary>
    public static bool Matches(
        uint itemCharges, uint itemMaxCharges, uint itemUpgrades, uint itemWireFlags,
        uint requestedCharges, uint requestedMaxCharges, uint requestedUpgrades, uint requestedWireFlags)
    {
        if (requestedCharges != Wildcard && itemCharges != requestedCharges)
        {
            return false;
        }

        if (requestedMaxCharges != Wildcard && itemMaxCharges != requestedMaxCharges)
        {
            return false;
        }

        if (requestedUpgrades != Wildcard && itemUpgrades != requestedUpgrades)
        {
            return false;
        }

        if (requestedWireFlags != Wildcard && itemWireFlags != requestedWireFlags)
        {
            return false;
        }

        return true;
    }
}
