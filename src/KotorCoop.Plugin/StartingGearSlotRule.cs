namespace KotorCoop.Plugin;

/// <summary>
/// Pure "which equip slot does this blueprint belong in" rule, factored out of
/// <see cref="Odyssey.EquipUnequippedStartingGear"/> so the classification itself can be
/// tested without a live game (the same split this project already applies to
/// <see cref="EquipApplyDecision"/> and <see cref="EquipSlotMap"/>).
///
/// WHY THIS EXISTS: a first-time joiner's `join_mode = newcharacter` chargen leaves its
/// starting gear sitting UNEQUIPPED in the fresh character's own item repository at the
/// moment <c>NewCharacterCaptureController</c> quicksaves it for carrying — see that
/// controller's own doc comment and docs/research/world-adoption.md's 2026-09-10 addendum.
/// The repair walks that repository and needs to decide, per item, which slot (if any) it
/// belongs in, WITHOUT a live baseitem-type read (this project has no verified offset for
/// <c>CSWSItem</c>'s BaseItem field — inventing one blind and shipping an unverified engine
/// write against it is exactly what docs/ARCHITECTURE.md's engine-write rules warn against).
///
/// Uses the SAME resref-prefix convention every equip decision already logged in this
/// project assumes for a stock blueprint (its Tag IS its resref, upper-cased —
/// docs/research/item-identity.md §8.2): "g_a_" is armor/clothing (worn in the BODY slot —
/// every "equip: phantom X wears g_a_clothes01 (slot 1)" line this project has ever logged
/// is exactly this), "g_w_" is a weapon (the RIGHT hand — this project has never needed to
/// equip a fresh character's OFF hand, so left-hand classification is deliberately not
/// attempted here). A tag this does not recognise (a custom mod's item, a misc/quest item a
/// class package might also grant) is left alone rather than guessed at — the safe default,
/// since the direct primitive that would carry out the equip
/// (<see cref="Odyssey.EquipItemFn"/>) never itself validates the slot.
/// </summary>
public static class StartingGearSlotRule
{
    /// <summary>NWScript slot ids, mirroring <c>Odyssey.InventorySlotBody</c>/
    /// <c>InventorySlotRightWeapon</c> without depending on the engine-facing half of the
    /// plugin (this file compiles into <c>KotorCoop.Tests</c> the same way
    /// <see cref="EquipSlotMap"/> does).</summary>
    public const int SlotBody = 1;

    public const int SlotRightWeapon = 4;

    /// <summary>The NWScript slot id <paramref name="tag"/>'s blueprint belongs in, or -1 when
    /// the prefix is not one this rule recognises.</summary>
    public static int Classify(string tag)
    {
        if (tag.Length < 4)
        {
            return -1;
        }

        string prefix = tag[..4].ToLowerInvariant();
        return prefix switch
        {
            "g_a_" => SlotBody,
            "g_w_" => SlotRightWeapon,
            _ => -1,
        };
    }
}
