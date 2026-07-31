using System.Net;
using KotorCoop.Protocol;

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
    Profiles,
    InstallDataMods,
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
    /// Optional path the agent watches for a drop request. Writing a blueprint resref into
    /// this file (or touching it empty to use the built-in cycle) drops that item at the
    /// player's feet and deletes the file. Exists because the dashboard's D key needs an
    /// interactive console, which a scripted or redirected run does not have.
    /// </summary>
    public string? DropFile { get; init; }

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
    /// Admit a peer whose save differs, degrading to the mod-independent channels rather
    /// than refusing. For deliberately split play where enemies, doors and items are not
    /// expected to line up.
    /// </summary>
    public bool AllowDifferentSave { get; init; }

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

    public const string DefaultGamePath =
        @"C:\Program Files (x86)\Steam\steamapps\common\swkotor\swkotor.exe";

    /// <summary>Steam app id for Knights of the Old Republic.</summary>
    public const int SteamAppId = 32370;

    public static AgentOptions Parse(string[] args)
    {
        if (args.Length == 0)
        {
            return new AgentOptions { Command = AgentCommand.Help };
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
            "profiles" => AgentCommand.Profiles,
            "install-datamods" => AgentCommand.InstallDataMods,
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
        IPEndPoint? hostEndPoint = null;
        int tickRate = ProtocolConstants.DefaultTickRate;
        bool readOnly = false;
        bool wait = false;
        string gamePath = DefaultGamePath;
        bool windowed = false;
        bool viaSteam = false;
        int instance = 1;
        int loadSave = 0;
        bool useKotorBridge = false;
        int phantoms = 4;
        bool filterByModule = true;
        bool followHostModule = true;
        bool loadViaMenuClicks = false;
        bool allowDifferentSave = false;
        string password = string.Empty;
        bool syncParty = true;
        bool syncProgression = true;
        bool menuConnect = false;
        string? dropFile = null;

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
                    break;

                case "--windowed":
                    windowed = true;
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

                case "--allow-different-save":
                    allowDifferentSave = true;
                    break;

                case "--password":
                    password = Next() ?? password;
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

                case "--phantoms":
                    phantoms = int.TryParse(Next(), out int parsedPhantoms)
                        ? Math.Clamp(parsedPhantoms, 0, 8)
                        : phantoms;
                    break;

                default:
                    Console.Error.WriteLine($"Unknown argument '{arg}'.");
                    return new AgentOptions { Command = AgentCommand.Help };
            }
        }

        if (mode == CoopMode.Join && hostEndPoint is not null && port != ProtocolConstants.DefaultPort)
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
            HostEndPoint = hostEndPoint,
            TickRate = tickRate,
            ReadOnlyAttach = readOnly,
            Wait = wait,
            GamePath = gamePath,
            Windowed = windowed,
            ViaSteam = viaSteam,
            Instance = instance,
            LoadSave = loadSave,
            UseKotorBridge = useKotorBridge,
            Phantoms = phantoms,
            FilterByModule = filterByModule,
            FollowHostModule = followHostModule,
            LoadViaMenuClicks = loadViaMenuClicks,
            AllowDifferentSave = allowDifferentSave,
            Password = password,
            SyncParty = syncParty,
            SyncProgression = syncProgression,
            MenuConnect = menuConnect,
            DropFile = dropFile,
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
          profiles  List available offset profiles.
          install-datamods
                    Merge the co-op 2DA rows into the install's Override: a friendly
                    "coop_player" faction in repute.2da and a distinct "coop_p2" body
                    in appearance.2da. Merges into the user's current tables (existing
                    mod rows are preserved), is safe to re-run, and never touches
                    dialog.tlk. Point it at the game with --game-path <dir or exe>.

        TARGET SELECTION

          --pid <n>            attach by process id
          --process <name>     attach by process name, without .exe (e.g. swkotor)
          --wait               wait for the process to appear rather than failing

        LAUNCH OPTIONS

          --game-path <path>   game executable
                               (default: the Steam install under Program Files (x86))
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
          --port <n>           listen/target port (default 27015)
          --tick <n>           poll and network rate in Hz (default 20)
          --read-only          attach without write access
          --no-follow          do not automatically travel to the host's module when
                               joining. By default a client follows the host through
                               area transitions the way a summoned phantom follows the
                               world's owner.
          --password <text>    session password. On --host, only clients with the same
                               password may join; on --join, the password to present.
                               A dropped client rejoins automatically when the link
                               returns, keeping its slot.
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
          --allow-different-save  join a peer on a different save. Enemies, doors and
                               items are not shared (their object ids would not line up),
                               but phantoms, appearance, equipment and journal still work.
                               Without this, a save mismatch is refused.
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
