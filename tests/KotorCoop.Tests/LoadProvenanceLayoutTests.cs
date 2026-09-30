using KotorCoop.Plugin;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public sealed class LoadProvenanceLayoutTests
{
    [Fact]
    public void MailboxUsesTheAgreedV74FreeTailBeforeTheLaterExtensions()
    {
        // v83's attributed bank starts after the v81/v82 dialogue and intent fields,
        // and its complete 8-peer capacity ends before v85's independent placeable
        // acknowledgements. Both banks fit without overlapping later extensions.
        Assert.Equal(0x8700, SharedBlock.OffsetAttributedDamageSequence);
        Assert.True(SharedBlock.OffsetAttributedDamageSequence >= SharedBlock.OffsetMenuLoadManualIntent + sizeof(uint));
        Assert.Equal(1024, SharedBlock.AttributedDamageCapacity);
        Assert.Equal(0xB710, SharedBlock.OffsetAttributedDamageSlots + SharedBlock.AttributedDamageCapacity * SharedBlock.AttributedDamageStride);
        int damageEnd = SharedBlock.OffsetAttributedDamageSlots + SharedBlock.AttributedDamageCapacity * SharedBlock.AttributedDamageStride;
        Assert.True(damageEnd <= SharedBlock.OffsetPlaceableAcknowledgements);
        Assert.Equal(0xB980, SharedBlock.OffsetPlaceableAcknowledgements + SharedBlock.PlaceableCapacity * SharedBlock.PlaceableAcknowledgementStride);
        Assert.Equal(0xB980, SharedBlock.OffsetDamageDealtIncarnation);
        Assert.True(SharedBlock.OffsetDamageDealtIncarnation + sizeof(uint) <= SharedBlock.OffsetLocalPlaceableChanges);
        Assert.True(SharedBlock.OffsetLocalPlaceableChanges + SharedBlock.PlaceableCapacity * SharedBlock.LocalPlaceableChangeStride <= SharedBlock.TotalSize);
        Assert.Equal(0, SharedBlock.TotalSize % 0x200);
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
