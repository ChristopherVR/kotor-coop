using KotorCoop.Plugin;
using Xunit;
namespace KotorCoop.Tests;
public sealed class LivenessBankTests
{
    [Theory]
    [InlineData(2, 0, 0, 1, 1, true)]
    [InlineData(0, 0, 0, 1, 1, false)]
    [InlineData(3, 0, 0, 1, 1, false)]
    [InlineData(2, 1, 2, 1, 1, false)]
    [InlineData(2, 0, 0, 1, 2, false)] // same module acknowledged load, serial unchanged
    [InlineData(2, 0, 0, 0, 0, false)]
    public void NativeBankRejectsPartialWritesAndEveryReplacedWorld(uint sequence, uint expectedSerial,
        uint serial, uint expectedEpoch, uint epoch, bool accepted) =>
        Assert.Equal(accepted, LivenessBankRule.CanRead(sequence, expectedSerial, serial, expectedEpoch, epoch));

    [Fact]
    public void BindingMetadataFitsBetweenDamageIncarnationAndLocalPlaceableEvents()
    {
        Assert.True(SharedBlock.OffsetLivenessExpectedWorldSerial >= 0xB984);
        Assert.Equal(SharedBlock.OffsetLivenessExpectedWorldSerial + 4, SharedBlock.OffsetLivenessExpectedEpoch);
        Assert.Equal(SharedBlock.OffsetLivenessExpectedEpoch + 4, SharedBlock.OffsetLivenessPublishSequence);
        Assert.Equal(SharedBlock.OffsetLivenessPublishSequence + 4, SharedBlock.OffsetLivenessNativeEpoch);
        Assert.True(SharedBlock.OffsetLivenessNativeEpoch + 4 <= SharedBlock.OffsetLocalPlaceableChanges);
        Assert.True(SharedBlock.OffsetLivenessNativeEpoch + 4 <= SharedBlock.TotalSize);
    }
}
