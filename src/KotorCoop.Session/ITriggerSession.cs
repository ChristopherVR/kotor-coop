// Encounter and generic-trigger relay: a client's own presence in an encounter/trigger area
// forwarded to the host so the host's engine can spawn/fire it, plus the native fired-set
// every peer's own engine reports back. See docs/research/trigger-relay.md.
namespace KotorCoop.Session;

public interface ITriggerSession
{
    /// <summary>
    /// Client only: "my player is standing inside these encounters." A client's own
    /// encounters are disarmed (they would drift the object-id counter), so this is how a
    /// client walking into an ambush gets one — the host runs the engine's eligibility test
    /// against that client's phantom and lets its engine spawn. Sent every tick as the
    /// current SET, not as an enter event. A host is a no-op: its engine fires its own.
    /// </summary>
    void PublishEncounterTriggers(string module, IReadOnlyList<uint> encounterIds) { }

    /// <summary>
    /// Client only: "my player is standing inside these GENERIC triggers" — a
    /// <c>CSWSTrigger</c> whose OnEnter starts a cutscene/dialogue/scripted event, never a
    /// trap or an area transition (the plugin's own watch already excludes those). Unlike
    /// <see cref="PublishEncounterTriggers"/>'s triggers, the client's OWN engine already
    /// fires these correctly for the client's own real character — this is purely so the
    /// HOST'S engine can run the same OnEnter script against that peer's phantom, keeping the
    /// host's object-id counter in lockstep with whatever the script created or destroyed.
    /// Sent every tick as the current SET. A host is a no-op: it fires its own, and does not
    /// need to be told what its own real player is standing in. See
    /// docs/research/trigger-relay.md.
    /// </summary>
    void PublishTriggerEntries(string module, IReadOnlyList<uint> triggerIds) { }

    /// <summary>
    /// Host only: the generic triggers clients are asking for right now, in
    /// <paramref name="module"/>, deduplicated and with anything already fired in that module
    /// removed. Empty on a client. See <see cref="TriggerRelayStore.Collect"/>.
    /// </summary>
    IReadOnlyList<TriggerFireRequest> GetTriggerFireRequests(string module) => [];

    /// <summary>
    /// The trigger ids known fired in <paramref name="module"/>: on the HOST, its own
    /// authoritative fired-set (<see cref="TriggerRelayStore.GetFiredIds"/>); on a CLIENT,
    /// the most recent such set the host has broadcast, so the client's watch can stop
    /// re-reporting ones already handled. Empty until the channel has said anything.
    /// </summary>
    IReadOnlyList<uint> GetFiredTriggerIds(string module) => [];

    /// <summary>
    /// Every peer, host included: "my OWN engine just natively fired these GENERIC triggers
    /// for my OWN real character" — the reverse of <see cref="PublishTriggerEntries"/>, which
    /// only ever covered a client's presence, never the host's. Allowlist- and
    /// eligibility-filtered by the plugin's <c>TriggerFireHook</c> observer before this is ever
    /// called, the same gates <see cref="PublishTriggerEntries"/>'s watch already applies. On a
    /// client this reaches the host over the wire (<see cref="TriggerRelayPacket.NativeFiredIds"/>,
    /// peer-&gt;host); on the host it folds straight into <see cref="TriggerRelayStore"/> with
    /// no packet at all — <c>CoopHost.HostPeerId</c> as origin. See
    /// docs/research/trigger-relay.md's protocol-36 section.
    /// </summary>
    void PublishNativeTriggerFires(string module, IReadOnlyList<uint> triggerIds) { }

    /// <summary>
    /// Every trigger this instance currently knows was natively fired by ANY peer (including
    /// itself — the caller filters entries whose origin is its own <see cref="LocalPeerId"/>,
    /// since that engine already ran the script for real) in <paramref name="module"/>. On the
    /// host this is <see cref="TriggerRelayStore.GetNativeFires"/> directly; on a client it is
    /// the most recent host broadcast. A LEVEL, like <see cref="GetFiredTriggerIds"/> — the
    /// caller is responsible for not re-issuing the SAME entry as a fresh fire request once it
    /// has already asked its own engine to run it (see <c>CoopAgent.PumpNativeTriggerFires</c>).
    /// </summary>
    IReadOnlyList<TriggerFireRequest> GetNativeTriggerFireRequests(string module) => [];

    /// <summary>
    /// Host only: the encounters clients are asking for right now, in
    /// <paramref name="module"/>, deduplicated. Empty on a client.
    /// </summary>
    IReadOnlyList<EncounterFireRequest> GetEncounterFireRequests(string module) => [];
}
