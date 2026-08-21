namespace KotorCoop.Protocol;

/// <summary>
/// Debounces the SAVE-IDENTITY half of a <see cref="WorldDigestComparison"/> so the LAST
/// stretch of an ordinary module transition does not silence combat.
///
/// WHY THIS EXISTS (round 54, 2026-08-21). <see cref="WorldDigest.Compare"/> now judges a
/// save-identity mismatch only once both peers report the SAME module — see its own doc
/// comment for why comparing it while they are apart cannot tell "still travelling" from "a
/// genuinely different save". That gate closes most of the race, but not the tail: the
/// instant both sides agree on the module, each one's own
/// <see cref="KotorCoop.Memory.SaveDigestCache"/> may still be holding the PRE-transition
/// digest for up to its own 2-second re-stat interval (the identity job runs off the file's
/// on-disk stamp, not the module read directly from engine memory, so the two do not update
/// in lockstep). For that residual window one side's digest can still legitimately disagree
/// with the other's even though <c>sameModule</c> is already true, which
/// <see cref="ContentCompatibility.ApplyWorldMismatch"/> would otherwise drop
/// <see cref="ContentCompatibility.ObjectIdKeyed"/> (enemies, doors, ground items, party)
/// over exactly like a real content-drifted save would.
///
/// Observed live crossing <c>tar_m10aa</c> (docs/research/shared-state-audit.md, round 53):
/// <c>[W_SAVE] different save: enemies, doors, items and party are off</c> fired on both
/// instances right after the warp landed and held for several minutes — neither save had
/// ever visited the module, so the two installs' predicted ids were identical and the
/// disagreement was purely the crossing's own timing, not real drift. <c>console
/// 'reconnect'</c> cleared it instantly by forcing a fresh comparison once both sides had
/// settled — the same recovery this class gives for free, without a player having to know to
/// ask for it.
///
/// THE RULE: honor a save-identity mismatch only once it has been observed continuously for
/// <see cref="SettleMs"/> milliseconds. Agreement is trusted the instant it is seen — there is
/// no reason to delay RESTORING a channel, only reason to delay taking one away — so a single
/// clean tick resets the timer immediately and the very next comparison can already read
/// clean. A genuinely different save stays mismatched on every tick and is still caught, just
/// <see cref="SettleMs"/> later than before; a same-save crossing self-heals within the
/// window and never reaches a player as a dropped channel at all.
///
/// Per-peer, per-side state — the host holds one instance per connected peer (the crossing is
/// judged against each peer's own digest independently) and each client holds one for its
/// view of the host. Never shared across peers or reused after a peer leaves.
/// </summary>
public sealed class SaveIdentitySettle
{
    /// <summary>
    /// Long enough to clear <see cref="KotorCoop.Memory.SaveDigestCache"/>'s own up-to-2-second
    /// re-stat interval on both sides plus the 1-second wire cadence each direction, with
    /// margin for a slower follow across a transition; short enough that a genuine mismatch is
    /// still caught within a few seconds rather than played through blind.
    /// </summary>
    public const long DefaultSettleMs = 5000;

    private readonly long _settleMs;
    private bool _pending;
    private long _sinceMs;

    public SaveIdentitySettle(long settleMs = DefaultSettleMs)
    {
        _settleMs = settleMs;
    }

    /// <summary>
    /// Feeds one raw observation and returns the debounced comparison. Every field other than
    /// <see cref="WorldDigestComparison.SaveIdentityMismatch"/> passes through unchanged — the
    /// module GIT already has its own guard (it is only ever judged while both peers report
    /// the SAME module, so it cannot see this race at all) and the globals catalogue is not
    /// module-scoped, so it carries no transition edge to debounce.
    /// </summary>
    public WorldDigestComparison Apply(WorldDigestComparison raw, long nowMs)
    {
        if (!raw.SaveIdentityMismatch)
        {
            _pending = false;
            return raw;
        }

        if (!_pending)
        {
            _pending = true;
            _sinceMs = nowMs;
            return raw with { SaveIdentityMismatch = false };
        }

        return nowMs - _sinceMs < _settleMs
            ? raw with { SaveIdentityMismatch = false }
            : raw;
    }

    /// <summary>Forgets any in-flight mismatch — a fresh session (a reconnect, a new peer)
    /// must not inherit a timer started against a peer it was never compared to.</summary>
    public void Reset()
    {
        _pending = false;
    }
}
