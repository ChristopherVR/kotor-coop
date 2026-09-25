namespace KotorCoop.Plugin;

/// <summary>Outcomes of an open attempt, echoed to the agent via
/// <see cref="SharedBlockView.PartySelectHostStatus"/>.</summary>
internal enum PartySelectHostStatusCode : uint
{
    None = 0,
    Opened = 1,
    Refused = 2,
}

/// <summary>
/// The host half of the party-select relay (SharedBlock.PartySelect.cs): acts on the agent's
/// "open party-select with these params" request by calling
/// <see cref="Odyssey.ShowPartySelectionGui"/> — the same native call the retail
/// <c>ShowPartySelectionGUI</c> nwscript action reaches — and then watches the screen it just
/// opened to tell the agent, later, whether the host confirmed or cancelled
/// (<see cref="PartySelectOutcomeRule"/>).
///
/// Runs from the same <see cref="PluginHost.OnFrame"/> schedule
/// <see cref="PartySelectSuppressionController"/> does, right after it: suppression only ever
/// fires on a client, this only ever fires on a host, so the two never race for the same
/// screen on the same instance.
/// </summary>
internal sealed unsafe class PartySelectHostRequestController(SharedBlockView block)
{
    private uint _appliedRequestSerial;

    /// <summary>The panel this controller opened and is watching for a close, or 0 while
    /// nothing is in flight.</summary>
    private nint _watchedPanel;

    private nint _watchedGuiManager;

    /// <summary><c>Odyssey.TryReadPartyRoster</c>'s <c>partyMask</c> at the moment the watched
    /// panel opened — <see cref="PartySelectOutcomeRule"/>'s baseline.</summary>
    private uint _rosterMaskAtOpen;

    private uint _outcomeSerial;

    /// <summary>Module change: a request filed for the OLD world (or a screen this instance
    /// had open in it) must not be acted on, or watched, in the new one.</summary>
    public void Invalidate()
    {
        _appliedRequestSerial = block.PartySelectHostRequestSerial;
        _watchedPanel = 0;
        _watchedGuiManager = 0;
    }

    public void OnFrame()
    {
        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        if (_watchedPanel != 0)
        {
            PollOutcome();
            return;
        }

        uint serial = block.PartySelectHostRequestSerial;
        if (serial == 0 || serial == _appliedRequestSerial)
        {
            return;
        }

        _appliedRequestSerial = serial;

        string exitScript = block.PartySelectHostExitScript;
        int npc1 = block.PartySelectHostForceNpc1;
        int npc2 = block.PartySelectHostForceNpc2;

        bool opened = Odyssey.ShowPartySelectionGui(exitScript, npc1, npc2, out string detail);
        if (opened && TryLocatePanel(out nint panel, out nint guiManager))
        {
            _watchedPanel = panel;
            _watchedGuiManager = guiManager;
            _rosterMaskAtOpen = Odyssey.TryReadPartyRoster(out _, out uint mask) ? mask : 0;
            block.PartySelectHostStatus = (uint)PartySelectHostStatusCode.Opened;
            PluginHost.Log($"party-select: opened request #{serial} for the host "
                + $"(exit_script='{exitScript}', forceNpc1={npc1}, forceNpc2={npc2})");
        }
        else
        {
            block.PartySelectHostStatus = (uint)PartySelectHostStatusCode.Refused;
            PluginHost.Log($"party-select: refused request #{serial} - {detail}");
        }

        // Status first, applied last: the agent reads applied, then status, so a torn read
        // can only ever show "not handled yet" — the same convention GalaxyDepartureController
        // uses.
        block.PartySelectHostApplied = serial;
    }

    /// <summary>
    /// <c>ShowPartySelectionGui</c> reports success by <c>CGuiInGame+0x78</c> being non-null
    /// right after the call, but that raw pointer is the same one
    /// <see cref="PartySelectSuppressionController"/>'s own header warns against trusting
    /// without a registration check — so this re-resolves through the exact same
    /// manager-registered path that controller uses, rather than reusing the bare pointer
    /// <c>ShowPartySelectionGui</c> already saw.
    /// </summary>
    private static bool TryLocatePanel(out nint panel, out nint guiManager) =>
        PartySelectSuppressionController.TryGetOpenPartySelectionPanel(out panel, out guiManager);

    private void PollOutcome()
    {
        bool stillOpen = PartySelectSuppressionController.PanelIsRegistered(_watchedGuiManager, _watchedPanel);
        uint rosterNow = Odyssey.TryReadPartyRoster(out _, out uint mask) ? mask : _rosterMaskAtOpen;

        PartySelectOutcomeCode outcome = PartySelectOutcomeRule.Decide(stillOpen, _rosterMaskAtOpen, rosterNow);
        if (outcome == PartySelectOutcomeCode.Pending)
        {
            return;
        }

        _outcomeSerial++;
        block.PartySelectHostOutcome = (uint)outcome;
        block.PartySelectHostOutcomeSerial = _outcomeSerial;

        PluginHost.Log(outcome == PartySelectOutcomeCode.Confirmed
            ? "party-select: the host confirmed a party (roster changed)"
            : "party-select: the host closed the screen without changing the party (cancelled)");

        _watchedPanel = 0;
        _watchedGuiManager = 0;
    }
}
