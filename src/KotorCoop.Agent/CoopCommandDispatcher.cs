using System.Net;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// The runtime command surface: polls the in-game console channel and the main-menu Connect
/// button out of the shared block and drives the <see cref="SessionManager"/> accordingly, so
/// a player can <c>connect</c>, <c>disconnect</c> and <c>reconnect</c> from inside the game.
///
/// Replies flow back the other way — status lines and the "password required" prompt are
/// written into the block for the plugin overlay to render, and mirrored to stdout for the
/// headless case.
///
/// Split into <c>CoopCommandDispatcher.*.cs</c> partial-class files by area (connect/session,
/// browser, announcers, save/load, items, effects, movies, doors, staging cheats,
/// diagnostics). This file keeps the tick loop, the cheat-answer channel every area shares,
/// and <see cref="Dispatch"/>'s verb table — the table just indexes methods living in the
/// area files; primary-constructor parameters (<c>manager</c>, <c>bridge</c>, ...) and fields
/// declared in one file are reachable from every other partial-class file of the same type.
/// </summary>
public sealed partial class CoopCommandDispatcher(
    SessionManager manager,
    IGameBridge bridge,
    int defaultPort,
    Func<string, SessionSettings> settingsForPassword,
    Func<CoopConfig?>? loadConfig = null,
    Action? menuAutoLoad = null,
    string gameDirectory = "",
    KotorCoop.Steam.SteamInviteService? steam = null)
{
    private readonly KotorGameBridge? _kotor = bridge as KotorGameBridge;

    /// <summary>
    /// The serial of a staging cheat still waiting on the plugin, or 0 for none.
    ///
    /// Held rather than fired-and-forgotten because a cheat's WHOLE VALUE is its answer: an
    /// unattended verification run types `slay` and needs to know whether the kill happened
    /// here, went to the host, or was refused because the thing it aimed at turned out to be
    /// a phantom. Reporting "sent" and moving on would make the channel untestable.
    /// </summary>
    private uint _pendingCheatSerial;

    /// <summary>Whether the cheat in flight was filed by an automatic drain rather than a
    /// human's console line, so its answer belongs in the log and NOT on the player's
    /// screen. Live 2026-09-17: every materialised-loot give ("gave 'g_w_blstrpstl001' to
    /// 0x13B") was landing in the joiner's chat overlay mid-fight, one line per item per
    /// enemy stand-in — the plugin's answer to a request the player never typed.</summary>
    private bool _pendingCheatQuiet;

    /// <summary>
    /// When that cheat was filed, so an answer that never comes is reported as such.
    ///
    /// It really can never come: the cheat executor runs on the SwapBuffers frame hook, which
    /// is dormant behind the main-menu movie, so a cheat typed at the menu sits unread until
    /// a world loads. Waiting silently forever is the one behaviour a staging channel must
    /// not have — and it would also wedge the next cheat, because only one is in flight.
    /// </summary>
    private DateTime _pendingCheatFiled;

    /// <summary>How long to wait for the game thread. Generous: the plugin answers on its
    /// next frame, so anything past a couple of seconds means no frames are running.</summary>
    private static readonly TimeSpan CheatAnswerTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// ⚠ ROOT CAUSE 7 (2026-08-28, docs/research/party-pool-sync.md §26): a still-unanswered
    /// cheat's request bytes used to be silently overwritten by whichever call reached
    /// <see cref="SendCheat"/> next — the shared block has exactly ONE request slot, and the
    /// plugin's <c>CheatController.OnFrame</c> polls it once per frame, executing whatever is
    /// THERE at that moment. Every console-verb dispatch-table entry calls
    /// <see cref="SendCheat"/> directly with no in-flight check (only the three
    /// <c>DrainAutomatic*</c> callers ever checked <see cref="_pendingCheatSerial"/> first), so a
    /// human's own <c>pooladd</c> — or any other staging verb — issued while
    /// <c>DrainAutomaticPartyPool</c>'s own dequeued reconcile apply was still awaiting the
    /// plugin's answer clobbered it outright: the apply's kind/text/amount fields in the shared
    /// block were overwritten before the plugin ever read them, so the engine-side delta that
    /// apply represented never ran, yet nothing about the overwrite failed loudly — the LATER
    /// request's own answer arrives looking completely normal. <c>PartyPoolPump</c> had already
    /// marked that key <c>_applyInFlight</c> the moment it queued the apply (before dequeue), and
    /// that flag is only ever cleared by observing the engine actually move for the key (root
    /// cause 6) — which a clobbered apply never produces — so the key stalls PERMANENTLY: step 2
    /// excludes it from reconcile forever, with no error, no retry, and no loop-breaker trip
    /// (nothing is being published for a frozen key). This is the exact live shape §25's rig
    /// round found: three rapid host <c>pooladd</c>s, the wire silent for 5+ minutes with only 2
    /// of 3 units confirmed landed.
    ///
    /// <b>The fix.</b> <see cref="SendCheat"/> and the automatic apply drains
    /// (<c>DrainAutomaticLoot</c>/<c>DrainAutomaticPartyPool</c>/<c>DrainAutomaticStore</c>) are
    /// now the SAME serialized issuer for every cheat kind: a request that arrives while
    /// <see cref="_pendingCheatSerial"/> is still nonzero is queued in <see cref="_queuedCheats"/>
    /// rather than overwriting the shared block, and <see cref="ReflectCheatAnswer"/> fires the
    /// next queued request the instant the current one resolves (a matched answer OR a timeout) —
    /// never both console-verb and automatic-apply traffic touching the block at once, and never
    /// a request silently discarded because it lost a race for the single slot. Replies are still
    /// matched by serial (<c>applied != _pendingCheatSerial</c>, unchanged and already sound —
    /// this never misattributed an answer, it just let the SLOT itself get stolen out from under
    /// a still-waiting request). See <see cref="Pumps.PartyPoolPump"/>'s own root-cause-7 remarks
    /// for the second half of the fix: an apply whose request never even reaches the plugin (a
    /// dropped connection, a refusal with no free retry path, or — belt and suspenders — any
    /// other way a landing could still go missing) now self-heals via a timeout instead of
    /// staying stuck in <c>_applyInFlight</c> forever.
    /// </summary>
    private readonly Queue<QueuedCheat> _queuedCheats = new();

    /// <summary>Bounded the same way every other unattended-growth queue in this codebase is
    /// (<c>PartyPoolPump.MaxPendingApplies</c>, <c>LootPump</c>'s own queue): a human mashing
    /// staging verbs faster than the plugin can ever answer them must not grow this without
    /// limit. Vastly more generous than any real recipe needs — the automatic drains are already
    /// throttled to at most one dequeue per idle tick each, so this only ever protects against a
    /// pathological console-spam case.</summary>
    private const int MaxQueuedCheats = 64;

    /// <summary>One request still waiting for its turn at the single shared slot, plus the
    /// follow-up it was filed with — see root cause 7's remarks above.</summary>
    private readonly record struct QueuedCheat(CheatRequest Request, Action? OnApplied, Action? OnRefused, bool Quiet);

    /// <summary>
    /// What to do once the pending cheat has been answered, and what to do instead if it was
    /// refused. Exactly one command needs it — <c>moviewarp</c>, whose two halves (stage the
    /// cinematic, then travel) MUST NOT be filed in the same tick, because the plugin's frame
    /// takes the travel branch and returns before it ever reaches the cheat channel. Modelling
    /// that as "wait for the answer" rather than "sleep a bit" is what makes a refused staging
    /// stop the warp instead of silently producing the movie-less transition that has already
    /// been tested. See <see cref="CheatFollowUp"/>.
    /// </summary>
    private readonly CheatFollowUp _afterCheat = new();

    /// <summary>Mirror of SharedBlock.ConnectStatusConnecting for the agent side.</summary>
    private const uint ConnectStatusConnecting = 1;

    /// <summary>Polls the block for console/menu commands and reflects status until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        if (!_browsing)
        {
            _browsing = true;
            _browser.StartListening();
        }

        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(150));

            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // One bad tick must not end the command surface. Without this, a single throw
                // anywhere below unwinds the loop and the ~ console, the menu button and the
                // browser all go quiet for the rest of the session — with nothing on screen to
                // say why, because the thing that reports problems is what died.
                try
                {
                    // Written unconditionally, every tick, whether or not a session even
                    // exists — this is "the agent is pumping at all", not "connected". The
                    // plugin watches it stall to notice a hung or crashed agent and freeze
                    // remote-peer writes rather than keep driving stale phantoms.
                    _kotor?.PublishHeartbeat();

                    manager.Poll();
                    DrainInputs();
                    PublishBrowserList();
                    ReflectPhase();
                    AnnouncePeerChanges();
                    AnnounceCompatibilityChanges();
                    AnnounceConnectionStatus();
                    AnnounceHostSaveAvailability();
                    AnnouncePublicAddress();

                    // After AnnouncePublicAddress, deliberately: the connect string a friend
                    // joins should carry the public endpoint STUN found, and this is the tick
                    // at which that is freshest.
                    MaintainSteamPresence();
                    PumpSteamJoinRequests();
                    MaintainSessionSave();
                    MaintainWorldServing();
                    DrainChat();
                    ReflectCheatAnswer();
                    DrainAutomaticLoot();
                    DrainAutomaticPartyPool();
                    DrainAutomaticStore();
                    DrainAutomaticPartyPoolV2();
                    DrainAutomaticStoreV2();
                    PumpPeerEffectLog();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.WriteLine($"[coop] command tick failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void DrainInputs()
    {
        if (_kotor is null)
        {
            return;
        }

        // The ~ console. A whole command line typed in-game.
        if (_kotor.TryTakeConsoleCommand(out string line))
        {
            DispatchFromGame(ConsoleCommand.Parse(line));
        }

        // The main-menu Connect button. It carries an optional typed host; empty means
        // "use the configured host" — a reconnect when a session existed, coop.ini's say
        // otherwise.
        if (_kotor.TryTakeConnectRequest(out string typedHost))
        {
            _kotor.SetConnectStatus(ConnectStatusConnecting);
            OnConnectRequest(typedHost);
        }

        // The in-game server browser: the HOST button, or a listed row confirmed. The row
        // is dialed as `connect #n` so it takes exactly the same path a typed pick would.
        if (_kotor.TryTakeBrowserHost())
        {
            DispatchFromGame(new CoopCommand(CoopCommandKind.Host));
        }

        if (_kotor.TryTakeBrowserConnect(out int picked))
        {
            if (picked >= 0 && picked < _lastServerList.Count)
            {
                DispatchFromGame(new CoopCommand(CoopCommandKind.Connect, Address: $"#{picked + 1}"));
            }
            else
            {
                Reply("that server is no longer listed");
            }
        }
    }

    /// <summary>
    /// Applies a parsed command to the session manager. Public so the initial CLI/menu
    /// connection can go through exactly the same path as an in-game command.
    ///
    /// The dispatch itself is a table lookup: <see cref="DispatchTable"/> has one entry per
    /// <see cref="CoopCommandKind"/>, built once per instance (a C# field initializer cannot
    /// call an instance method — CS0236 — so this uses the ordinary lazy-once pattern instead)
    /// by <see cref="BuildDispatchTable"/>. Each entry's handler lives in whichever
    /// <c>CoopCommandDispatcher.*.cs</c> file owns that verb's area.
    /// </summary>
    public void Dispatch(CoopCommand command)
    {
        if (DispatchTable.TryGetValue(command.Kind, out Action<CoopCommand>? handler))
        {
            handler(command);
        }
    }

    private Dictionary<CoopCommandKind, Action<CoopCommand>>? _dispatchTable;

    private Dictionary<CoopCommandKind, Action<CoopCommand>> DispatchTable => _dispatchTable ??= BuildDispatchTable();

    private Dictionary<CoopCommandKind, Action<CoopCommand>> BuildDispatchTable() => new()
    {
        // None/Unknown/Invalid: the original switch's `case None or Unknown` plus its own
        // Invalid case, kept as three explicit entries so the table has a row for every kind
        // Dispatch ever handled, sentinels included.
        [CoopCommandKind.None] = _ => { },
        [CoopCommandKind.Unknown] = _ => Reply("unknown command. " + ConsoleCommand.HelpText),
        [CoopCommandKind.Invalid] = c => Reply(c.Error),

        [CoopCommandKind.Connect] = c => Join(c.Address, c.Password),
        [CoopCommandKind.Host] = HostCommand,
        [CoopCommandKind.Password] = c => AnswerPasswordPrompt(c.Password ?? string.Empty),
        [CoopCommandKind.Disconnect] = _ => manager.Disconnect(),
        [CoopCommandKind.Reconnect] = ReconnectCommand,
        [CoopCommandKind.Say] = SayCommand,
        [CoopCommandKind.Clear] = _ => _kotor?.PostChatClear(),
        [CoopCommandKind.Servers] = _ => ListServers(),
        [CoopCommandKind.Status] = StatusCommand,
        [CoopCommandKind.Help] = _ => Reply(ConsoleCommand.HelpText),

        // Rides the existing agent->plugin travel channel (the same one peer-follow travel
        // uses): the plugin flushes everything it spawned, then the engine's own
        // move-to-module path runs — party collection, store-out, load screen. The parser
        // already validated the resref, so a null Address here would mean no plugin.
        [CoopCommandKind.Warp] = c => Warp(c.Address!, c.Password ?? string.Empty),

        [CoopCommandKind.StageMovie] = StageMovie,
        [CoopCommandKind.UseDoor] = c => UseDoor(c.Address ?? string.Empty),
        [CoopCommandKind.CrossDoor] = c => CrossDoor(c.Address ?? string.Empty),
        [CoopCommandKind.OpenDoor] = c => OpenDoor(c.Address ?? string.Empty),
        [CoopCommandKind.UsePlaceable] = c => UsePlaceable(c.Address ?? string.Empty),
        [CoopCommandKind.ClosePlaceable] = c => ClosePlaceable(c.Address ?? string.Empty),
        [CoopCommandKind.Who] = WhoCommand,
        [CoopCommandKind.NetSim] = c => ApplyNetSim(c.Address ?? "status", c.Password ?? string.Empty),
        [CoopCommandKind.Where] = WhereCommand,
        [CoopCommandKind.Census] = c => Census(c.Address),
        [CoopCommandKind.LagStats] = _ => LagStats(manager),
        [CoopCommandKind.PublicAddress] = _ => PublicAddressCommand(),
        [CoopCommandKind.SteamStatus] = SteamStatusCommand,
        [CoopCommandKind.SteamInvite] = SteamInviteCommand,

        // Straight to the plugin: these questions can only be answered on the game thread,
        // and the value of the answer is that it is taken in ONE frame.
        [CoopCommandKind.ResolveObject] = ResolveObjectCommand,
        [CoopCommandKind.MoveNear] = MoveNearCommand,
        [CoopCommandKind.FireScript] = FireScriptCommand,

        // spawncreature: every field the plugin needs is already in CoopCommand as-is
        // (template resref in Address, count in Amount, X/Y/Z the same origin-sentinel
        // convention MoveNear/Teleport/WalkTo already use) - no agent-side resolution needed,
        // same reasoning the GalaxyMap* rows below have.
        [CoopCommandKind.SpawnCreature] = c => SendCheat(new CheatRequest(
            CheatKinds.SpawnCreature, Text: c.Address ?? string.Empty, Amount: c.Amount, X: c.X, Y: c.Y, Z: c.Z)),

        // ---- staging cheats ---------------------------------------------
        [CoopCommandKind.God] = c => SendCheat(new CheatRequest(CheatKinds.God, Amount: c.Amount)),
        [CoopCommandKind.TopUp] = c => SendCheat(new CheatRequest(CheatKinds.TopUp, Amount: c.Amount)),
        [CoopCommandKind.StandUp] = c => SendCheat(new CheatRequest(CheatKinds.StandUp, Amount: c.Amount)),

        // c.X carries the party-roster slot (0-8), or negative for "yourself" — see
        // CoopCommandKind.Hurt/Down's own doc comments for why X, not Amount, holds it.
        [CoopCommandKind.Hurt] = c => SendCheat(new CheatRequest(
            CheatKinds.Hurt, Target: HurtDownTarget(c.X), Amount: c.Amount)),
        [CoopCommandKind.Down] = c => SendCheat(new CheatRequest(
            CheatKinds.Down, Target: HurtDownTarget(c.X))),

        [CoopCommandKind.Slay] = c => Slay(c.Address),
        [CoopCommandKind.Teleport] = c => SendCheat(new CheatRequest(CheatKinds.Teleport, X: c.X, Y: c.Y, Z: c.Z)),
        [CoopCommandKind.WalkTo] = c => SendCheat(new CheatRequest(CheatKinds.WalkTo, X: c.X, Y: c.Y, Z: c.Z)),
        [CoopCommandKind.GoTo] = c => GoTo(c.Address ?? string.Empty),
        [CoopCommandKind.DropItem] = c => DropItem(c.Address ?? string.Empty),
        [CoopCommandKind.Items] = c => ListItems(c.Amount),
        [CoopCommandKind.InspectItem] = c => InspectItem(c.Address ?? string.Empty),
        [CoopCommandKind.SetItem] = c => SetItem(c.Address ?? string.Empty, c.Item),
        [CoopCommandKind.GiveLoot] = c => GiveLoot(c.Address ?? string.Empty, c.Password ?? string.Empty, c.Amount),
        [CoopCommandKind.ApplyEffect] = ApplyEffect,
        [CoopCommandKind.Effects] = _ => ReportEffects(),
        [CoopCommandKind.MoviePlay] = c => MoviePlay(c.Address ?? string.Empty),
        [CoopCommandKind.DestroyObject] = c => DestroyObject(c.Address ?? string.Empty),
        [CoopCommandKind.Leader] = c => Leader(c.Amount),
        [CoopCommandKind.Recruit] = c => Recruit(c.Amount, c.Address ?? string.Empty),
        [CoopCommandKind.SkipLine] = _ => SendCheat(new CheatRequest(CheatKinds.SkipLine)),
        [CoopCommandKind.SkipKey] = _ => SendCheat(new CheatRequest(CheatKinds.SkipKey)),
        [CoopCommandKind.Save] = _ => SendCheat(new CheatRequest(CheatKinds.QuickSave)),
        [CoopCommandKind.SelectReply] = c => SendCheat(new CheatRequest(CheatKinds.SelectReply, Amount: c.Amount)),
        [CoopCommandKind.LoadGame] = c => TriggerMidSessionLoad(c.Amount),

        // The character-creation drive verbs. NOT SendCheat: the cheat channel refuses every
        // request with no player loaded and runs behind the plugin's world-epoch gate, and
        // every one of these is only meaningful at the main menu. See
        // KotorCoop.Plugin.CharGenDriveController.
        [CoopCommandKind.NewGame] = _ => DriveChargen(ChargenDriveKinds.NewGame, 0),
        [CoopCommandKind.ChargenQuick] = c => DriveChargen(ChargenDriveKinds.Quick, c.Amount),
        [CoopCommandKind.ChargenAccept] = _ => DriveChargen(ChargenDriveKinds.Accept, 0),

        [CoopCommandKind.GalaxyMapOpen] = _ => SendCheat(new CheatRequest(CheatKinds.GalaxyMapOpen)),
        [CoopCommandKind.GalaxyMapClose] = _ => SendCheat(new CheatRequest(CheatKinds.GalaxyMapClose)),
        [CoopCommandKind.GalaxyMapStats] = _ => SendCheat(new CheatRequest(CheatKinds.GalaxyMapStats)),
        [CoopCommandKind.GalaxyMapSelect] = c => SendCheat(new CheatRequest(CheatKinds.GalaxyMapSelect, Amount: c.Amount)),
        [CoopCommandKind.GalaxyMapUnlock] = c => SendCheat(new CheatRequest(CheatKinds.GalaxyMapUnlock, Amount: c.Amount)),
        [CoopCommandKind.GalaxyMapDepart] = c => SendCheat(new CheatRequest(CheatKinds.GalaxyMapDepart, Amount: c.Amount)),
        [CoopCommandKind.GalaxyAccept] = _ => SendCheat(new CheatRequest(CheatKinds.GalaxyAccept)),
        [CoopCommandKind.Gamepad] = c => SendCheat(new CheatRequest(CheatKinds.Gamepad, Text: c.Address ?? string.Empty)),
        [CoopCommandKind.QuitToMenu] = _ => SendCheat(new CheatRequest(CheatKinds.QuitToMenu)),

        // ⚠ Deliberately does NOT call NoteLocalItemGiven, unlike CoopCommandKind.GiveLoot's own
        // dispatcher entry just above — see CoopCommandKind.ScriptGive's own doc comment
        // (ConsoleCommand.cs) for why an UNTRACKED grant is the entire point of this verb.
        [CoopCommandKind.ScriptGive] = c => SendCheat(new CheatRequest(
            CheatKinds.ScriptGive, Text: c.Address ?? string.Empty, Amount: c.Amount)),

        [CoopCommandKind.LootTake] = c => LootTake(c.Address ?? string.Empty),

        // Same suppression as the automatic v2 drain (CoopCommandDispatcher.PartyPoolV2.cs):
        // a party-pool add lands in the PC's own item repository, so without this
        // ScriptItemGrantPump re-broadcasts a human's own `pooladd` as a script grant. See
        // CoopAgent.NoteLocalPartyPoolAdd.
        [CoopCommandKind.PoolAdd] = c => PoolAdd(c.Address ?? string.Empty, c.Amount),
        [CoopCommandKind.PoolRemove] = c => SendCheat(new CheatRequest(
            CheatKinds.PartyPoolRemove, Text: c.Address ?? string.Empty, Amount: c.Amount)),

        [CoopCommandKind.GrantXp] = GrantXp,
        [CoopCommandKind.Equip] = Equip,
        [CoopCommandKind.GrantGold] = c => GrantGold(c.Amount),

        [CoopCommandKind.SetGlobal] = SetGlobal,
        [CoopCommandKind.Pazaak] = Pazaak,
        [CoopCommandKind.OpenStore] = c => OpenStore(c.Address ?? string.Empty),
        [CoopCommandKind.CloseGui] = _ => SendCheat(new CheatRequest(CheatKinds.CloseGui)),
        [CoopCommandKind.PopModal] = c => SendCheat(new CheatRequest(CheatKinds.PopModal, Amount: c.Amount)),
        [CoopCommandKind.PartySelectOk] = _ => SendCheat(new CheatRequest(CheatKinds.PartySelectOk)),
        [CoopCommandKind.Faction] = FactionCommand,
        [CoopCommandKind.PazaakEnd] = _ => SendCheat(new CheatRequest(CheatKinds.PazaakEnd)),
        [CoopCommandKind.Buy] = c => Buy(c.Address ?? string.Empty, c.Password ?? string.Empty, c.Amount),
        [CoopCommandKind.Sell] = c => Sell(c.Address ?? string.Empty, c.Password ?? string.Empty, c.Amount),
        [CoopCommandKind.StartDialog] = c => StartDialog(c.Address ?? string.Empty, c.Password ?? string.Empty),
        [CoopCommandKind.RemoveParty] = c => SendCheat(new CheatRequest(CheatKinds.RemoveParty, Amount: c.Amount)),
        [CoopCommandKind.SetGold] = c => SendCheat(new CheatRequest(CheatKinds.SetGold, Amount: c.Amount)),
        [CoopCommandKind.FollowKick] = c => SendCheat(new CheatRequest(CheatKinds.FollowKick, Amount: c.Amount)),

        // Amount carries method*100 + slot — see CoopCommandKind.FollowStall for why this
        // dev-only verb packs two values into the one integer field CoopCommand has.
        [CoopCommandKind.FollowStall] = c => SendCheat(new CheatRequest(
            CheatKinds.FollowStall, Target: (uint)(c.Amount / 100), Amount: c.Amount % 100)),

        [CoopCommandKind.Cheats] = CheatsCommand,
    };

    /// <summary>
    /// Files a staging cheat with the plugin and remembers its serial so the answer can be
    /// reported. Refuses out loud when there is no plugin: "nothing happened" is the one
    /// outcome a staging command must never produce silently.
    ///
    /// The single shared slot can only ever hold one unanswered request — see root cause 7's
    /// remarks on <see cref="_queuedCheats"/> above. Every caller (a console verb, a follow-up
    /// like <c>moviewarp</c>'s travel half, or an automatic apply drain) goes through here, so
    /// this is the one place that decides "fire now" versus "queue behind whatever is still
    /// waiting on an answer" — never "overwrite it".
    /// </summary>
    /// <summary>Shared by <c>hurt</c>/<c>down</c>: the console's own "blank slot = yourself"
    /// convention (<see cref="CoopCommand.X"/> &lt; 0) becomes
    /// <see cref="CheatKinds.LocalPlayerTarget"/> in the request; a typed slot (already
    /// range-checked 0-8 by <c>ParseHurt</c>/<c>ParseDown</c>) crosses as-is.</summary>
    private static uint HurtDownTarget(float slot) => slot < 0 ? CheatKinds.LocalPlayerTarget : (uint)slot;

    private void SendCheat(CheatRequest request, Action? onApplied = null, Action? onRefused = null, bool quiet = false)
    {
        if (!bridge.SupportsCheats)
        {
            Reply("staging cheats need the in-game plugin (no shared block found)");
            onRefused?.Invoke();
            return;
        }

        if (_pendingCheatSerial != 0)
        {
            if (_queuedCheats.Count >= MaxQueuedCheats)
            {
                Reply("too many staging cheats already queued behind the game thread - dropping the oldest");
                QueuedCheat dropped = _queuedCheats.Dequeue();
                dropped.OnRefused?.Invoke();
            }

            _queuedCheats.Enqueue(new QueuedCheat(request, onApplied, onRefused, quiet));
            return;
        }

        FireCheat(request, onApplied, onRefused, quiet);
    }

    /// <summary>
    /// The one place that actually writes a request into the shared block — only ever called
    /// when the slot is confirmed free (<see cref="SendCheat"/> when idle, or
    /// <see cref="ReflectCheatAnswer"/> handing the slot to the next queued request the instant
    /// the current one resolves).
    /// </summary>
    private void FireCheat(CheatRequest request, Action? onApplied, Action? onRefused, bool quiet)
    {
        // Whatever the previous cheat was going to do next, it is not going to do it now — but
        // by construction (see SendCheat/ReflectCheatAnswer) that follow-up was already taken
        // and cleared when its own answer (or timeout) resolved the slot this is now filling, so
        // this is normally a no-op; kept for safety since an armed-and-not-yet-taken follow-up
        // must never leak into an unrelated request's answer.
        _afterCheat.Clear();

        if (bridge.TryRequestCheat(request, out uint serial))
        {
            _pendingCheatSerial = serial;
            _pendingCheatFiled = DateTime.UtcNow;
            _pendingCheatQuiet = quiet;
            _afterCheat.Arm(onApplied, onRefused);
        }
        else
        {
            Reply("could not reach the plugin to run that");
            onRefused?.Invoke();
        }
    }

    /// <summary>Hands the single shared slot to the next request that lost the race for it,
    /// if any — called the instant the current one resolves, from both branches of
    /// <see cref="ReflectCheatAnswer"/> (a matched answer and a timeout alike).</summary>
    private void FireNextQueuedCheat()
    {
        if (_queuedCheats.TryDequeue(out QueuedCheat next))
        {
            FireCheat(next.Request, next.OnApplied, next.OnRefused, next.Quiet);
        }
    }

    /// <summary>The plugin's answer to the cheat in flight: on screen for a request the player
    /// typed, log-only for one an automatic drain filed (<see cref="_pendingCheatQuiet"/>).
    /// The log line keeps the "cheat:" prefix the plugin's own log uses, so a materialised
    /// give is still traceable end to end from the agent log alone.</summary>
    private void Answer(string text)
    {
        if (_pendingCheatQuiet)
        {
            Log?.Invoke($"cheat (automatic): {text}");
            return;
        }

        Reply(text);
    }

    /// <summary>
    /// Prints the plugin's answer to the cheat we are waiting on, once. The detail sentence
    /// is what the plugin actually SAW — a refusal names the rule and the object id — so it
    /// is printed verbatim rather than being restated from the status code.
    /// </summary>
    private void ReflectCheatAnswer()
    {
        if (_pendingCheatSerial == 0)
        {
            return;
        }

        if (!bridge.TryReadCheatAnswer(out uint applied, out int status, out string detail)
            || applied != _pendingCheatSerial)
        {
            if (DateTime.UtcNow - _pendingCheatFiled > CheatAnswerTimeout)
            {
                _pendingCheatSerial = 0;
                Answer("the game never answered that - is a world loaded? (cheats run on the game's frame)");
                _afterCheat.Take(applied: false)?.Invoke();

                // The slot is free again - hand it to whatever lost the race for it, rather
                // than leaving a queued request waiting for a serial that will now never
                // resolve on its own (root cause 7, docs/research/party-pool-sync.md §26).
                FireNextQueuedCheat();
            }

            return;
        }

        _pendingCheatSerial = 0;

        string prefix = status switch
        {
            CheatStatus.Applied => string.Empty,
            CheatStatus.Forwarded => string.Empty,
            CheatStatus.NoWorld => "not in a world: ",
            CheatStatus.NoTarget => "no target: ",
            CheatStatus.Refused => "refused: ",
            CheatStatus.EngineRefused => "the engine refused it: ",
            _ => $"status {status}: ",
        };

        Answer(detail.Length > 0 ? prefix + detail : $"the plugin answered with status {status} and no detail");

        // The follow-up runs AFTER the plugin's own sentence is on screen, so a reader sees
        // what the game said before what we did about it.
        _afterCheat.Take(applied: status is CheatStatus.Applied or CheatStatus.Forwarded)?.Invoke();

        // Same reasoning as the timeout branch above: the slot just freed up, so give it to
        // the next request that was queued behind this one, if any.
        FireNextQueuedCheat();
    }

    /// <summary>
    /// The agent's log sink, wired to <c>AgentDashboard.Note</c> at startup — the same hook
    /// <c>SessionManager.Log</c> uses, and for the same reason.
    ///
    /// ⚠ WITHOUT THIS EVERY REPLY IS INVISIBLE TO AN UNATTENDED RUN. `demo.ps1` gives each
    /// agent a VISIBLE console (deliberately — the readout redraws in place, and a pipe turns
    /// every redraw into another appended copy) and a SEPARATE `--log` file written by the
    /// dashboard. A reply printed with Console.WriteLine therefore lands in a window nobody
    /// is watching and never reaches the file every check actually reads.
    ///
    /// Null in tests and in any host that has no dashboard; the console fallback below covers
    /// that case.
    /// </summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// All co-op feedback — command replies and chat alike — goes to the always-on overlay as
    /// well as the console reply line, because the console closes on Enter (so a reply that
    /// arrives a tick later would never be seen there) and the overlay persists regardless.
    ///
    /// ⚠ The in-game reply line is a 96-byte field and truncates without complaint, so a
    /// reply is a SUMMARY and the substance belongs in the log. Multi-line answers (`items`,
    /// `who`, `servers`) rely on that: only the last line survives in the block, and all of
    /// them survive in the log.
    /// </summary>
    private void Reply(string text)
    {
        // The log sink already writes to a redirected stdout, timestamped. Writing here too
        // would double every line in a piped run; writing nowhere would lose them in a
        // windowed one. So: the window when there is a window, the sink otherwise — and the
        // sink always, because the file is the record.
        if (Log is not { } log)
        {
            Console.WriteLine($"[coop] {text}");
        }
        else
        {
            if (!Console.IsOutputRedirected)
            {
                Console.WriteLine($"[coop] {text}");
            }

            log(text);
        }

        _kotor?.PostConsoleReply(text);
        _kotor?.PostChatLine(text);
    }

    /// <summary>Alias kept for call sites that specifically mean a chat line.</summary>
    private void Chat(string text) => Reply(text);
}
