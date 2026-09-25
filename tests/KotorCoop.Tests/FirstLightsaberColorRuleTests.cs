using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// docs/research/class-and-power-grants.md §10: Zhar's saber-construction reward always
/// mirrors the BUILDER's own colour to every other player; this rule is what the receiving
/// side substitutes in the RECEIVER's own class colour instead, guards against a second
/// first-saber grant, and otherwise leaves every other item untouched.
/// </summary>
public class FirstLightsaberColorRuleTests
{
    private const string Blue = "g_w_lghtsbr01";   // Guardian (3)
    private const string Green = "g_w_lghtsbr03";  // Consular (4)
    private const string Yellow = "g_w_lghtsbr04"; // Sentinel (5)

    private const int Guardian = 3;
    private const int Consular = 4;
    private const int Sentinel = 5;

    [Fact]
    public void AnUnrelatedResrefIsAlwaysPassedThroughRegardlessOfClassOrPossession()
    {
        FirstLightsaberColorRule.Result result = FirstLightsaberColorRule.Evaluate(
            "g_w_blstrpstl001", receiverClassIds: [Guardian], receiverAlreadyHasFirstSaber: true);

        Assert.Equal(FirstLightsaberColorRule.Decision.PassThrough, result.Decision);
        Assert.Equal("g_w_blstrpstl001", result.Resref);
    }

    [Theory]
    [InlineData(Guardian, Blue)]
    [InlineData(Sentinel, Yellow)]
    [InlineData(Consular, Green)]
    public void TheReceiversOwnClassProducesTheRetailColourForThatClass(int classId, string expectedResref)
    {
        // Builder happened to be a different class (Sentinel giving everyone yellow); the
        // receiver's own class should win.
        string builderResref = classId == Sentinel ? Blue : Yellow;

        FirstLightsaberColorRule.Result result = FirstLightsaberColorRule.Evaluate(
            builderResref, receiverClassIds: [classId], receiverAlreadyHasFirstSaber: false);

        Assert.Equal(FirstLightsaberColorRule.Decision.Substitute, result.Decision);
        Assert.Equal(expectedResref, result.Resref);
    }

    [Fact]
    public void WhenTheReceiversOwnClassAlreadyMatchesTheIncomingColourNothingChanges()
    {
        FirstLightsaberColorRule.Result result = FirstLightsaberColorRule.Evaluate(
            Yellow, receiverClassIds: [Sentinel], receiverAlreadyHasFirstSaber: false);

        Assert.Equal(FirstLightsaberColorRule.Decision.PassThrough, result.Decision);
        Assert.Equal(Yellow, result.Resref);
    }

    [Fact]
    public void AReceiverWhoAlreadyHasAFirstSaberGetsNothingRegardlessOfClass()
    {
        FirstLightsaberColorRule.Result result = FirstLightsaberColorRule.Evaluate(
            Blue, receiverClassIds: [Sentinel], receiverAlreadyHasFirstSaber: true);

        Assert.Equal(FirstLightsaberColorRule.Decision.Skip, result.Decision);
    }

    [Fact]
    public void TheAlreadyHasGuardCheckIsEvaluatedEveryCallSoARepeatedGrantStaysIdempotent()
    {
        // Simulates a resync/rejoin/late-join redelivering the SAME grant twice - the caller
        // is expected to re-check "already has one" fresh each time, never latch a one-shot
        // flag, which this rule's own signature enforces (no state carried between calls).
        FirstLightsaberColorRule.Result first = FirstLightsaberColorRule.Evaluate(
            Yellow, receiverClassIds: [Sentinel], receiverAlreadyHasFirstSaber: false);
        Assert.Equal(FirstLightsaberColorRule.Decision.PassThrough, first.Decision);

        // After the first grant lands, the receiver now genuinely owns a first saber.
        FirstLightsaberColorRule.Result replay = FirstLightsaberColorRule.Evaluate(
            Yellow, receiverClassIds: [Sentinel], receiverAlreadyHasFirstSaber: true);
        Assert.Equal(FirstLightsaberColorRule.Decision.Skip, replay.Decision);
    }

    [Fact]
    public void ABaseClassAloneWithNoJediSlotYetFallsBackToTheBuildersOwnColour()
    {
        // Soldier (0) is not one of the three Jedi classes - nothing to substitute to.
        FirstLightsaberColorRule.Result result = FirstLightsaberColorRule.Evaluate(
            Blue, receiverClassIds: [0], receiverAlreadyHasFirstSaber: false);

        Assert.Equal(FirstLightsaberColorRule.Decision.PassThrough, result.Decision);
        Assert.Equal(Blue, result.Resref);
    }

    [Fact]
    public void NoClassIdsAtAllFallsBackToTheBuildersOwnColour()
    {
        FirstLightsaberColorRule.Result result = FirstLightsaberColorRule.Evaluate(
            Green, receiverClassIds: [], receiverAlreadyHasFirstSaber: false);

        Assert.Equal(FirstLightsaberColorRule.Decision.PassThrough, result.Decision);
        Assert.Equal(Green, result.Resref);
    }

    [Fact]
    public void AMulticlassedReceiverIsMatchedByWhicheverSlotHoldsTheJediClass()
    {
        // classes.2da slot order is base-class-first (AddMultiClass always appends), but the
        // rule must not assume that - it should find the Jedi class wherever it sits.
        FirstLightsaberColorRule.Result resultSlot0 = FirstLightsaberColorRule.Evaluate(
            Blue, receiverClassIds: [Consular, 1 /* Scout base, slot after */], receiverAlreadyHasFirstSaber: false);
        Assert.Equal(FirstLightsaberColorRule.Decision.Substitute, resultSlot0.Decision);
        Assert.Equal(Green, resultSlot0.Resref);

        FirstLightsaberColorRule.Result resultSlot1 = FirstLightsaberColorRule.Evaluate(
            Blue, receiverClassIds: [1 /* Scout base */, Consular], receiverAlreadyHasFirstSaber: false);
        Assert.Equal(FirstLightsaberColorRule.Decision.Substitute, resultSlot1.Decision);
        Assert.Equal(Green, resultSlot1.Resref);
    }

    [Theory]
    [InlineData(Blue)]
    [InlineData(Green)]
    [InlineData(Yellow)]
    public void IsCanonicalFirstSaberRecognisesExactlyTheThreeRetailBlueprints(string resref)
    {
        Assert.True(FirstLightsaberColorRule.IsCanonicalFirstSaber(resref));
    }

    [Fact]
    public void IsCanonicalFirstSaberIsCaseInsensitiveAndRejectsEverythingElse()
    {
        Assert.True(FirstLightsaberColorRule.IsCanonicalFirstSaber("G_W_LGHTSBR01"));
        Assert.False(FirstLightsaberColorRule.IsCanonicalFirstSaber("g_w_lghtsbr02"));
        Assert.False(FirstLightsaberColorRule.IsCanonicalFirstSaber(string.Empty));
    }
}
