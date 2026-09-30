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
    /// LIVE-TEST PRIMITIVE (docs/research/store-sync.md's 2026-08-27 addendum, root-caused and
    /// fixed 2026-08-28): opens the store GUI against <see cref="SharedBlock.CheatTarget"/>
    /// (object id) or <see cref="SharedBlock.CheatText"/> (tag) through
    /// <see cref="Odyssey.OpenStore"/> — one of the two is required, there is no "nearest store"
    /// sweep. <paramref name="playerId"/> is the shopping creature's object id, passed straight
    /// through to <c>CGuiInGame::ShowStoreGui</c>'s own second argument (see Odyssey.Store.cs).
    /// </summary>
    private void OpenStore(uint playerId)
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

        if (!Odyssey.OpenStore(store, playerId, out string reason))
        {
            PluginHost.Log($"openstore: {what} refused - {reason}");
            Answer(SharedBlock.CheatStatusEngineRefused, $"openstore {what} refused: {reason}");
            return;
        }

        PluginHost.Log($"openstore: store {what} - ShowStoreGui called, panel populated");
        Answer(SharedBlock.CheatStatusApplied, $"store {what}: opened");
    }

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/shared-inventory-v2.md §14): closes the full-screen
    /// in-game GUI through <see cref="Odyssey.CloseFullScreenGui"/>. Takes no arguments.
    ///
    /// Lives beside <see cref="OpenStore"/> because it exists FOR it: that screen sets the one
    /// precondition <c>ShowStoreGui</c> refuses on, and that refusal has blocked the client half
    /// of four consecutive live store rounds. See
    /// <see cref="SharedBlock.CheatKindCloseGui"/> for the full account.
    ///
    /// A screen that was already closed answers APPLIED, not REFUSED — the caller asked for a
    /// state, not for an event, and a retry loop that treats "already fine" as a failure is how
    /// a recipe stalls on its own success.
    /// </summary>
    private void CloseGui()
    {
        bool closed = Odyssey.IsTopRegisteredStatusSummary()
            ? Odyssey.DismissStatusSummary(out string detail)
            : Odyssey.CloseFullScreenGui(out detail);
        PluginHost.Log($"closegui: {detail}");

        Answer(
            closed ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            $"closegui: {detail}");
    }

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/shared-inventory-v2.md §15): pops the GUI manager's
    /// own modal stack through <see cref="Odyssey.PopModalPanels"/>.
    ///
    /// The half <see cref="CloseGui"/> was missing. <c>HideSWInGameGui</c> refuses its own
    /// teardown while that stack is occupied, so on an instance whose stack carries a panel —
    /// which §15 traced to this project's OWN menu-load path — `closegui` could report only the
    /// refusal, never clear it. <see cref="SharedBlock.CheatAmount"/> caps how many to pop
    /// (default 1, clamped 1..8: a modal stack is a handful deep at worst, and an unbounded loop
    /// on the main thread is not a debugging tool).
    ///
    /// An already-empty stack answers APPLIED, same reasoning <see cref="CloseGui"/> gives.
    /// </summary>
    private void PopModal()
    {
        int max = Math.Clamp(block.CheatAmount > 0 ? block.CheatAmount : 1, 1, 8);

        bool popped = Odyssey.PopModalPanels(max, out string detail);
        PluginHost.Log($"popmodal: {detail}");

        Answer(
            popped ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            $"popmodal: {detail}");
    }

    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/store-sync.md's 2026-08-28 addendum): buys a resref
    /// from a store through the REAL <see cref="Odyssey.BuyFromStore"/> (the GUI's own Buy
    /// click handler) — gold and item both move through the engine's own path. See
    /// <see cref="SharedBlock.CheatKindStoreBuy"/>'s own doc comment for the field packing:
    /// <see cref="SharedBlock.CheatTarget"/> nonzero names the store by object id and
    /// <see cref="SharedBlock.CheatText"/> is then the bare resref; <see cref="SharedBlock.CheatTarget"/>
    /// zero means the store was a tag, packed as <c>"tag|resref"</c> in
    /// <see cref="SharedBlock.CheatText"/>. <paramref name="player"/> (resolved by
    /// <see cref="Execute"/> the same way every other per-player cheat kind already is) is
    /// threaded through to <see cref="Odyssey.BuyFromStore"/> for its own affordability guard
    /// (docs/research/store-sync.md's live-found gold-underflow bug — <c>BuyItem</c> itself has
    /// none).
    /// </summary>
    private void Buy(nint player) => Trade(isBuy: true, player);

    /// <summary>Sell side of <see cref="Buy"/> — see that method's own doc comment and
    /// <see cref="SharedBlock.CheatKindStoreSell"/>.</summary>
    private void Sell(nint player) => Trade(isBuy: false, player);

    /// <summary>
    /// Shared body for <see cref="Buy"/>/<see cref="Sell"/>: resolves the store (by id or tag),
    /// unpacks the resref out of <see cref="SharedBlock.CheatText"/>'s single string field
    /// (packed with the store's own tag when no numeric id was given — see
    /// <see cref="SharedBlock.CheatKindStoreBuy"/>), then drives <see cref="Odyssey.BuyFromStore"/>
    /// or <see cref="Odyssey.SellToStore"/> for <see cref="SharedBlock.CheatAmount"/> units
    /// (default 1, clamped to 100 — a typo should not loop hundreds of engine calls).
    /// </summary>
    private void Trade(bool isBuy, nint player)
    {
        string verb = isBuy ? "buy" : "sell";
        uint storeId = block.CheatTarget;

        byte* text = block.CheatText;
        int totalLength = 0;
        while (totalLength < SharedBlock.CheatTextLength - 1 && text[totalLength] != 0)
        {
            totalLength++;
        }

        byte* tagBytes = text;
        int tagLength = 0;
        byte* resrefBytes;
        int resrefLength;

        if (storeId != 0)
        {
            resrefBytes = text;
            resrefLength = totalLength;
        }
        else
        {
            int pipe = -1;
            for (int i = 0; i < totalLength; i++)
            {
                if (text[i] == (byte)'|')
                {
                    pipe = i;
                    break;
                }
            }

            if (pipe < 0)
            {
                Answer(SharedBlock.CheatStatusRefused, $"{verb}: no store tag/id given");
                return;
            }

            tagLength = pipe;
            resrefBytes = text + pipe + 1;
            resrefLength = totalLength - pipe - 1;
        }

        if (resrefLength == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, $"{verb}: no resref given");
            return;
        }

        nint store;
        string what;

        if (storeId != 0)
        {
            store = Odyssey.ResolveStoreBase(storeId);
            what = $"0x{storeId:X}";

            if (store == 0)
            {
                Answer(SharedBlock.CheatStatusNoTarget, $"{verb}: {Odyssey.LastStoreNote}");
                return;
            }
        }
        else
        {
            store = CreatureFinder.FindStoreByTag(new ReadOnlySpan<byte>(tagBytes, tagLength));
            what = Encoding.ASCII.GetString(tagBytes, tagLength);

            if (store == 0)
            {
                Answer(SharedBlock.CheatStatusNoTarget, $"no store tagged '{what}' found here");
                return;
            }

            storeId = *(uint*)(store + 0x04); // CSWSObject + this = the object id.
        }

        int count = Math.Clamp(block.CheatAmount > 0 ? block.CheatAmount : 1, 1, 100);
        string resref = Encoding.ASCII.GetString(resrefBytes, resrefLength);

        bool applied = isBuy
            ? Odyssey.BuyFromStore(player, storeId, resrefBytes, resrefLength, count, out int moved)
            : Odyssey.SellToStore(player, storeId, resrefBytes, resrefLength, count, out moved);

        if (!applied)
        {
            PluginHost.Log($"{verb}: '{resref}' x{count} store {what} failed - {Odyssey.LastStoreNote}");
            Answer(SharedBlock.CheatStatusEngineRefused, $"{verb} '{resref}' failed: {Odyssey.LastStoreNote}");
            return;
        }

        string note = moved < count ? $" (only {moved} available)" : string.Empty;
        string direction = isBuy ? "from" : "to";
        PluginHost.Log($"{verb}: {verb} '{resref}' x{moved} {direction} store {what}{note}");
        Answer(SharedBlock.CheatStatusApplied, $"{verb} '{resref}' x{moved} {direction} store {what}{note}");
    }

    /// <summary>
    /// DEV VERB: sets the party's gold directly to <see cref="SharedBlock.CheatAmount"/>
    /// through <see cref="Odyssey.SetGold"/> (the real <c>CSWSCreature::SetGold</c> call,
    /// clamped server-side to 999,999,999). Built to repair a save whose gold was driven
    /// negative-then-wrapped by an unguarded <c>buy</c> before this pass added
    /// <see cref="Odyssey.BuyFromStore"/>'s own affordability guard — see
    /// docs/live-verification.md for the shared rig save's own poisoned reading and how this
    /// verb fixes it.
    /// </summary>
    private void SetGold(nint player)
    {
        int amount = block.CheatAmount;

        bool applied = Odyssey.SetGold(player, amount, out int before, out int after);
        if (!applied)
        {
            PluginHost.Log($"setgold: failed - {Odyssey.LastStoreNote}");
            Answer(SharedBlock.CheatStatusEngineRefused, $"setgold failed: {Odyssey.LastStoreNote}");
            return;
        }

        PluginHost.Log($"setgold: gold {before} -> {after} (asked for {amount})");
        Answer(SharedBlock.CheatStatusApplied, $"gold {before} -> {after}");
    }
}
