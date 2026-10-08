using static KotorCoop.Plugin.Vec3;

namespace KotorCoop.Plugin;

internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// The Security action on a locked door, exactly as the retail target menu sends it
    /// (<c>CSWCDoor</c> menu action 0x00683E50 calls <c>SendPlayerToServerInput_UnlockObject</c>
    /// with the door id and OBJECT_INVALID for "no particular tool"). The server walks the
    /// controlled creature over and rolls its skill; a lock that needs a better character
    /// stays shut, which is the point of a playtest.
    /// </summary>
    private void UnlockDoor(nint player)
    {
        uint id = block.CheatTarget;
        nint door = id is 0 or Odyssey.ObjectInvalid ? 0 : Odyssey.ResolveDoor(id);
        if (door == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, $"unlockdoor: 0x{id:X} is not a door in this world");
            return;
        }

        if (!Odyssey.IsDoorLocked(door))
        {
            Answer(SharedBlock.CheatStatusRefused, $"unlockdoor: door 0x{id:X} is not locked");
            return;
        }

        float distance = player != 0 ? Distance(Odyssey.GetPosition(player), Odyssey.GetPosition(door)) : 0f;
        // The server walks the creature over with its own pathfinding before it rolls (seen live
        // from 11 m), so the reach is wider than usedoor's 40 m; the Endar Spire's Security
        // tutorial door sits 42 m from the room the story leaves the player in.
        if (distance > 80f)
        {
            Answer(SharedBlock.CheatStatusRefused, $"unlockdoor: door 0x{id:X} is {distance:F1}m away, past the 80m limit");
            return;
        }

        bool sent = Odyssey.UnlockObject(id);
        Answer(sent ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            sent ? $"door 0x{id:X}: native UnlockObject (Security) sent" : $"door 0x{id:X}: could not send UnlockObject");
    }

    private void SetLock()
    {
        uint id = block.CheatTarget;
        bool locked = block.CheatAmount != 0;
        if (id is 0 or Odyssey.ObjectInvalid)
        {
            Answer(SharedBlock.CheatStatusNoTarget, "setlock requires a real door or placeable id");
            return;
        }

        nint door = Odyssey.ResolveDoor(id);
        nint placeable = door == 0 ? Odyssey.ResolvePlaceable(id) : 0;
        if (door == 0 && placeable == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, $"0x{id:X} is not a door or placeable in this world");
            return;
        }

        bool before = door != 0 ? Odyssey.IsDoorLocked(door) : Odyssey.IsPlaceableLocked(placeable);
        if (door != 0)
        {
            Odyssey.SetDoorLocked(door, locked);
        }
        else if (locked)
        {
            Odyssey.LockPlaceable(placeable);
        }
        else
        {
            Odyssey.UnlockPlaceable(placeable);
        }

        bool after = door != 0 ? Odyssey.IsDoorLocked(door) : Odyssey.IsPlaceableLocked(placeable);
        string kind = door != 0 ? "door" : "placeable";
        PluginHost.Log($"{kind}: lock 0x{id:X} ({before} -> {after})");
        Answer(after == locked ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            $"{kind} 0x{id:X}: locked={after}");
    }

    // Developer staging uses the same SetOpenState/MoveToNextOpenState path as script
    // closes and door mirroring. A raw open-state write would not update the walkmesh.
    private void CloseDoor(nint player)
    {
        uint requestedId = block.CheatTarget;
        string text = ReadCheatText();
        nint door;
        if (requestedId != 0)
        {
            door = Odyssey.ResolveDoor(requestedId);
        }
        else if (text.Length > 0)
        {
            door = Odyssey.FindDoorByTag(block.CheatText, text.Length);
        }
        else
        {
            door = FindNearestAnyDoor(player, out _);
        }

        float distance = door != 0 ? Distance(Odyssey.GetPosition(player), Odyssey.GetPosition(door)) : 0f;
        DoorUseVerdict verdict = DoorUse.Decide(
            hasPlayer: player != 0,
            found: door != 0,
            isDoor: door != 0,
            linked: true,
            openState: door != 0 ? Odyssey.GetDoorOpenState(door) : 0,
            transitionPending: Odyssey.IsAreaTransitionPending(),
            moviePlaying: Odyssey.IsMoviePlaying(),
            distance: distance);
        if (verdict != DoorUseVerdict.Use)
        {
            Answer(SharedBlock.CheatStatusRefused, DoorUse.Explain(verdict, "the requested door", distance));
            return;
        }

        uint doorId = *(uint*)(door + 0x04);
        byte before = Odyssey.GetDoorOpenState(door);
        Odyssey.SetDoorOpenState(door, 0);
        Odyssey.CommitDoorOpenState(door);
        byte after = Odyssey.GetDoorOpenState(door);
        PluginHost.Log($"door: closed 0x{doorId:X} (state {before} -> {after}) at {distance:F1}m");
        Answer(after == 0 ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            after == 0 ? $"door 0x{doorId:X}: closed" : $"door 0x{doorId:X}: close refused (state {after})");
    }
}
