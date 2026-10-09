using KotorCoop.Protocol;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// The host is the only game that creates creatures from scripts. Wraps NWScript
/// <c>CreateObject</c> (routine 243, <c>CSWVirtualMachineCommands::ExecuteCommandCreateObject
/// 0x0052F820</c>, docs/re/spawn.md) by swapping its slot in the live routine table, which is the
/// only way the VM reaches it (no direct callers).
///
/// WHY: both games run the same story scripts, and each used to spawn its own copy of every
/// scripted creature. Object ids come from a per-game counter, so one spawn on one side only
/// shifted every later id, and the mirror (which binds a host creature to the joiner's by id)
/// found duplicates, joiner-only enemies and wrong matches; each was cleaned up afterwards, in
/// view of the player (2026-10-08/09: the Endar Spire reinforcements, the Taris Duros). Over the
/// internet the two games drift further apart, so the cleanup gets more visible.
///
/// JOINER (<see cref="SuppressCreatures"/>: any live session as a client; the follow always
/// brings a joiner back to the host's module, and its encounters are off the whole time too,
/// docs/research/combat-when-apart.md): a script's creature request is turned into "nothing created". The wrapper pops
/// <c>nObjectType</c>, pushes 0 in its place and runs the original, which pops the other three
/// arguments itself, matches no branch and returns OBJECT_INVALID: no creature, no id spent,
/// nothing to free. The host's copy reaches this game through the enemy mirror.
///
/// HOST: the creature is created as always, and its id and template go into a block ring
/// (SharedBlock.ScriptSpawns.cs) so the agent can tell joiners which blueprint to build.
///
/// Every other object type, and every call while suppression is off, runs the original with the
/// arguments untouched. The plugin's own spawns (<see cref="Odyssey.SpawnCreature"/>) call
/// 0x0052F820 directly and never come through here. MAIN THREAD ONLY (the VM runs there).
/// Kill switch: <c>host_spawns_only=0</c>.
/// </summary>
internal static unsafe class ScriptCreateObjectHook
{
    private const nint OriginalCreateObject = 0x0052F820;
    private const nint VirtualMachinePtr = 0x007A3A00;
    private const int ObjectTypeCreature = 1;
    private const uint ObjectInvalid = 0x7F000000;

    private static readonly delegate* unmanaged[Thiscall]<nint, int*, int> StackPopInteger =
        (delegate* unmanaged[Thiscall]<nint, int*, int>)0x005D1000;
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int> StackPushInteger =
        (delegate* unmanaged[Thiscall]<nint, int, int>)0x005D1010;
    private static readonly delegate* unmanaged[Thiscall]<nint, uint*, int> StackPopObject =
        (delegate* unmanaged[Thiscall]<nint, uint*, int>)0x005D10C0;
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int> StackPushObject =
        (delegate* unmanaged[Thiscall]<nint, uint, int>)0x005D10D0;

    private static nint _slot;
    private static byte* _block;
    private static int _logged;

    public static bool Enabled = true;

    /// <summary>Our wrapper's address, so <see cref="Odyssey.SpawnCreature"/>'s table check
    /// accepts the swapped slot.</summary>
    public static nint StubAddress { get; } =
        (nint)(delegate* unmanaged[Thiscall]<nint, int, int, int>)&OnCreateObject;

    public static bool Installed => _slot != 0;

    /// <summary>Creature spawns turned away on this game since start.</summary>
    public static int Suppressed { get; private set; }

    /// <summary>Swaps the table slot once the VM exists. Safe to call every frame.</summary>
    public static void TryInstall(byte* block)
    {
        _block = block;
        if (!Enabled || _slot != 0)
        {
            return;
        }

        nint slot = Odyssey.CreateObjectRoutineSlot();
        if (slot == 0 || !NativeMethods.IsReadable(slot, 4))
        {
            return;
        }

        if (*(nint*)slot != OriginalCreateObject)
        {
            Enabled = false;
            PluginHost.Log($"host spawns only: CreateObject slot holds 0x{*(nint*)slot:X}, not 0x{OriginalCreateObject:X}; not installed");
            return;
        }

        *(nint*)slot = StubAddress;
        _slot = slot;
        PluginHost.Log($"host spawns only: CreateObject wrapped at table slot 0x{slot:X}");
    }

    /// <summary>A joiner in a live session: the agent publishes the session role and clears it
    /// on a disconnect or host loss (LocalSessionRoleRule), and a stale heartbeat stops this too,
    /// so a joiner whose session ended gets its own spawns back.</summary>
    private static bool SuppressCreatures =>
        _block != null
        && LocalSessionRoleRule.TryDecode(*(uint*)(_block + SharedBlock.OffsetLocalSessionRole), out bool isHost)
        && !isHost
        && !AgentLivenessController.RemoteWritesFrozen;

    private static bool RecordSpawns =>
        _block != null
        && LocalSessionRoleRule.TryDecode(*(uint*)(_block + SharedBlock.OffsetLocalSessionRole), out bool isHost)
        && isHost;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static int OnCreateObject(nint commands, int commandId, int paramCount)
    {
        var original = (delegate* unmanaged[Thiscall]<nint, int, int, int>)OriginalCreateObject;
        nint vm = *(nint*)VirtualMachinePtr;
        int objectType;
        if (vm == 0 || StackPopInteger(vm, &objectType) == 0)
        {
            return original(commands, commandId, paramCount);
        }

        if (objectType != ObjectTypeCreature)
        {
            StackPushInteger(vm, objectType);
            return original(commands, commandId, paramCount);
        }

        // The template string is now on top of the stack; read it in place before the
        // original pops it.
        string template = ReadTemplate(Odyssey.PeekStackTopValue());

        if (SuppressCreatures)
        {
            Suppressed++;
            *(uint*)(_block + SharedBlock.OffsetSuppressedScriptCreatures) = (uint)Suppressed;
            if (_logged < 40)
            {
                _logged++;
                PluginHost.Log($"host spawns only: a script asked for creature '{template}' here - the host spawns it");
            }

            StackPushInteger(vm, 0);
            return original(commands, commandId, paramCount);
        }

        StackPushInteger(vm, objectType);
        int status = original(commands, commandId, paramCount);
        if (status == 0 && RecordSpawns && template.Length > 0)
        {
            uint objectId = ObjectInvalid;
            if (StackPopObject(vm, &objectId) != 0)
            {
                StackPushObject(vm, objectId);
                if (objectId != ObjectInvalid && objectId != 0)
                {
                    RecordSpawn(objectId, template);
                }
            }
        }

        return status;
    }

    /// <summary>Joiner: the blueprint the host named for its creature
    /// <paramref name="hostObjectId"/> (agent-written, SharedBlock.OffsetSpawnTemplateRecords).</summary>
    public static bool TryGetHostTemplate(uint hostObjectId, out string template)
    {
        template = string.Empty;
        if (_block == null || hostObjectId == 0)
        {
            return false;
        }

        uint count = Math.Min(*(uint*)(_block + SharedBlock.OffsetSpawnTemplateCount), (uint)SharedBlock.SpawnTemplateCapacity);
        for (int i = 0; i < count; i++)
        {
            byte* record = _block + SharedBlock.OffsetSpawnTemplateRecords + (i * SharedBlock.SpawnTemplateStride);
            if (*(uint*)record != hostObjectId)
            {
                continue;
            }

            Span<char> chars = stackalloc char[SharedBlock.ScriptSpawnResrefLength];
            int length = 0;
            while (length < SharedBlock.ScriptSpawnResrefLength && record[4 + length] != 0)
            {
                chars[length] = (char)record[4 + length];
                length++;
            }

            template = new string(chars[..length]);
            return length > 0;
        }

        return false;
    }

    /// <summary>A VM-owned CExoString (char* text, uint length). Empty if it does not look like a
    /// blueprint name.</summary>
    private static string ReadTemplate(nint exoString)
    {
        if (exoString == 0 || !NativeMethods.IsReadable(exoString, 8))
        {
            return string.Empty;
        }

        // Read only the string's own bytes: a short name sits at the end of its allocation, and
        // under a page heap the next byte is a guard page (2026-10-09, "n_commm").
        nint text = *(nint*)exoString;
        int limit = (int)Math.Min(*(uint*)(exoString + 4), (uint)SharedBlock.ScriptSpawnResrefLength);
        if (text == 0 || limit == 0 || !NativeMethods.IsReadable(text, (nuint)limit))
        {
            return string.Empty;
        }

        Span<char> chars = stackalloc char[SharedBlock.ScriptSpawnResrefLength];
        int length = 0;
        for (; length < limit; length++)
        {
            byte b = *(byte*)(text + length);
            if (b == 0)
            {
                break;
            }

            if (b < 0x20 || b > 0x7E)
            {
                return string.Empty;
            }

            chars[length] = (char)b;
        }

        return new string(chars[..length]).ToLowerInvariant();
    }

    private static void RecordSpawn(uint objectId, string template)
    {
        uint* count = (uint*)(_block + SharedBlock.OffsetScriptSpawnCount);
        byte* record = _block + SharedBlock.OffsetScriptSpawnRecords
            + (int)(*count % SharedBlock.ScriptSpawnCapacity) * SharedBlock.ScriptSpawnStride;
        *(uint*)record = 0; // id last: the agent skips a record whose id is 0
        *(uint*)(record + 4) = *(uint*)(_block + SharedBlock.OffsetLivenessNativeEpoch);
        for (int i = 0; i < SharedBlock.ScriptSpawnResrefLength; i++)
        {
            record[8 + i] = i < template.Length ? (byte)template[i] : (byte)0;
        }

        *(uint*)record = objectId;
        *count += 1;
    }
}
