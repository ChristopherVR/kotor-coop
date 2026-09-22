namespace KotorCoop.Protocol;

/// <summary>Bit-level contract for the agent-published local role word in the plugin's
/// version-73 shared block. This is local shared memory, not a network packet.</summary>
public static class LocalSessionRoleRule
{
    public const uint ValidBit = 1u << 0;
    public const uint HostBit = 1u << 1;

    public static uint Encode(bool sessionActive, bool isHost) =>
        sessionActive ? ValidBit | (isHost ? HostBit : 0u) : 0u;

    public static bool TryDecode(uint word, out bool isHost)
    {
        isHost = (word & HostBit) != 0;
        return (word & ValidBit) != 0;
    }

    public static bool IsPositiveHost(uint word) =>
        TryDecode(word, out bool isHost) && isHost;
}
