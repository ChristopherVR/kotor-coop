using System.Net;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

public enum AgentCommand
{
    Help,
    Run,
    Scan,
    Track,
    AutoTrack,
    Launch,
    Load,
    Info,
    Mods,
    Profiles,
    InstallDataMods,

    /// <summary>Serve this install's world (a save directory) to joiners over TCP.</summary>
    ServeWorld,

    /// <summary>Adopt a host's world and load it carrying this install's own character.</summary>
    Adopt,

    /// <summary>Put an extracted package into a game directory (PackageInstaller).</summary>
    Install,

    /// <summary>Remove what Install placed.</summary>
    Uninstall,

    /// <summary>Zip the co-op logs, the crash record and a redacted coop.ini for a bug report.</summary>
    Logs,

    /// <summary>No arguments at all: the double-click experience - find the game, install, launch.</summary>
    Setup,

    /// <summary>Offline save-crafting utility: an additive copy of a save directory with
    /// named global booleans/numbers forced to a value. See docs/research/minigames.md §11
    /// and <see cref="KotorCoop.Memory.GlobalFlagCraft"/>.</summary>
    CraftSave,
}

public enum CoopMode
{
    None,
    Host,
    Join,
}

public sealed record AgentOptions
{
    public AgentCommand Command { get; init; } = AgentCommand.Help;

    public int? Pid { get; init; }

    public string? ProcessName { get; init; }

    public string Profile { get; init; } = "kotor1";

    public string Name { get; init; } = "player";

    public CoopMode Mode { get; init; } = CoopMode.None;

    public int Port { get; init; } = ProtocolConstants.DefaultPort;

    /// <summary>Whether <c>--port</c> was actually passed on the command line, as opposed to
    /// <see cref="Port"/> merely holding its <see cref="ProtocolConstants.DefaultPort"/>
    /// default. A menu-driven (<c>--menu</c>) session takes its port from <c>coop.ini</c> when
    /// this is false, so "the CLI said nothing" has to be distinguishable from "the CLI said
    /// 27015" — see <c>AgentComposition.Compose</c>.</summary>
    public bool PortExplicit { get; init; }

    public IPEndPoint? HostEndPoint { get; init; }

    public int TickRate { get; init; } = ProtocolConstants.DefaultTickRate;

    public bool ReadOnlyAttach { get; init; }

    /// <summary>Wait for the target process to appear instead of failing immediately.</summary>
    public bool Wait { get; init; }

    /// <summary>Path to the game executable for the launch command.</summary>
    public string GamePath { get; init; } = DefaultGamePath;

    /// <summary>Rewrite swkotor.ini to run windowed, keeping a backup. Opt-in.</summary>
    public bool Windowed { get; init; }

    /// <summary>Launch through the Steam client rather than running the exe directly.</summary>
    public bool ViaSteam { get; init; }

    /// <summary>
    /// Which co-op instance this is. Anything above 1 is started with a per-instance mutex
    /// name so it coexists with an already-running copy; see <see cref="InstanceLauncher"/>.
    /// </summary>
    public int Instance { get; init; } = 1;

    /// <summary>
    /// Save to load automatically after launching, numbered from 1 for the top of the save
    /// list. 0 stops at the main menu.
    /// </summary>
    public int LoadSave { get; init; }

    /// <summary>
    /// Use the KOTOR-specific bridge (symbol-database offsets, creature vtable scan,
    /// phantom bodies) instead of a JSON offset profile.
    /// </summary>
    public bool UseKotorBridge { get; init; }

    /// <summary>How many idle creatures to claim as remote-player avatars.</summary>
    public int Phantoms { get; init; } = 4;

    /// <summary>
    /// DEBUG ONLY: overrides <see cref="KotorCoop.Memory.KotorGameBridge.BlockEnemyCapacity"/>
    /// (128 since version 80's "no cap really" pass — docs/research/creature-liveness.md) to
    /// something smaller, so a live test can exercise
    /// <see cref="KotorCoop.Memory.EnemyCensusPage"/>'s "beyond the census cap" rotation, or the
    /// creature-liveness reconciliation channel's own equivalent path (item 1 of that doc), in
    /// an ordinary module without needing one that legitimately places that many creatures.
    /// 0 (default) keeps the built-in cap — no behaviour change unless a session explicitly
    /// opts in via <c>--enemy-census-cap</c>.
    /// </summary>
    public int EnemyCensusCap { get; init; }

    /// <summary>
    /// Optional path the agent watches for a drop request. Writing a blueprint resref into
    /// this file (or touching it empty to use the built-in cycle) drops that item at the
    /// player's feet and deletes the file. Exists because the dashboard's D key needs an
    /// interactive console, which a scripted or redirected run does not have.
    /// </summary>
    public string? DropFile { get; init; }

    /// <summary>Where to append the compact status log. The console keeps its live readout;
    /// this file gets one timestamped line per change, which is the form worth keeping.</summary>
    public string? LogFile { get; init; }

    /// <summary>craft-save: the save directory to copy from. Untouched by the craft.</summary>
    public string? CraftSource { get; init; }

    /// <summary>craft-save: the save directory to create. Must not already exist.</summary>
    public string? CraftDest { get; init; }

    /// <summary>craft-save: "NAME=0" / "NAME=1" pairs, one per --bool. A bare "NAME" means
    /// 1 (set true).</summary>
    public IReadOnlyList<string> CraftBooleans { get; init; } = [];

    /// <summary>craft-save: "NAME=VALUE" pairs, one per --number.</summary>
    public IReadOnlyList<string> CraftNumbers { get; init; } = [];

    /// <summary>
    /// Hide peers whose module differs from ours. Correct between two copies of the same
    /// game, but wrong when bridging different games (or a mock harness), because the two
    /// sides name their areas differently and every peer gets filtered out.
    /// </summary>
    public bool FilterByModule { get; init; } = true;

    /// <summary>
    /// When joining, automatically travel to whatever module the host is in — the
    /// Souls-seamless "the world follows its owner" rule. Off with --no-follow for
    /// deliberately split play.
    /// </summary>
    public bool FollowHostModule { get; init; } = true;

    /// <summary>
    /// Load saves by clicking the menu instead of the in-process replay. Escape hatch:
    /// the replay currently crashes the game inside LoadPCAutoSave (2026-07-30).
    /// </summary>
    public bool LoadViaMenuClicks { get; init; }

    /// <summary>
    /// Refuse a peer whose save differs instead of admitting it with the object-id-keyed
    /// channels off. The pre-world-adoption rule, kept for anyone who would rather not play
    /// at all than play without shared enemies, doors and items.
    /// </summary>
    public bool RequireIdenticalSave { get; init; }

    /// <summary>Session password. A host with one admits only clients that match; a client
    /// presents it when joining. Empty is an open session.</summary>
    public string Password { get; init; } = string.Empty;

    /// <summary>Reconcile party companions (host drives, clients freeze and mirror). Off
    /// with --no-party-sync for split play or if solo mode misbehaves live.</summary>
    public bool SyncParty { get; init; } = true;

    /// <summary>Share party gold and PC experience (host-authoritative absolute mirror).
    /// Disable with --no-progression-sync to keep each player's own gold and level.</summary>
    public bool SyncProgression { get; init; } = true;

    /// <summary>
    /// Wait for the in-game Co-op menu button before connecting, taking the host/join
    /// details from coop.ini beside the game. Lets the player start co-op from the menu
    /// rather than the session beginning the moment the agent launches.
    /// </summary>
    public bool MenuConnect { get; init; }

    /// <summary>The supported build: DRM-free GOG (see README: Requirements).</summary>
    public const string DefaultGamePath =
        @"C:\GOG Games\Star Wars KOTOR (GOG)\swkotor.exe";

    /// <summary>Whether --game-path was given, for verbs that would rather detect the install
    /// than assume the default.</summary>
    public bool HasGamePath { get; init; }
    public string? CompareGamePath { get; init; }

    /// <summary>install: the extracted package to copy from (default: the agent's own directory).</summary>
    public string? PackageDirectory { get; init; }

    /// <summary>install: chain mode (keep a foreign dinput8.dll behind the loader).</summary>
    public bool Chain { get; init; }

    /// <summary>install: extra DLLs appended to dinput8.chain (implies chain mode).</summary>
    public IReadOnlyList<string> ChainLoad { get; init; } = [];

    /// <summary>install: overwrite a foreign dinput8.dll instead of refusing.</summary>
    public bool Force { get; init; }

    /// <summary>install: report the install layout and verify it (chain entries, markers,
    /// coop.ini) without changing anything.</summary>
    public bool Check { get; init; }

    /// <summary>install/setup: skip InstallScanCache and do a full drive/registry/Steam-library
    /// sweep, e.g. after installing a new copy of the game the cache does not know about yet.</summary>
    public bool Rescan { get; init; }

    /// <summary>Steam app id for Knights of the Old Republic.</summary>
    public const int SteamAppId = 32370;

    /// <summary>
    /// The rich-presence <c>connect</c> string this run was started from, when it was started
    /// by Steam's "Join Game" rather than by a person. Decoded by
    /// <see cref="SteamConnectStringRule"/> into <see cref="Mode"/>/<see cref="HostEndPoint"/>/
    /// <see cref="Password"/> during parsing, so everything downstream sees an ordinary join
    /// and no code path has to know Steam exists. Kept on the record purely so a log line can
    /// say where the join came from.
    /// </summary>
    public string? SteamConnect { get; init; }

    public static AgentOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new AgentOptions { Command = AgentCommand.Setup };
        }

        AgentCommand command = args[0].ToLowerInvariant() switch
        {
            "run" => AgentCommand.Run,
            "scan" => AgentCommand.Scan,
            "track" => AgentCommand.Track,
            "autotrack" => AgentCommand.AutoTrack,
            "launch" => AgentCommand.Launch,
            "load" => AgentCommand.Load,
            "info" => AgentCommand.Info,
            "mods" => AgentCommand.Mods,
            "profiles" => AgentCommand.Profiles,
            "install-datamods" => AgentCommand.InstallDataMods,
            "serve-world" => AgentCommand.ServeWorld,
            "adopt" => AgentCommand.Adopt,
            "craft-save" => AgentCommand.CraftSave,
            "install" => AgentCommand.Install,
            "uninstall" => AgentCommand.Uninstall,
            "logs" => AgentCommand.Logs,
            "setup" => AgentCommand.Setup,
            _ => AgentCommand.Help,
        };

        if (command == AgentCommand.Help)
        {
            return new AgentOptions { Command = AgentCommand.Help };
        }

        int? pid = null;
        string? processName = null;
        string profile = "kotor1";
        string name = $"player{Random.Shared.Next(100, 999)}";
        CoopMode mode = CoopMode.None;
        int port = ProtocolConstants.DefaultPort;
        bool portExplicit = false;
        IPEndPoint? hostEndPoint = null;
        int tickRate = ProtocolConstants.DefaultTickRate;
        bool readOnly = false;
        bool wait = false;
        string gamePath = DefaultGamePath;
        bool hasGamePath = false;
        string? compareGamePath = null;
        string? packageDirectory = null;
        bool chain = false;
        List<string> chainLoad = [];
        bool force = false;
        bool check = false;
        bool rescan = false;
        bool windowed = false;
        bool viaSteam = false;
        int instance = 1;
        int loadSave = 0;
        bool useKotorBridge = false;
        int phantoms = 4;
        int enemyCensusCap = 0;
        bool filterByModule = true;
        bool followHostModule = true;
        bool loadViaMenuClicks = false;
        bool requireIdenticalSave = false;
        string password = string.Empty;
        string? steamConnect = null;
        bool syncParty = true;
        bool syncProgression = true;
        bool menuConnect = false;
        string? dropFile = null;
        string? logFile = null;
        string? craftSource = null;
        string? craftDest = null;
        List<string> craftBooleans = [];
        List<string> craftNumbers = [];

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (arg)
            {
                case "--pid":
                    pid = int.TryParse(Next(), out int parsedPid) ? parsedPid : null;
                    break;

                case "--process":
                    processName = Next();
                    break;

                case "--profile":
                    profile = Next() ?? profile;
                    break;

                case "--name":
                    name = Next() ?? name;
                    break;

                case "--host":
                    mode = CoopMode.Host;
                    break;

                case "--join":
                    mode = CoopMode.Join;
                    hostEndPoint = ParseEndPoint(Next() ?? "127.0.0.1", ProtocolConstants.DefaultPort);
                    break;

                case "--port":
                    port = int.TryParse(Next(), out int parsedPort) ? parsedPort : port;
                    portExplicit = true;
                    break;

                case "--tick":
                    tickRate = int.TryParse(Next(), out int parsedTick) ? Math.Clamp(parsedTick, 1, 60) : tickRate;
                    break;

                case "--read-only":
                    readOnly = true;
                    break;

                case "--wait":
                    wait = true;
                    break;

                case "--game-path":
                    gamePath = Next() ?? gamePath;
                    hasGamePath = true;
                    break;

                case "--compare-with":
                    compareGamePath = Next();
                    break;

                case "--windowed":
                    windowed = true;
                    break;

                case "--from":
                    packageDirectory = Next();
                    break;

                case "--chain":
                    chain = true;
                    break;

                case "--load":
                    chainLoad.AddRange((Next() ?? string.Empty).Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;

                case "--force":
                    force = true;
                    break;

                case "--check":
                    check = true;
                    break;

                case "--rescan":
                    rescan = true;
                    break;

                case "--via-steam":
                    viaSteam = true;
                    break;

                case "--instance":
                    instance = int.TryParse(Next(), out int parsedInstance)
                        ? Math.Clamp(parsedInstance, 1, 9)
                        : instance;
                    break;

                case "--load-save":
                    loadSave = int.TryParse(Next(), out int parsedSave) ? Math.Max(parsedSave, 0) : 0;
                    break;

                case "--kotor":
                    useKotorBridge = true;
                    break;

                case "--no-module-filter":
                    filterByModule = false;
                    break;

                case "--no-follow":
                    followHostModule = false;
                    break;

                case "--load-via-menu":
                    loadViaMenuClicks = true;
                    break;

                case "--require-identical-save":
                    requireIdenticalSave = true;
                    break;

                // Kept accepted because it is in scripts and in the docs: it now describes
                // the default, so it is a no-op rather than an error.
                case "--allow-different-save":
                    requireIdenticalSave = false;
                    break;

                case "--password":
                    password = Next() ?? password;
                    break;

                // Steam's "Join Game" launches the app with the inviting host's rich-presence
                // connect string. Decoded here into the SAME three fields a typed
                // `--join <host:port> --password <pw>` would have set, so the Steam path and
                // the hand-typed path converge before anything downstream sees them.
                //
                // ⚠ UNTRUSTED INPUT: this arrives from another user's Steam client.
                // SteamConnectStringRule is total and does no I/O; a string it rejects ends
                // the run with the help text rather than being partly believed.
                case "--steam-join":
                    if (Next() is not { } connect
                        || !SteamConnectStringRule.TryParse(
                            connect, out string steamHost, out int steamPort, out string steamPassword))
                    {
                        Console.Error.WriteLine(
                            "--steam-join needs a connect string of the form "
                            + $"'{SteamConnectStringRule.Prefix}<host>:<port>[/<token>]'.");
                        return new AgentOptions { Command = AgentCommand.Help };
                    }

                    // Re-bracket an IPv6 literal: the rule hands the host back bare, and
                    // ParseEndPoint needs the brackets to know which colon is the port's.
                    string steamTarget = steamHost.Contains(':')
                        ? $"[{steamHost}]:{steamPort}"
                        : $"{steamHost}:{steamPort}";

                    try
                    {
                        hostEndPoint = ParseEndPoint(steamTarget, steamPort);
                    }
                    catch (Exception ex)
                    {
                        // ParseEndPoint resolves names, and this name came off the network.
                        // Program.Main parses options OUTSIDE its own try, so an escaping
                        // exception here would be an unhandled crash on a hostile string.
                        Console.Error.WriteLine($"--steam-join: could not resolve '{steamHost}': {ex.Message}");
                        return new AgentOptions { Command = AgentCommand.Help };
                    }

                    steamConnect = connect;
                    mode = CoopMode.Join;

                    if (steamPassword.Length > 0)
                    {
                        password = steamPassword;
                    }

                    break;

                case "--no-party-sync":
                    syncParty = false;
                    break;

                case "--no-progression-sync":
                    syncProgression = false;
                    break;

                case "--menu":
                    menuConnect = true;
                    break;

                case "--drop-file":
                    dropFile = Next();
                    break;

                case "--log":
                    logFile = Next();
                    break;

                case "--source":
                    craftSource = Next();
                    break;

                case "--dest":
                    craftDest = Next();
                    break;

                case "--bool":
                    if (Next() is { } boolArg)
                    {
                        craftBooleans.Add(boolArg);
                    }

                    break;

                case "--number":
                    if (Next() is { } numberArg)
                    {
                        craftNumbers.Add(numberArg);
                    }

                    break;

                case "--phantoms":
                    phantoms = int.TryParse(Next(), out int parsedPhantoms)
                        ? Math.Clamp(parsedPhantoms, 0, 8)
                        : phantoms;
                    break;

                case "--enemy-census-cap":
                    // DEBUG ONLY (docs/research/creature-liveness.md §"why 32 already covers
                    // this some other way"): lowers the enemy-census truncation
                    // (KotorGameBridge.BlockEnemyCapacity) so a small, ordinary module can be
                    // pushed past the cap without needing real content that places that many
                    // creatures. 0 (default) keeps the built-in cap. Clamped to
                    // KotorGameBridge.BlockEnemyCapacity — the single source of truth for the
                    // upper bound, referenced rather than re-typed so this clamp cannot drift
                    // from the shared-block region's own physical capacity the way a
                    // hand-copied literal could. A mistyped 0 here cannot be confused with
                    // "unset" and silently publish an empty census (clamp floor of 1).
                    enemyCensusCap = int.TryParse(Next(), out int parsedCap) && parsedCap > 0
                        ? Math.Clamp(parsedCap, 1, KotorGameBridge.BlockEnemyCapacity)
                        : enemyCensusCap;
                    break;

                default:
                    Console.Error.WriteLine($"Unknown argument '{arg}'.");
                    return new AgentOptions { Command = AgentCommand.Help };
            }
        }

        // An explicit --port always overrides whatever port --join <host[:port]> itself
        // carried — checked against PortExplicit rather than "port != DefaultPort" so that
        // `--join host --port 27015` (explicitly asking for the default) still overrides too.
        if (mode == CoopMode.Join && hostEndPoint is not null && portExplicit)
        {
            hostEndPoint = new IPEndPoint(hostEndPoint.Address, port);
        }

        return new AgentOptions
        {
            Command = command,
            Pid = pid,
            ProcessName = processName,
            Profile = profile,
            Name = name,
            Mode = mode,
            Port = port,
            PortExplicit = portExplicit,
            HostEndPoint = hostEndPoint,
            TickRate = tickRate,
            ReadOnlyAttach = readOnly,
            Wait = wait,
            GamePath = gamePath,
            Windowed = windowed,
            HasGamePath = hasGamePath,
            CompareGamePath = compareGamePath,
            PackageDirectory = packageDirectory,
            Chain = chain,
            ChainLoad = chainLoad,
            Force = force,
            Check = check,
            Rescan = rescan,
            ViaSteam = viaSteam,
            Instance = instance,
            LoadSave = loadSave,
            UseKotorBridge = useKotorBridge,
            Phantoms = phantoms,
            EnemyCensusCap = enemyCensusCap,
            FilterByModule = filterByModule,
            FollowHostModule = followHostModule,
            LoadViaMenuClicks = loadViaMenuClicks,
            RequireIdenticalSave = requireIdenticalSave,
            Password = password,
            SteamConnect = steamConnect,
            SyncParty = syncParty,
            SyncProgression = syncProgression,
            MenuConnect = menuConnect,
            DropFile = dropFile,
            LogFile = logFile,
            CraftSource = craftSource,
            CraftDest = craftDest,
            CraftBooleans = craftBooleans,
            CraftNumbers = craftNumbers,
        };
    }

    private static IPEndPoint ParseEndPoint(string text, int defaultPort)
    {
        if (IPEndPoint.TryParse(text, out IPEndPoint? parsed) && parsed.Port != 0)
        {
            return parsed;
        }

        if (IPAddress.TryParse(text, out IPAddress? address))
        {
            return new IPEndPoint(address, defaultPort);
        }

        string host = text;
        int port = defaultPort;
        int colon = text.LastIndexOf(':');

        if (colon > 0 && int.TryParse(text[(colon + 1)..], out int explicitPort))
        {
            host = text[..colon];
            port = explicitPort;
        }

        IPAddress[] resolved = Dns.GetHostAddresses(host);
        if (resolved.Length == 0)
        {
            throw new FormatException($"Could not resolve host '{host}'.");
        }

        return new IPEndPoint(resolved[0], port);
    }

    public static string HelpText =>
        """
        KotorCoop.Agent - attaches to a running game and bridges it to a co-op session.

        COMMANDS

          (none)    Setup: find the GOG install, copy the package in, offer to launch.
                    What double-clicking KotorCoop.Agent.exe in an extracted zip does.
                    `setup --rescan` forces a fresh drive sweep instead of the per-user cache.
          install   Copy the package (--from <dir>, default: beside this exe) into the game
                    directory (--game-path <dir>, default: auto-detect). If dinput8.dll is
                    already another mod's, this is detected and installed as a chain
                    automatically (our loader takes dinput8.dll, the other mod is kept as
                    dinput8.prev.dll and loaded after us) - no flag needed, and a plain copy
                    of Windows' own dinput8.dll is recognised as not-a-mod and simply
                    replaced. --chain forces chain mode even when nothing foreign is there yet
                    (accepted for scripts; it is now the automatic default); --load
                    a.dll,b.asi adds entries to dinput8.chain; --force overwrites a foreign
                    dinput8.dll outright instead of chaining it. Re-running install over an
                    existing install (plain or chain) upgrades the plugin in place and never
                    duplicates chain entries. Every install ends with the same report --check
                    prints; a non-zero exit means one of its lines said FAIL. Without
                    --game-path, auto-detect reads a per-user cache of where installs were
                    found last time instead of sweeping every drive again; --rescan forces a
                    fresh sweep (needed after installing a new copy of the game).
          uninstall Remove what install placed (--game-path); keeps coop.ini, restores a
                    kept dinput8.dll. Run from the extracted zip, not the game directory.
          logs      Zip the co-op logs, crash record and a redacted coop.ini into one file for a bug report.
          install --check
                    Report the install layout found (plain / chain / foreign-only / none)
                    and verify it - every dinput8.chain entry exists, the loader and
                    KotorCoop.Plugin.dll carry their markers, coop.ini exists - without
                    changing anything. One PASS/FAIL line per check; exits non-zero if any
                    line is FAIL.
          launch    Start KOTOR (optionally windowed) and wait for it to come up.
          load      Drive an already-running instance from the main menu into a save.
                    Separate from launch so several instances can be started first and
                    their windows moved apart: overlapping windows make menu clicks
                    land on whichever copy happens to be in front.
          autotrack Fully automatic offset discovery. Focuses the game, walks the
                    character itself, scores candidates by how they respond, and prints a
                    profile. Start here.
          track     The same discovery, but you do the walking and pick the candidate.
                    Use when autotrack cannot drive the game.
          run       Attach and run the co-op loop against a completed profile.
          scan      Raw interactive memory scanner, for when track is not enough.
          info      Print modules and memory regions of a target process.
          mods      Audit NPC/campaign content and coop-mods.json without running the game.
                    Use --game-path <dir> and optionally --compare-with <other game dir>.
          profiles  List available offset profiles.
          install-datamods
                    Merge the co-op 2DA rows into the install's Override: a friendly
                    "coop_player" faction in repute.2da and a distinct "coop_p2" body
                    in appearance.2da. Merges into the user's current tables (existing
                    mod rows are preserved), is safe to re-run, and never touches
                    dialog.tlk. Point it at the game with --game-path <dir or exe>.
          craft-save
                    Offline, additive save-flag surgery: copies --source into a brand new
                    --dest directory (which must not already exist) and, in the copy only,
                    forces named GLOBALVARS.res booleans/numbers to a value. Never touches
                    --source, never touches SAVEGAME.sav/module/position/GIT — only the two
                    ValBoolean/ValNumber blobs behind names already in that save's own
                    catalogue. A name not in the catalogue is reported, not invented. See
                    docs/research/minigames.md §11.
                      --source <dir>       save directory to copy from
                      --dest <dir>         save directory to create
                      --bool <NAME[=0|1]>  force a global boolean (repeatable; bare NAME
                                            means =1)
                      --number <NAME=N>    force a global number, -128..127 (repeatable)

        TARGET SELECTION

          --pid <n>            attach by process id
          --process <name>     attach by process name, without .exe (e.g. swkotor)
          --wait               wait for the process to appear rather than failing

        LAUNCH OPTIONS

          --game-path <path>   game executable
                               (default: the DRM-free install under C:\GOG Games)
          --windowed           set FullScreen=0 in swkotor.ini first, backing it up.
                               Strongly recommended: you cannot watch a console behind a
                               fullscreen game.
          --via-steam          launch through steam://rungameid/32370 instead of the exe
          --instance <n>       co-op instance number (default 1). 2 and above start with a
                               per-instance mutex name so several copies of KOTOR run at
                               once, which the retail single-instance guard otherwise
                               refuses. Needs a DRM-free (GOG) build.
          --load-save <n>      after launching, load save <n> with no human input, where
                               1 is the top of the save list. Waits for the main menu to
                               actually exist, drives it, then waits until the world is
                               up. Nobody has to touch the mouse; the menu is still on
                               screen for a couple of seconds while it happens.

        RUN OPTIONS

          --profile <name>     offset profile to use (default: kotor1)
          --name <text>        player name shown to peers
          --host               host a session
          --join <host[:port]> join a session
          --port <n>           listen/target port (default 27015; a --menu session takes the
                               port from coop.ini's own `port` key instead when this is
                               omitted)
          --tick <n>           poll and network rate in Hz (default 30)
          --read-only          attach without write access
          --no-follow          do not automatically travel to the host's module when
                               joining. By default a client follows the host through
                               area transitions the way a summoned phantom follows the
                               world's owner.
          --password <text>    session password. On --host, only clients with the same
                               password may join; on --join, the password to present.
                               A dropped client rejoins automatically when the link
                               returns, keeping its slot.
          --steam-join <s>     join the session named by a Steam rich-presence connect
                               string (kc1/<host>:<port>[/<token>]). This is what Steam's
                               friends-list "Join Game" passes; equivalent to --join with
                               the host, port and password it carries. Works without Steam
                               running - it is just a decoder.
          --no-party-sync      do not reconcile the party. By default the host drives the
                               companions (Trask, Carth...) and clients freeze their own
                               copies and mirror the host's, so companions stay one
                               synchronised set instead of two that drift.
          --no-progression-sync  do not share gold and experience. By default the host's
                               party gold and PC experience are mirrored onto clients, so
                               both parties level and spend together; disable to keep each
                               player's own totals.
          --menu               wait for the in-game Co-op menu button before connecting,
                               taking host/join details from coop.ini beside the game
                               (mode, host, port, password, name, menu_button). Lets the
                               player start co-op from the menu instead of the session
                               beginning the moment the agent launches.
          --require-identical-save  refuse a peer whose save differs. By default such a
                               peer is admitted with enemies, doors, items and party
                               switched off (their object ids would not line up) while
                               phantoms, appearance, equipment and journal keep working —
                               and told so on screen. `agent adopt` removes the mismatch
                               altogether by taking the host's world.
          --drop-file <path>   watch this path for a drop request. Write a blueprint
                               resref into it (or create it empty for the default cycle)
                               and the item is dropped at your feet and shared with every
                               player. For scripted demos; interactively just press D.

        TESTING MOVEMENT AGAINST THE REAL GAME

          1. dotnet run --project src/KotorCoop.Agent -- launch --windowed
          2. Load a save (or start a new game) and get in-world control of your character.
             Stand somewhere with open ground ahead, not in dialogue or combat.
          3. dotnet run --project src/KotorCoop.Agent -- autotrack --process swkotor
          4. Leave the mouse and keyboard alone. It walks the character, narrows millions
             of candidates to your coordinates, verifies them by measuring the response,
             and prints a profile snippet for profiles/kotor1.json.

        To test the co-op stack itself without the game, use KotorCoop.MockGame; it embeds
        this same agent and attaches to its own process.
        """;
}
