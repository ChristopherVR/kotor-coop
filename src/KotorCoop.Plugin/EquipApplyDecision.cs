namespace KotorCoop.Plugin;

/// <summary>
/// Pure "should this equip slot be (re)applied" decision, pulled out of
/// <c>PartyPresenceController.ApplyCompanionEquipment</c> so the rule itself — skip an empty
/// desired value, skip a value already applied, apply everything else — can be tested without
/// <c>SharedBlockView</c> or <c>Odyssey</c>. Every equip channel in this project (the phantom's
/// own <c>PhantomController.ApplyEquipment</c> included) shares the same shape; this is the
/// companion channel's copy of it, factored out because <c>docs/CONTRIBUTING.md</c> asks for a
/// pure rule wherever the shape repeats.
///
/// Both controllers hash the raw 16-byte resref buffer read out of the shared block rather
/// than compare managed strings — there is no managed string on that side of the process
/// boundary — so this operates on the hash already computed and the length already counted,
/// not on the bytes themselves.
/// </summary>
public static class EquipApplyDecision
{
    /// <summary>
    /// True when a nonempty desired resref differs from what was last applied to this slot.
    /// <paramref name="length"/> is the desired resref's length (0 = empty, unreadable on the
    /// sender, or the sender's <c>companion_gear_sync</c>/equivalent flag is off) — the "leave
    /// it alone" convention every equip channel in this project shares, so an absent value is
    /// never treated as "apply nothing".
    /// </summary>
    public static bool ShouldApply(int length, uint desiredHash, uint lastAppliedHash) =>
        length > 0 && desiredHash != lastAppliedHash;
}
