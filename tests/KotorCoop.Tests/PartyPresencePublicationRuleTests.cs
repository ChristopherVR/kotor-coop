using Xunit;
using KotorCoop.Memory;

namespace KotorCoop.Tests;

public sealed class PartyPresencePublicationRuleTests
{
    [Fact]
    public void FirstWriteUsesOddThenEvenGeneration()
    {
        uint odd = PartyPresencePublicationRule.BeginWrite(0);

        Assert.Equal(1u, odd);
        Assert.Equal(2u, PartyPresencePublicationRule.CompleteWrite(odd));
    }

    [Fact]
    public void ReaderRejectsBusyOrChangedGeneration()
    {
        Assert.False(PartyPresencePublicationRule.IsStableRead(3, 3));
        Assert.False(PartyPresencePublicationRule.IsStableRead(2, 4));
        Assert.True(PartyPresencePublicationRule.IsStableRead(2, 2));
    }

    [Fact]
    public void GenerationWrapKeepsBusyMarkerAndStableCompletion()
    {
        uint odd = PartyPresencePublicationRule.BeginWrite(0xFFFFFFFEu);

        Assert.Equal(0xFFFFFFFFu, odd);
        Assert.Equal(0u, PartyPresencePublicationRule.CompleteWrite(odd));
        Assert.True(PartyPresencePublicationRule.IsStableRead(0, 0));
    }

    [Fact]
    public void ReconnectUsesGenerationAlreadyInTheBlock()
    {
        // A restarted bridge must observe the block's stable generation before publishing.
        // Otherwise writing 1 after a prior 2 would let a reader that sampled 2 accept the
        // restarted payload when the writer eventually returned to 2.
        Assert.Equal(3u, PartyPresencePublicationRule.BeginWrite(2));
    }

    [Fact]
    public void RetryAfterFailedCommitAdvancesBusyGeneration()
    {
        // A failed even-store leaves the slot busy. Retrying from the observed odd marker
        // advances to another odd marker and remains unreadable until an even commit lands.
        uint retryOdd = PartyPresencePublicationRule.BeginWrite(1);

        Assert.Equal(3u, retryOdd);
        Assert.False(PartyPresencePublicationRule.IsStableRead(retryOdd, retryOdd));
    }
}
