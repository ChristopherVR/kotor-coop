namespace KotorCoop.Plugin;

/// <summary>
/// Press <c>Enter</c> in a loaded module — console closed, not mid-conversation — to open the
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
/// frame or two. Without <see cref="_armed"/>, that next frame reads as "key just went down,
/// console now closed" — a fresh rising edge — and reopens the chat prompt immediately after
/// every single submission. Confirmed live 2026-09-10: submitting `host` at the `~` console left
/// the game sitting open on `say ` unprompted. <see cref="_armed"/> latches false the moment a
/// hold observes the console open, so that SAME hold can never fall through into opening chat
/// once the engine closes the console under it — only a fresh press, after a full key release,
/// may open chat. <see cref="Odyssey.OpenChatInput"/> is also a no-op when the console is already
/// open, so a real grave-key open is never disturbed.
/// </summary>
internal sealed unsafe class ChatInputController
{
    private bool _enterWasDown;

    /// <summary>False while the current (still-held) Enter press has seen the console open —
    /// i.e., this hold belongs to a submission, not a fresh chat request. Reset to true the
    /// moment the key is released.</summary>
    private bool _armed = true;

    public void OnFrame()
    {
        bool down = NativeMethods.IsKeyDownHere(NativeMethods.VkReturn);
        bool rising = down && !_enterWasDown;
        _enterWasDown = down;

        if (!down)
        {
            _armed = true; // released: the next press is independent of whatever this one did
            return;
        }

        if (Odyssey.IsConsoleOpen)
        {
            _armed = false; // this hold is (or was) submitting through a real open console
            return;
        }

        if (!_armed || !rising || !Odyssey.IsInGame || Odyssey.IsConversationActive)
        {
            return;
        }

        Odyssey.OpenChatInput();
    }
}
