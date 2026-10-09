namespace KotorCoop.Memory;

/// <summary>
/// The handful of fixed addresses and pointer chains everything else in the KOTOR bridge
/// starts from.
///
/// Collected in one place deliberately. Nearly every capability — pause state, world
/// variables, startup phase, dialogue state — begins by walking
/// <c>APP_MANAGER -&gt; client|server -&gt; internal</c>, and three separate copies of that walk
/// had already accumulated. They are only correct because the image has no ASLR; if one
/// copy is ever adjusted and the others are not, the resulting reads land on plausible
/// garbage rather than failing.
/// </summary>
public static class EngineGlobals
{
    public const nint AppManagerPtr = 0x007A39FC;

    private const int AppClientOffset = 0x04;
    private const int AppServerOffset = 0x08;

    /// <summary>Both CClientExoApp and CServerExoApp keep their internal at +0x04.</summary>
    private const int InternalOffset = 0x04;

    /// <summary>Resolves CClientExoApp. Exists from startup, main menu included.</summary>
    public static bool TryGetClient(ProcessMemory memory, out nint client) =>
        TryStep(memory, AppManagerPtr, AppClientOffset, out client);

    /// <summary>
    /// Resolves CServerExoApp. Null at the main menu — the server is only created when a
    /// game starts, which is the fact several startup checks hinge on.
    /// </summary>
    public static bool TryGetServer(ProcessMemory memory, out nint server) =>
        TryStep(memory, AppManagerPtr, AppServerOffset, out server);

    public static bool TryGetClientInternal(ProcessMemory memory, out nint clientInternal)
    {
        clientInternal = 0;
        return TryGetClient(memory, out nint client) && TryDeref(memory, client + InternalOffset, out clientInternal);
    }

    public static bool TryGetServerInternal(ProcessMemory memory, out nint serverInternal)
    {
        serverInternal = 0;
        return TryGetServer(memory, out nint server) && TryDeref(memory, server + InternalOffset, out serverInternal);
    }

    // ---- the server's object array: the engine's own answer to "is this id real" ------

    /// <summary>
    /// CServerExoAppInternal + this = CGameObjectArray*, straight out of
    /// <c>CServerExoApp::GetObjectArray</c> (0x004AED70), which is three instructions:
    /// <c>mov eax,[ecx+4]</c> (the internal) / <c>mov eax,[eax+0x1005C]</c> / <c>ret</c>.
    /// </summary>
    private const int InternalObjectArrayOffset = 0x1005C;

    /// <summary>CGameObjectArray + this = the bucket table, <c>GameObjectArrayEntry**</c>
    /// (symbol database: <c>object_tree</c>, offset 0).</summary>
    private const int ObjectArrayTree = 0x00;

    // GameObjectArrayEntry, size 0xC in the symbol database, laid out exactly as
    // CGameObjectArray::GetGameObject (0x004D8230) walks it.
    private const int EntryObjectId = 0x00;
    private const int EntryObject = 0x04;
    private const int EntryNext = 0x08;

    /// <summary>
    /// A bound on the bucket chain walk. The buckets are 8192 wide over a few hundred live
    /// objects, so a real chain is one or two nodes; anything longer means the memory was
    /// read mid-rehash or is not a chain at all, and this is a read of ANOTHER process's
    /// heap — it must not be able to spin.
    /// </summary>
    private const int MaxChainWalk = 64;

    /// <summary>Resolves the server's <c>CGameObjectArray</c>. False at the main menu and
    /// while a module is being torn down — the array is destroyed and re-allocated by
    /// UnloadModule, which is also why its address makes a module-lifetime token.</summary>
    public static bool TryGetServerObjectArray(ProcessMemory memory, out nint objectArray)
    {
        objectArray = 0;
        return TryGetServerInternal(memory, out nint serverInternal)
            && TryDeref(memory, serverInternal + InternalObjectArrayOffset, out objectArray);
    }

    /// <summary>
    /// The engine's own id → object lookup, performed from outside the process.
    ///
    /// ⚠ THIS IS THE ONLY HONEST LIVENESS TEST AVAILABLE TO THE AGENT. Everything else the
    /// bridge knows about creatures comes from a vtable scan of committed memory, and a scan
    /// cannot tell a live object from the intact bytes of a destroyed one: the vtable word
    /// and the object id survive in a freed heap block until something else claims it. Ids
    /// that came from such a ghost are in nobody's id space — the publishing instance itself
    /// cannot resolve them — and any damage a peer forwards keyed by one is owed for ever
    /// (docs/research/enemy-presence-sync.md, "the census published ghosts").
    ///
    /// A transcription of <c>CGameObjectArray::GetGameObject</c> (0x004D8230), which is small
    /// enough to reproduce exactly:
    /// <code>
    ///   bucket = ((id >> 31) &amp; 1) &lt;&lt; 12 | (id &amp; 0xFFF)   // 8192 buckets
    ///   for (e = tree[bucket]; e; e = e->next)
    ///       if (e->id == (id &amp; 0x7FFFFFFF)) return e->object;
    ///   return 0;
    /// </code>
    /// The high bit splits the two id spaces the engine allocates from (ordinary objects and
    /// characters), which is why it selects a bucket half rather than being masked away.
    /// </summary>
    /// <returns>True when the array holds the id; <paramref name="address"/> is the object
    /// pointer the engine would hand back.</returns>
    public static bool TryResolveServerObject(
        ProcessMemory memory, nint objectArray, uint objectId, out nint address)
    {
        address = 0;

        if (objectArray == 0
            || !memory.TryReadPointer(objectArray + ObjectArrayTree, out nint tree) || tree == 0)
        {
            return false;
        }

        bool found = TryLookupObject(
            a => memory.TryReadUInt32(a, out uint value) ? value : null, tree, objectId, out uint pointer);

        address = (nint)pointer;
        return found;
    }

    /// <summary>Confirms a scanned body against the native object array. An unresolved
    /// array must not publish stale world flags while the game rebuilds its objects.</summary>
    public static bool IsCurrentServerObject(ProcessMemory memory, nint objectArray, uint objectId, nint candidate)
    {
        return objectArray != 0
            && memory.TryReadPointer(objectArray + ObjectArrayTree, out nint tree)
            && IsCurrentServerObject(
                a => memory.TryReadUInt32(a, out uint value) ? value : null,
                tree, objectId, candidate);
    }

    internal static bool IsCurrentServerObject(Func<nint, uint?> read32, nint tree, uint objectId, nint candidate) =>
        tree != 0 && candidate != 0
        && TryLookupObject(read32, tree, objectId, out uint liveAddress)
        && (nint)liveAddress == candidate;

    /// <summary>
    /// Every live server object's address, straight from the engine's own id table: one 32 KB
    /// read of the 8192-bucket head array, then each (short) chain. Unlike a vtable memory scan
    /// it sees an object the instant the engine registers it, which is what a freshly dropped
    /// body bag needs (CorpseLootPump). Empty when the array cannot be read.
    /// </summary>
    public static List<nint> EnumerateServerObjects(ProcessMemory memory)
    {
        List<nint> objects = [];
        if (!TryGetServerObjectArray(memory, out nint objectArray)
            || !memory.TryReadUInt32(objectArray + ObjectArrayTree, out uint tree) || tree == 0)
        {
            return objects;
        }

        byte[] heads = new byte[8192 * 4];
        if (!memory.TryRead((nint)tree, heads))
        {
            return objects;
        }

        for (int bucket = 0; bucket < 8192; bucket++)
        {
            uint entry = BitConverter.ToUInt32(heads, bucket * 4);
            for (int walked = 0; entry != 0 && walked < MaxChainWalk; walked++)
            {
                if (!memory.TryReadUInt32((nint)entry + EntryObject, out uint obj)
                    || !memory.TryReadUInt32((nint)entry + EntryNext, out uint next))
                {
                    break;
                }

                if (obj != 0)
                {
                    objects.Add((nint)obj);
                }

                entry = next;
            }
        }

        return objects;
    }

    /// <summary>
    /// The bucket arithmetic and chain walk on their own, over any reader.
    ///
    /// Split out so the transcription can be ASSERTED rather than described: it is the one
    /// part of this that is arithmetic rather than a pointer chain, so it is the one part a
    /// unit test can prove — the bucket split on the id's high bit especially, which is easy
    /// to "simplify" into a mask and would then resolve character ids onto ordinary objects.
    ///
    /// Every field of the structure is 4 bytes wide because the game is x86; the agent that
    /// reads it is x64, so the widths are stated here rather than inherited from the reader.
    /// </summary>
    /// <param name="read32">Reads a 4-byte word out of the game, or null when it cannot.</param>
    public static bool TryLookupObject(Func<nint, uint?> read32, nint tree, uint objectId, out uint address)
    {
        address = 0;

        uint bucket = (((objectId >> 31) & 1) << 12) | (objectId & 0xFFF);
        uint key = objectId & 0x7FFF_FFFF;

        if (read32(tree + (nint)(bucket * 4)) is not { } head)
        {
            return false;
        }

        uint entry = head;

        for (int walked = 0; entry != 0 && walked < MaxChainWalk; walked++)
        {
            if (read32((nint)entry + EntryObjectId) is not { } entryId)
            {
                return false;
            }

            if (entryId == key)
            {
                if (read32((nint)entry + EntryObject) is not { } obj || obj == 0)
                {
                    return false;
                }

                address = obj;
                return true;
            }

            if (read32((nint)entry + EntryNext) is not { } next)
            {
                return false;
            }

            entry = next;
        }

        return false;
    }

    /// <summary>Convenience overload that resolves the array first. Prefer the other one in
    /// a loop: the array pointer is worth reading once, not once per id.</summary>
    public static bool TryResolveServerObject(ProcessMemory memory, uint objectId, out nint address)
    {
        address = 0;
        return TryGetServerObjectArray(memory, out nint objectArray)
            && TryResolveServerObject(memory, objectArray, objectId, out address);
    }

    /// <summary>CServerExoAppInternal + this = CServerInfo* (symbol database: server_info).</summary>
    private const int ServerInfoOffset = 0x10004;

    /// <summary>CServerInfo + this = module name, a CExoString (symbol database: module_name).</summary>
    private const int ServerInfoModuleName = 0xFC;

    /// <summary>CServerExoAppInternal + this = nonzero while a module load is in progress.</summary>
    private const int LoadingModuleOffset = 0x100B4;

    /// <summary>CServerExoAppInternal + this = server mode; 2 while a module is running.</summary>
    private const int ServerModeOffset = 0x10008;

    /// <summary>
    /// Reads the loaded module's name, e.g. "end_m01aa". Empty during a transition or when
    /// no world is loaded. This — not the area id at creature+0x8C — is the correct module
    /// identity: area ids come from a counter that resets on every transition, so different
    /// modules routinely share one.
    /// </summary>
    public static bool TryReadModuleName(ProcessMemory memory, out string moduleName)
    {
        moduleName = string.Empty;

        if (!TryGetServerInternal(memory, out nint serverInternal))
        {
            return false;
        }

        // Only trust the string while the world is actually running; mid-transition the
        // name is being rewritten and could be read half-changed.
        if (!memory.TryReadInt32(serverInternal + LoadingModuleOffset, out int loading) || loading != 0
            // ⚠ The mode is a WORD, not a dword — the engine uses `cmp word ptr
            // [esi+0x10008], 1` (0x4B6294). A 32-bit read picks up the adjacent field, whose
            // contents depend on how the module was loaded: measured live, a click-loaded
            // world read 0x00000002 but a plugin-loaded one read 0x555FFC02, so this gate
            // failed and every module name fell back to "area{id}" — which then hid peers
            // behind the module filter, looking exactly like a broken network layer.
            || !memory.TryRead(serverInternal + ServerModeOffset, out ushort mode) || mode != 2)
        {
            return false;
        }

        if (!TryDeref(memory, serverInternal + ServerInfoOffset, out nint serverInfo)
            || !memory.TryReadPointer(serverInfo + ServerInfoModuleName, out nint text) || text == 0
            || !memory.TryReadUInt32(serverInfo + ServerInfoModuleName + 4, out uint length)
            || length is 0 or > 32)
        {
            return false;
        }

        byte[] buffer = new byte[length];
        if (!memory.TryRead(text, buffer))
        {
            return false;
        }

        // ⚠ A CExoString's length can INCLUDE its terminating null (the same trap that made
        // equipment resrefs arrive as "g_a_clothes01\0" and be refused). A trailing null here
        // is invisible in a log and harmless to the module FILTER — both peers carry the same
        // junk byte, so the comparison still matches — but it is fatal to TRAVEL: the client
        // asks the engine to load "end_m01ab\0", no such module exists, and the transition
        // retries forever. Measured live as `travel: moving to module 'end_m01ab?'`.
        // Cut at the first character a module name cannot contain.
        int end = 0;
        while (end < buffer.Length)
        {
            char c = (char)buffer[end];
            bool valid = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '-';
            if (!valid)
            {
                break;
            }

            end++;
        }

        if (end == 0)
        {
            return false;
        }

        moduleName = System.Text.Encoding.ASCII.GetString(buffer, 0, end);
        return true;
    }

    /// <summary>CServerExoAppInternal + this = the byte of pause-reason bits.</summary>
    private const int PauseMaskOffset = 0x10078;

    /// <summary>The player's own pause toggle (space bar / pause button). Set and cleared
    /// through TogglePauseState (0x004BA610) -&gt; SetPauseState (0x004B8110).</summary>
    public const byte PauseReasonPlayer = 0x01;

    /// <summary>Window-not-focused; also used by scripts and dialogue.</summary>
    public const byte PauseReasonFocus = 0x02;

    /// <summary>CClientExoAppInternal + this = the client's own pause-reason bitmask.</summary>
    private const int ClientPauseMaskOffset = 0x1CC;

    /// <summary>
    /// Reads the pause-reason bitmask. The client and the server keep SEPARATE masks and
    /// separate world timers, and a real pause holds both — so this reports the union,
    /// and a peer's pause is detected however it was entered.
    /// </summary>
    public static bool TryReadPauseMask(ProcessMemory memory, out byte mask)
    {
        mask = 0;
        bool any = false;

        if (TryGetServerInternal(memory, out nint serverInternal)
            && memory.TryRead(serverInternal + PauseMaskOffset, out byte serverMask))
        {
            mask |= serverMask;
            any = true;
        }

        if (TryGetClientInternal(memory, out nint clientInternal)
            && memory.TryRead(clientInternal + ClientPauseMaskOffset, out byte clientMask))
        {
            mask |= clientMask;
            any = true;
        }

        return any;
    }

    // ---- minigames: swoop races and turret sequences ---------------------------------

    /// <summary>CClientExoAppInternal + this = CSWCModule* (symbol database:
    /// CClientExoApp::GetModule / 0x005ED530 is exactly <c>[ecx+4]</c> then this offset —
    /// the same chain <c>CClientExoApp::GetMiniGame</c> 0x005EDB00 walks before handing off
    /// to <c>CSWCModule::GetMiniGame</c> 0x00641980).</summary>
    private const int ClientModuleOffset = 0x18;

    /// <summary>CSWCModule + this = CSWCArea* (symbol database: <c>area</c>).</summary>
    private const int ModuleAreaOffset = 0x48;

    /// <summary>CSWCArea + this = CSWMiniGame* (symbol database: <c>mini_game</c>), or 0
    /// when no swoop/turret sequence is running. See docs/research/minigames.md.</summary>
    private const int AreaMiniGameOffset = 0x264;

    /// <summary>Resolves the client's CSWCModule (docs/research/travel-map-init-crash.md
    /// calls this "the client's CSWCModule" at <c>[clientInternal+0x18]</c>). Null before a
    /// client world exists.</summary>
    public static bool TryGetClientModule(ProcessMemory memory, out nint module)
    {
        module = 0;
        return TryGetClientInternal(memory, out nint clientInternal)
            && TryDeref(memory, clientInternal + ClientModuleOffset, out module);
    }

    /// <summary>
    /// A transcription of <c>CSWCModule::GetMiniGame</c> 0x00641980: <c>[module+0x48]</c>
    /// (area) then <c>[area+0x264]</c> (mini_game), each step tolerating a null. Nonzero
    /// means the engine is running a swoop race or a forced turret sequence — MainLoop
    /// routes those through <c>SetInputClass(1,1)</c> instead of the normal HUD bring-up
    /// (docs/research/cinematics.md §1, correcting ingame-gui-transition.md's old reading
    /// of the same call as a cutscene flag). False only means "could not be read" (no
    /// client, no module yet); a successful read with no minigame reports true with
    /// <paramref name="inMiniGame"/> false.
    /// </summary>
    public static bool TryIsInMiniGame(ProcessMemory memory, out bool inMiniGame)
    {
        inMiniGame = false;

        if (!TryGetClientModule(memory, out nint module))
        {
            return false;
        }

        if (!memory.TryReadPointer(module + ModuleAreaOffset, out nint area) || area == 0)
        {
            // A module with no area yet (mid-load) is honestly "not in a minigame", not a
            // read failure — the caller should not treat this as unknown.
            return true;
        }

        if (!memory.TryReadPointer(area + AreaMiniGameOffset, out nint miniGame))
        {
            return false;
        }

        inMiniGame = miniGame != 0;
        return true;
    }

    // ---- Pazaak: a GUI panel, not CSWCModule::GetMiniGame -----------------------------

    /// <summary>CClientExoAppInternal + this = CPazaakData*, or 0 when no hand is running
    /// (symbol database: <c>CClientExoAppInternal.pazaak @ 112</c> = 0x70). The pointer
    /// itself IS the running flag — <c>CClientExoApp::GetPazaakIsRunning</c> 0x005EE140 is
    /// exactly <c>[[ecx+4]+0x70]</c>, nothing more. See docs/research/minigames.md §4.</summary>
    private const int ClientPazaakOffset = 0x70;

    /// <summary>
    /// A transcription of <c>CClientExoApp::GetPazaakIsRunning</c> 0x005EE140: nonzero at
    /// <c>[clientInternal+0x70]</c> means a Pazaak hand is running. Deliberately a SEPARATE
    /// reader from <see cref="TryIsInMiniGame"/> rather than folded into its walk — Pazaak is
    /// a <c>CSWGuiPazaakGame</c> GUI panel, not a <c>CSWCModule/CSWCArea/CSWMiniGame</c>
    /// chain at all (docs/research/minigames.md §4). <b>NEEDS-LIVE</b>: the offset is read
    /// from the symbol database and cross-checked against the disassembled
    /// <c>GetPazaakIsRunning</c>, not yet watched flip in a running session — callers gate
    /// this behind an explicit opt-in (<see cref="KotorGameBridge"/>'s
    /// <c>PazaakDetectEnabled</c>, coop.ini <c>pazaak_detect</c>) until it has. False only
    /// means "could not be read" (no client yet); a successful read with no game running
    /// reports true with <paramref name="inPazaak"/> false.
    /// </summary>
    public static bool TryIsInPazaak(ProcessMemory memory, out bool inPazaak)
    {
        inPazaak = false;

        if (!TryGetClientInternal(memory, out nint clientInternal))
        {
            return false;
        }

        if (!memory.TryReadPointer(clientInternal + ClientPazaakOffset, out nint pazaak))
        {
            return false;
        }

        inPazaak = pazaak != 0;
        return true;
    }

    private static bool TryStep(ProcessMemory memory, nint root, int offset, out nint result)
    {
        result = 0;
        return TryDeref(memory, root, out nint next) && TryDeref(memory, next + offset, out result);
    }

    private static bool TryDeref(ProcessMemory memory, nint address, out nint value)
    {
        value = 0;
        return memory.TryReadPointer(address, out value) && value != 0;
    }
}
