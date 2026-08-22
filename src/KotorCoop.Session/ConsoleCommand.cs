using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>What an in-game console (or dashboard) line asks the session layer to do.</summary>
public enum CoopCommandKind
{
    /// <summary>Blank line or comment; do nothing.</summary>
    None,

    /// <summary>Join a host: <see cref="CoopCommand.Address"/> is the typed host.</summary>
    Connect,

    /// <summary>Begin hosting.</summary>
    Host,

    /// <summary>Tear down the current session.</summary>
    Disconnect,

    /// <summary>Restart the last session with the same parameters.</summary>
    Reconnect,

    /// <summary>Print the current session status.</summary>
    Status,

    /// <summary>Send a chat line to the other players; the text is in <see cref="CoopCommand.Address"/>.</summary>
    Say,

    /// <summary>Wipe the on-screen overlay log.</summary>
    Clear,

    /// <summary>List the co-op servers discovered on the LAN.</summary>
    Servers,

    /// <summary>List the available commands.</summary>
    Help,

    /// <summary>
    /// Answer a password prompt: retry the join that is waiting on one, with the text in
    /// <see cref="CoopCommand.Password"/>. Carries no address — the address is whichever join
    /// asked, which is the whole point of the verb (the player picked a row in the browser and
    /// should not have to retype an IP they never saw).
    /// </summary>
    Password,

    /// <summary>The line was understood as a command but is malformed; see <see cref="CoopCommand.Error"/>.</summary>
    Invalid,

    /// <summary>Not one of ours (some other console command); leave it alone.</summary>
    Unknown,

    /// <summary>Move this instance to another module through the plugin's travel channel;
    /// the module resref is in <see cref="CoopCommand.Address"/> and an optional waypoint
    /// tag rides in <see cref="CoopCommand.Password"/>.</summary>
    Warp,

    /// <summary>List the connected players and which module each is in.</summary>
    Who,

    /// <summary>Print the local player's module and position.</summary>
    Where,

    /// <summary>
    /// Dump the enemy census and the engine's own verdict on every id in it — the diagnostic
    /// for "an id in the census names nothing its own publisher can resolve".
    ///
    /// Answers from whichever side it is typed on: on the fight's OWNER it reports what this
    /// instance is publishing and whether its own object array holds each id; on a mirroring
    /// instance it reports what the owner sent and which of those ids resolve locally.
    /// An optional id in <see cref="CoopCommand.Address"/> narrows it to one creature.
    /// </summary>
    Census,

    /// <summary>
    /// Ask the PLUGIN what this engine knows about one object id — the pointer chain, both
    /// ways of reaching the object array, the engine's own lookup and an in-process
    /// transcription of it, all in one frame on the game thread. The id is in
    /// <see cref="CoopCommand.Address"/>.
    ///
    /// The companion to <see cref="Census"/>, which answers the same question from the
    /// AGENT's side. Running both is how a disagreement between them is localised.
    /// </summary>
    ResolveObject,

    /// <summary>
    /// STRATEGY 2 DIAGNOSTIC (2026-08-21): peek or fire a creature's +0x260 event script.
    /// <see cref="CoopCommand.Address"/> is the object id; <see cref="CoopCommand.Amount"/>
    /// is 0 for a safe read-only peek, nonzero to actually fire it through the engine's own
    /// RunScript dispatcher. See docs/research/shared-state-audit.md §2, "Strategy 2."
    /// </summary>
    FireScript,

    /// <summary>
    /// ROUND 36 TEMPORARY DIAGNOSTIC (2026-08-21): snaps a creature to a hardcoded position
    /// near a known hostile, so a genuinely non-census creature (a party member) can be given
    /// a valid, in-range target before firing its own +0x260 script. <see
    /// cref="CoopCommand.Address"/> is the object id to move. See
    /// docs/research/shared-state-audit.md §2, round 36. Remove once settled.
    /// </summary>
    MoveNear,

    /// <summary>
    /// Inject a transport fault on the live session so a disconnect can be reproduced with
    /// nobody touching a cable. The sub-verb is in <see cref="CoopCommand.Address"/> and its
    /// argument (percentage, milliseconds, seconds, direction) in
    /// <see cref="CoopCommand.Password"/>.
    /// </summary>
    NetSim,

    // ---- staging cheats ---------------------------------------------------------
    //
    // Only the verbs whose ANSWER DEPENDS ON WHO TYPED THEM, plus the two that stage a
    // co-op scenario nothing else reaches. KOTOR's own console already ships giveitem,
    // givecredits, addexp, addlevel, revealmap and heal, all of which touch only the
    // typist's single-player state and are therefore already correct in co-op; duplicating
    // them here would add a second spelling and no correctness. See
    // docs/research/console-cheats.md.

    /// <summary>Toggle or report the local player's damage floor.
    /// <see cref="CoopCommand.Amount"/>: 1 on, 0 off, -1 report.</summary>
    God,

    /// <summary>Set the local player's hit points; <see cref="CoopCommand.Amount"/> 0 = to
    /// maximum.</summary>
    TopUp,

    /// <summary>Stand the local player up out of the co-op downed state.</summary>
    StandUp,

    /// <summary>Kill a creature through whichever authority path this instance owns. The
    /// target token (tag or id) is in <see cref="CoopCommand.Address"/>; empty means
    /// "the nearest hostile", resolved by the agent from its own enemy census.</summary>
    Slay,

    /// <summary>Move the local player to <see cref="CoopCommand.X"/>/<see cref="CoopCommand.Y"/>
    /// /<see cref="CoopCommand.Z"/> inside the current area.</summary>
    Teleport,

    /// <summary>Move the local player to a peer, named in <see cref="CoopCommand.Address"/>.</summary>
    GoTo,

    /// <summary>Put a blueprint on the floor at the local player's feet, where the item
    /// channel will replicate it to everyone.</summary>
    DropItem,

    /// <summary>List the staging cheats and the retail console cheats worth knowing.</summary>
    Cheats,

    /// <summary>
    /// Stage a cinematic onto a module transition — a TEST INSTRUMENT, not a convenience.
    /// <see cref="CoopCommand.Amount"/> carries a <see cref="StageMovieModes"/> value,
    /// <see cref="CoopCommand.Password"/> the comma-separated movie names, and
    /// <see cref="CoopCommand.Address"/> the destination module for the warp form.
    /// </summary>
    StageMovie,

    /// <summary>
    /// List the live items this instance can see, with their object ids and per-instance
    /// state. <see cref="CoopCommand.Amount"/> caps how many rows are printed.
    ///
    /// The point is picking a target: item identity can only be exercised by sending an item
    /// that genuinely differs from its blueprint, and nothing before this could say which of
    /// the hundreds of items in a loaded module those are.
    /// </summary>
    Items,

    /// <summary>Print one item's per-instance state; the id is in
    /// <see cref="CoopCommand.Address"/>. This is the READ-BACK half of an identity test —
    /// run it on the receiving instance against the object id its plugin log names.</summary>
    InspectItem,

    /// <summary>Poke one live item's per-instance state, so a sender has something worth
    /// sending. The id is in <see cref="CoopCommand.Address"/> and the fields in
    /// <see cref="CoopCommand.Item"/>.</summary>
    SetItem,

    /// <summary>
    /// Make the local player USE a door, so the ENGINE starts a module transition — the other
    /// half of the cinematic instrument, and the only half that opens the plugin's pre-store
    /// flush window. <see cref="CoopCommand.Address"/> carries the door's tag, or an object id
    /// in the three spellings <see cref="ConsoleCommand.TryParseObjectId"/> accepts; empty
    /// means "the nearest module transition door".
    /// </summary>
    UseDoor,

    /// <summary>Give a materialised enemy's local stand-in one item of its real corpse's
    /// actual loot. The owner's enemy id is in <see cref="CoopCommand.Address"/>, the resref
    /// in <see cref="CoopCommand.Password"/>, and the stack count in
    /// <see cref="CoopCommand.Amount"/>.</summary>
    GiveLoot,

    /// <summary>
    /// STAGING VERB (2026-08-22): run one of the peer-effect channel's non-damage
    /// constructors directly, through <c>OdysseyEffects.ApplyEffect</c> — the SAME apply-side
    /// path a peer's forwarded effect calls on arrival. Built because
    /// docs/research/effect-sync.md's live list had never run: no reachable test module has a
    /// Force-using hostile, and until this verb there was no way to apply a non-damage effect
    /// on demand at all.
    ///
    /// <see cref="CoopCommand.Amount"/> is the <c>PeerEffectKind</c> numeric value (1..9);
    /// <see cref="CoopCommand.Address"/> is the target's object id, or null for "the nearest
    /// phantom"; <see cref="CoopCommand.EffectDurationMs"/>/<see cref="CoopCommand.EffectMagnitude"/>
    /// carry the effect's duration and (Slow only) its percent.
    ///
    /// Staging only, local apply: this never crosses the network by itself. Aimed at a peer's
    /// PHANTOM it IS the live test — <c>PhantomEffectHook</c> captures it landing there and the
    /// ordinary channel forwards it for real, so what the hook does with it afterward is what
    /// is actually being measured. Aimed at a local creature (including your own player, via
    /// <c>me</c>) it is a constructor smoke test only. See docs/research/console-cheats.md.
    /// </summary>
    ApplyEffect,

    /// <summary>
    /// Read-only. Lists the last few peer-effect records this agent has captured, forwarded
    /// or applied, read straight out of the shared block's rings — so a live pass can read
    /// the whole non-damage effect pipeline without grepping either log. Takes no arguments.
    /// </summary>
    Effects,

    /// <summary>
    /// TEST HOOK (2026-08-22): plays <see cref="CoopCommand.Address"/> (a bare resref) RIGHT
    /// NOW on the local instance, through the same engine entry a mid-module <c>PlayMovie</c>
    /// call and the movie relay's own <c>PlayNow</c> path use
    /// (<c>OdysseyMovies.PlayMovieNow</c>) — built because docs/research/cinematics.md §10.7
    /// found no isolated way to fire <c>MoviePlayHook</c> on the host without a story trigger:
    /// <c>moviewarp</c> only ever writes the TRANSITION queue, a different path entirely.
    /// Follows <c>ApplyEffect</c>'s pattern exactly: staging only, local apply — this never
    /// crosses the network by itself, but the capturing hook and the movie-relay controller run
    /// exactly as they would for a peer's real script call, so the OTHER instance seeing
    /// <c>movie-relay: playing '...' now</c> is the actual test.
    /// </summary>
    MoviePlay,

    /// <summary>
    /// STAGING VERB (2026-08-22): destroys a creature outright, through
    /// <c>Odyssey.DestroyObjectById</c> — the SAME engine call
    /// <c>LivenessReconcileController</c>'s own Destroy action already wraps. Built because
    /// docs/research/creature-liveness.md's live passes (2026-08-22) found no console verb that
    /// could put a creature into the "Gone" state: <c>slay</c> only ever calls
    /// <c>Odyssey.KillCreature</c>. <see cref="CoopCommand.Address"/> is the target's object id —
    /// no tag form, no "nearest" fallback; this stages a SPECIFIC id, typically one `census`
    /// already named as out-of-census. Follows <c>ApplyEffect</c>'s wiring and <c>Slay</c>'s
    /// authority rules exactly (refuses a player, phantom, companion, or plot creature), but is
    /// HOST-ONLY where <c>Slay</c> forwards — a mirroring instance is refused, not handed off,
    /// because there is no destroy-ledger to spend a forwarded destroy through.
    /// </summary>
    DestroyObject,

    /// <summary>
    /// ROUND 57 STAGING VERB: opens an ORDINARY (non-transition) door through the engine's own
    /// door-use action, so the generic door-sync path can be verified without a working
    /// walk/click primitive. <see cref="CoopCommand.Address"/> carries the door's tag, or an
    /// object id in the three spellings <see cref="ConsoleCommand.TryParseObjectId"/> accepts;
    /// empty means "the nearest door" (no module-transition filter — that is <c>usedoor</c>'s
    /// own guard). Never walks the player through it.
    /// </summary>
    OpenDoor,

    /// <summary>
    /// CONTAINER-SYNC STAGING VERB (2026-08-22): makes the local player USE a placeable
    /// (chest, corpse, footlocker) through the engine's own use-object input, so the real
    /// OnUsed/GUI path runs and this instance's container-lock/animation machinery has
    /// something real to react to. <see cref="CoopCommand.Address"/> carries the placeable's
    /// tag, or an object id in the three spellings <see cref="ConsoleCommand.TryParseObjectId"/>
    /// accepts; empty means "the nearest container placeable". See
    /// docs/research/container-sync.md.
    /// </summary>
    UsePlaceable,
}

/// <summary>
/// What a <c>moviewarp</c> line asks for. Mirrored in <c>MovieStaging.Mode*</c> on the plugin
/// side (which cannot reference this assembly), the same way <c>CheatKinds</c> mirrors the
/// shared block's kind constants.
/// </summary>
public static class StageMovieModes
{
    /// <summary>Arm the movies, then warp: they are written into the engine's queue on the
    /// frame the travel path fires, and nowhere else.</summary>
    public const int WarpWithMovies = 0;

    /// <summary>Write them into the queue now and leave them, so the NEXT transition of any
    /// kind — a door, a script, the escape pod — carries them.</summary>
    public const int QueueOnly = 1;

    /// <summary>Empty the queue again.</summary>
    public const int Clear = 2;

    /// <summary>Report what is queued and change nothing.</summary>
    public const int Show = 3;
}

/// <summary>
/// Which of an item's per-instance fields a <see cref="CoopCommandKind.SetItem"/> line named.
/// Null means "leave it alone", which is a first-class answer here: a `setitem` that named
/// nothing is a pure read, and one that named a stack must not silently reset the charges.
/// </summary>
public readonly record struct ItemFields(
    uint? StackSize = null,
    uint? Charges = null,
    uint? MaxCharges = null,
    uint? Upgrades = null,
    bool? Identified = null)
{
    public bool Any =>
        StackSize is not null || Charges is not null || MaxCharges is not null
        || Upgrades is not null || Identified is not null;
}

/// <summary>
/// A parsed console command. <see cref="Address"/> and <see cref="Password"/> carry the
/// arguments for <see cref="CoopCommandKind.Connect"/>/<see cref="CoopCommandKind.Host"/>;
/// <see cref="Error"/> explains an <see cref="CoopCommandKind.Invalid"/> line.
/// </summary>
public readonly record struct CoopCommand(
    CoopCommandKind Kind,
    string? Address = null,
    string? Password = null,
    string Error = "",
    int Amount = 0,
    float X = 0f,
    float Y = 0f,
    float Z = 0f,
    ItemFields Item = default,
    uint EffectDurationMs = 0,
    uint EffectMagnitude = 0)
{
    /// <summary>
    /// Resolves <see cref="Address"/> to an endpoint, applying <paramref name="defaultPort"/>
    /// when the typed host omits one. Returns false for a host-mode or malformed command.
    /// </summary>
    public bool TryResolveEndpoint(int defaultPort, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.Loopback, defaultPort);

        if (string.IsNullOrWhiteSpace(Address))
        {
            return false;
        }

        string candidate = Address.Contains(':') ? Address : $"{Address}:{defaultPort}";
        if (IPEndPoint.TryParse(candidate, out IPEndPoint? parsed))
        {
            endpoint = parsed;
            return true;
        }

        // A bare hostname (not an IP literal): resolve it.
        string hostPart = Address;
        int port = defaultPort;
        int colon = Address.LastIndexOf(':');
        if (colon > 0 && int.TryParse(Address[(colon + 1)..], out int typedPort))
        {
            hostPart = Address[..colon];
            port = typedPort;
        }

        try
        {
            IPAddress[] resolved = Dns.GetHostAddresses(hostPart);
            if (resolved.Length > 0)
            {
                endpoint = new IPEndPoint(resolved[0], port);
                return true;
            }
        }
        catch
        {
            // Unresolvable; fall through to false.
        }

        return false;
    }
}

/// <summary>
/// Parses console/overlay command lines into <see cref="CoopCommand"/>. The grammar is
/// deliberately tiny and forgiving — this is typed by a player mid-game, not scripted:
///   <c>connect 1.2.3.4</c> · <c>connect 1.2.3.4:7777 secret</c> · <c>host</c> ·
///   <c>disconnect</c> · <c>reconnect</c> · <c>status</c> · <c>help</c>.
/// A leading <c>coop</c> (or <c>/coop</c>) prefix is accepted and stripped, so the same
/// parser serves both a dedicated overlay and a shared console.
/// </summary>
public static class ConsoleCommand
{
    public static CoopCommand Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return new CoopCommand(CoopCommandKind.None);
        }

        string[] parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int index = 0;

        // Optional "coop"/"/coop" prefix so it works as a sub-command of a shared console.
        string first = parts[index].TrimStart('/');
        if (first.Equals("coop", StringComparison.OrdinalIgnoreCase))
        {
            index++;
            if (index >= parts.Length)
            {
                return new CoopCommand(CoopCommandKind.Help);
            }
        }

        string verb = parts[index].TrimStart('/').ToLowerInvariant();
        string[] rest = parts[(index + 1)..];

        switch (verb)
        {
            case "connect" or "join":
                if (rest.Length == 0)
                {
                    return new CoopCommand(CoopCommandKind.Invalid, Error: "connect needs a host address, e.g. connect 1.2.3.4");
                }

                return new CoopCommand(
                    CoopCommandKind.Connect,
                    Address: rest[0],
                    Password: rest.Length > 1 ? string.Join(' ', rest[1..]) : null);

            case "host":
                return new CoopCommand(
                    CoopCommandKind.Host,
                    Password: rest.Length > 0 ? string.Join(' ', rest) : null);

            // The whole rest of the line is the password, spaces and all: a passphrase is a
            // perfectly ordinary thing to set, and silently keeping only its first word would
            // look exactly like a wrong password.
            case "password" or "pw":
                return rest.Length == 0
                    ? new CoopCommand(CoopCommandKind.Invalid, Error: "password needs the password, e.g. password hoth")
                    : new CoopCommand(CoopCommandKind.Password, Password: string.Join(' ', rest));

            case "disconnect" or "dc" or "leave":
                return new CoopCommand(CoopCommandKind.Disconnect);

            case "reconnect" or "rc":
                return new CoopCommand(CoopCommandKind.Reconnect);

            case "say" or "chat" or "msg":
                return rest.Length == 0
                    ? new CoopCommand(CoopCommandKind.Invalid, Error: "say needs a message, e.g. say on my way")
                    : new CoopCommand(CoopCommandKind.Say, Address: string.Join(' ', rest));

            case "clear" or "cls":
                return new CoopCommand(CoopCommandKind.Clear);

            case "servers" or "browse" or "list":
                return new CoopCommand(CoopCommandKind.Servers);

            case "status" or "stat":
                return new CoopCommand(CoopCommandKind.Status);

            case "help" or "?" or "commands":
                return new CoopCommand(CoopCommandKind.Help);

            case "warp":
                // Validated here rather than in the dispatcher so a typo is caught with a
                // message instead of a silent no-op travel request. A module resref is at
                // most 16 chars of [a-z0-9_]; a waypoint TAG is looser but rides the same
                // 32-byte block field, so it gets the same charset with the block's cap.
                if (rest.Length == 0)
                {
                    return new CoopCommand(CoopCommandKind.Invalid, Error: "warp needs a module, e.g. warp danm13");
                }

                if (!IsResRef(rest[0], maxLength: 16))
                {
                    return new CoopCommand(CoopCommandKind.Invalid, Error: $"'{rest[0]}' is not a module name (letters, digits, _; max 16)");
                }

                if (rest.Length > 1 && !IsResRef(rest[1], maxLength: 31))
                {
                    return new CoopCommand(CoopCommandKind.Invalid, Error: $"'{rest[1]}' is not a waypoint tag (letters, digits, _; max 31)");
                }

                return new CoopCommand(
                    CoopCommandKind.Warp,
                    Address: rest[0].ToLowerInvariant(),
                    Password: rest.Length > 1 ? rest[1] : null);

            // netsim off | status | loss <pct> | latency <ms> [jitter] | blackout <secs> [in|out|both]
            //
            // Validated here rather than in the dispatcher for the same reason `warp` is: a
            // typo in a fault-injection command must not silently arm nothing (or, worse,
            // something else) on a live session.
            case "netsim" or "net":
                return ParseNetSim(rest);

            case "who" or "peers":
                return new CoopCommand(CoopCommandKind.Who);

            case "where":
                return new CoopCommand(CoopCommandKind.Where);

            // An optional id turns the dump into a single question about one creature, which
            // is what an argument about a specific id actually needs.
            case "census":
                return new CoopCommand(
                    CoopCommandKind.Census, Address: rest.Length > 0 ? rest[0] : null);

            case "resolve":
                return rest.Length == 0
                    ? new CoopCommand(CoopCommandKind.Invalid, Error: "resolve <id>  (e.g. resolve 0x35)")
                    : new CoopCommand(CoopCommandKind.ResolveObject, Address: rest[0]);

            // firescript <id>              - peek slot +0x260, does not fire
            // firescript <id> fire         - fires +0x260 (OnCombatRoundEnd, k_def_combend01)
            // firescript <id> fire 270     - fires +0x270 instead (ROUND 34's OnNotice
            //                                candidate - ties Amount to the slot offset
            //                                directly rather than adding a new field)
            case "firescript":
                if (rest.Length == 0)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid, Error: "firescript <id> [fire [270]]  (e.g. firescript 0x35)");
                }

                bool fire = rest.Length > 1 && rest[1].Equals("fire", StringComparison.OrdinalIgnoreCase);
                int slotAmount = fire ? 1 : 0;
                if (fire && rest.Length > 2 && rest[2] == "270")
                {
                    slotAmount = 0x270;
                }

                return new CoopCommand(CoopCommandKind.FireScript, Address: rest[0], Amount: slotAmount);

            // movenear <id>  - ROUND 36 TEMPORARY DIAGNOSTIC, snaps <id> near a known hostile
            case "movenear":
                return rest.Length == 0
                    ? new CoopCommand(CoopCommandKind.Invalid, Error: "movenear <id>  (e.g. movenear 0x127)")
                    : new CoopCommand(CoopCommandKind.MoveNear, Address: rest[0]);

            // ---- staging cheats -------------------------------------------------

            // A cheat that only ever applies to your own character REFUSES A TARGET rather
            // than ignoring one. "god malak" is a reasonable thing for a player to try, and
            // silently arming their own invulnerability instead would be a lie about what
            // just happened — so the argument is validated and the message names the rule.
            case "god":
                if (rest.Length == 0)
                {
                    return new CoopCommand(CoopCommandKind.God, Amount: -1);
                }

                return rest[0].ToLowerInvariant() switch
                {
                    "on" or "1" or "true" or "yes" => new CoopCommand(CoopCommandKind.God, Amount: 1),
                    "off" or "0" or "false" or "no" => new CoopCommand(CoopCommandKind.God, Amount: 0),
                    _ => new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: "god applies to your own character only - use: god [on|off]"),
                };

            case "topup":
                if (rest.Length == 0)
                {
                    return new CoopCommand(CoopCommandKind.TopUp);
                }

                if (!int.TryParse(rest[0], out int hitPoints) || hitPoints <= 0)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"'{rest[0]}' is not a hit-point count - use: topup [n], or topup for full");
                }

                return new CoopCommand(CoopCommandKind.TopUp, Amount: hitPoints);

            case "standup" or "getup":
                if (rest.Length == 0)
                {
                    return new CoopCommand(CoopCommandKind.StandUp);
                }

                if (!int.TryParse(rest[0], out int reviveHitPoints) || reviveHitPoints <= 0)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"'{rest[0]}' is not a hit-point count - use: standup [n]");
                }

                return new CoopCommand(CoopCommandKind.StandUp, Amount: reviveHitPoints);

            case "slay":
                if (rest.Length == 0)
                {
                    // No target: the agent picks the nearest hostile from its own census.
                    return new CoopCommand(CoopCommandKind.Slay);
                }

                // Aiming this at a person is the mistake worth catching in the parser: the
                // plugin refuses it too (a phantom and a companion are both recognised
                // there), but a player who types "slay me" deserves the reason immediately
                // rather than a round trip.
                if (IsSelfWord(rest[0]))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: "slay is for enemies - your own character is not a target");
                }

                if (!IsTargetToken(rest[0]))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"'{rest[0]}' is not a creature tag or object id");
                }

                return new CoopCommand(CoopCommandKind.Slay, Address: rest[0]);

            case "tp":
                if (rest.Length < 2)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: "tp needs coordinates, e.g. tp 12.5 -30.0 (z optional)");
                }

                if (!TryParseCoordinate(rest[0], out float x) || !TryParseCoordinate(rest[1], out float y))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"'{rest[0]} {rest[1]}' is not a coordinate pair");
                }

                float z = 0f;
                if (rest.Length > 2 && !TryParseCoordinate(rest[2], out z))
                {
                    return new CoopCommand(CoopCommandKind.Invalid, Error: $"'{rest[2]}' is not a height");
                }

                return new CoopCommand(CoopCommandKind.Teleport, X: x, Y: y, Z: z);

            case "goto":
                return rest.Length == 0
                    ? new CoopCommand(CoopCommandKind.Invalid, Error: "goto needs a player, e.g. goto Malak (or `who` to list)")
                    : new CoopCommand(CoopCommandKind.GoTo, Address: string.Join(' ', rest));

            case "dropitem":
                if (rest.Length == 0)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: "dropitem needs a blueprint or a live item id, "
                            + "e.g. dropitem g_w_blstrpstl001 or dropitem 0x131");
                }

                // Two forms, and only the second can exercise item identity. A blueprint name
                // conjures a stock item — there is no instance to read a stack size or an
                // upgrade mask off, and inventing one would be indistinguishable from having
                // read a genuinely stock item. An object id names a LIVE item here, whose
                // real state travels with it.
                //
                // ⚠ An id must be spelled "#131" or "0x131", never a bare "131". Both a resref
                // and a decimal id can be digits, and guessing between them would silently
                // drop the wrong thing; the two forms that cannot be a resref are the two we
                // accept. ItemDropRequest does the parsing so the drop FILE and this verb
                // cannot disagree about what "#131" means (it is hex, because every id this
                // project logs is printed in hex).
                if (rest[0].StartsWith('#')
                    || rest[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return new CoopCommand(
                        CoopCommandKind.DropItem, Address: rest[0].ToLowerInvariant());
                }

                if (!IsResRef(rest[0], maxLength: 16))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"'{rest[0]}' is neither a blueprint resref (letters, digits, _; "
                            + "max 16) nor an object id (write one as 0x131 or #131)");
                }

                return new CoopCommand(CoopCommandKind.DropItem, Address: rest[0].ToLowerInvariant());

            // The staging instrument. Spelled in full, with no alias and no short form,
            // deliberately: what it arranges is a movie playing across a module transition,
            // and a movie parks the game's main thread for its whole length — several seconds
            // in which this plugin is not slow, it is UNREACHABLE, and every invariant it
            // maintains is unattended. That is a thing to ask for on purpose.
            case "moviewarp":
                return ParseStageMovie(rest);

            // The other half of the instrument, and spelled in full for the same reason: it
            // asks the ENGINE to start a module transition, which stores the outgoing area.
            // Anything the plugin has spawned and not destroyed by then is written into the
            // save permanently.
            case "usedoor":
                if (rest.Length == 0)
                {
                    // No argument is a legitimate form — "the nearest module transition door"
                    // — and it is SAFE because the nearest-door search refuses every door that
                    // is not a transition. A bare usedoor can do nothing else.
                    return new CoopCommand(CoopCommandKind.UseDoor);
                }

                if (rest.Length > 1)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: "usedoor takes one door, e.g. usedoor end_door10 (or bare, for the nearest)");
                }

                if (!TryParseObjectId(rest[0], out _) && !IsResRef(rest[0], maxLength: 31))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"'{rest[0]}' is not a door tag or object id");
                }

                return new CoopCommand(CoopCommandKind.UseDoor, Address: rest[0]);

            // ROUND 57: an ordinary door, deliberately not gated to LinkedToModule the way
            // usedoor is — see CoopCommandKind.OpenDoor.
            case "opendoor":
                if (rest.Length > 1)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: "opendoor takes one door, e.g. opendoor end_door01 (or bare, for the nearest)");
                }

                if (rest.Length == 0)
                {
                    return new CoopCommand(CoopCommandKind.OpenDoor);
                }

                if (!TryParseObjectId(rest[0], out _) && !IsResRef(rest[0], maxLength: 31))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"'{rest[0]}' is not a door tag or object id");
                }

                return new CoopCommand(CoopCommandKind.OpenDoor, Address: rest[0]);

            // CONTAINER-SYNC STAGING VERB: the same shape as opendoor, for a placeable —
            // see CoopCommandKind.UsePlaceable.
            case "useplaceable":
                if (rest.Length > 1)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: "useplaceable takes one placeable, e.g. useplaceable end_chest01 "
                            + "(or bare, for the nearest container)");
                }

                if (rest.Length == 0)
                {
                    return new CoopCommand(CoopCommandKind.UsePlaceable);
                }

                if (!TryParseObjectId(rest[0], out _) && !IsResRef(rest[0], maxLength: 31))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"'{rest[0]}' is not a placeable tag or object id");
                }

                return new CoopCommand(CoopCommandKind.UsePlaceable, Address: rest[0]);

            case "items":
                // A cap rather than a page: the listing goes to the agent's log, where a
                // hundred rows costs nothing, but the on-screen overlay is a handful of lines
                // and a full module would scroll everything else off it.
                return new CoopCommand(
                    CoopCommandKind.Items,
                    Amount: rest.Length > 0 && int.TryParse(rest[0], out int cap) ? cap : 0);

            case "item":
                if (rest.Length == 0 || !TryParseItemId(rest[0], out uint inspectId))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: "item needs an object id written as 0x131 or #131 "
                            + "(`items` lists them)");
                }

                return new CoopCommand(CoopCommandKind.InspectItem, Address: $"#{inspectId:x}");

            case "setitem":
                return ParseSetItem(rest);

            case "giveloot":
                return ParseGiveLoot(rest);

            case "applyeffect":
                return ParseApplyEffect(rest);

            case "effects":
                return rest.Length > 0
                    ? new CoopCommand(CoopCommandKind.Invalid, Error: "effects takes no arguments")
                    : new CoopCommand(CoopCommandKind.Effects);

            // TEST HOOK: fires MoviePlayHook on demand, on the HOST, without waiting for a
            // story trigger to reach a mid-module PlayMovie call. See CoopCommandKind.MoviePlay.
            case "movieplay":
                if (rest.Length != 1 || !IsResRef(rest[0], maxLength: 16))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: "movieplay <resref>  (e.g. movieplay 01c, or movieplay biologo)");
                }

                return new CoopCommand(CoopCommandKind.MoviePlay, Address: rest[0]);

            // destroyobject <id>  - STAGING VERB (2026-08-22), see CoopCommandKind.DestroyObject.
            // Object id only, deliberately: this stages a SPECIFIC creature `census` already
            // named, not a sweep.
            case "destroyobject" or "destroy":
                if (rest.Length != 1)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: "destroyobject needs exactly one object id, e.g. destroyobject 0x35");
                }

                if (IsSelfWord(rest[0]))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: "destroyobject is for enemies - your own character is not a target");
                }

                if (!TryParseObjectId(rest[0], out _))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"'{rest[0]}' is not an object id (try 0x35, or '#35')");
                }

                return new CoopCommand(CoopCommandKind.DestroyObject, Address: rest[0]);

            case "cheats":
                return new CoopCommand(CoopCommandKind.Cheats);

            default:
                return new CoopCommand(CoopCommandKind.Unknown);
        }
    }

    /// <summary>
    /// Parses the <c>netsim</c> sub-grammar. Every branch either produces a fully-specified
    /// command or an <see cref="CoopCommandKind.Invalid"/> naming the correct shape — there is
    /// no "close enough" reading of a line that deliberately breaks the network.
    /// </summary>
    private static CoopCommand ParseNetSim(string[] rest)
    {
        const string usage =
            "netsim off | status | loss <pct> | latency <ms> [jitter] | blackout <seconds> [in|out|both]";

        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.NetSim, Address: "status");
        }

        string sub = rest[0].ToLowerInvariant();
        string[] args = rest[1..];

        switch (sub)
        {
            case "off" or "none" or "clear":
                return new CoopCommand(CoopCommandKind.NetSim, Address: "off");

            case "status" or "stat":
                return new CoopCommand(CoopCommandKind.NetSim, Address: "status");

            case "loss" or "drop":
                if (args.Length == 0 || !double.TryParse(args[0], out double percent)
                    || percent < 0 || percent > 100)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid, Error: "netsim loss needs a percentage 0-100, e.g. netsim loss 30");
                }

                return new CoopCommand(CoopCommandKind.NetSim, Address: "loss", Password: string.Join(' ', args));

            case "latency" or "lag" or "delay":
                if (args.Length == 0 || !int.TryParse(args[0], out int latency) || latency < 0)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid, Error: "netsim latency needs milliseconds, e.g. netsim latency 120 40");
                }

                if (args.Length > 1 && (!int.TryParse(args[1], out int jitter) || jitter < 0))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid, Error: "netsim latency jitter must be milliseconds, e.g. netsim latency 120 40");
                }

                return new CoopCommand(CoopCommandKind.NetSim, Address: "latency", Password: string.Join(' ', args));

            case "blackout" or "cut" or "drop-all":
                if (args.Length == 0 || !double.TryParse(args[0], out double seconds) || seconds <= 0)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid, Error: "netsim blackout needs seconds, e.g. netsim blackout 10");
                }

                if (args.Length > 1 && args[1].ToLowerInvariant() is not ("in" or "out" or "both"))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid, Error: "netsim blackout direction must be in, out or both");
                }

                return new CoopCommand(CoopCommandKind.NetSim, Address: "blackout", Password: string.Join(' ', args));

            default:
                return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }
    }

    /// <summary>
    /// Parses the <c>moviewarp</c> sub-grammar — the one verb here that exists to arrange a
    /// multi-second window in which the in-game plugin does not run at all.
    ///
    /// Validated hard, in the parser, for the reason <c>netsim</c> is: a typo in a staging
    /// command must not silently arm nothing. It must also not silently arm SOMETHING ELSE —
    /// the engine's own queue add (`AddMovieToExoArrayList 0x005FBBF0`) builds
    /// <c>MOVIES:&lt;name&gt;</c> and quietly refuses a file it cannot open, so a mistyped
    /// movie produces a transition with no cinematic that looks exactly like the defect this
    /// verb was typed to measure.
    ///
    /// <c>moviewarp &lt;module&gt; &lt;movie&gt; [movie...]</c> stages and warps ·
    /// <c>moviewarp queue &lt;movie&gt; [movie...]</c> stages only, so the next transition of
    /// ANY kind carries it · <c>moviewarp clear</c> disarms · <c>moviewarp show</c> reports.
    /// </summary>
    private static CoopCommand ParseStageMovie(string[] rest)
    {
        const string usage =
            "moviewarp <module> <movie> [movie...] | moviewarp queue <movie> [movie...] | "
            + "moviewarp clear | moviewarp show";

        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        string first = rest[0].ToLowerInvariant();

        if (first is "clear" or "off")
        {
            return new CoopCommand(CoopCommandKind.StageMovie, Amount: StageMovieModes.Clear);
        }

        if (first is "show" or "status")
        {
            return new CoopCommand(CoopCommandKind.StageMovie, Amount: StageMovieModes.Show);
        }

        bool queueOnly = first == "queue";
        string module = queueOnly ? string.Empty : first;

        if (!queueOnly && !IsResRef(first, maxLength: 16))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not a module name (letters, digits, _; max 16)");
        }

        if (rest.Length < 2)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: queueOnly
                    ? "moviewarp queue needs a movie, e.g. moviewarp queue 01c"
                    : "moviewarp needs a movie as well as a module, e.g. moviewarp tar_m02af 01c "
                      + "(plain `warp` moves with no cinematic)");
        }

        // Commas or spaces, because both are natural to type and the wire form is commas.
        List<string> names = [];
        foreach (string word in rest[1..])
        {
            foreach (string piece in word.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                names.Add(piece);
            }
        }

        if (names.Count == 0)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: "moviewarp needs at least one movie name");
        }

        // ⚠ These three limits mirror MovieStaging on the plugin side, which cannot be
        // referenced from here (it compiles for win-x86 inside the game). MaxMovies is the
        // engine's own ceiling — NWScript StartNewModule takes exactly six movie arguments —
        // and MaxJoined is the shared block's 32-byte cheat text field, one byte reserved for
        // the terminator. MovieStagingTests asserts the two halves still agree.
        const int maxMovies = 6;
        const int maxJoined = 31;

        if (names.Count > maxMovies)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"{names.Count} movies named; the engine's own queue takes at most {maxMovies}");
        }

        for (int i = 0; i < names.Count; i++)
        {
            if (!IsResRef(names[i], maxLength: 16))
            {
                return new CoopCommand(
                    CoopCommandKind.Invalid,
                    Error: $"'{names[i]}' is not a movie name (letters, digits, _; max 16)");
            }

            // Refused rather than de-duplicated, because the ENGINE de-duplicates silently
            // (the queue add is CExoArrayList<CExoString>::AddUnique), which would leave the
            // reply claiming two movies where one was staged.
            for (int j = 0; j < i; j++)
            {
                if (names[i].Equals(names[j], StringComparison.OrdinalIgnoreCase))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"'{names[i]}' is named twice; the engine's queue add is unique-only");
                }
            }
        }

        string joined = string.Join(',', names);
        if (joined.Length > maxJoined)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"the movie list is {joined.Length} characters; it travels in a "
                    + $"{maxJoined}-character field, so name fewer movies");
        }

        return new CoopCommand(
            CoopCommandKind.StageMovie,
            Address: module,
            Password: joined,
            Amount: queueOnly ? StageMovieModes.QueueOnly : StageMovieModes.WarpWithMovies);
    }

    /// <summary>True for a resref-shaped token: 1..maxLength chars of ASCII letter/digit/underscore.</summary>
    private static bool IsResRef(string value, int maxLength)
    {
        if (value.Length < 1 || value.Length > maxLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Words a player uses to mean themselves. Recognised so a self-aimed
    /// <c>slay</c> gets the rule explained instead of "that is not a tag".</summary>
    private static bool IsSelfWord(string value) =>
        value.Equals("me", StringComparison.OrdinalIgnoreCase)
        || value.Equals("self", StringComparison.OrdinalIgnoreCase)
        || value.Equals("myself", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a token that could name a creature: a resref-shaped tag, or an object id
    /// written as <c>0x2A</c>, <c>#42</c> or <c>42</c>. Ids are accepted as text and resolved
    /// later — the parser's job is to reject a line that cannot possibly name anything.
    /// </summary>
    private static bool IsTargetToken(string value) => TryParseObjectId(value, out _) || IsResRef(value, maxLength: 31);

    /// <summary>
    /// Parses an object id in the three spellings a person types. Public because the agent
    /// resolves the same token against the world and must agree with the parser about what
    /// counts as an id.
    ///
    /// ⚠ ALL THREE SPELLINGS ARE HEX, and the two that did not used to be were a live
    /// footgun. Every id this project ever shows a person — engine log, plugin log, agent
    /// reply, `creatures.ps1` dump, this file's own error messages — is printed in hex, so
    /// the only thing anyone can type is a number they just read in hex. Measured on
    /// 2026-08-15: `slay #35` was typed to kill the disputed `0x35` and killed `0x23`
    /// instead, silently, because `#35` parsed as decimal 35. There is no source anywhere in
    /// this project for a DECIMAL object id, so the base that produced that accident had no
    /// user at all.
    ///
    /// Peer-list indices (`connect #2`, `goto #1`) are a different thing that happens to
    /// share the `#` character; they are parsed by their own callers and stay decimal, which
    /// is right — they are positions in a numbered list a person is reading, not ids.
    /// </summary>
    public static bool TryParseObjectId(string value, out uint objectId)
    {
        objectId = 0;
        if (value.Length == 0)
        {
            return false;
        }

        ReadOnlySpan<char> span = value.AsSpan();
        if (span[0] == '#')
        {
            span = span[1..];
        }

        bool marked = span.Length != value.Length;

        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
            marked = true;
        }

        if (span.IsEmpty)
        {
            return false;
        }

        // ⚠ A BARE token competes with TAGS, and plenty of real tags are valid hex —
        // `end_weld` is not, but `cad`, `beef` and `add` are, and reading one of those as a
        // number would target a creature nobody named. So a bare token is only an id when it
        // is all digits; anything with a letter has to say `0x` or `#` and mean it.
        if (!marked)
        {
            foreach (char c in span)
            {
                if (c is < '0' or > '9')
                {
                    return false;
                }
            }
        }

        return uint.TryParse(
            span, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out objectId);
    }

    /// <summary>
    /// Parses an ITEM object id, which — unlike a creature target — must be written in hex
    /// with an explicit marker: <c>0x131</c> or <c>#131</c>.
    ///
    /// The strictness is deliberate and it is about copy-paste. Every id these verbs deal in
    /// arrives from a log line that printed it as <c>0x131</c>, and a bare <c>131</c> read as
    /// decimal names object 0x83 — a real, different object that will usually resolve. A verb
    /// whose whole job is to write into an object must not have a spelling that silently aims
    /// at the wrong one.
    /// </summary>
    public static bool TryParseItemId(string value, out uint objectId)
    {
        objectId = 0;

        ReadOnlySpan<char> span = value.AsSpan();
        if (span.Length > 0 && span[0] == '#')
        {
            span = span[1..];
        }

        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }
        else if (value.Length == 0 || value[0] != '#')
        {
            // Neither marker: refuse rather than guess a base.
            return false;
        }

        return span.Length > 0
            && uint.TryParse(
                span, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out objectId)
            && objectId != 0;
    }

    /// <summary>
    /// Parses <c>setitem &lt;id&gt; [stack=N] [charges=N] [max=N] [upgrades=0xN]
    /// [identified=0|1]</c>.
    ///
    /// Named arguments rather than positional ones because "leave this field alone" has to be
    /// expressible: the verb's other job is to poke ONE field of an item and see it change,
    /// and a positional form would make every unnamed field a silent overwrite. A bare
    /// <c>setitem &lt;id&gt;</c> names nothing and is therefore a pure read.
    /// </summary>
    private static CoopCommand ParseSetItem(string[] rest)
    {
        if (rest.Length == 0 || !TryParseItemId(rest[0], out uint objectId))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "setitem needs an object id written as 0x131 or #131, then "
                    + "stack=N charges=N max=N upgrades=0xN identified=0|1");
        }

        ItemFields fields = default;

        for (int i = 1; i < rest.Length; i++)
        {
            string token = rest[i];
            int equals = token.IndexOf('=');
            if (equals <= 0 || equals == token.Length - 1)
            {
                return new CoopCommand(
                    CoopCommandKind.Invalid,
                    Error: $"'{token}' is not a field=value pair "
                        + "(stack, charges, max, upgrades, identified)");
            }

            string name = token[..equals].ToLowerInvariant();
            string value = token[(equals + 1)..];

            if (!TryParseFieldValue(value, out uint number))
            {
                return new CoopCommand(
                    CoopCommandKind.Invalid, Error: $"'{value}' is not a number (or 0x-number)");
            }

            switch (name)
            {
                case "stack" or "stacksize":
                    // Zero is refused HERE rather than clamped later: the engine never makes a
                    // stack of nothing, and an item with one is unpickable. A caller who typed
                    // it meant something else.
                    if (number is 0 or > ushort.MaxValue)
                    {
                        return new CoopCommand(
                            CoopCommandKind.Invalid,
                            Error: $"stack={number} is outside 1..{ushort.MaxValue}");
                    }

                    fields = fields with { StackSize = number };
                    break;

                case "charges":
                    if (number > byte.MaxValue)
                    {
                        return new CoopCommand(
                            CoopCommandKind.Invalid, Error: "charges is a byte (0..255)");
                    }

                    fields = fields with { Charges = number };
                    break;

                case "max" or "maxcharges":
                    if (number > byte.MaxValue)
                    {
                        return new CoopCommand(
                            CoopCommandKind.Invalid, Error: "max charges is a byte (0..255)");
                    }

                    fields = fields with { MaxCharges = number };
                    break;

                case "upgrades" or "upgrade":
                    fields = fields with { Upgrades = number };
                    break;

                case "identified" or "id":
                    fields = fields with { Identified = number != 0 };
                    break;

                default:
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"unknown field '{name}' "
                            + "(stack, charges, max, upgrades, identified)");
            }
        }

        return new CoopCommand(
            CoopCommandKind.SetItem, Address: $"#{objectId:x}", Item: fields);
    }

    /// <summary>
    /// Parses <c>giveloot &lt;owner id&gt; &lt;resref&gt; [stack]</c>. The id is the OWNER's
    /// enemy object id — the one every peer's census agrees on — not whatever local id this
    /// instance's own materialisation table spawned the stand-in under; only the receiving
    /// plugin can translate one to the other, which is why it takes the id rather than a name.
    /// </summary>
    private static CoopCommand ParseGiveLoot(string[] rest)
    {
        if (rest.Length < 2 || !TryParseItemId(rest[0], out uint ownerId)
            || !IsResRef(rest[1], maxLength: 31))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "giveloot needs the owner's enemy id and a resref, e.g. "
                    + "giveloot 0x35 g_w_blstrpstl001 [stack]");
        }

        int stack = 1;
        if (rest.Length > 2 && (!int.TryParse(rest[2], out stack) || stack <= 0))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"stack '{rest[2]}' is not a positive number");
        }

        return new CoopCommand(
            CoopCommandKind.GiveLoot, Address: $"#{ownerId:x}", Password: rest[1], Amount: stack);
    }

    /// <summary>The local player's own object id on every instance (docs/re/object-model.md:
    /// "Player object id is exactly 0x7FFFFFFF") — what <c>me</c>/<c>self</c> resolves to for
    /// <c>applyeffect</c>, so a player can smoke-test a constructor against their own
    /// character without knowing their own id.</summary>
    private const string LocalPlayerObjectIdToken = "0x7FFFFFFF";

    /// <summary>Duration an <c>applyeffect</c> line gets when it names none — long enough to
    /// actually be felt (a few seconds), not a measured figure. Mirrors the same fallback
    /// <c>PhantomEffectHook.FallbackDurationMs</c> uses on the capture side, for the same
    /// reason: most of these effects are meant to be felt, not applied as a permanent flag
    /// flip.</summary>
    private const uint DefaultApplyEffectDurationMs = 6000;

    /// <summary>
    /// Parses <c>applyeffect &lt;kind&gt; [target] [durationMs] [magnitude]</c> — the staging
    /// verb that runs a peer-effect constructor directly, through the same
    /// <c>OdysseyEffects.ApplyEffect</c> path the receiving side calls. Aimed at a peer's
    /// phantom (the default — a blank target) this IS the live test docs/research/effect-sync.md
    /// has never been able to run; aimed at an explicit id it is a constructor smoke test.
    /// </summary>
    private static CoopCommand ParseApplyEffect(string[] rest)
    {
        const string usage =
            "applyeffect <kind> [target] [durationMs] [magnitude]  (kind: stun, paralyze, "
            + "knockdown, slow, frightened, sleep, choke, forcepush, whirlwind; target: object "
            + "id, 'me', or blank for the nearest phantom)";

        if (rest.Length == 0 || rest.Length > 4)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        if (!Enum.TryParse(rest[0], ignoreCase: true, out PeerEffectKind kind) || kind == PeerEffectKind.None)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"'{rest[0]}' is not an effect kind - {usage}");
        }

        string? target = null;
        if (rest.Length > 1 && rest[1].Length > 0)
        {
            string raw = IsSelfWord(rest[1]) ? LocalPlayerObjectIdToken : rest[1];

            if (!TryParseObjectId(raw, out _))
            {
                return new CoopCommand(
                    CoopCommandKind.Invalid,
                    Error: $"'{rest[1]}' is not an object id (try 0x35, or 'me' for yourself)");
            }

            target = raw;
        }

        uint durationMs = DefaultApplyEffectDurationMs;
        if (rest.Length > 2 && !uint.TryParse(rest[2], out durationMs))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"'{rest[2]}' is not a duration in milliseconds");
        }

        uint magnitude = 0;
        if (rest.Length > 3 && !uint.TryParse(rest[3], out magnitude))
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: $"'{rest[3]}' is not a magnitude");
        }

        return new CoopCommand(
            CoopCommandKind.ApplyEffect,
            Address: target,
            Amount: (int)kind,
            EffectDurationMs: durationMs,
            EffectMagnitude: magnitude);
    }

    /// <summary>A field value: decimal, or hex with an explicit 0x. Unlike an object id a
    /// bare number here is unambiguous — a stack of 20 is 20 — so decimal is the default.</summary>
    private static bool TryParseFieldValue(string value, out uint number)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(
                value.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out number);
        }

        return uint.TryParse(value, out number);
    }

    /// <summary>Invariant-culture float parse: a console line is typed with a dot whatever
    /// the machine's locale says, and a comma-decimal locale would otherwise read "12.5" as
    /// 125.</summary>
    private static bool TryParseCoordinate(string value, out float result) =>
        float.TryParse(
            value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out result);

    /// <summary>One-line help listing the commands, for the overlay and dashboard.</summary>
    public const string HelpText =
        "commands: servers | connect <ip|#n> [password] | password <pw> | host | disconnect | reconnect | say <msg> | clear | status | warp <module> | who | where | census | resolve <id> | cheats | netsim <off|status|loss|latency|blackout> | help (see `cheats` for applyeffect/effects and the rest of the staging verbs)";

    /// <summary>
    /// The staging-cheat listing. Two halves on purpose: what co-op adds, and what the
    /// retail console already does correctly in co-op — because the second half is the part
    /// a player cannot discover on their own and the part we deliberately did not rewrite.
    /// </summary>
    public static readonly string[] CheatsText =
    [
        "co-op staging cheats (authority-aware):",
        "  god [on|off]        your damage floors at 1 hp; yours only",
        "  topup [n]           your hit points to n, or to full",
        "  standup [n]         stand yourself up out of the downed state",
        "  slay [tag|id]       kill an enemy; a client routes it to the host",
        "  destroyobject <id>  destroy a creature outright (Odyssey.DestroyObjectById);",
        "                      HOST-ONLY, no forward path - refused on a mirroring instance",
        "  tp <x> <y> [z]      move yourself inside this area",
        "  goto <player>       move yourself to a peer (same module)",
        "  dropitem <resref>   drop a blueprint; the item channel shares it",
        "  dropitem 0x131      drop a copy of a LIVE item, carrying its real state",
        "  items [n]           list live items here: id, resref, holder, stack/charges/upgrades",
        "  item 0x131          what one item's per-instance state actually reads",
        "  setitem 0x131 stack=20 charges=3 upgrades=0x5 identified=0",
        "                      poke those fields, so there is something worth sending",
        "  giveloot <owner id> <resref> [stack]   give a materialised stand-in its real loot",
        "  applyeffect <kind> [target] [durationMs] [magnitude]",
        "                      run a peer-effect constructor (stun/paralyze/knockdown/slow/",
        "                      frightened/sleep/choke/forcepush/whirlwind); blank target =",
        "                      nearest phantom. STAGING ONLY, LOCAL APPLY - what the hook",
        "                      forwards from a peer's phantom is the actual test",
        "  effects             last 8 captured/forwarded/applied peer effects, with ages",
        // Listed here because it is the same family — a verb that exists to stage a test —
        // and discoverability is the point of this listing. It is owned by the network
        // fault-injection work, which registers its own catalog entry and parser case; this
        // is only the line that tells a player it is there.
        "test harness:",
        "  netsim <off|status|loss <pct>|latency <ms> <jitter>|blackout <s> [in|out|both]>",
        // ⚠ Listed with the warning attached, because the thing it arranges is not "a video":
        // a movie parks the game's main thread for its whole length, and for those seconds
        // the co-op plugin is unreachable rather than idle.
        "  moviewarp <module> <movie...>   warp WITH a cinematic (the plugin stops for it)",
        "  moviewarp queue <movie...> | clear | show",
        // moviewarp only ever writes the TRANSITION queue - a different engine path entirely.
        // This is the one that fires MoviePlayHook / the mid-module movie relay on demand.
        "  movieplay <resref>              play a movie mid-module RIGHT NOW (blocks main",
        "                      thread); fires the mid-module movie relay for a connected peer",
        // The pair: queue the movie, then make the ENGINE start the transition. Only this
        // order opens the pre-store flush window a movie might swallow.
        "  usedoor [tag|id]                walk through a transition door (nearest by default)",
        "  opendoor [tag|id]               open an ORDINARY door (no transition; nearest by default)",
        "  useplaceable [tag|id]           use a container/corpse/switch (nearest container by default)",
        "  census [id]         what my enemy census holds, and what my own engine says of each id",
        "  resolve <id>        what the ENGINE knows about one id, in one frame (plugin log has the chain)",
        "retail console cheats, already co-op-safe (they touch only your game):",
        "  giveitem <resref> | givecredits <n> | addexp <n> | addlevel | heal",
        "  revealmap | infiniteuses | invulnerability | whereami | bright | turbo",
    ];
}
