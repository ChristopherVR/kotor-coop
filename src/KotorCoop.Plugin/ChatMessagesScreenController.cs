namespace KotorCoop.Plugin;

/// <summary>
/// coop.ini <c>chat_messages_screen</c> (default OFF, NEEDS-LIVE prototype - see
/// docs/research/chat-ui.md). Mirrors every chat line the always-on overlay
/// (<see cref="ChatOverlayController"/>) already shows into the RETAIL pause-menu Messages
/// screen's dialogue-history ring, via <see cref="Odyssey.AppendChatLineToMessagesScreen"/>, so a
/// player who missed the overlay (it fades) can still find recent chat by pressing Escape ->
/// Messages - a genuine engine panel, not a mod-drawn one, with its own scrollbar for free.
///
/// Reads the SAME shared-block fields <see cref="ChatOverlayController"/> already reads
/// (<see cref="SharedBlockView.ChatSerial"/>/<see cref="SharedBlockView.ChatLine"/>) - no new
/// block field, no new agent-side plumbing. The line already arrives pre-formatted as
/// "Name: text" (<c>CoopCommandDispatcher.DrainChat</c>/<c>Chat</c>), so it is appended as the
/// MESSAGE half with an empty speaker; splitting the two would need a second shared-block field
/// this pass does not add.
///
/// Best-effort only: a line that arrives while no module is loaded (main menu, a load screen)
/// is simply not written here (<see cref="Odyssey.AppendChatLineToMessagesScreen"/> no-ops with
/// no CGuiInGame) - the overlay is still what shows it in that case. Silently dropped rather
/// than queued, matching the ring's own nature (a rolling recent-history log, not a guaranteed
/// delivery channel).
/// </summary>
internal sealed unsafe class ChatMessagesScreenController(SharedBlockView block)
{
    private readonly bool _enabled = PluginConfig.ReadBool("chat_messages_screen", false);

    private uint _lastSerial;

    public void OnFrame()
    {
        if (!_enabled)
        {
            return;
        }

        uint serial = block.ChatSerial;
        if (serial == _lastSerial)
        {
            return;
        }

        _lastSerial = serial;

        // ChatLine is a fixed SharedBlock.ChatLineLength (128) byte, null-terminated ASCII
        // buffer the agent already wrote; find the terminator rather than assume it is full.
        byte* source = block.ChatLine;
        int length = 0;
        while (length < SharedBlock.ChatLineLength - 1 && source[length] != 0)
        {
            length++;
        }

        if (length > 0)
        {
            Odyssey.AppendChatLineToMessagesScreen(default, new ReadOnlySpan<byte>(source, length));
        }
    }
}
