namespace KotorCoop.Plugin;

/// <summary>
/// Press <c>Enter</c> in a loaded module — console closed, not mid-conversation — to open the
/// developer console pre-filled with <c>say&#160;</c> as a chat prompt, so a player can type a
/// message without knowing the console exists. Typing then Enter submits it, and the
/// <see cref="FuncInterpHook"/> routes the finished <c>say</c> line to the co-op layer.
///
/// Runs on the SwapBuffers frame hook, which fires in-world (where chat matters); it is idle at
/// the main menu. Enter is edge-detected from raw key state, exactly like the console submit,
/// so it never fights the engine's own Enter handling. <see cref="Odyssey.OpenChatInput"/> is a
/// no-op when the console is already open, so a real grave-key open is never disturbed.
/// </summary>
internal sealed unsafe class ChatInputController
{
    private bool _enterWasDown;

    public void OnFrame()
    {
        bool down = NativeMethods.IsKeyDownHere(NativeMethods.VkReturn);
        bool rising = down && !_enterWasDown;
        _enterWasDown = down;

        if (!rising || Odyssey.IsConsoleOpen || !Odyssey.IsInGame || Odyssey.IsConversationActive)
        {
            return;
        }

        Odyssey.OpenChatInput();
    }
}
