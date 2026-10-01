using System.Text;

namespace KotorCoop.Protocol;

/// <summary>
/// The conversations only the HOST may start: a joiner's own start is refused locally and
/// relayed to the host instead (docs/research/dialogue-sync.md, host-start relay).
///
/// ONE LIST, TWO CALLERS. The plugin's <c>DialogueGateHook</c> (an engine detour: no
/// allocation, raw bytes) calls <see cref="IsHostLed(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
/// against precomputed byte arrays; the host's relay controller and the agent call the string
/// overload. Both funnel into the same arrays, so the gate and the relay cannot drift. This
/// file is compiled into the plugin by source (see its csproj).
///
/// The Manaan airlock walks one player into a solo module (crashed the joiner twice); the
/// dark-side temple's Bastila scene is driven by an event on the engine that owns her AI, so a
/// joiner-led start ran the scene's opening and then did nothing (live 2026-10-01).
/// </summary>
public static class HostLedConversationRule
{
    /// <summary>Dialogue resrefs, lower case. A start naming one of these is host-led.</summary>
    public static readonly string[] Resrefs = ["man28_airlock", "unk44_evilbast"];

    /// <summary>Object tags, lower case, for a default-dialogue start that names no resref.</summary>
    public static readonly string[] Tags = ["bastilae"];

    private static readonly byte[][] ResrefBytes = ToBytes(Resrefs);

    private static readonly byte[][] TagBytes = ToBytes(Tags);

    /// <summary>Case-insensitive match on the dialogue resref or the target's tag.</summary>
    public static bool IsHostLed(string? resref, string? tag) =>
        IsHostLed(Ascii(resref), Ascii(tag));

    /// <summary>
    /// Allocation-free form for the engine detour. Each span ends at its first NUL (or its
    /// end); an empty span matches nothing.
    /// </summary>
    public static bool IsHostLed(ReadOnlySpan<byte> resref, ReadOnlySpan<byte> tag)
    {
        foreach (byte[] want in ResrefBytes)
        {
            if (EqualsNulTerminated(resref, want))
            {
                return true;
            }
        }

        foreach (byte[] want in TagBytes)
        {
            if (EqualsNulTerminated(tag, want))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EqualsNulTerminated(ReadOnlySpan<byte> actual, byte[] want)
    {
        int length = 0;
        while (length < actual.Length && actual[length] != 0)
        {
            length++;
        }

        if (length != want.Length)
        {
            return false;
        }

        for (int i = 0; i < length; i++)
        {
            byte c = actual[i];
            if (c is >= (byte)'A' and <= (byte)'Z')
            {
                c = (byte)(c + 32);
            }

            if (c != want[i])
            {
                return false;
            }
        }

        return true;
    }

    private static byte[] Ascii(string? text) => string.IsNullOrEmpty(text) ? [] : Encoding.ASCII.GetBytes(text);

    private static byte[][] ToBytes(string[] names)
    {
        byte[][] result = new byte[names.Length][];
        for (int i = 0; i < names.Length; i++)
        {
            result[i] = Encoding.ASCII.GetBytes(names[i]);
        }

        return result;
    }
}
