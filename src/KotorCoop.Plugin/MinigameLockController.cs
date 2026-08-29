namespace KotorCoop.Plugin;

/// <summary>
/// The waiting player's half of "one player races, everybody else waits"
/// (docs/research/minigames.md §24): while the agent asserts
/// <see cref="SharedBlockView.MinigameLockActive"/> — i.e. a PEER is inside a swoop race or a
/// turret sequence — this instance's own player is held still and told, on screen, who is
/// racing. Released the instant the flag clears, which the agent does when the racer's own
/// flag clears, when they disconnect, or when <c>minigame_lock_timeout_s</c> runs out.
///
/// The DECIDING is <c>KotorCoop.Protocol.MinigameLockRule</c>, agent-side, where the peer
/// roster lives (see <see cref="SharedBlock.OffsetMinigameLockActive"/> for why the direction
/// is agent -> plugin and not the other way round). This class is engine reads/writes only.
///
/// ⚠ INPUT LOCK IS BEST-EFFORT, and deliberately the SAME two levers
/// <see cref="SoloSegmentSpectateController"/> already stacks — not a third invention:
///   * <c>creature+0xE8</c> (<see cref="Odyssey.ObjCommandable"/>) set to 0, the flag
///     <c>ClaimCreature</c> flips off for a claimed enemy;
///   * <see cref="Odyssey.HoldCreatureStill"/>, reasserted every pass, which clears a queued
///     MOVE-class head action (a click-to-move in flight) without touching a fight action.
/// Neither has been watched stop a physical WASD press live, because injected keystrokes never
/// reach this game (docs/live-verification.md) — so that half stays NEEDS-LIVE here exactly as
/// it does there. What IS verifiable, and what this feature actually turns on, is the pair of
/// consequences that matter more than the keyboard: the follow-warp onto the single-seat track
/// is refused agent-side (<c>MinigameModuleRule</c>), and the waiting player is TOLD what is
/// happening instead of standing in an unexplained frozen world.
///
/// Defers to a live conversation and to the down-spectate camera for the same reason
/// <see cref="SoloSegmentSpectateController"/> does: those two own the player's screen already,
/// and fighting them frame to frame is worse than briefly not asserting.
///
/// Kill switch: <c>minigame_lock_peers=0</c> in coop.ini — read agent-side, so with the key off
/// the flag is never asserted and this controller never does anything. It reads no key of its
/// own on purpose: two readers of one key is exactly the drift <c>docs/CONFIG.md</c>'s own
/// footnote warns about, and this half has no decision to make.
/// </summary>
internal sealed unsafe class MinigameLockController(SharedBlockView block)
{
    /// <summary>Frames between reasserts — the same cadence
    /// <see cref="SoloSegmentSpectateController"/> uses, and for the same reason: the
    /// begin/end EDGE is judged every frame (it is one dword read), only the steady-state
    /// upkeep is throttled.</summary>
    private const int IntervalFrames = 30;

    private const float NoticeLife = 0.5f;

    /// <summary>Row 9: row 8 is <see cref="MinigameNoticeController"/>'s own
    /// "&lt;name&gt; is in a minigame - waiting" line, which under the shipped
    /// <c>minigame_hide_peer=1</c> cannot fire at all (see that class's KNOWN LIMITATION) —
    /// but the two must not collide in the configuration where it can.</summary>
    private const int NoticeColumn = 8;

    private const int NoticeRow = 9;

    private static readonly byte[] NoticeSuffix = " is racing - waiting"u8.ToArray();

    private readonly byte[] _noticeBuffer = new byte[SharedBlock.NameLength + NoticeSuffix.Length + 1];

    private int _countdown;

    private bool _locked;

    /// <summary>The creature whose commandable flag is held down, so the exact pointer that
    /// was clamped is the one restored — never a fresh read at restore time, which could by
    /// then be a different creature after a revive, a respawn or a module change. Same
    /// discipline as <see cref="SoloSegmentSpectateController"/>'s own <c>_lockedCreature</c>.</summary>
    private nint _lockedCreature;

    /// <summary>True while THIS instance is held for a peer's minigame. Static for the same
    /// reason <see cref="SoloSegmentSpectateController.IsSpectating"/> is: any other
    /// controller that wants to know runs in the same process on the same frame, so a plain
    /// static bool beats a second shared-block round trip.</summary>
    public static bool IsLocked { get; private set; }

    /// <summary>World teardown: forget the clamped pointer without touching it. The creature
    /// it names is being freed; the fresh world's player starts unclamped either way, and the
    /// agent re-asserts the flag next tick if the peer is still racing.</summary>
    public void Invalidate()
    {
        _locked = false;
        _lockedCreature = 0;
        _countdown = 0;
        IsLocked = false;
    }

    /// <summary>Session end: give control back immediately rather than waiting for an agent
    /// that is no longer running to clear the flag — the same "not the same as Invalidate"
    /// reasoning <see cref="SoloSegmentSpectateController.ReleaseSession"/> documents.</summary>
    public void ReleaseSession()
    {
        Release("the session ended");
    }

    public void OnFrame()
    {
        bool wanted = block.MinigameLockActive != 0;

        if (!wanted)
        {
            Release("the minigame ended");
            return;
        }

        // A conversation owns the screen and the player's actions completely — clamping
        // through one is how a dialogue ends up stuck. Checked every pass, not just on entry.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        nint controlled = Odyssey.GetPlayerCreaturePointer();
        if (controlled == 0)
        {
            return;
        }

        // A downed player is already not going anywhere, and SpectateController is asserting
        // its own camera on them this pass; holding them as well would just fight it.
        if (Odyssey.IsDowned(controlled))
        {
            return;
        }

        if (!_locked)
        {
            _locked = true;
            IsLocked = true;
            PluginHost.Log("minigame lock: a peer is racing — this player is held until it ends");
        }

        if (_countdown > 0)
        {
            _countdown--;
        }
        else
        {
            _countdown = IntervalFrames;

            // REASSERTED, not fire-and-forget: nothing guarantees the engine's own AI or a
            // follow-order reset never flips commandable back to 1 between passes
            // (party-reconciliation.md's "trap 5" is exactly this shape of surprise).
            _lockedCreature = controlled;
            *(int*)(controlled + Odyssey.ObjCommandable) = 0;
            Odyssey.HoldCreatureStill(controlled);
        }

        DrawNotice();
    }

    private void Release(string reason)
    {
        if (!_locked && _lockedCreature == 0)
        {
            return;
        }

        _locked = false;
        IsLocked = false;
        _countdown = 0;

        if (_lockedCreature != 0)
        {
            *(int*)(_lockedCreature + Odyssey.ObjCommandable) = 1;
            _lockedCreature = 0;
        }

        PluginHost.Log($"minigame lock: {reason} — control is yours again");
    }

    /// <summary>Same shape as <see cref="MinigameNoticeController.Draw"/>: the racer's own
    /// published name plus a fixed suffix, with an "A peer" fallback for a lock asserted
    /// before any name reached the block.</summary>
    private void DrawNotice()
    {
        int n = 0;
        int cap = _noticeBuffer.Length - NoticeSuffix.Length - 1;
        byte* name = block.MinigameLockName;

        while (n < cap && name[n] != 0)
        {
            _noticeBuffer[n] = name[n];
            n++;
        }

        if (n == 0)
        {
            foreach (byte b in "A peer"u8)
            {
                _noticeBuffer[n++] = b;
            }
        }

        foreach (byte b in NoticeSuffix)
        {
            _noticeBuffer[n++] = b;
        }

        _noticeBuffer[n] = 0;

        fixed (byte* p = _noticeBuffer)
        {
            Odyssey.PostTextLine(p, NoticeColumn, NoticeRow, NoticeLife);
        }
    }
}
