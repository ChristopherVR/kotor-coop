namespace KotorCoop.Memory;

/// <summary>Where a KOTOR process is in its startup.</summary>
public enum GamePhase
{
    /// <summary>Process is up but the client app is not ready; logo movies play here.</summary>
    Starting,

    /// <summary>Main menu. There is no server object at all in this state.</summary>
    MainMenu,

    /// <summary>A module is loading, or loaded but the player is not resolvable yet.</summary>
    Loading,

    /// <summary>In the world with a controllable player.</summary>
    InWorld,
}

/// <summary>
/// Reads which startup phase a KOTOR process is in, without needing the plugin.
///
/// The distinction that matters is that <b>the server object does not exist at the main
/// menu</b>: <c>APP_MANAGER+0x08</c> is null there and is only created when a game starts.
/// That single fact is what makes the phases separable from outside, and it is also why
/// the game cannot be made to load a save by sending it the load message from the menu -
/// there is nothing on the other end to receive it.
/// </summary>
public sealed class GameLifecycle(ProcessMemory memory)
{
    /// <summary>CClientExoAppInternal + this is the client's message channel.</summary>
    private const int ClientMessageOffset = 0x14C;

    public GamePhase GetPhase()
    {
        if (!memory.TryReadPointer(EngineGlobals.AppManagerPtr, out nint appManager) || appManager == 0)
        {
            return GamePhase.Starting;
        }

        if (EngineGlobals.TryGetServer(memory, out _))
        {
            // The server exists, so a game is running. Whether it is playable depends on
            // there being a player creature, which only appears once the module is in.
            //
            // A throwaway bridge, not the shared one the pump drives — its own per-instance
            // caches can never race another caller's, so this lock is currently uncontended.
            // Taken anyway (and TryResolve locks itself too — see IGameBridge.Sync) so a
            // future caller that DOES hand this a shared bridge inherits the same guarantee
            // for free, exactly like SaveLoader.WaitFor(GamePhase.InWorld) polling this from a
            // thread other than the pump during a mid-session load.
            KotorGameBridge bridge = new(memory, maxPhantoms: 0, ownsMemory: false);
            lock (bridge.Sync)
            {
                return bridge.TryResolve(out _) ? GamePhase.InWorld : GamePhase.Loading;
            }
        }

        // No server. Either still starting up, or sitting at the main menu; the client's
        // message channel being live is what separates the two.
        bool clientReady =
            EngineGlobals.TryGetClientInternal(memory, out nint clientInternal)
            && memory.TryReadPointer(clientInternal + ClientMessageOffset, out nint message)
            && message != 0;

        return clientReady ? GamePhase.MainMenu : GamePhase.Starting;
    }

    /// <summary>Waits for a phase, or returns false on timeout or process exit.</summary>
    public bool WaitFor(GamePhase phase, TimeSpan timeout, out GamePhase actual)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        actual = GamePhase.Starting;

        while (DateTime.UtcNow < deadline)
        {
            if (!memory.IsOpen)
            {
                return false;
            }

            actual = GetPhase();

            if (actual == phase)
            {
                return true;
            }

            Thread.Sleep(500);
        }

        return false;
    }
}
