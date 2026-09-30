using System.Reflection;
using System.Runtime.InteropServices;
using KotorCoop.Memory;
using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PlaceableLocalChangeTests
{
    [Theory]
    [InlineData(false, 0, true, 0, false)] // physically closed but not published yet
    [InlineData(true, 0, false, 0, false)] // stale canonical open still queued
    [InlineData(true, 1, true, 0, false)] // stale mirror has physically reopened it
    [InlineData(true, 0, true, 0, true)] // local close now canonical and physically applied
    [InlineData(true, 1, true, 1, true)] // genuinely later reopen also completes
    public void LocalEdgeProtectionRequiresBothNativeAndCanonicalCompletion(
        bool published, byte actual, bool canonicalMatches, byte local, bool expected)
    {
        Assert.Equal(expected, PlaceableLocalChangeRule.MayRelease(published, actual, canonicalMatches, local));
    }
    [Fact]
    public void RapidOpenCloseRetainsCloseUntilNativeCloseActuallyCompletes()
    {
        PlaceableLocalChangeTracker changes = new();
        changes.Record(0xC6, true);
        changes.Record(0xC6, false);
        Assert.Equal(0xC6u, changes.PendingId(0));
        Assert.False(changes.Confirm(0, true));
        Assert.True(changes.Confirm(0, false));
        Assert.Equal(0u, changes.PendingId(0));
    }

    [Fact]
    public void GenuineReopenSupersedesPendingClose()
    {
        PlaceableLocalChangeTracker changes = new();
        changes.Record(0xC6, false);
        changes.Record(0xC6, true);
        Assert.False(changes.Confirm(0, false));
        Assert.True(changes.Confirm(0, true));
    }

    [Fact]
    public void LocalCloseSurvivesStaleRemoteOpenAcknowledgementAndRepeatedReceipt()
    {
        const BindingFlags hidden = BindingFlags.Instance | BindingFlags.NonPublic;
        nint block = Marshal.AllocHGlobal(0xBC00);
        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);
        using KotorGameBridge bridge = new(memory, ownsMemory: false);
        try
        {
            Marshal.Copy(new byte[0xBC00], 0, block, 0xBC00);
            typeof(KotorGameBridge).GetField("_pluginBlock", hidden)!.SetValue(bridge, block);
            var pending = (Dictionary<uint, (byte Value, bool Published)>)typeof(KotorGameBridge)
                .GetField("_placeablePendingLocalChanges", hidden)!.GetValue(bridge)!;
            void Consume() => typeof(KotorGameBridge).GetMethod("ConsumeLocalPlaceableChanges", hidden)!.Invoke(bridge, null);
            Marshal.WriteInt32(block + 0xBA00, 0xC6);
            Marshal.WriteInt32(block + 0xBA04, 2); // genuine close, unlocked
            Marshal.WriteInt32(block + 0xBA08, 1);
            Consume();
            Assert.Equal(((byte)2, false), pending[0xC6]);
            pending[0xC6] = (2, true);
            Marshal.WriteInt32(block + 0xB800, 0xC6);
            Marshal.WriteInt32(block + 0xB804, 3); // stale remote open applied afterwards
            Marshal.WriteInt32(block + 0xB808, 2);
            typeof(KotorGameBridge).GetMethod("ConsumePlaceableApplyAcknowledgements", hidden)!.Invoke(bridge, null);
            Consume();
            Assert.Equal(((byte)2, true), pending[0xC6]);
            Marshal.WriteInt32(block + 0xBA04, 3);
            Marshal.WriteInt32(block + 0xBA08, 2); // a genuinely later local reopen
            Consume();
            Assert.Equal(((byte)3, false), pending[0xC6]);
        }
        finally { Marshal.FreeHGlobal(block); }
    }
}
