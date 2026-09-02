namespace KotorCoop.Protocol;

/// <summary>
/// Decides whether a save-identity mismatch between two peers is a DIFFERENT SAVE or the same
/// world after one side's engine wrote it to disk — the second half of the
/// <see cref="WorldDigestComparison.SaveIdentityMismatch"/> guard, after
/// <see cref="SaveIdentitySettle"/>'s timing debounce.
///
/// WHY THIS EXISTS. Measured live 2026-09-03 on the first real door crossing (`crossdoor
/// end_door10`, end_m01aa → end_m01ab, docs/research/cinematics.md §8.3c): KOTOR's own engine
/// autosaves through every module transition, into the very <c>000001 - AUTOSAVE</c> directory
/// <c>SaveDigest.FindSessionSave</c> digests, and each instance writes ITS OWN counters into that
/// autosave — the host had dealt a few more runtime ids than the client (phantoms, stand-ins,
/// spawned creatures), so the two identities disagreed for good once both stood in end_m01ab,
/// <see cref="SaveIdentitySettle"/> honoured it after its 5 s, and the client took an automatic
/// rejoin (world adoption, ~2 s) that closed the module-entry cutscene on the host mid-way
/// ("the owner ended the conversation; closing"). Nothing about the two worlds had diverged:
/// the module's own objects had the same ids on both sides (trigger 0x26 fired natively on
/// each), the id-keyed channels were fine, and a rejoin would have been needed after EVERY door
/// for the rest of the session.
///
/// THE RULE. A save-identity mismatch is honoured when the two identities have never agreed
/// (a joining peer playing a different save — the case the digest exists for), or when either
/// side's <see cref="WorldDigest.LoadSerial"/> has moved since they last agreed (someone LOADED
/// a save, which really can put a different world under the same module name; the plugin's
/// <c>WorldLoadRule</c> is what counts those). A mismatch that appears after agreement with no
/// load edge on either side can only have come from a SAVE — a transition autosave, or a manual
/// save the digest happens to track — and a save does not change which world anyone is playing,
/// so it is reported as agreement. Agreement is re-recorded whenever it is seen, so a later
/// load is judged against the most recent point the two provably matched.
///
/// Per-peer, per-side state, like <see cref="SaveIdentitySettle"/>: the host holds one per
/// connected peer, each client one for the host; a reconnecting peer gets a fresh instance and
/// therefore a fresh "never agreed" start, which is exactly what a rejoin should be judged on.
/// </summary>
public sealed class SaveLineage
{
    private bool _agreed;
    private uint _localLoadAtAgreement;
    private uint _remoteLoadAtAgreement;

    /// <summary>True once the two identities have been seen agreeing in the same module.</summary>
    public bool HasAgreed => _agreed;

    /// <summary>True from the first mismatch this rule read as a same-world save until the
    /// next recorded agreement — a level, not a per-call verdict, so a caller announcing it
    /// says it once per episode rather than once per module-name flicker mid-transition.</summary>
    public bool Absorbing { get; private set; }

    /// <summary>
    /// Feeds one comparison together with the two digests it was computed from and returns it
    /// with <see cref="WorldDigestComparison.SaveIdentityMismatch"/> cleared when the mismatch
    /// is a same-lineage save. Every other field passes through untouched.
    /// </summary>
    public WorldDigestComparison Apply(WorldDigestComparison raw, in WorldDigest local, in WorldDigest remote)
    {
        if (!raw.SaveIdentityMismatch)
        {
            // Compare() reports no mismatch both when the identities agree and when one side
            // has not resolved its own yet (or the two are in different modules). Only a real,
            // same-module agreement of two resolved identities is a lineage checkpoint.
            if (raw.Module.Length > 0 && local.SaveIdentity != 0 && remote.SaveIdentity != 0
                && local.SaveIdentity == remote.SaveIdentity)
            {
                _agreed = true;
                Absorbing = false;
                _localLoadAtAgreement = local.LoadSerial;
                _remoteLoadAtAgreement = remote.LoadSerial;
            }

            return raw;
        }

        if (!_agreed)
        {
            return raw;
        }

        if (local.LoadSerial != _localLoadAtAgreement || remote.LoadSerial != _remoteLoadAtAgreement)
        {
            return raw;
        }

        Absorbing = true;
        return raw with { SaveIdentityMismatch = false };
    }
}
