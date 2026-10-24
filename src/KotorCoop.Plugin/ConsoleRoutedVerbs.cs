namespace KotorCoop.Plugin;

/// <summary>
/// The first words <see cref="FuncInterpHook"/> takes from the retail console and hands to the
/// co-op layer; any other line goes to the engine's own FuncInterp. Kept in sync with
/// <c>ConsoleCommand.Parse</c> and, for completion, <c>ConsoleVerbCatalog</c>. Dependency-free so
/// it compiles into the 64-bit test project, which checks the catalog against it.
/// </summary>
internal static class ConsoleRoutedVerbs
{
    public static readonly string[] All =
    [
        "connect", "join", "host", "disconnect", "dc", "leave", "reconnect", "rc",
        "say", "chat", "msg", "clear", "cls", "servers", "browse", "list", "status", "help", "coop",
        "password", "pw",
        "stat", "commands", "?", "warp", "who", "peers", "where", "lagstats",

        // Roadmap item 24 ("hosting over the internet"): prints a hosting player's public
        // IP:port and UPnP verdict for sharing with a joiner. Not a retail verb.
        "public_address", "publicaddress", "pubaddr",

        // The controller layer's verb (docs/research/gamepad.md §5). Not a retail verb.
        "gamepad",

        // The isolated rig's synthetic mouse/keyboard (docs/research/isolated-rig.md). Not a
        // retail verb.
        "input",
        "stealth",
        "runscript",
        "firetrigger",

        // The isolated rig's plugin-side screenshot (docs/research/isolated-rig.md):
        // PrintWindow is unreliable for this OpenGL game. Not a retail verb.
        "screenshot",

        // A diagnostic, not a cheat: prints what this instance's enemy census holds and what
        // its own engine says about each id. It shadows nothing the retail console owns.
        "census", "resolve", "firescript",

        // "netsim" only, not its "net" alias: a three-letter word is far too likely to shadow
        // something the engine's own console owns, and `coop net ...` already routes through
        // the "coop" prefix for anyone who wants the short form.
        "netsim",

        // Staging cheats. None of these shadows a retail console verb — checked against the
        // engine's own registered names (docs/research/console-cheats.md) — so intercepting
        // them takes nothing away from the player.
        "god", "topup", "standup", "getup", "slay", "tp", "goto", "autofollow", "drop", "dropitem", "cheats",

        // The cinematic staging instrument. Full spelling only, for the same reason "netsim"
        // has no "net" alias here: a verb that arranges a multi-second window in which this
        // plugin does not run is not something to reach by a near-miss.
        "moviewarp", "usedoor", "opendoor",

        // crossdoor (2026-09-03, docs/research/cinematics.md's dated addendum): posts the SAME
        // door-crossing event usedoor's own walk relies on the pathfinder to trigger, directly
        // through CServerAIMaster::AddEventDeltaTime, with no walk at all. Does not shadow a
        // retail verb — checked against docs/research/console-cheats.md's table the same way
        // every other staging verb above was (ConsoleCompletionEngineTests.
        // NoCoopVerbShadowsARetailConsoleVerb also asserts it at build time).
        "crossdoor",

        // The container-sync live-test primitive (docs/research/container-sync.md): not a
        // retail verb (checked against docs/research/console-cheats.md's table the same way
        // every other staging verb above was).
        "useplaceable",
        // The close-side counterpart, added when the live-verify pass found no way to close
        // what useplaceable opened — see docs/research/container-sync.md.
        "closeplaceable",
        // The item-identity harness. "item"/"items"/"setitem" are ours: the engine registers
        // giveitem and nothing else beginning with "item".
        "items", "item", "setitem", "giveloot",

        // The peer-effect staging pair (docs/research/effect-sync.md).
        "applyeffect", "effects",

        // The mid-module movie relay's test hook (docs/research/cinematics.md §10.7):
        // fires MoviePlayHook directly, so a live pass does not need to wait for a story
        // trigger. `moviewarp` above only ever writes the TRANSITION queue, a different path.
        "movieplay",

        // Dev-only control-switch test primitive (docs/research/control-return.md): switches
        // the local player's controlled character. Local only, never crosses the network; not
        // a retail verb.
        "leader",

        // NOTE: "quittomenu" (docs/research/disconnect-reconnect.md §9) is deliberately NOT
        // added here, matching "skipline"/"selectreply"/"loadgame"/"walkto"/"recruit" above —
        // dev-only LIVE-TEST PRIMITIVES are reachable only via tools/blockpoke.ps1's direct
        // shared-block write, never by a real player typing at the in-game console.

        // Three more live-test primitives (2026-08-27), registered here (unlike the block
        // above) so a rig session can also type them directly at the in-game console, not only
        // via blockpoke: setglobal (docs/research/quest-sync.md), pazaak
        // (docs/research/minigames.md) and openstore (docs/research/store-sync.md). None
        // shadows a retail verb — checked the same way every other staging cheat above was.
        "setglobal", "pazaak", "openstore",

        // Two more live-test primitives (2026-08-28, docs/research/store-sync.md's 2026-08-28
        // addendum): registered here, same as setglobal/pazaak/openstore above, so a rig
        // session can type them directly at the in-game console. None shadows a retail verb.
        "buy", "sell",

        // A third live-test primitive (2026-08-28, docs/research/dialogue-sync.md's 2026-08-28
        // addendum), registered the same way. Does not shadow a retail verb.
        "startdialog",

        // Duels (docs/research/pvp-plan.md): the words a player types to challenge and answer.
        // None shadows a retail console verb.
        "duel", "arena", "accept", "decline", "yield", "sentinel", "chungus",

        // A fourth live-test primitive (2026-08-28, docs/research/star-forge-finale.md §11):
        // registered here, unlike "recruit"/"skipline"/etc above, because the live plan for the
        // solo-segment detector's script-agnostic trigger needs it typed directly at the
        // in-game console (`removeparty 0` on the host), not only via blockpoke. Does not
        // shadow a retail verb.
        "removeparty",

        // The character-creation drive verbs (2026-08-29, docs/research/world-adoption.md's
        // join_mode section). Registered here, unlike the blockpoke-only primitives above, for
        // one reason a rig cannot work around: they are only ever meaningful AT THE MAIN MENU,
        // and this detour is the one console surface that works there (see this file's own
        // header). None shadows a retail verb — retail's own new-game entry is the menu button,
        // and there is no `chargen` interpreter function.
        "newgame", "chargen",

        // A fifth live-test primitive (2026-08-28, docs/research/store-sync.md's live pass):
        // registered here so a rig session can repair a poisoned gold value directly at the
        // in-game console. Does not shadow a retail verb (retail's own credits verb is
        // `givecredits`).
        "setgold",

        // The class-grant relay's live-test primitive (2026-09-18,
        // docs/research/class-and-power-grants.md): simulates Dorak's AddMultiClass on the
        // local player so the capture -> relay -> peer apply chain can be exercised without
        // playing Dantooine. Does not shadow a retail verb.
        "addclass",

        // Party-placed mine replication's live-test verbs (2026-09-24, docs/research/mines.md
        // §14.6), typed on either instance during the two-instance recipe. None shadows a retail
        // verb (docs/research/console-cheats.md lists no mine verb).
        "placemine", "minestate", "placedmineaction",

        // BUG FIX (2026-08-29, live-verification session): `pooladd`/`poolremove`
        // (docs/research/party-pool-sync.md, added to ConsoleCommand.cs in fcdab50) were never
        // added here, so every real in-game keystroke submission of either fell through to
        // `_original` — the retail engine's own FuncInterp, which has never heard of them —
        // instead of reaching CoopCommandDispatcher at all. `docs/CONSOLE.md` lists both as
        // ordinary (non-blockpoke-only) staging cheats, and this round's own live pass (typed
        // keystrokes via tools/sendkey.ps1, not blockpoke.ps1) reproduced the silent failure
        // directly: correct text on the input line, a real submit (the console's own open flag
        // cleared), zero log output — `IsCoopCommand` returning false is the exact, sufficient
        // explanation. Does not shadow a retail verb (checked against
        // docs/research/console-cheats.md the same way every prior addition here was).
        "pooladd", "poolremove",

        // The local_var_sync live-test primitive (2026-09-24, docs/research/quest-sync.md §8.3):
        // reads/writes one per-object local slot and adds a session-only watch, typed directly
        // at the in-game console on both rig instances. Does not shadow a retail verb (the
        // string "localvar" does not occur anywhere in the GOG swkotor.exe image).
        "localvar",

        // The live-cast primitive (2026-09-24, docs/research/effect-sync.md §14): typed on
        // both rig instances during the partner_buffs/enemy_effect_sync recipes, because those
        // hooks need a REAL cast's creator id, which only an actual in-game cast produces.
        // Does not shadow a retail verb — neither name appears in
        // docs/research/console-cheats.md's retail table, checked the same way every prior
        // addition here was (ConsoleCompletionEngineTests.NoCoopVerbShadowsARetailConsoleVerb
        // also asserts it at build time).
        "cast", "grantpower",

        // The minigame-kill primitive (2026-09-25, docs/research/minigames.md): typed on the
        // host rig instance to finish a forced turret/gunnery ambush the unattended rig cannot
        // play by actually shooting (e.g. the m12ab Ebon Hawk Sith-fighter sequence). Does not
        // shadow a retail verb (ConsoleCompletionEngineTests.NoCoopVerbShadowsARetailConsoleVerb
        // asserts it at build time, same as every prior addition here).
        "mgkill",
        // The make-a-real-Jedi primitive (2026-09-25, docs/research/class-and-power-
        // grants.md §9): typed on the rig to give a level-1 test character actual Force
        // points before `cast`/`grantpower` can be exercised. Does not shadow a retail verb
        // (checked the same way every prior addition here was).
        "levelup", "stats",

        // The `attack` live-test primitive (2026-09-25, docs/research/combat-participation.md
        // §16): a real queued ActionAttack for the local player, so the sticky-aggro test and a
        // live `cast` (which needs the caster's own active combat round) can be exercised on a
        // rig that cannot click. Does not shadow a retail verb (checked the same way every
        // prior addition here was; ConsoleCompletionEngineTests.NoCoopVerbShadowsARetailConsoleVerb
        // asserts it at build time).
        "attack",

        // The showgui equip live-test primitive (2026-09-25, docs/research/companion-state-
        // sync.md's dated follow-up to §14): opens this instance's own Equipment screen through
        // the retail ShowSWInGameGui/SetCharacter pair, so CompanionEquipSuppressionController's
        // own close can be exercised without a key press or a portrait-arrow click. Does not
        // shadow a retail verb (checked the same way every prior addition here was;
        // ConsoleCompletionEngineTests.NoCoopVerbShadowsARetailConsoleVerb asserts it at build
        // time).
        "showgui",
        // BUG FIX (2026-10-24, internal-test build): the Steam verbs and four dev verbs were in
        // ConsoleVerbCatalog (so they Tab-completed) and parsed in ConsoleCommand, but were never
        // listed here, so a typed `steam_status` fell through to the retail FuncInterp and printed
        // "function not found" - the same failure as the pooladd/poolremove fix above.
        // ConsoleCompletionEngineTests.EveryCompletedVerbIsRoutedToTheAgent now catches that drift.
        "steamstatus", "steam_status", "steaminvite", "steam_invite",
        "closedoor", "setlock", "unlockdoor", "jedichoice",
    ];
}
