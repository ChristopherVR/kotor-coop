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

    /// <summary>CSWGuiPanel + this = the state bits the manager's per-frame sweep reads.
    /// Writing <c>(state &amp; ~0x300) | 0x400</c> is the exact retail close idiom
    /// docs/research/native-browser-panel.md §2's state machine documents: RemovePanel, then
    /// delete, on the manager's NEXT Update.</summary>
    private const int PanelStateBits = 0x44;

    /// <summary>CSWGuiManager::PanelExists(panel) — pointer compare over the registered array;
    /// the safe way to learn a panel was already swept without ever dereferencing it
    /// (docs/research/native-browser-panel.md §7's own lesson, and
    /// <see cref="NativeBrowserController"/>'s own use of the same call).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int> PanelExistsFn =
        (delegate* unmanaged[Thiscall]<nint, nint, int>)0x0040BD70;

    // ---- config / state --------------------------------------------------------------------

    private readonly bool _enabled = PluginConfig.ReadBool("party_select_host_only", true);

    /// <summary>The panel this controller has already marked for the sweep, or 0. Never
    /// dereferenced again once set — see the class comment.</summary>
    private nint _panel;

    private nint _guiManager;

    /// <summary>The last panel address this controller has ever torn down this module/session,
    /// or 0. See <see cref="PartySelectSuppressionRule.IsAlreadyHandled"/> — live pass
    /// 2026-08-28 found <c>GetPartySelectionPanel()</c> re-detecting the SAME address hundreds
    /// of times a second forever after the first teardown (a dangling <c>CGuiInGame+0x78</c>
    /// that nothing ever nulls), so this is remembered across the <c>_panel == 0</c> gap too,
    /// unlike <see cref="_panel"/> itself which only tracks the CURRENT in-flight teardown.</summary>
    private nint _lastHandledPanel;

    public void OnFrame()
    {
        if (!_enabled)
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

        if (!TryGetRegisteredPartySelectionPanel(out nint panel, out nint guiManager))
        {
            return;
        }

        if (PartySelectSuppressionRule.IsAlreadyHandled(panel, _lastHandledPanel))
        {
            // The identical address we already tore down this module/session — CGuiInGame+0x78
            // was never observed to clear afterward (see the rule's own doc comment), so this is
            // near-certainly a dangling re-read of already-freed (or never-really-reopened)
            // memory, not a fresh screen opening. Do not write through it again.
            return;
        }

        if (!PartySelectSuppressionRule.ShouldSuppress(_enabled, block.TryGetHostPartyRoster(out _, out _)))
        {
            // Either the feature is off, or this instance is the host (or not in a live
            // session at all) — leave the unmodified retail screen alone; the host is the one
            // who is SUPPOSED to pick.
            return;
        }

        uint* state = (uint*)(panel + PanelStateBits);
        *state = (*state & ~0x300u) | 0x400u;

        _panel = panel;
        _guiManager = guiManager;
        _lastHandledPanel = panel;

        block.PostLocalChatLine("party is chosen by the host");
        PluginHost.Log("party-select: client copy suppressed - the host is picking "
            + "(PartyRosterController's poll converges the roster)");
    }

    /// <summary>Forgets any in-flight teardown bookkeeping without touching engine memory — a
    /// module transition (or a session ending) has already torn down or is about to tear down
    /// the whole <c>CGuiInGame</c>/panel/manager chain itself, so re-checking a cached pointer
    /// afterward would risk asking a freed manager about a freed panel. Also forgets
    /// <see cref="_lastHandledPanel"/>: a fresh module gets a fresh <c>CGuiInGame</c>, so a
    /// genuinely new panel there deserves the same one-time suppression a first-ever panel
    /// would, even if its address happens to coincide with one from a previous module.</summary>
    public void Invalidate()
    {
        _panel = 0;
        _guiManager = 0;
        _lastHandledPanel = 0;
    }

    /// <summary>Resolves a currently registered party-selection panel. The manager's pointer
    /// comparison is deliberately before the candidate vtable read: <c>CGuiInGame+0x78</c> is
    /// a cached field that can retain an already-removed allocation, while
    /// <c>CSWGuiManager::PanelExists</c> only compares the candidate against its registered
    /// panel array and does not dereference the candidate. This ordering is the native
    /// <c>ShowPartySelection</c> ordering too (0x62DDC6..0x62DDD4).</summary>
    private static bool TryGetRegisteredPartySelectionPanel(out nint panel, out nint guiManager)
    {
        panel = 0;
        guiManager = 0;

        nint appManager = Deref(AppManagerPtr);
        if (appManager == 0)
        {
            return false;
        }

        nint client = Deref(appManager + AppClientOffset);
        if (client == 0)
        {
            return false;
        }

        nint clientInternal = Deref(client + ClientInternalOffset);
        if (!LooksLikeHeapPointer(clientInternal))
        {
            return false;
        }

        nint gui = Deref(clientInternal + ClientInternalGui);
        if (!LooksLikeHeapPointer(gui))
        {
            return false;
        }

        guiManager = Deref(gui + 0x38);
        panel = Deref(gui + GuiPartySelectionPanel);
        if (!LooksLikeHeapPointer(guiManager) || !LooksLikeHeapPointer(panel))
        {
            panel = 0;
            guiManager = 0;
            return false;
        }

        // PanelExists is a pointer comparison over the manager's registered array. Do this
        // before reading the candidate vtable: +0x78 is known to remain non-null after our
        // sweep removes the object, so a vtable check alone is not an open-panel test.
        if (PanelExistsFn(guiManager, panel) == 0)
        {
            panel = 0;
            guiManager = 0;
            return false;
        }

        // Only a registered candidate is safe to inspect beyond the pointer itself.
        if (Deref(panel) != PartySelectionVtable)
        {
            panel = 0;
            guiManager = 0;
            return false;
        }

        return true;
    }

    private static nint Deref(nint p) => p == 0 ? 0 : *(nint*)p;

    private static bool LooksLikeHeapPointer(nint p) =>
        p is > 0x10000 and < 0x7FFF0000 && (p & 3) == 0;
}
