namespace KotorCoop.Plugin;

// The hook-install sequence EnsureStarted runs, in LOAD-BEARING ORDER: CrashProbe first (see
// InstallCrashProbe — it must exist before anything else that could fault), then everything
// InstallHooks installs after the shared block and every controller exist. Reordering any of
// these is a behaviour change, not a refactor — the comment on each install says why its
// position matters. EnsureStarted calls InstallCrashProbe() and InstallHooks() at the same two
// points in its own sequence as this code used to run inline.
internal static unsafe partial class PluginHost
{
    private static void InstallCrashProbe(string directory)
    {
        // FIRST, before a single hook or controller exists. A fault during startup is
        // exactly as fatal as one during play, and the probe is what turns "the game
        // vanished" into a line naming the exception. Passed the game directory (not
        // read from _logPath) because it must resolve kotorcoop-crash.bin's path itself;
        // _logPath is already set by the time this runs, but CrashProbe does not reach
        // into PluginHost's fields for it.
        CrashProbe.Install(directory);

        // Second, and also before any hook: the clean-exit twin of the probe above. A
        // process that leaves through ExitProcess writes no fault and no Application-log
        // entry, and this is the only place that names that path.
        ExitProbe.Install();
    }

    private static void InstallHooks()
    {
        // coop.ini isolate_real_input (default 0, rig-only - docs/research/isolated-rig.md):
        // read BEFORE DirectInput8Create's caller wraps the mouse/keyboard devices below, so the
        // very first CreateDevice already sees the right flag. Never touches a real player;
        // only tools/demo.ps1 or demo3.ps1's -Isolated switch ever writes a 1, and only in the
        // two rig installs, only for the run.
        DirectInputKeyboardHook.IsolateRealInput = PluginConfig.ReadBool("isolate_real_input", false);
        if (DirectInputKeyboardHook.IsolateRealInput)
        {
            Log("input isolation: ON (coop.ini isolate_real_input=1) - real keyboard/mouse "
                + "input to this game is dropped; the plugin's own synthetic input (input/gamepad "
                + "console verbs) still works");
        }

        // Install the frame hook from here because DirectInput8Create - our caller - is
        // verified to run on the game's main thread during startup. Installing from the
        // worker thread would patch an import from the wrong thread.
        if (FrameHook.Install(OnFrame, out string hookDiagnostic))
        {
            Log($"frame hook installed: {hookDiagnostic}");
        }
        else
        {
            Log($"frame hook NOT installed: {hookDiagnostic}");
        }

        // One verify-bytes getter detour covers store row display, affordability/persuasion
        // checks and the click handlers. At 100% it returns the retail integer unchanged.
        if (StorePriceHook.Install(_view!, out string storePriceDiagnostic))
        {
            Log($"store prices: {storePriceDiagnostic}");
        }
        else
        {
            Log($"store price hooks NOT installed: {storePriceDiagnostic}");
        }

        // local_var_sync (docs/research/quest-sync.md §8.3): the two SetLocal* call sites inside
        // ExecuteCommandSetLocalValue. Only when the key is on; the audited allowlist is now live-verified and defaults ON.
        if (LocalVarController.Enabled)
        {
            LocalVarWatchTable.Build();
            if (LocalVarSyncHook.Install(_view!, out string localVarDiagnostic))
            {
                Log($"local vars: {localVarDiagnostic}");
            }
            else
            {
                Log($"local-var hook NOT installed: {localVarDiagnostic}");
            }
        }

        // The agent reads this word before draining the capture ring or handing out applies.
        _view!.SetLocalVarStatus(LocalVarSyncHook.IsInstalled, LocalVarController.Enabled);

        // Intercept the console command interpreter so co-op verbs route to the block and
        // are swallowed (no "function <x> not found"), while real typos still get flagged.
        // Independent of the frame hook, so the co-op console works at the menu too.
        // _view is set unconditionally by ConstructControllers, called just before this —
        // still nullable at its declaration (PluginHost.cs) because it also stands for
        // "startup has not gotten this far yet".
        if (FuncInterpHook.Install(_view!, out string interpDiagnostic))
        {
            Log($"console interp hooked: {interpDiagnostic}");
        }
        else
        {
            Log($"console interp NOT hooked: {interpDiagnostic}");
        }

        // A per-frame callback that also fires at the main menu (the SwapBuffers hook is
        // dormant there behind the menu movie), used to drive the Co-op menu button.
        if (MainLoopHook.Install(MenuFrame, out string menuHookDiagnostic))
        {
            Log($"menu frame hook installed: {menuHookDiagnostic}");
        }
        else
        {
            Log($"menu frame hook NOT installed: {menuHookDiagnostic}");
        }

        // The world-up party message crashes on a half-initialised player creature's
        // null faction; the guard routes that one case onto the handler's own
        // default-faction path. Required for the plugin-driven load (the browser's
        // click-to-join lands through it).
        if (PartyFactionGuard.Install(out string guardDiagnostic))
        {
            Log($"party faction guard: {guardDiagnostic}");
        }
        else
        {
            Log($"party faction guard NOT installed: {guardDiagnostic}");
        }

        // One-shot animations (draws, swings, flourishes, item use) are dispatched and
        // finished between polls, so they are captured by hooking the engine's own
        // dispatch rather than sampled.
        if (AnimationHook.Install(out string animationDiagnostic))
        {
            Log($"animations: {animationDiagnostic}");
        }
        else
        {
            Log($"animation hook NOT installed: {animationDiagnostic}");
        }

        // An NPC's one-line bark never enters the dialogue system, so nothing else in the
        // mod can see it. Opt-out because it patches a hot engine entry.
        if (PluginConfig.ReadBool("bark_sync", true))
        {
            if (BarkHook.Install(out string barkDiagnostic))
            {
                Log($"barks: {barkDiagnostic}");
            }
            else
            {
                Log($"bark hook NOT installed: {barkDiagnostic}");
            }
        }

        // Closes docs/research/item-identity.md §12's own open gap: a live CSWSItem retains
        // no template resref of its own, so an item a vanilla script places directly in
        // inventory (or a giveloot/scriptgive/storeinsert/partypooladd cheat, which funnels
        // through the identical VM command) could previously only ever be described by its
        // Tag, which sometimes genuinely diverges (docs/research/playthrough-endar-spire.md
        // step 5's live-confirmed g_i_progspike01/k_computer_spike). Opt-out because it is
        // this plugin's first MID-FUNCTION splice (see ItemTemplateSpawnHook's own remarks) —
        // default ON, but new enough to want an escape hatch beyond the usual prologue guard.
        if (PluginConfig.ReadBool("item_template_spawn_sync", true))
        {
            if (ItemTemplateSpawnHook.Install(_view!, out string itemTemplateSpawnDiagnostic))
            {
                Log($"item-template spawns: {itemTemplateSpawnDiagnostic}");
            }
            else
            {
                Log($"item-template spawn hook NOT installed: {itemTemplateSpawnDiagnostic}");
            }
        }

        // The joiner-never-becomes-a-Jedi gap (docs/review-seamless-2026-09-18.md blocker #1,
        // docs/research/class-and-power-grants.md): a mid-function splice inside
        // CSWVirtualMachineCommands::ExecuteCommandAddMultiClass that captures every
        // AddMultiClass(nClass, oTarget) a script runs, for ClassGrantController to relay.
        // Behind class_grant_sync (default ON since 2026-09-19: the splice and the relay ran live that day),
        // and with the key off nothing here reads or writes the block region either.
        if (PluginConfig.ReadBool("class_grant_sync", true))
        {
            if (AddMultiClassHook.Install(out string addMultiClassDiagnostic))
            {
                Log($"class-grant: {addMultiClassDiagnostic}");
            }
            else
            {
                Log($"class-grant hook NOT installed: {addMultiClassDiagnostic}");
            }
        }

        // Per-object local variables (docs/research/quest-sync.md §8.3): two whole-instruction
        // splices at the lower CSWVarTable setters
        // The script-agnostic second solo-segment trigger (docs/research/star-forge-finale.md
        // §11): a detour on every RemovePartyMember VM call, independent of and in addition to
        // SoloSegmentController's own roster-count poll. Installed unconditionally, like
        // BarkHook/PhantomHitHook — its debug log line and the `removeparty` dev verb are meant
        // to work whether or not solo_segment_spectate itself is on; SoloSegmentController's own
        // existing `_enabled` gate is what keeps the FEATURE (beginning a real segment) behind
        // that key, unchanged. `removeparty_hook` (default ON) is a DIAGNOSTIC-only kill key —
        // added 2026-08-29 for the transition-crash DLL bisect (docs/research/transition-crashes.md)
        // since this is a mid-function detour with no prior escape hatch beyond the usual
        // verify-bytes-or-refuse guard; not meant to ship OFF.
        if (PluginConfig.ReadBool("removeparty_hook", true))
        {
            if (RemovePartyMemberHook.Install(out string removePartyDiagnostic))
            {
                Log($"solo segment: {removePartyDiagnostic}");
            }
            else
            {
                Log($"RemovePartyMember hook NOT installed: {removePartyDiagnostic}");
            }
        }
        else
        {
            Log("RemovePartyMember hook NOT installed: disabled by coop.ini removeparty_hook=0");
        }

        NativeSaveCompletionHook.Block = BlockAddress;
        NativeSaveCompletionHook.BeforeSerialization = BoundSaveSerializationParty;
        NativeSaveCompletionHook.Install(out string saveCompletionDiagnostic);
        Log(saveCompletionDiagnostic);
        PartyOccupantHook.PublicationBank = BlockAddress + SharedBlock.OffsetLocalPartyOccupants;
        PartyOccupantHook.Install(out string occupantDiagnostic);
        Log($"party occupant: {occupantDiagnostic}");

        // Which dialogue reply the player picks is dispatched and gone between frames,
        // so it is captured by hooking the engine's own reply entry rather than sampled.
        if (DialogueReplyHook.Install(out string replyDiagnostic))
        {
            Log($"dialogue: {replyDiagnostic}");
        }
        else
        {
            Log($"dialogue reply hook NOT installed: {replyDiagnostic}");
        }

        // The other half of the same rule, and until now the missing half: a SPECTATOR must not
        // be able to skip or advance somebody else's conversation. Skip is a different engine
        // call one branch away from the reply commit inside the same switch — see
        // DialogueSkipHook for the disassembly and for the live bug (a host joined a client's
        // conversation and skipped a line).
        if (DialogueSkipHook.Install(out string skipDiagnostic))
        {
            Log($"dialogue: {skipDiagnostic}");
        }
        else
        {
            Log($"dialogue skip hook NOT installed: {skipDiagnostic}");
        }

        // One conversation for the whole session (the BG3 rule): while a peer holds the
        // token, this instance cannot start a dialogue at all. The spectate follow is
        // the one sanctioned exception (DialogueGateHook.BypassGrants).
        // Damage landing on a peer's phantom, so it can be forwarded to their real
        // character. Read-only and filtered to phantom ids, but it sits on every
        // damage event in the game, so it is verify-bytes like the rest.
        if (PhantomHitHook.Install(out string hitDiagnostic))
        {
            Log($"combat: {hitDiagnostic}");
        }
        else
        {
            Log($"phantom-hit watch NOT installed: {hitDiagnostic}");
        }

        // Non-damage effects (stun, paralyze, knockdown, slow, frightened, sleep, choke,
        // Force push/whirlwind) landing on a peer's phantom. Installed unconditionally,
        // like PhantomHitHook above — it is verify-bytes-or-refuse and inert unless a
        // phantom is actually watched — but PeerEffectController gates the PUBLISH and
        // APPLY halves behind coop.ini effect_sync (default off) until live-verified.
        // See docs/research/effect-sync.md.
        if (PhantomEffectHook.Install(out string effectDiagnostic))
        {
            Log($"combat: {effectDiagnostic}");
        }
        else
        {
            Log($"phantom-effect watch NOT installed: {effectDiagnostic}");
        }

        // The DETECTION half docs/research/trigger-relay.md §6 shipped without: which
        // movie a mid-module PlayMovie call (no transition involved) actually played.
        // Installed unconditionally, like the two hooks above — verify-bytes-or-refuse and
        // inert unless something is actually captured — but MidModuleMovieController gates
        // the PUBLISH and ACT halves behind coop.ini movie_relay (default off) until
        // live-verified. See docs/research/cinematics.md's 2026-08-22 section.
        if (MoviePlayHook.Install(out string moviePlayDiagnostic))
        {
            Log($"cinematic: {moviePlayDiagnostic}");
        }
        else
        {
            Log($"mid-module movie watch NOT installed: {moviePlayDiagnostic}");
        }

        // See docs/research/dialogue-join-latency.md "Far-join live pass": a joiner left
        // more than the engine's own 10-unit start-range away never reaches StartDialog at
        // all in this build (the player character is drive-controlled, not walked by the
        // action queue), so this teleports them into range for an automatic join
        // (force_dialogue_party, or the test hook) and restores their spot afterward.
        // Default off pending a live pass on a real click's own walk, which this must not
        // visibly disturb.
        Odyssey.DialogueJoinTeleportEnabled = PluginConfig.ReadBool("dialogue_join_teleport", true);

        // Whether forwarded-damage applies (PeerDamageController/IncomingDamageController/
        // CompanionDamageController) pass a real creator through to the receiving engine's own
        // EffectDamage, so its aggro/last-attacker bookkeeping sees who really hit it instead of
        // nobody. Default OFF, NEEDS-LIVE — see docs/research/peer-damage-ledger.md §8 for the
        // RE this rests on. Logged once here, at startup, rather than per controller: it is one
        // session-wide switch and every consumer reads the same flag.
        Odyssey.DamageCreatorAttributionEnabled = PluginConfig.ReadBool("damage_creator_attribution", false);
        Log(Odyssey.DamageCreatorAttributionEnabled
            ? "combat: damage_creator_attribution=ON - forwarded damage carries a real creator "
                + "id where one can be named (a peer's phantom for owner-applied enemy/companion "
                + "hits, a per-attacker id for incoming phantom hits); falls back to no creator "
                + "otherwise (see combat log lines for individual refusals)"
            : "combat: damage_creator_attribution=OFF (default) - forwarded damage applies with "
                + "no creator, as before");

        // The active retarget this instance's mirrored enemies need §8.10 found the passive
        // creator write does not provide: on the HOST, PeerDamageController tracks per-creature
        // threat from forwarded peer damage and orders a lopsided fight's target directly
        // through Odyssey.AttackTarget. Live-verified 2026-09-03 and ON in the shipped profile
        // (the enemy splits between the players instead of ignoring the second one); the
        // fallback for an absent key stays OFF — docs/research/peer-damage-ledger.md §9, §11.
        Odyssey.ThreatRetargetEnabled = PluginConfig.ReadBool("threat_retarget", true);
        Log(Odyssey.ThreatRetargetEnabled
            ? "combat: threat_retarget=ON - a creature whose tracked threat clearly favours a "
                + "peer over its current attack target will be ordered onto that peer directly "
                + "(see combat log 'threat:' lines)"
            : "combat: threat_retarget=OFF (set to 0 in coop.ini) - "
                + "mirrored enemies keep whichever target the engine's own AI picked");

        DialogueGateHook.Enabled = PluginConfig.ReadBool("dialogue_exclusive", true);
        if (DialogueGateHook.Install(out string gateDiagnostic))
        {
            Log($"dialogue: {gateDiagnostic}"
                + (DialogueGateHook.Enabled ? string.Empty : " (disabled by coop.ini)"));
        }
        else
        {
            Log($"dialogue gate NOT installed: {gateDiagnostic}");
        }

        // Item 1 (docs/research/dialogue-sync.md's 2026-08-23 section): the forced-vs-
        // ordinary conversation signal DialogueAutoJoinRule needs, so the OTHER player is
        // only ever auto-pulled into a conversation the vanilla engine itself would have
        // forced regardless of position — never an ordinary player-clicked chat.
        if (DialogueForceFlagHook.Install(out string forceFlagDiagnostic))
        {
            Log($"dialogue: {forceFlagDiagnostic}");
        }
        else
        {
            Log($"forced-conversation tap NOT installed: {forceFlagDiagnostic}");
        }

        // The container half of the same one-at-a-time rule — see ContainerUseHook and
        // docs/research/container-sync.md.
        ContainerUseHook.Enabled = PluginConfig.ReadBool("container_locks", true);

        // Closes the container-loot race (docs/roadmap-seamless.md Tier 1 item 4): default ON
        // because the fallback fails SAFE — if the host never answers within
        // ContainerLockArbiter.TimeoutMs, the deferred click simply proceeds exactly as it did
        // before this flag existed (see docs/research/container-sync.md's
        // container_lock_arbitration section for the reasoning). Meaningless (and left off)
        // when container_locks itself is off, since there is then no gate to defer at all.
        ContainerUseHook.ArbitrationEnabled =
            ContainerUseHook.Enabled && PluginConfig.ReadBool("container_lock_arbitration", true);

        if (ContainerUseHook.Install(out string containerGateDiagnostic))
        {
            Log($"container: {containerGateDiagnostic}"
                + (ContainerUseHook.Enabled ? string.Empty : " (disabled by coop.ini)")
                + (ContainerUseHook.Enabled
                    ? (ContainerUseHook.ArbitrationEnabled
                        ? "; container_lock_arbitration=1 (host-arbitrated)"
                        : "; container_lock_arbitration=0 (legacy eventually-consistent claim)")
                    : string.Empty));
        }
        else
        {
            Log($"container-use gate NOT installed: {containerGateDiagnostic}");
        }

        // LIVE-PASS ADDITION: watches every REAL CloseInventory so PlaceableController's
        // durable "ever opened" mirror does not clobber a fresh close — see
        // ContainerCloseHook's own remarks and docs/research/container-sync.md's live-pass
        // section. Shares container_locks with ContainerUseHook (same feature, same flag).
        ContainerCloseHook.Enabled = ContainerUseHook.Enabled;
        if (ContainerCloseHook.Install(out string containerCloseDiagnostic))
        {
            Log($"container: {containerCloseDiagnostic}"
                + (ContainerCloseHook.Enabled ? string.Empty : " (disabled by coop.ini)"));
        }
        else
        {
            Log($"container-close watch NOT installed: {containerCloseDiagnostic}");
        }

        // Opt-in diagnostic: records who writes CGuiInGame+0xB4, which is the one thing
        // that would settle why a wedged instance's conversation flag comes straight back.
        DialogStateWatchHook.Enabled = PluginConfig.ReadBool("dialog_state_watch", false);
        if (DialogStateWatchHook.Enabled)
        {
            Log(DialogStateWatchHook.Install(out string watchDiagnostic)
                ? $"dialogue: {watchDiagnostic}"
                : $"dialogue: dialog-state watch NOT installed: {watchDiagnostic}");
        }

        // A conversation ended from our frame hook can be followed by one last reply list
        // the server had already queued, and the dialogue camera resolves its participants
        // with no null check. Odyssey.CloseDialogue removes the source; this is the net.
        if (ShotCameraGuardHook.Install(out string shotDiagnostic))
        {
            Log($"dialogue: {shotDiagnostic}");
        }
        else
        {
            Log($"shot-camera guard NOT installed: {shotDiagnostic}");
        }

        // Party-placed mine replication's single-authority damage gate (docs/research/mines.md
        // §14.4): a five-byte call-site redirect of the one GetCanFireMineOnObject call, which
        // refuses a party-placed mine only while PlacedMineController says this is a client in a
        // live session. Behind placed_mine_sync (default OFF, NEEDS-LIVE); with the key off the
        // call site is never touched.
        if (PlacedMineController.Enabled)
        {
            if (PlacedMineFireGateHook.Install(out string placedMineDiagnostic))
            {
                Log($"placed mines: {placedMineDiagnostic}");
            }
            else
            {
                Log($"placed mines: fire gate NOT installed ({placedMineDiagnostic}); party-placed mines stay local-only");
            }

            Log(Odyssey.VerifyPlacedMineRoute(out string routeDiagnostic)
                ? $"placed mines: {routeDiagnostic}"
                : $"placed mines: {routeDiagnostic}; no replica will be built");
        }

        // The minimap draws exactly one arrow and has no note or party loop, so a peer
        // gets there by drawing that same arrow again, inside the viewport the engine
        // has already opened. coop.ini minimap_peers=0 opts out.
        if (PluginConfig.ReadBool("minimap_peers", true))
        {
            if (MinimapPeerHook.Install(out string minimapDiagnostic))
            {
                Log($"minimap: {minimapDiagnostic}");
            }
            else
            {
                Log($"minimap peers NOT installed: {minimapDiagnostic}");
            }
        }

        // The client must let the host execute the two native Leviathan control-transfer
        // action scripts; running them locally would move/switch the client's own character.
        // The gate shares the single CSWSDialog::RunScript splice with the optional observer
        // below, and skips only two exact resrefs in the proven Leviathan module on a proven
        // client. Unknown role/module/name and any byte mismatch fail open.
        bool dialogueScriptHookInstalled = DialogueNodeActionGateHook.Install(_view!, out string leviathanGateDiagnostic);
        if (dialogueScriptHookInstalled)
        {
            Log($"Leviathan dialogue actions: {leviathanGateDiagnostic}");
        }
        else
        {
            Log($"Leviathan dialogue action gate NOT installed: {leviathanGateDiagnostic}");
        }

        // Korriban's zero/one-companion solo boundary has two exact script cues: the Uthar
        // dialogue action begins the latch, and the later m35 area OnEnter can complete it
        // only after the final-test global and original roster identities are restored.
        bool areaScriptHookInstalled = KorribanAreaEnterHook.Install(_view!, out string korribanAreaDiagnostic);
        if (areaScriptHookInstalled)
        {
            Log($"Korriban area scripts: {korribanAreaDiagnostic}");
        }
        else
        {
            Log($"Korriban area-script observer NOT installed: {korribanAreaDiagnostic}");
        }
        SetKorribanScriptObserversReady(dialogueScriptHookInstalled && areaScriptHookInstalled);
        if (!dialogueScriptHookInstalled || !areaScriptHookInstalled)
        {
            Log("Korriban exact solo-segment detection disabled because both verified script observers are required");
        }

        // A default-OFF diagnostic, not a feature: four read-only splices on the delayed-
        // script-situation path (DelayCommand -> AddEventDeltaTime -> the AI master's drain),
        // for docs/research/minigames.md §24's root-cause question. See DelayEventProbeHook.
        ReviveEffectLifetimeProbe.Enabled = PluginConfig.ReadBool("revive_effect_probe", false);
        if (PluginConfig.ReadBool("delay_probe", false) || ReviveEffectLifetimeProbe.Enabled)
        {
            if (DelayEventProbeHook.Install(out string delayProbeDiagnostic))
            {
                Log($"minigame: {delayProbeDiagnostic}");
            }
            else
            {
                Log($"minigame: delayed-event probe NOT installed: {delayProbeDiagnostic}");
            }
        }
        if (ReviveEffectLifetimeProbe.Enabled)
        {
            bool installed = ReviveEffectLifetimeProbe.InstallDestructor(out string effectProbeDiagnostic);
            Log($"reviveprobe: destructor observer installed={installed}: {effectProbeDiagnostic}");
        }

        // A default-OFF diagnostic (coop.ini gui_probe=1): a splice on Show/HideSWInGameGui
        // plus, from the frame hook, a hardware watchpoint on CGuiInGame+0x30 (FieldWriteWatch).
        // Answered the galaxy-map relay's "who writes +0x30" question on 2026-09-03; kept for
        // the next one. See ShowInGameGuiProbeHook.
        FieldWriteWatch.WatchStatusSummary = PluginConfig.ReadBool("gui_summary_watch", false);
        if (FieldWriteWatch.WatchStatusSummary)
        {
            Log("gui-summary-watch: enabled read-only writes/backtrace probe on typed StatusSummary+0x64");
        }
        if (PluginConfig.ReadBool("gui_probe", false))
        {
            if (ShowInGameGuiProbeHook.Install(out string guiProbeDiagnostic))
            {
                Log($"gui-probe: {guiProbeDiagnostic}");
            }
            else
            {
                Log($"gui-probe: NOT installed: {guiProbeDiagnostic}");
            }
        }

        // The engine reverts any pause that lacks a valid "PausedBy" record within one
        // tick, which in a loopback session is every pause there is — space bar, HUD
        // button and mirror alike. See PauseSanityPatch.
        if (PauseSanityPatch.Install(out string pausePatchDiagnostic))
        {
            Log($"pause: {pausePatchDiagnostic}");
        }
        else
        {
            Log($"pause sanity patch NOT installed: {pausePatchDiagnostic}");
        }

        // The HUD pause button toggles on server mask bit 0x02, which is ALSO the
        // focus-loss reason FocusKeeper clears every 30 frames — so the button's "am I
        // paused?" read is always false and it can pause but never unpause. Measured:
        // PausesCleared went 0 -> 1 -> 2, one per click. See PauseButtonHook.
        if (PauseButtonHook.Install(out string pauseButtonDiagnostic))
        {
            Log($"pause: {pauseButtonDiagnostic}");
        }
        else
        {
            Log($"pause button NOT hooked: {pauseButtonDiagnostic}");
        }

        // Co-op death handling: suppress the control auto-swap and the game-over screen.
        // Both patches stay inert until the agent turns them on, so a plugin with no
        // session behaves exactly like stock KOTOR.
        if (DeathController.Install(out string deathDiagnostic))
        {
            Log($"death handling: {deathDiagnostic}");
        }
        else
        {
            Log($"death handling NOT installed: {deathDiagnostic}");
        }

        // Galaxy-map gating (docs/research/galaxy-map-and-party-select.md, "Galaxy-map
        // gating" section): on a CLIENT under follow_lead=host, refuse this instance's own
        // accept-button click on the galaxy map and post "only the host can choose a
        // destination" instead of running the confirm script. Installed unconditionally
        // (verify-bytes-or-refuse) - GalaxyMapConfirmHook itself reads galaxy_map_host_only
        // and follow_lead and only ever refuses a client under host-lead; a host's own
        // confirm is always let through untouched. NEEDS-LIVE.
        if (GalaxyMapConfirmHook.Install(out string galaxyMapDiagnostic))
        {
            Log($"galaxy-map: {galaxyMapDiagnostic}");
        }
        else
        {
            Log($"galaxy-map confirm gate NOT installed: {galaxyMapDiagnostic}");
        }

        // Floaty-text null-node guard (FloatyTextNodeGuardHook's own header): the engine
        // dereferences a model node it never null-checks when it draws "+N XP" / "+N" /
        // "miss" over an object; a materialised stand-in or a phantom whose client model has
        // no node yet made that a client crash at 0x688F54 twice (effect-sync.md §11.4 and
        // the 2026-09-03 spawncreature/slay round). Installed unconditionally on both halves
        // - the null path is the same no-op exit the engine's own no-model guard takes.
        if (FloatyTextNodeGuardHook.Install(out string floatyDiagnostic))
        {
            Log($"floaty: {floatyDiagnostic}");
        }
        else
        {
            Log($"floaty null-node guard NOT installed: {floatyDiagnostic}");
        }

        // Save-time flush (docs/research/save-ghosts.md §4, roadmap #36's second half): a
        // manual save — F4, the save panel, the `save` verb — is held at the one server entry
        // every save reaches (CServerExoAppInternal::SaveGame) while the plugin's own spawns
        // are destroyed, then re-issued unchanged. Verify-bytes-or-refuse; coop.ini
        // save_flush=0 opts out. NEEDS-LIVE — the recipe is in the hook's header.
        // A joiner's own scripts may not destroy a creature the host owns and has alive
        // (Rilka, 2026-09-26). Verify-bytes-or-refuse; coop.ini script_destroy_guard=0 opts out.
        EnemyPresenceController.ScriptDestroyGuardEnabled = PluginConfig.ReadBool("script_destroy_guard", true);
        if (EnemyPresenceController.ScriptDestroyGuardEnabled)
        {
            bool guarded = ScriptDestroyGuardHook.Install(out string destroyGuardDiagnostic);
            Log($"script destroy guard {(guarded ? "ready" : "NOT installed")}: {destroyGuardDiagnostic}");
        }

        bool moveObserverInstalled = ModuleMoveRequestHook.Install(out string moveObserverDiagnostic);
        Log($"module move observer {(moveObserverInstalled ? "ready" : "NOT installed")}: {moveObserverDiagnostic}");

        // Exact save selection/request provenance (v74): observe the retail main-menu
        // selected-entry call and the server LoadGame dispatcher before either begins. This
        // is diagnostic input for future save-bound consumers only; it does not claim load
        // completion and performs no world or hit-point writes.
        if (LoadProvenanceHook.Install(_view!, out string loadProvenanceDiagnostic))
        {
            Log($"load provenance: {loadProvenanceDiagnostic}");
        }
        else
        {
            Log($"load provenance NOT installed: {loadProvenanceDiagnostic}");
        }

        Log(NewGameIntentHook.Install(_view!, out string newGameIntentDiagnostic)
            ? $"new-game intent: {newGameIntentDiagnostic}"
            : $"new-game intent NOT installed: {newGameIntentDiagnostic}");

        if (SaveFlushHook.Install(out string saveFlushDiagnostic))
        {
            Log($"save flush: {saveFlushDiagnostic}");
        }
        else
        {
            Log($"save flush NOT installed: {saveFlushDiagnostic}");
        }
    }
}
