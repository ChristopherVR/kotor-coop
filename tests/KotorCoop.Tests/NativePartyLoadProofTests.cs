using KotorCoop.Protocol;
using KotorCoop.Session;
using KotorCoop.Memory;
using KotorCoop.Plugin;
using System.Runtime.InteropServices;
using Xunit;

namespace KotorCoop.Tests;

public sealed class NativePartyLoadProofTests
{
    private static string NewDirectory()
    {
        string path=Path.Combine(Path.GetTempPath(),"kcoop-gui-proof-"+Guid.NewGuid(),"000900 - Co-op");Directory.CreateDirectory(path);
        foreach(string name in new[]{"SAVEGAME.sav","savenfo.res","PARTYTABLE.res","GLOBALVARS.res",LocalSaveReadProofReader.ManifestName})
            File.WriteAllText(Path.Combine(path,name),"original "+name);
        return path;
    }
    [Fact]
    public void CanonicalFingerprintIsExactlyExistingSidecarHashAndOriginalPreRequestBytesAreRequired()
    {
        string directory=NewDirectory();
        try
        {
            Assert.True(LocalSaveReadProofReader.TryCapture(directory,out var before));
            Assert.True(ClientCharacterPersistence.TryHashSave(directory,out string expected,out long length));
            Assert.Equal(expected,before!.SaveSha256);Assert.Equal(length,before.SaveLength);
            File.WriteAllText(Path.Combine(directory,"SAVEGAME.sav"),"different save loaded later");
            Assert.True(LocalSaveReadProofReader.TryCapture(directory,out var after));Assert.NotEqual(before,after);
        }
        finally{Directory.Delete(Path.GetDirectoryName(directory)!,true);}
    }
    [Theory][InlineData("GLOBALVARS.res")][InlineData("kotorcoop-party-provenance.json")]
    public void GlobalOrManifestMutationCannotReuseOriginalSourceProof(string file)
    {
        string directory=NewDirectory();
        try
        {
            Assert.True(LocalSaveReadProofReader.TryCapture(directory,out var before));
            File.WriteAllText(Path.Combine(directory,file),"mutated after native request");
            Assert.True(LocalSaveReadProofReader.TryCapture(directory,out var after));
            Assert.Equal(before!.SaveSha256,after!.SaveSha256);Assert.NotEqual(before,after);
        }
        finally{Directory.Delete(Path.GetDirectoryName(directory)!,true);}
    }
    [Theory][InlineData("SAVEGAME.sav")][InlineData("savenfo.res")][InlineData("PARTYTABLE.res")][InlineData("GLOBALVARS.res")][InlineData("kotorcoop-party-provenance.json")]
    public void EveryMissingSelectedFileRefusesPreRequestCapture(string file)
    {
        string directory=NewDirectory();
        try{File.Delete(Path.Combine(directory,file));Assert.False(LocalSaveReadProofReader.TryCapture(directory,out _));}
        finally{Directory.Delete(Path.GetDirectoryName(directory)!,true);}
    }
    [Fact]
    public void OversizedManifestAndOversizedSparseArchiveRefuseWithoutBuffering()
    {
        string directory=NewDirectory();
        try
        {
            File.WriteAllText(Path.Combine(directory,LocalSaveReadProofReader.ManifestName),new string(' ',16385));
            Assert.False(LocalSaveReadProofReader.TryCapture(directory,out _));
            File.WriteAllText(Path.Combine(directory,LocalSaveReadProofReader.ManifestName),"known");
            using(var file=File.OpenWrite(Path.Combine(directory,"SAVEGAME.sav")))file.SetLength(536_870_913);
            Assert.False(LocalSaveReadProofReader.TryCapture(directory,out _));
        }
        finally{Directory.Delete(Path.GetDirectoryName(directory)!,true);}
    }
    [Fact]
    public void ExactGuiNumberAndServerNameResolveWithoutNewestSaveSelection()
    {
        string saves=Path.Combine(Path.GetTempPath(),"saves");
        Assert.True(LocalSaveReadProofReader.TryResolve(saves,375,LoadProvenanceSource.MainMenuSaveEntry,"Game375",out string path));
        Assert.Equal(Path.Combine(saves,"000375 - Game375"),path);
        Assert.True(LocalSaveReadProofReader.TryResolve(saves,0,LoadProvenanceSource.ServerLoadRequest,"QUICKSAVE",out path));
        Assert.EndsWith("000000 - QUICKSAVE",path);
        Assert.True(LocalSaveReadProofReader.TryResolve(saves,int.MinValue,LoadProvenanceSource.MainMenuDirectDirectory,"000001 - AUTOSAVE",out _));
        Assert.False(LocalSaveReadProofReader.TryResolve(saves,1,LoadProvenanceSource.MainMenuSaveEntry,"../other",out _));
        Assert.False(LocalSaveReadProofReader.TryResolve(saves,1000000,LoadProvenanceSource.ServerLoadRequest,"Co-op",out _));
    }
    [Theory]
    [InlineData(3,7,4,8,true)][InlineData(3,0,4,1,true)][InlineData(3,7,3,8,false)]
    [InlineData(3,7,4,7,false)][InlineData(3,7,4,0,false)][InlineData(3,7,5,8,false)]
    public void OnlyCorrespondingActuallyCompletedWorldLoadCanRestore(uint serial,uint epoch,uint currentSerial,uint currentEpoch,bool allowed)
        =>Assert.Equal(allowed,NativePartyLoadProofRule.Completed(serial,epoch,currentSerial,currentEpoch));
    [Fact]public void ExpiredAndFutureRequestTimesRefuse()
    {Assert.True(NativePartyLoadProofRule.Recent(10,90010));Assert.False(NativePartyLoadProofRule.Recent(10,90011));Assert.False(NativePartyLoadProofRule.Recent(10,9));}
    [Fact]
    public unsafe void NativeBankIsCorrelatedToRequestAndFailedLaterRequestRevokesProof()
    {
        nint block=Marshal.AllocHGlobal(SharedBlock.TotalSize);using ProcessMemory memory=ProcessMemory.Attach(Environment.ProcessId);
        using KotorGameBridge bridge=new(memory,ownsMemory:false);
        try
        {
            new Span<byte>((void*)block,SharedBlock.TotalSize).Clear();
            typeof(KotorGameBridge).GetField("_pluginBlock",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.SetValue(bridge,block);
            SharedBlockView view=new((byte*)block);
            LocalSaveReadProof source=new("000900 - Co-op",new string('A',64),100,new string('B',64),new string('C',64));
            view.PublishNativePartyLoadProof(2,3,7,100,source);
            Assert.False(bridge.TryReadNativePartyLoadProof(out _,out _));
            Marshal.WriteInt32(block+SharedBlock.OffsetLoadProvenanceGeneration,2);
            Assert.True(bridge.TryReadNativePartyLoadProof(out uint generation,out var proof));Assert.Equal(2u,generation);Assert.Equal(source,proof!.Source);
            Assert.True(NativePartyLoadProofRule.CanRestore(proof.RequestedAtTick,150,proof.RequestLoadSerial,proof.RequestNativeEpoch,4,8));
            Assert.False(NativePartyLoadProofRule.CanRestore(proof.RequestedAtTick,90101,proof.RequestLoadSerial,proof.RequestNativeEpoch,4,8));
            Assert.False(NativePartyLoadProofRule.CanRestore(proof.RequestedAtTick,150,proof.RequestLoadSerial,proof.RequestNativeEpoch,5,9));
            view.PublishNativePartyLoadProof(4,3,7,101,null);Marshal.WriteInt32(block+SharedBlock.OffsetLoadProvenanceGeneration,4);
            Assert.True(bridge.TryReadNativePartyLoadProof(out generation,out proof));Assert.Equal(4u,generation);Assert.Null(proof);
            Assert.True(SharedBlock.OffsetNativeSaveCompletion+SharedBlock.NativeSaveCompletionSize<=SharedBlock.OffsetNativePartyLoadProof);
            Assert.True(SharedBlock.OffsetNativePartyLoadProof+SharedBlock.NativePartyLoadProofSize<=SharedBlock.TotalSize);
        }
        finally{Marshal.FreeHGlobal(block);}
    }
}
