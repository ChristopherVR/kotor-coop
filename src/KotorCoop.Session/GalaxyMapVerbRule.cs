namespace KotorCoop.Session;

/// <summary>
/// Pure parse for the `galaxymap open|close|stats` dev-console verb (Tier 2 item 18 task 2's
/// own "Live test plan" follow-up, docs/research/galaxy-map-and-party-select.md's 2026-09-03
/// addendum): recognises exactly the three subcommands the plugin actually implements
/// (<c>SharedBlock.CheatKindGalaxyMapOpen</c>/<c>Close</c>/<c>Stats</c>,
/// <c>KotorCoop.Plugin.CheatController.GalaxyMap.cs</c>) and refuses anything else.
///
/// Mirrors the shape <c>ConsoleCommand.Cheats.cs</c>'s own <c>ParsePartySelect</c> already uses
/// for its single fixed subcommand ("ok"), but pulled out as a standalone pure rule because this
/// verb has three, and the task asked for a testable rule wherever there is decision logic —
/// unlike <c>partyselect</c>'s inline one-liner, a three-way switch is worth its own unit tests
/// independent of <c>ConsoleCommand.Parse</c>'s tokenising/dispatch plumbing.
///
/// A `galaxymap confirm` subcommand — driving the accept-button case handler directly, the way
/// `partyselect ok` drives <c>ConfirmPartySelection</c> — was investigated for this pass and
/// deliberately NOT added: see the research doc's own 2026-09-03 addendum for what fresh RE
/// found (the jump-table byte-lookup table decodes cleanly, but which raw GUI event code a REAL
/// accept click fires, and whether replaying it through a bare <c>HandleInputEvent</c> call
/// reproduces every side effect a real click has on the button's own internal state, is new RE
/// this pass could not verify without a running process — so it stays out rather than shipping a
/// guess).
/// </summary>
public static class GalaxyMapVerbRule
{
    /// <summary>The three subcommands this verb recognises, or <see cref="Invalid"/> for
    /// anything else (including no subcommand, or more than one token).</summary>
    public enum Verb
    {
        Invalid,
        Open,
        Close,
        Stats,
    }

    public const string Usage = "usage: galaxymap open|close|stats";

    /// <param name="args">The tokens after `galaxymap` (already split by
    /// <c>ConsoleCommand.Parse</c>, no leading/trailing whitespace on any token).</param>
    public static Verb Parse(string[] args)
    {
        if (args.Length != 1)
        {
            return Verb.Invalid;
        }

        return args[0].ToLowerInvariant() switch
        {
            "open" => Verb.Open,
            "close" => Verb.Close,
            "stats" => Verb.Stats,
            _ => Verb.Invalid,
        };
    }
}
