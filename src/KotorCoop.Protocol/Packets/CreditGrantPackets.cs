namespace KotorCoop.Protocol;

/// <summary>
/// "This peer's own conversation just handed <paramref name="Amount"/> credits to its local
/// player" (docs/research/personal-inventory.md §4) — the credits analogue of
/// <see cref="DialogueGrantPacket"/>, but a wholly separate, additive channel rather than a
/// field bolted onto that one: <see cref="DialogueGrantPacket"/>'s own wire shape and the
/// <c>IDialogueSession</c> interface it rides are consulted (and overridden) by test fixtures
/// this pass must not touch (see the design doc's own remarks on why). Relayed reliably —
/// a reward is a one-shot fact that must survive a dropped packet, never merely resampled.
/// Only ever positive (see <see cref="PacketCodec.WriteCreditGrant"/>'s own guard); dedup is
/// entirely on (<paramref name="OriginPeerId"/>, <paramref name="OpSequence"/>).
/// </summary>
public sealed record CreditGrantPacket(uint OriginPeerId, uint OpSequence, int Amount);

/// <summary>Acknowledges one <see cref="CreditGrantPacket"/> op, in either direction — same
/// (acking peer id, origin, sequence) shape as <see cref="DialogueGrantAckPacket"/>, for the
/// same reason: the host relays a client's report under the client's TRUE origin rather than
/// re-attributing it, so two different origins can legitimately reuse the same sequence
/// number.</summary>
public sealed record CreditGrantAckPacket(uint PeerId, uint OriginPeerId, uint OpSequence);
