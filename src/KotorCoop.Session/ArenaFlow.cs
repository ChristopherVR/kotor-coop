using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;
using KotorCoop.Session.Pumps;

namespace KotorCoop.Session;

/// <summary>What a game should do about an arena round right now.</summary>
public enum ArenaStepKind
{
    None,

    /// <summary>Ask the plugin for a module change (the host only: the others follow it).</summary>
    Travel,

    /// <summary>Move this player's character to a place inside the module they are in.</summary>
    Teleport,
}

/// <summary>Why a step was asked for, so <see cref="ArenaFlow.Confirm"/> counts the right retries.</summary>
public enum ArenaPurpose
{
    None,

    /// <summary>The host's single warp into the arena.</summary>
    Enter,

    /// <summary>Standing on this player's spawn once in the arena.</summary>
    Place,

    /// <summary>The host's single warp back to where the party was.</summary>
    Return,

    /// <summary>Back at the original spot after the way back.</summary>
    Restore,

    /// <summary>The round let go while the host was still in the arena: one more try to take it home.</summary>
    Rescue,
}

public readonly record struct ArenaStep(
    ArenaStepKind Kind, ArenaPurpose Purpose = ArenaPurpose.None, string Module = "", Vector3f Position = default)
{
    public static ArenaStep None => default;
}

/// <summary>
/// One game's side of an arena round (docs/research/pvp-plan.md, phases 3 and 4), as a pure state
/// machine over the round the host holds (<see cref="PvpView"/>) and what this game can see of
/// itself. The host's round only decides the PHASE; this is what each game does with it:
///
/// <list type="bullet">
/// <item><b>Everyone</b> remembers where they stood (module and spot) the first time they see an
/// arena round, before anyone has moved.</item>
/// <item><b>Entering.</b> The HOST asks the plugin for ONE module change into the arena. The
/// retail engine crashes on rapid warps, so this is exactly one request per round (it is repeated
/// only if the plugin could not take it at all). The others follow the host the way they always
/// follow its module changes.</item>
/// <item><b>Countdown and live.</b> Once in the arena for a moment, each player is moved to their
/// spawn: the challenger to team A, the target to team B, anyone else to the spectator mark. The
/// landing is checked against the position the game reports, and retried a few times.</item>
/// <item><b>Leaving.</b> The HOST asks for ONE module change back to where it was, a couple of
/// seconds after the fight ended so the result can be read. Everyone, once back in that module,
/// is moved to the spot they stood on.</item>
/// <item><b>After the round.</b> If the round let go while the host was still in the arena (the
/// way back timed out, or the host's packets were lost), the host tries to take everyone home once
/// more, no more than once every 30 s.</item>
/// </list>
///
/// Everything is a level read off the round, never an edge, so a lost packet or a late agent
/// costs a retry and not a stranded party.
/// </summary>
public sealed class ArenaFlow
{
    /// <summary>How long a player must have been settled in a module before being moved inside it.</summary>
    public const long SettleBeforeActMs = 1_500;

    /// <summary>The pause between the end of the fight and the host's way back.</summary>
    public const long LeaveDelayMs = 2_000;

    public const long TeleportRetryMs = 1_000;
    public const int MaxTeleportTries = 6;
    public const long TravelRetryMs = 3_000;
    public const int MaxTravelTries = 5;

    /// <summary>Within this many units of the target spot counts as arrived.</summary>
    public const float LandedRadius = 3f;

    public const long RescueRetryMs = 30_000;
    public const long RescueAfterIdleMs = 5_000;

    /// <summary>The round is forgotten this long after it ends.</summary>
    public const long ForgetAfterMs = 90_000;

    private bool _tracking;
    private uint _round;
    private string _origin = string.Empty;
    private Vector3f _originPos;
    private bool _originKnown;
    private bool _enterSent;
    private int _enterTries;
    private long _enterLastMs;
    private bool _placed;
    private int _placeTries;
    private long _placeLastMs;
    private bool _returnSent;
    private int _returnTries;
    private long _returnLastMs;
    private bool _restored;
    private int _restoreTries;
    private long _restoreLastMs;
    private long _rescueLastMs;
    private long _inArenaSinceMs;
    private long _atOriginSinceMs;
    private long _leavingSinceMs;
    private long _idleSinceMs;

    /// <summary>Whether a round is being followed (for the log and the tests).</summary>
    public bool Tracking => _tracking;

    /// <summary>Where this game stood when the round began, when known.</summary>
    public (string Module, Vector3f Position, bool Known) Origin => (_origin, _originPos, _originKnown);

    /// <summary>The spawn this player takes in a round: the challenger on team A, the target on
    /// team B, anyone else (the host looking on, a third player) at the spectator mark.</summary>
    /// <summary>
    /// The Teleport cheat's facing word for a spot in the ring: face the ring's centre (between
    /// the two fighters' marks), so the two fighters look at each other instead of standing back
    /// to back (live 2026-10-10: the host faced the wall). Encoded as 1 + milliradians of
    /// (angle + pi); 0 means "keep the facing" to every other Teleport caller.
    /// </summary>
    public static int FacingTowardCentre(Vector3f at)
    {
        ArenaSpawn a = ArenaModule.SpawnFor(0, 0);
        ArenaSpawn b = ArenaModule.SpawnFor(1, 0);
        float cx = (a.X + b.X) / 2f;
        float cy = (a.Y + b.Y) / 2f;
        float dx = cx - at.X;
        float dy = cy - at.Y;
        if ((dx * dx) + (dy * dy) < 0.01f)
        {
            return 0;
        }

        return 1 + (int)MathF.Round((MathF.Atan2(dy, dx) + MathF.PI) * 1000f);
    }

    /// <summary>The east end of the ring holds the party in a boss fight: a row across it, the
    /// first player in the middle and the rest alternating to either side. The boss takes the
    /// west end (<see cref="BossSpot"/>).</summary>
    public static (float X, float Y, float Z) BossFightSpot(int slot)
    {
        ArenaSpawn a = ArenaModule.SpawnFor(0, 1);
        slot = Math.Clamp(slot, 0, 7);
        if (slot == 7)
        {
            return (a.X - 5f, a.Y, a.Z);
        }

        int step = (slot + 1) / 2;
        float side = slot % 2 == 1 ? 1f : -1f;
        return (a.X, a.Y + (side * step * 3.2f), a.Z);
    }

    /// <summary>A spot in the ring for someone the boss has thrown there (or for him): on a
    /// circle about the middle, <paramref name="turn"/> of the way round it (0 to 1) and
    /// <paramref name="out"/> of the way out (0 to 1) to where the two ends' marks are.</summary>
    public static (float X, float Y, float Z) RingSpot(double turn, double @out)
    {
        ArenaSpawn a = ArenaModule.SpawnFor(0, 1);
        ArenaSpawn b = ArenaModule.SpawnFor(1, 1);
        float cx = (a.X + b.X) / 2f;
        float cy = (a.Y + b.Y) / 2f;
        float reach = MathF.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y))) / 2f;
        float radius = reach * (float)Math.Clamp(@out, 0.0, 1.0);
        float angle = (float)(turn * 2.0 * Math.PI);
        return (cx + (radius * MathF.Cos(angle)), cy + (radius * MathF.Sin(angle)), a.Z);
    }

    /// <summary>Where the boss lands when he blinks to someone standing at
    /// <paramref name="at"/>: beside them, on the side the middle of the ring is, so it is
    /// floor.</summary>
    public static (float X, float Y, float Z) BesideInRing(Vector3f at, float distance = 2.6f)
    {
        (float cx, float cy, _) = RingSpot(0, 0);
        float dx = cx - at.X;
        float dy = cy - at.Y;
        float length = MathF.Sqrt((dx * dx) + (dy * dy));
        (dx, dy) = length < 0.5f ? (1f, 0f) : (dx / length, dy / length);
        return (at.X + (dx * distance), at.Y + (dy * distance), at.Z);
    }

    /// <summary>Where the boss appears: the west end, where a duel's second fighter stands.</summary>
    public static (float X, float Y, float Z) BossSpot
    {
        get
        {
            ArenaSpawn b = ArenaModule.SpawnFor(1, 1);
            return (b.X, b.Y, b.Z);
        }
    }

    public static (float X, float Y, float Z) SpawnOf(in PvpView view, uint localPeerId, int slot = 0)
    {
        if (view.Boss)
        {
            return BossFightSpot(slot);
        }

        if (localPeerId == view.Challenger)
        {
            ArenaSpawn a = ArenaModule.SpawnFor(0, 0);
            return (a.X, a.Y, a.Z);
        }

        if (localPeerId == view.Target)
        {
            ArenaSpawn b = ArenaModule.SpawnFor(1, 0);
            return (b.X, b.Y, b.Z);
        }

        ArenaSpawn s = ArenaModule.Spectator;
        return (s.X, s.Y, s.Z);
    }

    private static bool Landed(Vector3f at, float x, float y) =>
        ((at.X - x) * (at.X - x)) + ((at.Y - y) * (at.Y - y)) <= LandedRadius * LandedRadius;

    private static bool Settled(string module) =>
        module.Length > 0 && !ModuleNameGuard.IsFallbackModuleName(module);

    /// <summary>What to do this tick. Nothing is latched here: the caller carries the step out
    /// and reports back with <see cref="Confirm"/>.</summary>
    /// <param name="slot">Boss fights only: this player's place in the party's row (0 for the
    /// lowest peer id, and so on), so nobody is put on top of anybody else.</param>
    public ArenaStep Step(
        in PvpView view, uint localPeerId, bool isHost, string module, Vector3f position, long nowMs, int slot = 0)
    {
        bool settled = Settled(module);
        bool inArena = settled && string.Equals(module, PvpDuelRound.ArenaModule, StringComparison.OrdinalIgnoreCase);
        bool atOrigin = settled && _originKnown && string.Equals(module, _origin, StringComparison.OrdinalIgnoreCase);

        _inArenaSinceMs = inArena ? (_inArenaSinceMs == 0 ? nowMs : _inArenaSinceMs) : 0;
        _atOriginSinceMs = atOrigin ? (_atOriginSinceMs == 0 ? nowMs : _atOriginSinceMs) : 0;

        if (!view.ArenaActive)
        {
            return StepAfterRound(isHost, inArena, atOrigin, position, nowMs);
        }

        _idleSinceMs = 0;
        if (!_tracking || _round != view.RoundId)
        {
            Begin(view.RoundId);
        }

        // Remember where we stood. Only before the first move, and only from a settled module that
        // is not the arena itself (an agent that starts mid-round simply has no way home to offer).
        if (!_originKnown && settled && !inArena)
        {
            _origin = module;
            _originPos = position;
            _originKnown = true;
        }

        switch (view.Phase)
        {
            case PvpPhase.Entering:
                if (isHost && !_enterSent && _originKnown && !inArena && settled
                    && _enterTries < MaxTravelTries && (_enterTries == 0 || nowMs - _enterLastMs >= TravelRetryMs))
                {
                    return new ArenaStep(ArenaStepKind.Travel, ArenaPurpose.Enter, PvpDuelRound.ArenaModule);
                }

                return ArenaStep.None;

            case PvpPhase.Countdown:
            case PvpPhase.Live:
                return PlaceStep(view, localPeerId, inArena, position, nowMs, slot);

            case PvpPhase.Leaving:
                if (_leavingSinceMs == 0)
                {
                    _leavingSinceMs = nowMs;
                }

                if (isHost && !_returnSent && _originKnown && inArena
                    && nowMs - _leavingSinceMs >= LeaveDelayMs
                    && _returnTries < MaxTravelTries && (_returnTries == 0 || nowMs - _returnLastMs >= TravelRetryMs))
                {
                    return new ArenaStep(ArenaStepKind.Travel, ArenaPurpose.Return, _origin);
                }

                return RestoreStep(atOrigin, position, nowMs);

            default:
                return ArenaStep.None;
        }
    }

    private ArenaStep PlaceStep(
        in PvpView view, uint localPeerId, bool inArena, Vector3f position, long nowMs, int slot)
    {
        if (_placed || !inArena || nowMs - _inArenaSinceMs < SettleBeforeActMs)
        {
            return ArenaStep.None;
        }

        (float x, float y, float z) = SpawnOf(view, localPeerId, slot);
        if (Landed(position, x, y))
        {
            _placed = true;
            return ArenaStep.None;
        }

        if (_placeTries >= MaxTeleportTries)
        {
            // Give up quietly: the fight goes ahead from wherever this player stands.
            _placed = true;
            return ArenaStep.None;
        }

        return _placeTries == 0 || nowMs - _placeLastMs >= TeleportRetryMs
            ? new ArenaStep(ArenaStepKind.Teleport, ArenaPurpose.Place, Position: new Vector3f(x, y, z))
            : ArenaStep.None;
    }

    private ArenaStep RestoreStep(bool atOrigin, Vector3f position, long nowMs)
    {
        if (_restored || !atOrigin || nowMs - _atOriginSinceMs < SettleBeforeActMs)
        {
            return ArenaStep.None;
        }

        if (Landed(position, _originPos.X, _originPos.Y))
        {
            _restored = true;
            return ArenaStep.None;
        }

        if (_restoreTries >= MaxTeleportTries)
        {
            _restored = true;
            return ArenaStep.None;
        }

        return _restoreTries == 0 || nowMs - _restoreLastMs >= TeleportRetryMs
            ? new ArenaStep(ArenaStepKind.Teleport, ArenaPurpose.Restore, Position: _originPos)
            : ArenaStep.None;
    }

    /// <summary>No arena round is running. If one just was, finish what it left undone.</summary>
    private ArenaStep StepAfterRound(bool isHost, bool inArena, bool atOrigin, Vector3f position, long nowMs)
    {
        if (!_tracking)
        {
            return ArenaStep.None;
        }

        if (_idleSinceMs == 0)
        {
            _idleSinceMs = nowMs;
        }

        long idleFor = nowMs - _idleSinceMs;

        // The round let go (the way back timed out, or the host's packets stopped) with the host
        // still standing in the arena: take the party home, but not in a hurry and not in a loop.
        if (isHost && inArena && _originKnown && idleFor >= RescueAfterIdleMs
            && (_rescueLastMs == 0 || nowMs - _rescueLastMs >= RescueRetryMs))
        {
            return new ArenaStep(ArenaStepKind.Travel, ArenaPurpose.Rescue, _origin);
        }

        // A late arrival home still gets put back on its spot.
        ArenaStep restore = RestoreStep(atOrigin, position, nowMs);
        if (restore.Kind != ArenaStepKind.None)
        {
            return restore;
        }

        bool finished = (_restored || !_originKnown) && !inArena && idleFor >= RescueAfterIdleMs;
        if (finished || idleFor >= ForgetAfterMs)
        {
            _tracking = false;
        }

        return ArenaStep.None;
    }

    private void Begin(uint round)
    {
        _tracking = true;
        _round = round;
        _origin = string.Empty;
        _originPos = default;
        _originKnown = false;
        _enterSent = false;
        _enterTries = 0;
        _enterLastMs = 0;
        _placed = false;
        _placeTries = 0;
        _placeLastMs = 0;
        _returnSent = false;
        _returnTries = 0;
        _returnLastMs = 0;
        _restored = false;
        _restoreTries = 0;
        _restoreLastMs = 0;
        _rescueLastMs = 0;
        _leavingSinceMs = 0;
        _idleSinceMs = 0;
    }

    /// <summary>The caller tried <paramref name="step"/>; <paramref name="accepted"/> is whether the
    /// plugin took the request (a teleport is only counted, its landing is read from the position).</summary>
    public void Confirm(ArenaStep step, bool accepted, long nowMs)
    {
        switch (step.Purpose)
        {
            case ArenaPurpose.Enter:
                _enterTries++;
                _enterLastMs = nowMs;
                _enterSent |= accepted;
                break;

            case ArenaPurpose.Return:
                _returnTries++;
                _returnLastMs = nowMs;
                _returnSent |= accepted;
                break;

            case ArenaPurpose.Rescue:
                _rescueLastMs = nowMs;
                break;

            case ArenaPurpose.Place:
                _placeTries++;
                _placeLastMs = nowMs;
                break;

            case ArenaPurpose.Restore:
                _restoreTries++;
                _restoreLastMs = nowMs;
                break;
        }
    }

    /// <summary>The session ended: forget everything.</summary>
    public void Reset()
    {
        Begin(0);
        _tracking = false;
    }
}
