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
/// the only positively-confirmed trigger for this screen) — is answered by NOT relying on the
/// sweep alone any more: 2026-09-25's relay (SharedBlock.PartySelect.cs) asks the host to
/// replay whatever caused this panel to open. The sweep-only design (host picks silently, the
/// roster poll converges) remains the fallback when <c>party_select_relay</c> is off, or when
/// no causal trigger correlates with this capture (see the 2026-09-25 SECOND PASS note below).
///
/// ⚠ 2026-09-25 SECOND PASS: the FIRST relay design called <c>CGuiInGame::ShowPartySelection</c>
/// directly on the host with the captured <c>exit_script</c> — live-tested, and WRONG. A live
/// run found the host's own screen never actually opened (screenshotted, confirmed empty) while
/// the direct call itself reported "opened" (<c>CGuiInGame+0x78</c> non-null right after the
/// call): that signal proves nothing, because <c>+0x78</c> is a PERSISTENTLY REUSED object (see
/// the CRASH FIX note below) — it is essentially always non-null once first constructed in a
/// module, whether or not the call that just ran actually did anything. Worse, <c>exit_script</c>
/// itself may be STALE: since the object is reused, its <c>+0x074</c> field can carry residue
/// from an earlier, unrelated <c>ShowPartySelectionGUI</c> call rather than anything caused by
/// THIS capture. The fix does not call <c>ShowPartySelection</c> at all any more — it relays
/// the TRIGGER this instance's own real player most recently fired
/// (<see cref="OdysseyTriggers.TryGetRecentLocalTrigger"/>) and the host re-fires that SAME
/// trigger's OnEnter for ITS OWN player through <c>OdysseyTriggers.FireTriggerForLocalPlayer</c>
/// — the IDENTICAL, already-live-proven call the <c>firetrigger</c> dev verb makes, so the
/// host's engine runs byte-for-byte the same retail script chain a real walk-in would (input
/// class, pause, <c>AddPanel</c>, and whatever else the retail dispatch does — all of it, for
/// free, because nothing here re-implements any part of that chain). See
/// <see cref="PartySelectHostRequestController"/> and <c>Odyssey.PartySelectRelay.cs</c>'s own
/// updated header for what the (now unused, kept for its RE value) direct call still documents.
///
/// ⚠ 2026-09-25 CRASH FIX: a live joiner-suppress-then-host-led-exit sequence produced a
/// 100%-reproducible <c>CGuiInGame::ShutDown 0x0062C310</c> access violation on the CLIENT at
/// the next module unload (AV at 0x0062C41E, <c>mov edx, [ecx]</c> where <c>ecx</c> is the
/// party-select panel this controller had already freed). Root cause, confirmed by fresh
/// disassembly of both ends: <c>ShowPartySelection 0x0062DD20</c> allocates
/// <c>CGuiInGame+0x78</c> exactly ONCE per module and reuses that same object for the rest of
/// the module's life (see <c>Odyssey.PartySelectRelay.cs</c>); the suppression sweep's
/// <c>panel+0x44 = (…&amp;~0x300)|0x400</c> state is <c>native-browser-panel.md</c>'s own
/// "0x400 set, 0x200 clear" case — RemovePanel THEN <c>call [panel_vtbl+0]( 1)</c>, the scalar
/// DELETING destructor — but nothing had ever nulled <c>CGuiInGame+0x78</c> back to 0
/// afterward, exactly matching the 2026-08-28 "never goes null again" finding below. On the
/// NEXT module unload, <c>ShutDown</c> walks every panel field it owns (including +0x78) and,
/// for each non-null one, dereferences its vtable to call that SAME deleting destructor a
/// second time — a use-after-free on an already-decommitted heap block (module unload churns
/// the heap hard). <see cref="OnFrame"/> now nulls <c>CGuiInGame+0x78</c> itself, in the same
/// call that arms the sweep, so a later <c>ShowPartySelection</c> reallocates instead of
/// writing into freed memory and <c>ShutDown</c>'s own null check skips the dead field —
/// exactly the idiom <c>ShutDown</c> already applies to its other 15 panel fields.
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

    /// <summary>coop.ini <c>party_select_relay</c> (default ON, plugin key): whether a captured
    /// panel is also relayed to the host as a request, rather than just silently suppressed.
    /// See <see cref="PartySelectSuppressionRule.ShouldRelay"/>.</summary>
    private readonly bool _relayEnabled = PluginConfig.ReadBool("party_select_relay", true);

    /// <summary>How stale a locally-fired trigger may be and still count as "what caused this
    /// panel" (<see cref="OdysseyTriggers.TryGetRecentLocalTrigger"/>). Generous: the live
    /// finding this window exists to cover fired within 3 ms, but a retail script chain can
    /// stage a <c>DelayCommand</c> before actually opening the screen.</summary>
    private const long TriggerCorrelationWindowMs = 5000;

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

        if (!TryGetRegisteredPartySelectionPanel(out nint panel, out nint guiManager, out nint gui))
        {
            return;
        }

        if (PartySelectSuppressionRule.IsAlreadyHandled(panel, _lastHandledPanel))
        {
            // The identical address we already tore down this module/session — belt and
            // suspenders: the crash fix below now nulls CGuiInGame+0x78 itself right after
            // arming the sweep, so a genuinely stale re-read of this exact address should no
            // longer happen by construction, but this guard costs nothing to keep.
            return;
        }

        bool isClientInLiveSession = block.TryGetHostPartyRoster(out _, out _);
        if (!PartySelectSuppressionRule.ShouldSuppress(_enabled, isClientInLiveSession))
        {
            // Either the feature is off, or this instance is the host (or not in a live
            // session at all) — leave the unmodified retail screen alone; the host is the one
            // who is SUPPOSED to pick.
            return;
        }

        // Capture BEFORE sweeping: once the sweep runs, the object is on its way to being
        // freed, and exit_script/party_data are no longer safe to read.
        bool haveExitScript = Odyssey.TryReadPartySelectExitScript(panel, out string exitScript);
        Odyssey.ReadPartySelectForcedNpcs(panel, out int forceNpc1, out int forceNpc2);

        // 2026-09-25 second pass: exit_script is kept for diagnostics only now — CGuiInGame+0x78
        // is a persistently REUSED object (see the class header), so the field it carries at
        // capture time can be stale residue from an earlier, unrelated call rather than
        // something genuinely caused by whatever opened the panel THIS time. What the host
        // actually replays is the TRIGGER this instance's own real player most recently fired
        // (correlated by OdysseyTriggers, the same "one slot, freshest wins" signal the
        // `firetrigger` dev verb and the native walk-in observer both already feed) — see
        // PartySelectHostRequestController.
        bool haveTrigger = OdysseyTriggers.TryGetRecentLocalTrigger(TriggerCorrelationWindowMs, out uint triggerId);

        uint* state = (uint*)(panel + PanelStateBits);
        *state = (*state & ~0x300u) | 0x400u;

        // 2026-09-25 crash fix: null the engine's own cached pointer right here, in the same
        // call that just armed the manager's next-frame delete. See the class header's "CRASH
        // FIX" note — without this, CGuiInGame+0x78 keeps pointing at freed memory for the
        // rest of the module's life and CGuiInGame::ShutDown dereferences it at the next
        // module unload.
        *(nint*)(gui + GuiPartySelectionPanel) = 0;

        _panel = panel;
        _guiManager = guiManager;
        _lastHandledPanel = panel;

        if (PartySelectSuppressionRule.ShouldRelay(_enabled, _relayEnabled, isClientInLiveSession) && haveTrigger)
        {
            block.PublishPartySelectClientCapture(
                haveExitScript ? exitScript : string.Empty, forceNpc1, forceNpc2, triggerId);
            block.PostLocalChatLine("asked the host to choose the party");
            PluginHost.Log($"party-select: client copy captured and relayed to the host "
                + $"(trigger=0x{triggerId:X}, exit_script='{exitScript}', forceNpc1={forceNpc1}, "
                + $"forceNpc2={forceNpc2})");
        }
        else
        {
            // No recent local trigger to correlate this capture with (or relay is off) —
            // nothing reliable to ask the host to replay. Falls back to the original
            // suppress-and-converge design: the host picks through its own unmodified screen
            // whenever IT next visits this same trigger/screen, and PartyRosterController's
            // poll still converges this client's roster regardless.
            block.PostLocalChatLine("party is chosen by the host");
            PluginHost.Log("party-select: client copy suppressed - the host is picking "
                + "(PartyRosterController's poll converges the roster)"
                + (PartySelectSuppressionRule.ShouldRelay(_enabled, _relayEnabled, isClientInLiveSession)
                    ? " - no recent local trigger to relay"
                    : string.Empty));
        }
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
    private static bool TryGetRegisteredPartySelectionPanel(out nint panel, out nint guiManager, out nint gui)
    {
        panel = 0;
        guiManager = 0;
        gui = 0;

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

        gui = Deref(clientInternal + ClientInternalGui);
        if (!LooksLikeHeapPointer(gui))
        {
            gui = 0;
            return false;
        }

        guiManager = Deref(gui + 0x38);
        panel = Deref(gui + GuiPartySelectionPanel);
        if (!LooksLikeHeapPointer(guiManager) || !LooksLikeHeapPointer(panel))
        {
            panel = 0;
            guiManager = 0;
            gui = 0;
            return false;
        }

        // PanelExists is a pointer comparison over the manager's registered array. Do this
        // before reading the candidate vtable: +0x78 is known to remain non-null after our
        // sweep removes the object, so a vtable check alone is not an open-panel test.
        if (PanelExistsFn(guiManager, panel) == 0)
        {
            panel = 0;
            guiManager = 0;
            gui = 0;
            return false;
        }

        // Only a registered candidate is safe to inspect beyond the pointer itself.
        if (Deref(panel) != PartySelectionVtable)
        {
            panel = 0;
            guiManager = 0;
            gui = 0;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Public entry for <see cref="PartySelectHostRequestController"/>: resolves the same
    /// registered, vtable-checked party-selection panel this controller uses, without exposing
    /// the <c>gui</c> pointer or any of the suppression-specific state above.
    /// </summary>
    internal static bool TryGetOpenPartySelectionPanel(out nint panel, out nint guiManager) =>
        TryGetRegisteredPartySelectionPanel(out panel, out guiManager, out _);

    /// <summary>Public entry for <see cref="PartySelectHostRequestController"/>'s own close
    /// watch: the same liveness check <see cref="OnFrame"/> uses mid-teardown, exposed so that
    /// controller never has to dereference a panel it did not itself resolve.</summary>
    internal static bool PanelIsRegistered(nint guiManager, nint panel) =>
        guiManager != 0 && panel != 0 && PanelExistsFn(guiManager, panel) != 0;

    private static nint Deref(nint p) => p == 0 ? 0 : *(nint*)p;

    private static bool LooksLikeHeapPointer(nint p) =>
        p is > 0x10000 and < 0x7FFF0000 && (p & 3) == 0;
}
