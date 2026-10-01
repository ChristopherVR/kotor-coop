namespace KotorCoop.Plugin;

internal sealed unsafe partial class PhantomController
{
    private static bool IsCurrentBody(nint body, uint objectId)
    {
        nint current = Odyssey.ResolveServerObject(objectId);
        uint* header = stackalloc uint[2];
        ushort appearance = 0;
        bool headerReadable = body != 0 && CreatureFinder.TryReadSelf(body, (byte*)header, 8);
        bool appearanceReadable = body != 0 && CreatureFinder.TryReadSelf(
            body + CreAppearanceType, (byte*)&appearance, sizeof(ushort));
        return PhantomBodyIdentityRule.IsCurrent(body, current, headerReadable, appearanceReadable,
            headerReadable ? header[0] : 0, headerReadable ? header[1] : 0, objectId,
            Odyssey.GetPlayerCreaturePointer());
    }

    private void ForgetStaleSlot(int slot)
    {
        uint objectId = _phantomIds[slot];
        // Both operations forget local tracking only. Never destroy by the stale ID:
        // its current occupant might be a legitimate newly-created creature.
        Odyssey.ForgetGait(_phantoms[slot]);
        ClearSlot(slot);
        PublishPhantomCount();
        PluginEvents.RaisePeerSlotDestroyed(slot, objectId);
        PluginHost.Log($"phantom slot {slot}: forgot stale body 0x{objectId:X}; current object identity changed or became unreadable");
    }
}
