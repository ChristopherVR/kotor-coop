using System.Diagnostics;
using KotorCoop.Memory;
using KotorCoop.MockGame;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The production configuration: a separate target process, swept by signature, read and
/// written from outside. This is what the agent does to swkotor.exe.
///
/// Full address-space sweeps belong here rather than in the self-attached tests. Reading
/// the test host's own GC heap through ReadProcessMemory in a tight loop kills the runtime
/// with an execution engine error; a separate target has no such feedback loop, because
/// the scanner's own buffers are not inside the memory it is scanning.
/// </summary>
public sealed class ExternalTargetTests : IDisposable
{
    private readonly Process? _target;
    private readonly string _skipReason = string.Empty;

    public ExternalTargetTests()
    {
        string exe = Path.Combine(
            Path.GetDirectoryName(typeof(GameStateBlock).Assembly.Location)!,
            "KotorCoop.MockGame.exe");

        if (!File.Exists(exe))
        {
            _skipReason = $"MockGame not built at {exe}";
            return;
        }

        _target = Process.Start(new ProcessStartInfo(exe)
        {
            // Standalone: no co-op session, just a process with a state block in it.
            Arguments = "--name Target --auto --exit-after 60",
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        if (_target is null)
        {
            _skipReason = "could not start MockGame";
            return;
        }

        // The first line reports the pid and block address, and only appears once the
        // block is allocated and marked.
        string? first = _target.StandardOutput.ReadLine();
        if (first is null || !first.Contains("state block at", StringComparison.Ordinal))
        {
            _skipReason = $"unexpected first line from MockGame: '{first}'";
        }
    }

    private ProcessMemory Attach()
    {
        Assert.True(_skipReason.Length == 0, _skipReason);
        Assert.NotNull(_target);
        Assert.False(_target.HasExited, "target process exited early");

        return ProcessMemory.Attach(_target.Id);
    }

    private static GameProfile SignatureProfile() => new()
    {
        Name = "external mock",
        ProcessNames = ["KotorCoop.MockGame"],
        Anchor = new AnchorSpec
        {
            Mode = AnchorMode.PatternScan,
            Pattern = "4B 4F 43 4F 4F 50 30 31 01 00 00 00 01 EE FF C0",
            Scope = ScanScope.PrivateCommitted,
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
    public void AttachesToASeparateProcess()
    {
        using ProcessMemory memory = Attach();

        Assert.True(memory.IsOpen);
        Assert.NotEqual(Environment.ProcessId, memory.ProcessId);
        Assert.Equal("KotorCoop.MockGame", memory.ProcessName);
        Assert.NotEmpty(memory.GetModules());
    }

    [Fact]
    public void FullSweepFindsTheStateBlockBySignature()
    {
        using ProcessMemory memory = Attach();

        PatternScanner scanner = new(memory);
        List<nint> hits = scanner.ScanAll(
            BytePattern.Parse("4B 4F 43 4F 4F 50 30 31 01 00 00 00 01 EE FF C0"),
            ScanScope.PrivateCommitted);

        Assert.NotEmpty(hits);
    }

    [Fact]
    public void ReadsTheTargetsLivePlayerState()
    {
        using ProcessMemory memory = Attach();
        using ProfileGameBridge bridge = new(memory, SignatureProfile(), ownsMemory: false);

        Assert.True(bridge.TryResolveAnchor(out string diagnostic), diagnostic);
        // The mock rewrites the module name in place (clear, then write), so a read that lands
        // between the two sees an empty name. Re-read until the block is settled.
        PlayerState state = default;
        bool read = false;
        for (int i = 0; i < 40 && !(read && state.IsPlausible && state.Module.Length > 0); i++)
        {
            if (i > 0) Thread.Sleep(25);
            read = bridge.TryReadLocalState(out state);
        }
        Assert.True(read, "could not read the target's player state");

        Assert.True(state.IsPlausible);
        Assert.Contains(state.Module, MockWorld.Modules);
        Assert.InRange(state.Position.X, 0f, MockWorld.Size);
        Assert.InRange(state.Position.Y, 0f, MockWorld.Size);
        Assert.Equal(100, state.Health);
    }

    [Fact]
    public async Task ObservesTheTargetMoving()
    {
        using ProcessMemory memory = Attach();
        using ProfileGameBridge bridge = new(memory, SignatureProfile(), ownsMemory: false);
        Assert.True(bridge.TryResolveAnchor(out _));

        Assert.True(bridge.TryReadLocalState(out PlayerState first));

        // The target auto-walks, so its position must change. This is the same thing the
        // track command asks a human to confirm by eye against the real game.
        bool moved = false;
        for (int i = 0; i < 60 && !moved; i++)
        {
            await Task.Delay(50);
            Assert.True(bridge.TryReadLocalState(out PlayerState now));
            moved = now.Position.DistanceTo(first.Position) > 0.5f;
        }

        Assert.True(moved, "the target's position never changed");
    }

    [Fact]
    public void WritesRemotePeersIntoTheTarget()
    {
        using ProcessMemory memory = Attach();
        using ProfileGameBridge bridge = new(memory, SignatureProfile(), ownsMemory: false);
        Assert.True(bridge.TryResolveAnchor(out _));
        Assert.True(bridge.CanWriteRemotes);

        List<RemotePeer> peers =
        [
            new(7, "Revan", new PlayerState(new Vector3f(11f, 22f, 0f), 1.25f, "end_m01aa", 64, 2)),
        ];

        Assert.True(bridge.TryWriteRemotePeers(peers));

        // Read the slot back out of the other process to prove the write landed.
        nint slot = bridge.AnchorAddress + GameStateBlock.Offsets.RemoteSlots;

        Assert.True(memory.TryReadUInt32(slot + GameStateBlock.Offsets.SlotActive, out uint active));
        Assert.Equal(1u, active);

        Assert.True(memory.TryReadUInt32(slot + GameStateBlock.Offsets.SlotPeerId, out uint peerId));
        Assert.Equal(7u, peerId);

        Assert.True(memory.TryReadSingle(slot + GameStateBlock.Offsets.SlotPosition, out float x));
        Assert.Equal(11f, x);

        Assert.True(memory.TryReadAsciiString(
            slot + GameStateBlock.Offsets.SlotName, GameStateBlock.Offsets.SlotNameLength, out string name));
        Assert.Equal("Revan", name);
    }

    /// <summary>
    /// Root cause 1 of docs/research/companion-state-sync.md's 2026-08-28 round, pinned
    /// against a REAL separate process (not self-attach): a KOTOR ResRef is a bare
    /// <c>char[16]</c> with no required terminator, but <c>ProcessMemory.TryWriteAsciiString</c>
    /// used to reserve the field's last byte for one anyway (<c>fieldLength - 1</c>), silently
    /// dropping the LAST character of any value exactly as long as the field —
    /// live-observed for a companion's real starting sidearm, <c>g_w_blstrpstl001</c> (16
    /// characters), which crossed the shared block as <c>g_w_blstrpstl00</c> and was refused as
    /// an unknown blueprint (failure 8).
    ///
    /// Writes into the SAME slot-name scratch area <see cref="WritesRemotePeersIntoTheTarget"/>
    /// already proves is safe to write (32 bytes of real storage, 32 more bytes of stride
    /// headroom after it), but asks for exactly 16 of those 32 bytes — the actual field width
    /// every resref-carrying shared-block region in KotorCoop.Plugin uses
    /// (<c>BlockCompanionEquipResrefLength</c>/<c>BlockEquipResrefLength</c>/
    /// <c>BlockItemResrefLength</c>, all 16).
    /// </summary>
    [Theory]
    [InlineData("g")] // 1 char
    [InlineData("g_w_blstrpstl00")] // 15 chars — fit even under the old, buggy reservation
    [InlineData("g_w_blstrpstl001")] // 16 chars — the exact live-observed truncation case
    public void FixedWidthResrefFieldsRoundTripAtExactly16BytesWithNoReservedTerminator(string resref)
    {
        using ProcessMemory memory = Attach();
        using ProfileGameBridge bridge = new(memory, SignatureProfile(), ownsMemory: false);
        Assert.True(bridge.TryResolveAnchor(out _));

        nint field = bridge.AnchorAddress + GameStateBlock.Offsets.RemoteSlots + GameStateBlock.Offsets.SlotName;

        Assert.True(memory.TryWriteAsciiString(field, resref, fieldLength: 16));
        Assert.True(memory.TryReadAsciiString(field, maxLength: 16, out string readBack));

        Assert.Equal(resref, readBack);
    }

    [Fact]
    public async Task SnapshotDiffNarrowsTowardsTheMovingPosition()
    {
        using ProcessMemory memory = Attach();
        ValueScanner scanner = new(memory);

        // The full-sweep discovery path, run against a real external target.
        MemorySnapshot baseline = scanner.TakeSnapshot();
        Assert.True(baseline.TotalBytes > 0);

        await Task.Delay(400);

        FloatCandidateSet changed = scanner.DiffFloats(
            baseline, keepChanged: true, -5000f, 5000f);

        Assert.NotEmpty(changed.Addresses);

        // The target's coordinates live in the mock world's bounds, so at least one
        // changing value must survive a world-range plus vector3-shape filter.
        FloatCandidateSet shaped = scanner.RefineVector3Head(
            scanner.RefineRange(changed, 0f, MockWorld.Size));

        Assert.NotEmpty(shaped.Addresses);
    }

    public void Dispose()
    {
        if (_target is null)
        {
            return;
        }

        try
        {
            if (!_target.HasExited)
            {
                _target.Kill(entireProcessTree: true);
                _target.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _target.Dispose();
        }
    }
}
