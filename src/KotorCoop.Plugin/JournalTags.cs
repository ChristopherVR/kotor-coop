namespace KotorCoop.Plugin;

/// <summary>
/// Tag comparison for the journal apply path's "already at this state?" check. Pure and
/// dependency-free so the rule is unit-tested rather than mirrored (this file is compiled
/// into the test project — see KotorCoop.Tests.csproj).
/// </summary>
internal static class JournalTags
{
    /// <summary>
    /// Case-insensitive ASCII equality between an engine-held tag buffer and a request
    /// tag, both cut at the first null. The cut is load-bearing: CExoString lengths
    /// include the terminator (the length trap — three bugs so far), so a span built from
    /// the engine's own length field carries a trailing null that a byte-wise compare
    /// would otherwise count as a difference.
    /// </summary>
    public static bool EqualsNoCase(ReadOnlySpan<byte> engineTag, ReadOnlySpan<byte> requestTag)
    {
        engineTag = CutAtNull(engineTag);
        requestTag = CutAtNull(requestTag);

        if (engineTag.Length != requestTag.Length)
        {
            return false;
        }

        for (int i = 0; i < engineTag.Length; i++)
        {
            if (ToLowerAscii(engineTag[i]) != ToLowerAscii(requestTag[i]))
            {
                return false;
            }
        }

        return engineTag.Length > 0;
    }

    private static ReadOnlySpan<byte> CutAtNull(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);
        return nul < 0 ? bytes : bytes[..nul];
    }

    private static byte ToLowerAscii(byte b) =>
        b is >= (byte)'A' and <= (byte)'Z' ? (byte)(b + 32) : b;
}
