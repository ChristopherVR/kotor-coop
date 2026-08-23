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
/// lockstep, AND — the live-verified round this comment documents — neither signal is
/// reliably FIRST. The load edge is written the instant the plugin notices an unannounced
/// world teardown (<c>PluginHost.OnFrame</c>, only once the engine's own multi-step teardown
/// actually runs); the digest is a re-stat of a file on disk, cached and refreshed on its own
/// schedule (<c>SaveDigestCache</c>, "up to a 2-second re-stat interval").
///
/// docs/research/session-save.md's round 86 live pass found the ORDER genuinely reverses for
/// the <c>loadgame</c> console verb's own mechanism: <c>PluginMenuLoader.TryStageSaveAsAutosave</c>
/// rewrites the watched save file on the AGENT'S OWN THREAD, synchronously, BEFORE it ever
/// asks the engine to load — so the digest edge routinely lands a full second or more before
/// the load-serial edge, which only fires once the engine's own teardown (message-box
/// precondition, <c>DestroyServer</c>+<c>UnloadModule</c>+<c>CreateServer</c>) actually runs.
/// The ORIGINAL version of this class only ever looked backward — a digest change correlated
/// against a load edge that had ALREADY happened — so for this reversed ordering the digest
/// edge was judged (silently, as a plain <see cref="SaveOrLoadVerdict.Save"/> — nothing
/// downstream reacts differently to <c>Save</c> vs <c>None</c>, which is exactly why this
/// stayed invisible) and CONSUMED before the load edge it belonged to ever arrived, so the
/// classifier never produced a <see cref="SaveOrLoadVerdict.Load"/> at all: this is
/// <c>client_load_rejoin</c>'s round-86 "the classifier never fires" bug, not a missing edge —
/// both edges fired, in the wrong-for-this-code order.
///
/// The fix: correlation is now symmetric. A load edge is remembered for
/// <see cref="CorrelationWindowMs"/> and consumed by the next digest change inside it (the
/// original direction, still exactly how a real pause-menu Load — which reads an existing
/// save rather than staging one — is expected to order the two signals). A digest change with
/// no load edge pending is provisionally reported as <see cref="SaveOrLoadVerdict.Save"/> —
/// unchanged from before, and safe because nothing reacts differently to <c>Save</c> than to
/// <c>None</c> — but ALSO remembered for the same window, so a load edge that arrives LATER
/// (the <c>loadgame</c> ordering) still upgrades it to <see cref="SaveOrLoadVerdict.Load"/> on
/// the tick it arrives, even though an earlier tick already reported <c>Save</c> for the same
/// underlying event.
///
/// A load edge that never gets a matching digest change, in EITHER direction, within the
/// window simply expires unconsumed and produces no verdict at all. This mirrors this
/// codebase's own documented, accepted gap (session-save.md Phase 1's NEEDS-LIVE item 1): a
/// genuine Load Game action might not touch the watched save file at all if the engine's own
/// load path performs no write of its own, in which case there is nothing here to classify.
/// </summary>
public sealed class SaveOrLoadClassifier(long correlationWindowMs = SaveOrLoadClassifier.DefaultCorrelationWindowMs)
{
    /// <summary>Generous margin over <c>SaveDigestCache</c>'s documented up-to-2-second
    /// re-stat interval — the same order of magnitude <see cref="KotorCoop.Protocol.SaveIdentitySettle.DefaultSettleMs"/>
    /// uses for a structurally similar "let a related signal catch up" wait.</summary>
    public const long DefaultCorrelationWindowMs = 5000;

    private readonly long _correlationWindowMs = correlationWindowMs;
    private long? _pendingLoadEdgeAtMs;

    /// <summary>An unmatched digest change, remembered so a load edge arriving LATER (the
    /// <c>loadgame</c> verb's own ordering — see this class's own doc comment) can still
    /// upgrade what was provisionally reported as a <see cref="SaveOrLoadVerdict.Save"/> into
    /// a <see cref="SaveOrLoadVerdict.Load"/>.</summary>
    private long? _pendingDigestEdgeAtMs;

    /// <summary>
    /// Feeds one tick's two raw edges and returns the verdict.
    /// </summary>
    public SaveOrLoadVerdict Observe(long nowMs, bool loadEdge, bool digestChanged)
    {
        if (loadEdge)
        {
            // A digest change already sitting unmatched, within the window: THIS load edge is
            // what it was waiting for — upgrade it to Load now, on this tick, even though an
            // earlier tick already (provisionally, silently) reported it as a Save. Consumes
            // both pendings, so a later, unrelated edge cannot reuse either one.
            if (_pendingDigestEdgeAtMs is { } digestAt && nowMs - digestAt <= _correlationWindowMs)
            {
                _pendingDigestEdgeAtMs = null;
                _pendingLoadEdgeAtMs = null;
                return SaveOrLoadVerdict.Load;
            }

            _pendingLoadEdgeAtMs = nowMs;
        }

        if (!digestChanged)
        {
            return SaveOrLoadVerdict.None;
        }

        // A digest change consumes whatever load edge is currently pending, so a later,
        // unrelated digest change cannot reuse a stale one.
        bool withinWindow = _pendingLoadEdgeAtMs is { } loadAt && nowMs - loadAt <= _correlationWindowMs;
        _pendingLoadEdgeAtMs = null;

        if (withinWindow)
        {
            _pendingDigestEdgeAtMs = null;
            return SaveOrLoadVerdict.Load;
        }

        // No load edge to correlate against YET — report the provisional verdict every other
        // caller in this codebase already expects (Save, indistinguishable downstream from
        // None), but remember this edge so a load edge arriving within the window on a LATER
        // tick can still find it and upgrade the verdict.
        _pendingDigestEdgeAtMs = nowMs;
        return SaveOrLoadVerdict.Save;
    }

    /// <summary>Forgets any pending load or digest edge — a fresh connection must not let an
    /// edge observed against a PREVIOUS session's baseline correlate with this one's first
    /// reading (which is itself never a "change" at all — see <see cref="SerialEdgeWatch.Observe"/>/
    /// <see cref="SaveIdentityWatch.Observe"/> — but Reset keeps every watch's lifecycle
    /// visibly identical rather than relying on that alone).</summary>
    public void Reset()
    {
        _pendingLoadEdgeAtMs = null;
        _pendingDigestEdgeAtMs = null;
    }
}
