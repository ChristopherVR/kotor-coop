using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The rule that decides whether a co-op client is being left driving — or watching — a
/// companion the session owns.
///
/// The scenario behind every case here is a real one: a player who is playing AS Trask when
/// they connect, or when an unattended auto-reconnect fires. The agent reverts CONTROL by id;
/// nothing reverts the CAMERA, because the engine's camera handoff lives inside the
/// ChangeCharacter the same session has patched to return immediately. So the fail-safe
/// direction is fixed and is what these assert: the player ends up on their own character.
/// </summary>
public class ControlReturnRuleTests
{
    private const int Budget = 30;

    private static (ControlReturnAction Action, int Left) Decide(
        bool mirroring, bool cameraBusy, bool sessionOwned, int assertsLeft = 0) =>
        ControlReturnRule.Decide(mirroring, cameraBusy, sessionOwned, assertsLeft, Budget);

    [Fact]
    public void AHostIsLeftAloneEntirely()
    {
        // The host owns its companions and is free to play as one; nothing here applies.
        Assert.Equal(
            (ControlReturnAction.Nothing, 0),
            Decide(mirroring: false, cameraBusy: false, sessionOwned: true, assertsLeft: Budget));
    }

    [Fact]
    public void DrivingASessionOwnedCompanionArmsTheHandBackWithoutMovingTheCamera()
    {
        (ControlReturnAction action, int left) =
            Decide(mirroring: true, cameraBusy: false, sessionOwned: true);

        // Deliberately NOT AssertCamera: control has not come back yet, so "the controlled
        // creature" is still the companion and asserting would re-point the camera at it.
        Assert.Equal(ControlReturnAction.NoteClaim, action);
        Assert.Equal(Budget, left);
    }

    [Fact]
    public void OnceControlIsBackTheCameraFollowsAndSpendsOneAssert()
    {
        (ControlReturnAction action, int left) =
            Decide(mirroring: true, cameraBusy: false, sessionOwned: false, assertsLeft: Budget);

        Assert.Equal(ControlReturnAction.AssertCamera, action);
        Assert.Equal(Budget - 1, left);
    }

    [Fact]
    public void TheAssertBudgetIsBoundedSoThisNeverOwnsTheCamera()
    {
        int left = Budget;
        int asserts = 0;

        for (int pass = 0; pass < Budget * 2; pass++)
        {
            (ControlReturnAction action, left) =
                Decide(mirroring: true, cameraBusy: false, sessionOwned: false, left);

            if (action == ControlReturnAction.AssertCamera)
            {
                asserts++;
            }
        }

        Assert.Equal(Budget, asserts);
        Assert.Equal(0, left);
    }

    [Fact]
    public void DialogueAndSpectateKeepTheirCameraAndTheHandBackWaitsForThem()
    {
        // A conversation opening (or the player going down) must not consume the budget: the
        // hand-back is still owed and lands once the other owner lets go.
        (ControlReturnAction action, int left) =
            Decide(mirroring: true, cameraBusy: true, sessionOwned: false, assertsLeft: Budget);

        Assert.Equal(ControlReturnAction.Nothing, action);
        Assert.Equal(Budget, left);

        (action, left) = Decide(mirroring: true, cameraBusy: false, sessionOwned: false, left);
        Assert.Equal(ControlReturnAction.AssertCamera, action);
        Assert.Equal(Budget - 1, left);
    }

    [Fact]
    public void ADisconnectDropsTheBudgetSoTheCameraIsThePlayersAgain()
    {
        // Ceasing to mirror is the release: the player owns their own game, including the
        // right to put the camera back on a companion if they want it there.
        Assert.Equal(
            (ControlReturnAction.Nothing, 0),
            Decide(mirroring: false, cameraBusy: false, sessionOwned: false, assertsLeft: Budget));
    }

    [Fact]
    public void NothingHappensOnAnOrdinaryPassWithNothingOwed()
    {
        Assert.Equal(
            (ControlReturnAction.Nothing, 0),
            Decide(mirroring: true, cameraBusy: false, sessionOwned: false));
    }
}
