namespace KotorCoop.Protocol;

/// <summary>
/// How the companion-damage channel carries HEALS (2026-09-18, docs/review-seamless-2026-09-18.md
/// §2 "medpacs a joiner applies to a companion are wasted") without a packet or block change:
/// the roster-slot field of a <see cref="CompanionDamageEntry"/> is a byte on the wire and a
/// u32 in the shared block, and a roster slot (plus one) is 1..9, so bit 7 is free. A row whose
/// slot field carries <see cref="HealFlag"/> is a cumulative HEAL total for that roster slot —
/// the same "cumulative, never a level" shape as damage, banked by the same rise-only ledger
/// under its own key, and spent by the host through the engine's own <c>EffectHeal</c>.
///
/// Everything between the plugin's watch and the host's apply passes the field through
/// unchanged (<c>KotorGameBridge.ReadCompanionDamageTaken</c>, <c>PacketCodec</c>,
/// <c>CoopHost.HandleCompanionDamage</c>, <c>CompanionDamageLedger.Merge</c>,
/// <c>TryPublishCompanionDamageOwed</c>); only the two ends decode it.
/// </summary>
public static class CompanionDamageSlots
{
    /// <summary>Bit 7 of the slot field: this row is a heal total, not a damage total.</summary>
    public const uint HealFlag = 0x80;

    /// <summary>The slot field for a heal row against one roster slot (plus one).</summary>
    public static uint HealSlot(uint rosterSlotPlusOne) => rosterSlotPlusOne | HealFlag;

    public static bool IsHeal(uint slotField) => (slotField & HealFlag) != 0;

    /// <summary>The roster slot (plus one) a slot field names, heal or damage.</summary>
    public static uint RosterSlotPlusOne(uint slotField) => slotField & 0x7F;
}
