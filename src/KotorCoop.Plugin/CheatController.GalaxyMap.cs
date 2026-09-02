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
    /// Reports <see cref="GalaxyMapConfirmHook"/>'s own live counters, plus — added the same
    /// pass as <see cref="GalaxyMapAccept"/> — whether the full-screen GUI is currently up and
    /// which planet is selected, so a live rig can confirm <c>galaxyaccept</c>'s outcome without
    /// a screenshot (Tier 2 item 18's own "read the panel's state" ask). No engine call: three
    /// reads (<see cref="Odyssey.TryReadGalaxyMapState"/>) plus the two static counters and the
    /// install flag. The counters were the piece the research doc's own "Live test plan"
    /// flagged as missing: <c>RefusedCount</c>/<c>AllowedCount</c> were not exposed by any verb,
    /// so a refusal could only be confirmed by grepping the plugin log for its one log line, not
    /// by a scripted rig.
    /// </summary>
    private void GalaxyMapStats()
    {
        bool hasState = Odyssey.TryReadGalaxyMapState(out bool open, out int selectedPlanet, out bool panelLooksRight);
        string state = hasState
            ? $"open={open}, selectedPlanet={selectedPlanet}, panelIsGalaxyMap={panelLooksRight}"
            : "open=?, selectedPlanet=?, panelIsGalaxyMap=? (no in-game GUI resolved)";

        string detail = $"installed={GalaxyMapConfirmHook.IsInstalled}, "
            + $"refused={GalaxyMapConfirmHook.RefusedCount}, "
            + $"allowed={GalaxyMapConfirmHook.AllowedCount}, "
            + $"replayed={GalaxyMapConfirmHook.ReplayedCount}, "
            + $"departures={block.GalaxyDepartureSerial}, {state}";
        Answer(SharedBlock.CheatStatusApplied, $"galaxymap stats: {detail}");
    }

    /// <summary>
    /// `galaxymap select &lt;n&gt;` — the planet-selection half of the `galaxyaccept` addendum
    /// (docs/research/galaxy-map-and-party-select.md's 2026-09-03 section): writes
    /// <c>CSWPartyTable::SetSelectedPlanet</c> directly through
    /// <see cref="Odyssey.SetGalaxyMapSelectedPlanet"/>, independent of whether the map screen
    /// is open, so <see cref="GalaxyMapAccept"/> has something real to confirm.
    /// </summary>
    private void SelectGalaxyMapPlanet()
    {
        int planet = block.CheatAmount;
        bool applied = Odyssey.SetGalaxyMapSelectedPlanet(planet, out string detail);
        PluginHost.Log($"galaxymap select {planet}: {detail}");
        Answer(
            applied ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            $"galaxymap select {planet}: {detail}");
    }

    /// <summary>
    /// STAGING VERB: `galaxymap unlock &lt;n&gt;` — marks planet n available and selectable in
    /// the party table (<see cref="Odyssey.UnlockGalaxyMapPlanet"/>) so
    /// <see cref="SelectGalaxyMapPlanet"/> can succeed on a save whose story has not unlocked
    /// it. Rig-only staging; see the 2026-09-03 live note in
    /// docs/research/galaxy-map-and-party-select.md for why (every rig save is Endar Spire).
    /// </summary>
    private void UnlockGalaxyMapPlanet()
    {
        int planet = block.CheatAmount;
        bool applied = Odyssey.UnlockGalaxyMapPlanet(planet, out string detail);
        PluginHost.Log($"galaxymap unlock {planet}: {detail}");
        Answer(
            applied ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            $"galaxymap unlock {planet}: {detail}");
    }

    /// <summary>
    /// `galaxymap depart &lt;n&gt;` — the whole interplanetary leg on this instance in one
    /// frame (<see cref="Odyssey.DepartToPlanet"/>): the same call the session's follower path
    /// (<see cref="GalaxyDepartureController"/>) makes when a peer's departure arrives, exposed
    /// as a verb so the replay can be proven on one instance before trusting the relay.
    /// </summary>
    private void DepartGalaxyMap()
    {
        int planet = block.CheatAmount;
        bool applied = Odyssey.DepartToPlanet(planet, out string detail);
        PluginHost.Log($"galaxymap depart {planet}: {detail}");
        Answer(
            applied ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            $"galaxymap depart {planet}: {detail}");
    }

    /// <summary>
    /// LIVE-TEST PRIMITIVE: `galaxyaccept` — presses Accept on the galaxy map through the
    /// engine's own input dispatch (<see cref="Odyssey.SendGalaxyMapAcceptKey"/>), so
    /// <see cref="GalaxyMapConfirmHook"/>'s confirm-refusal splice can finally be exercised
    /// without a human clicking. See docs/research/galaxy-map-and-party-select.md's 2026-09-03
    /// `galaxyaccept` addendum for the full derivation of the event code and the live recipe.
    /// </summary>
    private void GalaxyMapAccept()
    {
        bool applied = Odyssey.SendGalaxyMapAcceptKey(out string detail);
        PluginHost.Log($"galaxyaccept: {detail}");
        Answer(
            applied ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            $"galaxyaccept: {detail}");
    }
}
