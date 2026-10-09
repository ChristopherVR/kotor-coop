namespace KotorCoop.Session;

/// <summary>
/// Pure parse for the `spawncreature <template> [count] [x y [z]]` (alias `spawn`) dev-console
/// verb (docs/research/creature-liveness.md's 2026-09-03 checklist item 6: "a
/// `spawncreature`-style debug verb, following `Odyssey.SpawnCreature`'s existing call path";
/// also used for staging combat rounds, e.g. docs/research/peer-damage-ledger.md §9.5). Spawns
/// `count` hostile creatures from a .utc template resref through the engine's own CreateObject
/// path (<c>Odyssey.SpawnCreature</c>, <c>KotorCoop.Plugin.CheatController.SpawnCreature</c>) —
/// the same call <c>MaterialisedEnemies</c>/<c>PhantomController</c> already make for their own
/// stand-ins, just reachable from a console line now.
///
/// Pulled out as a standalone pure rule for the same reason <see cref="GalaxyMapVerbRule"/> was:
/// there is real decision logic here (a resref shape check, a count range, and an optional
/// coordinate pair/triple) worth unit-testing independent of <c>ConsoleCommand.Parse</c>'s
/// tokenising/dispatch plumbing.
///
/// ⚠ GRAMMAR CHOICE: <c>count</c> must be given whenever coordinates are, and the FIRST token
/// after the template is always read as count when one is present — never as a coordinate. This
/// keeps the grammar deterministic rather than ambiguous: three bare numbers after the template
/// could otherwise mean either "count x y" or "x y z" with no count, and
/// `spawn g_vulkar001 12.5 -30.0 5.0` would need a second guess to tell them apart. So this rule
/// only ever accepts 0, 1, 3 or 4 tokens after the template (never 2 or 5+) — count on its own,
/// or count-then-position — and a caller who wants an explicit position always spells the count
/// too: `spawn g_vulkar001 1 12.5 -30.0` for one creature at an explicit spot. A trailing-token
/// count that is not a valid integer (e.g. `12.5`) is rejected as a bad count, not silently
/// re-read as a coordinate.
/// </summary>
public static class SpawnCreatureVerbRule
{
    /// <summary>The lowest accepted spawn count.</summary>
    public const int MinCount = 1;

    /// <summary>The highest accepted spawn count — enough to stress
    /// <c>tools/liveness-freshness-probe.ps1</c> well past this game's own reachable content
    /// ceiling (docs/research/creature-liveness.md found ~33 as the natural maximum), without
    /// handing a mistyped extra zero a licence to spawn thousands.</summary>
    public const int MaxCount = 32;

    /// <summary>The longest a .utc template resref can be — the Aurora engine's own resref
    /// limit, matching <c>ParseStartDialog</c>'s dialog-resref check elsewhere in this file
    /// family.</summary>
    public const int MaxTemplateLength = 16;

    public const string Usage = "spawncreature <template> [count] [x y [z]]  "
        + "(e.g. spawncreature g_vulkar001, spawncreature g_vulkar001 5, or "
        + "spawncreature g_vulkar001 5 12.5 -30.0 for an explicit position - count must be given "
        + "whenever coordinates are, e.g. 'spawncreature g_vulkar001 1 12.5 -30.0' for one)";

    /// <summary>The parsed request: which template, how many, and where — mirroring
    /// <c>MoveNearDestinationRule.Resolve</c>'s own convention that <see cref="IsExplicitPosition"/>
    /// false means "compute the default (near the player)" on the plugin side, not "spawn at the
    /// world origin".</summary>
    public readonly record struct Result(
        string Template, int Count, float X, float Y, float Z, bool IsExplicitPosition);

    /// <param name="args">The tokens after `spawncreature`/`spawn` (already split by
    /// <c>ConsoleCommand.Parse</c>, no leading/trailing whitespace on any token).</param>
    public static bool TryParse(string[] args, out Result result, out string error)
    {
        result = default;
        error = Usage;

        if (args.Length == 0)
        {
            return false;
        }

        string template = args[0];

        // `@template` spawns the way a script does (through the routine table), to exercise the
        // host-spawns-only hook; the marker rides through to the plugin untouched.
        if (!IsTemplateResRef(template.StartsWith('@') ? template[1..] : template))
        {
            error = $"'{template}' is not a valid template resref "
                + $"(1-{MaxTemplateLength} ASCII letters/digits/underscore) - {Usage}";
            return false;
        }

        if (args.Length is not (1 or 2 or 4 or 5))
        {
            error = Usage;
            return false;
        }

        int count = MinCount;
        if (args.Length >= 2)
        {
            if (!int.TryParse(args[1], out count) || count < MinCount || count > MaxCount)
            {
                error = $"'{args[1]}' is not a spawn count ({MinCount}-{MaxCount}) - {Usage}";
                return false;
            }
        }

        float x = 0f;
        float y = 0f;
        float z = 0f;
        bool explicitPosition = false;

        if (args.Length >= 4)
        {
            if (!TryParseCoordinate(args[2], out x) || !TryParseCoordinate(args[3], out y))
            {
                error = $"'{args[2]} {args[3]}' is not a coordinate pair - {Usage}";
                return false;
            }

            if (args.Length == 5 && !TryParseCoordinate(args[4], out z))
            {
                error = $"'{args[4]}' is not a height - {Usage}";
                return false;
            }

            explicitPosition = true;
        }

        result = new Result(template, count, x, y, z, explicitPosition);
        error = string.Empty;
        return true;
    }

    /// <summary>True for a resref-shaped token: 1..<see cref="MaxTemplateLength"/> chars of
    /// ASCII letter/digit/underscore — deliberately self-contained rather than reusing
    /// <c>ConsoleCommand.IsResRef</c> (private to that partial class family), since this rule is
    /// meant to be testable with no dependency on the dispatcher's own internals.</summary>
    private static bool IsTemplateResRef(string value)
    {
        if (value.Length < 1 || value.Length > MaxTemplateLength)
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

    /// <summary>Invariant-culture float parse — same reasoning as
    /// <c>ConsoleCommand.TryParseCoordinate</c>: a console line is typed with a dot whatever the
    /// machine's locale says.</summary>
    private static bool TryParseCoordinate(string value, out float result) =>
        float.TryParse(
            value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out result);
}
