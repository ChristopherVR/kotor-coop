using System.Net;
using System.Text.RegularExpressions;

namespace KotorCoop.Agent;

/// <summary>Best-effort removal of personal connection data from a support copy, never the source logs.</summary>
public sealed class SupportRedaction
{
    private static readonly Regex Settings = new(
        @"^[ \t]*(?:password|host|name)[ \t]*=[ \t]*(?<value>[^;#\r\n]*)", RegexOptions.Multiline | RegexOptions.IgnoreCase);
    private static readonly Regex Credentials = new(
        @"(?i)(?:\b(?:password|passphrase|token)\s*[:=]|--password\s+)[^\r\n]*");
    private static readonly Regex Invite = new(@"\bkc\d+/[^\s""'<>]+", RegexOptions.IgnoreCase);
    private static readonly Regex ConnectCommand = new(
        @"(?i)(?<![\w])(?:connect|--join)\s+(?:\[[^\]\r\n]+\]|[\w-]+(?:\.[\w-]+)+|localhost)(?::\d+)?[^\r\n]*");
    private static readonly Regex WindowsPath = new(@"(?<![\w])(?:[A-Za-z]:[\\/]|\\\\)[^\r\n""<>|;]*");
    private static readonly Regex UnixPath = new(@"(?<![\w:])/(?:home|Users|mnt|media|tmp|var|opt|run)/[^\r\n""<>|;]*");
    private static readonly Regex Ipv4 = new(@"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])");
    private static readonly Regex Ipv6 = new(@"(?<![\w:])(?:[0-9a-f]{0,4}:){2,}[0-9a-f:.]*(?:%[\w.-]+)?", RegexOptions.IgnoreCase);
    private readonly string[] _privateValues;

    public SupportRedaction(string iniText)
    {
        _privateValues = Settings.Matches(iniText).Select(m => m.Groups["value"].Value.Trim())
            .Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length).ToArray();
    }

    public string Redact(string text)
    {
        // Remove whole invite/command tails first: passwords can be quoted, contain spaces,
        // or be reversibly encoded in an invitation rather than appear as plain INI values.
        text = Invite.Replace(text, "<connection invite removed>");
        text = Credentials.Replace(text, "<credential removed>");
        text = ConnectCommand.Replace(text, "<connection command removed>");
        text = WindowsPath.Replace(text, "<path>");
        text = UnixPath.Replace(text, "<path>");
        text = Ipv6.Replace(text, m => IPAddress.TryParse(m.Value, out _) ? "<address>" : m.Value);
        text = Ipv4.Replace(text, m => IPAddress.TryParse(m.Value, out _) ? "<address>" : m.Value);
        foreach (string value in _privateValues)
        {
            // Short configured names must not erase every matching letter in diagnostic words.
            text = value.Length < 4
                ? Regex.Replace(text, @"(?<!\w)" + Regex.Escape(value) + @"(?!\w)", "<private>", RegexOptions.IgnoreCase)
                : text.Replace(value, "<private>", StringComparison.OrdinalIgnoreCase);
        }
        return text;
    }
}
