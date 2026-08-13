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
    ];
}
