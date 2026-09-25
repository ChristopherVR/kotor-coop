using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>docs/research/personal-inventory.md §4's pure detect rule.</summary>
public class CreditGrantRuleTests
{
    [Fact]
    public void SharedCreditsModeNeverPublishesAndClearsTheBaseline()
    {
        CreditGrantObservation result = CreditGrantRule.Observe(
            baseline: 1000, current: 1500, owningWindow: true, creditsPersonal: false);

        Assert.Null(result.NewBaseline);
        Assert.Equal(0, result.AmountToPublish);
    }

    [Fact]
    public void NotOwningAConversationNeverPublishesAndClearsTheBaseline()
    {
        CreditGrantObservation result = CreditGrantRule.Observe(
            baseline: 1000, current: 1500, owningWindow: false, creditsPersonal: true);

        Assert.Null(result.NewBaseline);
        Assert.Equal(0, result.AmountToPublish);
    }

    [Fact]
    public void FirstSampleSinceTheWindowOpenedOnlyBaselinesNothingPublished()
    {
        CreditGrantObservation result = CreditGrantRule.Observe(
            baseline: null, current: 1000, owningWindow: true, creditsPersonal: true);

        Assert.Equal(1000, result.NewBaseline);
        Assert.Equal(0, result.AmountToPublish);
    }

    [Fact]
    public void ARiseWhileOwningIsPublishedInFull()
    {
        CreditGrantObservation result = CreditGrantRule.Observe(
            baseline: 1000, current: 1350, owningWindow: true, creditsPersonal: true);

        Assert.Equal(1350, result.NewBaseline);
        Assert.Equal(350, result.AmountToPublish);
    }

    [Fact]
    public void AFallIsNeverPublishedButStillRebaselines()
    {
        CreditGrantObservation result = CreditGrantRule.Observe(
            baseline: 1000, current: 900, owningWindow: true, creditsPersonal: true);

        Assert.Equal(900, result.NewBaseline);
        Assert.Equal(0, result.AmountToPublish);
    }

    [Fact]
    public void UnchangedGoldPublishesNothing()
    {
        CreditGrantObservation result = CreditGrantRule.Observe(
            baseline: 1000, current: 1000, owningWindow: true, creditsPersonal: true);

        Assert.Equal(1000, result.NewBaseline);
        Assert.Equal(0, result.AmountToPublish);
    }

    [Fact]
    public void SuccessiveRisesEachPublishOnlyTheirOwnDelta()
    {
        CreditGrantObservation first = CreditGrantRule.Observe(
            baseline: null, current: 500, owningWindow: true, creditsPersonal: true);
        Assert.Equal(0, first.AmountToPublish);

        CreditGrantObservation second = CreditGrantRule.Observe(
            baseline: first.NewBaseline, current: 750, owningWindow: true, creditsPersonal: true);
        Assert.Equal(250, second.AmountToPublish);

        CreditGrantObservation third = CreditGrantRule.Observe(
            baseline: second.NewBaseline, current: 900, owningWindow: true, creditsPersonal: true);
        Assert.Equal(150, third.AmountToPublish);
    }
}
