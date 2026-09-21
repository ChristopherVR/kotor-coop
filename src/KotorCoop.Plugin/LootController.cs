using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// Deletes items the host has looted out of shared containers from this instance's copies, on
/// the game's thread. A chest or corpse is the same object in both saves (same GIT order =&gt;
/// same object ids), so a container's contents can be kept from duplicating: the agent declares
/// the host's (container, item) removals for the local module, and this resolves each container
/// and removes the matching item through <see cref="Odyssey.RemoveContainerItem"/>.
///
/// Removal only — never a deposit into the party, which would duplicate the item. Every resolve
/// and deref is guarded; a miss (wrong module, item already gone, container not present) is a
/// no-op, never a crash. Applying is idempotent, so the agent re-declaring the same set every
/// tick is harmless.
/// </summary>
internal sealed unsafe class LootController(SharedBlockView block)
{
    /// <summary>Frames between passes; loot removals are rare and never urgent.</summary>
    private const int IntervalFrames = 20;

    private int _countdown;
    private int _logged;

    /// <summary>Forgets everything on a module change; ids now name different objects.</summary>
    public void Invalidate()
    {
        block.LootCount = 0;
        block.SetLootMailboxConsumedCount(0);
        block.SetLootMailboxAckEpoch(block.LootMailboxEpoch);
        block.SetLootMailboxAckSerial(block.LootMailboxPageSerial);
    }

    public void OnFrame()
    {
        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;

        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        uint pageSerial = block.LootMailboxPageSerial;
        if (pageSerial != 0 && (pageSerial != block.LootMailboxAckSerial
            || block.LootMailboxEpoch != block.LootMailboxAckEpoch))
        {
            byte* module = stackalloc byte[SharedBlock.ModuleNameLength];
            int length = Odyssey.ReadCurrentModuleName(module, SharedBlock.ModuleNameLength);
            string current = length > 0
                ? System.Text.Encoding.ASCII.GetString(module, length)
                : string.Empty;

            // A page for another module is consumed as a DROP once the local engine has
            // settled in that other module. This releases the producer to publish its new
            // module page without ever applying stale object ids.
            if (current.Length > 0 && ModuleIdentity.Hash(current) != block.LootMailboxModuleHash)
            {
                block.SetLootMailboxConsumedCount(0);
                block.SetLootMailboxAckEpoch(block.LootMailboxEpoch);
                block.SetLootMailboxAckSerial(pageSerial);
            }
            else if (current.Length > 0)
            {
                uint count = Math.Min(block.LootMailboxCount,
                    (uint)SharedBlock.LootMailboxCapacity);
                for (int i = 0; i < count; i++)
                    Apply(block.GetLootMailbox(i));

                // Publish consumption only after the complete page has been walked. The
                // agent will not overwrite these slots until this serial is acknowledged.
                block.SetLootMailboxConsumedCount(count);
                block.SetLootMailboxAckEpoch(block.LootMailboxEpoch);
                block.SetLootMailboxAckSerial(pageSerial);
            }
        }

        uint legacyCount = Math.Min(block.LootCount, (uint)SharedBlock.LootCapacity);

        for (int i = 0; i < legacyCount; i++)
        {
            Apply(block.GetLoot(i));
        }
    }

    private void Apply(LootView desired)
    {
        if (desired.ContainerId is 0 || desired.ContainerId == Odyssey.ObjectInvalid
            || desired.ItemId is 0 || desired.ItemId == Odyssey.ObjectInvalid)
        {
            return;
        }

        nint placeable = Odyssey.ResolvePlaceable(desired.ContainerId);
        if (placeable == 0)
        {
            return;
        }

        if (Odyssey.RemoveContainerItem(placeable, desired.ItemId))
        {
            LogFew($"container 0x{desired.ContainerId:X}: removed looted item 0x{desired.ItemId:X} on the session's authority");
        }
    }

    private void LogFew(string message)
    {
        if (_logged < 20)
        {
            _logged++;
            PluginHost.Log(message);
        }
    }
}
