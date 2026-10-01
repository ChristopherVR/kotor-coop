// The host-start conversation relay: a joiner's refused host-only conversation start, asked
// of the host instead. See docs/research/dialogue-sync.md's host-start relay section.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IHostConversationSession
{
    /// <summary>
    /// Sends this player's refused host-only conversation start to the host - one-shot, no
    /// wire-level retry (the plugin's own debounce covers a lost datagram). A HOST's own
    /// implementation queues it in-process, through the same path a peer's packet takes.
    /// Default no-op for a session that predates this.
    /// </summary>
    void RequestHostConversation(string module, string tag, string resref, uint requestSerial)
    { }

    /// <summary>
    /// HOST only: requests received from peers since the last poll, oldest first. A client
    /// always returns empty. Default empty for a session that predates this.
    /// </summary>
    IReadOnlyList<HostConversationRequestPacket> DrainHostConversationRequests() => [];
}
