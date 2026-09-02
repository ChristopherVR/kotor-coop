using KotorCoop.Memory;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// Roadmap item 14 phase 1's live wiring. Watches THIS instance's own save-identity digest
/// (<see cref="IGameBridge.SaveIdentityDigest"/> — the exact signal
/// <c>ContentCompatibility</c>/<c>SaveDigestCache</c> already compute and exchange every
/// heavy pump, see <c>docs/research/session-save.md</c> §3-4) with <see cref="SaveIdentityWatch"/>,
/// with no peer comparison at all — each role only ever needs to notice its OWN save
/// changing under it:
///
/// <list type="bullet">
/// <item><b>CLIENT.</b> A change is the client saving normally in-game while playing in the
/// adopted world — the "resume tomorrow" trigger. <see cref="ObserveClient"/> captures the
/// character straight into the <see cref="ClientCharacterPersistence"/> sidecar every time,
/// because a save is always safe to persist. The SAME signal would also fire for a
/// client-side mid-session LOAD (design doc §4), but a save and a load are not
/// distinguishable from a content digest alone — auto-rejoin (roadmap item 14 phase 1 item 3)
/// is therefore deliberately NOT implemented here; see
/// <c>docs/research/session-save.md</c>'s "Phase 1 shipped" section.</item>
/// <item><b>HOST.</b> A change — after the digest had already resolved once this connection
/// — is evidence of exactly one thing on the host's own install: it loaded a different save
/// while clients were already playing in the old one. <see cref="ObserveHost"/> returns true
/// exactly once for that edge; the caller announces the refusal line and drops the session,
/// which is the design doc's recommended safe default (refuse first, re-adopt is phase 2).</item>
/// </list>
///
/// PHASE 2 (docs/research/session-save.md's Phase 2 section) answers what phase 1 could not: a
/// save and a load are now distinguishable, via <see cref="ObserveOwnVerdict"/>/
/// <see cref="ObserveRemoteVerdict"/> and the world-load edge <c>KotorCoop.Plugin.WorldLoadRule</c>
/// adds alongside the digest. <c>CoopCommandDispatcher</c> uses the verdict, not the raw digest
/// edge above, to drive <c>client_load_rejoin</c>/<c>host_load_readopt</c> — and now also to
/// decide when the legacy <see cref="ObserveHost"/> refusal itself acts, closing the exact
/// false-positive (an ordinary transition trips the digest edge too) that shipped it OFF by
/// default in phase 1.
/// </summary>
public sealed class SessionSaveWatch(string gameDirectory)
{
    /// <summary>The exact line the host's own screen shows when it detects itself loading a
    /// different save mid-session. Matches roadmap item 14 phase 1's required wording.</summary>
    public const string HostRefusalMessage =
        "co-op: loading a save mid-session is not supported yet - clients must rejoin";

    private SaveIdentityWatchState _watch = SaveIdentityWatchState.Initial;

    /// <summary>The save identity this connection's OWN world started from — the watch's
    /// first established baseline, held for the life of the connection. Recorded into a
    /// captured sidecar's metadata as "which host world this character belongs to" (0 =
    /// unknown, which the preference rule never treats as a mismatch).</summary>
    private uint _sessionBaselineIdentity;

    // ---- roadmap item 14 phase 2: save-vs-load classification ---------------------------
    //
    // A SEPARATE watch pair from `_watch` above, deliberately: `_watch`/`ObserveHost`/
    // `ObserveClient` are phase 1's pure digest-edge detector, unchanged, still driving the
    // "capture on save" and (still flag-gated) legacy refusal paths exactly as shipped. These
    // additional watches feed the SAME digest readings into `SaveOrLoadClassifier`, which
    // needs its own independent edge state to correlate against the world-load serial —
    // running two pure observers over the same input stream is cheap and cannot diverge
    // (`SaveIdentityWatch.Observe` is a deterministic function of its inputs), so this is
    // simpler than threading one shared watch through two different consumers with different
    // needs.

    private SaveIdentityWatchState _ownClassifyWatch = SaveIdentityWatchState.Initial;
    private SerialEdgeWatchState _ownLoadWatch = SerialEdgeWatchState.Initial;
    private readonly SaveOrLoadClassifier _ownClassifier = new();

    /// <summary>The REMOTE peer's digest/load-edge — meaningful only on a CLIENT watching the
    /// HOST's reported <see cref="KotorCoop.Protocol.WorldDigest"/> for <c>host_load_readopt</c>
    /// (design doc §3.2, "have every client run the same auto-rejoin"). A host has many peers
    /// and no single "the remote", so it never calls <see cref="ObserveRemoteVerdict"/>.</summary>
    private SaveIdentityWatchState _remoteClassifyWatch = SaveIdentityWatchState.Initial;
    private SerialEdgeWatchState _remoteLoadWatch = SerialEdgeWatchState.Initial;
    private readonly SaveOrLoadClassifier _remoteClassifier = new();

    private string SavesDirectory => Path.Combine(gameDirectory, "saves");

    /// <summary>A new connection (or none): forget everything, so the next real reading
    /// becomes THIS connection's fresh baseline rather than being compared against a stale
    /// one from whatever ran before. This is also what makes "the load that IS the adoption"
    /// invisible to the phase 2 classifiers — see <c>ClientLoadRejoinRule</c>'s own doc
    /// comment: the first post-Reset reading of everything below becomes a baseline, never a
    /// change, for exactly the same reason <c>_watch</c> already worked this way in phase 1.</summary>
    public void Reset()
    {
        _watch = SaveIdentityWatchState.Initial;
        _sessionBaselineIdentity = 0;

        _ownClassifyWatch = SaveIdentityWatchState.Initial;
        _ownLoadWatch = SerialEdgeWatchState.Initial;
        _ownClassifier.Reset();

        _remoteClassifyWatch = SaveIdentityWatchState.Initial;
        _remoteLoadWatch = SerialEdgeWatchState.Initial;
        _remoteClassifier.Reset();
    }

    /// <summary>
    /// Roadmap item 14 phase 2: classifies THIS instance's own digest+load-edge readings for
    /// this tick. Both roles use this — a host for <c>host_load_readopt</c>/the rewired
    /// <c>session_save_host_refusal</c>, a client for <c>client_load_rejoin</c> — since each
    /// side only ever needs to watch itself for its OWN save/load edge, same as phase 1.
    /// </summary>
    public SaveOrLoadVerdict ObserveOwnVerdict(uint digestNow, uint loadSerialNow, long nowMs) =>
        ObserveOwnVerdict(digestNow, (uint?)loadSerialNow, nowMs);

    /// <summary>
    /// The form the dispatcher feeds: <paramref name="loadSerialNow"/> is null on a tick the
    /// bridge has no plugin block to read the serial from (<c>IWorldStateBridge.TryReadWorldLoadSerial</c>).
    /// Such a tick is skipped whole — neither watch advances — rather than fed a 0 that
    /// <see cref="SerialEdgeWatch"/> would baseline on or edge away from. ⚠ Live 2026-09-02
    /// (wipe → host reload → rejoin): the client's bridge re-scanned for the block while its
    /// own rejoin load was landing, the serial read 0 for those ticks, and the flicker back to
    /// the real value coincided with the adopted world's digest change — a textbook Load
    /// verdict for a load this agent had itself just asked for. That "you loaded a save" fired
    /// a second rejoin, whose load fired a third, four rejoins in 21 s before it converged.
    /// The digest is deliberately not observed on the skipped tick either: a real digest edge
    /// is still there on the next readable tick, and observing it alone would leave a pending
    /// Save the returning serial could then wrongly upgrade.
    /// </summary>
    public SaveOrLoadVerdict ObserveOwnVerdict(uint digestNow, uint? loadSerialNow, long nowMs)
    {
        if (loadSerialNow is null)
        {
            return SaveOrLoadVerdict.None;
        }

        (_ownClassifyWatch, bool digestChanged) = SaveIdentityWatch.Observe(_ownClassifyWatch, digestNow);
        (_ownLoadWatch, bool loadEdge) = SerialEdgeWatch.Observe(_ownLoadWatch, loadSerialNow.Value);
        return _ownClassifier.Observe(nowMs, loadEdge, digestChanged);
    }

    /// <summary>
    /// Roadmap item 14 phase 2, client-only: classifies the HOST's reported digest+load-edge
    /// (<see cref="KotorCoop.Session.CoopClient.RemoteWorldDigest"/>) for <c>host_load_readopt</c>.
    /// </summary>
    /// <param name="remoteLoadEdge">The raw serial edge this tick, before correlation — what
    /// <see cref="KotorCoop.Session.HostWorldReplacedRule"/> watches, because a host load that
    /// leaves the digest unchanged (a reload of the staged save, a Load of an older save) never
    /// becomes a verdict here at all.</param>
    public SaveOrLoadVerdict ObserveRemoteVerdict(
        uint remoteDigestNow, uint remoteLoadSerialNow, long nowMs, out bool remoteLoadEdge)
    {
        // ⚠ No packet yet is not a reading. A fresh CoopClient reports WorldDigest.Empty until
        // the host's first WorldDigestPacket lands; SerialEdgeWatch must not baseline on that
        // 0 — the host's real serial arriving a tick later then reads as a load edge. Live
        // 2026-09-02: a client that had just rejoined after the host's in-world reload saw
        // exactly that edge and rejoined a second time. (SaveIdentityWatch already skips 0 on
        // its own; the serial legitimately starts at 0 on the HOST, so it cannot.)
        if (remoteDigestNow == 0)
        {
            remoteLoadEdge = false;
            return SaveOrLoadVerdict.None;
        }

        (_remoteClassifyWatch, bool digestChanged) = SaveIdentityWatch.Observe(_remoteClassifyWatch, remoteDigestNow);
        (_remoteLoadWatch, remoteLoadEdge) = SerialEdgeWatch.Observe(_remoteLoadWatch, remoteLoadSerialNow);
        return _remoteClassifier.Observe(nowMs, remoteLoadEdge, digestChanged);
    }

    /// <summary>
    /// Host-side observation, once per tick. Returns true exactly on the tick a mid-session
    /// load was detected (an edge, never repeated for the same change) — the caller owns
    /// actually announcing and tearing the session down; this only decides WHEN.
    /// </summary>
    public bool ObserveHost(uint digestNow)
    {
        (bool _, bool changed) = Advance(digestNow);
        return changed;
    }

    /// <summary>
    /// Client-side observation, once per tick. When the digest changed this tick, captures
    /// the character out of <paramref name="currentSaveDirectory"/> into the sidecar and
    /// returns the outcome as a line for the log/screen; returns null on a tick with nothing
    /// to report.
    /// </summary>
    public string? ObserveClient(uint digestNow, string? currentSaveDirectory)
    {
        (bool _, bool changed) = Advance(digestNow);

        if (!changed || currentSaveDirectory is null)
        {
            return null;
        }

        return Capture(currentSaveDirectory);
    }

    /// <summary>
    /// The "OR on clean disconnect" trigger: a best-effort final capture using whatever the
    /// client's currently active save directory is, called once as a session is torn down.
    /// Idempotent with the per-tick capture — if nothing changed since the last one, this
    /// simply re-captures the same character, which costs a little disk I/O and changes
    /// nothing observable.
    /// </summary>
    public string? CaptureOnDisconnect(string? currentSaveDirectory) =>
        currentSaveDirectory is null ? null : Capture(currentSaveDirectory);

    private string? Capture(string currentSaveDirectory) =>
        ClientCharacterPersistence.TryCaptureAndWrite(
            SavesDirectory, currentSaveDirectory, _sessionBaselineIdentity, out string diagnostic)
            ? $"session-saved character: {diagnostic}"
            : $"session-saved character NOT captured: {diagnostic}";

    /// <summary>Runs the pure watch and records the connection's baseline the first time it
    /// establishes. Returns (justEstablished, changed) — <see cref="ObserveHost"/> and
    /// <see cref="ObserveClient"/> only ever care about <c>changed</c>; the tuple exists so
    /// both can share this one place that updates <see cref="_watch"/>.</summary>
    private (bool JustEstablished, bool Changed) Advance(uint digestNow)
    {
        SaveIdentityWatchState before = _watch;
        (SaveIdentityWatchState next, bool changed) = SaveIdentityWatch.Observe(_watch, digestNow);
        _watch = next;

        bool justEstablished = !before.Established && next.Established;
        if (justEstablished)
        {
            _sessionBaselineIdentity = next.Baseline;
        }

        return (justEstablished, changed);
    }
}
