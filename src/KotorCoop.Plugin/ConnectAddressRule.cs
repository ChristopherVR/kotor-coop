namespace KotorCoop.Plugin;

/// <summary>
/// Turns what a player typed into the browser's address box into the console line the agent
/// already understands: <c>connect &lt;host&gt;:&lt;port&gt;</c>. Pure arithmetic over strings
/// and spans — no pointers, no sockets, no DNS — so the 64-bit test host compiles it in and
/// pins the grammar (see KotorCoop.Tests.csproj's plugin-file include list).
///
/// WHY A RULE OF ITS OWN. The plugin is NativeAOT x86 and cannot reference the agent's
/// assemblies, so it cannot call <c>SteamConnectStringRule</c> or
/// <c>CoopCommand.TryResolveEndpoint</c> directly. This class mirrors the ENDPOINT half of
/// that grammar (<c>host[:port]</c>, <c>[v6]:port</c>) and always emits an explicit port,
/// because that is the one shape the agent's resolver reads unambiguously
/// (<c>IPEndPoint.TryParse</c> gives a bracketed literal with no port the port 0). The tests
/// hold the two sides to the same answers.
///
/// The rules worth pinning:
///
///   1. ⚠ THE CEXOSTRING LENGTH TRAP (three prior bugs in this repo). The engine's string
///      length INCLUDES the null terminator, and a stale buffer can carry bytes beyond it. The
///      typed text is whatever precedes the FIRST null, nothing more.
///   2. Empty typed text composes to NOTHING and no rejection: an OK on an empty box is a
///      cancel, exactly as it is for the password prompt.
///   3. Anything that is not an address composes to nothing WITH a rejection — a short line
///      that fits the modal's title label, so the panel can stay open and say what was wrong
///      instead of dialling garbage or silently closing.
///   4. A hostname is passed through, not resolved: resolution (and its failure message)
///      lives in the agent's existing join path.
///   5. A bare IPv6 literal (<c>2001:db8::1</c>) is accepted with the default port; to give it
///      a port it must be bracketed (<c>[2001:db8::1]:27140</c>), the same convention the Steam
///      connect string uses. An unbracketed literal followed by <c>:27140</c> is ambiguous and
///      is read as the literal alone, so the tests pin that too.
/// </summary>
internal static class ConnectAddressRule
{
    /// <summary>The verb prefix, exactly as the player would have typed it in the console.</summary>
    public const string Prefix = "connect ";

    /// <summary>The session port a typed address gets when it names none (coop.ini's own default).</summary>
    public const int DefaultPort = 27140;

    /// <summary>The longest host part accepted — a DNS name's own cap.</summary>
    public const int MaxHostLength = 253;

    /// <summary>
    /// Normalises what the player typed into <c>host:port</c> or <c>[v6]:port</c>. Returns
    /// false with a short, player-readable <paramref name="rejection"/> for anything that is
    /// not an address (including empty text — the caller decides whether empty is a cancel).
    /// Never throws.
    /// </summary>
    public static bool TryNormalize(string? typed, out string address, out string rejection)
    {
        address = string.Empty;
        rejection = string.Empty;

        string text = (typed ?? string.Empty).Trim();

        // A player who types the console verb by habit: "connect 1.2.3.4" is still an address,
        // and a bare "connect" is the verb with nothing after it, not a host of that name.
        if (text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[Prefix.Length..].Trim();
        }
        else if (text.Equals(Prefix.TrimEnd(), StringComparison.OrdinalIgnoreCase))
        {
            text = string.Empty;
        }

        if (text.Length == 0)
        {
            rejection = "Type the host's address";
            return false;
        }

        foreach (char c in text)
        {
            if (c < '!')
            {
                rejection = "No spaces in an address";
                return false;
            }

            if (c > '~')
            {
                rejection = "Letters, digits, dots and dashes only";
                return false;
            }
        }

        if (text.Contains('/') || text.Contains('\\'))
        {
            rejection = "Just host:port, no http:// or path";
            return false;
        }

        string host;      // the emitted host part: bracketed when it is an IPv6 literal
        string? portText; // null = no port typed; "" = a colon with nothing after it

        if (text[0] == '[')
        {
            int close = text.IndexOf(']');
            if (close < 2)
            {
                rejection = "Use [address]:port for IPv6";
                return false;
            }

            string literal = text[1..close];
            string after = text[(close + 1)..];
            if (after.Length == 0)
            {
                portText = null;
            }
            else if (after[0] == ':')
            {
                portText = after[1..];
            }
            else
            {
                rejection = "Use [address]:port for IPv6";
                return false;
            }

            if (!LooksLikeIpv6(literal))
            {
                rejection = "Brackets are for IPv6 only";
                return false;
            }

            if (literal.Length > MaxHostLength)
            {
                rejection = "That address is too long";
                return false;
            }

            host = "[" + literal + "]";
        }
        else
        {
            int colons = 0;
            foreach (char c in text)
            {
                if (c == ':')
                {
                    colons++;
                }
            }

            string bare;
            if (colons >= 2)
            {
                // An unbracketed IPv6 literal: the whole thing is the address (rule 5).
                if (!LooksLikeIpv6(text))
                {
                    rejection = "Use [address]:port for IPv6";
                    return false;
                }

                bare = text;
                host = "[" + text + "]";
                portText = null;
            }
            else if (colons == 1)
            {
                int colon = text.IndexOf(':');
                bare = text[..colon];
                host = bare;
                portText = text[(colon + 1)..];
            }
            else
            {
                bare = text;
                host = text;
                portText = null;
            }

            if (bare.Length == 0)
            {
                rejection = "Type the host's address";
                return false;
            }

            if (colons < 2 && !LooksLikeHostName(bare))
            {
                rejection = "Letters, digits, dots and dashes only";
                return false;
            }

            if (bare.Length > MaxHostLength)
            {
                rejection = "That address is too long";
                return false;
            }
        }

        int port = DefaultPort;
        if (portText is not null)
        {
            if (!int.TryParse(portText, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out port)
                || port is < 1 or > 65535)
            {
                rejection = "Port must be 1-65535, e.g. host:27140";
                return false;
            }
        }

        address = $"{host}:{port}";
        return true;
    }

    /// <summary>
    /// Composes <c>connect &lt;address&gt;</c> into <paramref name="destination"/> from the raw
    /// bytes read out of the engine's editbox, cutting at the first null and clamping to the
    /// destination (sized to the shared block's console line, terminator included). Returns
    /// the composed length; 0 with a null <paramref name="rejection"/> when nothing was typed
    /// (the caller's cancel), or 0 with a rejection when the text is not an address.
    /// </summary>
    public static int Compose(ReadOnlySpan<byte> typedRaw, Span<byte> destination, out string? rejection)
    {
        rejection = null;

        // Rule 1: the first null ends the string, whatever the engine's length field said.
        int typedLength = typedRaw.IndexOf((byte)0);
        if (typedLength < 0)
        {
            typedLength = typedRaw.Length;
        }

        if (typedLength == 0)
        {
            return 0; // rule 2: nothing typed is a cancel
        }

        char[] chars = new char[typedLength];
        for (int i = 0; i < typedLength; i++)
        {
            // The engine's editbox only appends printables (AddNewChar), so anything outside
            // that range is corruption, not input — neutralise it rather than forward it.
            byte c = typedRaw[i];
            chars[i] = c is < 0x20 or > 0x7E ? '?' : (char)c;
        }

        if (!TryNormalize(new string(chars), out string address, out string why))
        {
            rejection = why;
            return 0;
        }

        // Leave room for the terminator PublishConsoleCommand appends.
        int budget = destination.Length - 1;
        if (Prefix.Length + address.Length > budget)
        {
            rejection = "That address is too long";
            return 0;
        }

        for (int i = 0; i < Prefix.Length; i++)
        {
            destination[i] = (byte)Prefix[i];
        }

        for (int i = 0; i < address.Length; i++)
        {
            destination[Prefix.Length + i] = (byte)address[i];
        }

        return Prefix.Length + address.Length;
    }

    /// <summary>
    /// The address a composed <c>connect</c> line carries, or empty when the line is not one —
    /// what gets remembered for the next prefill.
    /// </summary>
    public static string AddressOf(ReadOnlySpan<byte> line)
    {
        if (line.Length <= Prefix.Length)
        {
            return string.Empty;
        }

        for (int i = 0; i < Prefix.Length; i++)
        {
            if (line[i] != (byte)Prefix[i])
            {
                return string.Empty;
            }
        }

        int end = line.IndexOf((byte)0);
        ReadOnlySpan<byte> rest = line[Prefix.Length..(end < 0 ? line.Length : end)];
        return System.Text.Encoding.ASCII.GetString(rest);
    }

    /// <summary>
    /// The prefill for a player who never used the box but configured coop.ini's <c>host</c>
    /// (and maybe <c>port</c>): the two keys folded into one normalised address, or empty when
    /// the host key is absent or not an address.
    /// </summary>
    public static string PrefillFromConfig(string? host, string? port)
    {
        string h = (host ?? string.Empty).Trim();
        if (h.Length == 0)
        {
            return string.Empty;
        }

        string p = (port ?? string.Empty).Trim();

        string joined;
        if (p.Length == 0)
        {
            joined = h;
        }
        else if (h[0] != '[' && h.Contains(':'))
        {
            joined = $"[{h}]:{p}";
        }
        else
        {
            joined = $"{h}:{p}";
        }

        return TryNormalize(joined, out string address, out _) ? address : string.Empty;
    }

    private static bool LooksLikeIpv6(string s)
    {
        if (s.Length == 0 || !s.Contains(':'))
        {
            return false;
        }

        foreach (char c in s)
        {
            bool ok = c is ':' or '.' || char.IsAsciiHexDigit(c);
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeHostName(string s)
    {
        foreach (char c in s)
        {
            bool ok = char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
