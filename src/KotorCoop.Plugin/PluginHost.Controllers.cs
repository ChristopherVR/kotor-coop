namespace KotorCoop.Plugin;

// Construction of every per-frame controller PluginHost drives, in the SAME order as before
// the split. The order here is mostly incidental (each constructor only needs the shared
// view), but it is kept verbatim anyway so a diff against history stays meaningful — the
// order that actually matters for behaviour is the per-frame CALL order in
// PluginHost.Frame.cs, which is independently justified there.
internal static unsafe partial class PluginHost
{
    private static void ConstructControllers(string directory)
    {
        _view = new SharedBlockView((byte*)_block);
        _controller = new PhantomController(_view);
        _items = new ItemController(_view);
        _focus = new FocusKeeper(_view);
        _combat = new CombatController(_view);
        _liveness = new LivenessReconcileController(_view, _combat);
        _enemyPresence = new EnemyPresenceController(_view);
        _partyPresence = new PartyPresenceController(_view);
        _partyFollow = new PartyFollowController(_view);
        _incomingDamage = new IncomingDamageController(_view);
        _peerEffects = new PeerEffectController(_view);
        _peerDamage = new PeerDamageController(_view);
        _testDrive = new TestDriveController(_view);
        _cheats = new CheatController(_view);
        _doors = new DoorController(_view);
        _placeables = new PlaceableController(_view);
        _loot = new LootController(_view);
        _travel = new TravelController(_view);
        _menuLoad = new MenuLoadController(_view);
        _newCharacterCapture = new NewCharacterCaptureController(_view);
        _chargenDrive = new CharGenDriveController(_view);
        _connectMenu = new ConnectMenuController(_view);
        _chatInput = new ChatInputController();
        _passwordPrompt = new PasswordPromptController(_view);
        _browser = new ServerBrowserController(_view);
        _nativeBrowser = new NativeBrowserController(_view);
        _death = new DeathController(_view);
        _spectate = new SpectateController(_view);
        _chatOverlay = new ChatOverlayController(_view);
        // Beside the log, in the game directory the plugin was loaded from: the recall
        // history is per-install, which is what a player means by "my commands".
        _consoleAssist = new ConsoleAssistController(
            Path.Combine(directory, "kotorcoop-console-history.txt"));
        _dialogueSpectate = new DialogueSpectateController(_view);
        _encounters = new EncounterController(_view);
        _triggerRelay = new TriggerRelayController(_view);
        _movieRelay = new MidModuleMovieController(_view);
        _pause = new PauseController(_view);
        _combatState = new CombatStateController();
        _pauseKey = new PauseKeyController();
        _journal = new JournalController(_view);
        _mapMarkers = new MapMarkerController(_view);
        _mapReveal = new MapRevealController(_view);
        _portraits = new PortraitController(_view);
        _barks = new BarkController(_view);
        _itemTemplateSpawns = new ItemTemplateSpawnController(_view);
        _interactionLocks = new InteractionLockController(_view);
        _partyRoster = new PartyRosterController(_view);
        _partySelectSuppression = new PartySelectSuppressionController(_view);
        _controlReturn = new ControlReturnController(_view);
        _agentLiveness = new AgentLivenessController(_view);
        _connectionIndicator = new ConnectionIndicatorController(_view);
        _minigameNotice = new MinigameNoticeController(_view);
        _soloSegment = new SoloSegmentController(_view);
        _soloSegmentSpectate = new SoloSegmentSpectateController(_view);
        _minigameLock = new MinigameLockController(_view);

        // The minimap draw runs on the render path and has no other route to peer
        // positions, so it reads the block directly.
        MinimapPeerHook.Bind(_view);
    }
}
