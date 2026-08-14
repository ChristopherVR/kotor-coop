namespace KotorCoop.Agent;

/// <summary>
/// What a staging cheat has arranged to do once the plugin answers it — and what to do
/// instead if the plugin says no.
///
/// WHY THIS IS NOT JUST A LAMBDA IN A FIELD. Exactly one command needs a second step
/// (<c>moviewarp</c>: stage the cinematic, then travel), and the ORDER is the whole design.
/// The plugin's frame takes the travel branch and returns before it ever reaches the cheat
/// channel, so a travel filed in the same tick as its cinematic would warp with no movie
/// while the movie sat armed, waiting to attach itself to whatever transition came next.
/// The rule that prevents that is small, and it is all about the failure cases:
///
/// <list type="bullet">
/// <item>the follow-up runs <b>once</b>, or the same warp is requested twice;</item>
/// <item>a <b>refusal or a timeout</b> runs the other branch, so "the movie was refused but
///   we travelled anyway" cannot happen — a movie-less transition is a test that has already
///   been run and proves nothing about the case under investigation;</item>
/// <item>filing a <b>new</b> cheat abandons the old follow-up, because only one request is in
///   flight and its answer is now unobtainable.</item>
/// </list>
///
/// Small enough to hold in your head, and consequential enough that it is worth asserting
/// rather than describing — which is what this type exists for.
/// </summary>
public sealed class CheatFollowUp
{
    private Action? _applied;
    private Action? _refused;

    /// <summary>True while something is waiting on an answer.</summary>
    public bool IsArmed => _applied is not null || _refused is not null;

    /// <summary>Arms both branches, replacing whatever was armed before.</summary>
    public void Arm(Action? onApplied, Action? onRefused)
    {
        _applied = onApplied;
        _refused = onRefused;
    }

    /// <summary>Abandons both branches without running either.</summary>
    public void Clear() => Arm(null, null);

    /// <summary>
    /// Hands back the branch for this answer and clears BOTH, so nothing can run twice or
    /// leak into the next request. Returns null when nothing was armed, which is the case on
    /// every cheat except the staged cinematic.
    /// </summary>
    public Action? Take(bool applied)
    {
        Action? next = applied ? _applied : _refused;
        Clear();
        return next;
    }
}
