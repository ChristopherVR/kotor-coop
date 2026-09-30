using System.Reflection;
using System.Runtime.InteropServices;
using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

public sealed class DoorApplyAcknowledgementTests : IDisposable
{
    private readonly nint _block = Marshal.AllocHGlobal(0x800);
    private readonly ProcessMemory _memory = ProcessMemory.Attach(Environment.ProcessId);
    private readonly KotorGameBridge _bridge;
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public DoorApplyAcknowledgementTests()
    {
        Marshal.Copy(new byte[0x800], 0, _block, 0x800);
        _bridge = new KotorGameBridge(_memory, ownsMemory: false);
        typeof(KotorGameBridge).GetField("_pluginBlock", Private)!.SetValue(_bridge, _block);
        Marshal.WriteInt32(_block + 0x690, 1);
        Marshal.WriteInt32(_block + 0x698, 0x47);
    }

    private Dictionary<uint, (byte Open, byte Locked)> Shadow =>
        (Dictionary<uint, (byte Open, byte Locked)>)typeof(KotorGameBridge).GetField("_doorShadow", Private)!.GetValue(_bridge)!;

    private void Consume(byte value, byte sequence)
    {
        Marshal.WriteByte(_block + 0x698 + 6, value);
        Marshal.WriteByte(_block + 0x698 + 7, sequence);
        typeof(KotorGameBridge).GetMethod("ConsumeDoorApplyAcknowledgements", Private)!.Invoke(_bridge, null);
    }

    [Fact]
    public void AppliedLockPrimesShadowEvenWhenLocalUnlockHappensBetweenScans()
    {
        // Scanner previously saw unlocked. A native remote lock then applies and the
        // player unlocks it before the next scan: current equals the OLD shadow again.
        Shadow[0x47] = (1, 0);
        Consume(value: 1, sequence: 1); // native open/locked apply acknowledged
        Assert.Equal(((byte)1, (byte)1), Shadow[0x47]);
        // The collector's next current open/unlocked sample now differs and publishes.
        Assert.NotEqual(((byte)1, (byte)0), Shadow[0x47]);
    }

    [Fact]
    public void RepeatedAcknowledgementCannotUndoALaterObservedLocalChange()
    {
        Consume(value: 1, sequence: 1);
        Shadow[0x47] = (1, 0); // collector observed and published the local unlock
        Consume(value: 1, sequence: 1);
        Assert.Equal(((byte)1, (byte)0), Shadow[0x47]);
        Consume(value: 1, sequence: 2); // a genuinely new native re-lock
        Assert.Equal(((byte)1, (byte)1), Shadow[0x47]);
    }

    [Fact]
    public void SameModuleReloadForgetsOldShadowAndAllowsFreshAcknowledgement()
    {
        var ensure = typeof(KotorGameBridge).GetMethod("EnsureDoorWorld", Private)!;
        ensure.Invoke(_bridge, ["END_M01AA", 1u]);
        Consume(value: 1, sequence: 1);
        Shadow[0x47] = (0, 0);
        ensure.Invoke(_bridge, ["END_M01AA", 1u]);
        Assert.Equal(((byte)0, (byte)0), Shadow[0x47]);
        ensure.Invoke(_bridge, ["END_M01AA", 2u]);
        Assert.Empty(Shadow);
        Consume(value: 1, sequence: 1);
        Assert.Equal(((byte)1, (byte)1), Shadow[0x47]);
    }

    [Fact]
    public void InvalidAcknowledgementDoesNotInventAnApply()
    {
        Consume(value: 1, sequence: 0);
        Assert.Empty(Shadow);
        Consume(value: 255, sequence: 1);
        Assert.Empty(Shadow);
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _memory.Dispose();
        Marshal.FreeHGlobal(_block);
    }
}
