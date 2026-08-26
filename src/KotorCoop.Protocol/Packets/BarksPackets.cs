namespace KotorCoop.Protocol;

// One-line NPC barks. Matches PacketCodec.Barks.cs.

/// <summary>
/// One NPC bark: the line an NPC throws into a bubble without opening a conversation.
///
/// <paramref name="Speaker"/> is the engine SERVER object id, which names the same creature
/// in every instance for anything the module loaded (Handoff 4.5).
///
/// ⚠ <paramref name="Text"/> and <paramref name="SoundResRef"/> are the RESOLVED CONTENTS of
/// the engine's arguments, not the arguments. <c>BarkString</c> takes a <c>CExoString*</c>
/// and a <c>CResRef*</c>, and an earlier version of this packet carried those POINTERS
/// verbatim on the principle that a value passed through untouched needs no interpretation.
/// In the receiving process they are wild addresses, and the engine dereferences both — that
/// is what killed two instances. See docs/research/bark-replay.md.
///
/// <paramref name="Module"/> rides along for the usual reason: object ids reset on a
/// transition, so a bark must never be replayed by someone standing somewhere else.
/// <paramref name="Serial"/> makes a repeat of the same line visible as a new one.
/// </summary>
public sealed record BarkPacket(
    uint PeerId, string Module, uint Speaker, byte[] Text, byte[] SoundResRef,
    uint Flags, uint Serial)
{
    /// <summary>Bytes of bark text carried. Nothing in the binary bounds a bark's length;
    /// this is anchored to the dialogue channel's own line limit.</summary>
    public const int MaxTextBytes = 200;

    /// <summary>A <c>CResRef</c> is a fixed <c>char[16]</c> — not a NUL-terminated string,
    /// and an empty one is an ordinary case (a line with no voice-over).</summary>
    public const int ResRefBytes = 16;
}
