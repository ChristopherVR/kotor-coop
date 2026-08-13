using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// Decides WHEN a session started from the game's own menu should load the world.
///
/// ⚠ THE GATE IS THE POINT. The load used to fire the instant the menu handed over a choice,
/// in parallel with a handshake that had not happened yet — so a join the host REFUSED still
/// put the player in the world. Live, with a wrong password: the client logged
/// <c>rejected by host: E_PASSWORD</c>, the host stayed at zero peers, and the game loaded
/// END_M01AA regardless. The player then stands in single-player believing they are in co-op,
/// with the only evidence in a console window behind the game. A refused join has to leave them
/// at the menu, where they can pick again or answer a password prompt.
///
/// Its own class, rather than a lambda inside the startup path, because this rule is worth a
/// test and the thing it guards is a real game load.
/// </summary>
public static class MenuAutoLoad
{
    /// <summary>
    /// Runs <paramref name="load"/> once the session is real.
    ///
    /// A HOST loads immediately: there is nobody to be admitted by, and it has to be in the
    /// world for a client to have anything to join. A JOINER waits for
    /// <see cref="SessionPhase.Connected"/> — with no deadline, deliberately, so that answering
    /// a password prompt and being admitted on the second (or third) attempt still lands in the
    /// world. Cancellation is the only other way out, and it loads nothing: a shutting-down
    /// agent must not leave the player in a world with no session behind it.
    ///
    /// <paramref name="announce"/> is called once if the join is refused, so the wait is visible
    /// rather than looking like a hang.
    /// </summary>
    public static async Task RunAsync(
        SessionManager manager,
        bool host,
        Action load,
        CancellationToken token,
        Action<string>? announce = null,
        int pollMs = 200)
    {
        if (!host)
        {
            bool announced = false;

            while (manager.Phase != SessionPhase.Connected)
            {
                if (manager.Phase == SessionPhase.Failed && !announced)
                {
                    announced = true;
                    announce?.Invoke("auto-load held: the join was refused; staying at the main menu.");
                }

                try
                {
                    await Task.Delay(pollMs, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        load();
    }
}
