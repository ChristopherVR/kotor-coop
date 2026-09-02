using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Galaxy-map gating, plugin side (Tier 2 item 18, task 2's own "wiring the plugin side is left
/// as follow-up work" note, docs/research/galaxy-map-and-party-select.md): on a CLIENT under
/// coop.ini <c>follow_lead=host</c>, refuse this instance's own click on
/// <c>CSWGuiInGameGalaxyMap</c>'s accept button — posting "only the host can choose a
/// destination" and swallowing the click — instead of letting it run the confirm script and
/// start a departure the host never chose. The host's own confirm is never touched: this hook
/// only ever refuses when <see cref="GalaxyMapConfirmSuppressionRule"/> says so, and that rule
/// is false for a host under either <c>follow_lead</c> mode.
///
/// <b>THE CHOKE POINT — RE done this pass, <c>tools/disasm.py</c>/<c>tools/xrefs.py</c>/
/// <c>tools/q.py</c> against the sha256-matched <c>tools/kotor1_0_3.db</c> GOG v1.0.3 build.</b>
/// docs/research/galaxy-map-and-party-select.md §2.1 named the party-select equivalent
/// (<c>CSWGuiPartySelection::ConfirmPartySelection</c> → <c>AcceptParty</c>) but explicitly left
/// the galaxy map's own accept-button dispatch un-traced (§4 item 2's "why no plugin wiring
/// shipped this pass" note) — §2.1's table only names the FIELD offset (<c>accept_button
/// 0x1F24</c>), not which function its click reaches. Unlike party-select, the galaxy map class
/// (<c>CSWGuiInGameGalaxyMap</c>, §1.1) has no separate named "confirm"/"accept" method in the
/// symbol database at all (<c>tools/q.py func '' CSWGuiInGameGalaxyMap</c> lists only
/// <c>Constructor</c>/<c>Destructor</c>/<c>OnPanelAdded</c>/<c>OnPanelRemoved</c>/
/// <c>DisplayPlanet</c>/<c>OnPlanetClicked</c>/<c>Draw</c>/<c>NextPlanet</c>/<c>PrevPlanet</c>/
/// <c>HandleInputEvent</c>) — every button on this panel, accept included, is dispatched from
/// inside <c>HandleInputEvent 0x00695980</c> itself, via a byte-lookup jump table
/// (<c>lea eax,[ebx-0x27]; cmp eax,0xB8; ja exit; movzx eax,[eax+0x695B0C]; jmp
/// [eax*4+0x695AF8]</c> — <c>ebx</c> is the raw GUI event code, the table maps it to one of a
/// handful of case handlers).
///
/// <c>tools/disasm.py 0x695980 500</c> disassembled every case body. The jump table has (at
/// least) four distinct targets: <c>0x695AAC</c> and <c>0x695AD1</c> call
/// <c>PrevPlanet 0x694CA0</c> and <c>NextPlanet 0x694BF0</c> respectively (both VERIFIED against
/// the same names §1.1 already cites) — clearly the arrow buttons, not our target. The other two,
/// <see cref="AcceptCaseHandler"/> (<c>0x6959D0</c>) and a sibling at <c>0x695A79</c>, both end by
/// calling <c>CGuiInGame::HideGalaxyMapGui 0x0062D1D0</c> (§1.1's own "show/hide entry points"
/// name) — the two buttons that CLOSE the panel, i.e. accept and back.
///
/// <b><see cref="AcceptCaseHandler"/> is the accept branch specifically</b>, confirmed two
/// independent ways this pass:
/// <list type="bullet">
/// <item>Only this branch pushes the literal <c>0x0075497C</c> before constructing a
/// <c>CExoString</c> and calling <c>CVirtualMachine::RunScript 0x005D0FC0</c> (name VERIFIED via
/// <c>tools/q.py sql "SELECT address,class_name,function_name FROM functions WHERE address IN
/// (...)"</c>). Reading that address out of the executable's own <c>.rdata</c>
/// (<c>tools/disasm.py</c>'s own PE-section walk, reused standalone) yields the ASCII string
/// <c>"k_sup_galaxymap\0"</c> — VERIFIED, byte for byte, the exact confirm-handler script name
/// §1.3 already traced end to end from the NWScript side ("<c>k_sup_galaxymap.nss</c> ... this is
/// the Accept confirm handler"). No other function in this class's <c>HandleInputEvent</c> reads
/// that string.</item>
/// <item><c>tools/xrefs.py 0x6959D0</c> reports ZERO direct call/jmp instruction sites and
/// EXACTLY ONE address-literal occurrence — the jump table entry at <c>0x695AF8</c> itself. The
/// only way execution ever reaches <c>0x6959D0</c> is through that one computed jump, for
/// whichever raw GUI event code(s) the lookup table at <c>0x695B0C</c> maps to case index 0. This
/// is the same "single funnel, cross-referenced" argument
/// <c>docs/research/galaxy-map-and-party-select.md</c> §1.4 makes for
/// <c>CServerExoAppInternal::StartNewModule</c>, and the same discipline every hook in this
/// plugin applies before splicing anything.</item>
/// </list>
///
/// <b>THE SPLICE.</b> The case handler's own first two instructions are position-independent and
/// exactly 5 bytes — <c>mov ecx,[esi+0x18]</c> (3) / <c>push 0</c> (2), the <c>this</c> pointer
/// and the argument to the immediately following <c>CSWGuiManager::PlayGuiSound 0x0040A140</c>
/// click-sound call — so, like <see cref="MoviePlayHook"/>/<see cref="ContainerUseHook"/>, only
/// those 5 bytes are relocated; nothing past them (the sound call, the string construction, the
/// script dispatch, the hide call) needs touching at all on the ALLOW path, which resumes at
/// <see cref="ResumeAt"/> — the untouched original <c>call 0x40a140</c> instruction — and runs
/// bit-for-bit as vanilla from there. Reached by a <c>jmp</c> splice (this is itself a jump-table
/// TARGET, not a callable function — there is no return address to preserve), so <c>esi</c>
/// (<c>this</c>), <c>ebx</c> (the raw event code) and <c>edi</c> (the caller's second argument,
/// already tested non-zero by <c>HandleInputEvent</c>'s own dispatch before the jump table is
/// even reached) are all live exactly as the vanilla case body expects them, and the real x86
/// call stack is untouched — our stub's own <c>pushad</c>/<c>popad</c> pair is the only thing
/// that ever moves <c>esp</c>, and it is back to its entry value before either the ALLOW or the
/// REFUSE path runs.
///
/// <b>THE REFUSE PATH IS THE FUNCTION'S OWN NO-OP EXIT, NOT A SYNTHESISED ONE</b> — the same
/// principle <see cref="ContainerUseHook"/>'s own remarks establish for its <c>ret 8</c>, adapted
/// for a JUMP-TABLE target instead of a CALLABLE function (there is no return address here to pop
/// — a synthesised <c>ret</c> would corrupt the caller). <see cref="NoOpExit"/>
/// (<c>0x695AE2</c>) is <c>HandleInputEvent</c>'s own shared "this event was not handled" tail —
/// reached today by BOTH of the dispatcher's own pre-jump-table exits (<c>edi==0</c>, or the raw
/// event code out of the <c>[0x27,0xDF]</c> range): it restores the saved SEH handler from
/// <c>[esp+0x14]</c>, pops <c>edi</c>/<c>esi</c>/<c>ebx</c> off the real stack (the SAME values
/// pushed at the function's own entry, untouched by anything this hook does), and
/// <c>ret 8</c>s. Jumping there from our stub is therefore exactly equivalent to the accept
/// click never having matched a case at all — no galaxy-map/panel state changes, the confirm
/// script never runs, and the panel is left exactly as it was (still open) for the notice to
/// explain.
///
/// <b>ABI.</b> <c>HandleInputEvent</c> itself is <c>__thiscall(ecx=this, [esp+4]=eventCode,
/// [esp+8]=secondArg), ret 8</c> — matches the symbol database's own
/// <c>CSWGuiPanel::HandleInputEvent</c> row for the base-class call this function makes at its
/// own entry (<c>0x00409E60</c>, <c>tools/q.py func '' CSWGuiPanel</c>). Nothing about our splice
/// depends on that ABI directly (we neither call nor return from the function ourselves), only on
/// the two jump targets (<see cref="ResumeAt"/>, <see cref="NoOpExit"/>) being reached with the
/// SAME register/stack state the vanilla dispatch would have left, which the analysis above
/// establishes.
///
/// <b>VERIFIED this pass</b>: every address, byte sequence, string, and xref cited above, against
/// the sha256-matched GOG v1.0.3 <c>swkotor.exe</c>. <b>NEEDS-LIVE</b>: never exercised on a
/// running process — see docs/research/galaxy-map-and-party-select.md's dated 2026-09-02 section
/// for the full live test plan. No engine calls from the managed callback beyond the two already-
/// proven-safe reads (<see cref="SharedBlockView.TryGetHostPartyRoster"/>,
/// <see cref="SharedBlockView.PostLocalChatLine"/>), no allocation, and a catch that can never
/// unwind into the engine — same discipline every hook in this plugin follows.
/// </summary>
internal static unsafe class GalaxyMapConfirmHook
{
    /// <summary>The accept/confirm case handler inside <c>HandleInputEvent</c>'s own jump table
    /// — see the class remarks for the RE that pins this down.</summary>
    private const nint AcceptCaseHandler = 0x006959D0;

    /// <summary>Where the relocated 2-instruction prologue resumes: the original, untouched
    /// <c>call CSWGuiManager::PlayGuiSound(0x0040A140)</c> instruction.</summary>
    private const nint ResumeAt = 0x006959D5;

    /// <summary><c>HandleInputEvent</c>'s own shared "event not handled" exit — see the class
    /// remarks for why jumping here is the correct, engine-native refusal for a jump-table
    /// target (as opposed to <see cref="ContainerUseHook"/>'s synthesised <c>ret</c>, which only
    /// works for a callable function's own true entry).</summary>
    private const nint NoOpExit = 0x00695AE2;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary><c>mov ecx,[esi+0x18]</c> / <c>push 0</c> — two whole instructions, 5 bytes,
    /// neither position-dependent (register-relative / immediate only).</summary>
    private static ReadOnlySpan<byte> Prologue => [0x8B, 0x4E, 0x18, 0x6A, 0x00];

    private const int RelocatedBytes = 5;

    private static bool _installed;

    private static SharedBlockView? _view;

    /// <summary>coop.ini <c>galaxy_map_host_only</c> — mirrors <c>party_select_host_only</c>'s
    /// own naming and default (ON, NEEDS-LIVE): see docs/CONFIG.md.</summary>
    private static readonly bool _enabled = PluginConfig.ReadBool("galaxy_map_host_only", true);

    /// <summary>This instance's own direct read of coop.ini <c>follow_lead</c> — the SAME key
    /// <c>CoopConfig.cs</c>'s <c>FollowLead</c> parses, matched byte-for-byte (case-insensitive
    /// <c>"host"</c>, default <c>any</c> absent the key). Read once at class-init time, the same
    /// idiom <see cref="PauseButtonHook"/>'s own <c>Wanted</c> field uses — <c>follow_lead</c> is
    /// not a per-frame setting anywhere else in this codebase either.</summary>
    private static readonly bool _followLeadHost =
        (PluginConfig.Read("follow_lead") ?? "any").Trim().Equals("host", StringComparison.OrdinalIgnoreCase);

    private static uint _refusedCount;
    private static uint _allowedCount;
    private static uint _replayedCount;

    /// <summary>Nonzero while <see cref="Odyssey.DepartToPlanet"/> is inside its own accept
    /// dispatch — the accept about to reach <see cref="OnConfirmClicked"/> is a REPLAY of a
    /// peer's departure (docs/research/galaxy-map-and-party-select.md, the 2026-09-03 relay
    /// addendum), not this player's own choice: it is let through under any
    /// <c>follow_lead</c> and NOT published as a departure of our own, or the two peers would
    /// replay the same leg at each other forever. A plain static rather than a block field
    /// because it is only ever set and cleared around ONE synchronous call on the main thread.</summary>
    private static int _replayArmed;

    /// <summary>Every accept click this hook refused, for the live test plan's own verification
    /// (a log-line count that survives beyond the plugin log's own rotation).</summary>
    public static uint RefusedCount => _refusedCount;

    /// <summary>Every accept click this hook saw and let through (host, or client under
    /// <c>follow_lead=any</c>, or the feature off/unbound). Replays count here too.</summary>
    public static uint AllowedCount => _allowedCount;

    /// <summary>Of <see cref="AllowedCount"/>, the accepts that were replays of a peer's
    /// departure (<see cref="Odyssey.DepartToPlanet"/>) rather than this player's own.</summary>
    public static uint ReplayedCount => _replayedCount;

    /// <summary>See <see cref="_replayArmed"/>. Paired with <see cref="DisarmReplay"/> in a
    /// <c>try</c>/<c>finally</c> around the one accept dispatch it covers.</summary>
    public static void ArmReplay() => _replayArmed = 1;

    public static void DisarmReplay() => _replayArmed = 0;

    /// <summary>Whether <see cref="Install"/> spliced the accept-button jump-table target
    /// successfully — false means either it has not run yet (no module loaded), or the prologue
    /// byte check refused (see the diagnostic <see cref="PluginHost.Startup"/> logs at startup).
    /// Read by the <c>galaxymap stats</c> console verb (<see cref="CheatController"/>) so a live
    /// session can confirm the hook is armed before trusting <see cref="RefusedCount"/>/
    /// <see cref="AllowedCount"/> as anything more than "never fired".</summary>
    public static bool IsInstalled => _installed;

    /// <summary>Set once the block exists; the hook fires from the GUI event dispatch and has no
    /// other route to the shared view. Same idiom <see cref="MinimapPeerHook.Bind"/> uses.</summary>
    public static void Bind(SharedBlockView view) => _view = view;

    /// <summary>Verify-bytes-or-refuse, like every patch in this plugin. MAIN THREAD ONLY, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)AcceptCaseHandler;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not gating the galaxy-map confirm";
                return false;
            }
        }

        nint stub = NativeMethods.VirtualAlloc(0, 64, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;

        s[n++] = 0x60; // pushad

        nint callback = (nint)(delegate* unmanaged[Cdecl]<int>)&OnConfirmClicked;
        s[n++] = 0xE8; // call OnConfirmClicked() - cdecl, no args
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x85; s[n++] = 0xC0; // test eax, eax  (before popad - popad overwrites eax)
        s[n++] = 0x61;                // popad (flags survive - popad never touches EFLAGS)

        // jnz refuse - skips the relocated-prologue+jmp-back block (10 bytes) below.
        s[n++] = 0x75; s[n++] = (byte)(RelocatedBytes + 5);

        for (int i = 0; i < RelocatedBytes; i++) // the relocated prologue
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9; // jmp ResumeAt (the ALLOW path - vanilla from here on)
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));
        n += 4;

        // refuse: jmp NoOpExit - HandleInputEvent's own "not handled" tail, see the class
        // remarks for why this must be a jmp into the real function rather than a synthesised
        // ret (there is no return address here to pop; this is a jump-table target, not a call).
        s[n++] = 0xE9;
        *(int*)(s + n) = (int)(NoOpExit - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(AcceptCaseHandler, (nuint)RelocatedBytes, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9; // jmp stub
        *(int*)(site + 1) = (int)(stub - (AcceptCaseHandler + 5));

        NativeMethods.VirtualProtect(AcceptCaseHandler, (nuint)RelocatedBytes, old, out _);

        _installed = true;
        diagnostic = $"galaxy-map confirm gate at 0x{AcceptCaseHandler:X} (stub 0x{stub:X}) - "
            + $"galaxy_map_host_only={(_enabled ? 1 : 0)}, follow_lead={(_followLeadHost ? "host" : "any")}";
        return true;
    }

    /// <summary>
    /// Runs at the exact moment the accept button's click reaches its own case handler, before
    /// the click sound plays or the confirm script runs. Returns 1 to refuse (swallow the click,
    /// leave the map open), 0 to allow (vanilla from here on). No engine calls beyond the two
    /// already-proven-safe shared-block reads/writes; never throws into the engine.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnConfirmClicked()
    {
        try
        {
            SharedBlockView? view = _view;
            if (view is null)
            {
                // No session bound yet (single-player, or before the first frame) - vanilla.
                _allowedCount++;
                return 0;
            }

            if (_replayArmed != 0)
            {
                // A peer's departure being replayed here (Odyssey.DepartToPlanet): always
                // through, never republished - see _replayArmed.
                _allowedCount++;
                _replayedCount++;
                return 0;
            }

            bool isClient = view.TryGetHostPartyRoster(out _, out _);
            if (!GalaxyMapConfirmSuppressionRule.ShouldRefuse(_enabled, isClient, _followLeadHost))
            {
                _allowedCount++;
                PublishDeparture(view);
                return 0;
            }

            _refusedCount++;
            view.PostLocalChatLine("only the host can choose a destination");
            PluginHost.Log("galaxy-map: confirm refused - only the host can choose a destination "
                + "(follow_lead=host)");
            return 1;
        }
        catch
        {
            // Never let a managed fault unwind into the engine's GUI event dispatch - and never
            // block a confirm on one either: fail open, exactly vanilla.
            return 0;
        }
    }

    /// <summary>
    /// The one observable moment of an interplanetary leg (SharedBlock.GalaxyDeparture.cs):
    /// this player's own accept is about to run the confirm script, and the main thread will
    /// be frozen for the next 17-45 s of movie + <c>StartNewModule</c>. Publish the planet the
    /// party table currently has selected — the same <c>CSWPartyTable+0xE0</c> field the
    /// confirm script itself reads — so the session can relay it while the engine is away.
    /// A planet of -1 (nothing selected) is the vanilla "accept with no destination" no-op
    /// and is not published at all: nothing departs, so there is nothing to follow.
    /// </summary>
    private static void PublishDeparture(SharedBlockView view)
    {
        if (!Odyssey.TryReadGalaxyMapState(out _, out int planet, out _) || planet < 0)
        {
            return;
        }

        view.PublishGalaxyDeparture(planet);
        PluginHost.Log($"galaxy-map: confirm allowed - departing to planet {planet} "
            + $"(published as departure #{view.GalaxyDepartureSerial} for the session to follow)");
    }
}
