using System.Net;
using System.Text;

namespace KotorCoop.Session;

/// <summary>
/// The string a hosting player publishes as Steam rich presence under the key
/// <c>connect</c>, and that a friend's "Join Game" hands back to us — encode and decode,
/// and nothing else.
///
/// WHY THIS IS A RULE. Steam treats the value as opaque bytes and does exactly two things
/// with it: shows a "Join Game" affordance to friends who can see the presence, and delivers
/// it verbatim to the joining player's copy of the app (as
/// <c>GameRichPresenceJoinRequested_t</c>, or as <c>+connect &lt;string&gt;</c> on the command
/// line when the app was not already running). Everything about what it *means* is ours. Two
/// consequences shape this file:
///
///   1. It is attacker-influenceable input. It arrives from another user's Steam client and
///      is turned into a socket connection and a session password. So parsing is TOTAL:
///      <see cref="TryParse"/> returns false, never throws, for every malformed shape, and
///      never performs DNS or any other I/O — resolution stays with the caller, on the
///      existing join path, where a failure is already handled.
///   2. It is a wire format between two independently-updated installs. A player on an older
///      build must reject a newer string outright rather than half-understand it, so the
///      format carries an explicit version prefix and an unknown prefix is a hard reject.
///
/// FORMAT
/// <code>
///   kc1/&lt;host&gt;:&lt;port&gt;[/&lt;token&gt;]
///
///   kc1/192.168.1.20:27140                  an open session
///   kc1/192.168.1.20:27140/c3dvcmRmaXNo     the same, password "swordfish"
///   kc1/[2001:db8::1]:27140                 IPv6 literals are bracketed, so the port colon
///                                           is unambiguous
///   kc1/play.example.com:27140              a name; resolved by the caller, not here
/// </code>
///
/// ⚠ THE TOKEN IS OBFUSCATION, NOT ENCRYPTION. It is URL-safe base64 of the password, which
/// anyone who can read the presence string can decode in one line. It exists so a private
/// session is joinable by invitation — the invitee was never told the password and should not
/// be asked to type one — and so the password is not sitting in a plainly readable field. The
/// access control is Steam's: rich presence is visible to friends and to people in the same
/// lobby, not to the world. Do not let the base64 talk anyone into treating this as a secret
/// channel.
/// </summary>
public static class SteamConnectStringRule
{
    /// <summary>The current format's version prefix, including its separator. A string that
    /// does not begin with a prefix this build knows is rejected whole.</summary>
    public const string Prefix = "kc1/";

    /// <summary>Steam's own cap on a rich-presence value. A string that would exceed it is
    /// refused at <see cref="Format"/> time rather than passed to an API that rejects it or
    /// a callback buffer that cannot carry it with its terminator.</summary>
    public const int MaxLength = 255; // Reserve the final byte of Steam's char[256] for NUL.

    /// <summary>
    /// Builds the connect string for a session, or null when it cannot be represented — an
    /// unusable host or port, or a password long enough to push the whole thing past
    /// <see cref="MaxLength"/>. Null means "publish nothing", which is always safe: a friend
    /// then simply sees no "Join Game".
    /// </summary>
    /// <param name="host">The address a joiner should dial. Whatever the host's own public
    /// address discovery produced, or the LAN address — this rule does not judge reachability.</param>
    /// <param name="port">The session port.</param>
    /// <param name="password">The session password, or empty for an open session.</param>
    public static string? Format(string? host, int port, string? password)
    {
        if (string.IsNullOrWhiteSpace(host) || port is < 1 or > 65535)
        {
            return null;
        }

        string trimmed = host.Trim();

        // A host that itself contains the separator or a colon-that-is-not-an-IPv6-literal
        // would produce a string that does not round-trip. Rather than escape, refuse: every
        // legitimate value here is an IP literal or a DNS name, and neither contains '/'.
        if (trimmed.Contains('/') || trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)))
        {
            return null;
        }

        // IPv6 literals must be bracketed so the port's colon is the last one and is
        // unambiguous. Accept an already-bracketed value unchanged so Format(Parse(x)) == x.
        string hostPart = trimmed;
        if (!hostPart.StartsWith('[') && IPAddress.TryParse(hostPart, out IPAddress? parsed)
            && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            hostPart = $"[{hostPart}]";
        }
        else if (!hostPart.StartsWith('[') && hostPart.Contains(':'))
        {
            // A colon in something that is not a parseable IPv6 literal cannot be encoded
            // unambiguously. Refuse rather than emit a string whose port is a guess.
            return null;
        }

        string result = $"{Prefix}{hostPart}:{port}";

        if (!string.IsNullOrEmpty(password))
        {
            result = $"{result}/{EncodeToken(password)}";
        }

        return Encoding.UTF8.GetByteCount(result) <= MaxLength
            && TryParse(result, out _, out _, out _) ? result : null;
    }

    /// <summary>
    /// Decodes a connect string. Returns false — never throws — for anything that is not a
    /// well-formed string of a version this build knows. <paramref name="host"/> is returned
    /// unbracketed and is NOT resolved: it may be a name, and the caller's existing join path
    /// is where resolution and its failure already live.
    /// </summary>
    public static bool TryParse(string? value, out string host, out int port, out string password)
    {
        host = string.Empty;
        port = 0;
        password = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string text = value.Trim();
        if (Encoding.UTF8.GetByteCount(text) > MaxLength || !text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        string body = text[Prefix.Length..];

        // Split off the optional token FIRST, so a '/' inside it (there cannot be one — base64
        // url-safe has no '/' — but the parse must not depend on that) cannot be confused with
        // the separator, and so an empty token is rejected rather than read as an empty password.
        string endpoint = body;
        string? token = null;
        int slash = body.IndexOf('/');
        if (slash >= 0)
        {
            endpoint = body[..slash];
            token = body[(slash + 1)..];

            if (token.Length == 0 || token.Contains('/'))
            {
                return false;
            }
        }

        if (!TrySplitEndpoint(endpoint, out host, out port))
        {
            host = string.Empty;
            port = 0;
            return false;
        }

        if (token is not null && !TryDecodeToken(token, out password))
        {
            host = string.Empty;
            port = 0;
            password = string.Empty;
            return false;
        }

        return true;
    }

    /// <summary>Splits <c>host:port</c> / <c>[v6]:port</c>. Total; no I/O.</summary>
    private static bool TrySplitEndpoint(string endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        if (endpoint.Length == 0)
        {
            return false;
        }

        int colon;
        if (endpoint[0] == '[')
        {
            int close = endpoint.IndexOf(']');
            if (close < 2)
            {
                return false;
            }

            host = endpoint[1..close];
            if (!IPAddress.TryParse(host, out IPAddress? address)
                || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                return false;
            }
            colon = close + 1;

            if (colon >= endpoint.Length || endpoint[colon] != ':')
            {
                return false;
            }
        }
        else
        {
            colon = endpoint.LastIndexOf(':');
            if (colon <= 0)
            {
                return false;
            }

            host = endpoint[..colon];

            // An unbracketed value with more than one colon is an IPv6 literal that was
            // written without brackets. Its "port" would be a hextet; refuse.
            if (host.Contains(':'))
            {
                return false;
            }
        }

        if (host.Length == 0 || host.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || Uri.CheckHostName(host) == UriHostNameType.Unknown)
        {
            return false;
        }

        // Bind-all and broadcast addresses are not destinations a friend can dial.
        if (IPAddress.TryParse(host, out IPAddress? destination))
        {
            // Dual-stack sockets treat mapped IPv4 literals as their IPv4 destination.
            // Compare that destination so ::ffff:0.0.0.0 cannot advertise a bind address.
            if (destination.IsIPv4MappedToIPv6) destination = destination.MapToIPv4();
            if (destination.Equals(IPAddress.Any) || destination.Equals(IPAddress.IPv6Any)
                || destination.Equals(IPAddress.Broadcast)) return false;
        }

        string portText = endpoint[(colon + 1)..];
        return portText.Length > 0 && portText.All(char.IsAsciiDigit)
            && int.TryParse(portText, out port) && port is >= 1 and <= 65535;
    }

    /// <summary>URL-safe base64 with the padding removed: the alphabet Steam, command lines
    /// and log files all carry without mangling.</summary>
    private static string EncodeToken(string password) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(password))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static bool TryDecodeToken(string token, out string password)
    {
        password = string.Empty;

        if (token.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        {
            return false;
        }

        string unpadded = token.Replace('-', '+').Replace('_', '/');

        // A base64 body whose length is 1 mod 4 encodes no whole byte and is not merely
        // unpadded, it is malformed. Rejected here rather than left to Convert, which is
        // happy to throw but says nothing useful.
        string padded = (unpadded.Length % 4) switch
        {
            0 => unpadded,
            2 => unpadded + "==",
            3 => unpadded + "=",
            _ => string.Empty,
        };

        if (padded.Length == 0)
        {
            return false;
        }

        try
        {
            password = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(padded));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
