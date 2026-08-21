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

    private string SavesDirectory => Path.Combine(gameDirectory, "saves");

    /// <summary>A new connection (or none): forget everything, so the next real reading
    /// becomes THIS connection's fresh baseline rather than being compared against a stale
    /// one from whatever ran before.</summary>
    public void Reset()
    {
        _watch = SaveIdentityWatchState.Initial;
        _sessionBaselineIdentity = 0;
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
