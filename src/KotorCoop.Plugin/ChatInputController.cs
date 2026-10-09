namespace KotorCoop.Plugin;

/// <summary>
/// Press <c>Enter</c> in a loaded module — console closed, no menu or conversation — to open the
/// developer console pre-filled with <c>say&#160;</c> as a chat prompt, so a player can type a
/// message without knowing the console exists. Typing then Enter submits it, and the
/// <see cref="FuncInterpHook"/> routes the finished <c>say</c> line to the co-op layer.
///
/// Runs on the SwapBuffers frame hook, which fires in-world (where chat matters); it is idle at
/// the main menu. Enter is edge-detected from raw key state (<see cref="NativeMethods.IsKeyDownHere"/>
/// polls <c>GetAsyncKeyState</c> once per rendered frame), which runs independently of — and can
/// see stale-relative-to — the engine's own message-driven console submit/close: submitting ANY
/// console line (a coop command, an engine cheat, or a chat message itself) closes the real
/// console almost instantly, but a human's Enter press is still physically held for the next
/// frame or two. Without <see cref="ChatInputLatch"/>, that next frame reads as "key just went down,
/// console now closed" — a fresh rising edge — and reopens the chat prompt immediately after
/// every single submission. Confirmed live 2026-09-10: submitting `host` at the `~` console left
/// the game sitting open on `say ` unprompted. The latch disarms the moment a
/// hold observes the console open, so that SAME hold can never fall through into opening chat
/// once the engine closes the console under it — only a fresh press, after a full key release,
/// may open chat. <see cref="Odyssey.OpenChatInput"/> is also a no-op when the console is already
/// open, so a real grave-key open is never disturbed.
/// </summary>
internal sealed unsafe class ChatInputController
{
    private readonly ChatInputLatch _latch = new();

    public void OnFrame()
    {
        bool down = NativeMethods.IsKeyDownHere(NativeMethods.VkReturn);
        if (!_latch.ShouldOpen(down, Odyssey.IsConsoleOpen, Odyssey.IsInGame,
            Odyssey.IsConversationActive, Odyssey.IsFullScreenGuiUp || PasswordEntryModal.IsAnyOpen
            || PvpMenuController.ListOpen))
        {
            return;
        }

        Odyssey.OpenChatInput();
    }
}
