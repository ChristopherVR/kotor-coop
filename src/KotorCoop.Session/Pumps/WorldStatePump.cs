using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// The world-state channels that carry no persistent bookkeeping of their own — each pump here
/// is a straight read-diff-publish/apply pass over the session's stores (interaction claims,
/// the quest journal, door/placeable state, story globals, ground items). Grouped into one
/// class because none of them share state with each other, only with the bridge and session
/// they already take as collaborators.
/// </summary>
internal sealed class WorldStatePump(
    IGameBridge bridge, ICoopSession session, AgentSettings settings,
    RecentlySyncedItems recentlySyncedItems)
{
    /// <summary>
    /// Claims whatever the local player is interacting with, and hands back every lock anyone
    /// holds so the plugin can hold the objects IT owns still.
    ///
    /// Both directions run every tick, deliberately. The claim is a lease that expires unless
    /// refreshed, and the lock list is a level that is replaced wholesale — neither has an
    /// edge that could be dropped, which is what makes a client dying mid-conversation
    /// harmless rather than a permanently frozen NPC.
    /// </summary>
    public void PumpInteractionLocks(in PlayerState local)
    {
        bridge.TryReadLocalInteraction(out string tag, out uint kind, out bool active, out uint targetId);

        // Sent even when the tag is empty: that is how the session learns which module the
        // locks are scoped to, and a lock names a tag (or, for InteractionKind.Container, a
        // target id instead — see InteractionLockStore's remarks), which only means anything
        // in one.
        session.PublishInteractionClaim(
            local.Module, tag ?? string.Empty, (InteractionKind)kind, active, targetId);

        // ⚠ OUR OWN LOCK IS FILTERED OUT HERE, so the plugin can hold everything it is given
        // without needing to know its own peer id.
        //
        // It would be actively wrong to send it: the object WE are interacting with is already
        // frozen locally by the engine's own dialogue system, and re-clearing its action queue
        // every pass would be fighting the conversation rather than helping it.
        IReadOnlyList<InteractionLock> all = session.GetInteractionLocks();
        List<InteractionLock> theirs = [];
        foreach (InteractionLock held in all)
        {
            if (held.PeerId != session.LocalPeerId)
            {
                theirs.Add(held);
            }
        }

        bridge.WriteInteractionLocks(theirs);

        PumpContainerLockArbitration(local.Module);
    }

    /// <summary>
    /// The synchronous-feeling half of the container-lock fix (docs/roadmap-seamless.md Tier 1
    /// item 4): forwards a fresh request the plugin's own deferral state machine published
    /// (through the shared block — the plugin cannot reach <c>ICoopSession</c> directly) into
    /// <see cref="ICoopSession.RequestContainerLock"/> exactly once, then hands back whatever
    /// answer <see cref="ICoopSession.TryGetLockResponse"/> currently has for it. Every tick, the
    /// same "cheap no-op almost every tick" shape <see cref="PumpJournal"/>'s own re-offer loop
    /// already uses.
    /// </summary>
    private void PumpContainerLockArbitration(string localModule)
    {
        if (bridge.TryReadContainerLockRequest(out uint targetId, out uint requestSerial)
            && requestSerial != 0 && targetId != 0 && requestSerial != _lastForwardedContainerLockSerial)
        {
            _lastForwardedContainerLockSerial = requestSerial;
            session.RequestContainerLock(localModule, targetId, requestSerial);
        }

        if (_lastForwardedContainerLockSerial != 0
            && session.TryGetLockResponse(_lastForwardedContainerLockSerial, out bool granted))
        {
            bridge.WriteContainerLockResponse(_lastForwardedContainerLockSerial, granted);
        }
    }

    /// <summary>The most recent request serial this pump has already forwarded to the session,
    /// so a request is sent exactly once rather than re-sent every tick while the plugin's own
    /// serial field has not yet moved on to a new one.</summary>
    private uint _lastForwardedContainerLockSerial;

    /// <summary>
    /// Carries the quest journal both ways: local changes (advances, legitimate decreases,
    /// and removals — see docs/research/quest-sync.md) are announced to the session, and
    /// anything the session knows that this game does not is applied through the engine's
    /// own SetState (or, for a removal, the removal path), which refreshes the journal GUI.
    /// This plus the globals channel is what makes a quest one quest instead of two parallel
    /// ones.
    /// </summary>
    public void PumpJournal(SessionCapabilities caps)
    {
        if (!bridge.SupportsJournalSync || (caps & SessionCapabilities.Journal) == 0)
        {
            return;
        }

        // ⚠ ORDER MATTERS: reading the local journal comes FIRST, so the bridge's shadow
        // is primed from the game before any store entry is offered for applying. On a
        // fresh join the store fills from the host's snapshot while this game is still
        // loading; offering those entries against an unprimed shadow replayed the whole
        // journal through the engine, and the engine's SetState shows the "Journal Entry
        // Added" box even for a no-op — the popup every player saw on load-in. The bridge
        // refuses applies until it has read the journal once, and skips any entry the
        // game already has; a GENUINE change from a peer still applies and still shows
        // the popup, deliberately — the quest really moved, and the engine offers no way
        // to apply it silently (SetState has no feedback flag).
        foreach (JournalChange change in bridge.CollectChangedJournal())
        {
            if (change.Removed)
            {
                session.RemoveJournalEntry(change.QuestTag);
            }
            else
            {
                session.SetJournalEntry(change.QuestTag, change.State);
            }
        }

        foreach (KeyValuePair<string, int> entry in session.Journal.Snapshot())
        {
            // The bridge keeps its own shadow and skips entries the game already has, so
            // this is a cheap no-op almost every tick.
            bridge.TryApplyJournalEntry(entry.Key, entry.Value);
        }

        // The removal twin: any tag the session knows as gone that this game has not been
        // told about yet. Small and rare (a handful of tombstones for a whole playthrough),
        // so re-offering the full list every tick is cheap — same shape as the live-entry
        // loop above.
        foreach (string tag in session.Journal.RemovedTags())
        {
            bridge.TryRemoveJournalEntry(tag);
        }
    }

    /// <summary>
    /// Carries door state both ways on the same reliable flag channel the quest globals use.
    /// A door open for one player and shut for the other is the most visible desync co-op can
    /// produce. Door values are USUALLY monotonic (closed to open, locked to unlocked,
    /// anything to destroyed), but not always — a script can swing one shut again — so this
    /// announces every genuine local change unconditionally rather than gating on an
    /// "advanced" check: <see cref="ICoopSession.SetFlag"/> mints a fresh, outranking serial
    /// per change (see <c>EventFlagStore</c>), so it already no-ops correctly on a value the
    /// store already agrees with, and never refuses a real local change in either direction.
    /// </summary>
    public void PumpDoors(SessionCapabilities caps)
    {
        if (!bridge.SupportsDoorSync || (caps & SessionCapabilities.Doors) == 0)
        {
            return;
        }

        foreach (KeyValuePair<uint, sbyte> change in bridge.CollectChangedDoorState())
        {
            // CollectChangedDoorState already only reports a genuine local transition (its
            // own shadow-diff), so every entry here is worth announcing — no separate gate.
            session.SetFlag(change.Key, change.Value);
        }

        bridge.TryWriteDoorStates(session.Flags.Snapshot());
    }

    /// <summary>
    /// The door channel's twin for placeables (chests, corpses, switches, containers): a
    /// searched chest or unlocked container stays that way for both players. Rides the same
    /// reliable flag store under a different tag, and shares the door capability gate because
    /// it rests on the same object-id-across-the-same-save assumption.
    /// </summary>
    public void PumpPlaceables(SessionCapabilities caps)
    {
        if (!bridge.SupportsPlaceableSync || (caps & SessionCapabilities.Doors) == 0)
        {
            return;
        }

        foreach (KeyValuePair<uint, sbyte> change in bridge.CollectChangedPlaceableState())
        {
            session.SetFlag(change.Key, change.Value);
        }

        bridge.TryWritePlaceableStates(session.Flags.Snapshot());
    }

    /// <summary>
    /// Carries KOTOR's own world state — the quest and story globals every script branches
    /// on — in both directions. This is what separates two players sharing a map from two
    /// players sharing a game.
    ///
    /// Both directions converge by per-key Lamport clock (see <c>EventFlagStore</c>), not a
    /// bare value comparison: a global can legitimately go DOWN (a kill counter decremented,
    /// a boolean cleared back to false), and a value-only "higher wins" policy refused those
    /// forever while also fighting the local engine's own write every tick.
    /// <see cref="ICoopSession.SetFlag"/> mints a fresh serial per genuine local change and
    /// always wins locally, so it is called unconditionally for every entry
    /// <see cref="IGameBridge.CollectChangedWorldState"/> reports — that method's own
    /// shadow-diff already guarantees each one is a real local transition, not an echo. See
    /// docs/research/quest-sync.md.
    /// </summary>
    public void PumpWorldState(SessionCapabilities caps)
    {
        if ((caps & SessionCapabilities.WorldState) == 0)
        {
            return;
        }

        if (!bridge.SupportsWorldState)
        {
            return;
        }

        foreach (KeyValuePair<uint, sbyte> change in bridge.CollectChangedWorldState())
        {
            session.SetFlag(change.Key, change.Value);
        }

        // Apply anything the session knows that the game does not. The bridge updates its
        // own shadow copy as it writes, so the next scan does not see this as local.
        foreach (KeyValuePair<uint, sbyte> flag in session.Flags.Snapshot())
        {
            bridge.TryApplyWorldState(flag.Key, flag.Value);
        }
    }

    /// <summary>
    /// Reconciles the session's ground-item truth with the game: the bridge materialises
    /// whatever the store says is on the ground, and anything the game reports as picked
    /// up locally is announced back so it vanishes for everyone else too.
    /// </summary>
    public void PumpItems(in PlayerState local, SessionCapabilities caps)
    {
        if (!bridge.SupportsGroundItems || (caps & SessionCapabilities.GroundItems) == 0)
        {
            return;
        }

        List<GroundItem> items = session.Items.Snapshot();
        string localModule = local.Module;

        IReadOnlyList<GroundItem> visible = settings.FilterByModule && localModule.Length > 0
            ? [.. items.Where(i => i.Module.Length == 0
                || string.Equals(i.Module, localModule, StringComparison.OrdinalIgnoreCase))]
            : items;

        bridge.TryWriteGroundItems(visible);

        foreach (uint itemId in bridge.CollectVanishedItems())
        {
            // ⚠ Looked up BEFORE RemoveItem, which is what actually removes the entry from
            // session.Items — the resref feeds ScriptItemGrantPump's suppression ledger
            // (docs/research/dialogue-consequences.md's script-item-sync section) so a ground
            // pickup landing in the local PC's inventory this same tick is not re-reported as
            // a script grant. A miss (the store no longer has the id for some reason) simply
            // means nothing is recorded — RemoveItem below is unaffected either way.
            if (session.Items.TryGet(itemId, out GroundItem? picked) && picked is not null)
            {
                recentlySyncedItems.Record(picked.Resref, 1, Environment.TickCount64);
            }

            session.RemoveItem(itemId);
        }
    }
}
