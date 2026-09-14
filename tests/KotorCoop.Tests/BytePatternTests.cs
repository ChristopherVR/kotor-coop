using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

public class BytePatternTests
{
    [Fact]
    public void EmptyLiteralPatternIsRejected() =>
        Assert.Throws<ArgumentException>(() => BytePattern.FromBytes([]));

    [Fact]
    public void ExtremeIndicesDoNotOverflow()
    {
        BytePattern pattern = BytePattern.Parse("AA BB");
        Assert.False(pattern.MatchesAt([0xAA, 0xBB], int.MaxValue));
        Assert.Equal(-1, pattern.IndexIn([0xAA, 0xBB], int.MaxValue));
        Assert.Equal(0, pattern.IndexIn([0xAA, 0xBB], int.MinValue));
    }

    [Fact]
    public void VectorSearchRetainsOverlapsAndSkipsNearMisses()
    {
        BytePattern pattern = BytePattern.Parse("AA ?? AA");
        byte[] bytes = new byte[1000];
        bytes[63] = 0xAA; // near miss across a vector boundary
        bytes.AsSpan(127, 5).Fill(0xAA);
        bytes.AsSpan(997, 3).Fill(0xAA); // final possible start
        Assert.Equal([127, 128, 129, 997], pattern.AllIndicesIn(bytes));
    }

    [Fact]
    public void FindsLiteralSequence()
    {
        BytePattern pattern = BytePattern.Parse("DE AD BE EF");
        byte[] haystack = [0x00, 0x11, 0xDE, 0xAD, 0xBE, 0xEF, 0x22];

        Assert.Equal(2, pattern.IndexIn(haystack));
    }

    [Fact]
    public void WildcardsMatchAnyByte()
    {
        // The usual shape of a real signature: opcodes fixed, operand bytes wildcarded
        // because they hold an address that moves between builds.
        BytePattern pattern = BytePattern.Parse("8B 0D ?? ?? ?? ?? 85 C9");
        byte[] haystack = [0x90, 0x8B, 0x0D, 0x44, 0x33, 0x22, 0x11, 0x85, 0xC9];

        Assert.Equal(1, pattern.IndexIn(haystack));
    }

    [Fact]
    public void ReturnsMinusOneWhenAbsent()
    {
        BytePattern pattern = BytePattern.Parse("DE AD BE EF");
        Assert.Equal(-1, pattern.IndexIn([0x00, 0x01, 0x02]));
    }

    [Fact]
    public void NearMissDoesNotMatch()
    {
        BytePattern pattern = BytePattern.Parse("DE AD BE EF");
        Assert.Equal(-1, pattern.IndexIn([0xDE, 0xAD, 0xBE, 0xEE]));
    }

    [Fact]
    public void FindsEveryOccurrence()
    {
        BytePattern pattern = BytePattern.Parse("AA BB");
        byte[] haystack = [0xAA, 0xBB, 0x00, 0xAA, 0xBB, 0x00, 0xAA, 0xBB];

        Assert.Equal([0, 3, 6], pattern.AllIndicesIn(haystack));
    }

    [Fact]
    public void LeadingWildcardIsRejected()
    {
        // The scanner fast-rejects on the first byte, so a leading wildcard would make it
        // silently miss matches. Better to refuse the pattern outright.
        Assert.False(BytePattern.TryParse("?? BB CC", out _, out string? error));
        Assert.Contains("wildcard", error!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ZZ")]
    [InlineData("DE AD B")]
    [InlineData("DEAD BEEF")]
    public void MalformedPatternsAreRejected(string text) =>
        Assert.False(BytePattern.TryParse(text, out _, out _));

    [Fact]
    public void MockGameMagicIsParseable()
    {
        // The exact signature profiles/mockgame.json uses.
        BytePattern pattern = BytePattern.Parse("4B 4F 43 4F 4F 50 30 31 01 00 00 00 01 EE FF C0");
        Assert.Equal(16, pattern.Length);

        byte[] block = [.. "KOCOOP01"u8.ToArray(), 0x01, 0x00, 0x00, 0x00, 0x01, 0xEE, 0xFF, 0xC0, 0x77];
        Assert.Equal(0, pattern.IndexIn(block));
    }

    [Fact]
    public void MagicWithoutVersionDoesNotMatch()
    {
        // Guards the reason the version field is in the signature: the bare string also
        // exists in the assembly's own constant data.
        BytePattern pattern = BytePattern.Parse("4B 4F 43 4F 4F 50 30 31 01 00 00 00 01 EE FF C0");
        byte[] stringConstantOnly = [.. "KOCOOP01"u8.ToArray(), 0x00, 0x00, 0x00, 0x00];

        Assert.Equal(-1, pattern.IndexIn(stringConstantOnly));
    }

    [Fact]
    public void MatchStraddlingNothingAtBufferEndIsSafe()
    {
        BytePattern pattern = BytePattern.Parse("AA BB CC");
        Assert.Equal(-1, pattern.IndexIn([0xAA, 0xBB]));
    }
}
