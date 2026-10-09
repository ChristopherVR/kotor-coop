namespace KotorCoop.Plugin;

/// <summary>What the controller must do to the local player this frame.</summary>
[Flags]
internal enum PvpAction
{
    None = 0,

    /// <summary>Pin the player at 1 hp minimum (the engine's min-1-hit-point flag).</summary>
    SetPin = 1,

    /// <summary>Take the pin off again. Only ever returned for a pin this machine set.</summary>
    ClearPin = 2,

    /// <summary>Stand a downed player back up through the engine's Resurrection effect.</summary>
    Revive = 4,

    WriteHitPoints = 8,
    WriteForcePoints = 16,
}

/// <summary>One frame's answer: the actions, and the numbers the writes carry.</summary>
internal readonly record struct PvpStep(PvpAction Actions, int HitPoints = 0, int ForcePoints = 0)
{
    public static PvpStep Nothing => default;

    public bool Has(PvpAction action) => (Actions & action) != 0;
}

/// <summary>What the controller read from the engine this frame. Every field is a plain level.</summary>
internal readonly record struct PvpFacts(
    uint Phase, bool PlayerResolvable, bool Pinned, bool Downed,
    int HitPoints, int MaxHitPoints, int ForcePoints, int MaxForcePoints);

/// <summary>
/// The local player's half of a duel (docs/research/pvp-plan.md), as a pure state machine so the
/// parts that can strand a player are tested rather than described.
///
/// <list type="bullet">
/// <item><b>Arm</b> (phase leaves none). Snapshot hit points and Force points, and pin the player
/// at 1 hp minimum, so the engine never plays a real death and the campaign's down, game-over
/// and wipe-vote machinery never sees a duel. The pin is the engine's own flag, the one god mode
/// and the Taris duel ring use: if a module had already pinned the player the machine does not
/// claim it and so never takes it off.</item>
/// <item><b>Hold</b>. If something takes the pin off mid-duel (a module script), put it back,
/// once per observed loss rather than every frame.</item>
/// <item><b>Release</b> (phase returns to none, for any reason: the round ended, the host went
/// away, the session closed). Take off the pin it set, stand the player up if they are down, and
/// write the snapshot back, retrying a few frames apart until the engine reads it back, because
/// a hit-point write onto a body that still reads dead is dropped by the engine. A player who
/// is in another world for a while (the area changed) is restored when they come back; the
/// snapshot is kept for a generous number of frames.</item>
/// </list>
/// </summary>
internal sealed class PvpDuelMachine
{
    /// <summary>Frames between write attempts while restoring.</summary>
    public const int RetrySpacingFrames = 5;

    /// <summary>Write attempts per value before giving up and saying so.</summary>
    public const int MaxWriteAttempts = 6;

    /// <summary>Frames to keep trying to restore (about ten seconds at 60 fps) before the
    /// snapshot is dropped.</summary>
    public const int RestoreBudgetFrames = 600;

    private bool _active;
    private bool _haveSnapshot;
    private int _hitPoints;
    private int _forcePoints;
    private bool _weSetPin;
    private bool _restoring;
    private int _restoreFrames;
    private int _hpAttempts;
    private int _fpAttempts;
    private int _framesSinceHpWrite = RetrySpacingFrames;
    private int _framesSinceFpWrite = RetrySpacingFrames;
    private int _framesSinceRevive = RetrySpacingFrames;

    /// <summary>True from arming until the restore has finished or been given up: the window
    /// in which this player carries the duel's changes and a save would bake the pin in.</summary>
    public bool Holding => _active || _restoring;

    /// <summary>True when the last restore ran out of budget or attempts without the engine
    /// reading the snapshot back (the controller logs it once).</summary>
    public bool GaveUp { get; private set; }

    public int SnapshotHitPoints => _hitPoints;

    public int SnapshotForcePoints => _forcePoints;

    public PvpStep Step(in PvpFacts facts)
    {
        _framesSinceHpWrite++;
        _framesSinceFpWrite++;
        _framesSinceRevive++;

        bool duel = facts.Phase != 0;

        if (duel)
        {
            return Hold(facts);
        }

        if (_active)
        {
            _active = false;
            _restoring = _haveSnapshot;
            _restoreFrames = 0;
            _hpAttempts = 0;
            _fpAttempts = 0;
            GaveUp = false;
        }

        return _restoring ? Restore(facts) : PvpStep.Nothing;
    }

    private PvpStep Hold(in PvpFacts facts)
    {
        // A snapshot is only taken from a standing, resolvable player. A player who is down when
        // the duel arms (they can be: the host refuses to start one, but a hit can land in the
        // countdown's first frame) waits to be up; reading 0 hp as "how they were" would restore
        // them to the floor.
        if (!_active)
        {
            if (!facts.PlayerResolvable || facts.Downed)
            {
                return PvpStep.Nothing;
            }

            _active = true;
            GaveUp = false;

            // Re-arming before a previous restore finished keeps the OLD snapshot: the current
            // numbers are the duel's, not the player's own.
            if (!_restoring || !_haveSnapshot)
            {
                _hitPoints = facts.HitPoints;
                _forcePoints = facts.ForcePoints;
                _haveSnapshot = true;
            }

            _restoring = false;

            if (!facts.Pinned)
            {
                _weSetPin = true;
                return new PvpStep(PvpAction.SetPin);
            }

            return PvpStep.Nothing;
        }

        // Held: put a pin back that something removed. Observed-then-written, never blind.
        if (facts.PlayerResolvable && !facts.Pinned)
        {
            _weSetPin = true;
            return new PvpStep(PvpAction.SetPin);
        }

        return PvpStep.Nothing;
    }

    private PvpStep Restore(in PvpFacts facts)
    {
        _restoreFrames++;
        if (_restoreFrames > RestoreBudgetFrames)
        {
            GaveUp = true;
            Finish();
            return PvpStep.Nothing;
        }

        if (!facts.PlayerResolvable)
        {
            return PvpStep.Nothing; // another world is loading; the snapshot waits
        }

        PvpAction actions = PvpAction.None;

        if (_weSetPin && facts.Pinned)
        {
            actions |= PvpAction.ClearPin;
        }

        if (_weSetPin && !facts.Pinned)
        {
            _weSetPin = false; // already off (a module cleared it): nothing left to release
        }

        if (facts.Downed)
        {
            if (_framesSinceRevive >= RetrySpacingFrames)
            {
                _framesSinceRevive = 0;
                actions |= PvpAction.Revive;
            }

            // Hit points wait for the body to read alive.
            return actions == PvpAction.None ? PvpStep.Nothing : new PvpStep(actions);
        }

        int hpTarget = RestoreHitPoints(_hitPoints, facts.MaxHitPoints);
        int fpTarget = RestoreForcePoints(_forcePoints, facts.MaxForcePoints);

        bool hpDone = facts.HitPoints == hpTarget;
        bool fpDone = facts.ForcePoints == fpTarget;

        int hpWrite = 0;
        int fpWrite = 0;
        bool hpGaveUp = false;
        bool fpGaveUp = false;

        if (!hpDone && _framesSinceHpWrite >= RetrySpacingFrames)
        {
            if (_hpAttempts >= MaxWriteAttempts)
            {
                hpGaveUp = true;
                GaveUp = true;
            }
            else
            {
                _hpAttempts++;
                _framesSinceHpWrite = 0;
                actions |= PvpAction.WriteHitPoints;
                hpWrite = hpTarget;
            }
        }

        if (!fpDone && _framesSinceFpWrite >= RetrySpacingFrames)
        {
            if (_fpAttempts >= MaxWriteAttempts)
            {
                fpGaveUp = true;
                GaveUp = true;
            }
            else
            {
                _fpAttempts++;
                _framesSinceFpWrite = 0;
                actions |= PvpAction.WriteForcePoints;
                fpWrite = fpTarget;
            }
        }

        if ((actions & PvpAction.ClearPin) != 0)
        {
            _weSetPin = false;
        }

        // Settled: read back as the snapshot, or out of attempts (said once by the controller).
        // A value written this frame is not settled until a later frame reads it back.
        bool hpSettled = hpDone || hpGaveUp;
        bool fpSettled = fpDone || fpGaveUp;
        if (hpSettled && fpSettled)
        {
            Finish();
        }

        return actions == PvpAction.None ? PvpStep.Nothing : new PvpStep(actions, hpWrite, fpWrite);
    }

    private void Finish()
    {
        _restoring = false;
        _haveSnapshot = false;
        _weSetPin = false;
    }

    /// <summary>The hit points to write back: the snapshot, never below 1 (a duel never leaves a
    /// player on the floor) and never above the maximum the engine reports now.</summary>
    public static int RestoreHitPoints(int snapshot, int maxHitPoints) =>
        maxHitPoints > 0 ? Math.Clamp(snapshot, 1, maxHitPoints) : Math.Max(1, snapshot);

    /// <summary>The Force points to write back: the snapshot, within 0 and the current maximum.</summary>
    public static int RestoreForcePoints(int snapshot, int maxForcePoints) =>
        maxForcePoints > 0 ? Math.Clamp(snapshot, 0, maxForcePoints) : Math.Max(0, snapshot);
}

/// <summary>Which hits on the opponent's stand-in count in a live duel.</summary>
internal static class PvpHitRule
{
    /// <summary>Only the local player's own character. A companion's swing (its AI attacks any
    /// hostile body), a trap, a mine or a script has no place in a one-on-one: it is dropped
    /// before it is forwarded. An unknown local player (id 0) counts nothing rather than
    /// everything, because "everything" is how a third party gets into the fight.</summary>
    public static bool CountsInDuel(uint creatorId, uint localPlayerId) =>
        localPlayerId != 0 && creatorId == localPlayerId;
}
