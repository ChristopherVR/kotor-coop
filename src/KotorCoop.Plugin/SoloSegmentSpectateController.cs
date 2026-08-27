using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// SPECTATOR half of solo-segment spectate (docs/research/star-forge-finale.md §3.3/§3.5): a
/// player standing in the same module as a peer publishing <c>PlayerState.FlagSoloSegment</c>
/// (the OWNER of an active Bastila/Malak-style forced-solo fight — see
/// <see cref="SoloSegmentController"/> for that half) has their own input parked, their camera
/// pointed at the owner's phantom, and their own damage against the owner's enemy mirror gated
/// off — the product decision this feature exists for: the OWNER's fight, the other human
/// SPECTATES.
///
/// The DECIDING is <see cref="SoloSegmentRule.ShouldSpectate"/> (KotorCoop.Protocol, pure,
/// unit-tested); this class is engine reads/writes reacting to it.
///
/// ⚠ INPUT LOCK IS BEST-EFFORT AND NEEDS-LIVE. Two levers, stacked, because neither is proven
/// alone against this project's own "verify a write live before shipping it" rule:
///   * <c>creature+0xE8</c> (<see cref="Odyssey.ObjCommandable"/>) set to 0. Party-
///     reconciliation.md's own "trap 5" already found this does NOT stop a queued follow
///     ACTION (RunActions never reads it), so it is not trusted alone for anything the engine's
///     own AI drives — but the local PLAYER's real-time WASD/click-to-move input is a different
///     path, and this is the same flag <c>ClaimCreature</c> flips off for a claimed enemy.
///   * <see cref="Odyssey.HoldCreatureStill"/>, reasserted every pass, exactly the way
///     <c>InteractionLockController</c> already uses it to hold an NPC someone is talking to —
///     clears a queued MOVE-class head action (a click-to-move in flight) without touching a
///     fight action, so a mid-swing attack this pass already queued is not yanked.
/// Neither has been watched stop a real WASD press live. Reuses <c>DialogueSpectateController</c>'s
/// own precedent for "the safest answer to an unverified input-block question is the engine's
/// OWN camera/UI-ownership discipline" by ALSO deferring completely to a live conversation and
/// to the down-spectate camera, the same two things <c>SpectateController</c> defers to.
///
/// Kill switch: <c>solo_segment_spectate=0</c> in coop.ini (shared with
/// <see cref="SoloSegmentController"/> — one key gates both halves).
/// </summary>
internal sealed unsafe class SoloSegmentSpectateController(SharedBlockView block)
{
    private readonly bool _enabled = PluginConfig.ReadBool("solo_segment_spectate", false);

    /// <summary>Frames between passes — matches <c>SpectateController</c>'s own cadence. The
    /// cadence is a smoothness/cost trade for the camera reassert and the hold; the flag-scan
    /// that decides WHETHER to spectate is cheap enough to run every pass regardless, so a
    /// begin/end edge is never delayed by this — only the steady-state upkeep is.</summary>
    private const int IntervalFrames = 30;

    private const float NoticeLife = 0.5f;

    private static readonly byte[] NoticeSuffix = " is fighting alone - spectating"u8.ToArray();

    private readonly byte[] _noticeBuffer = new byte[SharedBlock.NameLength + NoticeSuffix.Length + 1];

    private int _countdown;

    /// <summary>0 while not spectating; otherwise the owner's phantom id this instance's
    /// camera is following.</summary>
    private uint _watching;

    /// <summary>The local player's own creature while its commandable flag is held down, so
    /// the exact pointer that was clamped is the one restored — never a fresh read at restore
    /// time, which could by then be a different creature after a revive/respawn.</summary>
    private nint _lockedCreature;

    /// <summary>True while THIS instance is spectating a peer's solo segment — read by
    /// <see cref="PeerDamageController"/> to stop crediting this player's damage against the
    /// owner's enemy mirror, on top of (not instead of) the input lock above. Static, matching
    /// <c>EnemyPresenceController.LocalCombat</c>'s own established cross-controller-flag
    /// pattern — both controllers run in the same process, on the same frame, so a plain static
    /// bool is simpler and cheaper than a second shared-block round trip.</summary>
    public static bool IsSpectating { get; private set; }

    public void Invalidate()
    {
        _watching = 0;
        _lockedCreature = 0;
        _countdown = 0;
        IsSpectating = false;
    }

    /// <summary>Session end: give input and the camera back immediately, the same "not the
    /// same as Invalidate" reasoning <see cref="SpectateController.ReleaseSession"/> documents
    /// — a disconnect must not leave the local player's own input clamped for the rest of the
    /// process just because nothing is polling this controller's command gate any more.</summary>
    public void ReleaseSession()
    {
        StopSpectating("the session ended — control is yours again");
    }

    public void OnFrame()
    {
        if (!_enabled || block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        SoloSegmentRule rule = new(Enabled: true);

        // Find the first remote slot publishing FlagSoloSegment. First match wins, same
        // reasoning MinigameNoticeController's own doc comment gives — a future round with
        // three-player content in mind could rotate; not needed yet, and this feature's own
        // product decision (docs/research/star-forge-finale.md) only ever names a two-player
        // duel.
        uint ownerPhantomId = 0;
        int ownerSlot = -1;
        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            RemoteView remote = block.GetRemote(i);
            if (!remote.Active || !rule.ShouldSpectate(remote.InSoloSegment, sameModule: true))
            {
                // Module match is implicit: CoopAgent.PumpOnce's own module filter already
                // excludes a peer standing somewhere else from ever reaching this block's
                // remote slots at all (see that filter's own doc comment) — a slot that is
                // Active here is, by construction, in this instance's module.
                continue;
            }

            uint id = block.GetPhantomId(i);
            if (id != 0)
            {
                ownerPhantomId = id;
                ownerSlot = i;
                break;
            }
        }

        if (ownerPhantomId == 0)
        {
            StopSpectating("the fight ended — control is yours again");
            return;
        }

        // Dialogue owns the camera completely — same deference SpectateController gives it.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        // The down-spectate camera takes priority over this one: a downed player is not
        // meaningfully able to spectate a duel either, and SpectateController is already
        // asserting its own follow this pass. Checked every pass (not just on entry) so a
        // down that lands MID-spectate hands the camera over cleanly instead of the two
        // controllers fighting each other's SetCameraFollow call frame to frame.
        nint controlled = Odyssey.GetPlayerCreaturePointer();
        if (controlled != 0 && Odyssey.IsDowned(controlled))
        {
            return;
        }

        IsSpectating = true;

        if (_watching != ownerPhantomId)
        {
            _watching = ownerPhantomId;
            PluginHost.Log($"solo segment: spectating 0x{ownerPhantomId:X} — input parked, "
                + "camera follows");
        }

        if (_countdown > 0)
        {
            _countdown--;
        }
        else
        {
            _countdown = IntervalFrames;

            // Both levers below are REASSERTED, not fire-and-forget: SetCameraFollow silently
            // no-ops in combat mode and a cutscene camera can take the follow back at any
            // time (same reasoning SpectateController's own class comment gives), and nothing
            // guarantees the engine's own AI or a follow-order reset never flips commandable
            // back to 1 between passes (party-reconciliation.md's own "trap 5" is exactly this
            // shape of surprise) — so the write below repeats rather than latching once.
            Odyssey.SetCameraFollowServerObject(ownerPhantomId);
            LockInput(controlled);
        }

        DrawNotice(ownerSlot);
    }

    private void LockInput(nint controlled)
    {
        if (controlled == 0)
        {
            return;
        }

        _lockedCreature = controlled;
        *(int*)(controlled + Odyssey.ObjCommandable) = 0;
        Odyssey.HoldCreatureStill(controlled);
    }

    private void StopSpectating(string reason)
    {
        if (_watching == 0 && !IsSpectating)
        {
            return;
        }

        _watching = 0;
        IsSpectating = false;
        _countdown = 0;

        if (_lockedCreature != 0)
        {
            *(int*)(_lockedCreature + Odyssey.ObjCommandable) = 1;
            _lockedCreature = 0;
        }

        if (Odyssey.RestoreCameraToControlledCreature())
        {
            PluginHost.Log($"solo segment: {reason}");
        }
    }

    /// <summary>Same shape as <c>MinigameNoticeController.Draw</c>: the peer's own published
    /// name plus a fixed suffix, with an "A peer" fallback for the (should-not-happen) case of
    /// a slot latching Active with no name ever written to it.</summary>
    private void DrawNotice(int slot)
    {
        int n = 0;
        int cap = _noticeBuffer.Length - NoticeSuffix.Length - 1;
        byte* name = block.GetRemoteName(slot);

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
            Odyssey.PostTextLine(p, 8, 10, NoticeLife);
        }
    }
}
