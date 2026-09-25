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
/// The host half of the party-select relay (SharedBlock.PartySelect.cs). 2026-09-25 SECOND
/// PASS: acts on the agent's "open party-select for this request" by re-firing the CAUSAL
/// TRIGGER for the host's own player — <c>OdysseyTriggers.FireTriggerForLocalPlayer</c>, the
/// IDENTICAL, already-live-proven call the <c>firetrigger</c> dev verb makes — rather than
/// calling <c>CGuiInGame::ShowPartySelection</c> directly. See
/// <see cref="PartySelectSuppressionController"/>'s own header for why the direct call was
/// live-tested and found wrong (a false "opened" signal off a persistently-reused object, and
/// possibly-stale <c>exit_script</c> data).
///
/// Firing the trigger is synchronous (the script's own FIRST few lines run inline, inside this
/// call), but the retail script chain commonly stages a <c>DelayCommand</c> before it actually
/// reaches <c>ShowPartySelectionGUI</c> — so whether a real screen appears is NOT decided in the
/// same frame the trigger fires. This controller therefore fires once, then POLLS for the panel
/// to register over a several-second window before declaring the request opened or refused,
/// rather than checking same-frame the way the (now-abandoned) direct-call design did.
///
/// Runs from the same <see cref="PluginHost.OnFrame"/> schedule
/// <see cref="PartySelectSuppressionController"/> does, right after it: suppression only ever
/// fires on a client, this only ever fires on a host, so the two never race for the same
/// screen on the same instance.
/// </summary>
internal sealed unsafe class PartySelectHostRequestController(SharedBlockView block)
{
    private enum Phase
    {
        Idle,
        WatchingOpen,
        WatchingOutcome,
    }

    /// <summary>How long to poll for the panel to register after firing the trigger before
    /// declaring the request refused. Generous — a <c>DelayCommand(1.5, ...)</c>-shaped script
    /// (the swoop-taxi's own dispatch, §2.2) plus loading/frame slack.</summary>
    private const long OpenTimeoutMs = 8_000;

    private Phase _phase = Phase.Idle;

    private uint _appliedRequestSerial;

    /// <summary>The request currently being watched for an open, or 0 in <see cref="Phase.Idle"/>.</summary>
    private uint _pendingSerial;

    private long _watchOpenDeadlineMs;

    /// <summary>The panel this controller found open and is watching for a close, or 0 while
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
        _phase = Phase.Idle;
        _pendingSerial = 0;
        _watchedPanel = 0;
        _watchedGuiManager = 0;
    }

    public void OnFrame()
    {
        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        switch (_phase)
        {
            case Phase.WatchingOpen:
                PollWatchOpen();
                return;

            case Phase.WatchingOutcome:
                PollOutcome();
                return;
        }

        uint serial = block.PartySelectHostRequestSerial;
        if (serial == 0 || serial == _appliedRequestSerial)
        {
            return;
        }

        _appliedRequestSerial = serial;

        uint triggerId = block.PartySelectHostTriggerId;
        if (triggerId == 0)
        {
            Refuse(serial, "no known trigger to replay (the requester's own capture found no "
                + "recent local trigger to correlate — see PartySelectSuppressionController)");
            return;
        }

        uint hostPlayerId = Odyssey.GetPlayerObjectId();
        if (hostPlayerId == 0)
        {
            Refuse(serial, "no player loaded");
            return;
        }

        string result = OdysseyTriggers.FireTriggerForLocalPlayer($"0x{triggerId:X}", hostPlayerId);
        PluginHost.Log($"party-select: request #{serial} - {result}");

        if (!result.Contains("fired "))
        {
            Refuse(serial, result);
            return;
        }

        // Do NOT ack yet: the fire only ran the script's first few lines — whether a real
        // screen appears is decided by whatever that script does next, possibly after a
        // DelayCommand. See the class header.
        _pendingSerial = serial;
        _watchOpenDeadlineMs = Environment.TickCount64 + OpenTimeoutMs;
        _phase = Phase.WatchingOpen;
    }

    private void Refuse(uint serial, string detail)
    {
        block.PartySelectHostStatus = (uint)PartySelectHostStatusCode.Refused;
        block.PartySelectHostApplied = serial;
        PluginHost.Log($"party-select: refused request #{serial} - {detail}");
    }

    private void PollWatchOpen()
    {
        if (TryLocatePanel(out nint panel, out nint guiManager))
        {
            _watchedPanel = panel;
            _watchedGuiManager = guiManager;
            _rosterMaskAtOpen = Odyssey.TryReadPartyRoster(out _, out uint mask) ? mask : 0;

            block.PartySelectHostStatus = (uint)PartySelectHostStatusCode.Opened;
            block.PartySelectHostApplied = _pendingSerial;
            PluginHost.Log($"party-select: request #{_pendingSerial} opened for the host");

            _phase = Phase.WatchingOutcome;
            return;
        }

        if (Environment.TickCount64 < _watchOpenDeadlineMs)
        {
            return;
        }

        Refuse(_pendingSerial, $"no screen appeared within {OpenTimeoutMs} ms of firing the trigger");
        _phase = Phase.Idle;
        _pendingSerial = 0;
    }

    /// <summary>
    /// <c>PanelExists</c>-registered check — the same manager-registered path
    /// <see cref="PartySelectSuppressionController"/> uses, exposed here so this controller
    /// never has to guess from a raw <c>CGuiInGame+0x78</c> read (see the class header's own
    /// discussion of why that pointer alone proves nothing).
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
        _phase = Phase.Idle;
        _pendingSerial = 0;
    }
}
