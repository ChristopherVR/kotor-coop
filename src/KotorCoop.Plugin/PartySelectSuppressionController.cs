namespace KotorCoop.Plugin;

/// <summary>
/// Party-select suppression on the client (Tier 2 item 18, second half, task 1; coop.ini
/// <c>party_select_host_only</c>, plugin key, default ON, NEEDS-LIVE).
///
/// docs/research/galaxy-map-and-party-select.md §2: the retail party-selection screen
/// (<c>CSWGuiPartySelection</c>, reached from <c>CGuiInGame+0x078</c>) writes through the exact
/// <c>CSWPartyTable::SpawnNPC</c>/<c>AddMember</c> pair <see cref="PartyRosterController"/>
/// already mirrors for every other roster change — so the host picking through the unmodified
/// retail screen converges a client's roster within that controller's normal ~30-frame poll
/// cadence regardless of what caused the change. There is nothing for a client to negotiate:
/// this controller just keeps the client's own copy of the screen from ever giving the player
/// a choice, using the identical panel-teardown idiom docs/research/native-browser-panel.md
/// proves end to end for the native server-browser panel (mark <c>panel+0x44</c> for the
/// manager's own next-frame sweep, then never touch the object again — liveness afterward is
/// checked by ASKING the manager whether it is still registered, exactly the way
/// <see cref="NativeBrowserController"/> does, never by dereferencing a pointer that may
/// already be freed).
///
/// §2.4's own open question — whether suppressing via the sweep skips something the panel's
/// own <c>exit_script</c> field would otherwise run (e.g. the Taris swoop-taxi's own dispatch,
/// the only positively-confirmed trigger for this screen) — is NOT resolved here; this ships
/// the doc's own RECOMMENDED design (suppress outright, let the roster poll converge) and
/// leaves the fallback (§2.4 item 4, replaying the host's picks through a synthesized Accept
/// click) for a later pass if a live test shows the gap actually matters. NEEDS-LIVE for
/// exactly that reason, and for the panel/vtable offsets themselves — see the field comments
/// below for which are independently re-verified this pass and which are INFERRED from the
/// base <c>CSWGuiPanel</c> layout <c>native-browser-panel.md</c> verified for a DIFFERENT
/// panel class (<c>CSWGuiSaveLoad</c>).
/// </summary>
internal sealed unsafe class PartySelectSuppressionController(SharedBlockView block)
{
    // ---- engine bindings -----------------------------------------------------------------

    private const nint AppManagerPtr = 0x007A39FC;
    private const int AppClientOffset = 0x04;
    private const int ClientInternalOffset = 0x04;

    /// <summary>CClientExoAppInternal + this = CGuiInGame*, or 0 outside a loaded module. Same
    /// chain <c>Odyssey.Dialogue.Spectate.cs</c>'s <c>GetInGameGui</c> uses.</summary>
    private const int ClientInternalGui = 0x40;

    /// <summary>CGuiInGame + this = CSWGuiPartySelection* while the panel is on screen, else
    /// null. docs/research/galaxy-map-and-party-select.md §2.1 (VERIFIED, cross-checked twice
    /// against the symbol database and the disassembly of <c>ShowPartySelection 0x0062DD20</c>)
    /// — the SAME offset <c>Odyssey.Dialogue.Follow.cs</c>'s own postmortem comment names (do
    /// not confuse with the dialogue panel at +0x3C).</summary>
    private const int GuiPartySelectionPanel = 0x78;

    /// <summary>CSWGuiPartySelection's own vtable (symbol database,
    /// docs/research/galaxy-map-and-party-select.md §2.1) — the guard that a +0x78 pointer
    /// really is this class, laid out the way the offsets below assume, before anything writes
    /// through it.</summary>
    private const nint PartySelectionVtable = 0x00756D28;

    /// <summary>CSWGuiPanel + this = the CSWGuiManager* it was constructed with
    /// (docs/research/native-browser-panel.md §1: "The manager lands in panel+0x18" — verified
    /// for <c>CSWGuiSaveLoad</c>, a DIFFERENT panel class; INFERRED here to hold for every panel
    /// through the shared <c>CSWGuiPanel</c> base constructor, not independently re-verified
    /// for <c>CSWGuiPartySelection</c> — NEEDS-LIVE, guarded below by
    /// <see cref="LooksLikeHeapPointer"/> before use).</summary>
    private const int PanelGuiManager = 0x18;

    /// <summary>CSWGuiPanel + this = the state bits the manager's per-frame sweep reads.
    /// Writing <c>(state &amp; ~0x300) | 0x400</c> is the exact retail close idiom
    /// docs/research/native-browser-panel.md §2's state machine documents: RemovePanel, then
    /// delete, on the manager's NEXT Update. Same base-class-inferred caveat as
    /// <see cref="PanelGuiManager"/>.</summary>
    private const int PanelStateBits = 0x44;

    /// <summary>CSWGuiManager::PanelExists(panel) — pointer compare over the registered array;
    /// the safe way to learn a panel was already swept without ever dereferencing it
    /// (docs/research/native-browser-panel.md §7's own lesson, and
    /// <see cref="NativeBrowserController"/>'s own use of the same call).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int> PanelExistsFn =
        (delegate* unmanaged[Thiscall]<nint, nint, int>)0x0040BD70;

    // ---- config / state --------------------------------------------------------------------

    private readonly bool _enabled = PluginConfig.ReadBool("party_select_host_only", true);

    /// <summary>Latched once a guard refuses an address: stop touching this feature for the
    /// rest of the session rather than retry a wrong assumption every frame — the same idiom
    /// <see cref="NativeBrowserController"/>'s own <c>Break</c> uses.</summary>
    private bool _guardFailed;

    /// <summary>The panel this controller has already marked for the sweep, or 0. Never
    /// dereferenced again once set — see the class comment.</summary>
    private nint _panel;

    private nint _guiManager;

    public void OnFrame()
    {
        if (!_enabled || _guardFailed)
        {
            return;
        }

        if (_panel != 0)
        {
            // Mid-teardown: ask the manager, never the (possibly already-freed) panel itself.
            if (PanelExistsFn(_guiManager, _panel) == 0)
            {
                PluginHost.Log("party-select: client copy swept away");
                _panel = 0;
                _guiManager = 0;
            }

            return;
        }

        nint panel = GetPartySelectionPanel();
        if (panel == 0)
        {
            return;
        }

        if (!PartySelectSuppressionRule.ShouldSuppress(_enabled, block.TryGetHostPartyRoster(out _, out _)))
        {
            // Either the feature is off, or this instance is the host (or not in a live
            // session at all) — leave the unmodified retail screen alone; the host is the one
            // who is SUPPOSED to pick.
            return;
        }

        nint guiManager = Deref(panel + PanelGuiManager);
        if (!LooksLikeHeapPointer(guiManager))
        {
            _guardFailed = true;
            PluginHost.Log("party-select: guard refused (panel+0x18 did not look like a gui "
                + "manager pointer) — suppression disabled for this session");
            return;
        }

        uint* state = (uint*)(panel + PanelStateBits);
        *state = (*state & ~0x300u) | 0x400u;

        _panel = panel;
        _guiManager = guiManager;

        block.PostLocalChatLine("party is chosen by the host");
        PluginHost.Log("party-select: client copy suppressed - the host is picking "
            + "(PartyRosterController's poll converges the roster)");
    }

    /// <summary>Forgets any in-flight teardown bookkeeping without touching engine memory — a
    /// module transition (or a session ending) has already torn down or is about to tear down
    /// the whole <c>CGuiInGame</c>/panel/manager chain itself, so re-checking a cached pointer
    /// afterward would risk asking a freed manager about a freed panel. Does NOT reset
    /// <see cref="_guardFailed"/>: a wrong offset assumption is wrong for the whole process, not
    /// per-module.</summary>
    public void Invalidate()
    {
        _panel = 0;
        _guiManager = 0;
    }

    /// <summary>The live CSWGuiPartySelection*, vtable-verified, or 0 (no module loaded, the
    /// panel not open, or a pointer that is not what the offsets above assume).</summary>
    private static nint GetPartySelectionPanel()
    {
        nint appManager = Deref(AppManagerPtr);
        if (appManager == 0)
        {
            return 0;
        }

        nint client = Deref(appManager + AppClientOffset);
        if (client == 0)
        {
            return 0;
        }

        nint clientInternal = Deref(client + ClientInternalOffset);
        if (!LooksLikeHeapPointer(clientInternal))
        {
            return 0;
        }

        nint gui = Deref(clientInternal + ClientInternalGui);
        if (!LooksLikeHeapPointer(gui))
        {
            return 0;
        }

        nint panel = Deref(gui + GuiPartySelectionPanel);
        if (!LooksLikeHeapPointer(panel))
        {
            return 0;
        }

        return Deref(panel) == PartySelectionVtable ? panel : 0;
    }

    private static nint Deref(nint p) => p == 0 ? 0 : *(nint*)p;

    private static bool LooksLikeHeapPointer(nint p) =>
        p is > 0x10000 and < 0x7FFF0000 && (p & 3) == 0;
}
