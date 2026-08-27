namespace KotorCoop.Plugin;

/// <summary>
/// The persistent on-screen connection indicator (item 18, roadmap-seamless.md). Before this,
/// <c>SharedBlockView.ConnectStatus</c> — the agent-written packed state + peer count byte,
/// unconditionally kept current by <c>CoopCommandDispatcher.AnnounceConnectionStatus</c> since
/// 2026-08-21 — had no reader anywhere in the plugin; the only player-visible signal was the
/// fading chat/console toast <c>KotorCoop.Agent.ConnectionStatusAnnouncer</c> posts, which (by
/// design) says nothing while the state is stable. This closes the gap for a player who never
/// looks at the toast in time: as long as a session is not Idle, a one-line summary sits on
/// screen continuously — "co-op: connected, 2 players" / "co-op: reconnecting..." /
/// "co-op: connection failed" — and disappears entirely (no line at all) once the state reads
/// back Idle (no session, or a fresh install with no agent attached yet).
///
/// Runs on MainLoopHook (menu AND in-world — the same reason <see cref="AgentLivenessController"/>
/// does): the states this shows before a session exists (Connecting, Failed) happen while the
/// player is still at the main menu, where the SwapBuffers hook is dormant behind the menu
/// movie.
///
/// The pure "packed status -&gt; text" decode lives in <see cref="ConnectionIndicatorRule"/> so
/// it is unit-testable without a live block; this class is just the per-frame read + the same
/// queued-text render path every other overlay in this plugin uses
/// (<c>Odyssey.PostTextLine</c>, re-posted every frame per
/// docs/research/text-render-queued.md).
/// </summary>
internal sealed unsafe class ConnectionIndicatorController(SharedBlockView block)
{
    // Row/column choice, so a future controller does not have to re-derive this by grepping
    // every PostTextLine call site: console prompt/line own rows 0-2; ConsoleAssistController's
    // hint sits at (3, 3); AgentLivenessController's stale/failed-to-start warning sits at
    // (3, 5) — directly above this, which is deliberate (both are connectivity-flavoured
    // one-liners, so they read as a stack rather than scattered text); ServerBrowserController
    // uses column 8 (title row 6, entries from row 8) and DialogueSpectateController /
    // InteractionLockController use column 8 rows 4/6/7 — all a different column, so even a
    // same-numbered row never collides. ChatOverlayController's shallowest anchor starts at row
    // 11 (top-left/top-right) or 14 (bottom-left/bottom-right), well below this. Column 3, row 6
    // is otherwise unused by any controller in this plugin.
    private const int MessageColumn = 3;
    private const int MessageRow = 6;
    private const float MessageAlpha = 0.9f;

    // Re-posted every frame this controller runs (see the class doc comment), so a short life
    // is correct: if OnFrame ever stopped running, the line fades out in under a second instead
    // of lingering as a stale claim about a connection that may have long since changed.
    private const float MessageLife = 0.5f;

    // coop.ini `status_indicator` (plugin-side key, default on) — read once at construction
    // like every other plugin-side on/off toggle (see ConnectMenuController's own
    // `_configEnabled` field for the same pattern).
    private readonly bool _enabled = PluginConfig.ReadBool("status_indicator", true);

    private string? _lastText;
    private byte[] _messageBytes = [];

    public void OnFrame()
    {
        if (!_enabled)
        {
            return;
        }

        string? text = ConnectionIndicatorRule.Text(block.ConnectStatus);
        if (text is null)
        {
            // Idle: no session attempted yet (or one just ended and the agent already wrote
            // Disconnected-then-cleared). Nothing to show, and nothing left to re-post — the
            // last posted node simply expires on its own life countdown.
            _lastText = null;
            return;
        }

        if (ConnectionIndicatorRule.ShouldRedraw(_lastText, text))
        {
            _lastText = text;
            _messageBytes = EncodeAscii(text);
        }

        Draw();
    }

    private void Draw()
    {
        if (_messageBytes.Length == 0)
        {
            return;
        }

        Odyssey.SetQueuedTextAlpha(MessageAlpha);
        fixed (byte* p = _messageBytes)
        {
            Odyssey.PostTextLine(p, MessageColumn, MessageRow, MessageLife);
        }

        Odyssey.ResetQueuedTextAlpha();
    }

    private static byte[] EncodeAscii(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        byte[] bytes = new byte[text.Length + 1];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bytes[i] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
        }

        bytes[^1] = 0;
        return bytes;
    }
}
