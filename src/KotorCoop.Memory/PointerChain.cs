namespace KotorCoop.Memory;

/// <summary>
/// Cheat Engine style pointer path. Given a base address and offsets
/// [a, b, c], the resolved address is:
///
///   t = deref(base + a); t = deref(t + b); result = t + c
///
/// The last offset is a field offset, not a dereference. Every intermediate read uses
/// the target's pointer width, so a 4-byte chain in swkotor.exe resolves correctly from
/// a 64-bit agent.
/// </summary>
public static class PointerChain
{
    public static bool TryResolve(ProcessMemory memory, nint baseAddress, IReadOnlyList<int> offsets, out nint address)
    {
        address = baseAddress;

        if (offsets.Count == 0)
        {
            return true;
        }

        for (int i = 0; i < offsets.Count - 1; i++)
        {
            nint slot = address + offsets[i];

            if (!memory.TryReadPointer(slot, out nint next) || next == 0)
            {
                address = 0;
                return false;
            }

            address = next;
        }

        address += offsets[^1];
        return true;
    }

    public static string Describe(nint baseAddress, IReadOnlyList<int> offsets)
    {
        if (offsets.Count == 0)
        {
            return $"0x{baseAddress:X}";
        }

        string path = string.Join(" -> ", offsets.Select(o => $"0x{o:X}"));
        return $"[0x{baseAddress:X}] {path}";
    }
}
