using System.Text;

namespace KotorCoop.Plugin;

// The merchant/store stock staging cheats: StoreInsert / StoreRemove. CheatTarget is the
// store's object id; the rest reuses the exact same request fields SetItem/GiveLoot/
// PartyPoolAdd already carry (Text = resref, Amount = count, Charges/MaxCharges/Upgrades/
// ItemFlags = the rest of the stack identity) — no SharedBlock layout change, no version bump.
// See docs/research/store-sync.md.
internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// Inserts <see cref="SharedBlock.CheatAmount"/> units of a resref+identity stack into store
    /// <see cref="SharedBlock.CheatTarget"/>'s stock, through <see cref="Odyssey.InsertItemIntoStore"/>.
    /// This is the automatic apply half of the sync channel (docs/research/store-sync.md) — the
    /// same arbitrated single-slot channel a human's own console verb would use.
    /// </summary>
    private void StoreInsert(uint playerId)
    {
        uint storeObjectId = block.CheatTarget;
        if (storeObjectId is 0 or Odyssey.ObjectInvalid)
        {
            Answer(SharedBlock.CheatStatusRefused, "store insert: no store target given");
            return;
        }

        byte* text = block.CheatText;
        int length = 0;
        while (length < SharedBlock.CheatTextLength - 1 && text[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, "store insert: no resref given");
            return;
        }

        int count = block.CheatAmount;
        if (count <= 0)
        {
            Answer(SharedBlock.CheatStatusRefused, "store insert: needs a positive count");
            return;
        }

        string resref = Encoding.ASCII.GetString(text, length);
        uint charges = block.CheatItemCharges;
        uint maxCharges = block.CheatItemMaxCharges;
        uint upgrades = block.CheatItemUpgrades;
        uint wireFlags = block.CheatItemFlags;

        bool applied = Odyssey.InsertItemIntoStore(
            playerId, storeObjectId, text, length, count,
            upgrades == SharedBlock.CheatFieldUnchanged ? 0 : upgrades,
            charges == SharedBlock.CheatFieldUnchanged ? 0 : charges,
            maxCharges == SharedBlock.CheatFieldUnchanged ? 0 : maxCharges,
            wireFlags == SharedBlock.CheatFieldUnchanged ? 0 : wireFlags,
            out uint itemId, out Odyssey.ItemIdentityFields after);

        if (!applied)
        {
            PluginHost.Log(
                $"store: insert '{resref}' x{count} into store 0x{storeObjectId:X} failed — {Odyssey.LastStoreNote}");
            Answer(SharedBlock.CheatStatusEngineRefused, $"'{resref}' insert failed: {Odyssey.LastStoreNote}");
            return;
        }

        PluginHost.Log(
            $"store: inserted '{resref}' (0x{itemId:X}) x{after.StackSize} into store 0x{storeObjectId:X}, "
            + $"charges {after.Charges}/{after.MaxCharges}, upgrades 0x{after.Upgrades:X}");
        Answer(SharedBlock.CheatStatusApplied, $"inserted '{resref}' x{count} into store 0x{storeObjectId:X}");
    }

    /// <summary>
    /// Removes <see cref="SharedBlock.CheatAmount"/> units of a resref+identity stack from store
    /// <see cref="SharedBlock.CheatTarget"/>'s stock, through <see cref="Odyssey.RemoveStoreStack"/>.
    /// </summary>
    private void StoreRemove()
    {
        uint storeObjectId = block.CheatTarget;
        if (storeObjectId is 0 or Odyssey.ObjectInvalid)
        {
            Answer(SharedBlock.CheatStatusRefused, "store remove: no store target given");
            return;
        }

        byte* text = block.CheatText;
        int length = 0;
        while (length < SharedBlock.CheatTextLength - 1 && text[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, "store remove: no resref given");
            return;
        }

        int count = block.CheatAmount;
        if (count <= 0)
        {
            Answer(SharedBlock.CheatStatusRefused, "store remove: needs a positive count");
            return;
        }

        string resref = Encoding.ASCII.GetString(text, length);
        uint charges = block.CheatItemCharges;
        uint maxCharges = block.CheatItemMaxCharges;
        uint upgrades = block.CheatItemUpgrades;
        uint wireFlags = block.CheatItemFlags;

        bool applied = Odyssey.RemoveStoreStack(
            storeObjectId, text, length, count,
            charges == SharedBlock.CheatFieldUnchanged ? 0 : charges,
            maxCharges == SharedBlock.CheatFieldUnchanged ? 0 : maxCharges,
            upgrades == SharedBlock.CheatFieldUnchanged ? 0 : upgrades,
            wireFlags == SharedBlock.CheatFieldUnchanged ? 0 : wireFlags,
            out int removed);

        if (!applied)
        {
            PluginHost.Log(
                $"store: remove '{resref}' x{count} from store 0x{storeObjectId:X} failed — {Odyssey.LastStoreNote}");
            Answer(SharedBlock.CheatStatusEngineRefused, $"'{resref}' remove failed: {Odyssey.LastStoreNote}");
            return;
        }

        PluginHost.Log($"store: removed '{resref}' x{removed} (asked for {count}) from store 0x{storeObjectId:X}");
        Answer(SharedBlock.CheatStatusApplied, $"removed '{resref}' x{removed} from store 0x{storeObjectId:X}");
    }

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/store-sync.md's 2026-08-27 addendum): opens the store
    /// GUI against <see cref="SharedBlock.CheatTarget"/> (object id) or
    /// <see cref="SharedBlock.CheatText"/> (tag) through <see cref="Odyssey.OpenStore"/> — one
    /// of the two is required, there is no "nearest store" sweep.
    /// </summary>
    private void OpenStore()
    {
        uint requestedId = block.CheatTarget;
        string text = ReadCheatText();

        nint store;
        string what;

        if (requestedId != 0)
        {
            store = Odyssey.ResolveStoreBase(requestedId);
            what = $"0x{requestedId:X}";

            if (store == 0)
            {
                Answer(SharedBlock.CheatStatusNoTarget, $"openstore: {Odyssey.LastStoreNote}");
                return;
            }
        }
        else if (text.Length > 0)
        {
            byte* tag = block.CheatText;
            int length = 0;
            while (length < SharedBlock.CheatTextLength - 1 && tag[length] != 0)
            {
                length++;
            }

            store = CreatureFinder.FindStoreByTag(new ReadOnlySpan<byte>(tag, length));
            what = $"'{text}'";

            if (store == 0)
            {
                Answer(SharedBlock.CheatStatusNoTarget, $"no store tagged '{text}' found here");
                return;
            }
        }
        else
        {
            Answer(SharedBlock.CheatStatusRefused, "openstore needs a store object id or tag");
            return;
        }

        if (!Odyssey.OpenStore(store, out string reason))
        {
            PluginHost.Log($"openstore: {what} refused - {reason}");
            Answer(SharedBlock.CheatStatusEngineRefused, $"openstore {what} refused: {reason}");
            return;
        }

        PluginHost.Log($"openstore: store {what} - ShowStoreGui called, panel populated");
        Answer(SharedBlock.CheatStatusApplied, $"store {what}: opened");
    }
}
