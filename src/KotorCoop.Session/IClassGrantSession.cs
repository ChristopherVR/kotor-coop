// A class a script added to one player's character, relayed so every other player's
// character gets it too (docs/research/class-and-power-grants.md).
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IClassGrantSession
{
    /// <summary>
    /// Announces that a script on THIS instance just added <paramref name="classId"/>
    /// (a <c>classes.2da</c> row) to this player's own character — Dorak's Jedi path choice —
    /// delivered reliably to every OTHER peer so their instance can add the same class to
    /// their character. Symmetric like <see cref="IDialogueSession.PublishDialogueGrant"/>:
    /// <c>CoopHost</c> broadcasts directly; <c>CoopClient</c> sends to the host, which relays
    /// to every other client under this peer's TRUE (origin, sequence) identity.
    /// </summary>
    void PublishClassGrant(int classId) { }

    /// <summary>
    /// Every class-grant packet received from ANOTHER peer since the last drain, oldest first,
    /// for the agent to apply through the plugin's own <c>AddMultiClass</c> replay. A grant
    /// whose origin is THIS peer's own id is filtered out before it reaches this queue (a
    /// second add of the same class is refused by the plugin's apply rule anyway, but the
    /// echo is not worth a round trip).
    /// </summary>
    IReadOnlyList<ClassGrantPacket> DrainClassGrants() => [];
}
