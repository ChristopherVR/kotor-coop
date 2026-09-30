using System.Buffers.Binary;
using System.Text;
using System.Reflection;
using System.Runtime.InteropServices;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PartySaveProvenanceTests
{
    [Theory]
    [InlineData(true,-1,true)]
    [InlineData(false,-1,false)]
    [InlineData(true,0,false)]
    [InlineData(true,8,false)]
    [InlineData(true,99,false)]
    public void ActualPartyScriptControlOverridesTransientSoloState(bool readable,int slot,bool expected)
        => Assert.Equal(expected,PartyScriptSaveSafetyRule.CanSave(readable,slot));
    private static byte[] Archive(int hitPoints = 12)
    {
        GffWriter utc = new("UTC "); utc.Root.SetShort("CurrentHitPoints", (short)hitPoints);
        utc.Root.SetString("Tag", "bastila"); // deliberately contains no TemplateResRef
        byte[] payload = utc.ToBytes(), archive = new byte[192 + payload.Length];
        Encoding.ASCII.GetBytes("SAV V1.0").CopyTo(archive,0);
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(0x10),1);
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(0x18),160);
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(0x1C),184);
        Encoding.ASCII.GetBytes("AVAILNPC0").CopyTo(archive,160);
        BinaryPrimitives.WriteUInt16LittleEndian(archive.AsSpan(180),2027);
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(184),192);
        BinaryPrimitives.WriteUInt32LittleEndian(archive.AsSpan(188),(uint)payload.Length);
        payload.CopyTo(archive,192); return archive;
    }
    private static PartyRoster Known(string template = "orn_fakechanna") =>
        new(1,1,Occupants:[new(5,template,true)],HostLoadSerial:2,HostNativeEpoch:3);

    [Fact]
    public void ExactVerifiedPayloadSurvivesRestartButSaveOverwriteCannotInheritIdentity()
    {
        string directory = Path.Combine(Path.GetTempPath(),"kcoop-provenance-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Combine(directory,"SAVEGAME.sav"); File.WriteAllBytes(file,Archive());
            Assert.False(PartySaveProvenance.TryRead(directory,out _));
            string[] hashes = PartyCacheFingerprint.ReadSave(directory);
            Assert.True(PartySaveProvenance.TryWriteVerified(directory,Known(),hashes,out _));
            Assert.True(PartySaveProvenance.TryRead(directory,out var proof));
            Assert.Equal("orn_fakechanna",proof!.Occupants[0].Template);
            Assert.Equal(hashes[0],proof.Occupants[0].CacheFingerprint);
            string staged = Path.Combine(directory,"staged"); Directory.CreateDirectory(staged);
            File.Copy(file,Path.Combine(staged,"SAVEGAME.sav"));
            Assert.True(PartySaveProvenance.MatchesSnapshot(staged,proof));
            File.WriteAllBytes(Path.Combine(staged,"SAVEGAME.sav"),Archive(9));
            Assert.True(PartySaveProvenance.MatchesSnapshot(directory,proof));
            Assert.False(PartySaveProvenance.MatchesSnapshot(staged,proof));
            File.WriteAllBytes(file,Archive(11));
            Assert.False(PartySaveProvenance.TryRead(directory,out _));
            Assert.False(PartySaveProvenance.TryWriteVerified(directory,Known(),hashes,out _));
            // New legitimate HP is certifiable only by another explicitly verified save.
            Assert.True(PartySaveProvenance.TryWriteVerified(directory,Known(),PartyCacheFingerprint.ReadSave(directory),out _));
            Assert.True(PartySaveProvenance.TryRead(directory,out _));
        }
        finally { Directory.Delete(directory,true); }
    }

    [Fact]
    public void UnknownTemplateAndMalformedOrOversizedManifestRemainUnknown()
    {
        string directory = Path.Combine(Path.GetTempPath(),"kcoop-provenance-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory,"SAVEGAME.sav"),Archive());
            Assert.False(PartySaveProvenance.TryWriteVerified(directory,Known(""),PartyCacheFingerprint.ReadSave(directory),out _));
            string path = Path.Combine(directory,PartySaveProvenance.FileName);
            File.WriteAllText(path,"{\"Version\":1,\"Occupants\":null}");
            Assert.False(PartySaveProvenance.TryRead(directory,out _));
            File.WriteAllText(path,"{\"Version\":1,\"SaveSha256\":null,\"SaveLength\":0,\"Occupants\":[{},{},{},{},{},{},{},{},{}]}");
            Assert.False(PartySaveProvenance.TryRead(directory,out _));
            File.WriteAllText(path,new string(' ',16_385));
            Assert.False(PartySaveProvenance.TryRead(directory,out _));
        }
        finally { Directory.Delete(directory,true); }
    }

    [Fact]
    public void IdenticalUtcBytesDoNotCrossTemplateLineagesBetweenSaveManifests()
    {
        string root = Path.Combine(Path.GetTempPath(),"kcoop-provenance-" + Guid.NewGuid());
        try
        {
            foreach (string template in new[]{"p_channa","orn_fakechanna"})
            {
                string directory = Path.Combine(root,template); Directory.CreateDirectory(directory);
                File.WriteAllBytes(Path.Combine(directory,"SAVEGAME.sav"),Archive());
                Assert.True(PartySaveProvenance.TryWriteVerified(directory,Known(template),PartyCacheFingerprint.ReadSave(directory),out _));
                Assert.True(PartySaveProvenance.TryRead(directory,out var proof));
                Assert.Equal(template,proof!.Occupants[0].Template);
            }
        }
        finally { Directory.Delete(root,true); }
    }

    [Fact]
    public void OnlyLoadUnknownCanRestoreAndActualRecruitmentRevokesEligibility()
    {
        PartyOccupantTable table = new();
        table.ObserveRemoval(0); table.ObserveTemplate(0,"orn_fakechanna"u8,true);
        table.InvalidateWorld(); uint loaded = table.Generation(0);
        Assert.True(table.RestoreVerifiedTemplate(0,loaded,"orn_fakechanna"u8,0x40CA,true));
        Assert.Equal(loaded,table.Generation(0));
        Assert.False(table.RestoreVerifiedTemplate(0,loaded,"p_channa"u8,0x40CA,true));
        table.ObserveSerialization(0,0x40CA); Assert.True(table.TemplateKnown(0));
        table.InvalidateWorld(); loaded = table.Generation(0);
        table.ObserveAdmission(0); // scripts changed slot before the Agent first observed load
        Assert.False(table.RestoreVerifiedTemplate(0,table.Generation(0),"orn_fakechanna"u8,0x40CA,true));
        Assert.False(table.RestoreVerifiedTemplate(0,loaded,"orn_fakechanna"u8,0x40CA,true));
        table.HoldUnprovenLoadedSlot(1);
        Assert.True(table.RestoreVerifiedTemplate(1,table.Generation(1),"new_companion"u8,0x7F000000,true));
    }

    [Fact]
    public void RestoreBankDoesNotOverlapCompanionBanksAndFitsAlignedAllocation()
    {
        Assert.True(SharedBlock.OffsetPartyAdoptedLocalGenerations + 9*4 <= SharedBlock.OffsetPartyRestore);
        Assert.True(SharedBlock.OffsetPartyRestore + SharedBlock.PartyRestoreRows + 9*SharedBlock.PartyRestoreStride <= SharedBlock.TotalSize);
        Assert.Equal(0,SharedBlock.TotalSize % 0x200);
    }

    [Fact]
    public unsafe void NativeIdsAndOccupantsPublishTogetherAndRestoreUsesCapturedGeneration()
    {
        nint block = Marshal.AllocHGlobal(SharedBlock.TotalSize);
        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);
        using KotorGameBridge bridge = new(memory,ownsMemory:false);
        try
        {
            new Span<byte>((void*)block,SharedBlock.TotalSize).Clear();
            typeof(KotorGameBridge).GetField("_pluginBlock",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(bridge,block);
            SharedBlockView view = new((byte*)block);
            Marshal.WriteInt32(block+SharedBlock.OffsetLivenessNativeEpoch,3);
            Marshal.WriteInt32(block+SharedBlock.OffsetWorldLoadSerial,2);
            uint packed = SharedBlock.PartyRosterValid | (1u << SharedBlock.PartyRosterAvailableShift)
                | (1u << SharedBlock.PartyRosterPartyShift);
            view.BeginLocalPartyPublication();
            view.SetLocalPartyRoster(1,1);
            view.SetLocalPartyRosterObjectId(0,0x40CA);
            Assert.False(bridge.TryReadLocalPartyRoster(out _));
            view.PublishLocalPartyOccupants(packed);
            Assert.True(bridge.TryReadLocalPartyRoster(out PartyRoster current));
            Assert.Equal(0x40CAu,current.ObjectIdFor(0));
            Assert.True(bridge.TryRestoreSavedPartyOccupants([new(5,"orn_fakechanna",true,new string('A',64))],current));
            nint row = block+SharedBlock.OffsetPartyRestore+SharedBlock.PartyRestoreRows;
            Assert.Equal(current.OccupantFor(0).Generation,unchecked((uint)Marshal.ReadInt32(row)));
            Assert.Equal(0x40CA,Marshal.ReadInt32(row+4));
            Assert.Equal("orn_fakechanna",Marshal.PtrToStringAnsi(row+8));
            Marshal.WriteInt32(block+SharedBlock.OffsetWorldLoadSerial,3);
            Assert.False(bridge.TryRestoreSavedPartyOccupants([new(5,"orn_fakechanna",true,new string('A',64))],current));
        }
        finally { Marshal.FreeHGlobal(block); }
    }
}
