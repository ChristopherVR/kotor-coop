using KotorCoop.Plugin;
using KotorCoop.Memory;
using System.Text.RegularExpressions;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;
namespace KotorCoop.Tests;
public class EnemyEffectNamespaceTests
{
    [Fact] public void SameProcessReconnectKeepsReplayIdentityAndNewProcessGetsFreshRun()
    {
        var window = new EnemyEffectReplayWindow();
        Assert.True(window.Observe(2, 100, 45));
        Assert.False(window.Observe(2, 100, 45)); // Re-Hello/agent restart does not clear this window.
        Assert.True(window.Observe(2, 100, 46));
        Assert.True(window.Observe(2, 101, 1)); // Only current authenticated incarnation reaches Observe.
        Assert.False(window.Observe(2, 101, 1));
    }
    [Fact] public void ReorderingIsAllowedButEvictedOldCaptureCannotReplayAgainstRecycledId()
    {
        var window = new EnemyEffectReplayWindow();
        Assert.True(window.Observe(2, 100, 10));
        Assert.True(window.Observe(2, 100, 12));
        Assert.True(window.Observe(2, 100, 11));
        for(uint serial=13;serial<800;serial++) Assert.True(window.Observe(2,100,serial));
        Assert.False(window.Observe(2,100,10));
    }
    [Fact] public void NativeSerialWrapAcceptsFreshCaptureAndRejectsOldReplay()
    {
        var window=new EnemyEffectReplayWindow();
        Assert.True(window.Observe(2,100,0xFFFFFF));
        Assert.True(window.Observe(2,100,1));
        Assert.False(window.Observe(2,100,0xFFFFFF));
        Assert.True(window.Observe(3,100,1));
    }
    [Theory]
    [InlineData(6u,6u,7u,7u,3u,3u,0u,0u,true)]
    [InlineData(6u,6u,7u,8u,3u,3u,0u,0u,false)]
    [InlineData(6u,6u,7u,7u,3u,4u,0u,0u,false)]
    [InlineData(6u,6u,7u,7u,3u,3u,0u,1u,false)]
    [InlineData(6u,8u,7u,7u,3u,3u,0u,0u,false)]
    [InlineData(7u,7u,7u,7u,3u,3u,0u,0u,false)]
    [InlineData(0u,0u,7u,7u,3u,3u,0u,0u,false)]
    public void NativeQueuedEffectRequiresExactSessionWorldAndCompletePublication(uint seq,uint after,uint session,uint active,
        uint epoch,uint actual,uint load,uint actualLoad,bool expected)
        => Assert.Equal(expected,EnemyEffectNamespaceRule.Matches(seq,after,session,active,epoch,actual,load,actualLoad));
    [Fact] public void DelayedAckFromOldWorldCannotConsumeFreshSameSequence()
    {
        var sender=new EnemyEffectSender();
        sender.EnqueueLocal(2,"area",30,PeerEffectKind.Stun,1000,0,0,45,100,7,1,4);
        sender.Acknowledge(new EnemyEffectAckPacket(1,2,45,100,7,0,3));
        Assert.Single(sender.CollectDue(0));
        sender.Acknowledge(new EnemyEffectAckPacket(1,2,45,100,7,1,4));
        Assert.Empty(sender.CollectDue(1000));
    }
    [Fact] public void ProvenanceTailPreservesNewGameWitnessAndUsesMatchingAbi96Signature()
    {
        string root=AppContext.BaseDirectory;
        while(!File.Exists(Path.Combine(root,"KotorCoop.slnx"))) root=Directory.GetParent(root)!.FullName;
        string native=File.ReadAllText(Path.Combine(root,"src/KotorCoop.Plugin/SharedBlock.EnemyEffect.cs"));
        string managed=File.ReadAllText(Path.Combine(root,"src/KotorCoop.Memory/KotorGameBridge.PluginBlock.cs"));
        string[] names=["OutEpoch","OutLoad","InEpoch","InLoad","InSession","ActiveSession","InSequence","OutHostSession","OutHostLoad","OutHostEpoch","SourceSequence","SourceHostSession","SourceHostLoad","SourceHostEpoch","SourceLocalEpoch","SourceLocalLoad"];
        HashSet<int> fields=[];
        foreach(string name in names)
        {
            Match n=Regex.Match(native,$@"OffsetEnemyEffect{name}\s*=\s*0x([0-9A-F]+)");
            Match m=Regex.Match(managed,$@"BlockEnemyEffect{name}\s*=\s*0x([0-9A-F]+)");
            Assert.True(n.Success && m.Success);Assert.Equal(n.Groups[1].Value,m.Groups[1].Value);
            int offset=Convert.ToInt32(n.Groups[1].Value,16);
            Assert.InRange(offset,0xC420,0xC45C);Assert.True(fields.Add(offset));Assert.Equal(0,offset%4);
        }
        Assert.True(0xC460<=SharedBlock.TotalSize);
        Assert.Contains("30 31 66 00 00 00",KotorGameBridge.PluginSignature); // v102: joiner friendly creature hashes at 0xCCA0 (v101: duel panel flags at 0xCC8C (v100: duel levels at 0xCC80)
    }
}
