namespace KotorCoop.Plugin;

/// <summary>
/// The "&lt;name&gt; is in a minigame - waiting" HUD line (item 8 task 3): shown for a
/// REMOTE peer's slot while <see cref="RemoteView.InMinigame"/> was last observed true,
/// cleared once it is seen to clear (<see cref="MinigameNoticeRule"/>). Behind coop.ini
/// <c>minigame_wait_notice</c> (default ON).
///
/// ⚠ KNOWN LIMITATION, not a bug — see <see cref="MinigameNoticeRule"/>'s own doc comment for
/// the full mechanics. In short: <c>CoopAgent.cs</c>'s peer-visibility filter
/// (<c>minigame_hide_peer</c>, default ON) excludes a peer from what it writes to this block
/// in the SAME tick their flag turns true, so <see cref="RemoteView.InMinigame"/> can only
/// ever be observed true here while <c>minigame_hide_peer=0</c> (the peer stays mirrored, not
/// parked). Under the shipped default, a peer's phantom is still destroyed exactly as before
/// (see <c>PhantomController.Drive.cs</c>'s <c>!remote.Active</c> branch, which this
/// controller deliberately does NOT touch — no change to when or how a phantom is parked,
/// only this notice's own visibility), but this specific notice will not currently fire for
/// that transition. Left as a documented gap in docs/research/minigames.md's dated section
/// rather than worked around with a guess about WHY a slot went inactive (peer left vs. peer
/// parked look identical from here) — a wrong guess would show a misleading "in a minigame"
/// line for a player who simply disconnected, which is worse than the line not showing.
/// </summary>
internal sealed unsafe class MinigameNoticeController(SharedBlockView block)
{
    // Row 8: rows 0-2 are the console's own lines, row 3 is ConsoleAssistController's hint,
    // row 4 is DialogueSpectateController's "click to join", row 5 is
    // AgentLivenessController's stale-agent warning, row 6 is InteractionLockController's
    // "walk over to join" far notice and row 7 its container-lock notice (both documented on
    // that class's DrawFarNotice/DrawContainerNotice). Row 8 is the first one still free on
    // the in-world HUD; ServerBrowserController's FirstEntryRow (also 8) only ever draws
    // while the main-menu browser panel is open, a different screen this notice never shares
    // a frame with.
    private const int NoticeColumn = 8;
    private const int NoticeRow = 8;
    private const float NoticeLife = 0.5f;

    private static readonly byte[] NoticeSuffix = " is in a minigame - waiting"u8.ToArray();

    private readonly bool _enabled = PluginConfig.ReadBool("minigame_wait_notice", true);

    private readonly bool[] _waiting = new bool[SharedBlock.SlotCapacity];

    private readonly byte[] _textBuffer = new byte[SharedBlock.NameLength + NoticeSuffix.Length + 1];

    public void OnFrame()
    {
        if (!_enabled)
        {
            return;
        }

        int shownSlot = -1;

        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            RemoteView remote = block.GetRemote(i);
            bool next = MinigameNoticeRule.NextWaiting(_waiting[i], remote.Active, remote.InMinigame);

            if (next != _waiting[i])
            {
                _waiting[i] = next;
                PluginHost.Log(next
                    ? $"minigame wait: slot {i} peer flagged in a minigame — showing wait notice"
                    : $"minigame wait: slot {i} notice cleared");
            }

            // First match wins: the HUD has one row for this. A future round with three-
            // player content in mind could rotate or list names; not needed yet.
            if (next && shownSlot < 0)
            {
                shownSlot = i;
            }
        }

        if (shownSlot >= 0)
        {
            Draw(shownSlot);
        }
    }

    private void Draw(int slot)
    {
        int n = 0;
        byte* name = block.GetRemoteName(slot);
        int cap = _textBuffer.Length - NoticeSuffix.Length - 1;

        while (n < cap && name[n] != 0)
        {
            _textBuffer[n] = name[n];
            n++;
        }

        if (n == 0)
        {
            // A slot latched "waiting" without ever having a name written to it — should not
            // happen (the rule only latches true while Active, and TrySpawnOne writes the
            // name before anything else could observe Active), but fall back rather than post
            // a bare " is in a minigame - waiting" line.
            foreach (byte b in "A peer"u8)
            {
                _textBuffer[n++] = b;
            }
        }

        foreach (byte b in NoticeSuffix)
        {
            _textBuffer[n++] = b;
        }

        // Explicit terminator every draw, unlike this codebase's other reused notice
        // buffers (e.g. InteractionLockController's container notice) — this one is never
        // longer than a fixed worst case, so there is no cost to always writing it, and it
        // means a shorter name after a longer one can never leave a stale byte visible.
        _textBuffer[n] = 0;

        fixed (byte* p = _textBuffer)
        {
            Odyssey.PostTextLine(p, NoticeColumn, NoticeRow, NoticeLife);
        }
    }
}
