using KotorCoop.Plugin;
using Xunit;
namespace KotorCoop.Tests;
public class GameplayKeyboardLockTests
{
    [Fact]
    public void LockPurgesWrappedPendingRingWithoutLosingReleasesOrConsole()
    {
        (byte Key,bool Down)[] pending=[(0x11,true),(0x11,false),(0x29,true),(0x20,true)];
        int kept=GameplayKeyboardLock.PurgeQueued(pending,3,4);
        Assert.Equal(2,kept);
        Assert.Equal(((byte)0x11,false),pending[3]);
        Assert.Equal(((byte)0x29,true),pending[0]);
    }
    [Fact]
    public void LockReleasesHeldMovementAndDropsNewPressWithoutReplay()
    {
        var rule = new GameplayKeyboardLock();
        uint[] buffer = new uint[15];
        buffer[0]=0x11; buffer[1]=0x80; // W before lock
        Assert.Equal(1, rule.Apply(buffer,1,false,1));
        buffer[0]=0x20; buffer[1]=0x80; // D during lock
        Assert.Equal(1,rule.Apply(buffer,1,true,2));
        Assert.Equal(0x11u,buffer[0]); Assert.Equal(0u,buffer[1]);
        Assert.Equal(0,rule.Apply(buffer,0,false,3));
        buffer[0]=0x11; buffer[1]=0x80;
        Assert.Equal(1,rule.Apply(buffer,1,false,4));
    }
    [Fact]
    public void SmallReadDrainsAllPreexistingKeysAcrossPolls()
    {
        var rule=new GameplayKeyboardLock(); uint[] buffer=new uint[10];
        buffer[0]=0x11;buffer[1]=0x80;buffer[5]=0x20;buffer[6]=0x80;
        Assert.Equal(2,rule.Apply(buffer,2,false,1));
        Assert.Equal(1,rule.Apply(buffer.AsSpan(0,5),0,true,2));
        Assert.Equal(1,rule.Apply(buffer.AsSpan(0,5),0,true,3));
        Assert.Equal(0,rule.Apply(buffer.AsSpan(0,5),0,true,4));
    }
    [Theory]
    [InlineData(0x01)] [InlineData(0x29)] [InlineData(0x1C)]
    public void EscapeConsoleAndChatRemainAvailable(int key)
    {
        var rule=new GameplayKeyboardLock(); uint[] buffer=[(uint)key,0x80,0,0,0];
        Assert.Equal(1,rule.Apply(buffer,1,true,1));
        buffer[1]=0;
        Assert.Equal(1,rule.Apply(buffer,1,true,2));
    }
}
