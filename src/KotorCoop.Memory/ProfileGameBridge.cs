using System.Runtime.InteropServices;
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>
/// A profile-driven bridge. This same class drives the mock harness and the real game:
/// the only difference is the JSON offsets it is handed, which is the point of the
/// exercise. Validate the whole stack against something deterministic, then repoint it.
/// </summary>
public sealed class ProfileGameBridge : IGameBridge
{
    private readonly ProcessMemory _memory;
    private readonly GameProfile _profile;
    private readonly PatternScanner _scanner;
    private readonly bool _ownsMemory;

    private nint _anchor;
    private int _consecutiveFailures;

    public ProfileGameBridge(ProcessMemory memory, GameProfile profile, bool ownsMemory = true)
    {
        _memory = memory;
        _profile = profile;
        _scanner = new PatternScanner(memory);
        _ownsMemory = ownsMemory;
    }

    public string Description =>
        $"{_profile.Name} [pid {_memory.ProcessId} {_memory.ProcessName}, {(_memory.Is32Bit ? "x86" : "x64")}] anchor=0x{_anchor:X}";

    public bool IsAttached => _memory.IsOpen && _anchor != 0;

    public bool CanWriteRemotes => _profile.Remotes is not null;

    public nint AnchorAddress => _anchor;

    /// <summary>
    /// Resolves the anchor. Called once at startup and again whenever reads start failing,
    /// since a game that reloads a save can move everything the chain points at.
    /// </summary>
    public bool TryResolveAnchor(out string diagnostic)
    {
        nint root;

        switch (_profile.Anchor.Mode)
        {
            case AnchorMode.Absolute:
                root = (nint)_profile.Anchor.AbsoluteAddress;
                break;

            case AnchorMode.ModuleOffset:
            {
                string moduleName = _profile.Anchor.Module ?? $"{_memory.ProcessName}.exe";
                ModuleRegion? module = _memory.FindModule(moduleName);
                if (module is null)
                {
                    diagnostic = $"Module '{moduleName}' is not loaded in pid {_memory.ProcessId}.";
                    _anchor = 0;
                    return false;
                }

                root = module.Value.BaseAddress + (nint)_profile.Anchor.ModuleOffset;
                break;
            }

            case AnchorMode.PatternScan:
            default:
            {
                BytePattern pattern = BytePattern.Parse(_profile.Anchor.Pattern!);
                nint? hit = _profile.Anchor.Module is { } scanModule
                    ? _scanner.ScanModule(scanModule, pattern, limit: 1).Cast<nint?>().FirstOrDefault()
                    : _scanner.ScanFirst(pattern, _profile.Anchor.Scope);

                if (hit is null)
                {
                    diagnostic = $"Pattern '{pattern}' not found in scope {_profile.Anchor.Scope}.";
                    _anchor = 0;
                    return false;
                }

                root = hit.Value;
                break;
            }
        }

        root += _profile.Anchor.Displacement;

        if (_profile.Anchor.DereferenceAnchor)
        {
            if (!_memory.TryReadPointer(root, out nint dereferenced) || dereferenced == 0)
            {
                diagnostic = $"Anchor at 0x{root:X} did not hold a usable pointer.";
                _anchor = 0;
                return false;
            }

            root = dereferenced;
        }

        if (!PointerChain.TryResolve(_memory, root, _profile.Anchor.PointerChain, out nint resolved))
        {
            diagnostic = $"Pointer chain {PointerChain.Describe(root, _profile.Anchor.PointerChain)} broke.";
            _anchor = 0;
            return false;
        }

        _anchor = resolved;
        _consecutiveFailures = 0;
        diagnostic = $"Anchor resolved to 0x{_anchor:X}.";
        return true;
    }

    public bool TryReadLocalState(out PlayerState state)
    {
        state = PlayerState.Empty;

        if (_anchor == 0 && !TryResolveAnchor(out _))
        {
            return false;
        }

        StateLayout layout = _profile.Layout;

        // Position is the only mandatory field; everything else degrades to a default.
        if (layout.Position is not { } positionOffset)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[12];
        if (!_memory.TryRead(_anchor + positionOffset, buffer))
        {
            OnReadFailure();
            return false;
        }

        Vector3f position = new(
            MemoryMarshal.Read<float>(buffer[..4]),
            MemoryMarshal.Read<float>(buffer.Slice(4, 4)),
            MemoryMarshal.Read<float>(buffer.Slice(8, 4)));

        float facing = 0f;
        if (layout.Facing is { } facingOffset)
        {
            _memory.TryReadSingle(_anchor + facingOffset, out facing);
        }

        string module = string.Empty;
        if (layout.ModuleName is { } moduleOffset)
        {
            _memory.TryReadAsciiString(_anchor + moduleOffset, layout.ModuleNameLength, out module);
        }

        int health = 0;
        if (layout.Health is { } healthOffset)
        {
            _memory.TryReadInt32(_anchor + healthOffset, out health);
        }

        uint animation = 0;
        if (layout.Animation is { } animationOffset)
        {
            _memory.TryReadUInt32(_anchor + animationOffset, out animation);
        }

        state = new PlayerState(position, facing, module, health, animation);

        if (!state.IsPlausible)
        {
            // NaN or infinity means the offsets are wrong, not that the player teleported.
            OnReadFailure();
            state = PlayerState.Empty;
            return false;
        }

        _consecutiveFailures = 0;
        return true;
    }

    public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers)
    {
        if (_profile.Remotes is not { } remotes || _anchor == 0)
        {
            return false;
        }

        int count = Math.Min(peers.Count, remotes.Capacity);
        bool allOk = true;

        for (int slot = 0; slot < remotes.Capacity; slot++)
        {
            nint slotAddress = _anchor + remotes.Slots + (slot * remotes.Stride);

            if (slot >= count)
            {
                allOk &= _memory.TryWrite(slotAddress + remotes.Active, 0u);
                continue;
            }

            RemotePeer peer = peers[slot];
            allOk &= _memory.TryWrite(slotAddress + remotes.PeerId, peer.PeerId);
            allOk &= _memory.TryWrite(slotAddress + remotes.Position, peer.State.Position.X);
            allOk &= _memory.TryWrite(slotAddress + remotes.Position + 4, peer.State.Position.Y);
            allOk &= _memory.TryWrite(slotAddress + remotes.Position + 8, peer.State.Position.Z);
            allOk &= _memory.TryWrite(slotAddress + remotes.Facing, peer.State.Facing);
            allOk &= _memory.TryWrite(slotAddress + remotes.Health, peer.State.Health);
            allOk &= _memory.TryWrite(slotAddress + remotes.Animation, peer.State.Animation);
            allOk &= _memory.TryWriteAsciiString(slotAddress + remotes.Name, peer.Name, remotes.NameLength);

            // Active last, so the game never observes a half-written slot.
            allOk &= _memory.TryWrite(slotAddress + remotes.Active, 1u);
        }

        allOk &= _memory.TryWrite(_anchor + remotes.Count, (uint)count);
        return allOk;
    }

    private void OnReadFailure()
    {
        _consecutiveFailures++;

        // Tolerate transient failures, then assume the world moved and re-anchor.
        if (_consecutiveFailures >= 10)
        {
            _anchor = 0;
            _consecutiveFailures = 0;
        }
    }

    public void Dispose()
    {
        if (_ownsMemory)
        {
            _memory.Dispose();
        }
    }
}
