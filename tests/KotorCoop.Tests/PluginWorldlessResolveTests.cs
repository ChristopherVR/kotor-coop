using System.Reflection;
using System.Runtime.InteropServices;
using KotorCoop.Memory;
using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed unsafe class PluginWorldlessResolveTests : IDisposable
{
    private readonly nint _block = Marshal.AllocHGlobal(SharedBlock.TotalSize);
    private readonly ProcessMemory _memory = ProcessMemory.Attach(Environment.ProcessId);
    private readonly KotorGameBridge _bridge;

    public PluginWorldlessResolveTests()
    {
        new Span<byte>((void*)_block, SharedBlock.TotalSize).Clear();
        byte[] signature = Convert.FromHexString(KotorGameBridge.PluginSignature.Replace(" ", ""));
        Marshal.Copy(signature, 0, _block, signature.Length);
        _bridge = new KotorGameBridge(_memory, ownsMemory: false);
        typeof(KotorGameBridge).GetField("_pluginBlock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_bridge, _block);
    }

    [Fact]
    public void RepeatedResolveAtMenuKeepsAttachmentWithoutScanningOrRebaselining()
    {
        List<string> diagnostics = [];
        _bridge.ResolveDiagnostic = diagnostics.Add;
        Marshal.WriteInt32(_block + 0x4C, 99); // stationary while preview rendering is parked
        Marshal.WriteInt32(_block + 0x9A4, 8);
        for (int i = 0; i < 20; i++)
        {
            Assert.False(_bridge.TryResolve(out string why));
            Assert.Contains("no current player/world", why);
            Assert.False(_bridge.TryReadLocalState(out _));
        }
        Assert.Empty(diagnostics);
        Assert.Equal(_block, _bridge.PluginBlockAddress);
        Assert.Equal(8, Marshal.ReadInt32(_block + 0x9A4));
        Assert.Null(typeof(KotorGameBridge).GetField("_creatureCache", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_bridge));
    }

    [Fact]
    public void APlayerPublishedAfterChargenImmediatelyLeavesWorldlessFastPath()
    {
        Assert.True(_bridge.TryReadPluginWorldAbsent());
        Marshal.WriteInt32(_block + 0x2260, 0x1234);
        Assert.False(_bridge.TryReadPluginWorldAbsent());
        Marshal.WriteInt32(_block + 0x2260, 0);
        Assert.True(_bridge.TryReadPluginWorldAbsent());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(12)]
    public void InvalidSignatureNeverAuthorizesWorldlessSuppression(int offset)
    {
        Marshal.WriteInt32(_block + offset, 0);
        Assert.False(_bridge.CachedPluginSignatureValid());
        Assert.False(_bridge.TryReadPluginWorldAbsent());
    }

    [Fact]
    public void ExternalModeCannotUseNativeWorldlessSuppression()
    {
        typeof(KotorGameBridge).GetField("_pluginBlock", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_bridge, (nint)0);
        Assert.False(_bridge.CachedPluginSignatureValid());
        Assert.False(_bridge.TryReadPluginWorldAbsent());
    }

    [Theory]
    [InlineData(0u, 0u, 0u, 0u, false, true)]
    [InlineData(0u, 0u, 4u, 4u, false, true)]
    [InlineData(0u, 0u, 4u, 4u, true, false)]
    [InlineData(0u, 0u, 4u, 5u, false, false)]
    [InlineData(0u, 12u, 4u, 4u, false, false)]
    [InlineData(12u, 0u, 4u, 4u, false, false)]
    public void RunningModuleOrTornWorldSampleCannotSuppressResolution(uint player, uint afterPlayer,
        uint epoch, uint afterEpoch, bool moduleReady, bool expected)
        => Assert.Equal(expected, KotorGameBridge.PluginWorldAbsentSample(player, afterPlayer, epoch, afterEpoch, moduleReady));

    public void Dispose()
    {
        _bridge.Dispose(); _memory.Dispose(); Marshal.FreeHGlobal(_block);
    }
}
