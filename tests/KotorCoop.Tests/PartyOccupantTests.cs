using System.Text;
using KotorCoop.Plugin;
using KotorCoop.Protocol;
using Xunit;
namespace KotorCoop.Tests;
public sealed class PartyOccupantTests
{
    [Fact]
    public void SameAvailabilityMaskReplacementHasFreshGenerationAndExactTemplate()
    {
        PartyOccupantTable table = new();
        Assert.True(table.ObserveTemplate(0, "p_bastilla"u8, true));
        uint first = table.Generation(0);
        table.ObserveRemoval(0);
        table.ObserveUnknown(0); // accepted direct AddNPC call before template wrapper returns
        Assert.True(table.ObserveTemplate(0, "orn_fakechanna"u8, true));
        Assert.NotEqual(first, table.Generation(0)); Assert.True(table.Replaced(0));
        Span<byte> bytes = stackalloc byte[16]; table.CopyTemplate(0, bytes);
        Assert.Equal("orn_fakechanna", Encoding.ASCII.GetString(bytes).TrimEnd('\0'));
    }
    [Fact]
    public void FailedOrMalformedTemplateNeverEstablishesKnownIdentity()
    {
        PartyOccupantTable table = new(); table.ObserveUnknown(0);
        uint generation = table.Generation(0);
        Assert.False(table.ObserveTemplate(0, "orn_fakechanna"u8, false));
        Assert.False(table.ObserveTemplate(0, "../p_bastilla"u8, true));
        Assert.False(table.ObserveTemplate(9, "p_carth"u8, true));
        Assert.Equal(generation, table.Generation(0));
        Span<byte> bytes = stackalloc byte[16]; table.CopyTemplate(0,bytes); Assert.True(bytes.SequenceEqual(new byte[16]));
    }
    [Fact]
    public void NormalFirstStockRecruitPreservesLegacyDamageHealAndXpButReplacementDoesNot()
    {
        PartyOccupantTable table = new();
        Assert.True(table.LegacySideChannelAllowed(2));
        table.ObserveAdmission(2);
        Assert.False(table.LegacySideChannelAllowed(2)); // unknown direct-object identity
        table.ObserveTemplate(2,"p_carth"u8,true);
        Assert.False(table.Replaced(2)); Assert.True(table.LegacySideChannelAllowed(2));
        table.ObserveAdmission(2); // second accepted AddNPC, even without captured Remove
        table.ObserveTemplate(2,"p_carth"u8,true);
        Assert.True(table.Replaced(2)); Assert.False(table.LegacySideChannelAllowed(2));
        table.InvalidateWorld(); Assert.False(table.LegacySideChannelAllowed(2));
    }
    [Fact]
    public void LoadedSaveCannotKeepStaleModIdentityButStockMappingSurvives()
    {
        PartyOccupantTable table = new();
        table.ObserveTemplate(2,"p_carth"u8,true);
        table.ObserveRemoval(0); table.ObserveTemplate(0,"orn_fakechanna"u8,true);
        uint before = table.Generation(0); table.InvalidateWorld();
        Assert.Equal(0u, table.Generation(2)); Assert.NotEqual(before, table.Generation(0));
        Span<byte> bytes = stackalloc byte[16]; table.CopyTemplate(0,bytes); Assert.True(bytes.SequenceEqual(new byte[16]));
        Assert.True(table.Replaced(0));
    }
    [Theory]
    [InlineData(5u,false,false,false,true,PartyOccupantAction.Hold)]
    [InlineData(5u,true,false,true,true,PartyOccupantAction.Hold)]
    [InlineData(5u,true,false,false,false,PartyOccupantAction.Hold)]
    [InlineData(5u,true,false,false,true,PartyOccupantAction.RecruitInactive)]
    [InlineData(5u,true,true,true,true,PartyOccupantAction.Unchanged)]
    [InlineData(0u,false,false,true,true,PartyOccupantAction.Unchanged)]
    public void UnknownAndActiveReplacementCannotGuessOrDestroyActors(uint generation,bool known,bool applied,bool active,bool enabled,PartyOccupantAction expected)
        => Assert.Equal(expected,PartyOccupantRule.Decide(generation,known,applied,active,enabled));
    [Fact]
    public void RosterAndSnapshotRoundTripOccupantGenerationAndNamespace()
    {
        byte[] buffer = new byte[4096];
        PartyOccupantIdentity[] occupants = new PartyOccupantIdentity[9];
        for(int i=0;i<9;i++) occupants[i]=new(0,"");
        occupants[0]=new(8,"orn_fakechanna",true);
        PartyRosterPacket roster=new(20,new PartyRoster(1,0,Occupants:occupants,HostSessionId:100,HostLoadSerial:3,HostNativeEpoch:4));
        int length=PacketCodec.Write(buffer,roster); var read=PacketCodec.ReadPartyRoster(buffer.AsSpan(0,length))!;
        Assert.Equal(roster.Roster,read.Roster); Assert.Equal(100u,read.Roster.HostSessionId);
        PartySnapshotPacket snapshot=new(21,[new(0,Vector3f.Zero,0,0,12,OccupantGeneration:8)],100,3,4);
        length=PacketCodec.Write(buffer,snapshot); var decoded=PacketCodec.ReadPartySnapshot(buffer.AsSpan(0,length))!;
        Assert.Equal(8u,Assert.Single(decoded.Members).OccupantGeneration); Assert.Equal(4u,decoded.HostNativeEpoch);
    }
    [Fact]
    public void SuccessfulTemplateSplicePreservesStackAndRelocatesConditionalBranch()
    {
        byte[] code=new byte[96]; nint stub=0x10000000;
        int length=PartyOccupantHook.Emit(code,stub,0x10001000,0x5646A6,[0x85,0xF6,0x8B,0xF8,0x74,0x08],1);
        Assert.Equal(new byte[]{0x9C,0x60,0x8B,0x54,0x24,0x54,0x50,0x52,0x57},code[..9]);
        Assert.Equal(new byte[]{0x83,0xC4,12,0x61,0x9D,0x85,0xF6,0x8B,0xF8,0x0F,0x84},code[14..25]);
        Assert.Equal(0x5646B4,(long)stub+29+BitConverter.ToInt32(code,25));
        Assert.Equal(0x5646AC,(long)stub+length+BitConverter.ToInt32(code,length-4));
    }
    [Fact]
    public void OccupantBanksDoNotOverlapPriorLocalEventsAndFitAlignedAllocation()
    {
        Assert.True(SharedBlock.OffsetLocalPlaceableChanges+32*SharedBlock.LocalPlaceableChangeStride<=SharedBlock.OffsetLocalPartyOccupants);
        Assert.Equal(SharedBlock.OffsetHostPartyOccupants,SharedBlock.OffsetLocalPartyOccupants+256);
        Assert.True(SharedBlock.OffsetHostPartyOccupants+SharedBlock.PartyOccupantRowsOffset+9*SharedBlock.PartyOccupantStride+9<=SharedBlock.TotalSize);
        Assert.Equal(0,SharedBlock.TotalSize%0x200);
    }
    [Fact]
    public unsafe void NativeGateRejectsOldSnapshotGenerationAndOldLocalWorld()
    {
        byte[] memory = new byte[SharedBlock.TotalSize];
        fixed(byte* b=memory)
        {
            void Set(int offset,uint value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan(offset,4),value);
            int bank=SharedBlock.OffsetHostPartyOccupants,row=bank+SharedBlock.PartyOccupantRowsOffset;
            Set(SharedBlock.OffsetLivenessNativeEpoch,1); Set(SharedBlock.OffsetWorldLoadSerial,3);
            Set(SharedBlock.OffsetHostPartyRoster,SharedBlock.PartyRosterValid|1);
            Set(bank,2); Set(bank+4,1); Set(bank+8,3); Set(bank+12,100);
            Set(bank+16,SharedBlock.PartyRosterValid|1); Set(bank+20,1);
            Set(row,8); Set(row+4,8); "p_bastilla"u8.CopyTo(memory.AsSpan(row+8));
            SharedBlockView view=new(b);
            Assert.False(view.CanMirrorPartyOccupant(0,8));
            PartyOccupantReconcileState.Commit(0,100,8);
            Assert.True(view.CanMirrorPartyOccupant(0,8));
            Assert.False(view.CanMirrorPartyOccupant(0,7)); // old HP/snapshot
            Set(SharedBlock.OffsetLivenessNativeEpoch,2); Assert.False(view.CanMirrorPartyOccupant(0,8));
            Set(bank+4,2); Assert.False(view.CanMirrorPartyOccupant(0,8)); // old snapshot commit
            Set(bank+20,2); Assert.True(view.CanMirrorPartyOccupant(0,8));
            memory[bank+240]=1; Assert.False(view.CanMirrorPartyOccupant(0,8)); // replacement active hold
            memory[bank+240]=0; Set(bank,3); Assert.False(view.CanMirrorPartyOccupant(0,8));
            PartyOccupantReconcileState.Invalidate();
        }
    }

    [Fact]
    public void DelayedRosterSnapshotCannotDriveReplacedOrNewWorldOccupant()
    {
        using KotorCoop.Session.CoopClient client=new(new KotorCoop.Session.SessionSettings(),new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,27140));
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        typeof(KotorCoop.Session.CoopClient).GetField("_hostSessionId",flags)!.SetValue(client,100u);
        void World(uint serial,uint epoch)=>typeof(KotorCoop.Session.CoopClient).GetField("_hostWorld",flags)!.SetValue(client,new WorldDigest(1,1,"bos_orion",1,serial,NativeWorldEpoch:epoch));
        void Deliver(string method,byte[] bytes,int length)=>((PartyHandler)typeof(KotorCoop.Session.CoopClient).GetMethod(method,flags)!.CreateDelegate(typeof(PartyHandler),client))
            (new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,27140),bytes.AsSpan(0,length),0);
        PartyOccupantIdentity[] identities=new PartyOccupantIdentity[9];
        byte[] buffer=new byte[4096]; World(3,4);
        void Roster(long time,uint generation)
        {
            identities[0]=new(generation,"orn_fakechanna",true);
            Deliver("HandlePartyRoster",buffer,PacketCodec.Write(buffer,new PartyRosterPacket(time,new(1,1,Occupants:identities,HostSessionId:100,HostLoadSerial:3,HostNativeEpoch:4))));
        }
        void Snapshot(long time,uint generation)=>Deliver("HandlePartySnapshot",buffer,PacketCodec.Write(buffer,new PartySnapshotPacket(time,[new(0,Vector3f.Zero,0,0,12,OccupantGeneration:generation)],100,3,4)));
        Roster(10,8); Snapshot(10,8); Assert.Single(client.GetPartyMembers());
        Roster(11,9); Assert.Empty(client.GetPartyMembers());
        Snapshot(11,9); Assert.Single(client.GetPartyMembers());
        Roster(10,8); Snapshot(10,8); Assert.Equal(9u,Assert.Single(client.GetPartyMembers()).OccupantGeneration);
        World(4,5); Roster(100,8); Snapshot(100,8); Assert.Empty(client.GetPartyMembers());
        Assert.False(client.TryGetPartyRoster(out _));
    }
    private delegate void PartyHandler(System.Net.IPEndPoint from,ReadOnlySpan<byte> bytes,long now);
    [Fact]
    public unsafe void EquipmentCannotEscapeItsPresencePublicationOrDriveAnotherOccupant()
    {
        byte[] memory=new byte[SharedBlock.TotalSize];
        fixed(byte* b=memory)
        {
            void Set(int offset,uint value)=>System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan(offset,4),value);
            int bank=SharedBlock.OffsetHostPartyOccupants,row=bank+SharedBlock.PartyOccupantRowsOffset,presence=SharedBlock.OffsetPartyPresenceSlots;
            Set(SharedBlock.OffsetLivenessNativeEpoch,1); Set(SharedBlock.OffsetHostPartyRoster,SharedBlock.PartyRosterValid|1);
            Set(bank,2); Set(bank+4,1); Set(bank+12,100); Set(bank+16,SharedBlock.PartyRosterValid|1); Set(bank+20,1);
            Set(row,8); Set(row+4,8); "p_bastilla"u8.CopyTo(memory.AsSpan(row+8));
            Set(presence,1); Set(presence+0x24,2); "g_w_lghtsbr01"u8.CopyTo(memory.AsSpan(SharedBlock.OffsetCompanionEquipSlots));
            SharedBlockView view=new(b); PartyOccupantReconcileState.Commit(0,100,8);
            Assert.True(view.TryGetPartyPresence(0,out var member)); Span<byte> equipment=stackalloc byte[48];
            Assert.True(view.TryCopyCompanionEquipment(0,member,equipment));
            Assert.Equal("g_w_lghtsbr01",Encoding.ASCII.GetString(equipment[..16]).TrimEnd('\0'));
            Set(presence+0x24,3); Assert.False(view.TryCopyCompanionEquipment(0,member,equipment));
            Set(presence+0x24,4); Set(row+4,9); Assert.False(view.TryCopyCompanionEquipment(0,member,equipment));
            PartyOccupantReconcileState.Invalidate();
        }
    }
}
