namespace KotorCoop.Session;

/// <summary>
/// A monotonic-counter edge detector — the same shape as <see cref="SaveIdentityWatchState"/>/
/// <see cref="SaveIdentityWatch"/> minus that type's "0 = unresolved" special case, because a
/// world-load serial (<c>KotorCoop.Plugin.WorldLoadRule</c>, <c>SharedBlock.OffsetWorldLoadSerial</c>)
/// legitimately starts at 0 — "no load observed yet this process" is a real, meaningful
/// baseline, not an unknown reading to skip past.
/// </summary>
public readonly record struct SerialEdgeWatchState(uint Baseline, bool Established)
{
    public static SerialEdgeWatchState Initial => new(0, false);
}

/// <summary>Pure, stateless observation function for <see cref="SerialEdgeWatchState"/>.</summary>
public static class SerialEdgeWatch
{
    /// <returns>The next state, and whether THIS call is the edge. The first reading after
    /// <see cref="SerialEdgeWatchState.Initial"/> (a fresh connection, per <c>SessionSaveWatch.Reset</c>)
    /// becomes the baseline rather than a change — this is what makes "the load that IS the
    /// adoption" invisible: the very first serial this connection ever reads, whatever it is,
    /// is simply where counting starts.</returns>
    public static (SerialEdgeWatchState Next, bool Changed) Observe(SerialEdgeWatchState current, uint serialNow)
    {
        if (!current.Established)
        {
            return (new SerialEdgeWatchState(serialNow, true), false);
        }

        if (serialNow == current.Baseline)
        {
            return (current, false);
        }

        return (new SerialEdgeWatchState(serialNow, true), true);
    }
}

/// <summary>What one save-identity-digest edge turned out to be, once correlated against a
/// world-load edge. <see cref="None"/> is not a real outcome — <see cref="SaveOrLoadClassifier.Observe"/>
/// only ever returns it on a tick with no digest change to judge at all.</summary>
public enum SaveOrLoadVerdict
{
    None,
    Save,
    Load,
}

/// <summary>
/// Turns "my save-identity digest changed" (already detected by <see cref="SaveIdentityWatch"/>,
/// which cannot by itself tell a save from a load — KOTOR autosaves through every ordinary
/// module transition too, shared-state-audit.md round 53's live false-positive) into a verdict,
/// by correlating it against the world-load edge <see cref="SerialEdgeWatch"/> exposes.
///
/// THE CORRELATION WINDOW, and why one is needed at all: the two signals do not update in
/// lockstep. The load edge is written the instant the plugin notices an unannounced world
/// teardown (PluginHost.OnFrame, every frame); the digest is a re-stat of a file on disk,
/// cached and refreshed on its own schedule (<c>SaveDigestCache</c>, "up to a 2-second re-stat
/// interval" per <c>SaveIdentitySettle</c>'s own doc comment) — so a load edge this tick and
/// the digest catching up to it a tick or two later are the SAME event, not two. A load edge
/// is therefore remembered for <see cref="CorrelationWindowMs"/> and consumed by the next
/// digest change that arrives inside it; a digest change with no load edge in the window
/// (recent or current) is a save.
///
/// Deliberately NOT the other direction: a load edge that never gets a matching digest change
/// within the window simply expires unconsumed and produces no verdict at all. This mirrors
/// this codebase's own documented, accepted gap (session-save.md Phase 1's NEEDS-LIVE item 1):
/// a genuine Load Game action might not touch the watched save file at all if the engine's own
/// load path performs no write of its own, in which case there is nothing here to classify —
/// exactly as true before this class existed as it is after. What THIS class fixes is
/// classifying the edges that DO produce a digest change; it does not claim to catch every load.
/// </summary>
public sealed class SaveOrLoadClassifier(long correlationWindowMs = SaveOrLoadClassifier.DefaultCorrelationWindowMs)
{
    /// <summary>Generous margin over <c>SaveDigestCache</c>'s documented up-to-2-second
    /// re-stat interval — the same order of magnitude <see cref="KotorCoop.Protocol.SaveIdentitySettle.DefaultSettleMs"/>
    /// uses for a structurally similar "let a related signal catch up" wait.</summary>
    public const long DefaultCorrelationWindowMs = 5000;

    private readonly long _correlationWindowMs = correlationWindowMs;
    private long? _pendingLoadEdgeAtMs;

    /// <summary>
    /// Feeds one tick's two raw edges and returns the verdict. <paramref name="loadEdge"/> is
    /// recorded (refreshing the pending window) regardless of <paramref name="digestChanged"/>;
    /// a digest change consumes whatever load edge is currently pending, so a later, unrelated
    /// digest change cannot reuse a stale one.
    /// </summary>
    public SaveOrLoadVerdict Observe(long nowMs, bool loadEdge, bool digestChanged)
    {
        if (loadEdge)
        {
            _pendingLoadEdgeAtMs = nowMs;
        }

        if (!digestChanged)
        {
            return SaveOrLoadVerdict.None;
        }

        bool withinWindow = _pendingLoadEdgeAtMs is { } at && nowMs - at <= _correlationWindowMs;
        _pendingLoadEdgeAtMs = null;

        return withinWindow ? SaveOrLoadVerdict.Load : SaveOrLoadVerdict.Save;
    }

    /// <summary>Forgets any pending load edge — a fresh connection must not let a load edge
    /// observed against a PREVIOUS session's baseline correlate with this one's first digest
    /// reading (which is itself never a "change" at all — see <see cref="SerialEdgeWatch.Observe"/> —
    /// but Reset keeps the two watches' lifecycles visibly identical rather than relying on
    /// that alone).</summary>
    public void Reset() => _pendingLoadEdgeAtMs = null;
}
