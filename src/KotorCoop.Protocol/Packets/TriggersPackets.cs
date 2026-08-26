namespace KotorCoop.Protocol;

// Client-standing-in-trigger reports. Matches PacketCodec.Triggers.cs.

/// <summary>
/// client -> host: "my player is standing inside these encounters right now."
///
/// <paramref name="EncounterIds"/> are engine object ids, which is a legitimate
/// cross-instance key here for the same reason enemy ids are: <c>CSWSArea::LoadGIT</c>
/// walks the module's GIT lists in a fixed order and hands out ids from a monotonic
/// counter, and the Encounter List is one of those lists (Handoff 4.5;
/// <c>tools/kotorfiles.py objectids</c> reproduces the assignment offline).
///
/// <paramref name="Module"/> rides along for the same reason it does on
/// <see cref="EnemyReportPacket"/>: ids reset on a module transition, so the host must
/// never act on a request from a peer standing somewhere else.
///
/// Sent every tick while the set is non-empty. The set is the message — there is no
/// "entered" edge on the wire, because an edge that is lost is lost forever while a level
/// re-converges on the next datagram.
/// </summary>
public sealed record EncounterTriggerPacket(
    uint PeerId, string Module, IReadOnlyList<uint> EncounterIds);

/// <summary>
/// Two directions sharing one shape, distinguished by <paramref name="PeerId"/> exactly like
/// <see cref="WorldDigest"/>'s "both ways" packets:
///
/// - client -&gt; host (<paramref name="PeerId"/> is the sender's real, non-zero peer id):
///   the GENERIC triggers (a <c>CSWSTrigger</c> whose OnEnter starts a cutscene, dialogue or
///   scripted event — never a trap, never an area transition) this peer's player is standing
///   inside RIGHT NOW, in <paramref name="TriggerIds"/>. <paramref name="FiredIds"/> is empty.
///
/// - host -&gt; all (<paramref name="PeerId"/> == 0, which no real peer ever holds): the
///   trigger ids this host has already fired in <paramref name="Module"/>, in
///   <paramref name="FiredIds"/>, so a client's watch stops re-reporting ones the host has
///   already handled. <paramref name="TriggerIds"/> is empty.
///
/// <paramref name="Module"/> rides along for the same reason it does on every module-scoped
/// channel here: trigger object ids reset on a transition, so a report or a fired-set from a
/// different module names nothing the receiver should act on.
///
/// A LEVEL in both directions, like <see cref="EncounterTriggerPacket"/>: both sides resend
/// their current truth every tick rather than an edge, so one lost datagram costs a tick, not
/// a permanently missed relay. See docs/research/trigger-relay.md.
///
/// <paramref name="NativeFiredIds"/> / <paramref name="NativeFiredOriginPeerIds"/> close the
/// REVERSE direction (protocol 36): before this, only a client's presence in a trigger ever
/// reached the other instance. A <c>TriggerFireHook</c> detour on
/// <c>CSWSTrigger::EventHandler</c>'s own generic-script call site observes ANY instance's
/// engine natively firing a trigger for its own real PC (allowlist- and eligibility-filtered
/// the same way the watch already is) —
///
/// - client/host -&gt; host (<paramref name="PeerId"/> the sender's real id): triggers THIS
///   peer's own engine has natively fired for itself, in <paramref name="NativeFiredIds"/>.
///   <paramref name="NativeFiredOriginPeerIds"/> is empty — origin is implicitly the sender.
/// - host -&gt; all (<paramref name="PeerId"/> == 0): the merged per-module native-fire set —
///   every peer's own natural OnEnter this host knows about — in
///   <paramref name="NativeFiredIds"/>, matched 1:1 by index against the peer id that fired it
///   in <paramref name="NativeFiredOriginPeerIds"/>. A receiver whose own peer id already
///   equals an entry's origin skips it (that engine already ran it for real); every OTHER
///   receiver fires the SAME trigger locally against the origin's phantom, through the same
///   <c>TriggerRelayController.HandleRequests</c> path the host already used one-way. Folded
///   into the SAME per-module fired-set <see cref="FiredIds"/> already dedupes, so the two
///   directions can never double-fire one trigger.
/// </summary>
public sealed record TriggerRelayPacket(
    uint PeerId, string Module, IReadOnlyList<uint> TriggerIds, IReadOnlyList<uint> FiredIds,
    IReadOnlyList<uint> NativeFiredIds, IReadOnlyList<uint> NativeFiredOriginPeerIds)
{
    /// <summary>Convenience for the many existing callers that predate protocol 36 and never
    /// send a native fire in the same breath as their other report. Parameter names match the
    /// primary constructor's exactly so a caller using named arguments (<c>PeerId: 5</c>, say)
    /// still resolves to this overload rather than being forced onto the 6-arg one.</summary>
    public TriggerRelayPacket(
        uint PeerId, string Module, IReadOnlyList<uint> TriggerIds, IReadOnlyList<uint> FiredIds)
        : this(PeerId, Module, TriggerIds, FiredIds, [], [])
    {
    }
}
