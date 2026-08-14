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
        ("help", "help"),
        ("commands", "commands"),
        ("coop", "coop <command>"),
        ("warp", "warp <module> [waypoint]"),
        ("who", "who"),
        ("peers", "peers"),
        ("where", "where"),
        ("census", "census [id]  (what my enemy census holds, and what my engine says of it)"),
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
        ("dropitem", "dropitem <resref>"),
        ("cheats", "cheats"),

        // Completable, but NEVER recorded: ConsoleHistoryRing drops password/pw lines, so
        // completion may offer the verb while recall can never replay the secret.
        ("password", "password <pw>"),
        ("pw", "pw <pw>"),
    ];
}
