// NPC barks: the line an NPC throws into a bubble without ever opening a conversation.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IBarkSession
{
    /// <summary>
    /// Announces one NPC bark — the line an NPC throws into a bubble without ever opening a
    /// conversation, which the whole dialogue channel is blind to.
    ///
    /// <paramref name="text"/> and <paramref name="soundResRef"/> are the RESOLVED CONTENTS
    /// of the engine's arguments, not the arguments: <c>BarkString</c> takes a
    /// <c>CExoString*</c> and a <c>CResRef*</c>, and forwarding those pointers verbatim is
    /// what used to kill the receiving process (docs/research/bark-replay.md).
    /// </summary>
    void PublishBark(
        string module, uint speaker, byte[] text, byte[] soundResRef, uint flags, uint serial) { }

    /// <summary>Barks other peers have produced in <paramref name="module"/> since the last
    /// call, oldest first. Draining, so each is handed out exactly once.</summary>
    IReadOnlyList<BarkPacket> DrainBarks(string module) => [];
}
