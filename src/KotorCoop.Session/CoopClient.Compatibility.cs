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

    /// <summary>The behaviour-flag comparison against the host's Welcome — see
    /// <see cref="ICapabilitySession.BehaviourWarnings"/>. Computed once, on Welcome:
    /// coop.ini/CLI behaviour never changes mid-session the way in-world facts do, so unlike
    /// <see cref="Compatibility"/> this has no "refine on every module change" step.</summary>
    private BehaviourComparisonResult? _behaviourComparison;

    /// <summary>Runtime overrides adopted from the host for host-authoritative keys that
    /// differed — see <see cref="SessionOverrides"/>.</summary>
    private SessionOverrides _overrides = SessionOverrides.None;

    public BehaviourComparisonResult? BehaviourWarnings => _behaviourComparison;

    public SessionOverrides Overrides => _overrides;

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

    /// <summary>
    /// Compares this client's behaviour digest against the host's (from Welcome) and, for
    /// every host-authoritative key that differs, adopts the host's value into
    /// <see cref="_overrides"/> — docs/research/mod-compatibility.md's "Behaviour-flag
    /// negotiation". The raw differences (host-authoritative or not) are left on
    /// <see cref="_behaviourComparison"/> for <c>BehaviourAnnouncer</c> to turn into
    /// <c>W_CFG</c> lines on screen; adoption itself is logged straight to the console here,
    /// the same way <see cref="HandleWelcome"/> already logs an adopted tick rate a few lines
    /// below this call site.
    /// </summary>
    private void ApplyBehaviourComparison(in BehaviourDigest hostBehaviour)
    {
        BehaviourComparisonResult result = BehaviourCompatibility.Compare(hostBehaviour, _settings.Behaviour);
        _behaviourComparison = result;

        if (result.IsEmpty)
        {
            _overrides = SessionOverrides.None;
            return;
        }

        bool? Adopt(string key)
        {
            foreach (BehaviourDifference diff in result.Differences)
            {
                if (diff.HostAuthoritative && diff.Key == key)
                {
                    bool value = diff.HostValue == "1";
                    Console.Error.WriteLine(
                        $"[coop] adopted host's {key} ({(value ? "on" : "off")}; local setting was "
                        + $"{(diff.YourValue == "1" ? "on" : "off")})");
                    return value;
                }
            }

            return null;
        }

        _overrides = new SessionOverrides(
            PartySync: Adopt("party_sync"),
            EffectSync: Adopt("effect_sync"),
            TriggerRelay: Adopt("trigger_relay"),
            LivenessSync: Adopt("liveness_sync"),
            MapRevealSync: Adopt("map_reveal_sync"),
            ScriptItemSync: Adopt("script_item_sync"),
            DialogueItemSync: Adopt("dialogue_item_sync"),
            DialogueRecruitSync: Adopt("dialogue_recruit_sync"),
            MovieRelay: Adopt("movie_relay"));
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
