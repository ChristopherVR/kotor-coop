using KotorCoop.Plugin;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public sealed class LoadProvenanceLayoutTests
{
    [Fact]
    public void MailboxUsesTheAgreedV74FreeTailBeforeTheLaterExtensions()
    {
        // TotalSize has grown since v74 (most recently to 0x8600 for v80's "no cap really"
        // pass, which relocated the enemy tables into a brand-new region well past the
        // load-provenance mailbox below) — this assertion tracks the CURRENT edge, same as
        // every other test in this file's family that pins SharedBlock.TotalSize.
        Assert.Equal(0x8600, SharedBlock.TotalSize);
        Assert.Equal(0x4E50, SharedBlock.OffsetLoadProvenanceGeneration);
        Assert.Equal(0x4E54, SharedBlock.OffsetLoadProvenanceSlot);
        Assert.Equal(0x4E58, SharedBlock.OffsetLoadProvenanceSource);
        Assert.Equal(0x4E5C, SharedBlock.OffsetLoadProvenanceStatus);
        Assert.Equal(0x4E60, SharedBlock.OffsetLoadProvenanceWorldLoadSerial);
        Assert.Equal(0x4E64, SharedBlock.OffsetLoadProvenanceNameLength);
        Assert.Equal(0x4E68, SharedBlock.OffsetLoadProvenanceName);
        Assert.Equal(64, SharedBlock.LoadProvenanceNameCapacity);
        Assert.Equal(0x4EA8, SharedBlock.OffsetLoadProvenanceName + SharedBlock.LoadProvenanceNameCapacity);
        Assert.Equal(0x58, 0x4F00 - (SharedBlock.OffsetLoadProvenanceName + SharedBlock.LoadProvenanceNameCapacity));
        Assert.Equal(0x1C8, SharedBlock.LoadEntrySaveNumberOffset);
        Assert.Equal(0x1D8, SharedBlock.LoadEntrySaveNameOffset);
        Assert.Equal(0x1C4, LoadProvenanceCodec.MainMenuEntryFlagsOffset);
        Assert.Equal(0x01u, LoadProvenanceCodec.MainMenuRealSaveFlag);
        Assert.Equal(0x30u, LoadProvenanceCodec.MainMenuDirectDirectoryFlags);
        Assert.Equal(0x210, LoadProvenanceCodec.MainMenuDirectDirectoryOffset);
    }
}
