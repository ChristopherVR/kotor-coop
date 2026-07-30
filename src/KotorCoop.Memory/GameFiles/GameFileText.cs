using System.Text;

namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// Text decoding for the game's on-disk strings. The reference reader decodes
/// windows-1252; .NET without external packages only ships Latin-1, which is identical
/// for every byte the game's resrefs, labels and column names actually use (the two
/// differ only in 0x80-0x9F, typographic punctuation that appears in localised dialog
/// text, not in identifiers). Choosing Latin-1 here keeps the project dependency-free.
/// </summary>
internal static class GameFileText
{
    /// <summary>Decodes a fixed-size buffer up to its first NUL, like the C strings the
    /// formats store: a 16-byte resref field is NUL-padded, not length-prefixed.</summary>
    public static string CString(ReadOnlySpan<byte> bytes)
    {
        int nul = bytes.IndexOf((byte)0);

        if (nul >= 0)
        {
            bytes = bytes[..nul];
        }

        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>Decodes a length-delimited string (no NUL scan).</summary>
    public static string Decode(ReadOnlySpan<byte> bytes) => Encoding.Latin1.GetString(bytes);
}
