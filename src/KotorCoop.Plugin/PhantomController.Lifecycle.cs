namespace KotorCoop.Plugin;

// Phantom lifecycle: Invalidate (module-change wipe), DestroySlot (engine-side teardown
// with retry) and TrySpawnOne (blueprint spawn + dress-and-name), plus the constants and
// helpers only they use. See PhantomController.cs for the per-slot state these share with
// the drive/transition-flush partials.
internal sealed unsafe partial class PhantomController
{
    /// <summary>
    /// Blueprints tried in order for a phantom body. The co-op blueprint shipped in
    /// override (min-1-hp, neutral, no stock AI, equip proficiencies) is preferred; a
    /// generic male commoner is the fallback for an install without the override file.
    /// A resref a mod removed simply drops to the next; only when none resolve does
    /// spawning latch off.
    /// </summary>
    private static readonly string[] Templates = ["k_coop_phantom", "n_commm"];

    /// <summary>Consecutive spawn attempts that produced no body before phantoms are
    /// declared unavailable and the retry stops. Without this a missing blueprint retries
    /// every 30 frames forever, hammering the log on the game thread.</summary>
    private const int MaxSpawnFailures = 5;

    private int _spawnFailures;

    /// <summary>
    /// Forgets every phantom without touching the engine. Called when the module has
    /// changed (or is changing): the objects behind these pointers are freed by
    /// UnloadModule, so destroying them is neither possible nor necessary — the first
    /// frame in the new world just spawns fresh bodies.
    /// </summary>
    public void Invalidate()
    {
        for (int i = 0; i < _phantoms.Length; i++)
        {
            if (_phantoms[i] != 0)
            {
                Odyssey.ForgetGait(_phantoms[i]);
            }

            // A new world means the next position is not a continuation of the last one:
            // start the smoothing from wherever the peer actually is rather than gliding
            // across from a stale spot. ClearSlot resets all of that.
            ClearSlot(i);
        }

        PublishPhantomCount();

        // A new module may ship the blueprint the last one lacked, and a transient failure
        // clears — so give spawning a fresh start rather than staying latched off forever.
        _spawnFailures = 0;
        _spawnDisabled = false;
    }

    /// <summary>How many times a refused destroy is retried before the body is written off.
    /// A refusal is normally transient — <c>DestroyObjectById</c> declines outright while the
    /// script VM is mid-call (failure 4), and a disconnect lands on an arbitrary frame — so
    /// one attempt was never enough. Bounded, because the pre-store flush window is finite
    /// and a wedged transition is worse than a leaked body.</summary>
    private const int MaxDestroyAttempts = 3;

    /// <summary>Refusals so far per slot; reset with the slot.</summary>
    private readonly int[] _destroyAttempts = new int[SharedBlock.SlotCapacity];

    /// <summary>
    /// Destroys one slot's body through the engine and forgets everything derived from it.
    ///
    /// ⚠ THE SLOT SURVIVES A REFUSAL. It used to be cleared whichever way the destroy went,
    /// so a single declined call — which is what the VM returns whenever it is mid-script —
    /// orphaned the body permanently: the controller forgot it, nothing retried, and a
    /// coop_phantom stood in the player's game until they reloaded. Now a refusal keeps the
    /// slot so the next pass tries again, and only <see cref="MaxDestroyAttempts"/> failures
    /// write it off — loudly, because at that point a save really would bake it in.
    ///
    /// The slot is CLEARED, never compacted. A slot belongs to one peer for as long as that
    /// peer is in the block, so sliding the survivors down would hand a departing player's
    /// name, gear, faction state and cumulative damage total to whoever ended up in their
    /// index - which is precisely the class of bug three players makes reachable and two
    /// never could.
    /// </summary>
    /// <returns>True once the slot is finished with (destroyed, or written off).</returns>
    private bool DestroySlot(int slot, string reason)
    {
        uint objectId = _phantomIds[slot];

        if (_phantoms[slot] != 0)
        {
            Odyssey.ForgetGait(_phantoms[slot]);
        }

        if (Odyssey.DestroyObjectById(objectId, out int failure))
        {
            PluginHost.Log($"{reason}: destroyed phantom 0x{objectId:X} (slot {slot})");
            ClearSlot(slot);
            PluginEvents.RaisePeerSlotDestroyed(slot, objectId);
            return true;
        }

        if (++_destroyAttempts[slot] < MaxDestroyAttempts)
        {
            PluginHost.Log($"{reason}: destroy refused for phantom 0x{objectId:X} "
                + $"(slot {slot}, failure {failure}) — retrying "
                + $"({_destroyAttempts[slot]} of {MaxDestroyAttempts})");
            return false;
        }

        PluginHost.Log($"{reason}: GIVING UP on phantom 0x{objectId:X} (slot {slot}, "
            + $"failure {failure}) after {MaxDestroyAttempts} attempts — the body is LEFT "
            + "STANDING and a save would bake it into the module");
        ClearSlot(slot);
        PluginEvents.RaisePeerSlotDestroyed(slot, objectId);
        return true;
    }

    /// <summary>The old spawn spot: beside the local player, fanned out per SLOT so two
    /// peers that spawn moments apart never land on the same point.</summary>
    private static Vec3 Offset(Vec3 playerPosition, int slot) => new()
    {
        X = playerPosition.X + 2.5f + (slot * 1.5f),
        Y = playerPosition.Y + 1.5f,
        Z = playerPosition.Z,
    };

    /// <summary>Rejects the all-zero position a slot carries before its first update, and any
    /// NaN/infinity a bad read would produce.</summary>
    private static bool IsPlausiblePosition(Vec3 p) =>
        float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z)
        && (p.X != 0f || p.Y != 0f);

    private void TrySpawnOne(int slot, nint player)
    {
        if (!_modeLogged)
        {
            _modeLogged = true;
            PluginHost.Log($"phantom movement: {(_directMovement ? "direct (seamless)" : "walk")}");
        }

        // Spawn the body WHERE THE PEER ACTUALLY IS. This used to spawn at a fixed offset
        // from the local player and let the walk logic carry it to the real position, which
        // meant every phantom appeared next to you and then jogged across the room — the
        // "initial positions are not synced" report. The peer is standing on their position,
        // so it is by definition a valid walkmesh point, and ComputeSafeLocation inside
        // CreateObject still searches outward from it.
        //
        // The local player is the fallback for the case where the slot has no plausible
        // position yet (a peer that has connected but not yet published one).
        RemoteView remote = block.GetRemote(slot);
        Vec3 peerPosition = remote.Position;

        Vec3 spawnPosition = IsPlausiblePosition(peerPosition)
            ? peerPosition
            : Offset(Odyssey.GetPosition(player), slot);

        Vec3 facing = new() { X = 1f, Y = 0f, Z = 0f };

        // Try each blueprint until one resolves. A missing resref (a mod removed the
        // co-op blueprint, or even the stock commoner) returns failure 9; only when every
        // template fails does spawning give up.
        uint objectId = Odyssey.ObjectInvalid;
        int failure = 0;
        foreach (string template in Templates)
        {
            objectId = Odyssey.SpawnCreature(template, spawnPosition, facing, out failure);
            if (objectId != Odyssey.ObjectInvalid)
            {
                break;
            }
        }

        block.LastStatus = failure;

        if (objectId == Odyssey.ObjectInvalid && IsPlausiblePosition(peerPosition))
        {
            // The peer's own position refused a body (a door frame, a lift, mid-transition
            // geometry). Fall back to the old offset-from-the-player spot rather than losing
            // the phantom entirely — a phantom in the wrong place beats no phantom.
            Vec3 fallback = Offset(Odyssey.GetPosition(player), slot);
            foreach (string template in Templates)
            {
                objectId = Odyssey.SpawnCreature(template, fallback, facing, out failure);
                if (objectId != Odyssey.ObjectInvalid)
                {
                    break;
                }
            }

            block.LastStatus = failure;
        }

        if (objectId == Odyssey.ObjectInvalid)
        {
            // Latch after a few consecutive misses so a blueprint no install has does not
            // retry every 30 frames forever, writing to the log on the game thread each
            // time. Invalidate() (a module change) clears the latch for a fresh try.
            if (++_spawnFailures >= MaxSpawnFailures && !_spawnDisabled)
            {
                _spawnDisabled = true;
                PluginHost.Log(
                    $"phantoms unavailable: no blueprint spawned after {_spawnFailures} tries "
                    + $"(last failure {failure}); is a phantom blueprint present?");
            }
            else if (_spawnFailures < MaxSpawnFailures)
            {
                PluginHost.Log($"spawn refused (failure code {failure}); retry {_spawnFailures}/{MaxSpawnFailures}");
            }

            return;
        }

        _spawnFailures = 0;

        // CreateObject returns an id, not a pointer. Resolve it by locating the creature
        // whose object id matches; the plugin is in-process so this is a direct read.
        nint creature = CreatureFinder.FindByObjectId(objectId);
        if (creature == 0)
        {
            PluginHost.Log($"spawned id 0x{objectId:X} but could not resolve its object");
            return;
        }

        Odyssey.ClaimCreature(creature);

        // A phantom belongs to a remote player, so this instance must not be able to fight
        // it or kill it. Done before the creature is ever driven, because the window where
        // a fresh creature stands next to the party is exactly when something shoots it.
        Odyssey.MarkAsRemotePlayer(creature);

        // Dress the phantom as its remote player immediately, so it never renders a single
        // frame as the commoner blueprint. This is a HALF measure by construction — the client
        // twin does not exist yet, and the twin is what renders — so ApplyAppearance leaves
        // _twinDressed clear and the drive loop finishes the job the moment the twin appears.
        // Before that latch existed this call was the ONLY one that ever ran after a re-spawn,
        // which is why a peer came out of a warp wearing the blueprint. Also note the row is
        // the peer's own, or the last one that peer published (PeerIdentityRule) — a peer
        // mid-transition publishes 0, and 0 must never mean "use whatever the body has".
        RemoteView owner = block.GetRemote(slot);
        _phantomIds[slot] = objectId;
        if (owner.Active)
        {
            ApplyAppearance(slot, creature, owner.Appearance);
        }

        // Name it after its player, same reasoning: set before the twin exists so every
        // copy the engine makes carries it. Without a first name the selection band above
        // the phantom is blank (trap 16) and clicking a teammate shows nothing.
        byte* name = block.GetRemoteName(slot);
        int nameLength = 0;
        while (nameLength < SharedBlock.NameLength - 1 && name[nameLength] != 0)
        {
            nameLength++;
        }

        if (nameLength > 0)
        {
            Odyssey.SetCreatureFirstName(creature, name, nameLength);
        }

        _phantoms[slot] = creature;
        _phantomIds[slot] = objectId;
        block.SetPhantomId(slot, objectId);
        PublishPhantomCount();

        // From here on, damage landing on this body is the peer's to take.
        PhantomHitHook.Watch(slot, objectId);

        // The identity fields go in the spawn line because "what does this body look like, and
        // who says so" is the first question a wrong-look report asks and nothing logged it.
        // Server row and twin row are BOTH here on purpose: they are the two halves that can
        // disagree (see ApplyAppearance), and a line carrying one of them proves nothing.
        ushort serverRow = *(ushort*)(creature + CreAppearanceType);
        ushort portrait = *(ushort*)(creature + ObjPortraitId);
        string twinRow = Odyssey.TryGetTwinAppearanceRow(creature, out ushort renderedRow)
            ? renderedRow.ToString()
            : "no twin yet";

        PluginHost.Log(
            $"spawned phantom in slot {slot}: id 0x{objectId:X} at 0x{creature:X} "
            + $"({spawnPosition.X:F2}, {spawnPosition.Y:F2}, {spawnPosition.Z:F2}) "
            + $"appearance {serverRow} (peer published {owner.Appearance}, renders {twinRow}), "
            + $"portrait {portrait}");
        PluginEvents.RaisePeerSlotSpawned(slot, objectId);
    }
}
