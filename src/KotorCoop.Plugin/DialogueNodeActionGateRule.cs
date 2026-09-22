namespace KotorCoop.Plugin;

/// <summary>Pure allowlist for the two Leviathan dialogue-node action scripts whose native
/// control transfer is host-owned. Unknown role, module, or script names fail open.</summary>
internal static class DialogueNodeActionGateRule
{
    public static bool ShouldSuppress(
        bool isClient,
        ReadOnlySpan<byte> moduleResref,
        ReadOnlySpan<byte> scriptResref)
    {
        if (!isClient || !EqualsAsciiIgnoreCase(moduleResref, "lev_m40aa"u8))
        {
            return false;
        }

        return EqualsAsciiIgnoreCase(scriptResref, "k_plev_transform"u8)
            || EqualsAsciiIgnoreCase(scriptResref, "k_plev_transfrm1"u8);
    }

    private static bool EqualsAsciiIgnoreCase(ReadOnlySpan<byte> value, ReadOnlySpan<byte> expected)
    {
        if (value.Length != expected.Length)
        {
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            byte left = value[i];
            byte right = expected[i];
            if (left is >= (byte)'A' and <= (byte)'Z')
            {
                left = (byte)(left + ('a' - 'A'));
            }

            if (left != right)
            {
                return false;
            }
        }

        return true;
    }
}
