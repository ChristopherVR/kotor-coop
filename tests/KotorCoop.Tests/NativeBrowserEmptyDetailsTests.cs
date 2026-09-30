using KotorCoop.Plugin;
using Xunit;
namespace KotorCoop.Tests;
public sealed class NativeBrowserEmptyDetailsTests
{
    [Fact]
    public void MissingServerPortraitsProduceBlankEngineReferencesInsteadOfNullLengthFault()
    {
        // Placeholder/address selection has no published details: the original browser
        // handed these uninitialised array entries to its engine-image encoder.
        string?[] missingPortraits = new string?[3];
        byte[] reference = Enumerable.Repeat((byte)0xCC, 16).ToArray();
        foreach (string? face in missingPortraits)
        {
            NativeResRefEncoding.Encode(face, reference);
            Assert.All(reference, b => Assert.Equal(0, b));
        }
        NativeResRefEncoding.Encode("po_pfhc1", reference);
        Assert.Equal("po_pfhc1", System.Text.Encoding.ASCII.GetString(reference, 0, 8));
        NativeResRefEncoding.Encode(string.Empty, reference); // blanking previous real details
        Assert.All(reference, b => Assert.Equal(0, b));
    }
    [Fact]
    public void ResourceReferenceUsesFixedEngineWidthAndPrintableAscii()
    {
        byte[] reference = new byte[16];
        NativeResRefEncoding.Encode("1234567890abcdefghijkl", reference);
        Assert.Equal("1234567890abcdef", System.Text.Encoding.ASCII.GetString(reference));
        NativeResRefEncoding.Encode("a\u00e9b", reference);
        Assert.Equal(new byte[] { 97, 0, 98 }, reference[..3]);
        Assert.All(reference[3..], b => Assert.Equal(0, b));
    }
}
