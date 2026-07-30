using KotorCoop.Memory;
using KotorCoop.MockGame;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Exercises the real memory path end to end by attaching to the test process itself.
/// These are not mocks: OpenProcess, VirtualQueryEx, the pattern scanner and
/// ReadProcessMemory/WriteProcessMemory all run for real against a live address space.
/// </summary>
public class MemoryBridgeTests
{
    /// <summary>
    /// Anchors directly on the known block address rather than by signature scan.
    ///
    /// Sweeping the whole address space is covered by <see cref="ExternalTargetTests"/>,
    /// against a separate process. It must not be done against the test host itself:
    /// repeatedly reading our own GC heap through ReadProcessMemory reliably kills the
    /// runtime with an execution engine error (0x80131506, faulting inside coreclr).
    /// A separate target process, which is how the tool actually runs, is unaffected.
    /// </summary>
    private static GameProfile MockProfile(nint anchor) => new()
    {
        Name = "test",
        ProcessNames = ["testhost"],
        Anchor = new AnchorSpec
        {
            Mode = AnchorMode.Absolute,
            AbsoluteAddress = anchor,
        },
        Layout = new StateLayout
        {
            Position = GameStateBlock.Offsets.LocalPosition,
            Facing = GameStateBlock.Offsets.LocalFacing,
            ModuleName = GameStateBlock.Offsets.ModuleName,
            ModuleNameLength = GameStateBlock.Offsets.ModuleNameLength,
            Health = GameStateBlock.Offsets.LocalHealth,
            Animation = GameStateBlock.Offsets.LocalAnimation,
        },
        Remotes = new RemoteLayout
        {
            Count = GameStateBlock.Offsets.RemoteCount,
            Slots = GameStateBlock.Offsets.RemoteSlots,
            Stride = GameStateBlock.Offsets.SlotStride,
            Capacity = GameStateBlock.Offsets.SlotCapacity,
            Active = GameStateBlock.Offsets.SlotActive,
            PeerId = GameStateBlock.Offsets.SlotPeerId,
            Position = GameStateBlock.Offsets.SlotPosition,
            Facing = GameStateBlock.Offsets.SlotFacing,
            Health = GameStateBlock.Offsets.SlotHealth,
            Animation = GameStateBlock.Offsets.SlotAnimation,
            Name = GameStateBlock.Offsets.SlotName,
            NameLength = GameStateBlock.Offsets.SlotNameLength,
        },
    };

    [Fact]
    public void AttachesToSelfAndReportsBitness()
    {
        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);

        Assert.True(memory.IsOpen);
        Assert.True(memory.IsAlive);
        Assert.Equal(Environment.ProcessId, memory.ProcessId);
        Assert.Equal(memory.Is32Bit ? 4 : 8, memory.PointerSize);
    }

    [Fact]
    public void EnumeratesPrivateCommittedRegions()
    {
        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);

        List<MemoryRegion> regions = [.. memory.EnumerateRegions(ScanScope.PrivateCommitted).Take(50)];

        Assert.NotEmpty(regions);
        Assert.All(regions, r => Assert.True(r.IsReadable));
        Assert.All(regions, r => Assert.True(r.IsPrivate));
    }

    [Fact]
    public void BridgeReadsWhatTheBlockHolds()
    {
        using GameStateBlock block = new();
        block.SetLocal(new Vector3f(12.5f, -7.25f, 3f), 1.5f, "end_m01aa", 73, 9);

        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);
        using ProfileGameBridge bridge = new(memory, MockProfile(block.Address), ownsMemory: false);

        Assert.True(bridge.TryResolveAnchor(out string diagnostic), diagnostic);
        Assert.Equal(block.Address, bridge.AnchorAddress);

        Assert.True(bridge.TryReadLocalState(out PlayerState state));
        Assert.Equal(new Vector3f(12.5f, -7.25f, 3f), state.Position);
        Assert.Equal(1.5f, state.Facing);
        Assert.Equal("end_m01aa", state.Module);
        Assert.Equal(73, state.Health);
        Assert.Equal(9u, state.Animation);
    }

    [Fact]
    public void BridgeTracksSubsequentChanges()
    {
        using GameStateBlock block = new();
        block.SetLocal(new Vector3f(0f, 0f, 0f), 0f, "end_m01aa", 100, 0);

        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);
        using ProfileGameBridge bridge = new(memory, MockProfile(block.Address), ownsMemory: false);
        Assert.True(bridge.TryResolveAnchor(out _));

        block.SetLocal(new Vector3f(42f, 43f, 44f), 2f, "tar_m02aa", 55, 3);

        Assert.True(bridge.TryReadLocalState(out PlayerState state));
        Assert.Equal(new Vector3f(42f, 43f, 44f), state.Position);
        Assert.Equal("tar_m02aa", state.Module);
    }

    [Fact]
    public void BridgeWritesRemotePeersBackIntoTheBlock()
    {
        using GameStateBlock block = new();
        block.SetLocal(Vector3f.Zero, 0f, "end_m01aa", 100, 0);

        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);
        using ProfileGameBridge bridge = new(memory, MockProfile(block.Address), ownsMemory: false);
        Assert.True(bridge.TryResolveAnchor(out _));
        Assert.True(bridge.CanWriteRemotes);

        List<RemotePeer> peers =
        [
            new(2, "Malak", new PlayerState(new Vector3f(1f, 2f, 3f), 0.5f, "end_m01aa", 80, 2)),
            new(3, "Bastila", new PlayerState(new Vector3f(4f, 5f, 6f), 1.5f, "end_m01aa", 90, 1)),
        ];

        Assert.True(bridge.TryWriteRemotePeers(peers));
        Assert.Equal(2, block.RemoteCount);

        List<RemoteSlotView> written = [.. block.ActiveRemotes()];
        Assert.Equal(2, written.Count);

        Assert.Equal(2u, written[0].PeerId);
        Assert.Equal("Malak", written[0].Name);
        Assert.Equal(new Vector3f(1f, 2f, 3f), written[0].Position);
        Assert.Equal(80, written[0].Health);

        Assert.Equal("Bastila", written[1].Name);
        Assert.Equal(new Vector3f(4f, 5f, 6f), written[1].Position);
    }

    [Fact]
    public void DepartedPeersAreDeactivated()
    {
        using GameStateBlock block = new();
        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);
        using ProfileGameBridge bridge = new(memory, MockProfile(block.Address), ownsMemory: false);
        Assert.True(bridge.TryResolveAnchor(out _));

        bridge.TryWriteRemotePeers(
        [
            new(2, "Malak", new PlayerState(new Vector3f(1f, 2f, 3f), 0f, "end_m01aa", 80, 0)),
            new(3, "Bastila", new PlayerState(new Vector3f(4f, 5f, 6f), 0f, "end_m01aa", 90, 0)),
        ]);
        Assert.Equal(2, block.ActiveRemotes().Count());

        // One peer leaves: the stale slot must be cleared, not left rendering a ghost.
        bridge.TryWriteRemotePeers(
        [
            new(2, "Malak", new PlayerState(new Vector3f(1f, 2f, 3f), 0f, "end_m01aa", 80, 0)),
        ]);

        Assert.Equal(1, block.RemoteCount);
        Assert.Single(block.ActiveRemotes());
    }

    [Fact]
    public void ReadOnlyProfileReportsItCannotWrite()
    {
        using GameStateBlock block = new();
        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);

        GameProfile profile = MockProfile(block.Address);
        profile.Remotes = null;

        using ProfileGameBridge bridge = new(memory, profile, ownsMemory: false);
        Assert.True(bridge.TryResolveAnchor(out _));

        Assert.False(bridge.CanWriteRemotes);
        Assert.False(bridge.TryWriteRemotePeers([]));
    }

    [Fact]
    public void PointerChainResolvesThroughAnIndirection()
    {
        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);

        // Two-level chain: slot -> struct -> field, the shape a real game profile uses.
        nint target = 0;
        unsafe
        {
            nint* slot = stackalloc nint[1];
            byte* payload = stackalloc byte[64];
            slot[0] = (nint)payload;

            Assert.True(PointerChain.TryResolve(memory, (nint)slot, [0, 16], out target));
            Assert.Equal((nint)payload + 16, target);
        }

        Assert.NotEqual(0, target);
    }

    [Fact]
    public void BrokenPointerChainFailsCleanly()
    {
        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);

        Assert.False(PointerChain.TryResolve(memory, 0x10, [0, 0], out nint address));
        Assert.Equal(0, address);
    }

    [Fact]
    public void ProfileWithoutPositionIsRejectedAtLoad()
    {
        string path = Path.Combine(Path.GetTempPath(), $"kotorcoop-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"name":"broken","anchor":{"mode":"Absolute","absoluteAddress":1024},"layout":{}}""");

        try
        {
            // A profile that reads nothing must fail loudly rather than produce zeroes.
            InvalidDataException ex = Assert.Throws<InvalidDataException>(() => GameProfile.Load(path));
            Assert.Contains("position", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ShippedMockProfileLoadsAndMatchesTheBlockLayout()
    {
        GameProfile profile = GameProfile.Load(ProfileLocator.Resolve("mockgame"));

        // Guards against profiles/mockgame.json drifting away from GameStateBlock.
        Assert.Equal(GameStateBlock.Offsets.LocalPosition, profile.Layout.Position);
        Assert.Equal(GameStateBlock.Offsets.LocalFacing, profile.Layout.Facing);
        Assert.Equal(GameStateBlock.Offsets.ModuleName, profile.Layout.ModuleName);
        Assert.Equal(GameStateBlock.Offsets.LocalHealth, profile.Layout.Health);
        Assert.Equal(GameStateBlock.Offsets.LocalAnimation, profile.Layout.Animation);

        Assert.NotNull(profile.Remotes);
        Assert.Equal(GameStateBlock.Offsets.RemoteCount, profile.Remotes.Count);
        Assert.Equal(GameStateBlock.Offsets.RemoteSlots, profile.Remotes.Slots);
        Assert.Equal(GameStateBlock.Offsets.SlotStride, profile.Remotes.Stride);
        Assert.Equal(GameStateBlock.Offsets.SlotCapacity, profile.Remotes.Capacity);
    }
}
