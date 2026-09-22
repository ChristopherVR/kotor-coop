using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class LoadProvenanceLayoutTests
{
    [Fact]
    public void MailboxUsesTheAgreedV74FreeTailWithoutGrowingTheBlock()
    {
        Assert.Equal(0x4F00, SharedBlock.TotalSize);
        Assert.Equal(0x4E50, SharedBlock.OffsetLoadProvenanceGeneration);
        Assert.Equal(0x4E54, SharedBlock.OffsetLoadProvenanceSlot);
        Assert.Equal(0x4E58, SharedBlock.OffsetLoadProvenanceSource);
        Assert.Equal(0x4E5C, SharedBlock.OffsetLoadProvenanceStatus);
        Assert.Equal(0x4E60, SharedBlock.OffsetLoadProvenanceWorldLoadSerial);
        Assert.Equal(0x4E64, SharedBlock.OffsetLoadProvenanceNameLength);
        Assert.Equal(0x4E68, SharedBlock.OffsetLoadProvenanceName);
        Assert.Equal(64, SharedBlock.LoadProvenanceNameCapacity);
        Assert.Equal(0x4EA8, SharedBlock.OffsetLoadProvenanceName + SharedBlock.LoadProvenanceNameCapacity);
        Assert.Equal(0x58, SharedBlock.TotalSize - (SharedBlock.OffsetLoadProvenanceName + SharedBlock.LoadProvenanceNameCapacity));
    }
}
