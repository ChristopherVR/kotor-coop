using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// docs/research/combat-participation.md §13: `local_combat_timeout_s` measures INACTIVITY, not
/// fight length. Measured live (2026-09-02, tar_m10aa), a genuinely ongoing fight — the host was
/// applying the client's forwarded hits on the excluded creature the whole time — got released
/// every 25s regardless, purely because that much time had passed since the fight was ARMED, then
/// re-armed and re-engaged the same creature roughly 0.8s later. The fix: refresh a per-slot
/// activity clock whenever there is evidence (a rise in damage dealt, or the creature currently
/// targeting the local player), and only release once neither has been seen for the timeout
/// window.
/// </summary>
public class LocalFightActivityRuleTests
{
    private const double Timeout = 25.0;

    [Fact]
    public void DamageRoseRefreshesEvenLongPastTheWindow()
    {
        // Evidence always wins over the clock — a hit landing right at the edge of what would
        // otherwise be a timeout must not lose the race to the release check.
        Assert.Equal(
            LocalFightActivityAction.Refresh,
            LocalFightActivityRule.Decide(
                damageDealtRose: true, targetingUs: false,
                lastActivityAtSeconds: 0.0, nowSeconds: 1000.0, Timeout));
    }

    [Fact]
    public void NoRiseAndNotTargetingWithinTheWindowContinues()
    {
        Assert.Equal(
            LocalFightActivityAction.Continue,
            LocalFightActivityRule.Decide(
                damageDealtRose: false, targetingUs: false,
                lastActivityAtSeconds: 10.0, nowSeconds: 20.0, Timeout));
    }

    [Fact]
    public void TargetingUsAloneRefreshesWithNoDamageAtAll()
    {
        Assert.Equal(
            LocalFightActivityAction.Refresh,
            LocalFightActivityRule.Decide(
                damageDealtRose: false, targetingUs: true,
                lastActivityAtSeconds: 0.0, nowSeconds: 1000.0, Timeout));
    }

    [Fact]
    public void BothPiecesOfEvidenceAtOnceStillJustRefreshes()
    {
        Assert.Equal(
            LocalFightActivityAction.Refresh,
            LocalFightActivityRule.Decide(
                damageDealtRose: true, targetingUs: true,
                lastActivityAtSeconds: 0.0, nowSeconds: 5.0, Timeout));
    }

    [Fact]
    public void ExactlyAtTheBoundaryContinues()
    {
        // > timeout, not >= — a slot polled at exactly the boundary keeps its fight for one
        // more pass, matching the release check this replaced (now - since > timeout).
        Assert.Equal(
            LocalFightActivityAction.Continue,
            LocalFightActivityRule.Decide(
                damageDealtRose: false, targetingUs: false,
                lastActivityAtSeconds: 0.0, nowSeconds: Timeout, Timeout));
    }

    [Fact]
    public void JustPastTheBoundaryTimesOut()
    {
        Assert.Equal(
            LocalFightActivityAction.TimedOut,
            LocalFightActivityRule.Decide(
                damageDealtRose: false, targetingUs: false,
                lastActivityAtSeconds: 0.0, nowSeconds: Timeout + 0.001, Timeout));
    }

    [Fact]
    public void ARealisticOngoingFightNeverTimesOutWhileHitsKeepLanding()
    {
        // The tar_m10aa shape this fix is built from: armed at t=0, and every ~4s a fresh hit
        // lands (well inside the 25s window), for a fight that runs far longer than the old
        // "since armed" ceiling ever allowed. The activity clock should refresh at every hit and
        // never reach TimedOut across the whole run.
        double lastActivityAt = 0.0;
        double[] hitTimes = [3.0, 8.0, 15.0, 22.0, 30.0, 41.0, 55.0];

        foreach (double hitAt in hitTimes)
        {
            LocalFightActivityAction action = LocalFightActivityRule.Decide(
                damageDealtRose: true, targetingUs: false, lastActivityAt, hitAt, Timeout);

            Assert.Equal(LocalFightActivityAction.Refresh, action);
            lastActivityAt = hitAt;
        }
    }

    [Fact]
    public void ARealisticIdleSlotTimesOutOnceEvidenceStops()
    {
        // Symmetric case: the fight starts, evidence stops arriving, and once the window has
        // genuinely elapsed with nothing to refresh it, the rule reports TimedOut.
        double lastActivityAt = 0.0;

        Assert.Equal(
            LocalFightActivityAction.Continue,
            LocalFightActivityRule.Decide(
                damageDealtRose: false, targetingUs: false, lastActivityAt, nowSeconds: 24.9, Timeout));

        Assert.Equal(
            LocalFightActivityAction.TimedOut,
            LocalFightActivityRule.Decide(
                damageDealtRose: false, targetingUs: false, lastActivityAt, nowSeconds: 25.1, Timeout));
    }
}
