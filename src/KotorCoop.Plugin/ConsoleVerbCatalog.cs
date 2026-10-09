namespace KotorCoop.Plugin;

/// <summary>
/// The co-op console verbs with their one-line usage strings, for Tab completion and the
/// hint line. Dependency-free so it compiles into the 64-bit test project.
///
/// ⚠ KEEP IN SYNC with <c>ConsoleCommand.Parse</c> and <c>FuncInterpHook.Verbs</c> — the
/// same demand those two already make of each other. A verb listed here but not parsed
/// would complete to a line the dispatcher rejects; a parsed verb missing here simply never
/// completes, which is the safer failure but still a gap. ConsoleCompletionEngineTests
/// asserts every entry here parses to a co-op kind, which catches half of the drift.
///
/// ("?" is parsed as help but deliberately not listed: cycling a completion onto a literal
/// question mark reads as a glitch, and nobody Tab-completes one character.)
/// </summary>
internal static class ConsoleVerbCatalog
{
    public static readonly (string Verb, string Usage)[] Entries =
    [
        ("connect", "connect <ip|#n> [password]"),
        ("join", "join <ip|#n> [password]"),
        ("host", "host [password]"),
        ("disconnect", "disconnect"),
        ("dc", "dc"),
        ("leave", "leave"),
        ("reconnect", "reconnect [world]  (world: reload the host's latest save with your own character)"),
        ("rc", "rc"),
        ("say", "say <message>"),
        ("chat", "chat <message>"),
        ("msg", "msg <message>"),
        ("clear", "clear"),
        ("cls", "cls"),
        ("servers", "servers"),
        ("browse", "browse"),
        ("list", "list"),
        ("status", "status"),
        ("stat", "stat"),
        ("lagstats", "lagstats  (interpolation delay, real send/recv intervals, agent pump cost)"),
        ("public_address", "public_address  (this host's public IP:port and UPnP verdict, for sharing)"),
        ("publicaddress", "publicaddress"),
        ("pubaddr", "pubaddr"),
        ("steamstatus", "steamstatus  (is Steam active, your SteamID64, and what a friend would join)"),
        ("steam_status", "steam_status"),
        ("steaminvite", "steaminvite <steamid64>  (invite one friend to the session you're hosting)"),
        ("steam_invite", "steam_invite <steamid64>"),
        ("help", "help"),
        ("commands", "commands"),
        ("coop", "coop <command>"),
        ("warp", "warp <module> [waypoint]"),
        ("who", "who"),
        ("peers", "peers"),
        ("where", "where"),
        ("census", "census [id]  (what my enemy census holds, and what my engine says of it)"),
        ("resolve", "resolve <id|party>  (what the engine knows about one id, or the CSWParty member array; chain in the plugin log)"),
        ("firescript", "firescript <id> [fire]  (peek or fire the +0x260 event script)"),
        ("runscript", "runscript <resref> [0xid]  (dev: run a named script with that object as its caller)"),
        ("netsim", "netsim <off|status|loss <pct>|latency <ms> [jitter]|blackout <secs> [in|out|both]>"),

        // ---- staging cheats ------------------------------------------------------
        //
        // Every one of these is a name the retail console does NOT already use (its list is
        // heal, addexp, addlevel, giveitem, givecredits, revealmap, invulnerability, warp,
        // whereami, infiniteuses, bright, turbo, the set* stat verbs and the give* supply
        // verbs — read out of the binary, see docs/research/console-cheats.md). Shadowing a
        // retail verb would take a working cheat away from the player to replace it with a
        // co-op one they did not ask for; `warp` is the single deliberate exception and it
        // predates this list.
        ("god", "god [on|off]"),
        ("topup", "topup [hitpoints]"),
        ("standup", "standup [hitpoints]"),
        ("getup", "getup [hitpoints]"),
        ("slay", "slay [tag|id]  (blank = nearest hostile)"),
        ("tp", "tp <x> <y> [z]"),
        ("goto", "goto <player|#n>"),
        ("autofollow", "autofollow [player|#n|off|status]  (blank = the host)"),
        ("drop", "drop <0xid|resref> [count]  (one of YOUR backpack items, for your partner)"),
        ("dropitem", "dropitem <resref|0x131>"),
        ("cheats", "cheats"),

        // The staging instrument, spelled in full and with no alias. Completion offering it
        // is fine — discoverability is this list's job — but nothing about it is short, and
        // the usage says what it costs.
        ("moviewarp", "moviewarp <module> <movie...> | queue <movie...> | clear | show  (the plugin stops for the movie)"),

        // The engine-initiated half: only a door transition opens the pre-store flush window
        // a queued movie might swallow, so this and `moviewarp queue` are used together.
        ("usedoor", "usedoor [tag|id]  (blank = nearest module transition door)"),

        // crossdoor (2026-09-03): same target resolution as usedoor, but posts the door's own
        // crossing event directly instead of queuing a walk - usedoor's walk has stalled short
        // of the door on every live attempt (docs/research/cinematics.md §8.3b). Does not
        // shadow a retail verb (checked the same way every staging cheat above was).
        ("crossdoor", "crossdoor [tag|id]  (blank = nearest module transition door; posts the crossing event, no walk)"),
        ("opendoor", "opendoor [tag|id]  (blank = nearest door; unlike usedoor, no transition needed)"),
        ("closedoor", "closedoor [tag|id]  (dev: close a door through the engine; blank = nearest door)"),
        ("setlock", "setlock <id> on|off  (dev: change a real door/container lock)"),
        ("unlockdoor", "unlockdoor <id>  (use the Security skill on a locked door, as the target menu does)"),

        // The container-sync live-test primitive: use a chest/corpse/switch through the
        // engine's own use-object input, exactly like a real click. See
        // docs/research/container-sync.md.
        ("useplaceable", "useplaceable [tag|id]  (blank = nearest container placeable)"),
        // The close-side counterpart, added when the live-verify pass found no way to close
        // what useplaceable opened — see docs/research/container-sync.md.
        ("closeplaceable", "closeplaceable [tag|id]  (blank = nearest container placeable)"),
        // The item-identity harness. `items` finds a target, `setitem` makes it worth
        // sending, `dropitem 0x..` sends it, `item` reads back what arrived — the whole loop
        // is these four, and none of them shadows a retail verb (the engine's list has
        // giveitem and nothing else in this family; docs/research/console-cheats.md §2).
        ("items", "items [n]"),
        ("item", "item <0x131>"),
        ("setitem", "setitem <0x131> [stack=N] [charges=N] [max=N] [upgrades=0xN] [identified=0|1]"),
        ("giveloot", "giveloot <owner id> <resref> [stack]"),

        // The peer-effect staging pair. Neither shadows a retail verb.
        ("applyeffect", "applyeffect <kind> [target] [durationMs] [magnitude]"),
        ("effects", "effects"),

        // The mid-module movie relay's test hook: fires MoviePlayHook directly, without a
        // story trigger. `moviewarp` only ever writes the TRANSITION queue, a different path.
        ("movieplay", "movieplay <resref>  (plays it NOW; blocks the main thread)"),

        // Dev-only control-switch test primitive; local only, never crosses the network.
        ("leader", "leader <slot|pc>  (switch YOUR control to a party-roster companion 0-8, or back to pc)"),

        // Completable, but NEVER recorded: ConsoleHistoryRing drops password/pw lines, so
        // completion may offer the verb while recall can never replay the secret.
        ("password", "password <pw>"),
        ("pw", "pw <pw>"),

        // Three live-test primitives (2026-08-27): none shadows a retail verb.
        ("setglobal", "setglobal <name> <int|true|false|on|off>  (writes a script global by name)"),
        ("localvar", "localvar <get|set|watch> <tag|0xid> <b|n> <slot> [value]  (dev-only: one object's local slot; watch = session-only local_var_sync test)"),
        ("pazaak", "pazaak [npc-tag|id] [deck]  (starts a Pazaak hand; blank = engine's own default opponent)"),
        ("openstore", "openstore <resref|tag|id>  (opens the store GUI against a resolved store)"),

        // Two more live-test primitives (2026-08-28): none shadows a retail verb.
        ("buy", "buy <store-tag|id> <resref> [count]  (the store's own GUI must already be open)"),
        ("sell", "sell <store-tag|id> <resref> [count]  (the store's own GUI must already be open)"),

        // A third live-test primitive (2026-08-28): does not shadow a retail verb.
        ("startdialog", "startdialog <npc-tag|id> [dlg-resref]  (starts a conversation with the local player)"),

        // Dev-only, script-agnostic solo-segment detector test primitive (2026-08-28): calls the
        // real RemovePartyMember(nNPC) directly, no dialogue needed. Does not shadow a retail verb.
        ("removeparty", "removeparty <slot>  (dev-only: calls the real RemovePartyMember on roster slot 0-8)"),

        // A fifth live-test primitive (2026-08-28, docs/research/store-sync.md's live pass):
        // registered the same way. Does not shadow a retail verb.
        ("setgold", "setgold <amount>  (dev-only: sets party gold directly via CSWSCreature::SetGold; repairs a poisoned save)"),

        // The class-grant relay's live-test primitive (2026-09-18,
        // docs/research/class-and-power-grants.md): the same AddMultiClass VM call Dorak's
        // script makes, against the local player, captured and relayed like the real thing.
        ("addclass", "addclass <class 0-8>  (dev-only: runs the engine's AddMultiClass on your player; 3 Guardian, 4 Consular, 5 Sentinel)"),

        // Party-placed mine replication's live-test verbs (2026-09-24, docs/research/mines.md §14).
        ("placemine", "placemine <traps.2da row> [detectDC] [disarmDC]  (dev-only: builds a party mine at your feet, captured like a real one)"),
        ("minestate", "minestate  (dev-only: party-placed mine serials, local ids and the fire gate)"),
        ("placedmineaction", "placedmineaction <recover|disarm> <serial>  (dev-only: retail recover/disarm on a replicated party mine)"),

        // The character-creation drive verbs (2026-08-29). Only meaningful at the MAIN MENU,
        // which is exactly where the console detour still works and the frame hook does not.
        ("newgame", "newgame  (opens the game's own character-creation screens from the main menu)"),
        ("chargen", "chargen quick [class 0-5] | chargen accept  (picks a class + Quick template, or presses Play)"),
        ("gamepad", "gamepad [status|on|off|cursor|sim <lx= ly= rx= ry= lt= rt= a b x y lb rb start back ls rs up down left right>|sim off]"),
        ("firetrigger", "firetrigger <tag|0xid>  (fire a trigger's OnEnter for you, as if you walked in; dev)"),
        ("stealth", "stealth on|off  (the local player's engine stealth mode; a live-test primitive)"),
        ("input", "input click <x> <y> [left|right] | input move <x> <y> | input delta <dx> <dy> | input key <dik hex/dec> [down|up|tap] | input status  (isolated-rig synthetic mouse/keyboard, no real cursor movement)"),
        ("screenshot", "screenshot <name> [front] | screenshot status  (plugin-side GL screenshot for the isolated rig; writes <gamedir>\\kotorcoop-shots\\<name>.bmp - PrintWindow is unreliable for this OpenGL game)"),

        // The live-cast primitive (2026-09-24, docs/research/effect-sync.md §14): a real
        // player cast through the engine's own action queue, so a capture hook that needs the
        // effect's creator to be the local player can finally be exercised.
        ("cast", "cast <spell id|name> <target: me|0xid> [metamagic]  (dev-only: real cast through the engine's action queue; names: heal, cure, valor, stun, fear, forcepush, stasis)"),
        ("grantpower", "grantpower <spell id|name> [classSlot]  (dev-only: engine's own AddKnownSpell, for a save that doesn't know the power yet)"),

        // The minigame-kill primitive (2026-09-25, docs/research/minigames.md): finishes a
        // forced turret/gunnery ambush (e.g. the m12ab Ebon Hawk Sith-fighter sequence) the
        // unattended rig cannot play by actually shooting, through the engine's own
        // SWMG_AdjustFollowerHitPoints VM routine so each fighter's real OnDeath script fires.
        ("mgkill", "mgkill <all|n>  (dev-only: kills up to n - or all - alive minigame enemy fighters via the engine's own SWMG_AdjustFollowerHitPoints; repeat per wave)"),
        // The make-a-real-Jedi primitive (2026-09-25, docs/research/class-and-power-grants.md
        // §9): closes the "no Force points to cast with" dead end effect-sync.md §14.7 hit.
        ("levelup", "levelup <classSlot 0|1 | classes.2da id> [count]  (dev-only: engine's own AutoLevelup on the LAST class slot; see 'addclass'/'stats')"),
        ("stats", "stats  (dev-only: read-only class levels, hp/fp, known powers of your own player)"),

        // The `attack` live-test primitive (2026-09-25, docs/research/combat-participation.md
        // §16): a real queued ActionAttack for the local player, the same route a click on a
        // hostile produces, so the sticky-aggro test and a live `cast` (which needs the
        // caster's own active combat round) can be exercised on a rig that cannot click.
        ("attack", "attack <0xid> | attack ranged <0xid> (native attack without range staging) | attack stop (cancel combat)"),

        // The showgui equip primitive (2026-09-25, docs/research/companion-state-sync.md's
        // dated follow-up to §14): opens THIS instance's own Equipment screen through the
        // retail ShowSWInGameGui/SetCharacter pair, so CompanionEquipSuppressionController's
        // own close can finally be exercised on a rig that cannot press a key or click an arrow.
        ("showgui", "showgui equip [partyIndex]  (dev-only: opens this instance's own Equipment screen via the retail ShowSWInGameGui/SetCharacter pair; 0 = your PC, 1-2 = active companions)"),

        // jedichoice - docs/research/class-and-power-grants.md's 2026-09-25 section: forces the
        // local class-choice popup check, exactly as a peer's real Jedi choice would trigger it.
        ("jedichoice", "jedichoice  (dev-only: manufactures a local 'a peer's Jedi choice happened' request, so the class-choice popup can be exercised without a second player or the Dantooine conversation)"),

        // Duels - docs/research/pvp-plan.md. Plain words a player types in answer to a challenge. None
        // shadows a retail console verb (the engine registers no accept/decline/yield/duel).
        ("duel", "duel <name>  (challenge a player; they type accept or decline); duel off / duel on  (refuse / allow challenges)"),
        ("arena", "arena <name>  (challenge a player to a duel in the arena; the host takes everyone there and back)"),
        ("accept", "accept  (say yes to a duel challenge)"),
        ("decline", "decline  (say no to a duel challenge)"),
        ("yield", "yield  (give up a duel)"),
    ];
}
