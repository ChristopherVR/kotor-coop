// Odyssey.GalaxyMapDepart.cs - `galaxymap depart <n>` and the follower half of the galaxy-map
// departure relay (SharedBlock.GalaxyDeparture.cs): run the SAME scripted interplanetary leg
// the leader's accept click ran, on this instance's own engine, in one frame - the four
// live-proven staging primitives (unlock, select, open, accept; docs/research/
// galaxy-map-and-party-select.md's 2026-09-03 addendum, three clean departures on the rig)
// composed into one call, with GalaxyMapConfirmHook told that the accept it is about to see
// is a REPLAY rather than this player's own choice.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary>
    /// Departs this instance to <paramref name="planet"/> exactly the way a galaxy-map accept
    /// does — the confirm script, the take-off movie, <c>StartNewModule</c> into the launch
    /// cutscene, the fresh Ebon Hawk — so a follower arrives in the same world state the
    /// leader's own accept produced, natively, with nothing to reconstruct afterwards.
    ///
    /// Composition, in order, each step's own refusal reported verbatim:
    /// <list type="number">
    /// <item>Take down whatever full-screen GUI is up (<see cref="CloseFullScreenGui"/>): a
    /// peer's departure outranks this player's own inventory screen, and
    /// <c>ShowGalaxyMapGui</c> refuses while <c>CGuiInGame+0x30</c> is nonzero.</item>
    /// <item><see cref="UnlockGalaxyMapPlanet"/>: the party-table planet flags are per-save and
    /// not synced, so the follower's own save may not have unlocked what the leader's has.</item>
    /// <item><see cref="SetGalaxyMapSelectedPlanet"/>, then <see cref="OpenGalaxyMap"/> (the
    /// accept dispatch requires the panel to be the open full-screen GUI).</item>
    /// <item><see cref="SendGalaxyMapAcceptKey"/> with <see cref="GalaxyMapConfirmHook"/>'s
    /// replay marker armed, so the hook lets it through under any <c>follow_lead</c> and does
    /// NOT republish it as this player's own departure.</item>
    /// </list>
    ///
    /// Refuses (rather than half-running) while a conversation is active: the leader's own
    /// leg never starts mid-dialogue either, and <c>ShowGalaxyMapGui</c> would refuse anyway.
    /// The caller retries on a later frame. MAIN THREAD ONLY.
    /// </summary>
    internal static bool DepartToPlanet(int planet, out string detail)
    {
        if (planet is < 0 or > 0x0F)
        {
            detail = $"planet {planet} is out of range [0,15]";
            return false;
        }

        nint gui = GetInGameGui();
        if (gui == 0)
        {
            detail = "no in-game GUI resolved (not in a loaded module)";
            return false;
        }

        if (*(int*)(gui + InGameGuiInitializedOffset) == 0)
        {
            detail = "the HUD is not initialised yet (still loading)";
            return false;
        }

        if (*(int*)(gui + GuiConversationActive) != 0 || IsConversationActive)
        {
            detail = "a conversation is active - not departing mid-dialogue";
            return false;
        }

        if (*(int*)(gui + InGameGuiBusyOffset) != 0 && !CloseFullScreenGui(out string closeDetail))
        {
            detail = $"could not take down the open full-screen GUI first: {closeDetail}";
            return false;
        }

        if (!UnlockGalaxyMapPlanet(planet, out string unlockDetail))
        {
            detail = $"unlock: {unlockDetail}";
            return false;
        }

        if (!SetGalaxyMapSelectedPlanet(planet, out string selectDetail))
        {
            detail = $"select: {selectDetail}";
            return false;
        }

        if (!OpenGalaxyMap(out string openDetail))
        {
            detail = $"open: {openDetail}";
            return false;
        }

        GalaxyMapConfirmHook.ArmReplay();
        bool accepted;
        string acceptDetail;
        try
        {
            accepted = SendGalaxyMapAcceptKey(out acceptDetail);
        }
        finally
        {
            GalaxyMapConfirmHook.DisarmReplay();
        }

        if (!accepted)
        {
            // Leave nothing half-open behind a refusal: the map we opened two steps ago is
            // still the full-screen GUI, and the player did not ask for it.
            CloseGalaxyMap(out _);
            detail = $"accept: {acceptDetail}";
            return false;
        }

        detail = $"planet {planet}: {unlockDetail}; {selectDetail}; {acceptDetail}";
        return true;
    }
}
