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
    private WorldDigest _lastResolvedLocalWorld = WorldDigest.Empty;
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

    /// <summary>Tells a same-world save (the engine's own transition autosave, live 2026-09-03)
    /// apart from a genuinely different save — see <see cref="SaveLineage"/>. Fresh per
    /// connection, so a rejoin is judged from "never agreed" again.</summary>
    private readonly SaveLineage _saveLineage = new();

    /// <summary>The GIT counterpart — see <see cref="ModuleGitLineage"/>.</summary>
    private readonly ModuleGitLineage _gitLineage = new();

    /// <summary>True from a raw save-identity disagreement <see cref="SaveLineage"/> read as a
    /// same-world save until the identities next agree — the dispatcher puts one line on
    /// screen per episode, so a crossing that used to cost a rejoin is still visible in the
    /// log as the case it now handles.</summary>
    public bool SaveLineageAbsorbing => _saveLineage.Absorbing;

    /// <summary>True once this connection has seen the two save identities agree (or started
    /// from a carried-over lineage seed) — the only proof that a later save-identity drift is
    /// the same world. <see cref="PersistentSaveMismatchRule"/> re-adopts an unproven one.</summary>
    public bool SaveLineageAgreed => _saveLineage.HasAgreed;

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

        // Unavailable transition samples must not erase the last known namespace. A
        // fresh epoch with an empty module is inconclusive until its label is resolved.
        if (!_lastResolvedLocalWorld.IsEmpty &&
            (_lastResolvedLocalWorld.LoadSerial != digest.LoadSerial ||
             (digest.Module.Length > 0 && digest.NativeWorldEpoch != 0 &&
              _lastResolvedLocalWorld.NativeWorldEpoch != 0 &&
              _lastResolvedLocalWorld.NativeWorldEpoch != digest.NativeWorldEpoch &&
              string.Equals(_lastResolvedLocalWorld.Module, digest.Module, StringComparison.OrdinalIgnoreCase))))
            InvalidateCreatureLivenessForLocalLoad();
        if (digest.Module.Length > 0 && digest.NativeWorldEpoch != 0)
            _lastResolvedLocalWorld = digest;
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
        mismatch = _gitLineage.Apply(
            mismatch, _localWorld, _hostWorld, identity => identity == _adoptedSaveIdentity);
        mismatch = _saveLineage.Apply(mismatch, _localWorld, _hostWorld);
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

        // ⚠ NOT Console.Error.WriteLine here (fixed 2026-08-27): that reached nobody on the
        // rig — not the --log file, not the chat overlay, only a console window that may not
        // exist. BehaviourAnnouncer already receives this exact result via _behaviourComparison
        // (AnnounceCompatibilityChanges below), so it announces the adoption itself, through the
        // same trio the W_CFG lines already use, once it observes a HostAuthoritative
        // difference. See BehaviourAnnouncer.Observe.
        bool? Adopt(string key)
        {
            foreach (BehaviourDifference diff in result.Differences)
            {
                if (diff.HostAuthoritative && diff.Key == key)
                {
                    return diff.HostValue == "1";
                }
            }

            return null;
        }

        // A scaling SETTING travels as the text a player reads ("auto" / "150" —
        // PlayerCountScalingRule.Describe) and is parsed back the same lenient way coop.ini
        // is; the parse cannot fail on text Describe produced, but the fallback is still
        // "adopt nothing" rather than a made-up number.
        int? AdoptSetting(string key)
        {
            foreach (BehaviourDifference diff in result.Differences)
            {
                if (diff.HostAuthoritative && diff.Key == key)
                {
                    int parsed = PlayerCountScalingRule.ParseSetting(diff.HostValue, int.MinValue);
                    return parsed == int.MinValue ? null : parsed;
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
            MovieRelay: Adopt("movie_relay"),
            EnemyHealth: AdoptSetting("enemy_health"),
            EnemyDamage: AdoptSetting("enemy_damage"),
            XpRate: AdoptSetting("xp_rate"),
            StoreStock: AdoptSetting("store_stock"),
            StorePrices: AdoptSetting("store_prices"),
            InventoryPersonal: Adopt("inventory_mode"),
            CreditsPersonal: Adopt("credits_mode"));
    }

    private void HandleWorldDigest(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadWorldDigest(span) is { } hostDigest)
        {
            lock (_gate)
            {
            if (!_hostWorld.IsEmpty && hostDigest.Digest.LoadSerial != _hostWorld.LoadSerial
                && !LivenessAdmissionRule.IsNewer(hostDigest.Digest.LoadSerial, _hostWorld.LoadSerial)) return;
            if (!_hostWorld.IsEmpty && hostDigest.Digest.NativeWorldEpoch != _hostWorld.NativeWorldEpoch
                && !LivenessAdmissionRule.IsNewer(hostDigest.Digest.NativeWorldEpoch, _hostWorld.NativeWorldEpoch)) return;
            if (_hostWorld.LoadSerial != hostDigest.Digest.LoadSerial)
            {
                Flags.Prune(KotorCoop.Memory.ObjectKeyedFlags.IsObjectKeyed);
                _flagSender.Clear();
            }
            _hostWorld = hostDigest.Digest;
            }
            RefineCompatibility();
        }
    }
}
