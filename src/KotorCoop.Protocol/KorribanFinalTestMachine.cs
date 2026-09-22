namespace KotorCoop.Protocol;

/// <summary>
/// Tracks Korriban's player final-test solo boundary from its exact story-script dispatches.
/// The start is the <c>k_pkor_partyleav</c> dialogue action in m35; completion requires a later
/// m35 <c>kor35_enter</c> area-script dispatch, after visiting the final-test route, with the
/// progression threshold and the original companion slots restored. The session and world-load
/// serial are part of the evidence so an old global or a loaded save cannot complete a new
/// segment by coincidence.
/// </summary>
public sealed class KorribanFinalTestMachine
{
    public const string StartModule = "korr_m35aa";
    public const string StartScript = "k_pkor_partyleav";
    public const string ReturnScript = "kor35_enter";

    private const uint PartyRosterMask = (1u << 9) - 1;

    private static readonly string[] RouteModules =
    [
        "korr_m36aa",
        "korr_m37aa",
        "korr_m38aa",
        "korr_m38ab",
        "korr_m39aa",
    ];

    private uint _worldLoadSerial;
    private uint _lastDialogueSequence;
    private uint _lastAreaSequence;
    private bool _visitedRoute;
    private bool _returnDispatchPending;

    /// <summary>Whether this exact story path currently owns the solo-segment level.</summary>
    public bool Active { get; private set; }

    /// <summary>The NPC-slot identity mask present immediately before the start script runs.</summary>
    public uint InitialPartyMask { get; private set; }

    /// <summary>True after the exact return script has been observed and is awaiting its
    /// progression/roster checks. This lets the controller avoid global-table reads on ordinary
    /// frames.</summary>
    public bool HasPendingReturnDispatch => _returnDispatchPending;

    /// <summary>Whether the most recent Step completed the exact return condition rather
    /// than cancelling the latch on a session/load/module boundary.</summary>
    public bool CompletedLastStep { get; private set; }

    /// <summary>The load serial captured at the start dispatch, for lifecycle invalidation.</summary>
    public uint StartWorldLoadSerial => _worldLoadSerial;

    /// <summary>Accepts only the exact dialogue action in an active session and snapshots the
    /// party mask before the script can remove anyone. A repeated action while active does not
    /// reset the original evidence.</summary>
    public bool TryBegin(
        string? module,
        string? scriptResref,
        bool sessionActive,
        uint worldLoadSerial,
        uint partyMask,
        uint dispatchSequence)
    {
        if (Active
            || !sessionActive
            || !IsNamed(module, StartModule)
            || !IsNamed(scriptResref, StartScript)
            || dispatchSequence == 0
            || dispatchSequence <= _lastDialogueSequence
            || (partyMask & ~PartyRosterMask) != 0)
        {
            return false;
        }

        _lastDialogueSequence = dispatchSequence;
        _worldLoadSerial = worldLoadSerial;
        InitialPartyMask = partyMask;
        _visitedRoute = false;
        _lastAreaSequence = 0;
        _returnDispatchPending = false;
        Active = true;
        return true;
    }

    /// <summary>Records the exact m35 area OnEnter script before the VM executes it. It is only
    /// eligible after a verified route departure, so the initial m35 entry cannot end a segment.</summary>
    public bool ObserveAreaScript(
        string? module,
        string? scriptResref,
        bool sessionActive,
        uint worldLoadSerial,
        uint dispatchSequence)
    {
        if (!Active
            || !_visitedRoute
            || !sessionActive
            || worldLoadSerial != _worldLoadSerial
            || !IsNamed(module, StartModule)
            || !IsNamed(scriptResref, ReturnScript)
            || dispatchSequence == 0
            || dispatchSequence <= _lastAreaSequence)
        {
            return false;
        }

        _lastAreaSequence = dispatchSequence;
        _returnDispatchPending = true;
        return true;
    }

    /// <summary>Advances the bounded latch. Any session loss, unannounced world load, or move
    /// outside the verified Korriban route cancels it. Return dispatches are consumed once; an
    /// early m35 entry with a low final-test value cannot become valid later from stale state.</summary>
    public bool Step(
        string? module,
        bool sessionActive,
        uint worldLoadSerial,
        uint currentPartyMask,
        int finalTestValue,
        bool partyRosterKnown = true,
        bool finalTestValueKnown = true)
    {
        CompletedLastStep = false;
        if (!Active)
        {
            return false;
        }

        if (!sessionActive
            || worldLoadSerial != _worldLoadSerial
            || (!string.IsNullOrEmpty(module) && !IsRouteModule(module)))
        {
            Reset();
            return true;
        }

        // During ordinary transitions PluginHost briefly has no readable module name. That is
        // missing evidence, not evidence that the player left the story route.
        if (string.IsNullOrEmpty(module))
        {
            return false;
        }

        if (!IsNamed(module, StartModule))
        {
            _visitedRoute = true;
            return false;
        }

        if (!_visitedRoute || !_returnDispatchPending)
        {
            return false;
        }

        // A transition can briefly leave either native roster access or the embedded global
        // table unavailable. Keep the exact return dispatch pending until both inputs can be
        // checked; missing evidence must not consume a valid boundary.
        if (!partyRosterKnown || !finalTestValueKnown)
        {
            return false;
        }

        _returnDispatchPending = false;
        if (finalTestValue <= 3 || (currentPartyMask & InitialPartyMask) != InitialPartyMask)
        {
            return false;
        }

        Reset();
        CompletedLastStep = true;
        return true;
    }

    /// <summary>Clears the story latch. Called on co-op session release and on every load-serial
    /// edge; ordinary module transitions do not call this path.</summary>
    public void Reset()
    {
        Active = false;
        InitialPartyMask = 0;
        _worldLoadSerial = 0;
        _visitedRoute = false;
        _returnDispatchPending = false;
        _lastAreaSequence = 0;
        CompletedLastStep = false;
        // Keep the last dialogue sequence across resets. It is process-local deduplication, not
        // story state, and prevents a delayed duplicate observation from starting a new latch.
    }

    public static bool IsRouteModule(string? module)
    {
        if (IsNamed(module, StartModule))
        {
            return true;
        }

        if (string.IsNullOrEmpty(module))
        {
            return false;
        }

        foreach (string routeModule in RouteModules)
        {
            if (string.Equals(module, routeModule, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsNamed(string? value, string expected) =>
        string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);
}
