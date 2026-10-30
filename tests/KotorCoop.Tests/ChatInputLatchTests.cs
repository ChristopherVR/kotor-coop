using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class ChatInputLatchTests
{
    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, false, false, true)]
    public void EnterOwnedByAnotherContextCannotBecomeChatWhenItCloses(
        bool console, bool menu, bool conversation, bool outsideWorld)
    {
        var latch = new ChatInputLatch();
        Assert.False(latch.ShouldOpen(true, console, !outsideWorld, conversation, menu));
        Assert.False(latch.ShouldOpen(true, false, true, false, false));
        Assert.False(latch.ShouldOpen(false, false, true, false, false));
        Assert.True(latch.ShouldOpen(true, false, true, false, false));
    }

    [Fact]
    public void WorldEnterOpensOncePerPress()
    {
        var latch = new ChatInputLatch();
        Assert.True(latch.ShouldOpen(true, false, true, false, false));
        Assert.False(latch.ShouldOpen(true, false, true, false, false));
        Assert.False(latch.ShouldOpen(false, false, true, false, false));
        Assert.True(latch.ShouldOpen(true, false, true, false, false));
    }

    [Fact]
    public void BrowserConfirmBeforeTheChatFrameCannotLeakEnterIntoChat()
    {
        var latch = new ChatInputLatch();
        // Main-loop browser handles Enter and closes before SwapBuffers/chat gets a
        // frame. Chat never observed the browser while the key was down.
        latch.ConsumeCurrentPress();
        Assert.False(latch.ShouldOpen(true, false, true, false, false));
        Assert.False(latch.ShouldOpen(true, false, true, false, false));
        Assert.False(latch.ShouldOpen(false, false, true, false, false));
        Assert.True(latch.ShouldOpen(true, false, true, false, false));
    }
}
