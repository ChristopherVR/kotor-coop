namespace KotorCoop.Plugin;

/// <summary>
/// Materialises the session's ground items in the game, once per frame, on the game's
/// thread. The agent writes the DESIRED set of items into the shared block; this class
/// reconciles the world toward it: spawn what is missing, destroy what is no longer
/// wanted, and report anything that vanished on its own (the local player picked it up).
///
/// Every engine call here is reached from the frame hook, never from the worker thread.
/// </summary>
internal sealed unsafe class ItemController(SharedBlockView block)
{
    /// <summary>Frames between engine mutations; spawning several in one frame stutters.</summary>
    private const int OperationCooldownFrames = 15;

    /// <summary>Frames between aliveness probes of a single slot, round-robin.</summary>
    private const int ProbeIntervalFrames = 20;

    private readonly uint[] _itemIds = new uint[SharedBlock.ItemSlotCapacity];
    private readonly uint[] _objectIds = new uint[SharedBlock.ItemSlotCapacity];
    private readonly uint[] _states = new uint[SharedBlock.ItemSlotCapacity];
    private readonly uint[] _areaIds = new uint[SharedBlock.ItemSlotCapacity];

    /// <summary>
    /// Probes to skip after spawning a slot's item. A freshly created object is not
    /// necessarily resolvable on the very next frame, and reporting "picked up" for an
    /// item that was created moments ago propagates a removal that deletes it everywhere.
    /// </summary>
    private readonly int[] _probeGrace = new int[SharedBlock.ItemSlotCapacity];
    private int _operationCooldown;
    private int _probeCooldown;
    private int _probeCursor;
    private int _resumeHoldFrames;

    /// <summary>
    /// Forgets every item body without touching the engine (the module change already
    /// freed them), and holds off respawning for about a second so the agent has time to
    /// withdraw slots whose items belong to the module that was just left. Without the
    /// hold, a wanted-but-stale slot would be respawned into the wrong module.
    /// </summary>
    public void Invalidate()
    {
        for (int i = 0; i < SharedBlock.ItemSlotCapacity; i++)
        {
            Clear(i);
            _probeGrace[i] = 0;
        }

        _resumeHoldFrames = 300;
    }

    /// <summary>
    /// Destroys one live item body through the engine, for the pre-store flush window: a
    /// replicated item left on the floor when the area is stored gets serialised into the
    /// saved module. The slot's status deliberately goes back to None, NOT Vanished —
    /// Vanished means "picked up here" and would propagate a session-wide removal.
    /// Returns false once nothing is left to destroy — a refused destroy still returns true,
    /// because the body IS still on the floor and the caller must come back for it.
    /// </summary>
    public bool FlushOneBody()
    {
        for (int slot = 0; slot < SharedBlock.ItemSlotCapacity; slot++)
        {
            if (_states[slot] != SharedBlock.ItemStateAlive)
            {
                continue;
            }

            DestroyBody(slot, "flush, before the module is stored");
            return true;
        }

        return false;
    }

    /// <summary>How many replicated item bodies are on the floor right now — what the
    /// save-time flush asks before deciding to hold a save.</summary>
    public int StandingCount
    {
        get
        {
            int standing = 0;
            for (int slot = 0; slot < SharedBlock.ItemSlotCapacity; slot++)
            {
                if (_states[slot] == SharedBlock.ItemStateAlive)
                {
                    standing++;
                }
            }

            return standing;
        }
    }

    /// <summary>
    /// Destroys every item body this controller put on the floor and reports how many are
    /// still there. The session's counterpart to <see cref="FlushOneBody"/>: a full sweep,
    /// because nothing is waiting behind teardown, and a replicated item left lying in a
    /// disconnected player's game is baked into their next save exactly like a phantom.
    /// </summary>
    public int ReleaseSession()
    {
        int standing = 0;

        for (int slot = 0; slot < SharedBlock.ItemSlotCapacity; slot++)
        {
            if (_states[slot] != SharedBlock.ItemStateAlive)
            {
                continue;
            }

            if (!DestroyBody(slot, "session released"))
            {
                standing++;
            }
        }

        return standing;
    }

    /// <summary>How many refusals before a body is written off. See PhantomController's
    /// constant of the same name: a declined destroy is normally the script VM being
    /// mid-call, which the next frame clears.</summary>
    private const int MaxDestroyAttempts = 3;

    private readonly int[] _destroyAttempts = new int[SharedBlock.ItemSlotCapacity];

    /// <summary>
    /// Destroys one slot's body.
    ///
    /// ⚠ THE SLOT SURVIVES A REFUSAL, so the next pass tries again — the same orphaning that
    /// left a peer's phantom standing after a disconnect applied here to every replicated
    /// item on the floor. The status deliberately goes back to None, NOT Vanished: Vanished
    /// means "picked up here" and would propagate a session-wide removal.
    /// </summary>
    /// <returns>True once the slot is finished with (destroyed, or written off).</returns>
    private bool DestroyBody(int slot, string reason)
    {
        uint objectId = _objectIds[slot];

        if (Odyssey.DestroyObjectById(objectId, out int failure))
        {
            PluginHost.Log($"{reason}: destroyed item body 0x{objectId:X} (item 0x{_itemIds[slot]:X})");
        }
        else if (++_destroyAttempts[slot] < MaxDestroyAttempts)
        {
            PluginHost.Log($"{reason}: destroy refused for item body 0x{objectId:X} "
                + $"(failure {failure}) — retrying ({_destroyAttempts[slot]} of {MaxDestroyAttempts})");
            return false;
        }
        else
        {
            PluginHost.Log($"{reason}: GIVING UP on item body 0x{objectId:X} (failure "
                + $"{failure}) after {MaxDestroyAttempts} attempts — it is LEFT ON THE FLOOR "
                + "and a save would bake it into the module");
        }

        _states[slot] = SharedBlock.ItemStateNone;
        _objectIds[slot] = 0;
        _destroyAttempts[slot] = 0;
        block.SetItemStatus(slot, _itemIds[slot], 0, SharedBlock.ItemStateNone, 0);
        return true;
    }

    public void OnFrame()
    {
        if (block.Command != SharedBlock.CommandSpawnAndDrive || block.PlayerCreature == 0)
        {
            return;
        }

        if (_resumeHoldFrames > 0)
        {
            _resumeHoldFrames--;
            return;
        }

        if (_operationCooldown > 0)
        {
            _operationCooldown--;
        }

        for (int i = 0; i < SharedBlock.ItemSlotCapacity; i++)
        {
            Reconcile(i);
        }

        ServicePickUpRequest();
        ProbeOneSlot();
    }

    /// <summary>
    /// Honours a test request to have the local player pick an item up, using the engine's
    /// own ActionPickUpItem - the same action a mouse click on the item produces. Exists
    /// because clicking a small object on the floor is not something the automated harness
    /// can do reliably, and the pickup path needs exercising end to end.
    /// </summary>
    private void ServicePickUpRequest()
    {
        uint request = block.PickUpRequest;
        if (request == 0)
        {
            return;
        }

        // Clear first: a request that fails must not be retried on every frame forever.
        block.PickUpRequest = 0;

        int slot = (int)request - 1;
        if (slot < 0 || slot >= SharedBlock.ItemSlotCapacity
            || _states[slot] != SharedBlock.ItemStateAlive || _objectIds[slot] == 0)
        {
            PluginHost.Log($"pickup: slot {slot} holds no live item");
            return;
        }

        uint player = Odyssey.GetPlayerObjectId();
        if (player == 0)
        {
            PluginHost.Log("pickup: no player creature");
            return;
        }

        if (Odyssey.PickUpItem(player, _objectIds[slot], out int failure))
        {
            block.PickUpIssued = block.PickUpIssued + 1;
            PluginHost.Log($"pickup: player 0x{player:X} told to take object 0x{_objectIds[slot]:X}");
        }
        else
        {
            PluginHost.Log($"pickup: refused for object 0x{_objectIds[slot]:X} (failure {failure})");
        }
    }

    private void Reconcile(int slot)
    {
        ItemSlotView desired = block.GetItemSlot(slot);

        if (!desired.Wanted)
        {
            // No longer wanted. Destroy our copy if it is still on the floor; if it
            // already vanished (picked up here), there is nothing to destroy - the item
            // now lives in somebody's inventory and must be left alone.
            if (_states[slot] == SharedBlock.ItemStateAlive && _operationCooldown == 0)
            {
                _operationCooldown = OperationCooldownFrames;

                if (!Odyssey.DestroyObjectById(_objectIds[slot], out int failure))
                {
                    PluginHost.Log($"item slot {slot}: destroy 0x{_objectIds[slot]:X} refused ({failure})");
                }
                else
                {
                    PluginHost.Log($"item slot {slot}: destroyed 0x{_objectIds[slot]:X} (item 0x{_itemIds[slot]:X})");
                }

                Clear(slot);
            }
            else if (_states[slot] != SharedBlock.ItemStateAlive && _itemIds[slot] != 0)
            {
                Clear(slot);
            }

            return;
        }

        uint wantedItemId = desired.ItemId;
        if (wantedItemId == 0)
        {
            return;
        }

        if (_itemIds[slot] == wantedItemId)
        {
            // Already materialised (alive) or already reported gone (vanished); the
            // aliveness probe handles the transition between the two.
            return;
        }

        // The slot was reassigned to a different item without being unwanted first;
        // drop the old body before spawning the new one.
        if (_states[slot] == SharedBlock.ItemStateAlive)
        {
            if (_operationCooldown != 0)
            {
                return;
            }

            _operationCooldown = OperationCooldownFrames;
            Odyssey.DestroyObjectById(_objectIds[slot], out _);
            Clear(slot);
            return;
        }

        if (_operationCooldown != 0)
        {
            return;
        }

        _operationCooldown = OperationCooldownFrames;

        int length = 0;
        byte* resref = desired.Resref;
        while (length < SharedBlock.ItemResrefLength && resref[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            return;
        }

        Vec3 position = desired.Position;
        uint objectId = Odyssey.SpawnItemOnFloor(resref, length, position, out int spawnFailure);

        if (objectId == Odyssey.ObjectInvalid)
        {
            PluginHost.Log($"item slot {slot}: spawn refused (failure {spawnFailure})");
            block.SetItemStatus(slot, wantedItemId, 0, SharedBlock.ItemStateNone, spawnFailure);
            return;
        }

        // ⚠ A NON-ZERO OBJECT ID DOES NOT MEAN THE BLUEPRINT EXISTS. CreateItemOnFloor
        // 0x00547F60 calls LoadFromTemplate at 0x0054806B and never tests its result, so a
        // resref naming no blueprint still produces a live item — one carrying nothing but
        // its constructor's defaults. The check above can therefore never fire for a typo,
        // which is exactly how a drop of `g_w_blstrpstl01` (no such resref; the real ones end
        // 001..010) spent a whole rig session looking like a successful spawn.
        if (Odyssey.TryReadItemIdentityFields(objectId, out Odyssey.ItemIdentityFields spawned)
            && spawned.LooksUnloaded)
        {
            PluginHost.Log(
                $"item slot {slot}: spawned 0x{objectId:X} but it has NO TAG and "
                + $"{spawned.Charges} charges — the blueprint almost certainly does not exist "
                + "in this install (CreateItemOnFloor does not check the template load). "
                + "The body is real but empty; expect a nameless item on the floor.");
        }

        // The engine made a stock copy of the blueprint; the peer sent us THEIR copy.
        // Reconcile the difference before anything can look at the item — but only when the
        // agent actually read the far side's state, because an unknown identity must leave
        // the item exactly as the engine made it rather than assert a stock one.
        if (desired.IdentityKnown)
        {
            bool applied = Odyssey.ApplyItemIdentity(
                objectId, desired.Upgrades, desired.StackSize,
                desired.Charges, desired.MaxCharges, desired.IdentityFlags,
                out Odyssey.ItemIdentityFields after);

            // ⚠ The success line reports what the item NOW HOLDS, read back out of the same
            // five addresses, not what the request asked for. An apply that echoes its own
            // inputs cannot distinguish "the write landed" from "the function returned true",
            // and this line is the marker an unattended verification run greps for.
            //
            // The refusal names what the resolve actually saw — no such object, an id at
            // neither shape's offset, a vtable belonging to some other class. The old wording
            // ("not a live item here") was true and useless: it is the same sentence whether
            // the item was destroyed a frame ago or the id space is wrong.
            PluginHost.Log(applied
                ? $"item slot {slot}: identity applied to 0x{objectId:X} — asked for "
                  + $"(stack {desired.StackSize}, charges {desired.Charges}/{desired.MaxCharges}, "
                  + $"upgrades 0x{desired.Upgrades:X}, flags 0x{desired.IdentityFlags:X}), "
                  + $"item now reads (stack {after.StackSize}, charges {after.Charges}/{after.MaxCharges}, "
                  + $"upgrades 0x{after.Upgrades:X}, bits 0x{after.BitFlags:X})"
                : $"item slot {slot}: identity SKIPPED for 0x{objectId:X} — {Odyssey.LastItemResolveNote}");
        }

        nint serverObject = Odyssey.ResolveServerObject(objectId);
        uint areaId = serverObject != 0 ? Odyssey.GetAreaId(serverObject) : 0;

        _itemIds[slot] = wantedItemId;
        _objectIds[slot] = objectId;
        _states[slot] = SharedBlock.ItemStateAlive;
        _areaIds[slot] = areaId;
        _probeGrace[slot] = 3;
        block.SetItemStatus(slot, wantedItemId, objectId, SharedBlock.ItemStateAlive, 0);

        PluginHost.Log(
            $"item slot {slot}: spawned item 0x{wantedItemId:X} as object 0x{objectId:X} "
            + $"at ({position.X:F2}, {position.Y:F2}, {position.Z:F2}), area 0x{areaId:X}");
    }

    /// <summary>
    /// Checks one live slot per interval for an item that left the floor.
    ///
    /// This asks the engine's own question - is the item's possessor OBJECT_INVALID -
    /// rather than inferring it from a changed area id. The area-id test did work, but it
    /// was a proxy: it also fired on a module unload and on the brief window inside
    /// CSWSItem::AddToArea where the item is momentarily area-less, either of which would
    /// have told the peer an item was picked up when it was not.
    /// </summary>
    private void ProbeOneSlot()
    {
        if (_probeCooldown > 0)
        {
            _probeCooldown--;
            return;
        }

        _probeCooldown = ProbeIntervalFrames;

        for (int step = 0; step < SharedBlock.ItemSlotCapacity; step++)
        {
            int slot = _probeCursor;
            _probeCursor = (_probeCursor + 1) % SharedBlock.ItemSlotCapacity;

            if (_states[slot] != SharedBlock.ItemStateAlive)
            {
                continue;
            }

            if (_probeGrace[slot] > 0)
            {
                _probeGrace[slot]--;
                return;
            }

            Odyssey.ItemFloorState state =
                Odyssey.GetItemFloorState(_objectIds[slot], out uint possessor);

            if (state != Odyssey.ItemFloorState.OnFloor)
            {
                string how = state == Odyssey.ItemFloorState.Held
                    ? $"taken by creature 0x{possessor:X}"
                    : $"gone (ptr 0x{Odyssey.LastItemPointer:X}, id@+0x14 0x{Odyssey.LastItemIdAtOffset14:X})";

                PluginHost.Log(
                    $"item slot {slot}: item 0x{_itemIds[slot]:X} (object 0x{_objectIds[slot]:X}) {how}");

                _states[slot] = SharedBlock.ItemStateVanished;
                _objectIds[slot] = 0;
                block.SetItemStatus(slot, _itemIds[slot], 0, SharedBlock.ItemStateVanished, 0);
            }

            return;
        }
    }

    private void Clear(int slot)
    {
        _itemIds[slot] = 0;
        _objectIds[slot] = 0;
        _states[slot] = SharedBlock.ItemStateNone;
        _areaIds[slot] = 0;
        _destroyAttempts[slot] = 0;
        block.SetItemStatus(slot, 0, 0, SharedBlock.ItemStateNone, 0);
    }
}
