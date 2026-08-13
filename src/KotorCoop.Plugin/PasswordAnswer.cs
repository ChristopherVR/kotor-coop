namespace KotorCoop.Plugin;

/// <summary>
/// Turns the bytes a player typed into the native password panel into the console line the
/// agent already understands: <c>password &lt;secret&gt;</c>. Pure arithmetic over spans —
/// no pointers, no engine — so the 64-bit test host can compile it in and pin the rules
/// (see KotorCoop.Tests.csproj's plugin-file include list).
///
/// The rules worth pinning:
///
///   1. ⚠ THE CEXOSTRING LENGTH TRAP (three prior bugs in this repo). The engine's string
///      length INCLUDES the null terminator, so the raw bytes read from panel+0x548 carry a
///      trailing NUL — and a stale buffer can carry bytes beyond it. The typed text is
///      whatever precedes the FIRST null, nothing more.
///   2. The whole remainder of the line is the password (ConsoleCommand.Parse's contract for
///      the <c>password</c> verb), so embedded spaces survive; only leading/trailing spaces
///      are the separator's and get no special handling here beyond what the player typed.
///   3. Empty typed text composes to NOTHING (returns 0): an OK on an empty box is a cancel,
///      not an attempt to join with a zero-length password the host would only refuse.
/// </summary>
internal static class PasswordAnswer
{
    /// <summary>The verb prefix, exactly as the player would have typed it in the console.</summary>
    public const string Prefix = "password ";

    /// <summary>
    /// Composes <c>password &lt;typed&gt;</c> into <paramref name="destination"/>, cutting
    /// <paramref name="typedRaw"/> at its first null and clamping to the destination (which is
    /// sized to the shared block's console line, terminator included, by the caller). Returns
    /// the composed length, or 0 when the typed text is empty — the caller's cancel signal.
    /// </summary>
    public static int Compose(ReadOnlySpan<byte> typedRaw, Span<byte> destination)
    {
        // Rule 1: the first null ends the string, whatever the engine's length field said.
        int typedLength = typedRaw.IndexOf((byte)0);
        if (typedLength < 0)
        {
            typedLength = typedRaw.Length;
        }

        if (typedLength == 0)
        {
            return 0; // rule 3: nothing typed is a cancel, not an empty password
        }

        // Leave room for the terminator PublishConsoleCommand appends: the destination span
        // is the full line budget, so the composed text may fill it exactly minus one.
        int budget = destination.Length - 1;
        if (budget <= Prefix.Length)
        {
            return 0; // a destination too small for even one secret byte asks for nothing
        }

        for (int i = 0; i < Prefix.Length; i++)
        {
            destination[i] = (byte)Prefix[i];
        }

        int count = Math.Min(typedLength, budget - Prefix.Length);
        for (int i = 0; i < count; i++)
        {
            // The engine's editbox only appends printables (AddNewChar), so anything outside
            // that range is corruption, not input — neutralise it rather than forward it.
            byte c = typedRaw[i];
            destination[Prefix.Length + i] = c is < 0x20 or > 0x7E ? (byte)'?' : c;
        }

        return Prefix.Length + count;
    }
}
