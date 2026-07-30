namespace KotorCoop.Memory;

public readonly record struct MemoryRegion(nint BaseAddress, long Size, uint Protect, uint State, uint Type)
{
    public nint EndAddress => BaseAddress + (nint)Size;

    public bool IsCommitted => State == MemoryConstants.MemCommit;

    public bool IsPrivate => Type == MemoryConstants.MemPrivate;

    public bool IsImage => Type == MemoryConstants.MemImage;

    public bool IsReadable
    {
        get
        {
            if (!IsCommitted)
            {
                return false;
            }

            // A guard page faults on touch; reading it would disturb the target.
            if ((Protect & MemoryConstants.PageGuard) != 0)
            {
                return false;
            }

            return (Protect & (MemoryConstants.PageReadOnly
                | MemoryConstants.PageReadWrite
                | MemoryConstants.PageWriteCopy
                | MemoryConstants.PageExecuteRead
                | MemoryConstants.PageExecuteReadWrite
                | MemoryConstants.PageExecuteWriteCopy)) != 0;
        }
    }

    public bool IsWritable => IsReadable && (Protect & (MemoryConstants.PageReadWrite
        | MemoryConstants.PageWriteCopy
        | MemoryConstants.PageExecuteReadWrite
        | MemoryConstants.PageExecuteWriteCopy)) != 0;

    public override string ToString() =>
        $"0x{BaseAddress:X} .. 0x{EndAddress:X} ({Size:N0} bytes) protect=0x{Protect:X} type=0x{Type:X}";
}

public readonly record struct ModuleRegion(string Name, string FullPath, nint BaseAddress, long Size)
{
    public nint EndAddress => BaseAddress + (nint)Size;

    public override string ToString() => $"{Name} @ 0x{BaseAddress:X} (0x{Size:X} bytes)";
}

/// <summary>Which parts of the target to sweep when searching for a pattern or value.</summary>
public enum ScanScope
{
    /// <summary>Heap and other private committed allocations. Where game object state lives.</summary>
    PrivateCommitted,

    /// <summary>Loaded image sections only (code and static data).</summary>
    Images,

    /// <summary>Everything readable. Slowest.</summary>
    AllReadable,
}
