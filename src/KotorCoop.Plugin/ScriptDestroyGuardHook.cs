using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// On a JOINER, stops this game's own scripts from destroying a creature the host owns and
/// still has alive — the host decides whether it leaves.
///
/// THE BUG (live 2026-09-26, Dantooine courtyard): after adopting the host's save, Rilka
/// (<c>dan14_settlerf</c>, object 0xE3) was present on the joiner for about two seconds, then gone,
/// while she stood alive on the host. Her user-defined event script (<c>k_pdan_rilka_d</c>:
/// <c>ActionForceMoveToObject</c> to a waypoint, then <c>ActionDoCommand(DestroyObject(self))</c>)
/// fired on the joiner's freshly loaded world against the joiner's own local state; the host,
/// whose world was never reloaded, never ran it. The enemy mirror then found the host's 0xE3
/// missing and stood a generic stand-in body in her place — "another NPC with half HP".
///
/// THE SPLICE: the NWScript <c>DestroyObject</c> handler
/// (<c>CSWVirtualMachineCommands::ExecuteCommandDestroyObject 0x0052FF20</c>) ends by posting the
/// destroy event (<c>[0x007441E0]</c> = 11) through <c>CServerAIMaster::AddEventDeltaTime
/// 0x004B08D0</c> at 0x0053015C (thiscall, six stack args: days, time, source, target, event id,
/// data; <c>ret 0x18</c>; docs/research/mines.md §6, creature-liveness.md). That one call is
/// redirected here. A destroy aimed at a protected id is dropped — nothing was posted, so nothing
/// dies — and every other call goes to the original unchanged. Only this call site: engine-internal
/// removals (corpse decay, encounter cleanup) never pass through it.
///
/// PROTECTED = the host's census entries the joiner's enemy mirror saw ALIVE in its last pass
/// (<see cref="SetProtected"/>, stamped; stale after <see cref="StaleMs"/> so an ended session
/// protects nothing). The plugin's own <see cref="Odyssey.DestroyObjectById"/> (a removal the
/// HOST decided, relayed) sets <see cref="Bypass"/> around its call.
///
/// Kill switch: <c>script_destroy_guard=0</c>. MAIN THREAD ONLY (the script VM runs there).
/// </summary>
internal static unsafe class ScriptDestroyGuardHook
{
    private const nint CallSite = 0x0053015C;
    private const nint AddEventDeltaTime = 0x004B08D0;
    private const nint DestroyEventIdAddress = 0x007441E0;
    /// <summary>How long the last census set keeps protecting: long enough to span a rejoin,
    /// which releases the session BEFORE the reload and reconnects only after the world is up
    /// (live: script fired ~1.5 s after world-up, the first census pass came ~2 s after it).</summary>
    private const long StaleMs = 60_000;

    /// <summary>How long the world-up snapshot protects, until the census takes over.</summary>
    private const long SnapshotMs = 20_000;

    private static readonly HashSet<uint> Protected = [];
    private static long _protectedAtMs;
    private static readonly HashSet<uint> Snapshot = [];
    private static long _snapshotUntilMs;
    private static bool _installed;
    private static int _logged;
    private static int _allowedLogged;

    private static byte* _block;

    /// <summary>Set by the plugin's own destroy (a host-decided removal) while it runs.</summary>
    public static bool Bypass;

    /// <summary>Destroys this guard refused, for diagnostics.</summary>
    public static int Refused { get; private set; }

    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)CallSite;
        if (site[0] != 0xE8 || CallSite + 5 + *(int*)(site + 1) != AddEventDeltaTime)
        {
            diagnostic = $"call site 0x{CallSite:X} is not 'call 0x{AddEventDeltaTime:X}'; guard not installed";
            return false;
        }

        if (!NativeMethods.VirtualProtect(CallSite, 5, 0x40, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        nint target = (nint)(delegate* unmanaged[Thiscall]<nint, uint, uint, uint, uint, uint, uint, void>)&OnDestroyEventPosted;
        *(int*)(site + 1) = (int)(target - (CallSite + 5));
        NativeMethods.VirtualProtect(CallSite, 5, old, out _);
        _installed = true;
        diagnostic = $"script DestroyObject guard installed at 0x{CallSite:X}";
        return true;
    }

    /// <summary>The ids the joiner's mirror found the host keeping alive this pass.</summary>
    public static void SetProtected(HashSet<uint> aliveOwnerIds)
    {
        Protected.Clear();
        Protected.UnionWith(aliveOwnerIds);
        _protectedAtMs = Environment.TickCount64;
    }

    /// <summary>
    /// A joiner's world just came up (an adopted host world, or any load while this game is a
    /// session client): every creature in it was loaded from the HOST's save, so it is the
    /// host's until the census says otherwise. Protected from the first frame — a module's own
    /// load-time scripts run before the first census pass arrives. A world that is NOT the
    /// host's drops the previous world's census set.
    /// </summary>
    public static unsafe void OnWorldUp(bool hostsWorld, byte* block)
    {
        _block = block;
        *(uint*)(block + SharedBlock.OffsetScriptDestroyedCount) = 0;
        Snapshot.Clear();
        _snapshotUntilMs = 0;
        if (!hostsWorld)
        {
            Protected.Clear();
            _protectedAtMs = 0;
            return;
        }

        const int Capacity = 1024;
        nint* creatures = stackalloc nint[Capacity];
        int count = Odyssey.CollectCreaturesByObjectTable(creatures, Capacity);
        for (int i = 0; i < count; i++)
        {
            uint id = *(uint*)(creatures[i] + 0x04);
            if (id != 0 && id != 0x7FFFFFFF)
            {
                Snapshot.Add(id);
            }
        }

        _snapshotUntilMs = Environment.TickCount64 + SnapshotMs;
        PluginHost.Log($"script destroy guard: protecting {Snapshot.Count} creature(s) of the host's world until the census arrives");
    }

    /// <summary>Hands the target of a script DestroyObject that went through to the agent (see
    /// SharedBlock.ScriptDestroy.cs): on the host, the liveness scan reports it gone the moment
    /// it leaves the world, even if the scan never saw it alive.</summary>
    private static void RecordScriptDestroy(uint id)
    {
        if (_block == null)
        {
            return;
        }

        uint* count = (uint*)(_block + SharedBlock.OffsetScriptDestroyedCount);
        uint* ids = (uint*)(_block + SharedBlock.OffsetScriptDestroyedIds);
        for (int i = 0; i < Math.Min(*count, (uint)SharedBlock.ScriptDestroyedCapacity); i++)
        {
            if (ids[i] == id)
            {
                return;
            }
        }

        ids[*count % SharedBlock.ScriptDestroyedCapacity] = id;
        *count += 1;
    }

    private static readonly HashSet<uint> PhantomBodies = [];

    /// <summary>A peer stand-in this plugin created: no script may destroy it. Only the
    /// plugin's own bypassed destroy removes one.</summary>
    public static void NotePhantomBody(uint id)
    {
        if (PhantomBodies.Count > 4096)
        {
            PhantomBodies.Clear();
        }

        PhantomBodies.Add(id);
    }

    /// <summary>
    /// A companion object this game's own party table names. Party members are each player's
    /// own, never the host's creatures, whatever number they happen to carry: entering the
    /// Taris hideout again, its arrival script removes the companion it placed last time before
    /// placing a fresh one, the guard refused that because the host had a different creature
    /// with the same id, and the joiner saw two Carths (2026-10-11, Party Conversations on
    /// Ebon Hawk build).
    /// </summary>
    private static bool IsOwnCompanion(uint id)
    {
        for (int slot = 0; slot < 9; slot++)
        {
            if (Odyssey.GetPartyRosterObjectId(slot) == id)
            {
                return true;
            }
        }

        // A companion standing about (available, not in the party) has no roster slot: known by
        // the tag every K1 companion blueprint carries.
        nint body = Odyssey.ResolveServerObject(id);
        return body != 0 && NativeMethods.IsReadable(body, 0x20)
            && Odyssey.TryReadExoString(body + 0x18, 16, out string tag)
            && CompanionTags.Contains(tag);
    }

    private static readonly HashSet<string> CompanionTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "Carth", "Mission", "Bastila", "Cand", "Zaalbar", "T3M4", "Jolee", "Juhani", "HK47",
    };

    private static bool IsProtected(uint id)
    {
        long now = Environment.TickCount64;
        return (now - _protectedAtMs <= StaleMs && Protected.Contains(id))
            || (now < _snapshotUntilMs && Snapshot.Contains(id));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static void OnDestroyEventPosted(nint aiMaster, uint days, uint time, uint source, uint target, uint eventId, uint data)
    {
        if (!Bypass && eventId == *(uint*)DestroyEventIdAddress && PhantomBodies.Contains(target))
        {
            Refused++;
            if (_logged < 40)
            {
                _logged++;
                PluginHost.Log($"script destroy guard: refused a script's DestroyObject on peer body 0x{target:X} "
                    + $"(source 0x{source:X})");
            }

            return;
        }

        if (!Bypass && eventId == *(uint*)DestroyEventIdAddress && IsProtected(target) && !IsOwnCompanion(target))
        {
            Refused++;
            if (_logged < 20)
            {
                _logged++;
                PluginHost.Log($"script destroy guard: refused a local script's DestroyObject on 0x{target:X} "
                    + $"(source 0x{source:X}) - the host owns it and has it alive");
            }

            return;
        }

        if (!Bypass && eventId == *(uint*)DestroyEventIdAddress)
        {
            // Creatures only: an item or placeable destroy is not the liveness channel's business.
            nint body = Odyssey.ResolveServerObject(target);
            if (body != 0 && NativeMethods.IsReadable(body, 4) && *(nint*)body == CreatureFinder.CreatureVtable)
            {
                RecordScriptDestroy(target);

                // Which creatures local scripts remove, and on whose orders: a joiner whose
                // copy of a scene loses an actor the host still has is otherwise silent.
                if (_allowedLogged < 20)
                {
                    _allowedLogged++;
                    Odyssey.TryReadExoString(body + 0x18, 32, out string tag);
                    PluginHost.Log($"script destroy guard: let a local script destroy creature 0x{target:X} '{tag}' (source 0x{source:X})");
                }
            }
        }

        ((delegate* unmanaged[Thiscall]<nint, uint, uint, uint, uint, uint, uint, void>)AddEventDeltaTime)(
            aiMaster, days, time, source, target, eventId, data);
    }
}
