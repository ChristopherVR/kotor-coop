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
    // every PostTextLine call site.
    //
    // 2026-09-10: moved from (3, 6) to (8, 3) — row 6 at column 3 sits inside the minimap's own
    // on-screen footprint (the minimap fills the top-left corner; see
    // docs/research/minimap-peers.md), so the persistent indicator was drawing directly over it
    // every frame (player-reported: the two visually overlap).
    //
    // The first fix tried was (3, 11) — reusing ChatOverlayController's top-left anchor row, on
    // the theory that a row already documented as clearing the minimap must be safe. Live-tested
    // with tools\textqueue.ps1 and rejected: at session connect, several toasts
    // (compatibility/peer-join notices) legitimately stack up to ChatOverlayController's default
    // `chat_overlay_lines = 5`, and even under the DEFAULT `chat_overlay_anchor = bottom-left`
    // that stack reaches up to row 14-(5-1) = row 10, one above 11 — the queue dump below caught
    // both this controller's line and a compatibility-notice line sharing (3, 11) at once:
    //   [0] col=3 row=11 'co-op [W_GIT] module END_M01AA is modded differently on t...'
    //   [3] col=3 row=11 'co-op: connected, 1 player'
    // (2026-09-10 live session, docs/research/console-overlay.md's addendum has the rest.) Column
    // 3 is exactly the column every chat anchor's LEFT variant uses, so any row in that column is
    // only ever one bad connect-burst away from this exact collision — swapping columns, not
    // hunting for a taller gap, is what actually avoids it.
    //
    // Column 8 is the fix: ChatOverlayController only ever posts at column 3 (left anchors) or
    // column 50 (right anchors) — never column 8, by construction, regardless of the player's
    // `chat_overlay_anchor` setting. So column 8 cannot collide with chat/toasts no matter how a
    // player configures it, which is a stronger guarantee than "clears the minimap" alone gave
    // (3, 11). Row 3 is shallow (near the top, as requested); live-verified with
    // tools\textqueue.ps1 against a running two-instance demo (no working screenshot tool on
    // this rig — see docs/research/console-overlay.md's 2026-09-10 addendum for the actual
    // queue dumps): (8, 3) never collided with the column-3 toast burst on connect, and the
    // burst itself faded out on its own life countdown a few seconds later, leaving only this
    // controller's line.
    //
    // Other reserved slots, so a future controller does not collide either: console prompt/line
    // own rows 0-2; ConsoleAssistController's hint sits at (3, 3); AgentLivenessController's
    // stale/failed-to-start warning sits at (3, 5); ServerBrowserController uses column 8 (title
    // row 6, entries from row 8) and DialogueSpectateController / InteractionLockController use
    // column 8 rows 4/6/7, SoloSegmentSpectateController row 10 — all below this row 3.
    private const int MessageColumn = 8;
    private const int MessageRow = 3;
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
