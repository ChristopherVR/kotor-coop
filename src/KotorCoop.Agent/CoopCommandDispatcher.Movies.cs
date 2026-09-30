using KotorCoop.Memory;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>The cinematic staging verbs: warp (also used as the travel half of moviewarp),
/// moviewarp itself, and movieplay.</summary>
public sealed partial class CoopCommandDispatcher
{
    /// <summary>
    /// Asks the plugin to move this instance to another module. Extracted so the staged
    /// cinematic can reuse the EXACT path a typed <c>warp</c> takes — a staging verb that
    /// travels a different way would be measuring its own plumbing.
    /// </summary>
    private void Warp(string module, string waypoint)
    {
        if (_kotor is null || !_kotor.SupportsTravel)
        {
            Reply("warp needs the in-game plugin (no shared block found)");
        }
        else if (_kotor.TryRequestManualTravel(module, waypoint))
        {
            Reply($"warping to {module}...");
        }
        else
        {
            Reply($"could not request warp to {module}");
        }
    }

    /// <summary>
    /// Stages a cinematic onto a module transition (docs/research/cinematics.md §7) — a test
    /// instrument, not a feature, and the only command here that deliberately arranges for the
    /// in-game plugin to stop running for several seconds.
    ///
    /// The warp form is TWO STEPS ON PURPOSE, and the order is the whole point. The plugin
    /// arms the movie names first and answers; only when that answer arrives does the travel
    /// request go out. Filing both at once would race: the plugin's frame does the travel
    /// branch and returns BEFORE it reaches the cheat channel, so a travel filed in the same
    /// tick as its cinematic would warp without one and the movie would sit armed, waiting to
    /// ambush whatever transition came next.
    ///
    /// A refusal therefore stops the whole thing rather than warping anyway — a movie-less
    /// transition proves nothing about the case under investigation.
    /// </summary>
    private void StageMovie(CoopCommand command)
    {
        string movies = command.Password ?? string.Empty;
        string module = command.Address ?? string.Empty;

        if (command.Amount != StageMovieModes.WarpWithMovies)
        {
            SendCheat(new CheatRequest(CheatKinds.StageMovie, Amount: command.Amount, Text: movies));
            return;
        }

        SendCheat(
            new CheatRequest(CheatKinds.StageMovie, Amount: command.Amount, Text: movies),
            onApplied: () =>
            {
                Reply($"staged {movies}; now warping to {module} - the movie plays on the way in");
                Warp(module, string.Empty);
            },
            onRefused: () => Reply($"the cinematic was not staged, so the warp to {module} was NOT attempted"));
    }

    /// <summary>
    /// TEST HOOK: plays <paramref name="resref"/> RIGHT NOW on this instance, through
    /// <c>OdysseyMovies.PlayMovieNow</c> — the same engine entry a mid-module script's own
    /// <c>PlayMovie</c> call reaches, and the one <c>MoviePlayHook</c> watches. Built because
    /// docs/research/cinematics.md §10.7 found no isolated way to fire that hook on the host
    /// without reaching a story trigger: <c>moviewarp</c> only ever writes the TRANSITION
    /// queue, a different engine path entirely. Follows <see cref="ApplyEffect"/>'s pattern:
    /// staging only, no network send here — the capturing hook and the mid-module movie
    /// controller do the actual relay once the movie plays.
    /// </summary>
    private void MoviePlay(string resref)
    {
        if (resref.Length == 0)
        {
            Reply("movieplay: needs a resref, e.g. movieplay 01c");
            return;
        }

        SendCheat(new CheatRequest(CheatKinds.MoviePlay, Text: resref));
    }
}
