using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

// ---- the item-identity harness ----------------------------------------------------
//
// Four verbs that together make the ground channel's apply path exercisable with nobody
// at the controls: `items` finds a target, `setitem` makes it worth sending, `dropitem`
// sends it, `item` reads back what arrived on the far side. Before these the apply path
// could not be reached at all — the only drop the harness could make named a BLUEPRINT,
// which by construction carries no identity, so the receiving half had never once run
// (docs/research/item-identity.md §8). `giveloot` and its automatic drain
// (<see cref="CoopCommandDispatcher.DrainAutomaticLoot"/>) play the same role for
// materialised-enemy loot (docs/research/enemy-materialisation.md §5/§6).

/// <summary>The item-identity staging verbs: dropitem, items, item, setitem, giveloot, and
/// the automatic loot-give drain that shares <c>giveloot</c>'s own channel.</summary>
public sealed partial class CoopCommandDispatcher
{
    /// <summary>How many item rows `items` prints when the line names no cap. Enough to show
    /// a party's pack and the floor, few enough not to bury the overlay.</summary>
    private const int DefaultItemListing = 12;

    /// <summary>
    /// Drops a blueprint, or a copy of a LIVE item named by object id, as a shared ground
    /// item — through the SESSION, so every peer materialises it.
    ///
    /// ⚠ This used to file a <c>DropItem</c> staging cheat instead, which spawns a body in
    /// the typist's own game and nothing else. Nothing scans the floor for items to publish,
    /// so that drop reached no peer at all — while both the help text and
    /// docs/research/console-cheats.md §4 said "the item channel shares it". The session's
    /// own drop is what shares it, and the local body then arrives the same way a peer's
    /// does: spawned by the plugin from the item slot, identity and all.
    /// </summary>
    private void DropItem(string request)
    {
        ItemDropRequest.Outcome outcome = ItemDropRequest.Drop(manager, bridge, request);

        // ⚠ THE OFFLINE CASE STILL HAS TO WORK. Routing this verb through the session was the
        // fix for a drop that reached no peer — but a single-player game has no session, and
        // "not connected" would take away the only way to put an item on the floor before
        // anyone joins. That matters here specifically: staging a target for `setitem` is
        // something you want to do BEFORE the pair is up. So a sessionless drop falls back to
        // the staging cheat, which spawns it locally, and the answer says which of the two
        // happened rather than letting the caller assume.
        if (outcome.NoSession && !request.StartsWith('#')
            && !request.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            Reply("not connected - spawning it locally only, where no peer will see it");
            SendCheat(new CheatRequest(CheatKinds.DropItem, Text: request));
            return;
        }

        Reply(outcome.Note);
    }

    /// <summary>
    /// Lists the live items this instance can see, most interesting first.
    ///
    /// "Interesting" means differing from a stock blueprint in a way the identity channel
    /// carries — a stack, charges, an upgrade mask — because those are the only items whose
    /// replication proves anything. A listing of stock pistols would be a listing of items
    /// for which the apply path is indistinguishable from doing nothing.
    /// </summary>
    private void ListItems(int cap)
    {
        if (_kotor is null)
        {
            Reply("items needs --kotor (there is no game to read)");
            return;
        }

        IReadOnlyList<KotorGameBridge.ItemListing> items = _kotor.ListItems();

        // The sweep's own account of itself comes FIRST, because a short list has two very
        // different causes — few items, or a sweep that stopped early — and they are
        // indistinguishable from the rows.
        Reply($"items: {_kotor.LastItemScanNote}");

        if (items.Count == 0)
        {
            return;
        }

        uint localPlayer = _kotor.Player?.ObjectId ?? 0;
        int limit = cap > 0 ? Math.Min(cap, items.Count) : Math.Min(DefaultItemListing, items.Count);

        // The party pool's own id array, read straight off the repository: "yours" below is
        // possessor-keyed and the pool's possessor follows the party leader, so pool
        // MEMBERSHIP is reported separately (and the raw list first, so an id in the pool that
        // no body below answers to is visible as such).
        HashSet<uint>? poolIds = _kotor.TryReadPartyPoolItemIds();
        Reply(poolIds is null
            ? "items: party pool repository unreadable"
            : $"items: party pool repository lists {poolIds.Count} id(s): "
              + string.Join(", ", poolIds.OrderBy(id => id).Select(id => $"0x{id:X}")));

        for (int i = 0; i < limit; i++)
        {
            KotorGameBridge.ItemListing item = items[i];

            string where = item.OnFloor
                ? "on the floor"
                : item.Possessor == localPlayer
                    ? "yours"
                    : $"held by 0x{item.Possessor:X}";
            if (poolIds is not null && poolIds.Contains(item.ObjectId))
            {
                where += " [pool]";
            }

            // A body with no name at all is almost always one CreateItemOnFloor built from a
            // blueprint that is not in this install — it does not check the template load, so
            // a typo produces a real but empty item. Say so here: it is the difference between
            // "pick this one" and "this one is the wreckage of a mistyped drop".
            string name = item.Resref.Length > 0
                ? item.Resref
                : "<NO NAME - probably a bad blueprint>";

            Reply(
                $"  0x{item.ObjectId:X}  {name}  "
                + $"{where}  stack {item.Identity.StackSize}  "
                + $"charges {item.Identity.Charges}/{item.Identity.MaxCharges}  "
                + $"upgrades 0x{item.Identity.Upgrades:X}"
                + (item.IsInteresting ? "  <- not stock" : string.Empty));
        }

        // ⚠ A CLOSING SUMMARY, and it is not redundant with the header. Only the LAST reply
        // survives in the block's single reply field, which is what
        // `blockpoke.ps1 console 'items'` reads; the rows and the header reach the agent log
        // and the chat overlay, but a scripted caller would otherwise see nothing but the
        // last item row and have no idea whether the list was complete.
        int interesting = 0;
        foreach (KotorGameBridge.ItemListing item in items)
        {
            if (item.IsInteresting)
            {
                interesting++;
            }
        }

        Reply(
            $"items: showed {limit} of {items.Count}, {interesting} not stock"
            + (limit < items.Count ? $" (`items {items.Count}` for all of them)" : string.Empty)
            + " - full list in the agent log");
    }

    /// <summary>
    /// Prints one item's per-instance state, read straight out of the game's memory.
    ///
    /// This is the READ-BACK half of an identity test and the reason it is worth having as
    /// its own verb: run it on the RECEIVING instance, against the object id that instance's
    /// plugin log named, and it answers with what the item actually holds — independently of
    /// the plugin that wrote it, from a different process. The plugin's own read-back proves
    /// the write reached the address; this proves the address holds the item.
    /// </summary>
    private void InspectItem(string token)
    {
        if (_kotor is null)
        {
            Reply("item needs --kotor (there is no game to read)");
            return;
        }

        if (!ConsoleCommand.TryParseItemId(token, out uint objectId))
        {
            Reply($"item: '{token}' is not an object id (write it as 0x131)");
            return;
        }

        if (!_kotor.TryReadItemIdentity(objectId, out ItemIdentity identity, out string why, force: true))
        {
            Reply($"item 0x{objectId:X}: {why}");
            return;
        }

        _kotor.TryGetItemResref(objectId, out string resref, force: true);

        // ⚠ Kept inside 96 characters on purpose — that is the in-game reply field, and it
        // truncates silently. The log gets the same line, so what matters is that the fields
        // an identity check compares survive the cut.
        Reply(
            $"item 0x{objectId:X} {(resref.Length > 0 ? resref : "<NO NAME>")}: "
            + $"stack {identity.StackSize}, chg {identity.Charges}/{identity.MaxCharges}, "
            + $"upg 0x{identity.Upgrades:X}, "
            + $"{(identity.Identified ? "identified" : "UNidentified")}"
            + (identity.Stolen ? ", stolen" : string.Empty)
            + (identity.NonEquippable ? ", non-equippable" : string.Empty));

        if (resref.Length == 0)
        {
            Reply(
                $"item 0x{objectId:X} has no tag and no spawn record - CreateItemOnFloor "
                + "builds a real but EMPTY body for a blueprint that is not installed, so "
                + "this is most likely the wreckage of a mistyped resref");
        }
    }

    /// <summary>
    /// Pokes one live item's per-instance state through the plugin, so the sender has an item
    /// worth sending.
    ///
    /// The write itself happens on the game's thread, in
    /// <c>Odyssey.ApplyItemIdentity</c> — the very function that runs on a peer's arriving
    /// item. That is the point: staging the sender through a DIFFERENT writer would test a
    /// writer nothing else uses.
    ///
    /// The identified bit needs the item's current flags to be sent whole (the block carries
    /// a value, not a set/clear instruction), so it is read here first and the refusal says
    /// so rather than quietly leaving the bit alone.
    /// </summary>
    private void SetItem(string token, ItemFields fields)
    {
        if (!ConsoleCommand.TryParseItemId(token, out uint objectId))
        {
            Reply($"setitem: '{token}' is not an object id (write it as 0x131)");
            return;
        }

        uint flags = CheatKinds.Unchanged;

        if (fields.Identified is bool wanted)
        {
            // The block carries a flags VALUE, not a set/clear instruction, so flipping one
            // bit means knowing the other two. Only this branch needs to read the game, which
            // is why the requirement is stated here rather than at the top: every other
            // setitem is a plain write the plugin performs on its own.
            if (_kotor is null)
            {
                Reply("setitem identified= needs --kotor (it has to read the item's other flags first)");
                return;
            }

            if (!_kotor.TryReadItemIdentity(objectId, out ItemIdentity current, out string why, force: true))
            {
                Reply($"setitem 0x{objectId:X}: identified= needs the item's current flags, and {why}");
                return;
            }

            flags = wanted
                ? (uint)(current.Flags | ItemIdentity.FlagIdentified)
                : (uint)(current.Flags & ~ItemIdentity.FlagIdentified);
        }

        SendCheat(new CheatRequest(
            CheatKinds.SetItem,
            Target: objectId,
            Amount: (int)(fields.StackSize ?? 0),
            Charges: fields.Charges ?? CheatKinds.Unchanged,
            MaxCharges: fields.MaxCharges ?? CheatKinds.Unchanged,
            Upgrades: fields.Upgrades ?? CheatKinds.Unchanged,
            ItemFlags: flags));
    }

    /// <summary>
    /// Gives a materialised enemy's local stand-in one item of its real corpse's actual loot —
    /// the staging instrument for docs/research/enemy-materialisation.md §5/§6's still-open
    /// loot-sync gap, the same role `setitem`/`dropitem` play for item identity.
    /// </summary>
    private void GiveLoot(string ownerToken, string resref, int stack)
    {
        if (!ConsoleCommand.TryParseItemId(ownerToken, out uint ownerId))
        {
            Reply($"giveloot: '{ownerToken}' is not an object id (write it as 0x35)");
            return;
        }

        // A human typing this against the local-player sentinel is exactly the same shape of
        // write as the automatic drain below (DrainAutomaticLoot) — one fresh row about to
        // land in the local PC's own inventory. Feeding it into the same suppression ledger
        // keeps ScriptItemGrantPump's sweep from mistaking a manual test give for a script
        // grant. A no-op for any other target (a materialised enemy's stand-in never touches
        // the PC's inventory) — see CoopAgent.NoteLocalItemGiven's own doc.
        manager.Agent?.NoteLocalItemGiven(ownerId, resref);

        SendCheat(new CheatRequest(
            CheatKinds.GiveLoot,
            Target: ownerId,
            Text: resref,
            Amount: stack));
    }

    /// <summary>
    /// A human's own <c>pooladd</c>. Identical shape to <see cref="GiveLoot"/> above and to the
    /// automatic v2 drain (<c>CoopCommandDispatcher.PartyPoolV2.cs</c>): our own cheat channel is
    /// about to CREATE this item, and a party-pool add lands in the PC's own item repository, so
    /// without the ledger entry <c>Pumps.ScriptItemGrantPump</c> re-broadcasts it as a script
    /// grant. See <c>CoopAgent.NoteLocalPartyPoolAdd</c> for the live trace.
    /// </summary>
    private void PoolAdd(string resref, int count)
    {
        manager.Agent?.NoteLocalPartyPoolAdd(resref);

        SendCheat(new CheatRequest(CheatKinds.PartyPoolAdd, Text: resref, Amount: count));
    }

    /// <summary>
    /// The automatic half of <see cref="GiveLoot"/>: drains one queued give per tick from
    /// the connected <see cref="CoopAgent"/> — materialised loot received over the wire
    /// (docs/research/enemy-materialisation.md §6) OR a peer's dialogue-conversation grant
    /// (docs/research/dialogue-consequences.md §3), sharing the one pending-give queue —
    /// through the EXACT same <see cref="SendCheat"/> call a typed <c>giveloot</c> console
    /// command uses — but, unlike the console verb's stack-only argument, carries the FULL
    /// identity (<see cref="CheatKinds.GiveLoot"/>'s handler already accepts it, the same
    /// four fields <c>setitem</c> writes) since this is exactly the path that identity was
    /// built for.
    ///
    /// Deliberately gated on <see cref="_pendingCheatSerial"/> already being idle: this is
    /// the same single-slot channel a human can be mid-command on, and <see cref="SendCheat"/>
    /// itself says so — "only one request is in flight". Firing here anyway would silently
    /// abandon whichever answer — the player's or this one's — was already in flight, and an
    /// item that never actually reaches a corpse would look identical to one that did.
    /// Skipping this tick when busy is correct: the queue holds the request until the
    /// channel is free again, which given its own well-under-a-tick turnaround is at most a
    /// few ticks later.
    /// </summary>
    private void DrainAutomaticLoot()
    {
        if (_pendingCheatSerial != 0)
        {
            return;
        }

        if (manager.Agent is not { } agent || !agent.TryDequeuePendingLoot(out CoopAgent.PendingMaterialisedLootGive give))
        {
            return;
        }

        ItemIdentity identity = give.Item.Identity;
        Log?.Invoke($"materialised loot: submitting '{give.Item.Resref}' for target 0x{give.TargetObjectId:X} through the giveloot channel");
        SendCheat(new CheatRequest(
            CheatKinds.GiveLoot,
            Target: give.TargetObjectId,
            Text: give.Item.Resref,
            Amount: identity.StackSize,
            Charges: identity.Charges,
            MaxCharges: identity.MaxCharges,
            Upgrades: identity.Upgrades,
            ItemFlags: identity.Flags));
    }
}
