using System.Reflection;
using System.Runtime.InteropServices;
using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PlaceableApplyAcknowledgementTests : IDisposable
{
    private readonly nint _block = Marshal.AllocHGlobal(0xBA00);
    private readonly ProcessMemory _memory = ProcessMemory.Attach(Environment.ProcessId);
    private readonly KotorGameBridge _bridge;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    public PlaceableApplyAcknowledgementTests()
    {
        Marshal.Copy(new byte[0xBA00], 0, _block, 0xBA00);
        _bridge = new(_memory, ownsMemory: false);
        typeof(KotorGameBridge).GetField("_pluginBlock", Private)!.SetValue(_bridge, _block);
    }
    private Dictionary<uint, (byte Open, byte Locked, byte Trapped)> Shadow =>
        (Dictionary<uint, (byte Open, byte Locked, byte Trapped)>)typeof(KotorGameBridge)
            .GetField("_placeableShadow", Private)!.GetValue(_bridge)!;
    private void Consume(uint value, uint generation)
    {
        Marshal.WriteInt32(_block + 0xB800, 0xC6);
        Marshal.WriteInt32(_block + 0xB804, (int)value);
        Marshal.WriteInt32(_block + 0xB808, (int)generation);
        typeof(KotorGameBridge).GetMethod("ConsumePlaceableApplyAcknowledgements", Private)!.Invoke(_bridge, null);
    }
    [Fact]
    public void RemoteOpenThenImmediateLocalCloseCannotDisappearBetweenScans()
    {
        Shadow[0xC6] = (0, 0, 1);
        Consume(7, 1); // actual open/unlocked/trapped native apply
        Assert.Equal(((byte)1, (byte)0, (byte)1), Shadow[0xC6]);
        Assert.NotEqual(((byte)0, (byte)0, (byte)1), Shadow[0xC6]);
    }
    [Fact]
    public void RepeatedApplyGenerationCannotOverwritePublishedLocalClose()
    {
        Consume(3, 1);
        Shadow[0xC6] = (0, 0, 0);
        Consume(3, 1);
        Assert.Equal(((byte)0, (byte)0, (byte)0), Shadow[0xC6]);
        Consume(1, 2);
        Assert.Equal(((byte)1, (byte)1, (byte)0), Shadow[0xC6]);
    }
    [Fact]
    public void SameModuleReloadForgetsCloseIntentAndFreshApplyIsNotDeduplicated()
    {
        var ensure = typeof(KotorGameBridge).GetMethod("EnsurePlaceableWorld", Private)!;
        ensure.Invoke(_bridge, ["END_M01AA", 1u]);
        Consume(3, 1);
        var pending = (Dictionary<uint, (byte Value, bool Published)>)typeof(KotorGameBridge)
            .GetField("_placeablePendingLocalChanges", Private)!.GetValue(_bridge)!;
        pending[0xC6] = (0, true);
        ensure.Invoke(_bridge, ["END_M01AA", 1u]);
        Assert.Single(pending);
        ensure.Invoke(_bridge, ["END_M01AA", 2u]);
        Assert.Empty(pending);
        Assert.Empty(Shadow);
        Consume(3, 1);
        Assert.Equal(((byte)1, (byte)0, (byte)0), Shadow[0xC6]);
    }

    [Fact]
    public void InvalidOrIncompleteAcknowledgementCannotInventAppliedState()
    {
        Consume(7, 0);
        Assert.Empty(Shadow);
        Consume(8, 1);
        Assert.Empty(Shadow);
    }

    [Fact]
    public void OlderRotatingRowsCannotOverwriteTheNewestAppliedState()
    {
        // One object moves between slots as the bounded desired list rotates. Older
        // completed ACKs remain in the previous slots, exactly as on the live rig.
        Marshal.WriteInt32(_block + 0xB80C, 0xC6);
        Marshal.WriteInt32(_block + 0xB810, 3); // old open/unlocked tuple
        Marshal.WriteInt32(_block + 0xB814, 2);
        Consume(0, 4); // newest closed/locked tuple, encountered first
        Assert.Equal(((byte)0, (byte)1, (byte)0), Shadow[0xC6]);

        // A genuine local unlock must survive the next poll of those same rows.
        Shadow[0xC6] = (0, 0, 0);
        Consume(0, 4);
        Assert.Equal(((byte)0, (byte)0, (byte)0), Shadow[0xC6]);
    }

    [Fact]
    public void AppliedGenerationWrapAcceptsNewStateAndRejectsOldRows()
    {
        Consume(3, uint.MaxValue);
        Consume(0, 1); // generation zero is reserved
        Assert.Equal(((byte)0, (byte)1, (byte)0), Shadow[0xC6]);
        Consume(3, uint.MaxValue);
        Assert.Equal(((byte)0, (byte)1, (byte)0), Shadow[0xC6]);
    }
    public void Dispose()
    {
        _bridge.Dispose();
        _memory.Dispose();
        Marshal.FreeHGlobal(_block);
    }
}
