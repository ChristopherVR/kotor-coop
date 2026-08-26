// Small, generic memory-read helpers shared by several concern files: raw vector reads and
// decoding a CExoString (a char* + length, not a null-terminated buffer).
using System.Runtime.InteropServices;
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ---- helpers --------------------------------------------------------------

    private bool TryReadVector(nint address, out Vector3f value)
    {
        value = Vector3f.Zero;
        Span<byte> buffer = stackalloc byte[12];

        if (!_memory.TryRead(address, buffer))
        {
            return false;
        }

        value = new Vector3f(
            MemoryMarshal.Read<float>(buffer[..4]),
            MemoryMarshal.Read<float>(buffer.Slice(4, 4)),
            MemoryMarshal.Read<float>(buffer.Slice(8, 4)));
        return true;
    }
    /// <summary>Reads a CExoString: a char* and a length, not a null-terminated buffer.</summary>
    private string ReadExoString(nint address)
    {
        if (!_memory.TryReadPointer(address, out nint text) || text == 0)
        {
            return string.Empty;
        }

        if (!_memory.TryReadUInt32(address + 4, out uint length) || length == 0 || length > 64)
        {
            return string.Empty;
        }

        byte[] buffer = new byte[length];
        if (!_memory.TryRead(text, buffer))
        {
            return string.Empty;
        }

        // ⚠ A CExoString's length routinely INCLUDES its terminating null — the engine's
        // own journal entries store 11 for "end_attack". Taking the length at face value
        // put a trailing '\0' inside the string, and because that string is written back
        // into the block and re-read as the next request's tag, every round trip added
        // another byte: measured live as the tag growing 'end_attack\0', 'end_attack\0\0'
        // … and the quest re-applying every few seconds because it never matched.
        // Same trap as the module name in Handoff 0g; cut at the first null.
        int end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, end >= 0 ? end : buffer.Length);
    }
}
