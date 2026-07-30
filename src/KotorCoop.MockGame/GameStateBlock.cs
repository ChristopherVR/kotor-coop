using System.Runtime.InteropServices;
using System.Text;
using KotorCoop.Protocol;

namespace KotorCoop.MockGame;

public readonly record struct RemoteSlotView(bool Active, uint PeerId, Vector3f Position, float Facing, int Health, uint Animation, string Name);

/// <summary>
/// A stand-in for the game's player structure, allocated in unmanaged memory so it never
/// moves and lives in a private committed region exactly like a real game's heap object.
///
/// This is not a simulation of memory access: the agent genuinely attaches to this
/// process with OpenProcess and finds this block by scanning for its magic marker, so the
/// scanner, the pointer arithmetic and the read/write paths are all the real ones.
/// </summary>
public sealed unsafe class GameStateBlock : IDisposable
{
    public const int TotalSize = 0x250;

    public static class Offsets
    {
        public const int Magic = 0x00;          // 8 bytes: "KOCOOP01"
        public const int Version = 0x08;        // uint32
        public const int Guard = 0x0C;          // uint32, GuardValue
        public const int LocalPosition = 0x10;  // 3 x float
        public const int LocalFacing = 0x1C;    // float, radians
        public const int ModuleName = 0x20;     // 32 bytes ascii, null padded
        public const int ModuleNameLength = 32;
        public const int LocalHealth = 0x40;    // int32
        public const int LocalAnimation = 0x44; // uint32
        public const int RemoteCount = 0x48;    // uint32
        public const int RemoteSlots = 0x50;

        public const int SlotStride = 0x40;
        public const int SlotCapacity = 8;

        public const int SlotActive = 0x00;
        public const int SlotPeerId = 0x04;
        public const int SlotPosition = 0x08;
        public const int SlotFacing = 0x14;
        public const int SlotHealth = 0x18;
        public const int SlotAnimation = 0x1C;
        public const int SlotName = 0x20;
        public const int SlotNameLength = 32;
    }

    /// <summary>The marker the agent's pattern scanner searches for.</summary>
    public static ReadOnlySpan<byte> MagicBytes => "KOCOOP01"u8;

    /// <summary>
    /// A second, arbitrary constant written immediately after the version.
    ///
    /// Magic plus version alone was not selective enough: a scan of the mock's own process
    /// matched something else at a lower address, and because the scanner takes the first
    /// hit the agent silently anchored on it and published garbage. Extending the signature
    /// to 16 bytes makes a coincidental match effectively impossible.
    /// </summary>
    public const uint GuardValue = 0xC0FFEE01;

    private byte* _block;

    public GameStateBlock()
    {
        _block = (byte*)NativeMemory.AllocZeroed(TotalSize);
        MagicBytes.CopyTo(Raw[..MagicBytes.Length]);
        Write(Offsets.Version, 1u);
        Write(Offsets.Guard, GuardValue);
    }

    public nint Address => (nint)_block;

    private Span<byte> Raw => new(_block, TotalSize);

    private void Write<T>(int offset, T value) where T : unmanaged =>
        MemoryMarshal.Write(Raw.Slice(offset, sizeof(T)), in value);

    private T Read<T>(int offset) where T : unmanaged =>
        MemoryMarshal.Read<T>(Raw.Slice(offset, sizeof(T)));

    private void WriteAscii(int offset, int length, string value)
    {
        Span<byte> field = Raw.Slice(offset, length);
        field.Clear();

        int count = Math.Min(value.Length, length - 1);
        for (int i = 0; i < count; i++)
        {
            char c = value[i];
            field[i] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
        }
    }

    private string ReadAscii(int offset, int length)
    {
        Span<byte> field = Raw.Slice(offset, length);
        int end = field.IndexOf((byte)0);
        if (end < 0)
        {
            end = length;
        }

        return Encoding.ASCII.GetString(field[..end]);
    }

    public void SetLocal(Vector3f position, float facing, string module, int health, uint animation)
    {
        Write(Offsets.LocalPosition, position.X);
        Write(Offsets.LocalPosition + 4, position.Y);
        Write(Offsets.LocalPosition + 8, position.Z);
        Write(Offsets.LocalFacing, facing);
        WriteAscii(Offsets.ModuleName, Offsets.ModuleNameLength, module);
        Write(Offsets.LocalHealth, health);
        Write(Offsets.LocalAnimation, animation);
    }

    /// <summary>Number of remote slots the agent has filled in. Written by the agent, read here.</summary>
    public int RemoteCount => (int)Math.Min(Read<uint>(Offsets.RemoteCount), Offsets.SlotCapacity);

    public RemoteSlotView GetRemote(int index)
    {
        int slot = Offsets.RemoteSlots + (index * Offsets.SlotStride);

        return new RemoteSlotView(
            Read<uint>(slot + Offsets.SlotActive) != 0,
            Read<uint>(slot + Offsets.SlotPeerId),
            new Vector3f(
                Read<float>(slot + Offsets.SlotPosition),
                Read<float>(slot + Offsets.SlotPosition + 4),
                Read<float>(slot + Offsets.SlotPosition + 8)),
            Read<float>(slot + Offsets.SlotFacing),
            Read<int>(slot + Offsets.SlotHealth),
            Read<uint>(slot + Offsets.SlotAnimation),
            ReadAscii(slot + Offsets.SlotName, Offsets.SlotNameLength));
    }

    public IEnumerable<RemoteSlotView> ActiveRemotes()
    {
        for (int i = 0; i < Offsets.SlotCapacity; i++)
        {
            RemoteSlotView slot = GetRemote(i);
            if (slot.Active)
            {
                yield return slot;
            }
        }
    }

    public void Dispose()
    {
        if (_block is not null)
        {
            // Wipe the magic before freeing. Freed heap memory keeps its old contents, so
            // a later scan would otherwise find this dead block, anchor on it, and read
            // and write into memory nothing owns any more. The same hazard exists in a
            // real game whenever a signature survives in a stale allocation.
            Raw.Clear();

            NativeMemory.Free(_block);
            _block = null;
        }
    }
}
