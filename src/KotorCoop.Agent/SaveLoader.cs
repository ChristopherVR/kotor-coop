using System.Diagnostics;
using KotorCoop.Memory;

namespace KotorCoop.Agent;

/// <summary>
/// Takes a freshly launched game into a save with no human input.
///
/// This drives the main menu rather than calling the engine, and that is a deliberate
/// retreat from the obvious approach. The engine route was tried: the save menu ends in
/// <c>CClientExoApp::SendLoadGameRequest</c>, and that call can be reproduced - but only
/// after <c>CAppManager::CreateServer</c>, because <b>there is no server object at the
/// main menu</b> and sending the request without one takes the process down. Even with
/// the server created and its services started, and with valid (non-null) CExoStrings,
/// the request is accepted and then never serviced: the game stays on the menu. Faithfully
/// reproducing the transition means replaying the GUI state machine too, which is a much
/// larger job than it looks. See section 4.2 of Handoff.md for the addresses, so nobody
/// has to rediscover that.
///
/// What makes the menu route reliable is not clicking harder, it is waiting on observable
/// state instead of sleeping. <see cref="GameLifecycle"/> tells us when the menu actually
/// exists - the earlier version of this failed purely because it clicked during the logo
/// movies - and when the world is finally up.
///
/// Click positions are fractions of the client area, so they survive a resolution change.
/// They were measured at 800x600 and are the fragile part: a mod that reskins the main
/// menu moves them.
/// </summary>
public static class SaveLoader
{
    /// <summary>"Load Game" on the main menu.</summary>
    private const double LoadGameX = 0.605;
    private const double LoadGameY = 0.538;

    /// <summary>The first save slot in the list, and the spacing between slots.</summary>
    private const double FirstSlotX = 0.340;
    private const double FirstSlotY = 0.283;
    private const double SlotSpacingY = 0.090;

    /// <summary>The "Load" button at the bottom of the save list.</summary>
    private const double LoadButtonX = 0.501;
    private const double LoadButtonY = 0.807;

    /// <summary>
    /// Clicks through Load Game -> slot -> Load and returns once the player creature
    /// exists. <paramref name="slotIndex"/> is zero-based from the top of the save list.
    /// </summary>
    public static bool TryLoadSave(
        Process game, ProcessMemory memory, int slotIndex, TimeSpan timeout, out string diagnostic,
        bool viaMenuClicks = false)
    {
        GameLifecycle lifecycle = new(memory);
        DateTime deadline = DateTime.UtcNow + timeout;

        if (lifecycle.GetPhase() == GamePhase.InWorld)
        {
            diagnostic = "already in the world; nothing to load";
            return true;
        }

        // Wait for the menu to actually exist. Clicking before this is the single reason
        // the first version of this never worked: the window appears many seconds before
        // the menu does, and clicks during the logo movies go nowhere.
        if (!lifecycle.WaitFor(GamePhase.MainMenu, TimeSpan.FromSeconds(90), out GamePhase phase))
        {
            diagnostic = phase == GamePhase.InWorld
                ? "already in the world; nothing to load"
                : $"the main menu never came up (stuck in {phase})";
            return phase == GamePhase.InWorld;
        }

        // The in-process route: the plugin replays the GUI's own load path on the game's
        // thread, so it needs no window focus, no cursor and no clicks — menu input
        // injection is intermittently dead in ways that resisted diagnosis (trap 25).
        //
        // OPT-IN for now (KOTORCOOP_PLUGIN_LOAD=1): the replay no longer crashes — the
        // full LoadGame branch is reproduced, server + loopback session included — but
        // the module transition is never serviced (black load screen, no world; see the
        // Handoff addendum for the two remaining leads). A stalled load leaves nothing
        // for the click fallback to click on, so it must not be the default until it
        // completes.
        bool pluginRouteRequested =
            !viaMenuClicks && Environment.GetEnvironmentVariable("KOTORCOOP_PLUGIN_LOAD") == "1";

        if (pluginRouteRequested)
        {
            if (TryLoadViaPlugin(game, memory, lifecycle, slotIndex, out string pluginDiagnostic))
            {
                diagnostic = pluginDiagnostic;
                return true;
            }

            // Say WHY before falling back to clicking, or the plugin route's failures
            // are indistinguishable from it never having been tried.
            Console.WriteLine($"plugin load route failed: {pluginDiagnostic}");
        }

        if (!TryWaitForWindow(game, TimeSpan.FromSeconds(30), out int width, out int height))
        {
            diagnostic = "the game never produced a window to click on";
            return false;
        }

        if (!GameInput.FocusGameWindow(game, out string focusDiagnostic))
        {
            diagnostic = $"cannot drive the menu: {focusDiagnostic}";
            return false;
        }

        // Retry the whole sequence rather than trying to verify each click. Escape at the
        // start backs out of the save list, so every attempt begins from the main menu no
        // matter where the previous one stalled.
        for (int attempt = 1; attempt <= 4 && DateTime.UtcNow < deadline; attempt++)
        {
            GameInput.TapKey(GameInput.ScanEscape);
            Thread.Sleep(900);

            Click(game, width, height, LoadGameX, LoadGameY);
            Thread.Sleep(1500);

            Click(game, width, height, FirstSlotX, FirstSlotY + (SlotSpacingY * slotIndex));
            Thread.Sleep(800);

            Click(game, width, height, LoadButtonX, LoadButtonY);
            Thread.Sleep(300);

            if (lifecycle.WaitFor(GamePhase.InWorld, TimeSpan.FromSeconds(60), out _))
            {
                diagnostic = $"loaded on attempt {attempt}";
                return true;
            }

            game.Refresh();
            if (game.HasExited)
            {
                diagnostic = "the game exited while loading";
                return false;
            }
        }

        diagnostic = $"the save did not load within {timeout.TotalSeconds:F0}s";
        return false;
    }

    /// <summary>
    /// Clicks a point given as a fraction of the client area, making sure the game is in
    /// front first.
    ///
    /// The focus check is not belt-and-braces. Windows gives a click on a background
    /// window to the activation, not to whatever was under the cursor, so a single lost
    /// focus turns the whole sequence into a no-op that looks exactly like wrong
    /// coordinates. That is precisely how this failed intermittently.
    /// </summary>
    private static bool Click(Process game, int width, int height, double fractionX, double fractionY)
    {
        if (!GameInput.IsGameForeground(game))
        {
            GameInput.FocusGameWindow(game, out _);
            Thread.Sleep(250);
        }

        return GameInput.ClickClient(game, (int)(width * fractionX), (int)(height * fractionY));
    }

    /// <summary>
    /// Stages the wanted save as the autosave, asks the plugin to fire the engine's own
    /// autosave-load path, and waits for the world. False (with the reason) when the
    /// plugin is absent or refused — the caller falls back to menu clicking.
    /// </summary>
    private static bool TryLoadViaPlugin(
        Process game, ProcessMemory memory, GameLifecycle lifecycle, int slotIndex, out string diagnostic)
    {
        string? gameDirectory;
        try
        {
            gameDirectory = Path.GetDirectoryName(game.MainModule?.FileName);
        }
        catch (Exception ex)
        {
            diagnostic = $"cannot resolve the game directory: {ex.Message}";
            return false;
        }

        if (gameDirectory is null)
        {
            diagnostic = "cannot resolve the game directory";
            return false;
        }

        if (!PluginMenuLoader.TryStageSaveAsAutosave(gameDirectory, slotIndex, out string staging))
        {
            diagnostic = staging;
            return false;
        }

        if (!PluginMenuLoader.TryRequestLoad(memory, out nint block, out string request))
        {
            diagnostic = request;
            return false;
        }

        Console.WriteLine($"{staging}; {request}");

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (lifecycle.GetPhase() == GamePhase.InWorld)
            {
                diagnostic = "loaded through the plugin (no input injection)";
                return true;
            }

            // A non-zero status is the plugin telling us the fire itself failed —
            // no point waiting the full window on it.
            if (PluginMenuLoader.TryReadStatus(memory, block, out _, out int status) && status != 0)
            {
                diagnostic = $"the plugin refused the load (failure {status})";
                return false;
            }

            game.Refresh();
            if (game.HasExited)
            {
                diagnostic = "the game exited while loading";
                return false;
            }

            Thread.Sleep(500);
        }

        diagnostic = "the plugin accepted the load but no world appeared within 90s";
        return false;
    }

    private static bool TryWaitForWindow(Process game, TimeSpan timeout, out int width, out int height)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            game.Refresh();

            if (game.HasExited)
            {
                break;
            }

            (width, height) = GameInput.GetClientSize(game);

            if (width > 0 && height > 0)
            {
                return true;
            }

            Thread.Sleep(500);
        }

        width = 0;
        height = 0;
        return false;
    }
}
