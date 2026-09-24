using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Inline detour on the developer console's command interpreter
/// <c>FuncInterp 0x0044C1F0</c> (<c>__cdecl char*(char* commandLine)</c>). It runs whenever a
/// line is entered in the <c>~</c> console, inside the engine's own main loop — independent of
/// the <see cref="FrameHook"/> SwapBuffers callback (which is dormant at the main menu), so the
/// co-op console works even there.
///
/// For one of our co-op verbs the detour publishes the whole line to the shared block and
/// returns an empty result WITHOUT calling the engine, so the engine never dispatches the verb
/// and never prints "function &lt;x&gt; not found". For anything else it tail-calls the original
/// interpreter, so a genuine typo still gets the engine's not-found message — which is why the
/// old global blank of that format string is gone.
///
/// This replaces the previous buffer-capture-on-Enter routing (which raced the engine's own
/// Enter handling) with an exact, in-line interception at the one function that dispatches.
/// </summary>
internal static unsafe class FuncInterpHook
{
    private const nint FuncInterp = 0x0044C1F0;
    private const nint FuncInterpBody = 0x0044C1F5; // FuncInterp + 5, past the stolen prologue
    private const nint ResultBuffer = 0x007B8AB8;   // the static result string FuncInterp returns
    private const int StolenBytes = 5;              // mov eax, 0xC080  (B8 80 C0 00 00)
    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>Verbs the co-op layer owns; kept in sync with <c>ConsoleCommand.Parse</c>
    /// (and, for completion, <c>ConsoleVerbCatalog</c>).</summary>
    private static readonly string[] Verbs =
    [
        "connect", "join", "host", "disconnect", "dc", "leave", "reconnect", "rc",
        "say", "chat", "msg", "clear", "cls", "servers", "browse", "list", "status", "help", "coop",
        "password", "pw",
        "stat", "commands", "?", "warp", "who", "peers", "where", "lagstats",

        // Roadmap item 24 ("hosting over the internet"): prints a hosting player's public
        // IP:port and UPnP verdict for sharing with a joiner. Not a retail verb.
        "public_address", "publicaddress", "pubaddr",

        // The controller layer's verb (docs/research/gamepad.md §5). Not a retail verb.
        "gamepad",

        // A diagnostic, not a cheat: prints what this instance's enemy census holds and what
        // its own engine says about each id. It shadows nothing the retail console owns.
        "census", "resolve", "firescript",

        // "netsim" only, not its "net" alias: a three-letter word is far too likely to shadow
        // something the engine's own console owns, and `coop net ...` already routes through
        // the "coop" prefix for anyone who wants the short form.
        "netsim",

        // Staging cheats. None of these shadows a retail console verb — checked against the
        // engine's own registered names (docs/research/console-cheats.md) — so intercepting
        // them takes nothing away from the player.
        "god", "topup", "standup", "getup", "slay", "tp", "goto", "dropitem", "cheats",

        // The cinematic staging instrument. Full spelling only, for the same reason "netsim"
        // has no "net" alias here: a verb that arranges a multi-second window in which this
        // plugin does not run is not something to reach by a near-miss.
        "moviewarp", "usedoor", "opendoor",

        // crossdoor (2026-09-03, docs/research/cinematics.md's dated addendum): posts the SAME
        // door-crossing event usedoor's own walk relies on the pathfinder to trigger, directly
        // through CServerAIMaster::AddEventDeltaTime, with no walk at all. Does not shadow a
        // retail verb — checked against docs/research/console-cheats.md's table the same way
        // every other staging verb above was (ConsoleCompletionEngineTests.
        // NoCoopVerbShadowsARetailConsoleVerb also asserts it at build time).
        "crossdoor",

        // The container-sync live-test primitive (docs/research/container-sync.md): not a
        // retail verb (checked against docs/research/console-cheats.md's table the same way
        // every other staging verb above was).
        "useplaceable",
        // The close-side counterpart, added when the live-verify pass found no way to close
        // what useplaceable opened — see docs/research/container-sync.md.
        "closeplaceable",
        // The item-identity harness. "item"/"items"/"setitem" are ours: the engine registers
        // giveitem and nothing else beginning with "item".
        "items", "item", "setitem", "giveloot",

        // The peer-effect staging pair (docs/research/effect-sync.md).
        "applyeffect", "effects",

        // The mid-module movie relay's test hook (docs/research/cinematics.md §10.7):
        // fires MoviePlayHook directly, so a live pass does not need to wait for a story
        // trigger. `moviewarp` above only ever writes the TRANSITION queue, a different path.
        "movieplay",

        // Dev-only control-switch test primitive (docs/research/control-return.md): switches
        // the local player's controlled character. Local only, never crosses the network; not
        // a retail verb.
        "leader",

        // NOTE: "quittomenu" (docs/research/disconnect-reconnect.md §9) is deliberately NOT
        // added here, matching "skipline"/"selectreply"/"loadgame"/"walkto"/"recruit" above —
        // dev-only LIVE-TEST PRIMITIVES are reachable only via tools/blockpoke.ps1's direct
        // shared-block write, never by a real player typing at the in-game console.

        // Three more live-test primitives (2026-08-27), registered here (unlike the block
        // above) so a rig session can also type them directly at the in-game console, not only
        // via blockpoke: setglobal (docs/research/quest-sync.md), pazaak
        // (docs/research/minigames.md) and openstore (docs/research/store-sync.md). None
        // shadows a retail verb — checked the same way every other staging cheat above was.
        "setglobal", "pazaak", "openstore",

        // Two more live-test primitives (2026-08-28, docs/research/store-sync.md's 2026-08-28
        // addendum): registered here, same as setglobal/pazaak/openstore above, so a rig
        // session can type them directly at the in-game console. None shadows a retail verb.
        "buy", "sell",

        // A third live-test primitive (2026-08-28, docs/research/dialogue-sync.md's 2026-08-28
        // addendum), registered the same way. Does not shadow a retail verb.
        "startdialog",

        // A fourth live-test primitive (2026-08-28, docs/research/star-forge-finale.md §11):
        // registered here, unlike "recruit"/"skipline"/etc above, because the live plan for the
        // solo-segment detector's script-agnostic trigger needs it typed directly at the
        // in-game console (`removeparty 0` on the host), not only via blockpoke. Does not
        // shadow a retail verb.
        "removeparty",

        // The character-creation drive verbs (2026-08-29, docs/research/world-adoption.md's
        // join_mode section). Registered here, unlike the blockpoke-only primitives above, for
        // one reason a rig cannot work around: they are only ever meaningful AT THE MAIN MENU,
        // and this detour is the one console surface that works there (see this file's own
        // header). None shadows a retail verb — retail's own new-game entry is the menu button,
        // and there is no `chargen` interpreter function.
        "newgame", "chargen",

        // A fifth live-test primitive (2026-08-28, docs/research/store-sync.md's live pass):
        // registered here so a rig session can repair a poisoned gold value directly at the
        // in-game console. Does not shadow a retail verb (retail's own credits verb is
        // `givecredits`).
        "setgold",

        // The class-grant relay's live-test primitive (2026-09-18,
        // docs/research/class-and-power-grants.md): simulates Dorak's AddMultiClass on the
        // local player so the capture -> relay -> peer apply chain can be exercised without
        // playing Dantooine. Does not shadow a retail verb.
        "addclass",

        // Party-placed mine replication's live-test verbs (2026-09-24, docs/research/mines.md
        // §14.6), typed on either instance during the two-instance recipe. None shadows a retail
        // verb (docs/research/console-cheats.md lists no mine verb).
        "placemine", "minestate", "placedmineaction",

        // BUG FIX (2026-08-29, live-verification session): `pooladd`/`poolremove`
        // (docs/research/party-pool-sync.md, added to ConsoleCommand.cs in fcdab50) were never
        // added here, so every real in-game keystroke submission of either fell through to
        // `_original` — the retail engine's own FuncInterp, which has never heard of them —
        // instead of reaching CoopCommandDispatcher at all. `docs/CONSOLE.md` lists both as
        // ordinary (non-blockpoke-only) staging cheats, and this round's own live pass (typed
        // keystrokes via tools/sendkey.ps1, not blockpoke.ps1) reproduced the silent failure
        // directly: correct text on the input line, a real submit (the console's own open flag
        // cleared), zero log output — `IsCoopCommand` returning false is the exact, sufficient
        // explanation. Does not shadow a retail verb (checked against
        // docs/research/console-cheats.md the same way every prior addition here was).
        "pooladd", "poolremove",

        // The local_var_sync live-test primitive (2026-09-24, docs/research/quest-sync.md §8.3):
        // reads/writes one per-object local slot and adds a session-only watch, typed directly
        // at the in-game console on both rig instances. Does not shadow a retail verb (the
        // string "localvar" does not occur anywhere in the GOG swkotor.exe image).
        "localvar",

        // The live-cast primitive (2026-09-24, docs/research/effect-sync.md §14): typed on
        // both rig instances during the partner_buffs/enemy_effect_sync recipes, because those
        // hooks need a REAL cast's creator id, which only an actual in-game cast produces.
        // Does not shadow a retail verb — neither name appears in
        // docs/research/console-cheats.md's retail table, checked the same way every prior
        // addition here was (ConsoleCompletionEngineTests.NoCoopVerbShadowsARetailConsoleVerb
        // also asserts it at build time).
        "cast", "grantpower",

        // The minigame-kill primitive (2026-09-25, docs/research/minigames.md): typed on the
        // host rig instance to finish a forced turret/gunnery ambush the unattended rig cannot
        // play by actually shooting (e.g. the m12ab Ebon Hawk Sith-fighter sequence). Does not
        // shadow a retail verb (ConsoleCompletionEngineTests.NoCoopVerbShadowsARetailConsoleVerb
        // asserts it at build time, same as every prior addition here).
        "mgkill",
    ];

    /// <summary>
    /// Every submitted console line, for Up/Down recall (see ConsoleAssistController).
    /// Recorded here because this detour is the one place EVERY submission passes through —
    /// engine cheats included, and recalling <c>giveitem</c> is as useful as <c>connect</c>.
    /// Touched only on the game's main thread: the detour runs on it, and so does the
    /// MainLoop hook that navigates.
    /// </summary>
    internal static readonly ConsoleHistoryRing History = new(32);

    private static SharedBlockView? _block;
    private static delegate* unmanaged[Cdecl]<byte*, nint> _original;
    private static bool _installed;

    /// <summary>
    /// Patches FuncInterp's prologue to jump to <see cref="Detour"/>, building a trampoline that
    /// preserves the original. MAIN THREAD ONLY (the same reason FrameHook.Install is).
    /// </summary>
    public static bool Install(SharedBlockView block, out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        _block = block;
        byte* target = (byte*)FuncInterp;

        // Refuse unless the prologue is exactly the expected `mov eax, 0xC080`, so a shifted
        // layout or another hook can never make us corrupt the code.
        if (!(target[0] == 0xB8 && target[1] == 0x80 && target[2] == 0xC0 && target[3] == 0x00 && target[4] == 0x00))
        {
            diagnostic = "FuncInterp prologue mismatch; not hooking";
            return false;
        }

        // Trampoline = [stolen 5 bytes][E9 rel32 -> FuncInterpBody]. The stolen bytes are a
        // position-independent immediate load, so they run correctly at any address.
        nint tramp = NativeMethods.VirtualAlloc(0, 16, MemCommitReserve, PageExecuteReadWrite);
        if (tramp == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* t = (byte*)tramp;
        for (int i = 0; i < StolenBytes; i++)
        {
            t[i] = target[i];
        }

        t[StolenBytes] = 0xE9;
        *(int*)(t + StolenBytes + 1) = (int)(FuncInterpBody - (tramp + StolenBytes + 5));
        _original = (delegate* unmanaged[Cdecl]<byte*, nint>)tramp;

        nint detour = (nint)(delegate* unmanaged[Cdecl]<byte*, nint>)&Detour;
        if (!NativeMethods.VirtualProtect(FuncInterp, (nuint)StolenBytes, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        target[0] = 0xE9;
        *(int*)(target + 1) = (int)(detour - (FuncInterp + 5));
        NativeMethods.VirtualProtect(FuncInterp, (nuint)StolenBytes, old, out _);

        _installed = true;
        diagnostic = $"FuncInterp hooked (trampoline 0x{tramp:X})";
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Detour(byte* commandLine)
    {
        try
        {
            if (commandLine != null)
            {
                RecordHistory(commandLine);

                if (_block is { } block && IsCoopCommand(commandLine, out int length))
                {
                    block.PublishConsoleCommand(commandLine, length);
                    *(byte*)ResultBuffer = 0; // empty result: nothing echoed, no "not found"
                    return ResultBuffer;
                }
            }
        }
        catch
        {
            // A detour that throws into the engine would be fatal; fall through to the original.
        }

        return _original(commandLine);
    }

    /// <summary>
    /// Copies the submitted line into the history ring (one small string per submission —
    /// the only allocation this detour makes, and only on the rare human-typed event, never
    /// per frame). The ring itself decides what to keep: empties, consecutive duplicates
    /// and <c>password</c>/<c>pw</c> lines are dropped there.
    /// </summary>
    private static void RecordHistory(byte* commandLine)
    {
        int n = 0;
        while (n < 512 && commandLine[n] != 0)
        {
            n++;
        }

        if (n == 0)
        {
            return;
        }

        Span<char> chars = n <= 256 ? stackalloc char[256] : new char[n];
        for (int i = 0; i < n; i++)
        {
            chars[i] = (char)commandLine[i];
        }

        History.Record(new string(chars[..n]));
    }

    /// <summary>True when the line's first token is one of our verbs; sets its length.</summary>
    private static bool IsCoopCommand(byte* cmd, out int length)
    {
        int n = 0;
        while (n < 512 && cmd[n] != 0)
        {
            n++;
        }

        length = n;

        int start = 0;
        while (start < n && cmd[start] == (byte)' ')
        {
            start++;
        }

        if (start < n && cmd[start] == (byte)'/')
        {
            start++; // a leading "/coop"-style slash
        }

        int end = start;
        while (end < n && cmd[end] != (byte)' ')
        {
            end++;
        }

        int tokenLength = end - start;
        if (tokenLength is <= 0 or > 16)
        {
            return false;
        }

        foreach (string verb in Verbs)
        {
            if (verb.Length != tokenLength)
            {
                continue;
            }

            bool match = true;
            for (int i = 0; i < tokenLength; i++)
            {
                byte c = cmd[start + i];
                char lower = (char)(c is >= (byte)'A' and <= (byte)'Z' ? c + 32 : c);
                if (lower != verb[i])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }
}
