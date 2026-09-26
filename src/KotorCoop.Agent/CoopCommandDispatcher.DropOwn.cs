using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>One of the local player's own backpack items, as the F2 picker and <c>drop</c> see it.</summary>
public sealed record DroppableItem(uint ObjectId, string Resref, string Name, ItemIdentity Identity)
{
    /// <summary>"Heavy Blaster x1" — the readable name when the install has one.</summary>
    public string Label => Identity.StackSize > 1 ? $"{Name} x{Identity.StackSize}" : Name;
}

/// <summary>
/// <c>drop &lt;0xid|resref&gt;</c>: puts one of YOUR OWN backpack items on the floor for your
/// partner. Unlike <c>dropitem</c> (a test verb that spawns a COPY from a blueprint, so it
/// carries no item state — the "no identity" line players kept seeing on F2), this drops the
/// real item: its live stack/charges/upgrades ride the ground-item channel, and the same stack
/// is then taken out of the backpack through the party-pool remove, matched on that identity.
///
/// Only backpack (party-pool) items the local player holds are offered: equipped gear is not
/// in the pool, and quest items stay shared under personal inventory, so dropping one would
/// take it from both players' stories.
/// </summary>
public sealed partial class CoopCommandDispatcher
{
    /// <summary>Your own droppable items, readable names first. Safe from any thread.
    /// <paramref name="fresh"/> forces a full item sweep (seconds on a big module); the
    /// picker's background refresh passes false and takes the throttled cache instead.</summary>
    public IReadOnlyList<DroppableItem> ListDroppableItems(bool fresh = true)
    {
        if (_kotor is not { } live)
        {
            return [];
        }

        IReadOnlyList<KotorGameBridge.ItemListing> items;
        HashSet<uint>? pool;
        uint localPlayer;
        lock (bridge.Sync)
        {
            items = live.ListItems(force: fresh);
            pool = live.TryReadPartyPoolItemIds();
            localPlayer = live.Player?.ObjectId ?? 0;
        }

        if (pool is null || localPlayer == 0)
        {
            return [];
        }

        List<DroppableItem> result = [];
        foreach (KotorGameBridge.ItemListing item in items)
        {
            if (item.IsDeleting || item.Resref.Length == 0 || item.Possessor != localPlayer
                || !pool.Contains(item.ObjectId) || QuestItemCatalog.IsQuestItemTag(item.Resref))
            {
                continue;
            }

            string name = ItemNames.TryRead(gameDirectory, item.Resref, out string readable)
                ? readable
                : item.Resref;
            result.Add(new DroppableItem(item.ObjectId, item.Resref, name, item.Identity));
        }

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>An item's in-game name from this install, or the resref when it has none.</summary>
    public string ItemName(string resref) =>
        ItemNames.TryRead(gameDirectory, resref, out string name) ? name : resref;

    private void DropOwnItem(string token, int count)
    {
        if (_kotor is null)
        {
            Reply("drop needs the game attached (--kotor)");
            return;
        }

        IReadOnlyList<DroppableItem> own = ListDroppableItems();
        DroppableItem? pick = ConsoleCommand.TryParseItemId(token, out uint id)
            ? own.FirstOrDefault(i => i.ObjectId == id)
            : own.FirstOrDefault(i => i.Resref.Equals(token, StringComparison.OrdinalIgnoreCase));

        if (pick is null)
        {
            Reply(QuestItemCatalog.IsQuestItemTag(token)
                ? $"drop: '{token}' is a quest item - those stay shared and can't be dropped"
                : $"drop: '{token}' is not in your backpack (F2 in the agent window lists what you can drop)");
            return;
        }

        DropOwn(pick, count);
    }

    /// <summary>
    /// Drops <paramref name="count"/> of <paramref name="item"/> (0 or more than the stack =
    /// the whole stack) in front of the player and takes the same amount out of the backpack.
    /// Public for the F2 picker, which already holds the item's identity and so skips the
    /// second memory sweep the console path needs.
    /// </summary>
    public void DropOwn(DroppableItem item, int count)
    {
        int stack = Math.Max(1, (int)item.Identity.StackSize);
        int amount = count <= 0 || count > stack ? stack : count;

        ItemDropRequest.Outcome outcome = ItemDropRequest.DropKnown(manager, item.Resref, item.Identity, (ushort)amount);
        if (!outcome.Dropped)
        {
            Reply(outcome.Note);
            return;
        }

        // Out of the backpack, matched on the identity just dropped so another stack of the
        // same resref (different upgrades, say) is left alone. SendCheat adds the world-load
        // fence pool requests need.
        string what = amount > 1 ? $"{item.Name} x{amount}" : item.Name;
        SendCheat(
            new CheatRequest(
                CheatKinds.PartyPoolRemove,
                Text: item.Resref,
                Amount: amount,
                Charges: item.Identity.Charges,
                MaxCharges: item.Identity.MaxCharges,
                Upgrades: item.Identity.Upgrades,
                ItemFlags: item.Identity.Flags),
            onApplied: () => Reply($"dropped {what} in front of you - your partner can pick it up (F3)"),
            onRefused: () => Reply(
                $"dropped {what}, but it could not be taken out of your backpack - you may now have both"),
            quiet: true);
    }
}
