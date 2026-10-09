namespace KotorCoop.Session;

/// <summary>
/// Per-session runtime overrides for the behaviour keys where the HOST's value must win to
/// keep a host-authoritative channel coherent — the second half of the behaviour-flag
/// negotiation (docs/research/mod-compatibility.md, "Behaviour-flag negotiation"). Built once
/// by a <see cref="CoopClient"/> when its <see cref="KotorCoop.Protocol.BehaviourDigest"/>
/// comparison against the host's <c>Welcome</c> finds a host-authoritative key differs; a
/// <see cref="CoopHost"/> always exposes <see cref="None"/> — a host is authoritative by
/// definition and never adopts anything from a peer.
///
/// Every field is <c>null</c> = "no override, read the local coop.ini/CLI value" (through
/// <see cref="AgentSettings"/> or <see cref="CoopConfig"/>, whichever the consuming site
/// already reads). Deliberately NOT a mutation of the parsed <see cref="AgentSettings"/> or
/// <see cref="CoopConfig"/> record: those describe what THIS install's coop.ini/CLI actually
/// said, which stays true for diagnostics, `agent info`, and a future reconnect to a
/// DIFFERENT host whose values might not need overriding at all. This is a separate,
/// session-scoped layer that the handful of pumps able to act on a host-authoritative key
/// consult ALONGSIDE their <see cref="AgentSettings"/> — see <see
/// cref="ICapabilitySession.Overrides"/> and its four call sites (<c>PartyPump</c>,
/// <c>DialoguePump</c>, <c>ScriptItemGrantPump</c>, <c>TransitionPump</c>).
///
/// Five of the nine host-authoritative keys (<c>effect_sync</c>, <c>trigger_relay</c>,
/// <c>liveness_sync</c>, <c>map_reveal_sync</c>, <c>dialogue_recruit_sync</c>) are fields here
/// too — a difference on them is compared, warned and recorded exactly like the other four —
/// but nothing in <c>KotorCoop.Session</c> itself reads them: those five channels are gated
/// entirely inside the separate plugin process (<c>PluginConfig</c> and per-controller reads,
/// e.g. <c>PeerEffectController</c>, <c>TriggerRelayController</c>). CLOSED 2026-08-27: the
/// same idiom <c>ApplyChatOverlayConfig</c>/<c>PazaakDetectEnabled</c> already use to push a
/// couple of OTHER settings across the process boundary now has a slot for these five too —
/// <see cref="CoopAgent"/> encodes this record every tick
/// (<c>BehaviourOverrideEncoding.Encode</c>) and pushes it through
/// <c>IPlayerBridge.SetBehaviourOverrides</c> whenever it changes, into
/// <c>SharedBlock.OffsetBehaviourOverrideMask</c>/<c>Values</c>; each of the five plugin
/// controllers resolves its effective value from that region instead of a construction-time
/// <c>PluginConfig</c> read (see <c>EffectiveBehaviourFlag</c>). See mod-compatibility.md's
/// dated follow-up entry.
/// </summary>
public sealed record SessionOverrides(
    bool? PartySync = null,
    bool? EffectSync = null,
    bool? TriggerRelay = null,
    bool? LivenessSync = null,
    bool? MapRevealSync = null,
    bool? ScriptItemSync = null,
    bool? DialogueItemSync = null,
    bool? DialogueRecruitSync = null,
    bool? MovieRelay = null,
    int? EnemyHealth = null,
    int? EnemyDamage = null,
    int? XpRate = null,
    int? StoreStock = null,
    int? StorePrices = null,
    bool? InventoryPersonal = null,
    bool? CreditsPersonal = null,
    bool? CorpseLootPersonal = null)
{
    // The five int? fields (2026-09-18/23) are the player-count scaling SETTINGS
    // (docs/research/player-count-scaling.md): the host's coop.ini value, adopted verbatim —
    // PlayerCountScalingRule.Auto or a fixed percent — so this client resolves the same
    // percent from the same player count the host does. Consulted by CoopAgent's
    // PlayerCountScaling.Update alongside AgentSettings.Scaling, the same "override ?? local"
    // idiom the bool fields use.

    /// <summary>No overrides: every consultation site falls back to its own local
    /// AgentSettings/CoopConfig value. What a host always returns, and what a fresh client
    /// returns before its first Welcome.</summary>
    public static readonly SessionOverrides None = new();
}
