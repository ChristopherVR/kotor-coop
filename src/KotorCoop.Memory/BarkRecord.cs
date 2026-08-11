namespace KotorCoop.Memory;

/// <summary>
/// One NPC bark the plugin captured: the line an NPC threw into a bubble without ever
/// opening a conversation.
///
/// <see cref="Speaker"/> is the engine SERVER object id — the same number in every instance
/// for anything the module loaded. <see cref="Text"/> and <see cref="SoundResRef"/> are the
/// RESOLVED CONTENTS of the engine's own arguments, not the arguments themselves: two of the
/// four are pointers into the producing process, and shipping them verbatim is what used to
/// kill the receiving engine (docs/research/bark-replay.md). <see cref="Serial"/> makes a
/// repeat of the same line visible as a new bark rather than a duplicate to be swallowed.
/// </summary>
/// <remarks>
/// <see cref="SoundResRef"/> is always 16 bytes: a <c>CResRef</c> is a fixed
/// <c>char[16]</c>, not a NUL-terminated string, and an empty one is an ordinary case — a
/// line with no voice-over.
/// </remarks>
public readonly record struct BarkRecord(
    uint Speaker, byte[] Text, byte[] SoundResRef, uint Flags, uint Serial);
