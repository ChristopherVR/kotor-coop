using KotorCoop.Protocol;

namespace KotorCoop.Agent;

/// <summary>
/// Turns a behaviour-flag comparison (docs/research/mod-compatibility.md's "Behaviour-flag
/// negotiation", docs/roadmap-seamless.md Tier 1 item 7) into lines a player actually sees —
/// the same job <see cref="CompatibilityAnnouncer"/> does for a content mismatch, through the
/// same trio (stdout, the <c>~</c> console reply line, the in-game overlay).
///
/// Unlike a content mismatch, a behaviour-flag difference never refuses a session and never
/// drops a capability bit — the worst it does is get silently adopted (a client running a
/// different <c>effect_sync</c> than its host, say). "Silently" is the part this fixes: the
/// line is <c>co-op [W_CFG] &lt;key&gt; host=&lt;v&gt; you=&lt;v&gt;</c>, once per key, for the life of
/// the connection — coop.ini/CLI settings do not change mid-session the way in-world facts do,
/// so unlike <see cref="CompatibilityAnnouncer"/>'s <c>ModuleGit</c> case there is nothing here
/// that legitimately clears and needs to be said twice.
/// </summary>
public sealed class BehaviourAnnouncer
{
    private readonly HashSet<string> _announced = [];

    /// <summary>
    /// Diffs <paramref name="result"/> against what has already been said and announces
    /// whatever is new. Null (no session, or a session that has not compared yet) says
    /// nothing.
    /// </summary>
    public void Observe(BehaviourComparisonResult? result, Action<string> announce)
    {
        ArgumentNullException.ThrowIfNull(announce);

        if (result is null)
        {
            return;
        }

        foreach (BehaviourDifference diff in result.Differences)
        {
            if (_announced.Add(diff.Key))
            {
                announce($"co-op [{BehaviourDifference.Tag}] {diff.Text}");
            }
        }
    }

    /// <summary>The session went away: forget everything, so a reconnect (possibly to a
    /// different host, with different settings) re-announces its own comparison.</summary>
    public void Reset() => _announced.Clear();
}
