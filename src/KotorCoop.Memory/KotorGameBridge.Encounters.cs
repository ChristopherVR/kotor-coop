// Encounter and generic-trigger sync: disarming local encounters (runtime-only, never
// serialised), watching which encounters/triggers a peer has entered, and relaying fire
// requests to the plugin.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ---- encounters ------------------------------------------------------------

    /// <summary>CSWSEncounter vtable.</summary>
    public const uint EncounterVtable = 0x00749D98;

    /// <summary>
    /// CSWSEncounter + this = active dword (symbol database: active).
    ///
    /// ⚠ NEVER WRITE THIS. It is kept named so the next person recognises it rather than
    /// rediscovering it: <c>SaveEncounter</c> (0x591350) serialises this field into the
    /// GIT, so a write here follows the player into their save and disarms the encounter
    /// permanently, in single-player too. Disarm through <see cref="EncounterStarted"/>,
    /// which is runtime-only. See <see cref="DisableLocalEncounters"/>.
    /// </summary>
    private const int EncounterActive = 0x238;

    /// <summary>CSWSEncounter + this = started dword (symbol database: started).</summary>
    private const int EncounterStarted = 0x278;

    private string _encounterModule = string.Empty;
    private long _encounterDisarmedAtMs;

    /// <summary>
    /// How often the disarm is re-asserted within one module. It is a LEVEL now, not a
    /// one-shot: <c>started</c> is the engine's own "this one has fired" latch and the
    /// binary was not exhaustively swept for a path that clears it (a respawn timer is the
    /// plausible one), so re-writing it a few times a minute costs a cached scan and removes
    /// the question. Cheap because the scan is cached and the writes are plain dwords.
    /// </summary>
    private const int EncounterDisarmRefreshMs = 15_000;

    /// <summary>
    /// Turns off every encounter in the local module. Called on the instance that does NOT
    /// own enemies. Encounter spawns are the one engine-side source of object-id drift — a
    /// creature spawned by an encounter in one instance and not the other shifts the id
    /// counter for everything created afterwards, and identical ids are the key the whole
    /// combat replication rests on. The host keeps its encounters, and a peer that walks into
    /// one asks the owner to fire it rather than firing its own (see
    /// <see cref="TryRequestEncounterFire"/>).
    ///
    /// ⚠ ONLY <c>started</c> IS WRITTEN, AND THAT IS DELIBERATE — it used to write
    /// <c>active = 0</c> as well, which POISONED THE PLAYER'S SAVE.
    /// <c>CSWSEncounter::SaveEncounter</c> (0x591350) serialises <c>active</c> (+0x238) into
    /// the GIT and does NOT serialise <c>started</c> (+0x278) — verified by disassembly, the
    /// function references +0x238 and +0x23C and nothing else from that pair. So a client
    /// who saved while joined wrote "inactive" into their own save for every encounter in
    /// the module, permanently, and those encounters never fired again in single-player
    /// either. <c>started</c> is runtime-only and dies with the module.
    ///
    /// One is enough: <c>CSWSEncounter::SpawnIfAppropriate</c> (0x593BE0) early-outs on
    /// <c>active == 0</c> OR <c>started == 1</c>, and sets <c>started = 1</c> itself
    /// (0x593C39) when it does spawn — so writing it is exactly claiming the encounter has
    /// already fired.
    ///
    /// ⚠ This comment used to claim that whatever the owner's encounters spawn "reaches the
    /// client through the enemy channel". It does not. Both mirror controllers begin with
    /// <c>ResolveServerObject(id)</c> and give up on 0, so a creature that exists only in the
    /// owner's instance is skipped — see docs/research/client-encounters.md §8, which is
    /// where the missing materialisation path is designed.
    ///
    /// Plain dword writes, so this runs externally.
    /// </summary>
    public int DisableLocalEncounters()
    {
        if (!EngineGlobals.TryReadModuleName(_memory, out string module) || module.Length == 0)
        {
            return 0;
        }

        // ⚠ NEVER WHILE A TRANSITION IS IN FLIGHT. An "areaNN" name is what the engine reads
        // as while the module is being torn down and rebuilt, and every encounter the scan
        // knows about belongs to the world being freed. Measured: a client crashed one frame
        // into a follow-travel with eip in the HEAP — a corrupted call target, which is what
        // writing into recycled memory produces.
        if (IsTransitionalModuleName(module))
        {
            return 0;
        }

        long nowMs = Environment.TickCount64;
        bool sameModule = string.Equals(module, _encounterModule, StringComparison.OrdinalIgnoreCase);

        if (sameModule && nowMs - _encounterDisarmedAtMs < EncounterDisarmRefreshMs)
        {
            return 0;
        }

        // ⚠ AND NEVER FROM A LIST THAT PREDATES THE WORLD. The scan cache lives for 30 s,
        // which is longer than a module lasts in a bad case and longer than the refresh
        // interval below — so a new module would otherwise be "disarmed" by writing into the
        // previous one's freed objects.
        if (!sameModule)
        {
            _scanCache.Remove(EncounterVtable);
        }

        _encounterDisarmedAtMs = nowMs;

        int disabled = 0;

        foreach (nint address in CachedScan(EncounterVtable, 128, ObjectScanCacheMs))
        {
            // ⚠ RE-VALIDATE THE VTABLE, not just the id. These addresses can be up to 30 s
            // old, and an object freed in between may have been recycled into something else
            // whose bytes at +0x04 happen to look like a plausible object id. The vtable is
            // what makes this an encounter, so it is what has to still be there before we
            // write into the thing.
            if (!_memory.TryReadUInt32(address, out uint vtable) || vtable != EncounterVtable)
            {
                continue;
            }

            // Sanity: a real encounter has its object id where CSWSObject keeps it.
            if (!_memory.TryReadUInt32(address + OffObjectId, out uint objectId)
                || objectId is 0 or >= 0x7F000000)
            {
                continue;
            }

            if (_memory.TryWrite(address + EncounterStarted, 1))
            {
                disabled++;
            }
        }

        // Remember the module even if there were no encounters, so the scan is not
        // repeated every pump for a module that simply has none.
        _encounterModule = module;

        // A refresh pass re-asserts the same encounters it already disarmed; reporting them
        // again would have the agent log "disabled N encounter(s)" every 15 seconds forever.
        return sameModule ? 0 : disabled;
    }

    // Client-triggered encounters, mirroring SharedBlock 0x1DE0..0x1E60. The plugin owns
    // both halves (docs/research/client-encounters.md); the bridge only carries them.
    private const int BlockEncounterWatch = 0x1DE0;
    private const int BlockEncounterInsideCount = 0x1DE4;
    private const int BlockEncounterInside = 0x1DEC;
    private const int BlockEncounterInsideCapacity = 4;
    private const int BlockEncounterRequestCount = 0x1DFC;
    private const int BlockEncounterRequestSerial = 0x1E00;
    private const int BlockEncounterRequests = 0x1E04;
    private const int BlockEncounterRequestStride = 0x08;
    private const int BlockEncounterRequestCapacity = 8;

    private uint _encounterRequestSerial;

    private bool _encounterWatchWritten;
    private bool _encounterWatchState;

    public void SetEncounterWatch(bool enabled)
    {
        // Edge-written: this is a per-pump call and the value changes at most once a session.
        if (_pluginBlock == 0 || (_encounterWatchWritten && _encounterWatchState == enabled))
        {
            return;
        }

        if (_memory.TryWrite(_pluginBlock + BlockEncounterWatch, enabled ? 1u : 0u))
        {
            _encounterWatchWritten = true;
            _encounterWatchState = enabled;
        }
    }

    public IReadOnlyList<uint> ReadEnteredEncounters()
    {
        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockEncounterInsideCount, out uint count)
            || count == 0)
        {
            return [];
        }

        count = Math.Min(count, BlockEncounterInsideCapacity);

        List<uint> ids = new((int)count);
        for (int i = 0; i < count; i++)
        {
            if (_memory.TryReadUInt32(_pluginBlock + BlockEncounterInside + (i * 4), out uint id)
                && id != 0)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    public bool TryRequestEncounterFire(IReadOnlyList<(uint EncounterId, uint PeerId)> requests)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        int count = Math.Min(requests.Count, BlockEncounterRequestCapacity);

        bool ok = true;
        for (int i = 0; i < count; i++)
        {
            nint slot = _pluginBlock + BlockEncounterRequests + (i * BlockEncounterRequestStride);
            ok &= _memory.TryWrite(slot + 0x00, requests[i].EncounterId);
            ok &= _memory.TryWrite(slot + 0x04, requests[i].PeerId);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockEncounterRequestCount, (uint)count);

        // Serial last and always advancing, even for an unchanged list: the plugin only
        // acts when the serial moves, and firing is idempotent (the engine refuses an
        // encounter that has already started), so re-offering costs nothing and covers a
        // request that arrived while the plugin was mid-transition.
        ok &= _memory.TryWrite(_pluginBlock + BlockEncounterRequestSerial, ++_encounterRequestSerial);
        return ok;
    }

    // TriggerRelay, mirroring SharedBlock's 0x3B88..0x3BD8 tail-slack region (added post
    // block-version-44 without growing TotalSize — see SharedBlock.cs). The plugin owns both
    // halves (docs/research/trigger-relay.md); the bridge only carries them, exactly like the
    // encounter region above.
    private const int BlockTriggerWatch = 0x3B88;
    private const int BlockTriggerInsideCount = 0x3B8C;
    private const int BlockTriggerInside = 0x3B94;
    private const int BlockTriggerInsideCapacity = 4;
    private const int BlockTriggerRequestCount = 0x3BA4;
    private const int BlockTriggerRequestSerial = 0x3BA8;
    private const int BlockTriggerRequests = 0x3BAC;
    private const int BlockTriggerRequestStride = 0x08;
    private const int BlockTriggerRequestCapacity = 4;

    // Native-fire report (protocol 36), fit into the 40 bytes of tail-slack the version-44
    // comment above already measured and the trigger-relay region (ending 0x3BD8) did not use
    // — see SharedBlock.cs. No TotalSize growth.
    private const int BlockTriggerNativeFiredCount = 0x3BD8;
    private const int BlockTriggerNativeFiredSerial = 0x3BDC;
    private const int BlockTriggerNativeFired = 0x3BE0;
    private const int BlockTriggerNativeFiredCapacity = 4;

    private uint _triggerRequestSerial;

    private bool _triggerWatchWritten;
    private bool _triggerWatchState;

    public void SetTriggerWatch(bool enabled)
    {
        if (_pluginBlock == 0 || (_triggerWatchWritten && _triggerWatchState == enabled))
        {
            return;
        }

        if (_memory.TryWrite(_pluginBlock + BlockTriggerWatch, enabled ? 1u : 0u))
        {
            _triggerWatchWritten = true;
            _triggerWatchState = enabled;
        }
    }

    public IReadOnlyList<uint> ReadEnteredTriggers()
    {
        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockTriggerInsideCount, out uint count)
            || count == 0)
        {
            return [];
        }

        count = Math.Min(count, BlockTriggerInsideCapacity);

        List<uint> ids = new((int)count);
        for (int i = 0; i < count; i++)
        {
            if (_memory.TryReadUInt32(_pluginBlock + BlockTriggerInside + (i * 4), out uint id)
                && id != 0)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    /// <summary>Protocol 36: the plugin's own recent, filtered, natively-fired trigger ids —
    /// see <see cref="IGameBridge.ReadNativeFiredTriggers"/>.</summary>
    public IReadOnlyList<uint> ReadNativeFiredTriggers()
    {
        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockTriggerNativeFiredCount, out uint count)
            || count == 0)
        {
            return [];
        }

        count = Math.Min(count, (uint)BlockTriggerNativeFiredCapacity);

        List<uint> ids = new((int)count);
        for (int i = 0; i < count; i++)
        {
            if (_memory.TryReadUInt32(_pluginBlock + BlockTriggerNativeFired + (i * 4), out uint id)
                && id != 0)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    public bool TryRequestTriggerFire(IReadOnlyList<(uint TriggerId, uint PeerId)> requests)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        int count = Math.Min(requests.Count, BlockTriggerRequestCapacity);

        bool ok = true;
        for (int i = 0; i < count; i++)
        {
            nint slot = _pluginBlock + BlockTriggerRequests + (i * BlockTriggerRequestStride);
            ok &= _memory.TryWrite(slot + 0x00, requests[i].TriggerId);
            ok &= _memory.TryWrite(slot + 0x04, requests[i].PeerId);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockTriggerRequestCount, (uint)count);

        // Serial last, always advancing even for an unchanged list — same reasoning as
        // TryRequestEncounterFire: the plugin only acts when the serial moves, and every id
        // handed here already passed TriggerRelayStore's own idempotence check, so a repeat
        // list (there should never be one) would still cost nothing.
        ok &= _memory.TryWrite(_pluginBlock + BlockTriggerRequestSerial, ++_triggerRequestSerial);
        return ok;
    }

}
