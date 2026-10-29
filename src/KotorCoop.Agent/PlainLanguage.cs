using System.Text.RegularExpressions;
using KotorCoop.Protocol;

namespace KotorCoop.Agent;

/// <summary>
/// Turns the machine tags (<c>E_PROTO</c>, <c>W_GIT</c>...) that travel between peers and sit
/// in the log into sentences a non-technical player can act on. Translation happens on the
/// DISPLAY side only: the wire text (a Reject reason, a note's tag) is unchanged, so an older
/// peer still parses it, and the raw tag and technical detail stay in the log line.
/// </summary>
public static partial class PlainLanguage
{
    /// <summary>Prefix <c>CoopClient.GetStats</c> puts in front of a Reject reason.</summary>
    private const string RejectPrefix = "rejected by host: ";

    /// <summary>The refusal tags a host can send in a Reject packet. One place, so a test can
    /// prove each has a sentence.</summary>
    public static readonly string[] RejectTags = ["E_PROTO", "E_EXE", "E_SAVE", "E_PASSWORD"];

    private const string GenericRefusal = "The host refused the connection.";

    [GeneratedRegex(@"host is v(\d+), you are v(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPair();

    /// <summary>
    /// A client's session detail (<c>rejected by host: E_PROTO: host is v4, you are v3</c>, or
    /// the lenient <c>protocol mismatch: host is v4, you are v3</c> from a host on another wire
    /// version) as a player sentence. Anything that is not a recognised rejection comes back
    /// unchanged — a connect timeout is already plain.
    /// </summary>
    public static string Reject(string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        bool prefixed = detail.StartsWith(RejectPrefix, StringComparison.OrdinalIgnoreCase);
        string reason = prefixed ? detail[RejectPrefix.Length..] : detail;

        if (reason.StartsWith("protocol mismatch", StringComparison.OrdinalIgnoreCase))
        {
            return RejectForTag("E_PROTO", reason);
        }

        int colon = reason.IndexOf(':', StringComparison.Ordinal);
        string tag = colon > 0 ? reason[..colon] : reason;
        if (tag.StartsWith("E_", StringComparison.Ordinal))
        {
            return RejectForTag(tag, reason);
        }

        return prefixed ? GenericRefusal : detail;
    }

    /// <summary>The sentence for one <c>E_*</c> tag; <paramref name="reason"/> supplies the
    /// version numbers for <c>E_PROTO</c> when it carries them.</summary>
    public static string RejectForTag(string tag, string reason = "")
    {
        switch (tag)
        {
            case "E_PROTO":
                Match m = VersionPair().Match(reason);
                return m.Success
                    ? $"Your co-op mod version differs from the host's (you: v{m.Groups[2].Value}, host: v{m.Groups[1].Value}). "
                        + "Both players need the same release."
                    : "Your co-op mod version differs from the host's. Both players need the same release.";
            case "E_EXE":
                return "Your game code or runtime patches differ from the host's. Both players need KOTOR 1 version 1.03 "
                    + "and matching patches and patch settings. This can happen even with the same graphics mod version.";
            case "E_SAVE":
                return "You and the host are on different saves. Rejoin to load the host's world.";
            case "E_PASSWORD":
                return "Wrong password. Ask the host for the password and try again.";
            default:
                return GenericRefusal;
        }
    }

    /// <summary>The sentence for a verdict refusal (the host refused, or evicted, this world).</summary>
    public static string Refusal(RefusalReason reason) => reason switch
    {
        RefusalReason.ProtocolVersion => RejectForTag("E_PROTO"),
        RefusalReason.Executable => RejectForTag("E_EXE"),
        RefusalReason.SaveGame => RejectForTag("E_SAVE"),
        _ => "Co-op cannot share this world.",
    };

    /// <summary>
    /// What a compatibility finding means in plain words: what differs, and what was switched
    /// off because of it. The finding's own technical <see cref="CompatibilityNote.Text"/>
    /// goes to the log instead.
    /// </summary>
    public static string Note(CompatibilityNote note)
    {
        string off = note.Dropped == SessionCapabilities.None
            ? "some features"
            : CompatibilityAnnouncer.Describe(note.Dropped);

        return note.Code switch
        {
            CompatibilityCode.Unchecked =>
                "Co-op could not confirm that your game files match the host's. Sharing stays on; "
                + "if things look out of step, check you both have the same install.",
            CompatibilityCode.Save =>
                $"You are on a different save from the host, so {off} are off. "
                + "Rejoin to load the host's world and get them back.",
            CompatibilityCode.ModuleGit =>
                $"Your game files differ from the host's (area layouts), so {off} are off in this area "
                + "until you rejoin or reach an area that matches.",
            CompatibilityCode.Globals =>
                $"Your game files differ from the host's (quest variables), so {off} are off for this session.",
            CompatibilityCode.Appearance =>
                "Your game files differ from the host's (character looks), so other players keep their "
                + "default appearance for this session.",
            CompatibilityCode.Journal =>
                "Your game files differ from the host's (quest journal), so only quests you both have are shared.",
            CompatibilityCode.Tlk =>
                "Your game text differs from the host's (a text mod). This is harmless.",
            CompatibilityCode.Override =>
                "Your mods differ from the host's. Gear or items only one of you has may not show up for the other.",
            CompatibilityCode.TwoDa =>
                "Your game data tables differ from the host's, so some items or gear may look or behave differently.",
            CompatibilityCode.Modules =>
                "Your area files differ from the host's. An area edited on one side may go out of step there.",
            CompatibilityCode.Difficulty =>
                "Your difficulty setting differs from the host's, so damage may differ. Co-op stays on.",
            CompatibilityCode.Gameplay =>
                "Your game archives or campaign content differ from the host's, so shared world, dialogue, "
                + "combat, party and item syncing is off. Install matching content to get it back.",
            CompatibilityCode.Executable =>
                "Your game executable differs from the host's (a patched or modded swkotor.exe?). The host "
                + "allows this, but co-op may misbehave or crash. Mention it in any bug report.",
            CompatibilityCode.KmrpRuntime =>
                "Your executable and KMRP patch files and settings match. Their code can load at different addresses, "
                + "so co-op has allowed the connection. KMRP gameplay compatibility still needs testing.",
            _ => note.Text,
        };
    }

    /// <summary>A co-op setting that differs from the host's (the old <c>W_CFG</c> line).</summary>
    public static string SettingDifference(BehaviourDifference diff, Func<string, string> valueWord) =>
        $"Your co-op setting '{diff.Key}' differs from the host's (host: {valueWord(diff.HostValue)}, "
        + $"you: {valueWord(diff.YourValue)}).";
}
