namespace KotorCoop.Protocol;

/// <summary>
/// Which coop.ini/CLI behaviour flags <see cref="BehaviourDigest"/> carries — one bit each,
/// fixed order (never renumber a shipped bit; add new ones above <see cref="_NextBit"/> and
/// bump <see cref="BehaviourDigest.CurrentScheme"/> if the shape ever needs to change instead
/// of just growing).
///
/// This is the roadmap-seamless.md Tier 1 item 7 set: every flag here gates a wire channel or
/// a locally-visible behaviour difference big enough that two peers silently disagreeing on it
/// is worth a line on screen. Read docs/research/mod-compatibility.md's "Behaviour-flag
/// negotiation" section for the policy this feeds (WARN always; ADOPT the host's value for the
/// channels marked host-authoritative below).
/// </summary>
[Flags]
public enum BehaviourFlags : uint
{
    None = 0,

    /// <summary>coop.ini <c>local_combat</c> (plugin). Whether a mirrored enemy released
    /// nearby fights through THIS engine instead of staying a driven mirror. One side running
    /// this off while the other runs it on means enemies visibly fight for only one player —
    /// the other watches a mirror that never swings.</summary>
    LocalCombat = 1 << 0,

    /// <summary>coop.ini <c>companion_local_combat</c> (plugin). Same as <see
    /// cref="LocalCombat"/> for a nearby companion released to fight locally.</summary>
    CompanionLocalCombat = 1 << 1,

    /// <summary>coop.ini <c>party_fast_pump</c>. Whether the party channel rides the real-time
    /// path or the ~150 ms combat cadence — a pure latency knob, but a large enough one
    /// (docs/research/lag-audit.md's measured 45-55% cut) that one side running the slow
    /// cadence explains "your companion feels laggier than mine" without either side being
    /// broken.</summary>
    PartyFastPump = 1 << 2,

    /// <summary>Whether the party channel runs at all (CLI <c>--no-party-sync</c>; no coop.ini
    /// key — <see cref="AgentSettings.SyncParty"/>). HOST-AUTHORITATIVE: the host drives the
    /// companions and a client only ever freezes/mirrors — a client running with this off
    /// while the host runs it on would let its own engine's companion AI fight the host's
    /// mirror writes every tick, and a host running it off while a client expects a mirror
    /// gets a party that never moves. Adopted from the host so the channel is coherent.</summary>
    PartySync = 1 << 3,

    /// <summary>coop.ini <c>client_door_refusal</c>. Whether a CLIENT refuses its own premature
    /// door crossing while following the host. A purely local guard — no wire effect either
    /// way — but worth a warning because it changes whether a follower's own engine-native
    /// crossing gets cancelled at all.</summary>
    ClientDoorRefusal = 1 << 4,

    /// <summary>coop.ini <c>transition_announce</c>. Whether a follower acts on an announced
    /// transition immediately instead of waiting for the module-settle debounce. Purely local
    /// (each side decides for itself whether IT acts fast on an announce it receives), so it
    /// warns rather than degrades a channel — but the asymmetry ("my transitions feel instant,
    /// theirs lag") is worth explaining.</summary>
    TransitionAnnounce = 1 << 5,

    /// <summary>coop.ini <c>client_load_rejoin</c>. Whether a CLIENT's own Load Game tears the
    /// session down and re-adopts. Purely local to the side that loads.</summary>
    ClientLoadRejoin = 1 << 6,

    /// <summary>coop.ini <c>host_load_readopt</c>. Whether a HOST's own Load Game has every
    /// client rejoin instead of disconnecting. Meaningful only on the host, but a client that
    /// disagrees should still know what to expect if the host reloads.</summary>
    HostLoadReadopt = 1 << 7,

    /// <summary>coop.ini <c>effect_sync</c> (plugin). Whether non-damage combat effects (stun,
    /// slow, knockdown...) are forwarded at all. HOST-AUTHORITATIVE for the same reason as
    /// every other combat-adjacent channel here: the host's engine is what actually applies
    /// the effect to the shared enemy, so a client that silently stopped forwarding its own
    /// effects would make the host's fight easier for no visible reason.</summary>
    EffectSync = 1 << 8,

    /// <summary>coop.ini <c>trigger_relay</c> (plugin). Whether a client reports the generic
    /// triggers it stands in, and whether the host's own trigger fires reach clients. HOST-
    /// AUTHORITATIVE: the host's object-id counter is what the whole session's ids are keyed
    /// on (docs/research/trigger-relay.md), so a client silently not participating leaves that
    /// counter one-sided.</summary>
    TriggerRelay = 1 << 9,

    /// <summary>coop.ini <c>client_encounters</c> (plugin). Whether a client's own encounter
    /// trigger fires it on the host too. A client running this off while the host expects it
    /// leaves an encounter spawned on only one side.</summary>
    ClientEncounters = 1 << 10,

    /// <summary>coop.ini <c>dialogue_item_sync</c>. Whether a reward node's item grant is
    /// forwarded to the other player. HOST-AUTHORITATIVE in the sense that the party's shared
    /// experience of "did we both get the reward" needs both sides running the same policy —
    /// adopted from the host so a client that quietly turned this off does not silently starve
    /// its partner of quest rewards.</summary>
    DialogueItemSync = 1 << 11,

    /// <summary>coop.ini <c>dialogue_recruit_sync</c> (plugin). Whether a companion recruited
    /// through dialogue reaches the peer through the real network path. HOST-AUTHORITATIVE:
    /// the party roster channel is already host-authoritative
    /// (docs/research/party-reconciliation.md), so recruiting locally without telling the peer
    /// desyncs the one thing every other companion channel assumes is shared.</summary>
    DialogueRecruitSync = 1 << 12,

    /// <summary>coop.ini <c>script_item_sync</c>. Whether an OnEnter/OnDeath/heartbeat script
    /// grant (outside any conversation) is forwarded. HOST-AUTHORITATIVE for the same reason
    /// as <see cref="DialogueItemSync"/> — same channel, different trigger.</summary>
    ScriptItemSync = 1 << 13,

    /// <summary>coop.ini <c>movie_relay</c>. Whether a mid-module <c>PlayMovie</c> with no
    /// transition is relayed to peers. HOST-AUTHORITATIVE: a cutscene the host expects both
    /// players to see together should not silently stay one-sided because a client turned its
    /// own relay off.</summary>
    MovieRelay = 1 << 14,

    /// <summary>coop.ini <c>liveness_sync</c> (plugin). Whether a body alive on one screen,
    /// dead on the other, is reconciled. HOST-AUTHORITATIVE: the host's creature census is the
    /// one both sides are supposed to converge toward.</summary>
    LivenessSync = 1 << 15,

    /// <summary>coop.ini <c>map_reveal_sync</c> (plugin). Whether fog-of-war reveals around a
    /// peer's live position. HOST-AUTHORITATIVE by the same convention as the rest of this
    /// list, even though either side's reveal is cosmetic on its own — kept consistent so a
    /// player is never surprised that their partner's map behaves differently mid-session.</summary>
    MapRevealSync = 1 << 16,

    /// <summary>CLI <c>--require-identical-save</c> (no coop.ini key; <see
    /// cref="SessionSettings.RequireIdenticalSave"/>). Not host-authoritative here — the HARD
    /// refusal this flag drives already runs on its own path
    /// (<see cref="ContentCompatibility.RequireIdenticalSave"/>) independent of this digest.
    /// Carried in the behaviour digest purely so a mismatch between the two sides' intent is
    /// visible (a host demanding identical saves while a client did not expect a refusal is
    /// worth a warning even though the refusal itself needs no negotiation to happen).</summary>
    RequireIdenticalSave = 1 << 17,
}

/// <summary>
/// A compact snapshot of the coop.ini/CLI behaviour flags that matter enough to warn (or, for
/// a host-authoritative channel, adopt) when two peers disagree — the negotiation game content
/// fingerprinting (<see cref="ContentDigest"/>) already gets and behaviour flags never did
/// (docs/roadmap-seamless.md Tier 1 item 7). Carried in <see cref="HelloPacket"/> (client to
/// host) and <see cref="WelcomePacket"/> (host to client), compared identically on both ends by
/// <see cref="BehaviourCompatibility.Compare"/>.
///
/// Deliberately NOT modelled like <see cref="ContentDigest"/> (no per-field KnownMask): every
/// key here comes from a parsed coop.ini/CLI value with a built-in default, so it is always
/// resolvable — there is no filesystem-dependent "could not be computed" state to represent.
///
/// Every boolean lives in <see cref="Flags"/> (one bitfield, one wire uint) rather than one
/// field per key; <c>interp_ms</c> and <c>follow_lead</c> get their own fields because they are
/// not booleans, and so do the four player-count scaling SETTINGS (<c>enemy_health</c>,
/// <c>enemy_damage</c>, <c>xp_rate</c>, <c>store_stock</c> — integer percents, or
/// <see cref="PlayerCountScalingRule.Auto"/>; docs/research/player-count-scaling.md), which are
/// host-authoritative: a client adopts the host's setting so both resolve the same percent
/// from the same player count. <see cref="Hash"/> is a stable FNV-1a-32 over the whole digest, purely for a
/// cheap one-line startup log the way <see cref="ContentDigest"/>'s fields already get one
/// (<c>AgentComposition.DescribeDigest</c>) — nothing compares against it directly; the actual
/// judgment is always per-field, the same rule <see cref="ContentCompatibility"/> follows.
/// </summary>
public readonly record struct BehaviourDigest(
    uint Flags, int InterpolationMs, string FollowLead,
    int EnemyHealth, int EnemyDamage, int XpRate, int StoreStock, uint Hash)
{
    /// <summary>Bumped only if the field set/order changes — mirrors <see
    /// cref="ContentDigest.CurrentScheme"/>. 2 (2026-09-18): the four player-count scaling
    /// settings joined the digest (protocol version 53).</summary>
    public const byte CurrentScheme = 2;

    /// <summary>Every field at its built-in default: everything on except the OFF-by-default
    /// keys, <c>interp_ms</c> at the built-in delay, <c>follow_lead</c> at "any", the four
    /// scaling settings at their code fallbacks (<c>enemy_health</c>/<c>enemy_damage</c> 100 —
    /// NEEDS-LIVE, see docs/research/player-count-scaling.md; <c>xp_rate</c>/<c>store_stock</c>
    /// auto). Used by test fakes and any <see cref="ICoopSession"/> that never computed a real
    /// one.</summary>
    public static BehaviourDigest Default { get; } = Compute(
        localCombat: true,
        companionLocalCombat: false,
        partyFastPump: true,
        partySync: true,
        clientDoorRefusal: true,
        transitionAnnounce: true,
        clientLoadRejoin: true,
        hostLoadReadopt: true,
        effectSync: true,
        triggerRelay: true,
        clientEncounters: true,
        dialogueItemSync: true,
        dialogueRecruitSync: false,
        scriptItemSync: false,
        movieRelay: true,
        livenessSync: false,
        mapRevealSync: false,
        requireIdenticalSave: false,
        interpolationMs: ProtocolConstants.DefaultInterpolationDelayMs,
        followLead: "any",
        enemyHealth: PlayerCountScalingRule.Auto,
        enemyDamage: PlayerCountScalingRule.Auto,
        xpRate: PlayerCountScalingRule.Auto,
        storeStock: PlayerCountScalingRule.Auto);

    public bool Has(BehaviourFlags flag) => (Flags & (uint)flag) != 0;

    /// <summary>Builds a digest from an already-assembled flags word, recomputing
    /// <see cref="Hash"/> from it rather than trusting a caller-supplied one — used by
    /// <see cref="PacketCodec"/> on read, so a corrupted/truncated wire hash can never
    /// silently pass a same-hash comparison against genuinely different flags. A follow-lead
    /// of null/empty normalises to <c>"any"</c>, the same default <see cref="Compute"/>
    /// applies; a scaling setting is normalised through
    /// <see cref="PlayerCountScalingRule.NormaliseSetting"/> so a corrupted wire int can never
    /// resolve to a percent outside the documented range.</summary>
    public static BehaviourDigest FromFields(
        uint flags, int interpolationMs, string? followLead,
        int enemyHealth = PlayerCountScalingRule.Auto, int enemyDamage = PlayerCountScalingRule.Auto,
        int xpRate = PlayerCountScalingRule.Auto, int storeStock = PlayerCountScalingRule.Auto)
    {
        string follow = string.IsNullOrEmpty(followLead) ? "any" : followLead;
        enemyHealth = PlayerCountScalingRule.NormaliseSetting(enemyHealth);
        enemyDamage = PlayerCountScalingRule.NormaliseSetting(enemyDamage);
        xpRate = PlayerCountScalingRule.NormaliseSetting(xpRate);
        storeStock = PlayerCountScalingRule.NormaliseSetting(storeStock);
        return new BehaviourDigest(
            flags, interpolationMs, follow, enemyHealth, enemyDamage, xpRate, storeStock,
            ComputeHash(flags, interpolationMs, follow, enemyHealth, enemyDamage, xpRate, storeStock));
    }

    /// <summary>Builds a digest from the resolved (post coop.ini/CLI-default) value of every
    /// key, computing <see cref="Hash"/> so callers never have to get the hash formula right
    /// themselves.</summary>
    public static BehaviourDigest Compute(
        bool localCombat,
        bool companionLocalCombat,
        bool partyFastPump,
        bool partySync,
        bool clientDoorRefusal,
        bool transitionAnnounce,
        bool clientLoadRejoin,
        bool hostLoadReadopt,
        bool effectSync,
        bool triggerRelay,
        bool clientEncounters,
        bool dialogueItemSync,
        bool dialogueRecruitSync,
        bool scriptItemSync,
        bool movieRelay,
        bool livenessSync,
        bool mapRevealSync,
        bool requireIdenticalSave,
        int interpolationMs,
        string followLead,
        int enemyHealth = PlayerCountScalingRule.Auto,
        int enemyDamage = PlayerCountScalingRule.Auto,
        int xpRate = PlayerCountScalingRule.Auto,
        int storeStock = PlayerCountScalingRule.Auto)
    {
        uint flags = 0;
        void Set(bool on, BehaviourFlags bit)
        {
            if (on)
            {
                flags |= (uint)bit;
            }
        }

        Set(localCombat, BehaviourFlags.LocalCombat);
        Set(companionLocalCombat, BehaviourFlags.CompanionLocalCombat);
        Set(partyFastPump, BehaviourFlags.PartyFastPump);
        Set(partySync, BehaviourFlags.PartySync);
        Set(clientDoorRefusal, BehaviourFlags.ClientDoorRefusal);
        Set(transitionAnnounce, BehaviourFlags.TransitionAnnounce);
        Set(clientLoadRejoin, BehaviourFlags.ClientLoadRejoin);
        Set(hostLoadReadopt, BehaviourFlags.HostLoadReadopt);
        Set(effectSync, BehaviourFlags.EffectSync);
        Set(triggerRelay, BehaviourFlags.TriggerRelay);
        Set(clientEncounters, BehaviourFlags.ClientEncounters);
        Set(dialogueItemSync, BehaviourFlags.DialogueItemSync);
        Set(dialogueRecruitSync, BehaviourFlags.DialogueRecruitSync);
        Set(scriptItemSync, BehaviourFlags.ScriptItemSync);
        Set(movieRelay, BehaviourFlags.MovieRelay);
        Set(livenessSync, BehaviourFlags.LivenessSync);
        Set(mapRevealSync, BehaviourFlags.MapRevealSync);
        Set(requireIdenticalSave, BehaviourFlags.RequireIdenticalSave);

        return FromFields(flags, interpolationMs, followLead, enemyHealth, enemyDamage, xpRate, storeStock);
    }

    /// <summary>FNV-1a-32, the same house hash <see cref="SessionPassword"/> and <see
    /// cref="ContentDigest"/>'s producers use — folded over the flags word, the interpolation
    /// delay, the follow-lead string's UTF-16 code units, and the four scaling settings.</summary>
    private static uint ComputeHash(
        uint flags, int interpolationMs, string followLead,
        int enemyHealth, int enemyDamage, int xpRate, int storeStock)
    {
        uint h = 2166136261;
        void Mix(byte b) => h = (h ^ b) * 16777619;

        Mix((byte)flags);
        Mix((byte)(flags >> 8));
        Mix((byte)(flags >> 16));
        Mix((byte)(flags >> 24));

        uint interp = unchecked((uint)interpolationMs);
        Mix((byte)interp);
        Mix((byte)(interp >> 8));
        Mix((byte)(interp >> 16));
        Mix((byte)(interp >> 24));

        foreach (char c in followLead)
        {
            Mix((byte)c);
            Mix((byte)(c >> 8));
        }

        MixInt(enemyHealth);
        MixInt(enemyDamage);
        MixInt(xpRate);
        MixInt(storeStock);

        return h;

        void MixInt(int value)
        {
            uint u = unchecked((uint)value);
            Mix((byte)u);
            Mix((byte)(u >> 8));
            Mix((byte)(u >> 16));
            Mix((byte)(u >> 24));
        }
    }
}
