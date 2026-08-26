using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — Content-compatibility judging from this client's side: the install-time
/// digest verdict refined by the in-world comparison (globals catalogue, save
/// identity, current module's GIT) against the host's own broadcast digest.
/// </summary>
public sealed partial class CoopClient
{
    private CompatibilityVerdict? _installVerdict;
    private CompatibilityVerdict? _compatibility;
    private WorldDigest _localWorld = WorldDigest.Empty;
    private WorldDigest _hostWorld = WorldDigest.Empty;
    private long _lastWorldDigestMs;

    /// <summary>Absorbs the transient save-identity mismatch an ordinary module transition
    /// produces while this client and the host cross at slightly different times — see
    /// <see cref="SaveIdentitySettle"/>.</summary>
    private readonly SaveIdentitySettle _saveSettle = new();

    public SessionCapabilities Capabilities => _compatibility?.Allowed ?? SessionCapabilities.All;

    public CompatibilityVerdict? Compatibility => _compatibility;

    /// <summary>
    /// The host's most recently received <see cref="WorldDigest"/> — roadmap item 14 phase 2
    /// (docs/research/session-save.md): unlike <see cref="Compatibility"/> (already a compared
    /// VERDICT), this exposes the raw fields so the dispatcher can run its OWN
    /// <see cref="SaveOrLoadClassifier"/> against the host's <see cref="WorldDigest.SaveIdentity"/>
    /// and <see cref="WorldDigest.LoadSerial"/> — the signal <c>host_load_readopt</c> needs to
    /// tell "the host saved" apart from "the host loaded a different save", which the already-
    /// compared verdict cannot express (it only ever says "mismatched right now", not "changed
    /// via a load"). <see cref="WorldDigest.Empty"/> before the host's first digest arrives.
    /// </summary>
    public WorldDigest RemoteWorldDigest => _hostWorld;

    public void PublishWorldDigest(in WorldDigest digest)
    {
        if (digest == _localWorld)
        {
            return;
        }

        _localWorld = digest;
        RefineCompatibility();
    }

    /// <summary>
    /// Recomputes the client's verdict from the install compare plus the in-world
    /// comparison (globals catalogue, save identity, current module's GIT). Each field
    /// must be resolved on both sides before a difference counts, and the module fields
    /// track the players — so this re-judges itself on every module change, restoring
    /// channels wherever the two GITs agree again.
    /// </summary>
    private void RefineCompatibility()
    {
        if (_installVerdict is not { } install)
        {
            return;
        }

        WorldDigestComparison mismatch = WorldDigest.Compare(_localWorld, _hostWorld);
        mismatch = _saveSettle.Apply(mismatch, SessionClock.NowMs);

        _compatibility = ContentCompatibility.ApplyWorldMismatch(install, mismatch);
    }

    private void HandleWorldDigest(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadWorldDigest(span) is { } hostDigest)
        {
            _hostWorld = hostDigest.Digest;
            RefineCompatibility();
        }
    }
}
