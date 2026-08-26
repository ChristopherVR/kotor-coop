namespace KotorCoop.Session;

/// <summary>
/// Shared low-level token parsers used by several verb areas: resref/target validation, the
/// three object-id spellings, and numeric/coordinate parsing. Kept together because
/// <see cref="ConsoleCommand.TryParseObjectId"/> and <see cref="ConsoleCommand.TryParseItemId"/>
/// are public — other verb files and the agent side both need to agree with the parser about
/// what counts as an id.
/// </summary>
public static partial class ConsoleCommand
{
    /// <summary>True for a resref-shaped token: 1..maxLength chars of ASCII letter/digit/underscore.</summary>
    private static bool IsResRef(string value, int maxLength)
    {
        if (value.Length < 1 || value.Length > maxLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Words a player uses to mean themselves. Recognised so a self-aimed
    /// <c>slay</c> gets the rule explained instead of "that is not a tag".</summary>
    private static bool IsSelfWord(string value) =>
        value.Equals("me", StringComparison.OrdinalIgnoreCase)
        || value.Equals("self", StringComparison.OrdinalIgnoreCase)
        || value.Equals("myself", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a token that could name a creature: a resref-shaped tag, or an object id
    /// written as <c>0x2A</c>, <c>#42</c> or <c>42</c>. Ids are accepted as text and resolved
    /// later — the parser's job is to reject a line that cannot possibly name anything.
    /// </summary>
    private static bool IsTargetToken(string value) => TryParseObjectId(value, out _) || IsResRef(value, maxLength: 31);

    /// <summary>
    /// Parses an object id in the three spellings a person types. Public because the agent
    /// resolves the same token against the world and must agree with the parser about what
    /// counts as an id.
    ///
    /// ⚠ ALL THREE SPELLINGS ARE HEX, and the two that did not used to be were a live
    /// footgun. Every id this project ever shows a person — engine log, plugin log, agent
    /// reply, `creatures.ps1` dump, this file's own error messages — is printed in hex, so
    /// the only thing anyone can type is a number they just read in hex. There is no source
    /// anywhere in this project for a DECIMAL object id.
    ///
    /// Peer-list indices (`connect #2`, `goto #1`) are a different thing that happens to
    /// share the `#` character; they are parsed by their own callers and stay decimal, which
    /// is right — they are positions in a numbered list a person is reading, not ids.
    /// </summary>
    public static bool TryParseObjectId(string value, out uint objectId)
    {
        objectId = 0;
        if (value.Length == 0)
        {
            return false;
        }

        ReadOnlySpan<char> span = value.AsSpan();
        if (span[0] == '#')
        {
            span = span[1..];
        }

        bool marked = span.Length != value.Length;

        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
            marked = true;
        }

        if (span.IsEmpty)
        {
            return false;
        }

        // ⚠ A BARE token competes with TAGS, and plenty of real tags are valid hex —
        // `end_weld` is not, but `cad`, `beef` and `add` are, and reading one of those as a
        // number would target a creature nobody named. So a bare token is only an id when it
        // is all digits; anything with a letter has to say `0x` or `#` and mean it.
        if (!marked)
        {
            foreach (char c in span)
            {
                if (c is < '0' or > '9')
                {
                    return false;
                }
            }
        }

        return uint.TryParse(
            span, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out objectId);
    }

    /// <summary>
    /// Parses an ITEM object id, which — unlike a creature target — must be written in hex
    /// with an explicit marker: <c>0x131</c> or <c>#131</c>.
    ///
    /// The strictness is deliberate and it is about copy-paste. Every id these verbs deal in
    /// arrives from a log line that printed it as <c>0x131</c>, and a bare <c>131</c> read as
    /// decimal names object 0x83 — a real, different object that will usually resolve. A verb
    /// whose whole job is to write into an object must not have a spelling that silently aims
    /// at the wrong one.
    /// </summary>
    public static bool TryParseItemId(string value, out uint objectId)
    {
        objectId = 0;

        ReadOnlySpan<char> span = value.AsSpan();
        if (span.Length > 0 && span[0] == '#')
        {
            span = span[1..];
        }

        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }
        else if (value.Length == 0 || value[0] != '#')
        {
            // Neither marker: refuse rather than guess a base.
            return false;
        }

        return span.Length > 0
            && uint.TryParse(
                span, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out objectId)
            && objectId != 0;
    }

    /// <summary>A field value: decimal, or hex with an explicit 0x. Unlike an object id a
    /// bare number here is unambiguous — a stack of 20 is 20 — so decimal is the default.</summary>
    private static bool TryParseFieldValue(string value, out uint number)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(
                value.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out number);
        }

        return uint.TryParse(value, out number);
    }

    /// <summary>Invariant-culture float parse: a console line is typed with a dot whatever
    /// the machine's locale says, and a comma-decimal locale would otherwise read "12.5" as
    /// 125.</summary>
    private static bool TryParseCoordinate(string value, out float result) =>
        float.TryParse(
            value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out result);
}
