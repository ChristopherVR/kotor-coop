using System.Net;
using KotorCoop.Memory;
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
/// </summary>
public sealed class CoopCommandDispatcher(
    SessionManager manager,
    IGameBridge bridge,
    int defaultPort,
    Func<string, SessionSettings> settingsForPassword,
    Func<CoopConfig?>? loadConfig = null,
    Action? menuAutoLoad = null)
{
    private readonly KotorGameBridge? _kotor = bridge as KotorGameBridge;

    /// <summary>Listens for co-op servers on the LAN so `servers` can list them and
    /// `connect #n` can dial one without an IP. Started on the first RunAsync tick.</summary>
    private readonly ServerDiscovery _browser = new();
    private bool _browsing;

    /// <summary>The servers listed by the last `servers` command, so `connect #n` resolves.</summary>
    private IReadOnlyList<ServerInfo> _lastServerList = [];

    /// <summary>The address of the last join attempt, so a password prompt can echo it back.</summary>
    private string? _pendingAddress;

    /// <summary>
    /// The endpoint of the last join attempt, so answering a password prompt can retry it
    /// without the player retyping an address. This is what makes <c>password &lt;pw&gt;</c> a
    /// complete answer: a player who picked a row in the browser never saw an IP, and asking
    /// them to produce one is the same as not prompting at all.
    /// </summary>
    private IPEndPoint? _pendingEndpoint;

    /// <summary>
    /// The password the current attempt is using. Tracked so a locked server can be recognised
    /// as unanswerable BEFORE dialling it — "the row says locked and we hold nothing" is worth a
    /// prompt straight away rather than a round trip that can only end in E_PASSWORD.
    /// </summary>
    private string _password = string.Empty;

    private SessionPhase _lastPhase = SessionPhase.Idle;
    private bool _promptedForPassword;

    /// <summary>
    /// The serial of a staging cheat still waiting on the plugin, or 0 for none.
    ///
    /// Held rather than fired-and-forgotten because a cheat's WHOLE VALUE is its answer: an
    /// unattended verification run types `slay` and needs to know whether the kill happened
    /// here, went to the host, or was refused because the thing it aimed at turned out to be
    /// a phantom. Reporting "sent" and moving on would make the channel untestable.
    /// </summary>
    private uint _pendingCheatSerial;

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
    /// What to do once the pending cheat has been answered, and what to do instead if it was
    /// refused. Exactly one command needs it — <c>moviewarp</c>, whose two halves (stage the
    /// cinematic, then travel) MUST NOT be filed in the same tick, because the plugin's frame
    /// takes the travel branch and returns before it ever reaches the cheat channel. Modelling
    /// that as "wait for the answer" rather than "sleep a bit" is what makes a refused staging
    /// stop the warp instead of silently producing the movie-less transition that has already
    /// been tested. See <see cref="CheatFollowUp"/>.
    /// </summary>
    private readonly CheatFollowUp _afterCheat = new();

    /// <summary>
    /// Armed by a connect/host that originated inside the game (Co-op button, browser
    /// panel, ~ console) — i.e. one made while the player could plausibly still be at the
    /// main menu. When such a session reaches its load condition the save auto-loads once,
    /// so "pick a server, arrive in the world" is one action.
    /// </summary>
    private bool _menuAutoLoadArmed;

    /// <summary>Mirror of SharedBlock.ConnectStatusConnecting for the agent side.</summary>
    private const uint ConnectStatusConnecting = 1;

    /// <summary>Roster changes become lines on BOTH screens: "Malak joined the game" /
    /// "Malak left the game" (see <see cref="PeerAnnouncer"/> for why this exists).</summary>
    private readonly PeerAnnouncer _peerAnnouncer = new();

    /// <summary>Content differences become lines on screen too — see
    /// <see cref="CompatibilityAnnouncer"/> for why a degradation that is only logged is
    /// indistinguishable from a bug.</summary>
    private readonly CompatibilityAnnouncer _compatibilityAnnouncer = new();

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
                    manager.Poll();
                    DrainInputs();
                    PublishBrowserList();
                    ReflectPhase();
                    AnnouncePeerChanges();
                    AnnounceCompatibilityChanges();
                    DrainChat();
                    ReflectCheatAnswer();
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
    /// Handles a Co-op button press; <paramref name="typedHost"/> is the host typed
    /// in-game, empty for a bare press. Public so the no-plugin <c>--menu</c> startup (and
    /// tests) can drive the same path the button does.
    /// </summary>
    public void OnConnectRequest(string typedHost)
    {
        if (typedHost.Length > 0)
        {
            DispatchFromGame(new CoopCommand(CoopCommandKind.Connect, Address: typedHost));
        }
        else if (manager.CanReconnect)
        {
            DispatchFromGame(new CoopCommand(CoopCommandKind.Reconnect));
        }
        else
        {
            DispatchConfiguredConnect();
        }
    }

    /// <summary>
    /// A bare connect with no session to reconnect to: coop.ini decides. mode=host hosts,
    /// host=&lt;address&gt; joins it, and with neither the player is pointed at the server
    /// browser instead of getting "nothing to reconnect to".
    /// </summary>
    private void DispatchConfiguredConnect()
    {
        CoopConfig? config = loadConfig?.Invoke();

        if (config is { Host: true })
        {
            DispatchFromGame(new CoopCommand(
                CoopCommandKind.Host,
                Password: config.Password.Length > 0 ? config.Password : null));
            return;
        }

        if (config is { HasHostAddress: true })
        {
            DispatchFromGame(new CoopCommand(
                CoopCommandKind.Connect,
                Address: $"{config.HostAddress}:{config.Port}",
                Password: config.Password.Length > 0 ? config.Password : null));
            return;
        }

        Reply("no co-op server configured - pick one in the server browser, or set host=<ip> in coop.ini");
    }

    /// <summary>
    /// Dispatches a command that originated inside the game rather than from the command
    /// line. A connect or host chosen this way arms the one-shot menu auto-load; a host
    /// has nothing to wait for, so its load fires immediately, while a joiner's fires when
    /// the session reaches Connected (see <see cref="ReflectPhase"/>).
    /// </summary>
    private void DispatchFromGame(CoopCommand command)
    {
        if (command.Kind is CoopCommandKind.Connect or CoopCommandKind.Host)
        {
            _menuAutoLoadArmed = true;
        }

        Dispatch(command);

        if (command.Kind == CoopCommandKind.Host)
        {
            FireMenuAutoLoadIfArmed();
        }
    }

    /// <summary>
    /// Fires the menu auto-load at most once per menu-initiated connect, and only when the
    /// game is verifiably still at the main menu (no readable player state) — never
    /// mid-world, never again on a reconnect.
    /// </summary>
    private void FireMenuAutoLoadIfArmed()
    {
        if (!_menuAutoLoadArmed)
        {
            return;
        }

        _menuAutoLoadArmed = false;

        if (menuAutoLoad is not null && !bridge.TryReadLocalState(out _))
        {
            menuAutoLoad();
        }
    }

    /// <summary>
    /// Applies a parsed command to the session manager. Public so the initial CLI/menu
    /// connection can go through exactly the same path as an in-game command.
    /// </summary>
    public void Dispatch(CoopCommand command)
    {
        switch (command.Kind)
        {
            case CoopCommandKind.None or CoopCommandKind.Unknown:
                if (command.Kind == CoopCommandKind.Unknown)
                {
                    Reply("unknown command. " + ConsoleCommand.HelpText);
                }

                break;

            case CoopCommandKind.Invalid:
                Reply(command.Error);
                break;

            case CoopCommandKind.Connect:
                Join(command.Address, command.Password);
                break;

            case CoopCommandKind.Host:
                _pendingAddress = null;
                _pendingEndpoint = null;
                _promptedForPassword = false;
                _password = command.Password ?? string.Empty;
                manager.Connect(host: true, endpoint: null, settingsForPassword(_password));
                break;

            case CoopCommandKind.Password:
                AnswerPasswordPrompt(command.Password ?? string.Empty);
                break;

            case CoopCommandKind.Disconnect:
                manager.Disconnect();
                break;

            case CoopCommandKind.Reconnect:
                _promptedForPassword = false;
                manager.Reconnect();
                break;

            case CoopCommandKind.Say:
                if (manager.Current is { } session && command.Address is { Length: > 0 } message)
                {
                    session.SendChat(message);
                    Chat($"you: {message}"); // echo locally; peers get it over the wire
                }
                else
                {
                    Reply("not connected - nothing to say to");
                }

                break;

            case CoopCommandKind.Clear:
                _kotor?.PostChatClear();
                break;

            case CoopCommandKind.Servers:
                ListServers();
                break;

            case CoopCommandKind.Status:
                Reply($"{manager.Phase.ToString().ToLowerInvariant()} - {manager.Detail}");
                break;

            case CoopCommandKind.Help:
                Reply(ConsoleCommand.HelpText);
                break;

            case CoopCommandKind.Warp:
                // Rides the existing agent->plugin travel channel (the same one peer-follow
                // travel uses): the plugin flushes everything it spawned, then the engine's
                // own move-to-module path runs — party collection, store-out, load screen.
                // The parser already validated the resref, so a false here means no plugin.
                Warp(command.Address!, command.Password ?? string.Empty);
                break;

            case CoopCommandKind.StageMovie:
                StageMovie(command);
                break;

            case CoopCommandKind.UseDoor:
                UseDoor(command.Address ?? string.Empty);
                break;

            case CoopCommandKind.Who:
                if (manager.Current is not { } who)
                {
                    Reply("not connected - no players to list");
                    break;
                }

                ListPlayers(who);
                break;

            case CoopCommandKind.NetSim:
                ApplyNetSim(command.Address ?? "status", command.Password ?? string.Empty);
                break;

            case CoopCommandKind.Where:
                if (bridge.TryReadLocalState(out PlayerState local) && local.Module.Length > 0)
                {
                    Reply($"module {local.Module}  at {local.Position.X:F1} {local.Position.Y:F1} {local.Position.Z:F1}"
                        + $"  hp {local.Health}/{local.MaxHealth}");
                }
                else
                {
                    Reply("no player loaded (not in a module yet)");
                }

                break;

            case CoopCommandKind.Census:
                Census(command.Address);
                break;

            case CoopCommandKind.ResolveObject:
                // Straight to the plugin: this question can only be answered on the game
                // thread, and the value of the answer is that it is taken in ONE frame.
                if (ConsoleCommand.TryParseObjectId(command.Address ?? string.Empty, out uint resolveId))
                {
                    SendCheat(new CheatRequest(CheatKinds.Resolve, Target: resolveId));
                }
                else
                {
                    Reply($"resolve: '{command.Address}' is not an object id (try 0x35)");
                }

                break;

            case CoopCommandKind.MoveNear:
                if (ConsoleCommand.TryParseObjectId(command.Address ?? string.Empty, out uint moveId))
                {
                    SendCheat(new CheatRequest(CheatKinds.MoveNear, Target: moveId));
                }
                else
                {
                    Reply($"movenear: '{command.Address}' is not an object id (try 0x127)");
                }

                break;

            case CoopCommandKind.FireScript:
                if (ConsoleCommand.TryParseObjectId(command.Address ?? string.Empty, out uint scriptId))
                {
                    SendCheat(new CheatRequest(CheatKinds.FireScript, Target: scriptId, Amount: command.Amount));
                }
                else
                {
                    Reply($"firescript: '{command.Address}' is not an object id (try 0x35)");
                }

                break;

            // ---- staging cheats ---------------------------------------------

            case CoopCommandKind.God:
                SendCheat(new CheatRequest(CheatKinds.God, Amount: command.Amount));
                break;

            case CoopCommandKind.TopUp:
                SendCheat(new CheatRequest(CheatKinds.TopUp, Amount: command.Amount));
                break;

            case CoopCommandKind.StandUp:
                SendCheat(new CheatRequest(CheatKinds.StandUp, Amount: command.Amount));
                break;

            case CoopCommandKind.Slay:
                Slay(command.Address);
                break;

            case CoopCommandKind.Teleport:
                SendCheat(new CheatRequest(CheatKinds.Teleport, X: command.X, Y: command.Y, Z: command.Z));
                break;

            case CoopCommandKind.GoTo:
                GoTo(command.Address ?? string.Empty);
                break;

            case CoopCommandKind.DropItem:
                DropItem(command.Address ?? string.Empty);
                break;

            case CoopCommandKind.Items:
                ListItems(command.Amount);
                break;

            case CoopCommandKind.InspectItem:
                InspectItem(command.Address ?? string.Empty);
                break;

            case CoopCommandKind.SetItem:
                SetItem(command.Address ?? string.Empty, command.Item);
                break;

            case CoopCommandKind.GiveLoot:
                GiveLoot(command.Address ?? string.Empty, command.Password ?? string.Empty, command.Amount);
                break;

            case CoopCommandKind.Cheats:
                foreach (string line in ConsoleCommand.CheatsText)
                {
                    Reply(line);
                }

                break;
        }
    }

    // ---- the item-identity harness ----------------------------------------------------
    //
    // Four verbs that together make the ground channel's apply path exercisable with nobody
    // at the controls: `items` finds a target, `setitem` makes it worth sending, `dropitem`
    // sends it, `item` reads back what arrived on the far side. Before these the apply path
    // could not be reached at all — the only drop the harness could make named a BLUEPRINT,
    // which by construction carries no identity, so the receiving half had never once run
    // (docs/research/item-identity.md §8, docs/live-verification.md 2026-08-15).

    /// <summary>How many item rows `items` prints when the line names no cap. Enough to show
    /// a party's pack and the floor, few enough not to bury the overlay.</summary>
    private const int DefaultItemListing = 12;

    /// <summary>
    /// Drops a blueprint, or a copy of a LIVE item named by object id, as a shared ground
    /// item — through the SESSION, so every peer materialises it.
    ///
    /// ⚠ This used to file a <c>DropItem</c> staging cheat instead, which spawns a body in
    /// the typist's own game and nothing else. Nothing scans the floor for items to publish,
    /// so that drop reached no peer at all — while both the help text and
    /// docs/research/console-cheats.md §4 said "the item channel shares it". The session's
    /// own drop is what shares it, and the local body then arrives the same way a peer's
    /// does: spawned by the plugin from the item slot, identity and all.
    /// </summary>
    private void DropItem(string request)
    {
        ItemDropRequest.Outcome outcome = ItemDropRequest.Drop(manager, bridge, request);

        // ⚠ THE OFFLINE CASE STILL HAS TO WORK. Routing this verb through the session was the
        // fix for a drop that reached no peer — but a single-player game has no session, and
        // "not connected" would take away the only way to put an item on the floor before
        // anyone joins. That matters here specifically: staging a target for `setitem` is
        // something you want to do BEFORE the pair is up. So a sessionless drop falls back to
        // the staging cheat, which spawns it locally, and the answer says which of the two
        // happened rather than letting the caller assume.
        if (outcome.NoSession && !request.StartsWith('#')
            && !request.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            Reply("not connected - spawning it locally only, where no peer will see it");
            SendCheat(new CheatRequest(CheatKinds.DropItem, Text: request));
            return;
        }

        Reply(outcome.Note);
    }

    /// <summary>
    /// Lists the live items this instance can see, most interesting first.
    ///
    /// "Interesting" means differing from a stock blueprint in a way the identity channel
    /// carries — a stack, charges, an upgrade mask — because those are the only items whose
    /// replication proves anything. A listing of stock pistols would be a listing of items
    /// for which the apply path is indistinguishable from doing nothing.
    /// </summary>
    private void ListItems(int cap)
    {
        if (_kotor is null)
        {
            Reply("items needs --kotor (there is no game to read)");
            return;
        }

        IReadOnlyList<KotorGameBridge.ItemListing> items = _kotor.ListItems();

        // The sweep's own account of itself comes FIRST, because a short list has two very
        // different causes — few items, or a sweep that stopped early — and they are
        // indistinguishable from the rows.
        Reply($"items: {_kotor.LastItemScanNote}");

        if (items.Count == 0)
        {
            return;
        }

        uint localPlayer = _kotor.Player?.ObjectId ?? 0;
        int limit = cap > 0 ? Math.Min(cap, items.Count) : Math.Min(DefaultItemListing, items.Count);

        for (int i = 0; i < limit; i++)
        {
            KotorGameBridge.ItemListing item = items[i];

            string where = item.OnFloor
                ? "on the floor"
                : item.Possessor == localPlayer
                    ? "yours"
                    : $"held by 0x{item.Possessor:X}";

            // A body with no name at all is almost always one CreateItemOnFloor built from a
            // blueprint that is not in this install — it does not check the template load, so
            // a typo produces a real but empty item. Say so here: it is the difference between
            // "pick this one" and "this one is the wreckage of a mistyped drop".
            string name = item.Resref.Length > 0
                ? item.Resref
                : "<NO NAME - probably a bad blueprint>";

            Reply(
                $"  0x{item.ObjectId:X}  {name}  "
                + $"{where}  stack {item.Identity.StackSize}  "
                + $"charges {item.Identity.Charges}/{item.Identity.MaxCharges}  "
                + $"upgrades 0x{item.Identity.Upgrades:X}"
                + (item.IsInteresting ? "  <- not stock" : string.Empty));
        }

        // ⚠ A CLOSING SUMMARY, and it is not redundant with the header. Only the LAST reply
        // survives in the block's single reply field, which is what
        // `blockpoke.ps1 console 'items'` reads; the rows and the header reach the agent log
        // and the chat overlay, but a scripted caller would otherwise see nothing but the
        // last item row and have no idea whether the list was complete.
        int interesting = 0;
        foreach (KotorGameBridge.ItemListing item in items)
        {
            if (item.IsInteresting)
            {
                interesting++;
            }
        }

        Reply(
            $"items: showed {limit} of {items.Count}, {interesting} not stock"
            + (limit < items.Count ? $" (`items {items.Count}` for all of them)" : string.Empty)
            + " - full list in the agent log");
    }

    /// <summary>
    /// Prints one item's per-instance state, read straight out of the game's memory.
    ///
    /// This is the READ-BACK half of an identity test and the reason it is worth having as
    /// its own verb: run it on the RECEIVING instance, against the object id that instance's
    /// plugin log named, and it answers with what the item actually holds — independently of
    /// the plugin that wrote it, from a different process. The plugin's own read-back proves
    /// the write reached the address; this proves the address holds the item.
    /// </summary>
    private void InspectItem(string token)
    {
        if (_kotor is null)
        {
            Reply("item needs --kotor (there is no game to read)");
            return;
        }

        if (!ConsoleCommand.TryParseItemId(token, out uint objectId))
        {
            Reply($"item: '{token}' is not an object id (write it as 0x131)");
            return;
        }

        if (!_kotor.TryReadItemIdentity(objectId, out ItemIdentity identity, out string why, force: true))
        {
            Reply($"item 0x{objectId:X}: {why}");
            return;
        }

        _kotor.TryGetItemResref(objectId, out string resref, force: true);

        // ⚠ Kept inside 96 characters on purpose — that is the in-game reply field, and it
        // truncates silently. The log gets the same line, so what matters is that the fields
        // an identity check compares survive the cut.
        Reply(
            $"item 0x{objectId:X} {(resref.Length > 0 ? resref : "<NO NAME>")}: "
            + $"stack {identity.StackSize}, chg {identity.Charges}/{identity.MaxCharges}, "
            + $"upg 0x{identity.Upgrades:X}, "
            + $"{(identity.Identified ? "identified" : "UNidentified")}"
            + (identity.Stolen ? ", stolen" : string.Empty)
            + (identity.NonEquippable ? ", non-equippable" : string.Empty));

        if (resref.Length == 0)
        {
            Reply(
                $"item 0x{objectId:X} has no tag and no spawn record - CreateItemOnFloor "
                + "builds a real but EMPTY body for a blueprint that is not installed, so "
                + "this is most likely the wreckage of a mistyped resref");
        }
    }

    /// <summary>
    /// Pokes one live item's per-instance state through the plugin, so the sender has an item
    /// worth sending.
    ///
    /// The write itself happens on the game's thread, in
    /// <c>Odyssey.ApplyItemIdentity</c> — the very function that runs on a peer's arriving
    /// item. That is the point: staging the sender through a DIFFERENT writer would test a
    /// writer nothing else uses.
    ///
    /// The identified bit needs the item's current flags to be sent whole (the block carries
    /// a value, not a set/clear instruction), so it is read here first and the refusal says
    /// so rather than quietly leaving the bit alone.
    /// </summary>
    private void SetItem(string token, ItemFields fields)
    {
        if (!ConsoleCommand.TryParseItemId(token, out uint objectId))
        {
            Reply($"setitem: '{token}' is not an object id (write it as 0x131)");
            return;
        }

        uint flags = CheatKinds.Unchanged;

        if (fields.Identified is bool wanted)
        {
            // The block carries a flags VALUE, not a set/clear instruction, so flipping one
            // bit means knowing the other two. Only this branch needs to read the game, which
            // is why the requirement is stated here rather than at the top: every other
            // setitem is a plain write the plugin performs on its own.
            if (_kotor is null)
            {
                Reply("setitem identified= needs --kotor (it has to read the item's other flags first)");
                return;
            }

            if (!_kotor.TryReadItemIdentity(objectId, out ItemIdentity current, out string why, force: true))
            {
                Reply($"setitem 0x{objectId:X}: identified= needs the item's current flags, and {why}");
                return;
            }

            flags = wanted
                ? (uint)(current.Flags | ItemIdentity.FlagIdentified)
                : (uint)(current.Flags & ~ItemIdentity.FlagIdentified);
        }

        SendCheat(new CheatRequest(
            CheatKinds.SetItem,
            Target: objectId,
            Amount: (int)(fields.StackSize ?? 0),
            Charges: fields.Charges ?? CheatKinds.Unchanged,
            MaxCharges: fields.MaxCharges ?? CheatKinds.Unchanged,
            Upgrades: fields.Upgrades ?? CheatKinds.Unchanged,
            ItemFlags: flags));
    }

    /// <summary>
    /// Gives a materialised enemy's local stand-in one item of its real corpse's actual loot —
    /// the staging instrument for docs/research/enemy-materialisation.md §5/§6's still-open
    /// loot-sync gap, the same role `setitem`/`dropitem` play for item identity.
    /// </summary>
    private void GiveLoot(string ownerToken, string resref, int stack)
    {
        if (!ConsoleCommand.TryParseItemId(ownerToken, out uint ownerId))
        {
            Reply($"giveloot: '{ownerToken}' is not an object id (write it as 0x35)");
            return;
        }

        SendCheat(new CheatRequest(
            CheatKinds.GiveLoot,
            Target: ownerId,
            Text: resref,
            Amount: stack));
    }

    /// <summary>
    /// Asks the plugin to move this instance to another module. Extracted so the staged
    /// cinematic can reuse the EXACT path a typed <c>warp</c> takes — a staging verb that
    /// travels a different way would be measuring its own plumbing.
    /// </summary>
    private void Warp(string module, string waypoint)
    {
        if (_kotor is null || !_kotor.SupportsTravel)
        {
            Reply("warp needs the in-game plugin (no shared block found)");
        }
        else if (_kotor.TryRequestTravel(module, waypoint))
        {
            Reply($"warping to {module}...");
        }
        else
        {
            Reply($"could not request warp to {module}");
        }
    }

    /// <summary>
    /// Stages a cinematic onto a module transition (docs/research/cinematics.md §7) — a test
    /// instrument, not a feature, and the only command here that deliberately arranges for the
    /// in-game plugin to stop running for several seconds.
    ///
    /// The warp form is TWO STEPS ON PURPOSE, and the order is the whole point. The plugin
    /// arms the movie names first and answers; only when that answer arrives does the travel
    /// request go out. Filing both at once would race: the plugin's frame does the travel
    /// branch and returns BEFORE it reaches the cheat channel, so a travel filed in the same
    /// tick as its cinematic would warp without one and the movie would sit armed, waiting to
    /// ambush whatever transition came next.
    ///
    /// A refusal therefore stops the whole thing rather than warping anyway. "The movie was
    /// refused but we travelled" is the outcome that would waste a live run: a movie-less
    /// transition already passed this test on 2026-08-15 and proves nothing about the case
    /// under investigation.
    /// </summary>
    private void StageMovie(CoopCommand command)
    {
        string movies = command.Password ?? string.Empty;
        string module = command.Address ?? string.Empty;

        if (command.Amount != StageMovieModes.WarpWithMovies)
        {
            SendCheat(new CheatRequest(CheatKinds.StageMovie, Amount: command.Amount, Text: movies));
            return;
        }

        SendCheat(
            new CheatRequest(CheatKinds.StageMovie, Amount: command.Amount, Text: movies),
            onApplied: () =>
            {
                Reply($"staged {movies}; now warping to {module} - the movie plays on the way in");
                Warp(module, string.Empty);
            },
            onRefused: () => Reply($"the cinematic was not staged, so the warp to {module} was NOT attempted"));
    }

    /// <summary>
    /// Asks the plugin to make the local player use a door — the ENGINE-initiated half of the
    /// cinematic test (docs/research/cinematics.md §8).
    ///
    /// One request, no follow-up: unlike <c>moviewarp</c> there is nothing to sequence, because
    /// the movie was queued by a SEPARATE earlier command and is already sitting in the
    /// engine's own transition queue. That separation is the point — the plugin must not be
    /// the thing that starts this transition, or the flush window under test never opens.
    ///
    /// The token is passed through as text and resolved inside the game, deliberately: a door
    /// tag means nothing to the agent, and an object id resolved out here would be resolved
    /// against a census that is a frame or two old.
    /// </summary>
    private void UseDoor(string token)
    {
        if (ConsoleCommand.TryParseObjectId(token, out uint objectId) && objectId != 0)
        {
            SendCheat(new CheatRequest(CheatKinds.UseDoor, Target: objectId));
            return;
        }

        SendCheat(new CheatRequest(CheatKinds.UseDoor, Text: token));
    }

    /// <summary>
    /// Arms (or clears) the transport fault injector on the live session, so a disconnect can
    /// be produced on the rig without unplugging anything and without killing a process — a
    /// killed process is a clean socket close, which is the one failure shape that was already
    /// known to recover. See <see cref="NetworkSim"/>.
    ///
    /// Deliberately applied to the LIVE session only: a fault outlives no session, so
    /// disconnecting is always a way out of one you set by mistake.
    /// </summary>
    private void ApplyNetSim(string sub, string args)
    {
        if (manager.Current?.Network is not { } sim)
        {
            Reply("netsim: not connected - nothing to break");
            return;
        }

        string[] parts = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        switch (sub)
        {
            case "off":
                sim.Clear();
                break;

            case "loss":
                sim.SetLoss(double.Parse(parts[0]));
                break;

            case "latency":
                sim.SetLatency(int.Parse(parts[0]), parts.Length > 1 ? int.Parse(parts[1]) : 0);
                break;

            case "blackout":
                NetSimDirection direction = parts.Length > 1
                    ? parts[1].ToLowerInvariant() switch
                    {
                        "in" => NetSimDirection.Inbound,
                        "out" => NetSimDirection.Outbound,
                        _ => NetSimDirection.Both,
                    }
                    : NetSimDirection.Both;

                sim.StartBlackout((int)(double.Parse(parts[0]) * 1000), direction);
                break;
        }

        Reply(sim.Describe());
    }

    /// <summary>
    /// Files a staging cheat with the plugin and remembers its serial so the answer can be
    /// reported. Refuses out loud when there is no plugin: "nothing happened" is the one
    /// outcome a staging command must never produce silently.
    /// </summary>
    private void SendCheat(CheatRequest request, Action? onApplied = null, Action? onRefused = null)
    {
        // Whatever the previous cheat was going to do next, it is not going to do it now:
        // only one request is in flight, so filing a new one abandons the old one's answer.
        _afterCheat.Clear();

        if (!bridge.SupportsCheats)
        {
            Reply("staging cheats need the in-game plugin (no shared block found)");
            onRefused?.Invoke();
            return;
        }

        if (bridge.TryRequestCheat(request, out uint serial))
        {
            _pendingCheatSerial = serial;
            _pendingCheatFiled = DateTime.UtcNow;
            _afterCheat.Arm(onApplied, onRefused);
        }
        else
        {
            Reply("could not reach the plugin to run that");
            onRefused?.Invoke();
        }
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
                Reply("the game never answered that - is a world loaded? (cheats run on the game's frame)");
                _afterCheat.Take(applied: false)?.Invoke();
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

        Reply(detail.Length > 0 ? prefix + detail : $"the plugin answered with status {status} and no detail");

        // The follow-up runs AFTER the plugin's own sentence is on screen, so a reader sees
        // what the game said before what we did about it.
        _afterCheat.Take(applied: status is CheatStatus.Applied or CheatStatus.Forwarded)?.Invoke();
    }

    /// <summary>
    /// Resolves what <c>slay</c> should aim at and hands it over. A bare <c>slay</c> means
    /// "the nearest hostile", which only the agent can answer — it is the side that runs the
    /// enemy census — so the nearest id is picked here and the plugin is given a number
    /// rather than a policy.
    ///
    /// The authority decision itself is deliberately NOT made here: whether this instance
    /// kills the creature or forwards the kill to its owner depends on state only the plugin
    /// can see (the phantom table, the party roster, the mirror's own pin), and splitting
    /// that rule across two processes is how it would drift.
    /// </summary>
    private void Slay(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            if (!TryFindNearestEnemy(out uint nearest, out float distance))
            {
                // Names the census, because "none in range" and "the owner has not published
                // any here" look identical from the console and mean different things.
                Reply(
                    manager.Current is { IsEnemyAuthority: false }
                        ? "no living enemy in the owner's census here - name one: slay <tag|id>"
                        : "no living enemy in range - name one: slay <tag|id>");
                return;
            }

            // Names the census it came from: an id is only meaningful in one id space, and
            // when a forwarded kill goes missing the FIRST question is which list chose it.
            Reply(
                $"slaying the nearest enemy 0x{nearest:X} ({distance:F1}m away) "
                + $"[from {_lastCensusSource}]");
            SendCheat(new CheatRequest(CheatKinds.Slay, Target: nearest));
            return;
        }

        if (ConsoleCommand.TryParseObjectId(token, out uint objectId))
        {
            SendCheat(new CheatRequest(CheatKinds.Slay, Target: objectId));
            return;
        }

        // A tag. Refuse one that names a PLAYER before it reaches the engine: the plugin
        // rejects a phantom by id anyway, but a peer's name is what a person actually types
        // and the reason is clearer said here.
        if (manager.Current is { } session)
        {
            foreach (RemotePeer peer in session.GetRemotePeers())
            {
                if (peer.Name.Equals(token, StringComparison.OrdinalIgnoreCase))
                {
                    Reply($"{peer.Name} is a player - their character is theirs, not yours to kill");
                    return;
                }
            }
        }

        SendCheat(new CheatRequest(CheatKinds.Slay, Text: token));
    }

    /// <summary>
    /// The nearest living enemy to the local player.
    ///
    /// ⚠ Which census answers this depends on who owns the fight, and getting it wrong is
    /// silent. On a MIRRORING instance the local engine's creature list is in OUR id space,
    /// and those ids mean nothing to the owner — the kill is forwarded as a debt keyed by
    /// object id, so an id the owner cannot resolve is a debt it holds for ever. Measured
    /// live on 2026-08-15: the client's list held 45 creatures to the host's 38, the sweep
    /// picked an `end_weld` that exists only here, and the host logged nothing at all
    /// because it had never heard of that id.
    ///
    /// So a mirroring instance sweeps the OWNER's published set instead: those ids are the
    /// only ones a forwarded kill can be keyed by, and the positions travel with them.
    /// </summary>
    /// <summary>Which list the last nearest-enemy sweep read, for the reply line. An id
    /// is only meaningful in one id space, so the source belongs next to the id.</summary>
    private string _lastCensusSource = "none";

    private bool TryFindNearestEnemy(out uint objectId, out float distance)
    {
        objectId = 0;
        distance = 0f;

        if (!bridge.TryReadLocalState(out PlayerState local))
        {
            return false;
        }

        IReadOnlyList<EnemyState> census;

        if (manager.Current is { IsEnemyAuthority: false } mirror)
        {
            census = mirror.GetEnemies(local.Module);
            _lastCensusSource = $"owner census, {census.Count} in '{local.Module}'";
        }
        else
        {
            census = bridge.ReadEnemies([]);
            _lastCensusSource = $"local engine, {census.Count}";
        }

        float best = float.MaxValue;
        foreach (EnemyState enemy in census)
        {
            if (enemy.Dead || enemy.Plot || enemy.CurrentHealth == 0)
            {
                continue;
            }

            float dx = enemy.Position.X - local.Position.X;
            float dy = enemy.Position.Y - local.Position.Y;
            float dz = enemy.Position.Z - local.Position.Z;
            float d = MathF.Sqrt((dx * dx) + (dy * dy) + (dz * dz));

            if (d < best)
            {
                best = d;
                objectId = enemy.ObjectId;
            }
        }

        distance = best;
        return objectId != 0;
    }

    /// <summary>
    /// Dumps the enemy census beside the engine's own verdict on every id in it.
    ///
    /// ⚠ THIS EXISTS BECAUSE THE ARGUMENT COULD NOT BE SETTLED FROM OUTSIDE. The failure it
    /// serves is "an id in the census names nothing its own publisher can resolve", and the
    /// evidence for it came from three places that disagreed — a peer's console reply, the
    /// other instance's plugin log, and a `creatures.ps1` scan — with no way to ask either
    /// side what it thought it was publishing. Every branch of that question is answered here,
    /// on demand, from the side that knows:
    ///
    /// <list type="bullet">
    /// <item>the liveness gate's state, including the two cases in which it checks NOTHING;</item>
    /// <item>how many published ids this engine's own object array holds, and which do not;</item>
    /// <item>for a disputed id, whether it is in the raw memory scan, in the object array, and
    /// in the census — "in the scan but not the array" is a ghost the gate should have caught,
    /// while "in neither" means the id never came from this instance at all.</item>
    /// </list>
    /// </summary>
    private void Census(string? token)
    {
        if (_kotor is null)
        {
            Reply("census: only the KOTOR bridge can answer that");
            return;
        }

        bool mirror = manager.Current is { IsEnemyAuthority: false };
        string module = bridge.TryReadLocalState(out PlayerState here) ? here.Module : string.Empty;

        // Always first, and always printed: this is the line whose ABSENCE from a log was
        // ambiguous between "no ghosts", "never ran" and "never wired".
        Reply($"census: {_kotor.LastCensusGate.Describe()}");

        // The owner is asked what it WOULD publish right now (the same call the pump makes,
        // gate and all); a mirror is asked what it was sent.
        IReadOnlyList<EnemyState> census = mirror
            ? manager.Current?.GetEnemies(module) ?? []
            : bridge.ReadEnemies([]);

        string source = mirror ? "the owner's census" : "my own census";

        List<uint> unresolved = [];
        foreach (EnemyState enemy in census)
        {
            if (!_kotor.TryResolveObjectHere(enemy.ObjectId, out _))
            {
                unresolved.Add(enemy.ObjectId);
            }
        }

        // ⚠ Short lines, deliberately. The console answer channel is 96 bytes and truncates
        // without a mark; the previous round's answers were all cut off mid-sentence.
        Reply(
            $"census: {(mirror ? "MIRROR" : "OWNER")} {census.Count} id(s) '{module}': "
            + $"{census.Count - unresolved.Count} resolve here, {unresolved.Count} do not");

        if (unresolved.Count > 0)
        {
            Reply(mirror
                ? "census: a mirror need not resolve them all (stand-ins carry local ids)"
                : "census: as the OWNER, every id that does not resolve here is the defect");
        }

        if (!mirror && _kotor.CensusPublishRejected.Count > 0)
        {
            Reply(
                "census: publish refused "
                + string.Join(" ", _kotor.CensusPublishRejected.Take(4).Select(id => $"0x{id:X}")));
        }

        foreach (uint id in unresolved.Take(CensusDetailLines))
        {
            DescribeId(id, census);
        }

        if (unresolved.Count > CensusDetailLines)
        {
            Reply($"census: ...and {unresolved.Count - CensusDetailLines} more");
        }

        if (!string.IsNullOrWhiteSpace(token) && ConsoleCommand.TryParseObjectId(token, out uint asked))
        {
            DescribeId(asked, census);
        }
    }

    /// <summary>How many per-id lines a dump prints before summarising. A console overlay is
    /// a few lines tall; a log can take more, but an unbounded dump helps nobody.</summary>
    private const int CensusDetailLines = 8;

    /// <summary>
    /// One id, checked against all three lists at once: the engine's object array (the
    /// authority), the raw memory scan (what a vtable sweep sees, ghosts included), and the
    /// census itself. Their DISAGREEMENT is the finding, so they are reported together — but
    /// across TWO short lines, because one line of all three does not survive the 96-byte
    /// console answer channel.
    /// </summary>
    private void DescribeId(uint objectId, IReadOnlyList<EnemyState> census)
    {
        string array = _kotor!.TryResolveObjectHere(objectId, out nint address)
            ? $"array 0x{address:X}"
            : "array NOT HELD";

        CreatureRef? scanned = null;
        foreach (CreatureRef creature in _kotor.UnconfirmedCreatureScan())
        {
            if (creature.ObjectId == objectId)
            {
                scanned = creature;
                break;
            }
        }

        string scan = scanned is null
            ? "scan absent"
            : $"scan 0x{scanned.Address:X} '{Shorten(scanned.Tag)}'";

        Reply($"census 0x{objectId:X}: {array} | {scan}");

        foreach (EnemyState enemy in census)
        {
            if (enemy.ObjectId == objectId)
            {
                Reply(
                    $"census 0x{objectId:X}: listed hp {enemy.CurrentHealth}/{enemy.MaxHealth}"
                    + $" at {enemy.Position.X:F1},{enemy.Position.Y:F1}"
                    + (enemy.Dead ? " dead" : string.Empty));
                return;
            }
        }

        Reply($"census 0x{objectId:X}: not listed in this census");
    }

    /// <summary>Keeps a tag from pushing a diagnostic line past the answer channel's 96
    /// bytes. Tags are resref-sized; a long one is still recognisable from its head.</summary>
    private static string Shorten(string tag) =>
        tag.Length == 0 ? "untagged" : tag.Length <= 24 ? tag : tag[..24];

    /// <summary>
    /// Moves the local player to a peer's last known position.
    ///
    /// The module check is the whole point: peer positions are area coordinates, so the same
    /// numbers name a completely different place in a different module, and a cross-module
    /// <c>goto</c> would drop the player through the floor of the world they are in. Use
    /// <c>warp</c> to follow somebody to another module and <c>goto</c> once you are there.
    /// </summary>
    private void GoTo(string name)
    {
        if (manager.Current is not { } session)
        {
            Reply("not connected - nobody to go to");
            return;
        }

        IReadOnlyList<RemotePeer> peers = session.GetRemotePeers();
        RemotePeer? found = null;

        // "#n" picks the n-th of the `who` listing, for a name nobody wants to type.
        if (name.StartsWith('#') && int.TryParse(name[1..], out int index)
            && index >= 1 && index <= peers.Count)
        {
            found = peers[index - 1];
        }
        else
        {
            foreach (RemotePeer peer in peers)
            {
                if (peer.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    found = peer;
                    break;
                }
            }
        }

        if (found is not { } target)
        {
            Reply($"no player called '{name}' - run `who` to see who is here");
            return;
        }

        string localModule = bridge.TryReadLocalState(out PlayerState local) && local.Module.Length > 0
            ? local.Module
            : string.Empty;

        if (localModule.Length > 0 && target.State.Module.Length > 0
            && !localModule.Equals(target.State.Module, StringComparison.OrdinalIgnoreCase))
        {
            Reply($"{target.Name} is in {target.State.Module} and you are in {localModule} - warp there first");
            return;
        }

        SendCheat(new CheatRequest(
            CheatKinds.Teleport,
            X: target.State.Position.X,
            Y: target.State.Position.Y,
            Z: target.State.Position.Z));
    }

    /// <summary>Diffs the admitted roster each tick and posts joins/leaves through the same
    /// trio every reply uses (stdout, console line, chat overlay) — which is what puts the
    /// line on the HOST's screen when a player is admitted.</summary>
    private void AnnouncePeerChanges()
    {
        if (manager.Current is not { } session)
        {
            _peerAnnouncer.Reset();
            _kotor?.PublishSessionPeerCount(0);
            return;
        }

        IReadOnlyList<RemotePeer> peers = session.GetRemotePeers();
        _peerAnnouncer.Observe(peers, Reply);

        // The seamless-arrival gate's signal: written here, every tick, because THIS loop
        // never depends on the player resolve — the pump's RemoteCount does, and its 1 Hz
        // resolve is precisely why the load screen used to drop before the first sync.
        _kotor?.PublishSessionPeerCount(peers.Count);
    }

    /// <summary>
    /// Puts content-compatibility findings on the player's screen, through the same trio
    /// every reply uses. Without this the per-channel mod policy is invisible where it
    /// matters: a channel switched off for safety looks exactly like a channel that broke,
    /// and the one place the verdict was visible — the agent's console readout — is not
    /// where a player is looking.
    /// </summary>
    private void AnnounceCompatibilityChanges()
    {
        if (manager.Current is not { } session)
        {
            _compatibilityAnnouncer.Reset();
            return;
        }

        _compatibilityAnnouncer.Observe(session.Compatibility, Reply);
    }

    /// <summary>Posts the session roster: the local player first, then each remote peer with
    /// the module they were last seen in. Everything read here already exists per tick —
    /// <see cref="ICoopSession.GetRemotePeers"/> and the session stats.</summary>
    private void ListPlayers(ICoopSession session)
    {
        IReadOnlyList<RemotePeer> peers = session.GetRemotePeers();
        SessionStats stats = session.GetStats();

        string rtt = stats.RoundTripMs > 0 ? $" - rtt {stats.RoundTripMs:F0}ms" : string.Empty;
        Reply($"players ({peers.Count + 1}){rtt}:");

        string localModule = bridge.TryReadLocalState(out PlayerState state) && state.Module.Length > 0
            ? state.Module
            : "?";
        Reply($"  {session.LocalName} (you)  {localModule}");

        foreach (RemotePeer peer in peers)
        {
            string module = peer.State.Module.Length > 0 ? peer.State.Module : "?";
            string flags = peer.State.IsDowned ? "  [down]"
                : peer.State.IsInDialogue ? "  [talking]"
                : peer.State.IsInCombat ? "  [fighting]"
                : string.Empty;
            Reply($"  {peer.Name}  {module}{flags}");
        }
    }

    private void Join(string? address, string? password)
    {
        // "connect #n" dials the n-th server from the last `servers` listing.
        if (address is { Length: > 1 } && address[0] == '#'
            && int.TryParse(address[1..], out int index))
        {
            if (index < 1 || index > _lastServerList.Count)
            {
                Reply($"no server #{index} - run `servers` first");
                return;
            }

            JoinListed(index - 1, password);
            return;
        }

        CoopCommand command = new(CoopCommandKind.Connect, address, password);
        if (!command.TryResolveEndpoint(defaultPort, out IPEndPoint endpoint))
        {
            Reply($"could not resolve host '{address}'");
            return;
        }

        // A typed password wins; otherwise whatever is already held (coop.ini's, or one
        // offered ahead of the join by the in-browser prompt) rides along — the same rule
        // JoinListed applies.
        Dial(address, endpoint, password ?? _password, locked: false);
    }

    /// <summary>
    /// Joins the <paramref name="index"/>-th server of the last published listing — the path
    /// behind both <c>connect #n</c> and a row confirmed in the in-game browser panel. A typed
    /// <paramref name="password"/> wins; otherwise whatever the session is already carrying
    /// (coop.ini's, or one the player has answered a prompt with) is reused.
    /// </summary>
    private void JoinListed(int index, string? password = null)
    {
        if (index < 0 || index >= _lastServerList.Count)
        {
            Reply("that server is no longer listed");
            return;
        }

        ServerInfo chosen = _lastServerList[index];
        Reply($"connecting to {chosen.Name} ({chosen.Address})");
        Dial(chosen.Address, chosen.EndPoint, password ?? _password, chosen.Locked);
    }

    /// <summary>
    /// The one place a join is actually started. Records what a password prompt needs to retry
    /// it, and refuses to dial a server the beacon says is locked when nothing has supplied a
    /// password — prompting instead, because that attempt can only end in E_PASSWORD and a
    /// failed handshake is a slower, more confusing way to ask the same question.
    /// </summary>
    private void Dial(string? address, IPEndPoint endpoint, string password, bool locked)
    {
        _pendingAddress = address ?? endpoint.ToString();
        _pendingEndpoint = endpoint;
        _password = password;
        _promptedForPassword = false;

        if (locked && password.Length == 0)
        {
            PromptForPassword("this server is password protected");
            return;
        }

        manager.Connect(host: false, endpoint, settingsForPassword(password));
    }

    /// <summary>
    /// Answers a prompt: retry the waiting join with the typed password — or, when no join is
    /// waiting, HOLD the password for the one about to start. The unsolicited case is normal
    /// now, not an error: the in-browser prompt publishes <c>password &lt;pw&gt;</c> BEFORE
    /// the browser confirms the row, precisely so this agent already holds the secret when
    /// the connect request lands a moment later.
    /// </summary>
    private void AnswerPasswordPrompt(string password)
    {
        if (password.Length == 0)
        {
            Reply("that password was empty - type: password <the server's password>");
            return;
        }

        if (_pendingEndpoint is not { } endpoint)
        {
            // An offer: nothing to retry, so nothing is dialled — Dial picks this up via
            // _password when the join starts (the browser-confirm arrives in this same tick,
            // drained after the console line).
            _password = password;
            Reply("password saved for the next join");
            return;
        }

        _password = password;
        _promptedForPassword = false; // a wrong one must be able to ask again
        Reply($"retrying {_pendingAddress}...");
        manager.Connect(host: false, endpoint, settingsForPassword(password));
    }

    /// <summary>
    /// Asks the player for a password. The ask is handed to the PLUGIN, which force-opens the
    /// developer console pre-filled with <c>password&#160;</c> — the agent's own console is
    /// behind a fullscreen game and cannot take the keystroke. The written hint goes out too,
    /// for the headless case and because the overlay keeps it on screen after the console
    /// closes on Enter.
    /// </summary>
    private void PromptForPassword(string why)
    {
        _promptedForPassword = true;
        Reply($"{why} - type the password and press Enter (or: password <pw>)");

        // The ask has to reach the GAME. If it cannot, the player is left looking at a menu
        // with no prompt on it while we wait for an answer nobody was asked for — so say so
        // instead of failing silently, and name the manual way in.
        if (_kotor is null || !_kotor.RequestPasswordPrompt())
        {
            Console.WriteLine(
                "[coop] could not open the in-game prompt; press ~ in the game and type: password <pw>");
        }
    }

    /// <summary>
    /// Keeps the in-game browser panel fed with what the LAN listener has heard, for as long
    /// as the agent is alive — not only while it is sitting at the menu.
    ///
    /// The menu loop publishes this before you connect; nothing did afterwards, so the F9
    /// panel went empty the moment you joined a session and stayed that way, which reads as
    /// "the browser is broken" rather than "the list is not being refreshed". The publish
    /// itself is cheap: it signature-checks and returns without writing when the list has
    /// not changed, which is the common case.
    /// </summary>
    private void PublishBrowserList()
    {
        if (_kotor is null)
        {
            return;
        }

        _lastServerList = _browser.Servers;
        _kotor.PublishBrowserServers(
            [.. _lastServerList.Select(s =>
                (s.Name, s.Address, (byte)s.PlayerCount, (byte)s.MaxPlayers, s.Locked, s.Module, s.Faces))],
            passwordHeld: _password.Length > 0);
    }

    /// <summary>Posts the LAN server list to the overlay, numbered for `connect #n`.</summary>
    private void ListServers()
    {
        _lastServerList = _browser.Servers;
        if (_lastServerList.Count == 0)
        {
            Reply("no co-op servers found on the LAN (host one, or connect by IP)");
            return;
        }

        Reply($"servers on the LAN ({_lastServerList.Count}) - connect #n to join:");
        for (int i = 0; i < _lastServerList.Count; i++)
        {
            ServerInfo s = _lastServerList[i];
            Reply($"  {i + 1}) {s.Name}  {s.Address}  ({s.PlayerCount}/{s.MaxPlayers})");
        }
    }

    /// <summary>
    /// Posts a console reply when the session phase changes, and turns a password-related
    /// rejection into an actionable prompt that echoes the address to retype with a password.
    /// </summary>
    private void ReflectPhase()
    {
        SessionPhase phase = manager.Phase;
        if (phase == _lastPhase)
        {
            return;
        }

        _lastPhase = phase;

        switch (phase)
        {
            case SessionPhase.Connected:
                Reply($"connected - {manager.Detail}");
                FireMenuAutoLoadIfArmed();
                break;

            case SessionPhase.Failed:
                // The host answered E_PASSWORD: the one set in coop.ini was wrong, or there was
                // none. Ask, rather than leaving the player at a menu with a rejection they can
                // only see in the agent's window.
                if (!_promptedForPassword && _pendingEndpoint is not null
                    && manager.Detail.Contains("password", StringComparison.OrdinalIgnoreCase))
                {
                    PromptForPassword(_password.Length > 0 ? "that password was wrong" : "this server needs a password");
                }
                else
                {
                    Reply($"failed - {manager.Detail}");
                }

                // A refused join (wrong password, incompatible save...) must never load a
                // save out from under the menu.
                _menuAutoLoadArmed = false;
                break;

            case SessionPhase.Disconnected:
                Reply("disconnected");
                break;
        }
    }

    /// <summary>Surfaces chat received from peers on the in-game console and stdout.</summary>
    private void DrainChat()
    {
        if (manager.Current is not { } session)
        {
            return;
        }

        foreach (ChatMessage message in session.DrainChat())
        {
            Chat($"{message.Name}: {message.Text}");
        }
    }

    /// <summary>
    /// The agent's log sink, wired to <c>AgentDashboard.Note</c> at startup — the same hook
    /// <c>SessionManager.Log</c> uses, and for the same reason.
    ///
    /// ⚠ WITHOUT THIS EVERY REPLY IS INVISIBLE TO AN UNATTENDED RUN. `demo.ps1` gives each
    /// agent a VISIBLE console (deliberately — the readout redraws in place, and a pipe turns
    /// every redraw into another appended copy) and a SEPARATE `--log` file written by the
    /// dashboard. A reply printed with Console.WriteLine therefore lands in a window nobody
    /// is watching and never reaches the file every check actually reads. Measured on the rig
    /// on 2026-08-15: `items` answered "showed 12 of 58 - full list in the agent log" and the
    /// agent log held seven status lines and nothing else. Same class of bug as
    /// `_lastDropNote` rendering only to the dashboard.
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
