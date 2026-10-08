using System.Reflection;
using System.Runtime.InteropServices;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The joiner's agent rewrote the host party-roster bank every pump, ~50 cross-process writes
/// under an odd sequence, and the plugin's companion mirror kept reading it mid-write
/// (live 2026-10-09: "mirroring 0/1" three times a second, "host bank mid-write").
/// Unchanged content is now left alone.
/// </summary>
public sealed class HostPartyRosterRewriteTests : IDisposable
{
    private const int BlockSize = 0xC600;
    private const int HostOccupantsSequence = 0xBD00;
    private readonly nint _block = Marshal.AllocHGlobal(BlockSize);
    private readonly ProcessMemory _memory = ProcessMemory.Attach(Environment.ProcessId);
    private readonly KotorGameBridge _bridge;

    public HostPartyRosterRewriteTests()
    {
        Marshal.Copy(new byte[BlockSize], 0, _block, BlockSize);
        _bridge = new KotorGameBridge(_memory, ownsMemory: false);
        typeof(KotorGameBridge).GetField("_pluginBlock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(_bridge, _block);
    }

    private uint Sequence => (uint)Marshal.ReadInt32(_block + HostOccupantsSequence);

    [Fact]
    public void AnUnchangedRosterIsNotRewritten()
    {
        PartyRoster roster = new(1, 1, HostNativeEpoch: 1);
        Assert.True(_bridge.WriteHostPartyRoster(true, roster));
        uint first = Sequence;
        Assert.Equal(0u, first & 1);

        Assert.True(_bridge.WriteHostPartyRoster(true, roster));
        Assert.Equal(first, Sequence);
    }

    [Fact]
    public void AChangedRosterIsWritten()
    {
        Assert.True(_bridge.WriteHostPartyRoster(true, new PartyRoster(1, 1, HostNativeEpoch: 1)));
        uint first = Sequence;
        Assert.True(_bridge.WriteHostPartyRoster(true, new PartyRoster(3, 3, HostNativeEpoch: 1)));
        Assert.NotEqual(first, Sequence);
        Assert.Equal(0u, Sequence & 1);
    }

    [Fact]
    public void ABankSomeoneElseTouchedIsWrittenAgain()
    {
        PartyRoster roster = new(1, 1, HostNativeEpoch: 1);
        Assert.True(_bridge.WriteHostPartyRoster(true, roster));
        Marshal.WriteInt32(_block + HostOccupantsSequence, 0); // a reset the writer did not make
        Assert.True(_bridge.WriteHostPartyRoster(true, roster));
        Assert.NotEqual(0u, Sequence);
    }

    public void Dispose()
    {
        _bridge.Dispose();
        Marshal.FreeHGlobal(_block);
    }
}
