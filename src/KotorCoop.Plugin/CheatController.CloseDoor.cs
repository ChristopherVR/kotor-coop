using static KotorCoop.Plugin.Vec3;

namespace KotorCoop.Plugin;

internal sealed unsafe partial class CheatController
{
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
