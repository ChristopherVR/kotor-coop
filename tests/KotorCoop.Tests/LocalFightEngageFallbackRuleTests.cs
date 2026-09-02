using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// docs/research/combat-participation.md §12: the direct-drive fallback for an idle GIT hostile
/// whose own +0x260 script fire found nothing to act on (the live tar_m10aa Vulkars — hostile in
/// the census, a real k_def_combend01 script, no combat round already running, and the script
/// fire alone left them frozen). One-shot per fight, gated on the creature's own action-queue
/// head staying empty for a grace window since the fight was armed.
/// </summary>
public class LocalFightEngageFallbackRuleTests
{
    private const bool Enabled = true;
    private const double Grace = 0.5;
    private const int EmptyHead = -1;
    private const int OccupiedHead = 0x3F; // ActionAttack's own action id, per CompanionLocalFightRuleTests.

    [Fact]
    public void EmptyHeadPastGraceEngagesDirectly()
    {
        Assert.Equal(
            LocalFightEngageFallbackAction.EngageDirectly,
            LocalFightEngageFallbackRule.Decide(
                Enabled, alreadyIssued: false, EmptyHead, armedAtSeconds: 10.0, nowSeconds: 10.5, Grace));
    }

    [Fact]
    public void EmptyHeadExactlyAtGraceEngagesDirectly()
    {
        // >= grace, not strictly greater — a slot polled at exactly the boundary should not have
        // to wait an extra frame.
        Assert.Equal(
            LocalFightEngageFallbackAction.EngageDirectly,
            LocalFightEngageFallbackRule.Decide(
                Enabled, alreadyIssued: false, EmptyHead, armedAtSeconds: 0.0, nowSeconds: Grace, Grace));
    }

    [Fact]
    public void EmptyHeadBeforeGraceWaits()
    {
        Assert.Equal(
            LocalFightEngageFallbackAction.None,
            LocalFightEngageFallbackRule.Decide(
                Enabled, alreadyIssued: false, EmptyHead, armedAtSeconds: 10.0, nowSeconds: 10.2, Grace));
    }

    [Fact]
    public void OccupiedHeadNeverEngagesEvenLongAfterGrace()
    {
        // The script fire worked (or the creature's own AI queued something independently) —
        // driving on top of a real target would only contest it.
        Assert.Equal(
            LocalFightEngageFallbackAction.None,
            LocalFightEngageFallbackRule.Decide(
                Enabled, alreadyIssued: false, OccupiedHead, armedAtSeconds: 0.0, nowSeconds: 100.0, Grace));
    }

    [Fact]
    public void AlreadyIssuedNeverFiresAgainEvenWhileHeadStaysEmpty()
    {
        // The one-shot guard: a creature that still shows no target after the direct drive is
        // not going to gain one from an identical second call.
        Assert.Equal(
            LocalFightEngageFallbackAction.None,
            LocalFightEngageFallbackRule.Decide(
                Enabled, alreadyIssued: true, EmptyHead, armedAtSeconds: 0.0, nowSeconds: 100.0, Grace));
    }

    [Fact]
    public void DisabledNeverEngagesEvenWhenEveryOtherGateIsOpen()
    {
        Assert.Equal(
            LocalFightEngageFallbackAction.None,
            LocalFightEngageFallbackRule.Decide(
                enabled: false, alreadyIssued: false, EmptyHead, armedAtSeconds: 0.0, nowSeconds: 100.0, Grace));
    }

    [Fact]
    public void TheScriptWorkingBeforeGraceExpiresNeverTriggersTheFallback()
    {
        // A realistic end_gensith-style pass: armed at t=0, the script fire lands a real target
        // within one tick (t=0.05), well inside the grace window — the fallback must never fire
        // for this creature at all, not even once the grace window has long since passed, since
        // headActionType keeps reading occupied from here on.
        LocalFightEngageFallbackAction atArm = LocalFightEngageFallbackRule.Decide(
            Enabled, alreadyIssued: false, headActionType: EmptyHead, armedAtSeconds: 0.0, nowSeconds: 0.0, Grace);
        Assert.Equal(LocalFightEngageFallbackAction.None, atArm);

        LocalFightEngageFallbackAction afterScriptLands = LocalFightEngageFallbackRule.Decide(
            Enabled, alreadyIssued: false, headActionType: OccupiedHead, armedAtSeconds: 0.0, nowSeconds: 5.0, Grace);
        Assert.Equal(LocalFightEngageFallbackAction.None, afterScriptLands);
    }

    [Fact]
    public void ARealisticIdleFightFiresExactlyOnce()
    {
        // The tar_m10aa shape: armed at t=0, head stays empty every pass (the script fire found
        // nothing), the fallback fires the first pass at/after the grace window, and every pass
        // after that stays silent because the caller latches alreadyIssued once it fires.
        bool issued = false;
        int engagements = 0;

        double[] passTimes = [0.0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 1.0, 2.0];
        foreach (double now in passTimes)
        {
            LocalFightEngageFallbackAction action = LocalFightEngageFallbackRule.Decide(
                Enabled, alreadyIssued: issued, EmptyHead, armedAtSeconds: 0.0, now, Grace);

            if (action == LocalFightEngageFallbackAction.EngageDirectly)
            {
                engagements++;
                issued = true;
            }
        }

        Assert.Equal(1, engagements);
    }
}
