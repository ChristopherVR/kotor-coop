using KotorCoop.Agent;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>Raw E_* / W_* tags must never reach a player's screen; each maps to a sentence.</summary>
public class PlainLanguageTests
{
    private static void AssertPlain(string text)
    {
        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotMatch(@"\b[EW]_[A-Z0-9]+", text);
    }

    [Fact]
    public void EveryCompatibilityCodeHasAPlainNote()
    {
        foreach (CompatibilityCode code in Enum.GetValues<CompatibilityCode>())
        {
            if (code == CompatibilityCode.None)
            {
                continue;
            }

            CompatibilityNote note = new(code, ContentCompatibility.ObjectIdKeyed, "technical detail");
            string plain = PlainLanguage.Note(note);
            AssertPlain(plain);
            Assert.NotEqual("technical detail", plain);
        }
    }

    [Fact]
    public void EveryRefusalReasonHasAPlainSentence()
    {
        foreach (RefusalReason reason in Enum.GetValues<RefusalReason>())
        {
            AssertPlain(PlainLanguage.Refusal(reason));
        }
    }

    [Fact]
    public void EveryRejectTagHasAMappingAndTheWireTextsTranslate()
    {
        foreach (string tag in PlainLanguage.RejectTags)
        {
            string plain = PlainLanguage.RejectForTag(tag);
            AssertPlain(plain);
            Assert.NotEqual("The host refused the connection.", plain);
        }

        // Every tag the code can put on the wire.
        foreach (RefusalReason reason in Enum.GetValues<RefusalReason>().Where(r => r != RefusalReason.None))
        {
            CompatibilityVerdict v = new(reason, SessionCapabilities.None, []);
            string tag = v.Summary[..v.Summary.IndexOf(':', StringComparison.Ordinal)];
            Assert.Contains(tag, PlainLanguage.RejectTags);
            AssertPlain(PlainLanguage.Reject("rejected by host: " + v.Summary));
        }
    }

    [Fact]
    public void ProtocolMismatchNamesBothVersions()
    {
        string plain = PlainLanguage.Reject("rejected by host: E_PROTO: host is v4, you are v3");
        Assert.Equal(
            "Your co-op mod version differs from the host's (you: v3, host: v4). Both players need the same release.",
            plain);

        // The lenient path from a host on another wire version.
        Assert.Contains("you: v3, host: v4",
            PlainLanguage.Reject("rejected by host: protocol mismatch: host is v4, you are v3"));
    }

    [Fact]
    public void ExecutableAndPasswordAreExplained()
    {
        Assert.Contains("KOTOR 1 version 1.03",
            PlainLanguage.Reject("rejected by host: E_EXE: different swkotor.exe (addresses would not match)"));
        Assert.Contains("password",
            PlainLanguage.Reject("rejected by host: E_PASSWORD: wrong password"), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonRejectionDetailsPassThrough()
    {
        Assert.Equal("no answer from 1.2.3.4:27140", PlainLanguage.Reject("no answer from 1.2.3.4:27140"));
        Assert.Equal("The host refused the connection.", PlainLanguage.Reject("rejected by host: something new"));
    }

    [Fact]
    public void AnnouncerShowsPlainTextAndLogsTheTag()
    {
        CompatibilityAnnouncer announcer = new();
        List<string> shown = [];
        List<string> logged = [];

        announcer.Observe(
            new CompatibilityVerdict(RefusalReason.Executable, SessionCapabilities.None, []),
            shown.Add, logged.Add);

        Assert.DoesNotContain("E_EXE", Assert.Single(shown), StringComparison.Ordinal);
        Assert.Contains("E_EXE", Assert.Single(logged), StringComparison.Ordinal);
    }
}
