namespace KotorCoop.Session;

/// <summary>Which path drives this tick's follow decision.</summary>
public enum TransitionFollowPath
{
    /// <summary>
    /// Warp NOW to the announce's destination, bypassing the settle debounce and the
    /// "does the host's published module already differ from ours" check — the host itself
    /// confirmed its load began, which is strictly earlier and strictly more certain than
    /// waiting for its published module to catch up.
    /// </summary>
    Announced,

    /// <summary>
    /// No usable announce right now — none pending, it named our own current module (nothing
    /// to do), or it has expired — so this tick falls back to the pre-existing host-module-
    /// based follow, through its own settle debounce.
    /// </summary>
    Fallback,
}

/// <summary>
/// The pure half of "announced transitions" (docs/research/transition-smoothness.md): given
/// whether a usable host <see cref="TransitionAnnouncePacket"/> is pending, decide whether this
/// tick's follow should race ahead of the host's published module or wait for it.
///
/// Deliberately three lines of arithmetic and nothing else — no engine pointers, no session,
/// no clock read inside it — so the state machine described in the task ("announce received →
/// warp now; no announce but module changed → old path; announce lost → fallback") is testable
/// as data in, decision out. Everything ELSE a real follow needs (the stunt-module gate, the
/// fallback-area-name guard, the retry debounce) is unchanged and applied by the CALLER
/// (<c>CoopAgent.PumpTravel</c>) to whichever module name this decided to use — an announced
/// destination is not exempt from any of those; it only skips the wait.
/// </summary>
public static class TransitionAnnounceRule
{
    /// <summary>
    /// <paramref name="hasPendingAnnounce"/> is "a host announce arrived and has not yet been
    /// consumed or expired" — the caller owns expiry (a stale announce is simply not pending
    /// any more, and is dropped exactly like <c>_pendingFollowMovies</c> is on a genuine
    /// reunion). <paramref name="announceModule"/> empty means "nothing decodable" and always
    /// falls back, the same as any other unreadable module name.
    /// </summary>
    public static TransitionFollowPath Decide(
        bool hasPendingAnnounce, string announceModule, string localModule)
    {
        if (!hasPendingAnnounce || announceModule.Length == 0)
        {
            return TransitionFollowPath.Fallback;
        }

        // Already there (or the announce is describing a module we have not yet left, e.g.
        // its own stale echo): nothing to race ahead of.
        return string.Equals(announceModule, localModule, StringComparison.OrdinalIgnoreCase)
            ? TransitionFollowPath.Fallback
            : TransitionFollowPath.Announced;
    }
}
