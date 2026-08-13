using System.Text;
using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The plugin-side tag compare that decides whether an apply request names an entry the
/// journal already has. Compiled straight from the plugin source (see the csproj) so the
/// rule cannot drift from what ships in dinput8.dll.
/// </summary>
public class JournalTagsTests
{
    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    [Fact]
    public void MatchesIgnoringCase()
    {
        Assert.True(JournalTags.EqualsNoCase(Ascii("End_Attack"), Ascii("end_attack")));
        Assert.True(JournalTags.EqualsNoCase(Ascii("END_ATTACK"), Ascii("End_Attack")));
        Assert.False(JournalTags.EqualsNoCase(Ascii("end_attack"), Ascii("end_defend")));
    }

    [Fact]
    public void EngineLengthIncludesTheNullAndMustNotBreakTheCompare()
    {
        // CExoString lengths are strlen + 1, so a span built from the engine's own length
        // field carries the terminator. That trailing null must not read as a difference.
        Assert.True(JournalTags.EqualsNoCase(Ascii("end_attack\0"), Ascii("end_attack")));
    }

    [Fact]
    public void BytesAfterTheNullAreGarbageAndIgnored()
    {
        // A bounded over-read of the engine buffer may pick up whatever follows the
        // string; everything past the first null is not part of the tag.
        Assert.True(JournalTags.EqualsNoCase(Ascii("end_attack\0XYZW"), Ascii("end_attack")));
        Assert.False(JournalTags.EqualsNoCase(Ascii("end\0_attack"), Ascii("end_attack")));
    }

    [Fact]
    public void PrefixesAreNotMatches()
    {
        Assert.False(JournalTags.EqualsNoCase(Ascii("end_attack"), Ascii("end_att")));
        Assert.False(JournalTags.EqualsNoCase(Ascii("end_att"), Ascii("end_attack")));
    }

    [Fact]
    public void EmptyTagsNeverMatchAnything()
    {
        // An empty request tag matching an empty engine slot would silently skip a real
        // apply; "no tag" is not a quest.
        Assert.False(JournalTags.EqualsNoCase(Ascii(""), Ascii("")));
        Assert.False(JournalTags.EqualsNoCase(Ascii("\0"), Ascii("\0garbage")));
        Assert.False(JournalTags.EqualsNoCase(Ascii(""), Ascii("end_attack")));
    }
}
