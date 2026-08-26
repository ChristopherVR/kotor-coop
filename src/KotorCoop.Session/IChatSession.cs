// Short text chat between players.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IChatSession
{
    /// <summary>Sends a short text message to the other players. Empty/whitespace is ignored.</summary>
    void SendChat(string text);

    /// <summary>
    /// Returns and clears the chat messages received since the last call, oldest first. The
    /// sender's own lines are not returned here — the caller echoes them on send.
    /// </summary>
    IReadOnlyList<ChatMessage> DrainChat();
}
