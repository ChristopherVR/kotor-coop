namespace KotorCoop.Plugin;

/// <summary>What a control-return pass should do this time round.</summary>
internal enum ControlReturnAction
{
    /// <summary>Leave everything alone.</summary>
    Nothing,

    /// <summary>The session owns the body the player is driving. Say so, and arm the camera
    /// hand-back for the moment control comes back — do NOT move the camera yet, because the
    /// camera is currently on the thing the player is still driving.</summary>
    NoteClaim,

    /// <summary>Control is back on the player's own character; point the camera at it.</summary>
    AssertCamera,
}

/// <summary>
/// The decision half of <c>ControlReturnController</c>, separated so it can be unit-tested:
/// the controller's other half is engine calls and the shared block, neither of which exists
/// in the test runner (the same split <c>SpectateCycle</c> uses).
///
/// The question it answers, once per pass: a co-op client is locked to its own PC, so what
/// should happen when the player is driving — or watching — a companion the session has
/// taken over?
/// </summary>
internal static class ControlReturnRule
{
    /// <summary>
    /// Decides the pass and returns the new re-assert budget.
    ///
    /// <paramref name="mirroring"/> is "a session drives this instance's world" (a client;
    /// a host owns its companions and is left alone entirely).
    /// <paramref name="cameraOwnedElsewhere"/> is a conversation showing or the player being
    /// down — dialogue and the spectate camera both own the view outright, and fighting either
    /// is how you get a camera that jitters between two owners.
    /// <paramref name="controlledIsSessionOwned"/> is "the creature the player drives is a
    /// party-roster companion", which on a client is never legitimate.
    /// <paramref name="assertsLeft"/> is the remaining budget from the previous pass.
    ///
    /// Rules, in order:
    /// - not mirroring: nothing, and the budget is dropped — the player owns their own game
    ///   again and the camera is theirs to point wherever they like.
    /// - camera owned elsewhere: nothing, and the budget is KEPT, so a hand-back that was due
    ///   when a conversation opened still happens after it closes.
    /// - the session owns what is being driven: note it and arm the full budget. No camera
    ///   move: control has not come back yet, so "the controlled creature" is still the
    ///   companion, and asserting would simply re-point the camera at it.
    /// - otherwise, while the budget lasts: assert, spending one.
    ///
    /// The budget exists because <c>SetCameraFollow</c> silently no-ops while the client is in
    /// combat mode (docs/research/spectate-camera.md), so a single call lands or is lost with
    /// no way to tell which. Re-asserting a bounded number of times converges through a fight
    /// without becoming a permanent camera owner.
    /// </summary>
    public static (ControlReturnAction Action, int AssertsLeft) Decide(
        bool mirroring,
        bool cameraOwnedElsewhere,
        bool controlledIsSessionOwned,
        int assertsLeft,
        int assertBudget)
    {
        if (!mirroring)
        {
            return (ControlReturnAction.Nothing, 0);
        }

        if (cameraOwnedElsewhere)
        {
            return (ControlReturnAction.Nothing, assertsLeft);
        }

        if (controlledIsSessionOwned)
        {
            return (ControlReturnAction.NoteClaim, assertBudget);
        }

        return assertsLeft > 0
            ? (ControlReturnAction.AssertCamera, assertsLeft - 1)
            : (ControlReturnAction.Nothing, 0);
    }
}
