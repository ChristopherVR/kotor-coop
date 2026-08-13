using System.Text;
using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The native password panel's OK handler reads a CExoString out of the engine and turns it
/// into the console line <c>password &lt;secret&gt;</c>. These pin the composition rules —
/// above all the CExoString length trap (the engine's length INCLUDES the null terminator),
/// which has produced three separate live bugs in this repo already.
/// </summary>
public class PasswordAnswerTests
{
    private static string ComposeToString(byte[] typed, int destinationSize = 96)
    {
        byte[] destination = new byte[destinationSize];
        int n = PasswordAnswer.Compose(typed, destination);
        return Encoding.ASCII.GetString(destination, 0, n);
    }

    private static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s);

    [Fact]
    public void ComposesTheExactConsoleLine()
    {
        Assert.Equal("password hoth", ComposeToString(Ascii("hoth")));
    }

    [Fact]
    public void CutsAtTheFirstNullWhateverFollowsIt()
    {
        // The raw engine read: "hoth\0" (length-includes-null), and stale buffer bytes after
        // the terminator from a longer previous entry. Only "hoth" is the password.
        Assert.Equal("password hoth", ComposeToString(Ascii("hoth\0garbage")));
    }

    [Fact]
    public void EmptyTypedTextComposesNothing()
    {
        // OK on an empty box is a cancel, not a join with a zero-length password.
        Assert.Equal(0, PasswordAnswer.Compose(Ascii(""), new byte[96]));
        Assert.Equal(0, PasswordAnswer.Compose(Ascii("\0"), new byte[96]));
        Assert.Equal(0, PasswordAnswer.Compose(Ascii("\0hoth"), new byte[96]));
    }

    [Fact]
    public void PassphrasesWithSpacesSurviveWhole()
    {
        // The `password` verb's contract: the whole rest of the line is the secret.
        Assert.Equal("password darth revan lives", ComposeToString(Ascii("darth revan lives")));
    }

    [Fact]
    public void ClampsToTheDestinationLeavingRoomForTheTerminator()
    {
        byte[] destination = new byte[16];
        int n = PasswordAnswer.Compose(Ascii("abcdefghijklmnop"), destination);

        // "password " is 9 bytes; a 16-byte line budget keeps 15 for text (the block writer
        // appends the null), so 6 secret bytes survive.
        Assert.Equal(15, n);
        Assert.Equal("password abcdef", Encoding.ASCII.GetString(destination, 0, n));
    }

    [Fact]
    public void ADestinationTooSmallForAnySecretAsksForNothing()
    {
        // Prefix-only (or less) can never carry a secret; composing it would publish a bare
        // "password" line the dispatcher reads as an empty answer.
        Assert.Equal(0, PasswordAnswer.Compose(Ascii("hoth"), new byte[PasswordAnswer.Prefix.Length]));
        Assert.Equal(0, PasswordAnswer.Compose(Ascii("hoth"), new byte[3]));
    }

    [Fact]
    public void NonPrintableBytesAreNeutralisedNotForwarded()
    {
        // The engine's editbox only ever appends printables, so a control byte here is
        // corruption; it must not reach the console channel as-is.
        Assert.Equal("password ho?h", ComposeToString([
            (byte)'h', (byte)'o', 0x07, (byte)'h',
        ]));
    }

    [Fact]
    public void ExactFitFillsTheBudgetPrecisely()
    {
        // 96-byte line, 1 reserved for the terminator, 9 for the prefix: 86 secret bytes fit.
        string secret = new('x', 86);
        Assert.Equal($"password {secret}", ComposeToString(Ascii(secret + "y")));
    }
}
