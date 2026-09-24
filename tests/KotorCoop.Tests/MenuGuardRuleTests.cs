using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="MenuGuardRule"/> — the bounded protection window behind
/// <see cref="KotorCoop.Protocol.PlayerState.FlagMenuGuard"/> (docs/limitations.md's "Pausing"
/// row parity fix, docs/research/effect-sync.md §13). Pure timer logic: the stamp bookkeeping,
/// the strict cap, the cooldown that only a capped-out opening earns, and that protection ends
/// the instant the menu closes regardless of the cap.
/// </summary>
public sealed class MenuGuardRuleTests
{
    private const int CapMs = 12_000;
    private const int CooldownMs = 20_000;

    [Fact]
    public void TrackOpenSinceStampsTheFirstOpenTickHoldsItAndClearsOnClose()
    {
        Assert.Equal(0, MenuGuardRule.TrackOpenSince(openSinceMs: 0, menuOpenNow: false, nowMs: 1000));
        Assert.Equal(1000, MenuGuardRule.TrackOpenSince(openSinceMs: 0, menuOpenNow: true, nowMs: 1000));
        Assert.Equal(1000, MenuGuardRule.TrackOpenSince(openSinceMs: 1000, menuOpenNow: true, nowMs: 5000));
        Assert.Equal(0, MenuGuardRule.TrackOpenSince(openSinceMs: 1000, menuOpenNow: false, nowMs: 5000));
    }

    [Fact]
    public void IsProtectedStartsTheInstantTheMenuOpensNoDelay()
    {
        long openSince = MenuGuardRule.TrackOpenSince(0, menuOpenNow: true, nowMs: 1000);
        Assert.True(MenuGuardRule.IsProtected(openSince, cooldownUntilMs: 0, CapMs, nowMs: 1000));
    }

    [Fact]
    public void IsProtectedEndsTheInstantTheMenuClosesEvenWellUnderTheCap()
    {
        long openSince = MenuGuardRule.TrackOpenSince(0, menuOpenNow: true, nowMs: 1000);
        Assert.True(MenuGuardRule.IsProtected(openSince, 0, CapMs, nowMs: 3000));

        // The menu closes: TrackOpenSince resets to 0, and protection must end on the same tick.
        openSince = MenuGuardRule.TrackOpenSince(openSince, menuOpenNow: false, nowMs: 3000);
        Assert.False(MenuGuardRule.IsProtected(openSince, 0, CapMs, nowMs: 3000));
    }

    [Fact]
    public void CapExhaustedIsFalseBeforeTheCapTrueAtOrPastItAndFalseOnceClosed()
    {
        Assert.False(MenuGuardRule.CapExhausted(openSinceMs: 1000, CapMs, nowMs: 1000 + CapMs - 1));
        Assert.True(MenuGuardRule.CapExhausted(openSinceMs: 1000, CapMs, nowMs: 1000 + CapMs));
        Assert.True(MenuGuardRule.CapExhausted(openSinceMs: 1000, CapMs, nowMs: 1000 + CapMs + 5000));

        // openSinceMs == 0 means "not open" - never exhausted, whatever nowMs is.
        Assert.False(MenuGuardRule.CapExhausted(openSinceMs: 0, CapMs, nowMs: 999_999));
    }

    [Fact]
    public void IsProtectedLapsesExactlyAtTheCapWhileTheMenuIsStillOpen()
    {
        long openSince = 1000;
        Assert.True(MenuGuardRule.IsProtected(openSince, cooldownUntilMs: 0, CapMs, nowMs: openSince + CapMs - 1));
        Assert.False(MenuGuardRule.IsProtected(openSince, cooldownUntilMs: 0, CapMs, nowMs: openSince + CapMs));
    }

    [Fact]
    public void TrackCooldownOnlyEngagesWhileCapExhaustedAndHoldsItsLastValueOtherwise()
    {
        // A short opening that never exhausts the cap never touches the cooldown.
        Assert.Equal(0, MenuGuardRule.TrackCooldown(cooldownUntilMs: 0, capExhaustedNow: false, CooldownMs, nowMs: 5000));

        // The cap is reached: the cooldown stamp is set to now + cooldownMs.
        long cooldownUntil = MenuGuardRule.TrackCooldown(0, capExhaustedNow: true, CooldownMs, nowMs: 13_000);
        Assert.Equal(13_000 + CooldownMs, cooldownUntil);

        // Refreshed continuously while still capExhausted (the opening stays open past the cap).
        cooldownUntil = MenuGuardRule.TrackCooldown(cooldownUntil, capExhaustedNow: true, CooldownMs, nowMs: 20_000);
        Assert.Equal(20_000 + CooldownMs, cooldownUntil);

        // Held at its last value once capExhausted goes false (the opening finally closes).
        cooldownUntil = MenuGuardRule.TrackCooldown(cooldownUntil, capExhaustedNow: false, CooldownMs, nowMs: 999_999);
        Assert.Equal(20_000 + CooldownMs, cooldownUntil);
    }

    /// <summary>Every simulated opening starts at a nonzero tick, the same discipline
    /// <c>HostAwayRuleTests</c> follows: 0 is the sentinel for "not open"/"no cooldown
    /// pending", so an opening that genuinely began at real time 0 would be indistinguishable
    /// from one that never began at all. A live poll loop starts well after process boot, so
    /// this is not a real-world case - only a test-hygiene one.</summary>
    private const long Epoch = 1000;

    [Fact]
    public void ACappedOutOpeningBlocksAnImmediateReopenUntilTheCooldownElapses()
    {
        // Opening #1: opens at t=Epoch, stays open, hits the cap at t=Epoch+CapMs.
        long openSince = MenuGuardRule.TrackOpenSince(0, true, nowMs: Epoch);
        long cooldownUntil = 0;

        for (long t = Epoch; t <= Epoch + CapMs; t += 1000)
        {
            openSince = MenuGuardRule.TrackOpenSince(openSince, true, t);
            bool capExhausted = MenuGuardRule.CapExhausted(openSince, CapMs, t);
            cooldownUntil = MenuGuardRule.TrackCooldown(cooldownUntil, capExhausted, CooldownMs, t);
        }

        long capTick = Epoch + CapMs;
        Assert.False(MenuGuardRule.IsProtected(openSince, cooldownUntil, CapMs, nowMs: capTick),
            "protection should already have lapsed at the cap");

        // Closed, then IMMEDIATELY reopened at the same tick.
        openSince = MenuGuardRule.TrackOpenSince(openSince, menuOpenNow: false, nowMs: capTick);
        openSince = MenuGuardRule.TrackOpenSince(openSince, menuOpenNow: true, nowMs: capTick);

        // A fresh cap window on its own would grant protection again immediately - it must not,
        // because the cooldown from the capped-out opening is still counting down.
        Assert.False(MenuGuardRule.IsProtected(openSince, cooldownUntil, CapMs, nowMs: capTick),
            "an immediate reopen must not renew protection while the cooldown is still running");

        // Still blocked a moment before the cooldown ends, with the same reopened window still
        // continuously open (nowhere near the cap when measured from ITS OWN start at capTick).
        Assert.False(MenuGuardRule.IsProtected(openSince, cooldownUntil, CapMs, nowMs: cooldownUntil - 1));

        // A FRESH opening - close, then reopen right as the cooldown elapses - is protected
        // again. (Using the same continuously-open window here would itself have run well past
        // its own cap by this point, 20s after it opened against a 12s cap - a different,
        // already-covered case, not what this assertion is checking.)
        openSince = MenuGuardRule.TrackOpenSince(openSince, menuOpenNow: false, nowMs: cooldownUntil - 1);
        openSince = MenuGuardRule.TrackOpenSince(openSince, menuOpenNow: true, nowMs: cooldownUntil);
        Assert.True(MenuGuardRule.IsProtected(openSince, cooldownUntil, CapMs, nowMs: cooldownUntil));
    }

    [Fact]
    public void AShortOpeningWellUnderTheCapNeverTriggersACooldown()
    {
        long openSince = MenuGuardRule.TrackOpenSince(0, true, nowMs: Epoch);
        long cooldownUntil = 0;

        // Open for 2s, well under the 12s cap, then close.
        for (long t = Epoch; t <= Epoch + 2000; t += 500)
        {
            openSince = MenuGuardRule.TrackOpenSince(openSince, true, t);
            bool capExhausted = MenuGuardRule.CapExhausted(openSince, CapMs, t);
            cooldownUntil = MenuGuardRule.TrackCooldown(cooldownUntil, capExhausted, CooldownMs, t);
        }

        long closeTick = Epoch + 2000;
        openSince = MenuGuardRule.TrackOpenSince(openSince, menuOpenNow: false, nowMs: closeTick);
        Assert.Equal(0, cooldownUntil);

        // Reopening right away grants a fresh full cap - no cooldown was ever earned.
        openSince = MenuGuardRule.TrackOpenSince(openSince, menuOpenNow: true, nowMs: closeTick);
        Assert.True(MenuGuardRule.IsProtected(openSince, cooldownUntil, CapMs, nowMs: closeTick));
    }

    [Fact]
    public void ACapOfZeroOrLessNeverExhaustsAndNeverLapsesProtectionOnItsOwn()
    {
        Assert.False(MenuGuardRule.CapExhausted(openSinceMs: 1000, capMs: 0, nowMs: 999_999_999));
        Assert.True(MenuGuardRule.IsProtected(openSinceMs: 1000, cooldownUntilMs: 0, capMs: 0, nowMs: 999_999_999));
    }
}
