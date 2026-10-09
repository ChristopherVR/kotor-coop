namespace KotorCoop.Protocol;

/// <summary>
/// One coop.ini/CLI behaviour key two peers disagreed on. Always a WARNING — unlike
/// <see cref="ContentCompatibility"/>, a behaviour-flag difference never refuses a session and
/// never drops a capability bit; the worst it does is get adopted (see
/// <see cref="HostAuthoritative"/>) so a host-driven channel stays coherent.
/// </summary>
/// <param name="Key">The coop.ini/CLI key name, exactly as it appears in coop.ini (or, for the
/// two keys with none, the CLI flag's own name) — what a player would grep for.</param>
/// <param name="HostValue">The host's value, as the text a player reads.</param>
/// <param name="YourValue">The other peer's (non-host) value, as the text a player reads.
/// Named "Your" because the line this produces (<see cref="Text"/>) is written from that
/// peer's own point of view — the same convention <c>CompatibilityAnnouncer</c> already uses
/// for a channel that drops locally.</param>
/// <param name="HostAuthoritative">Whether this key belongs to a channel the host already
/// drives by design (party, effects, triggers, liveness, map reveal, the three item/movie
/// relays) — see docs/research/mod-compatibility.md's "Behaviour-flag negotiation" section for
/// the full list and why each one is on it. A CLIENT reading a difference on one of these
/// keys adopts the host's value at runtime (<see cref="KotorCoop.Session.SessionOverrides"/>);
/// a HOST reading the same difference does not act on it — the host's own value never
/// changes.</param>
public readonly record struct BehaviourDifference(
    string Key, string HostValue, string YourValue, bool HostAuthoritative)
{
    /// <summary>Stable short tag, matching the <c>W_XXX</c>/<c>E_XXX</c> convention
    /// <see cref="CompatibilityNote.Tag"/> already established.</summary>
    public const string Tag = "W_CFG";

    /// <summary>The body of the line, WITHOUT the tag — <c>&lt;key&gt; host=&lt;v&gt;
    /// you=&lt;v&gt;</c>. <c>BehaviourAnnouncer</c> prefixes <see cref="Tag"/> the same way
    /// <c>CompatibilityAnnouncer</c> prefixes <c>CompatibilityNote.Tag</c> onto
    /// <c>CompatibilityNote.Text</c>, so the two announcers produce the same
    /// <c>co-op [TAG] ...</c> shape on screen.</summary>
    public string Text => $"{Key} host={HostValue} you={YourValue}";
}

/// <summary>The outcome of comparing two peers' <see cref="BehaviourDigest"/>s: every key that
/// differed. Empty when the two installs agree on everything, which is the common case.</summary>
public sealed record BehaviourComparisonResult(IReadOnlyList<BehaviourDifference> Differences)
{
    public static readonly BehaviourComparisonResult None = new([]);

    public bool IsEmpty => Differences.Count == 0;
}

/// <summary>
/// Judges two peers' behaviour-flag digests. Pure and deterministic, mirroring
/// <see cref="ContentCompatibility"/>'s shape: same inputs produce the same result on either
/// peer, so host and client never have to exchange a verdict, only the raw digests.
///
/// Every call is in HOST-then-PEER order regardless of which side is actually calling it —
/// <see cref="Compare"/>'s <c>host</c> parameter is always the session's host, so the "host="
/// half of every <see cref="BehaviourDifference.Text"/> line means the same thing whether the
/// host or a client printed it (see <see cref="CoopHost.HandleHello"/> and
/// <see cref="CoopClient.HandleWelcome"/>, the two call sites).
/// </summary>
public static class BehaviourCompatibility
{
    public static BehaviourComparisonResult Compare(in BehaviourDigest host, in BehaviourDigest peer)
    {
        // Copied out of the `in` parameters: a local function cannot close over a ref/in
        // parameter, only a plain local.
        BehaviourDigest hostDigest = host;
        BehaviourDigest peerDigest = peer;

        List<BehaviourDifference> diffs = [];

        void CompareFlag(BehaviourFlags bit, string key, bool hostAuthoritative)
        {
            bool hostOn = hostDigest.Has(bit);
            bool peerOn = peerDigest.Has(bit);
            if (hostOn != peerOn)
            {
                diffs.Add(new BehaviourDifference(key, Bool(hostOn), Bool(peerOn), hostAuthoritative));
            }
        }

        // Order matches BehaviourFlags' bit order — not load-bearing, just easier to audit
        // against that enum's own doc comments.
        CompareFlag(BehaviourFlags.LocalCombat, "local_combat", hostAuthoritative: false);
        CompareFlag(BehaviourFlags.CompanionLocalCombat, "companion_local_combat", hostAuthoritative: false);
        CompareFlag(BehaviourFlags.PartyFastPump, "party_fast_pump", hostAuthoritative: false);
        CompareFlag(BehaviourFlags.PartySync, "party_sync", hostAuthoritative: true);
        CompareFlag(BehaviourFlags.ClientDoorRefusal, "client_door_refusal", hostAuthoritative: false);
        CompareFlag(BehaviourFlags.TransitionAnnounce, "transition_announce", hostAuthoritative: false);
        CompareFlag(BehaviourFlags.ClientLoadRejoin, "client_load_rejoin", hostAuthoritative: false);
        CompareFlag(BehaviourFlags.HostLoadReadopt, "host_load_readopt", hostAuthoritative: false);
        CompareFlag(BehaviourFlags.EffectSync, "effect_sync", hostAuthoritative: true);
        CompareFlag(BehaviourFlags.TriggerRelay, "trigger_relay", hostAuthoritative: true);
        CompareFlag(BehaviourFlags.ClientEncounters, "client_encounters", hostAuthoritative: false);
        CompareFlag(BehaviourFlags.DialogueItemSync, "dialogue_item_sync", hostAuthoritative: true);
        CompareFlag(BehaviourFlags.DialogueRecruitSync, "dialogue_recruit_sync", hostAuthoritative: true);
        CompareFlag(BehaviourFlags.ScriptItemSync, "script_item_sync", hostAuthoritative: true);
        CompareFlag(BehaviourFlags.MovieRelay, "movie_relay", hostAuthoritative: true);
        CompareFlag(BehaviourFlags.LivenessSync, "liveness_sync", hostAuthoritative: true);
        CompareFlag(BehaviourFlags.MapRevealSync, "map_reveal_sync", hostAuthoritative: true);
        CompareFlag(BehaviourFlags.RequireIdenticalSave, "require_identical_save", hostAuthoritative: false);
        CompareFlag(BehaviourFlags.LocalVarSync, "local_var_sync", hostAuthoritative: false);
        CompareFlag(BehaviourFlags.InventoryPersonal, "inventory_mode", hostAuthoritative: true);
        CompareFlag(BehaviourFlags.CreditsPersonal, "credits_mode", hostAuthoritative: true);
        CompareFlag(BehaviourFlags.CorpseLootPersonal, "corpse_loot", hostAuthoritative: true);
        CompareFlag(BehaviourFlags.Pvp, "pvp", hostAuthoritative: true);

        if (host.InterpolationMs != peer.InterpolationMs)
        {
            diffs.Add(new BehaviourDifference(
                "interp_ms", host.InterpolationMs.ToString(), peer.InterpolationMs.ToString(),
                HostAuthoritative: false));
        }

        if (!string.Equals(host.FollowLead, peer.FollowLead, StringComparison.OrdinalIgnoreCase))
        {
            diffs.Add(new BehaviourDifference(
                "follow_lead", host.FollowLead, peer.FollowLead, HostAuthoritative: false));
        }

        // The five player-count scaling SETTINGS (docs/research/player-count-scaling.md). All
        // HOST-AUTHORITATIVE: the host's engine is where enemy hit points are set, where the
        // XP delta is scaled before the max-merge and where store stock is topped up, so a
        // client running a different number would only disagree with what it is shown — and a
        // client that fights a released enemy through its own engine (local_combat) must scale
        // that enemy's damage by the same percent the host does. Values travel as the text a
        // player reads ("auto" / "150"); SessionOverrides parses them back.
        CompareSetting(host.EnemyHealth, peer.EnemyHealth, "enemy_health");
        CompareSetting(host.EnemyDamage, peer.EnemyDamage, "enemy_damage");
        CompareSetting(host.XpRate, peer.XpRate, "xp_rate");
        CompareSetting(host.StoreStock, peer.StoreStock, "store_stock");
        CompareSetting(host.StorePrices, peer.StorePrices, "store_prices");

        return diffs.Count == 0 ? BehaviourComparisonResult.None : new BehaviourComparisonResult(diffs);

        void CompareSetting(int hostSetting, int peerSetting, string key)
        {
            if (hostSetting != peerSetting)
            {
                diffs.Add(new BehaviourDifference(
                    key,
                    PlayerCountScalingRule.Describe(hostSetting),
                    PlayerCountScalingRule.Describe(peerSetting),
                    HostAuthoritative: true));
            }
        }
    }

    private static string Bool(bool value) => value ? "1" : "0";
}
