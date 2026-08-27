namespace KotorCoop.Plugin;

/// <summary>
/// Which engine mechanism raised a given <c>TransitionAnnounce</c> — diagnostic/log-only,
/// computed and logged next to the announce so a live pass can tell a galaxy-map departure
/// from an ordinary door crossing at a glance. This NEVER reaches
/// <c>KotorCoop.Session.TransitionAnnounceRule</c>/<c>LeadFollowRule</c> and never crosses the
/// wire or touches <see cref="SharedBlock"/> — every one of those already treats an announced
/// destination identically regardless of how it was resolved
/// (docs/research/transition-smoothness.md §3.3: "an announced destination is earlier
/// information, never a different set of rules"), so adding a kind changes nothing about what
/// gets followed or when, only what the log says happened.
/// </summary>
public enum TransitionAnnounceKind
{
    /// <summary>Resolved from the nearest linked door's own <c>LinkedToModule</c> — the
    /// pre-existing <c>trans_pending</c> rising edge (<c>TransitionAnnounceWatch.OnTransitionBegan</c>).</summary>
    Door,

    /// <summary>
    /// Resolved from <c>CServerExoAppInternal</c>'s move-to-module-pending funnel with no door
    /// in the loop: an NWScript <c>StartNewModule()</c> call, a native area/trigger-driven
    /// transition, or a debug verb (<c>warp</c>, the load-module debug menu).
    /// </summary>
    Script,

    /// <summary>
    /// A <see cref="Script"/> transition whose SOURCE module — the one being left, not the
    /// destination — is a known Ebon Hawk instance resref. INFERRED, not an engine-native
    /// distinction: nothing in <c>CServerExoAppInternal</c>'s own state says "this transition
    /// came from the galaxy map or the ship's exit corridor" — a source-module heuristic is the
    /// closest available signal, which is exactly why this stays diagnostic-only rather than
    /// feeding any decision. See docs/research/galaxy-map-and-party-select.md §1.3: both traced
    /// departure legs (the galaxy map's own confirm, and walking out through the exit corridor)
    /// originate from inside one of these three resrefs.
    /// </summary>
    GalaxyMap,
}

/// <summary>Pure classifier for <see cref="TransitionAnnounceKind"/> — no engine pointers, no
/// shared block, so it compiles into the 64-bit test project directly (the "plugin
/// compile-include trick", docs/CONTRIBUTING.md).</summary>
public static class TransitionAnnounceKindClassifier
{
    /// <summary>The three Ebon Hawk instance resrefs
    /// docs/research/galaxy-map-and-party-select.md §1.3 traced both departure legs
    /// (confirm-on-the-map, and the exit-corridor dispatch) originating from.</summary>
    public static readonly string[] EbonHawkModules = ["ebo_m12aa", "ebo_m40aa", "ebo_m41aa"];

    /// <summary>
    /// <paramref name="sourceModule"/> is the module being LEFT — empty or unreadable always
    /// classifies as <see cref="TransitionAnnounceKind.Script"/> (the honest "we don't know"
    /// answer, never a guess dressed up as <see cref="TransitionAnnounceKind.GalaxyMap"/>).
    /// </summary>
    public static TransitionAnnounceKind Classify(string sourceModule)
    {
        if (sourceModule.Length == 0)
        {
            return TransitionAnnounceKind.Script;
        }

        foreach (string ebonHawk in EbonHawkModules)
        {
            if (string.Equals(sourceModule, ebonHawk, StringComparison.OrdinalIgnoreCase))
            {
                return TransitionAnnounceKind.GalaxyMap;
            }
        }

        return TransitionAnnounceKind.Script;
    }
}
