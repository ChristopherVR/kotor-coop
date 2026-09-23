namespace KotorCoop.Plugin;

/// <summary>
/// Moves <see cref="ItemTemplateSpawnHook"/>'s captures into the shared block's ring, every
/// frame — see that class's own remarks for what it captures and why. Plugin -&gt; agent only:
/// nothing on the agent side ever needs to hand one of these back to a plugin, unlike barks or
/// peer effects, so there is no IN half to this channel.
/// </summary>
internal sealed unsafe class ItemTemplateSpawnController
{
    private readonly SharedBlockView _block;

    public ItemTemplateSpawnController(SharedBlockView block)
    {
        _block = block;
        _block.AdvanceItemTemplateSpawnGeneration();
    }

    /// <summary>Highest hook-side serial already copied into the block's ring, so a slot the
    /// hook has not touched again since the last pass is not republished. Same dedup shape as
    /// <see cref="BarkController"/>'s own <c>Serials</c> comparison, simplified: this direction
    /// never needs to remember (speaker, serial) pairs because nothing here can arrive twice
    /// from two different slots the way a replayed bark can.</summary>
    private uint _lastPublishedSerial;

    /// <summary>A module change reassigns every object id; anything the hook captured but this
    /// had not yet drained would name a stranger by the time the agent could read it.</summary>
    public void Invalidate()
    {
        _block.AdvanceItemTemplateSpawnGeneration();
        ItemTemplateSpawnHook.Invalidate();
        _lastPublishedSerial = 0;
    }

    public void OnFrame()
    {
        Span<byte> resRef = stackalloc byte[ItemTemplateSpawnHook.ResRefBytes];
        uint highest = _lastPublishedSerial;

        // Walk every slot rather than tracking which ones wrapped since last pass: the ring is
        // small (ItemTemplateSpawnHook.Capacity) and this runs once a frame, so a full scan
        // costs nothing worth avoiding here.
        for (int i = 0; i < ItemTemplateSpawnHook.Capacity; i++)
        {
            ItemTemplateSpawnHook.Read(
                i, out uint objectId, out uint serial, out uint worldGeneration, out uint worldLoadSerial,
                out bool isCheat, out ushort stackCount, resRef);

            if (serial == 0 || serial <= _lastPublishedSerial || objectId == 0 || worldGeneration == 0)
            {
                continue;
            }

            _block.PublishItemTemplateSpawn(
                objectId, resRef, isCheat, stackCount, worldGeneration, worldLoadSerial);

            if (serial > highest)
            {
                highest = serial;
            }
        }

        _lastPublishedSerial = highest;
    }
}
