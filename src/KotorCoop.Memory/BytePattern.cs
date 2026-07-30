using System.Globalization;

namespace KotorCoop.Memory;

/// <summary>
/// An array-of-bytes signature with wildcards, written the way every RE tool writes them:
/// "8B 0D ?? ?? ?? ?? 85 C9". Wildcards cover the operand bytes that move between builds,
/// so a signature keeps matching after a patch when a hardcoded address would not.
/// </summary>
public sealed class BytePattern
{
    private readonly byte[] _bytes;
    private readonly bool[] _mask;

    private BytePattern(byte[] bytes, bool[] mask, string text)
    {
        _bytes = bytes;
        _mask = mask;
        Text = text;
    }

    public string Text { get; }

    public int Length => _bytes.Length;

    public static BytePattern Parse(string pattern)
    {
        if (!TryParse(pattern, out BytePattern? result, out string? error))
        {
            throw new FormatException(error);
        }

        return result;
    }

    public static bool TryParse(string pattern, out BytePattern result, out string? error)
    {
        result = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            error = "Pattern is empty.";
            return false;
        }

        string[] tokens = pattern.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            error = "Pattern contains no tokens.";
            return false;
        }

        byte[] bytes = new byte[tokens.Length];
        bool[] mask = new bool[tokens.Length];

        for (int i = 0; i < tokens.Length; i++)
        {
            string token = tokens[i];

            if (token is "?" or "??" or "*")
            {
                mask[i] = false;
                continue;
            }

            if (token.Length != 2
                || !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
            {
                error = $"Token '{token}' at index {i} is not a hex byte or a wildcard.";
                return false;
            }

            bytes[i] = value;
            mask[i] = true;
        }

        if (!mask[0])
        {
            error = "Pattern must not start with a wildcard.";
            return false;
        }

        result = new BytePattern(bytes, mask, string.Join(' ', tokens));
        return true;
    }

    /// <summary>Builds a fully literal pattern from raw bytes, e.g. a magic marker.</summary>
    public static BytePattern FromBytes(ReadOnlySpan<byte> bytes)
    {
        byte[] copy = bytes.ToArray();
        bool[] mask = new bool[copy.Length];
        Array.Fill(mask, true);
        return new BytePattern(copy, mask, Convert.ToHexString(copy));
    }

    public bool MatchesAt(ReadOnlySpan<byte> haystack, int index)
    {
        if (index < 0 || index + _bytes.Length > haystack.Length)
        {
            return false;
        }

        for (int i = 0; i < _bytes.Length; i++)
        {
            if (_mask[i] && haystack[index + i] != _bytes[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns the index of the first match, or -1. Pure, so it is directly testable.</summary>
    public int IndexIn(ReadOnlySpan<byte> haystack, int startIndex = 0)
    {
        if (_bytes.Length == 0 || haystack.Length < _bytes.Length)
        {
            return -1;
        }

        byte first = _bytes[0];
        int last = haystack.Length - _bytes.Length;

        for (int i = Math.Max(0, startIndex); i <= last; i++)
        {
            // The parser forbids a leading wildcard precisely so this fast reject is valid.
            if (haystack[i] != first)
            {
                continue;
            }

            if (MatchesAt(haystack, i))
            {
                return i;
            }
        }

        return -1;
    }

    public IEnumerable<int> AllIndicesIn(byte[] haystack)
    {
        int index = 0;
        while (true)
        {
            int found = IndexIn(haystack, index);
            if (found < 0)
            {
                yield break;
            }

            yield return found;
            index = found + 1;
        }
    }

    public override string ToString() => Text;
}
