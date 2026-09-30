namespace KotorCoop.Plugin;

/// <summary>The engine's fixed16-byte CResRef, including a deliberately blank details image.</summary>
internal static class NativeResRefEncoding
{
    public static void Encode(string? text, Span<byte> destination)
    {
        destination.Clear();
        if (text is null) return;
        int count = Math.Min(text.Length, Math.Min(16, destination.Length));
        for (int i = 0; i < count; i++)
        {
            char c = text[i];
            destination[i] = c is >= ' ' and <= '~' ? (byte)c : (byte)0;
        }
    }
}
