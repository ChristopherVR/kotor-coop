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
/// but nothing in <c>KotorCoop.Session</c> currently reads them: those five channels are
/// gated entirely inside the separate plugin process (<c>PluginConfig</c> and per-controller
/// reads, e.g. <c>PeerEffectController</c>, <c>TriggerRelayController</c>), which this
/// negotiation does not yet reach — the same gap <c>ApplyChatOverlayConfig</c>/
/// <c>PazaakDetectEnabled</c> already bridge for a couple of OTHER settings via
/// <c>KotorGameBridge</c>'s shared-memory block, but that bridge has no slot for these five
/// yet. Recorded here so the object is complete and ready for that bridge to consult once it
/// exists; see mod-compatibility.md for the open follow-up.
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
    bool? MovieRelay = null)
{
    /// <summary>No overrides: every consultation site falls back to its own local
    /// AgentSettings/CoopConfig value. What a host always returns, and what a fresh client
    /// returns before its first Welcome.</summary>
    public static readonly SessionOverrides None = new();
}
