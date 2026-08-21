namespace KotorCoop.Plugin;

/// <summary>What a peer should do with a mid-module movie another instance's
/// <c>CinematicWatch</c> announced.</summary>
internal enum MovieRelayAction
{
    /// <summary>Play it now: nothing here is fragile to interrupt with a several-second
    /// main-thread block.</summary>
    PlayNow,

    /// <summary>Stage it for the NEXT module transition instead
    /// (<see cref="MovieStaging.ModeQueueOnly"/>) — the same queue transition movies already
    /// ride, so no new engine call is needed to hold it.</summary>
    DeferToNextTransition,

    /// <summary>Do nothing: this announcement is not for a module this peer is even in, or
    /// the channel found nothing worth acting on.</summary>
    Ignore,
}

/// <summary>
/// The rules for deciding what a MID-MODULE movie announcement should do on the receiving
/// end, as pure arithmetic over facts the caller has already measured — no engine pointers, no
/// shared block, no packets — the same shape and the same reason as <see cref="MovieStaging"/>
/// and <see cref="CheatAuthority"/>: a decision worth asserting is a decision worth compiling
/// into the test project without an engine underneath it.
///
/// WHY "PLAY NOW" IS SAFE TO EVEN CONSIDER, AND WHY IT USUALLY IS NOT. A pre-rendered movie
/// played mid-module is not a module TRANSITION — nothing about <c>StartNewModule</c> or the
/// travel path is involved — but it is still a call into <c>WaitForMovie 0x005F6F20</c>, which
/// parks the MAIN THREAD until the film ends (docs/research/cinematics.md,
/// docs/research/trigger-relay.md §6). Interrupting a conversation or a fight to do that is
/// exactly the kind of surprise this codebase avoids everywhere else (the encounter watch and
/// the trigger watch both skip a pass mid-dialogue for the identical reason), so this class
/// only ever answers <see cref="MovieRelayAction.PlayNow"/> when the receiving peer is doing
/// nothing that a several-second freeze would disrupt. Every other case defers to the
/// transition-movie machinery that already exists and is already live-verified.
///
/// ⚠ HONEST GAP, stated once rather than hidden in a comment nobody reads at the call site:
/// this class is the DECISION half only. The DETECTION half — CinematicWatch capturing the
/// resref a mid-module <c>PlayMovie</c> call played, as opposed to the transition queue it
/// already reads today — needs a new engine hook (the likely target, not yet located: the
/// NWScript command dispatch entry for <c>ExecuteCommandPlayMovie</c>, found the way
/// <c>CSWSTrigger::EventHandler</c> was found for this same pass — via <c>tools/xrefs.py</c>
/// on a string literal or a known caller) and was NOT completed in this pass. See
/// docs/research/trigger-relay.md §6 for exactly what is and is not wired up.
/// </summary>
internal static class MovieRelayQueue
{
    /// <summary>
    /// Decides what to do with one mid-module movie announcement.
    /// </summary>
    /// <param name="sameModule">Whether the announcing peer's module (carried on the
    /// announcement, the same way <c>TransitionMoviePacket</c> and every other module-scoped
    /// channel here carries it) matches this instance's own current module. An announcement
    /// for a different module names nothing this instance should act on — the two players
    /// are not even looking at the same room.</param>
    /// <param name="hasMovieName">False when nothing survived parsing/resref validation
    /// (<see cref="MovieStaging.IsMovieName"/>) — an empty announcement is not staged either
    /// way.</param>
    /// <param name="isIdle">The receiving peer's own player is not mid-transition, not
    /// mid-minigame, and the world is actually up (<c>Odyssey.HasServer</c>) — the same
    /// baseline every other watch in this codebase checks before touching engine state.</param>
    /// <param name="inDialogue">A conversation is active locally. Freezing the main thread
    /// mid-conversation is the one interruption every dialogue-adjacent controller in this
    /// codebase (the encounter watch, the trigger watch, the dialogue spectate camera) already
    /// refuses to cause.</param>
    /// <param name="inCombat">A fight is active locally (this player's own engine, not a
    /// mirrored one). Freezing mid-swing is the same class of surprise, on the combat side.
    /// </param>
    /// <param name="alreadyQueued">Movies the engine's own transition queue already holds.
    /// Mirrors <see cref="MovieStaging.Decide"/>'s <c>RefuseQueueBusy</c>: piling a mid-module
    /// announcement on top of a queue the GAME itself just filled would change what the
    /// player is about to watch on their next transition, which is not this channel's call to
    /// make.</param>
    public static MovieRelayAction Decide(
        bool sameModule,
        bool hasMovieName,
        bool isIdle,
        bool inDialogue,
        bool inCombat,
        int alreadyQueued)
    {
        if (!sameModule || !hasMovieName)
        {
            return MovieRelayAction.Ignore;
        }

        if (alreadyQueued > 0)
        {
            // The engine already has its own transition movies staged; queueing a second,
            // unrelated one behind them would silently change what plays next. Deferring
            // further (to whatever transition comes AFTER this queue drains) is not correct
            // either — that transition's queue is unknown from here — so the honest answer is
            // to drop this announcement rather than guess. A dropped mid-module movie costs a
            // cinematic, never a desync, exactly like a dropped TransitionMovies announcement.
            return MovieRelayAction.Ignore;
        }

        if (!isIdle)
        {
            return MovieRelayAction.DeferToNextTransition;
        }

        return inDialogue || inCombat
            ? MovieRelayAction.DeferToNextTransition
            : MovieRelayAction.PlayNow;
    }
}
