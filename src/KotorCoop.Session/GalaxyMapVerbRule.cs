namespace KotorCoop.Session;

/// <summary>
/// Pure parse for the `galaxymap open|close|stats|select &lt;n&gt;` dev-console verb (Tier 2
/// item 18 task 2's own "Live test plan" follow-up, docs/research/galaxy-map-and-party-select.md's
/// 2026-09-03 addendum): recognises exactly the subcommands the plugin actually implements
/// (<c>SharedBlock.CheatKindGalaxyMapOpen</c>/<c>Close</c>/<c>Stats</c>/<c>Select</c>,
/// <c>KotorCoop.Plugin.CheatController.GalaxyMap.cs</c>) and refuses anything else.
///
/// Mirrors the shape <c>ConsoleCommand.Cheats.cs</c>'s own <c>ParsePartySelect</c> already uses
/// for its single fixed subcommand ("ok"), but pulled out as a standalone pure rule because this
/// verb has several, and the task asked for a testable rule wherever there is decision logic —
/// unlike <c>partyselect</c>'s inline one-liner, a multi-way switch is worth its own unit tests
/// independent of <c>ConsoleCommand.Parse</c>'s tokenising/dispatch plumbing.
///
/// A `galaxymap confirm` subcommand — driving the accept-button CASE HANDLER directly, the way
/// `partyselect ok` drives <c>ConfirmPartySelection</c> — was investigated in the 2026-09-03 pass
/// that added <c>select</c>/<c>stats</c> and deliberately NOT added: the case handler
/// (<c>0x006959D0</c>) is a jump-table TARGET, not a callable function, so calling it directly
/// would corrupt the stack. The follow-up `galaxyaccept` VERB (a separate, TOP-LEVEL console
/// verb — see <c>ConsoleCommand.cs</c>'s own <c>GalaxyAccept</c> kind, NOT a `galaxymap`
/// subcommand) sidesteps that by calling <c>HandleInputEvent</c> ITSELF instead — a genuine
/// callable function, the same shape `skipkey` already uses for the dialogue panel — with the
/// raw GUI event code that same investigation's own byte-lookup-table decode already pinned to
/// the accept case (<c>0x27</c>, independently re-derived this pass). See
/// <c>KotorCoop.Plugin.Odyssey.GalaxyMapAccept.cs</c> and the research doc's own `galaxyaccept`
/// addendum for the full derivation and the live NEEDS-LIVE caveats.
/// </summary>
public static class GalaxyMapVerbRule
{
    /// <summary>The subcommands this verb recognises, or <see cref="Invalid"/> for anything
    /// else (including no subcommand, or a malformed one).</summary>
    public enum Verb
    {
        Invalid,
        Open,
        Close,
        Stats,

        /// <summary>`galaxymap select &lt;n&gt;` — see the two-argument <c>Parse</c> overload's
        /// own <c>planet</c> out parameter for the parsed index.</summary>
        Select,
    }

    public const string Usage = "usage: galaxymap open|close|stats|select <planet 0-15>";

    /// <param name="args">The tokens after `galaxymap` (already split by
    /// <c>ConsoleCommand.Parse</c>, no leading/trailing whitespace on any token).</param>
    /// <remarks>Convenience overload for callers (and the existing test suite) that do not need
    /// <see cref="Verb.Select"/>'s planet index — delegates to the two-argument overload and
    /// discards it.</remarks>
    public static Verb Parse(string[] args) => Parse(args, out _);

    /// <param name="args">The tokens after `galaxymap`, as above.</param>
    /// <param name="planet">The parsed planet index (0-15) when the result is
    /// <see cref="Verb.Select"/>; -1 otherwise, including every non-<see cref="Verb.Select"/>
    /// result — never meaningful unless the return value says so.</param>
    public static Verb Parse(string[] args, out int planet)
    {
        planet = -1;

        if (args.Length == 1)
        {
            return args[0].ToLowerInvariant() switch
            {
                "open" => Verb.Open,
                "close" => Verb.Close,
                "stats" => Verb.Stats,
                _ => Verb.Invalid,
            };
        }

        if (args.Length == 2
            && string.Equals(args[0], "select", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[1], out int n)
            && n is >= 0 and <= 15)
        {
            planet = n;
            return Verb.Select;
        }

        return Verb.Invalid;
    }
}
