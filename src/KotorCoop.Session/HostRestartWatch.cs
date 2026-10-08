namespace KotorCoop.Session;

/// <summary>
/// Notices the host process restarting underneath one client connection: the same client object
/// welcomed by a different host session id. A restarted host loaded its world from a save, so a
/// joiner that reconnected must adopt it again even when the save identities happen to agree (the
/// host may have relaunched on the very save the joiner adopted from, while the joiner's live world
/// had moved on). A new connection object only takes a baseline: a fresh join adopts by itself.
/// </summary>
public static class HostRestartWatch
{
    public record struct State(object? Connection, uint HostSessionId);

    /// <returns>True on the one call where the same connection first reports a different, known
    /// host session id.</returns>
    public static bool Observe(ref State state, object connection, uint hostSessionId)
    {
        if (!ReferenceEquals(state.Connection, connection))
        {
            state = new State(connection, hostSessionId);
            return false;
        }

        if (hostSessionId == 0 || hostSessionId == state.HostSessionId)
        {
            return false;
        }

        bool restarted = state.HostSessionId != 0;
        state = state with { HostSessionId = hostSessionId };
        return restarted;
    }
}
