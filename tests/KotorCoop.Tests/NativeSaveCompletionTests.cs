using KotorCoop.Memory;
using KotorCoop.Plugin;
using Xunit;
using System.Runtime.InteropServices;

namespace KotorCoop.Tests;

public sealed class NativeSaveCompletionTests
{
    private const string Folder="000900 - Co-op";
    private static readonly string[] Files=["SAVEGAME.sav","PARTYTABLE.res","savenfo.res","GLOBALVARS.res"];
    private static NativeSaveIoWatch Ready(int omit=-1)
    {
        NativeSaveIoWatch watch=new(); Assert.True(watch.Begin(1,Folder));
        for(int i=0;i<4;i++)if(i!=omit)
        { watch.Write(1,(nint)(i+1),"D:/Game/saves/"+Folder+"/"+Files[i],100,100); watch.Close(1,(nint)(i+1),0); }
        return watch;
    }
    [Fact] public void ExactFourOutputsMustBeWrittenAndSuccessfullyClosed()=>Assert.True(Ready().Complete(1));
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)][InlineData(3)]
    public void EveryMissingOutputRefusesCompletion(int omitted)=>Assert.False(Ready(omitted).Complete(1));
    [Fact] public void NoWritesAndUnrelatedLogsDoNotCertifyAnExistingSave()
    {
        NativeSaveIoWatch watch=new();watch.Begin(1,Folder);
        watch.Write(1,1,"D:/Game/kotorcoop.log",10,10);watch.Close(1,1,0);
        Assert.False(watch.Complete(1));
    }
    [Fact] public void OtherThreadAndOtherFolderCannotSupplyMissingArchive()
    {
        NativeSaveIoWatch watch=Ready(0);
        watch.Write(2,1,"D:/Game/saves/"+Folder+"/SAVEGAME.sav",100,100);watch.Close(2,1,0);
        watch.Write(1,1,"D:/Game/saves/000901 - Co-op/SAVEGAME.sav",100,100);watch.Close(1,1,0);
        Assert.False(watch.Complete(1));
    }
    [Theory][InlineData(0)][InlineData(1)][InlineData(2)]
    public void WriteFlushAndCloseFailuresAreIndependentRefusals(int kind)
    {
        NativeSaveIoWatch watch=Ready(0);
        watch.Write(1,1,"D:/Game/saves/"+Folder+"/SAVEGAME.sav",kind==0?99u:100u,100);
        watch.Flush(1,1,kind==1?-1:0);watch.Close(1,1,kind==2?-1:0);
        Assert.False(watch.Complete(1));
    }
    [Fact] public void ReusedFilePointerMustCloseItsNewFileToo()
    {
        NativeSaveIoWatch watch=new();watch.Begin(1,Folder);
        for(int i=0;i<4;i++)
        {
            watch.Write(1,1,"D:/Game/saves/"+Folder+"/"+Files[i],100,100);
            if(i!=3)watch.Close(1,1,0);
        }
        Assert.False(watch.Complete(1));
        watch.Begin(1,Folder);
        for(int i=0;i<4;i++){watch.Write(1,1,"D:/Game/saves/"+Folder+"/"+Files[i],100,100);watch.Close(1,1,0);}
        Assert.True(watch.Complete(1));
    }
    [Fact] public void FailedAttemptAndNestedStallCannotLeakSuccessIntoNextSave()
    {
        NativeSaveIoWatch watch=Ready(); Assert.False(watch.Begin(1,Folder));Assert.False(watch.Complete(1));
        Assert.True(watch.Begin(1,Folder));Assert.False(watch.Complete(1));
        Assert.False(watch.Complete(1));
    }
    [Fact] public void CompletionBankFitsAfterAllEarlierBanks()
    {
        Assert.True(SharedBlock.OffsetPartyRestore+16+9*24<=SharedBlock.OffsetNativeSaveCompletion);
        Assert.True(SharedBlock.OffsetNativeSaveCompletion+SharedBlock.NativeSaveCompletionSize<=SharedBlock.TotalSize);
        Assert.Equal(0,SharedBlock.TotalSize%0x200);
    }
    [Fact] public void SpliceRelocatesOriginalCallAndPreservesArgumentsAndFlags()
    {
        byte[] original=[0x89,0x74,0x24,0x24,0xE8,0x0B,0xFF,0x0F,0];
        Span<byte> stub=stackalloc byte[128];
        int n=NativeSaveCompletionHook.Emit(stub,0x10000000,0x4B327C,0x20000000,original,[0x57,0x56],4);
        Assert.Equal(new byte[]{0x9C,0x60,0x57,0x56},stub[..4].ToArray());
        Assert.Equal(new byte[]{0x83,0xC4,8,0x61,0x9D},stub.Slice(9,5).ToArray());
        Assert.Equal(0x5B3190,0x10000000+14+9+BitConverter.ToInt32(stub.Slice(19,4)));
        Assert.Equal(0x4B3285,0x10000000+n+BitConverter.ToInt32(stub.Slice(n-4,4)));
    }
    [Theory]
    [InlineData(2,3,1,-1,true)]
    [InlineData(3,3,1,-1,false)]
    [InlineData(2,4,1,-1,false)]
    [InlineData(2,3,2,-1,false)]
    [InlineData(2,3,1,0,false)]
    public void ReloadMembershipAndScriptControlChangesRevokeCompletion(uint epoch,uint serial,uint packed,int control,bool allowed)
        =>Assert.Equal(allowed,NativeSaveCompletionHook.ContextCurrent(2,3,1,-1,epoch,serial,packed,control));
    [Fact]
    public unsafe void CoherentCompletionReaderRetainsActualMasksIdsAndTemplateLineage()
    {
        nint block=Marshal.AllocHGlobal(SharedBlock.TotalSize);
        using ProcessMemory memory=ProcessMemory.Attach(Environment.ProcessId);
        using KotorGameBridge bridge=new(memory,ownsMemory:false);
        try
        {
            new Span<byte>((void*)block,SharedBlock.TotalSize).Clear();
            typeof(KotorGameBridge).GetField("_pluginBlock",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(bridge,block);
            nint bank=block+SharedBlock.OffsetNativeSaveCompletion;
            Marshal.WriteInt32(bank,2);Marshal.WriteInt32(bank+4,7);Marshal.WriteInt32(bank+8,3);
            Marshal.WriteInt32(bank+12,unchecked((int)(SharedBlock.PartyRosterValid|1u|(1u<<16))));
            memory.TryWriteAsciiString(bank+16,Folder,96);Marshal.WriteInt32(bank+112,5);Marshal.WriteInt32(bank+116,1);
            memory.TryWriteAsciiString(bank+120,"orn_fakechanna",16);Marshal.WriteInt32(bank+328,0x40CA);
            Assert.True(bridge.TryReadNativeSaveCompletion(out uint sequence,out string directory,out var party));
            Assert.Equal(2u,sequence);Assert.Equal(Folder,directory);Assert.Equal(7u,party.HostNativeEpoch);Assert.Equal(3u,party.HostLoadSerial);
            Assert.True(party.IsAvailable(0));Assert.True(party.IsInParty(0));Assert.Equal(0x40CAu,party.ObjectIdFor(0));
            Assert.Equal("orn_fakechanna",party.OccupantFor(0).Template);Assert.True(party.OccupantFor(0).Replaced);Assert.Equal(5u,party.OccupantFor(0).Generation);
            Marshal.WriteInt32(bank,3);Assert.False(bridge.TryReadNativeSaveCompletion(out _,out _,out _));
        }
        finally{Marshal.FreeHGlobal(block);}
    }
    [Fact] public void MissingOrInvalidNativeOutputFilesRefuseArtifactCompletion()
    {
        string directory=Path.Combine(Path.GetTempPath(),"kcoop-savecompletion-"+Guid.NewGuid());Directory.CreateDirectory(directory);
        try
        {
            Assert.False(NativeSaveArtifactRule.Complete(directory));
            foreach(string file in Files)File.WriteAllBytes(Path.Combine(directory,file),new byte[100]);
            Assert.False(NativeSaveArtifactRule.Complete(directory));
        }
        finally{Directory.Delete(directory,true);}
    }
}
