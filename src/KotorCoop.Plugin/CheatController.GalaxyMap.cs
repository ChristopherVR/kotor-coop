namespace KotorCoop.Plugin;

// The `galaxymap open|close|stats` dev-console verbs (Tier 2 item 18 task 2's own "Live test
// plan" follow-up, docs/research/galaxy-map-and-party-select.md): exercise
// GalaxyMapConfirmHook's refusal without a human clicking. `open`/`close` drive the real engine
// entry points (Odyssey.GalaxyMap.cs); `stats` is a pure read of the hook's own counters, no
// engine call at all. See docs/research/galaxy-map-and-party-select.md's 2026-09-03 addendum
// for the live recipe.
internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// LIVE-TEST PRIMITIVE: opens this instance's own galaxy-map GUI through
    /// <see cref="Odyssey.OpenGalaxyMap"/>. On a CLIENT under <c>follow_lead=host</c> with
    /// <c>galaxy_map_host_only</c> enabled this only opens the SCREEN — it does not confirm a
    /// destination, so <see cref="GalaxyMapConfirmHook"/> never fires from this verb alone;
    /// pair it with a real accept click (there is no synthesised `galaxymap confirm` — see that
    /// hook's own class remarks and the research doc's 2026-09-03 addendum for why).
    /// </summary>
    private void OpenGalaxyMap()
    {
        bool applied = Odyssey.OpenGalaxyMap(out string detail);
        PluginHost.Log($"galaxymap open: {detail}");
        Answer(
            applied ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            $"galaxymap open: {detail}");
    }

    /// <summary>The counterpart to <see cref="OpenGalaxyMap"/>, through
    /// <see cref="Odyssey.CloseGalaxyMap"/>.</summary>
    private void CloseGalaxyMap()
    {
        bool applied = Odyssey.CloseGalaxyMap(out string detail);
        PluginHost.Log($"galaxymap close: {detail}");
        Answer(
            applied ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            $"galaxymap close: {detail}");
    }

    /// <summary>
    /// Reports <see cref="GalaxyMapConfirmHook"/>'s own live counters — no engine call, just the
    /// two static counters and the install flag. This is the piece the research doc's own
    /// "Live test plan" flagged as missing: <c>RefusedCount</c>/<c>AllowedCount</c> were not
    /// exposed by any verb, so a refusal could only be confirmed by grepping the plugin log for
    /// its one log line, not by a scripted rig.
    /// </summary>
    private void GalaxyMapStats()
    {
        string detail = $"installed={GalaxyMapConfirmHook.IsInstalled}, "
            + $"refused={GalaxyMapConfirmHook.RefusedCount}, "
            + $"allowed={GalaxyMapConfirmHook.AllowedCount}";
        Answer(SharedBlock.CheatStatusApplied, $"galaxymap stats: {detail}");
    }
}
