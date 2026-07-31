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

        moduleName = System.Text.Encoding.ASCII.GetString(buffer);
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
