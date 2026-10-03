using System.Text;
using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class NativeSaveStringTests
{
    [Fact]
    public void ReusedQuickSaveBufferContainsShorterCoopNameBeforeItsReportedEnd()
    {
        byte[] buffer = Encoding.ASCII.GetBytes("SAVES:000000 - QUICKSAVE\0\0");
        Encoding.ASCII.GetBytes("SAVES:000003 - Co-op\0").CopyTo(buffer, 0);
        Assert.Equal("SAVES:000003 - Co-op", NativeSaveCompletionHook.DecodeString(buffer));
    }

    [Fact]
    public void BytesAfterTerminatorAreNotPartOfTheNativePath()
        => Assert.Equal("SAVES:000003 - Co-op", NativeSaveCompletionHook.DecodeString(
            [.. Encoding.ASCII.GetBytes("SAVES:000003 - Co-op"), 0, 0xFF, 1]));

    [Fact]
    public void NonPrintableBytesBeforeTerminatorStillRefuseThePath()
        => Assert.Equal("", NativeSaveCompletionHook.DecodeString(
            Encoding.ASCII.GetBytes("SAVES:000003 - Co\nop\0")));
}
