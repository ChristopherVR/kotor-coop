using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="WipeRecoveryRule"/> — the one rejoin trigger a session wipe needs. The live shape
/// this pins (2026-09-02, docs/research/death-handling.md §6.4): both parties wiped and readable
/// for ~13 s, then the local game tears down (reads fail), the host parks at its game-over menu
/// publishing its last (wiped, away) state, and eventually the host is reported standing in a
/// world again. That last report, and only that, is the trigger.
/// </summary>
public sealed class WipeRecoveryRuleTests
{
    private static PlayerState Standing(string module = "end_m01aa") =>
        new(Vector3f.Zero, 0f, module, 12, 0, MaxHealth: 12);

    private static PlayerState Wiped(string module = "end_m01aa", bool away = false) =>
        new(Vector3f.Zero, 0f, module, 0, 0, MaxHealth: 12,
            Flags: PlayerState.FlagDowned | PlayerState.FlagPartyWiped | (away ? PlayerState.FlagAway : 0u));

    private static PlayerState DownedNotWiped() =>
        new(Vector3f.Zero, 0f, "end_m01aa", 0, 0, MaxHealth: 12, Flags: PlayerState.FlagDowned);

    [Fact]
    public void NothingHappensWhileEverybodyStands()
    {
        (bool trigger, WipeRecoveryState next) =
            WipeRecoveryRule.Observe(WipeRecoveryState.Initial, Standing(), Standing());

        Assert.False(trigger);
        Assert.False(next.Wiped);
    }

    [Fact]
    public void BothPartiesWipedArmsTheLatchWithoutTriggering()
    {
        (bool trigger, WipeRecoveryState next) =
            WipeRecoveryRule.Observe(WipeRecoveryState.Initial, Wiped(), Wiped());

        Assert.False(trigger);
        Assert.True(next.Wiped);
    }

    [Fact]
    public void OnePartyWipedIsNotASessionWipe()
    {
        // The host's PC is down but a companion still fights: DeathPump holds the game-over,
        // and this rule must not arm either — the host may win and come pick us up.
        (bool trigger, WipeRecoveryState next) =
            WipeRecoveryRule.Observe(WipeRecoveryState.Initial, Wiped(), DownedNotWiped());

        Assert.False(trigger);
        Assert.False(next.Wiped);

        (trigger, next) = WipeRecoveryRule.Observe(WipeRecoveryState.Initial, DownedNotWiped(), Wiped());

        Assert.False(trigger);
        Assert.False(next.Wiped);
    }

    [Fact]
    public void TheLatchSurvivesTheLocalTeardownAndAParkedHost()
    {
        WipeRecoveryState armed = new(true);

        // Our own world is gone (reads fail) and the host is parked at its game-over menu,
        // still publishing wiped + away. Nothing to do yet.
        (bool trigger, WipeRecoveryState next) =
            WipeRecoveryRule.Observe(armed, local: null, host: Wiped(away: true));

        Assert.False(trigger);
        Assert.True(next.Wiped);

        // The host's peer entry momentarily missing changes nothing either.
        (trigger, next) = WipeRecoveryRule.Observe(next, local: null, host: null);

        Assert.False(trigger);
        Assert.True(next.Wiped);
    }

    [Fact]
    public void TheHostStandingInAWorldAgainTriggersOnceAndDisarms()
    {
        WipeRecoveryState armed = new(true);

        (bool trigger, WipeRecoveryState next) =
            WipeRecoveryRule.Observe(armed, local: null, host: Standing());

        Assert.True(trigger);
        Assert.False(next.Wiped);

        // The tick after: the host is still standing, but the latch is spent.
        (trigger, next) = WipeRecoveryRule.Observe(next, local: null, host: Standing());

        Assert.False(trigger);
        Assert.False(next.Wiped);
    }

    [Fact]
    public void AHostBackWithoutAModuleIsNotBackYet()
    {
        // A player with no module has not loaded a world (main menu, or an unresolved player).
        WipeRecoveryState armed = new(true);
        PlayerState noModule = new(Vector3f.Zero, 0f, string.Empty, 12, 0, MaxHealth: 12);

        (bool trigger, WipeRecoveryState next) = WipeRecoveryRule.Observe(armed, null, noModule);

        Assert.False(trigger);
        Assert.True(next.Wiped);
    }

    [Fact]
    public void AStaleDownedHostIsNotBackYet()
    {
        // The exact stale-flag shape live 2026-09-02 before DeathController.Invalidate lowered
        // LocalDowned on teardown: back in a world, hp 12/12, still flagged downed. Waiting on
        // the flag rather than the module is what makes this rule safe against it.
        WipeRecoveryState armed = new(true);
        PlayerState staleDowned = new(Vector3f.Zero, 0f, "end_m01aa", 12, 0, MaxHealth: 12, Flags: PlayerState.FlagDowned);

        (bool trigger, WipeRecoveryState next) = WipeRecoveryRule.Observe(armed, null, staleDowned);

        Assert.False(trigger);
        Assert.True(next.Wiped);
    }

    [Fact]
    public void ALocalPlayerWhoReloadedFirstStillFollowsTheHost()
    {
        // The client loaded their own save from the game-over menu before the host did. The
        // latch stays armed; the host's return still pulls the client into the host's world.
        WipeRecoveryState armed = new(true);

        (bool trigger, WipeRecoveryState next) =
            WipeRecoveryRule.Observe(armed, local: Standing(), host: Wiped(away: true));

        Assert.False(trigger);
        Assert.True(next.Wiped);

        (trigger, _) = WipeRecoveryRule.Observe(next, local: Standing(), host: Standing());

        Assert.True(trigger);
    }
}
