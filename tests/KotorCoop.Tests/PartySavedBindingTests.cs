using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PartySavedBindingTests
{
    private static PartyRoster Roster(uint generation = 5, uint epoch = 3, string? hash = null)
        => new(1, 1, Occupants: [new(generation, "orn_fakechanna", true, hash ?? new string('A', 64))],
            HostSessionId: 20, HostLoadSerial: 2, HostNativeEpoch: epoch, RefreshSaveIdentity: 45, RefreshRevision: 6);

    [Fact]
    public unsafe void FirstNonstockAdmissionNeedsSavedProofEvenWithoutReplacementFlag()
    {
        byte* memory = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)SharedBlock.TotalSize);
        try
        {
            SharedBlockView view = new(memory);
            *(uint*)(memory + SharedBlock.OffsetLivenessNativeEpoch) = 8;
            int bank = SharedBlock.OffsetHostPartyOccupants;
            *(uint*)(memory + bank) = 2;
            *(uint*)(memory + bank + 4) = 8;
            *(uint*)(memory + bank + 20) = 8;
            *(uint*)(memory + bank + 12) = 20;
            int row = bank + SharedBlock.PartyOccupantRowsOffset;
            *(uint*)(memory + row) = 5;
            Encoding.ASCII.GetBytes("orn_fakechanna").CopyTo(new Span<byte>(memory + row + 8,16));
            PartyOccupantReconcileState.Commit(0,20,5);
            Assert.True(view.HostPartyOccupantRequiresBinding(0));
            Assert.False(view.CanMirrorPartyOccupant(0,5));
            memory[bank + 240] = 2;
            *(uint*)(memory + SharedBlock.OffsetPartyAdoptedLocalGenerations) = PartyOccupantHook.Captured.Generation(0);
            Assert.True(view.CanMirrorPartyOccupant(0,5));
            Assert.False(PartySavedOccupantRule.RequiresBinding(0,new(1,"p_bastilla")));
        }
        finally { PartyOccupantReconcileState.Invalidate(); System.Runtime.InteropServices.NativeMemory.Free(memory); }
    }

    [Fact]
    public void EqualRosterCopiesRemainEqualAndLocalReplacementRevokesTransferredProof()
    {
        PartyRoster roster = Roster();
        Assert.Equal(roster, roster with { Occupants = Enumerable.Range(0,9).Select(roster.OccupantFor).ToArray() });
        uint[] generations = [7];
        PartyAdoptionProof proof = new(roster, [new string('A',64)], 4, 8, 45, generations);
        generations[0] = 8;
        Assert.True(proof.Allows(0, roster, 4, 8, 7));
        Assert.False(proof.Allows(0, roster, 4, 8, 8));
    }

    [Fact]
    public unsafe void NativeRecruitmentRevokesAdoptionBeforeManagedPublication()
    {
        byte* memory = (byte*)System.Runtime.InteropServices.NativeMemory.AllocZeroed((nuint)SharedBlock.TotalSize);
        try
        {
            SharedBlockView view = new(memory);
            PartyOccupantHook.Captured.InvalidateWorld();
            uint local = PartyOccupantHook.Captured.Generation(0);
            *(uint*)(memory + SharedBlock.OffsetLivenessNativeEpoch) = 8;
            *(uint*)(memory + SharedBlock.OffsetWorldLoadSerial) = 4;
            *(uint*)(memory + SharedBlock.OffsetHostPartyOccupants) = 2;
            *(uint*)(memory + SharedBlock.OffsetHostPartyOccupants + 4) = 8;
            *(uint*)(memory + SharedBlock.OffsetHostPartyOccupants + 8) = 4;
            memory[SharedBlock.OffsetHostPartyOccupants + 240] = 2;
            *(uint*)(memory + SharedBlock.OffsetPartyAdoptedLocalGenerations) = local;
            Assert.True(view.HostPartyOccupantAdopted(0));
            PartyOccupantHook.PublicationBank = (nint)(memory + SharedBlock.OffsetLocalPartyOccupants);
            *(uint*)PartyOccupantHook.PublicationBank = 2;
            PartyOccupantHook.InvalidatePublication();
            PartyOccupantHook.Captured.ObserveRemoval(0);
            Assert.False(view.HostPartyOccupantAdopted(0));
            Assert.Equal(1u, *(uint*)PartyOccupantHook.PublicationBank & 1);
        }
        finally { PartyOccupantHook.PublicationBank = 0; System.Runtime.InteropServices.NativeMemory.Free(memory); }
    }

    [Fact]
    public void ExactTransferredPayloadAndSettledNativeWorldAreRequired()
    {
        PartyRoster roster = Roster();
        PartyAdoptionProof missing = new(roster, [], 4, 8, 45);
        PartyAdoptionProof wrongBytes = new(roster, [new string('B', 64)], 4, 8, 45);
        PartyAdoptionProof wrongSave = new(roster, [new string('A', 64)], 4, 8, 46);
        Assert.False(missing.Allows(0, roster, 4, 8));
        Assert.False(wrongBytes.Allows(0, roster, 4, 8));
        Assert.False(wrongSave.Allows(0, roster, 4, 8));
        PartyAdoptionProof proof = new(roster, [new string('A', 64)], 4, 8, 45);
        Assert.True(proof.Allows(0, roster, 4, 8));
        Assert.False(proof.Allows(0, roster, 5, 8));
        Assert.False(proof.Allows(0, roster, 4, 9));
        Assert.False(proof.Allows(0, Roster(6), 4, 8));
        Assert.False(proof.Allows(0, roster with { RefreshRevision = 7 }, 4, 8));
        Assert.False(proof.Allows(0, roster with { HostSessionId = 21 }, 4, 8));
    }

    [Fact]
    public void AdoptionProofCopiesIdentityAndDoesNotDependOnLaterRuntimeMutation()
    {
        PartyOccupantIdentity[] identities = [new(5, "orn_fakechanna", true, new string('A', 64))];
        PartyRoster roster = Roster() with { Occupants = identities };
        PartyAdoptionProof proof = new(roster, [new string('A', 64)], 4, 8, 45);
        identities[0] = new(6, "p_channa", true, new string('B', 64));
        Assert.True(proof.Allows(0, Roster(), 4, 8));
        Assert.False(proof.Allows(0, roster, 4, 8));
        Assert.False(new PartyAdoptionProof(Roster() with { Occupants = [new(5, "", true, new string('A', 64))] },
            [new string('A', 64)], 4, 8, 45).Allows(0, Roster(), 4, 8));
    }

    [Fact]
    public void SavingRetainedOldBodyWithdrawsIdentityBeforeAnyRetryCanRelabelItsBytes()
    {
        PartyOccupantTable table = new();
        table.ObserveTemplate(0, "p_channa"u8, true);
        table.ObserveCachedBody(0, 0x40CB);
        table.ObserveRemoval(0);
        table.ObserveAdmission(0);
        table.ObserveTemplate(0, "orn_fakechanna"u8, true);
        uint generation = table.Generation(0);
        table.ObserveSerialization(0, 0x40CB);
        Assert.False(table.TemplateKnown(0));
        Assert.True(table.Replaced(0));
        Assert.NotEqual(generation, table.Generation(0));
        table.ObserveSerialization(0, 0x40CB);
        Assert.False(table.TemplateKnown(0));
    }

    [Theory]
    [InlineData(0x40CBu, 0x40CCu)]
    [InlineData(0x7F000000u, 0x40CCu)]
    public void VerifiedNewBodyAndVerifiedFormerlyEmptyCacheMaySerializeTheirCurrentState(uint retired, uint fresh)
    {
        PartyOccupantTable table = new();
        table.ObserveCachedBody(0, retired);
        table.HoldUnprovenLoadedSlot(0);
        table.ObserveAdmission(0);
        table.ObserveTemplate(0, "orn_fakechanna"u8, true);
        uint generation = table.Generation(0);
        table.ObserveSerialization(0, fresh);
        Assert.True(table.TemplateKnown(0));
        Assert.Equal(generation, table.Generation(0));
    }

    [Fact]
    public void UnknownLoadedSlotAndUnknownRetiredBodyNeverGainLegacyOrSaveAuthority()
    {
        PartyOccupantTable table = new();
        table.HoldUnprovenLoadedSlot(0);
        Assert.NotEqual(0u, table.Generation(0));
        Assert.False(table.LegacySideChannelAllowed(0));
        table.ObserveAdmission(0);
        table.ObserveTemplate(0, "orn_fakechanna"u8, true);
        table.ObserveSerialization(0, 0x40CC);
        Assert.False(table.TemplateKnown(0));
    }

    [Fact]
    public void SaveMemberStubPreservesRegistersFlagsAndBothOriginalBranches()
    {
        byte[] code = new byte[96]; nint stub = 0x10000000;
        int count = PartyOccupantHook.Emit(code, stub, 0x10001000, 0x563EC9,
            [0x8B, 0xF8, 0x85, 0xFF, 0x0F, 0x84, 0x44, 0x01, 0, 0], 3);
        Assert.Equal(new byte[] { 0x9C, 0x60, 0x50, 0x56 }, code[..4]);
        Assert.Equal(new byte[] { 0x83, 0xC4, 8, 0x61, 0x9D }, code[9..14]);
        Assert.Equal(new byte[] { 0x8B, 0xF8, 0x85, 0xFF, 0x0F, 0x84 }, code[14..20]);
        Assert.Equal(0x564017, (int)stub + 24 + BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(20, 4)));
        Assert.Equal(0x563ED3, (int)stub + count + BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(count - 4, 4)));
    }

    [Fact]
    public void CompanionLedgerCannotReplayLowerTotalsOldRunsOrDebtIntoReplacement()
    {
        CompanionDamageLedger ledger = new();
        PartyRoster old = Roster(5);
        ledger.ObserveRoster(old);
        PeerCompanionDamage Report(uint generation, uint total, uint occupant = 5, uint epoch = 3)
            => new(2, generation, 1, total, occupant, 9, 2, epoch);
        ledger.Merge([Report(1, 20)]);
        ledger.Merge([Report(1, 12)]);
        ledger.Merge([Report(1, 20)]);
        Assert.Equal(20u, ledger.OwedFor(1));
        ledger.Merge([Report(2, 4)]);
        ledger.Merge([Report(1, 20)]);
        Assert.Equal(24u, ledger.OwedFor(1));
        ledger.ObserveRoster(Roster(6));
        ledger.Merge([Report(2, 4, 6), Report(1, 20, 5)]);
        Assert.Equal(0u, ledger.OwedFor(1));
        ledger.Merge([Report(3, 2, 6)]);
        Assert.Equal(2u, ledger.OwedFor(1));
        ledger.ObserveRoster(Roster(6, 4));
        ledger.Merge([Report(4, 10, 6)]);
        Assert.Equal(0u, ledger.OwedFor(1));
        ledger.Merge([Report(4, 3, 6, 4)]);
        Assert.Equal(3u, ledger.OwedFor(1));
    }

    [Fact]
    public void NativeCompanionOwedMetadataRejectsPriorWorldAndOddPublication()
    {
        byte[] memory = new byte[SharedBlock.TotalSize];
        unsafe
        {
            fixed (byte* bytes = memory)
            {
                SharedBlockView block = new(bytes);
                void Put(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan(offset, 4), value);
                Put(SharedBlock.OffsetLivenessNativeEpoch, 5); Put(SharedBlock.OffsetWorldLoadSerial, 4);
                Put(SharedBlock.OffsetCompanionOwedMetadata, 2);
                Put(SharedBlock.OffsetCompanionOwedMetadata + 4, 4); Put(SharedBlock.OffsetCompanionOwedMetadata + 8, 4);
                Assert.False(block.CompanionOwedCurrent(out _));
                Put(SharedBlock.OffsetCompanionOwedMetadata + 4, 5);
                Assert.True(block.CompanionOwedCurrent(out uint sequence));
                Put(SharedBlock.OffsetCompanionOwedMetadata, 3);
                Assert.False(block.CompanionOwedStillCurrent(sequence));
            }
        }
        Assert.True(SharedBlock.OffsetHostPartyOccupants + 256 <= SharedBlock.OffsetCompanionTakenMetadata);
        Assert.True(SharedBlock.OffsetCompanionTakenMetadata + 36 <= SharedBlock.OffsetCompanionOwedMetadata);
        Assert.True(SharedBlock.OffsetCompanionOwedMetadata + 52 <= SharedBlock.OffsetPartyAdoptedLocalGenerations);
        Assert.True(SharedBlock.OffsetPartyAdoptedLocalGenerations + 9 * 4 <= SharedBlock.TotalSize);
        Assert.Equal(0, SharedBlock.TotalSize % 0x200);
    }

    [Fact]
    public void ExactUtcPayloadIsFingerprintedWhileMalformedSaveRemainsUnknown()
    {
        string directory = Path.Combine(Path.GetTempPath(), "kcoop-party-proof-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            GffWriter utc = new("UTC "); utc.Root.SetWord("Appearance_Type", 511);
            byte[] payload = utc.ToBytes();
            byte[] archive = new byte[192 + payload.Length];
            Encoding.ASCII.GetBytes("SAV V1.0").CopyTo(archive, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(0x10), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(0x18), 160);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(0x1C), 184);
            Encoding.ASCII.GetBytes("AVAILNPC0").CopyTo(archive, 160);
            BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(180), 2027);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(184), 192);
            BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(188), (uint)payload.Length);
            payload.CopyTo(archive, 192);
            string save = Path.Combine(directory, "SAVEGAME.sav"); File.WriteAllBytes(save, archive);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), PartyCacheFingerprint.ReadSave(directory)[0]);
            File.WriteAllBytes(save, [0, 1, 2]);
            Assert.All(PartyCacheFingerprint.ReadSave(directory), hash => Assert.Empty(hash));
        }
        finally { Directory.Delete(directory, true); }
    }
}
