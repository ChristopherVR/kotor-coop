using System.Text;

namespace KotorCoop.Plugin;

// Item verbs: DropItem (ground spawn), GiveLoot (materialised-stand-in / dialogue-grant
// inventory spawn) and SetItem (per-instance identity read/poke), plus their shared
// describe/wire-flags helpers.
internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// Spawns a blueprint on the floor at the player's feet.
    ///
    /// On the floor rather than in the inventory ON PURPOSE. A ground item is already shared
    /// world state — the item channel publishes it, the peer's instance materialises a real
    /// clickable object, and it vanishes for both when either picks it up — so this is a
    /// "give the party an item" that obeys the replication rule the mod already has. The
    /// retail <c>giveitem</c> puts the item straight in the typist's pack, where no peer will
    /// ever see it; both are useful and they are different things.
    /// </summary>
    private void DropItem(nint player)
    {
        byte* text = block.CheatText;
        int length = 0;
        while (length < SharedBlock.CheatTextLength - 1 && text[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, "no blueprint given");
            return;
        }

        string resref = Encoding.ASCII.GetString(text, length);
        Vec3 position = Odyssey.GetPosition(player);

        uint itemId = Odyssey.SpawnItemOnFloor(text, length, position, out int failure);
        if (itemId is 0 or Odyssey.ObjectInvalid)
        {
            // The overwhelmingly likely cause, and worth naming: the blueprint is not in
            // this install. That is also the mod-mismatch symptom, so say which resref.
            Answer(
                SharedBlock.CheatStatusEngineRefused,
                $"'{resref}' did not spawn (failure {failure}; wrong resref?)");
            return;
        }

        // ⚠ The guard above cannot catch a wrong resref, so this one has to. CreateItemOnFloor
        // 0x00547F60 does not test LoadFromTemplate's result (0x0054806B), so a blueprint that
        // is not in this install still returns a perfectly good object id — and the old answer
        // "did not spawn (failure N; wrong resref?)" could therefore never be the one a typo
        // produced. What a typo produces is a nameless, empty body reported as a success.
        if (Odyssey.TryReadItemIdentityFields(itemId, out Odyssey.ItemIdentityFields spawned)
            && spawned.LooksUnloaded)
        {
            Answer(
                SharedBlock.CheatStatusEngineRefused,
                $"'{resref}' is not a blueprint here - 0x{itemId:X} spawned EMPTY");
            return;
        }

        Answer(SharedBlock.CheatStatusApplied, $"dropped '{resref}' as 0x{itemId:X}");
    }

    /// <summary>
    /// Gives an item to either of two targets, decided by <see cref="SharedBlock.CheatTarget"/>:
    /// a materialised enemy's local stand-in, one item of its real corpse's actual loot (the
    /// OWNER's enemy object id, translated locally — see
    /// <see cref="SharedBlock.CheatKindGiveLoot"/> for the full field layout); or, when the
    /// target is the <see cref="SharedBlock.CheatTargetLocalPlayer"/> sentinel, THIS instance's
    /// own player directly — the receiving half of the dialogue item-grant channel
    /// (docs/research/dialogue-consequences.md §3.4), which needs no translation at all.
    /// </summary>
    private void GiveLoot()
    {
        uint ownerEnemyId = block.CheatTarget;
        uint localId;

        if (ownerEnemyId == SharedBlock.CheatTargetLocalPlayer)
        {
            // The dialogue item-grant channel (docs/research/dialogue-consequences.md §3.4):
            // give straight to THIS instance's own player. No owner-id translation is involved
            // — "the local player" already names the same creature on every instance — so
            // MaterialisedEnemies.ToLocal must never be consulted for this sentinel.
            localId = Odyssey.GetPlayerObjectId();
            if (localId == 0)
            {
                Answer(SharedBlock.CheatStatusNoTarget, "giveloot: no local player to give to");
                return;
            }
        }
        else if (ownerEnemyId is 0 or Odyssey.ObjectInvalid)
        {
            Answer(SharedBlock.CheatStatusNoTarget, "giveloot needs the owner's enemy object id");
            return;
        }
        else
        {
            localId = MaterialisedEnemies.ToLocal(ownerEnemyId);
            if (localId == 0)
            {
                // Not an error: the overwhelmingly common case is a peer holding the REAL
                // creature (same GIT id), which already rolled its own equivalent loot
                // independently when it died — applying this here too would double it. Only a
                // materialised stand-in has none of its own, and only one maps through this
                // table.
                Answer(
                    SharedBlock.CheatStatusRefused,
                    $"0x{ownerEnemyId:X} is not a materialised stand-in here - nothing to give");
                return;
            }
        }

        byte* text = block.CheatText;
        int length = 0;
        while (length < SharedBlock.CheatTextLength - 1 && text[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, "no item resref given");
            return;
        }

        string resref = Encoding.ASCII.GetString(text, length);
        int stack = block.CheatAmount;

        uint itemId = Odyssey.GiveItemToInventory(localId, text, length, Math.Max(1, stack), out int failure);
        if (itemId is 0 or Odyssey.ObjectInvalid)
        {
            Answer(
                SharedBlock.CheatStatusEngineRefused,
                $"'{resref}' did not spawn onto 0x{localId:X} (failure {failure}; wrong resref?)");
            return;
        }

        uint charges = block.CheatItemCharges;
        uint maxCharges = block.CheatItemMaxCharges;
        uint upgrades = block.CheatItemUpgrades;
        uint wireFlags = block.CheatItemFlags;

        bool applied = Odyssey.ApplyItemIdentity(
            itemId,
            upgrades == SharedBlock.CheatFieldUnchanged ? 0 : upgrades,
            (uint)Math.Max(1, stack),
            charges == SharedBlock.CheatFieldUnchanged ? 0 : charges,
            maxCharges == SharedBlock.CheatFieldUnchanged ? 0 : maxCharges,
            (wireFlags == SharedBlock.CheatFieldUnchanged ? 0 : wireFlags) | SharedBlock.ItemIdentityKnown,
            out Odyssey.ItemIdentityFields after);

        string forWhom = ownerEnemyId == SharedBlock.CheatTargetLocalPlayer
            ? "the local player (dialogue grant)"
            : $"owner 0x{ownerEnemyId:X}";

        PluginHost.Log(applied
            ? $"materialise: gave loot '{resref}' (0x{itemId:X}) to stand-in 0x{localId:X} for "
              + $"{forWhom} — stack {after.StackSize}, "
              + $"charges {after.Charges}/{after.MaxCharges}, upgrades 0x{after.Upgrades:X}"
            : $"materialise: '{resref}' spawned as 0x{itemId:X} on 0x{localId:X} but identity "
              + $"apply failed — {Odyssey.LastItemResolveNote}");

        Answer(SharedBlock.CheatStatusApplied, $"gave '{resref}' to 0x{localId:X}");
    }

    /// <summary>
    /// Reads one live item's per-instance identity, and pokes whichever of the five fields
    /// the request named — through <see cref="Odyssey.ApplyItemIdentity"/>, the very function
    /// the ground-item channel calls on a peer's dropped item.
    ///
    /// ⚠ Two jobs on purpose, and the same code path for both. A request that names no field
    /// is a pure read; a request that names some reads first, overlays them, and writes the
    /// whole set back — so the "leave this alone" case is a value the writer was handed
    /// rather than a branch inside the writer. That matters because the writer is the thing
    /// under test: a `setitem` that took a different route to memory would prove nothing
    /// about what an arriving item gets.
    ///
    /// Why a staging cheat is the right shape for this. The apply path only ever runs on an
    /// item the far side has genuinely modified, and KOTOR gives a script no way to modify
    /// one: there is no drop UI, the workbench is a mouse-driven panel, and a blueprint
    /// spawned by `dropitem` is stock by definition. So an unattended run could produce a
    /// SENDER but never an interesting sender, which is precisely why the feature shipped
    /// with its whole apply half unmeasured. This makes the interesting sender.
    ///
    /// The answer always reports the fields READ BACK out of the item afterwards, never the
    /// values that were asked for.
    /// </summary>
    private void SetItem()
    {
        uint itemId = block.CheatTarget;
        if (itemId is 0 or Odyssey.ObjectInvalid)
        {
            Answer(SharedBlock.CheatStatusNoTarget, "setitem needs an item object id");
            return;
        }

        if (!Odyssey.TryReadItemIdentityFields(itemId, out Odyssey.ItemIdentityFields before))
        {
            // The resolve note names what it saw — no such object, an id at neither of the
            // two layouts' offsets, or a vtable belonging to some other class entirely.
            Answer(SharedBlock.CheatStatusNoTarget, Odyssey.LastItemResolveNote);
            return;
        }

        int stack = block.CheatAmount;
        uint charges = block.CheatItemCharges;
        uint maxCharges = block.CheatItemMaxCharges;
        uint upgrades = block.CheatItemUpgrades;
        uint wireFlags = block.CheatItemFlags;

        bool changes = stack > 0
            || charges != SharedBlock.CheatFieldUnchanged
            || maxCharges != SharedBlock.CheatFieldUnchanged
            || upgrades != SharedBlock.CheatFieldUnchanged
            || wireFlags != SharedBlock.CheatFieldUnchanged;

        if (!changes)
        {
            Answer(SharedBlock.CheatStatusApplied, Describe(itemId, before));
            return;
        }

        // The engine's own bit numbers going in, the wire's flag numbers coming out of the
        // request: ApplyItemIdentity takes the WIRE shape (it is fed by the block's item
        // slot), so an unchanged flags field has to be re-encoded from the item's live bits
        // rather than passed through.
        uint flags = wireFlags != SharedBlock.CheatFieldUnchanged
            ? wireFlags | SharedBlock.ItemIdentityKnown
            : ToWireFlags(before.BitFlags);

        bool applied = Odyssey.ApplyItemIdentity(
            itemId,
            upgrades != SharedBlock.CheatFieldUnchanged ? upgrades : before.Upgrades,
            stack > 0 ? (uint)stack : before.StackSize,
            charges != SharedBlock.CheatFieldUnchanged ? charges : before.Charges,
            maxCharges != SharedBlock.CheatFieldUnchanged ? maxCharges : before.MaxCharges,
            flags,
            out Odyssey.ItemIdentityFields after);

        if (!applied)
        {
            // It resolved a moment ago, so this is the interesting failure: something freed
            // the item between the read and the write. Say that rather than repeating "no
            // target", which would read as a typo in the id.
            Answer(
                SharedBlock.CheatStatusEngineRefused,
                $"0x{itemId:X} stopped resolving mid-poke: {Odyssey.LastItemResolveNote}");
            return;
        }

        // The full before/after goes to the LOG, where there is room for it, and the short
        // "what it holds now" goes in the block's 64-byte detail field. Splitting them is not
        // cosmetic: SetCheatDetail truncates without complaint, and the field it would have
        // cut off is the upgrade mask — the one number this whole feature exists to carry.
        PluginHost.Log(
            $"setitem 0x{itemId:X}: stack {before.StackSize}->{after.StackSize}, "
            + $"charges {before.Charges}/{before.MaxCharges}->{after.Charges}/{after.MaxCharges}, "
            + $"upgrades 0x{before.Upgrades:X}->0x{after.Upgrades:X}, "
            + $"bits 0x{before.BitFlags:X}->0x{after.BitFlags:X}, "
            + $"possessor 0x{after.Possessor:X}");

        Answer(SharedBlock.CheatStatusApplied, Describe(itemId, after));
    }

    /// <summary>
    /// One item's state in a sentence short enough to survive the block's 64-byte detail
    /// field, worst case included: a 0x7FFFFFFF id with every field at its maximum still
    /// fits. Hex without the 0x prefix for the two mask fields, because those four saved
    /// characters are the difference.
    /// </summary>
    private static string Describe(uint itemId, Odyssey.ItemIdentityFields fields) =>
        $"0x{itemId:X}: stk {fields.StackSize} chg {fields.Charges}/{fields.MaxCharges} "
        + $"upg {fields.Upgrades:X} bits {fields.BitFlags:X}";

    /// <summary>
    /// The engine's +0x288 bits as the wire's identity flags. The inverse of what
    /// <see cref="Odyssey.ApplyItemIdentity"/> does on the way in, and it deliberately covers
    /// only the three bits this channel carries — the other five real bits in that dword
    /// (dropable, pickpocketable, new-item, DELETING) are engine bookkeeping that
    /// docs/research/item-identity.md §5 explains we must not move.
    /// </summary>
    private static uint ToWireFlags(uint bits)
    {
        uint flags = SharedBlock.ItemIdentityKnown;

        if ((bits & (1u << 0)) != 0)
        {
            flags |= SharedBlock.ItemIdentityIdentified;
        }

        if ((bits & (1u << 5)) != 0)
        {
            flags |= SharedBlock.ItemIdentityStolen;
        }

        if ((bits & (1u << 6)) != 0)
        {
            flags |= SharedBlock.ItemIdentityNonEquippable;
        }

        return flags;
    }
}
