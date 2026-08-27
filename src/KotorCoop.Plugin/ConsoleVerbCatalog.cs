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
        ("reconnect", "reconnect"),
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
        ("help", "help"),
        ("commands", "commands"),
        ("coop", "coop <command>"),
        ("warp", "warp <module> [waypoint]"),
        ("who", "who"),
        ("peers", "peers"),
        ("where", "where"),
        ("census", "census [id]  (what my enemy census holds, and what my engine says of it)"),
        ("resolve", "resolve <id>  (what the engine knows about one id; chain in the plugin log)"),
        ("firescript", "firescript <id> [fire]  (peek or fire the +0x260 event script)"),
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
        ("dropitem", "dropitem <resref|0x131>"),
        ("cheats", "cheats"),

        // The staging instrument, spelled in full and with no alias. Completion offering it
        // is fine — discoverability is this list's job — but nothing about it is short, and
        // the usage says what it costs.
        ("moviewarp", "moviewarp <module> <movie...> | queue <movie...> | clear | show  (the plugin stops for the movie)"),

        // The engine-initiated half: only a door transition opens the pre-store flush window
        // a queued movie might swallow, so this and `moviewarp queue` are used together.
        ("usedoor", "usedoor [tag|id]  (blank = nearest module transition door)"),
        ("opendoor", "opendoor [tag|id]  (blank = nearest door; unlike usedoor, no transition needed)"),

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
    ];
}
