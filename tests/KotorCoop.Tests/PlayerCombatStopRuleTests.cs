using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PlayerCombatStopRuleTests
{
    [Fact]
    public void AllVerifiedCancelCombatBytesAreRequiredIncludingArgumentAndTargetClear()
    {
        byte[] code = PlayerCombatStopRule.CancelCombatBytes.ToArray();
        Assert.Equal(125, code.Length);
        Assert.Equal(new byte[] { 0xC2, 0x04, 0x00 }, code[^3..]);
        Assert.True(PlayerCombatStopRule.CodeMatches(code));
        Assert.False(PlayerCombatStopRule.CodeMatches(code[..^1]));
        for (int i = 0; i < code.Length; i++)
        {
            byte saved = code[i];
            code[i] ^= 1;
            Assert.False(PlayerCombatStopRule.CodeMatches(code));
            code[i] = saved;
        }
    }

    [Fact]
    public void StaleBodyOtherCreatureAndClientTwinCannotBeCancelledAsLocalPlayer()
    {
        Assert.True(PlayerCombatStopRule.OwnCurrentBody(10, 10, 10, 0x7470C8, 0xFFFFFFFF, 0x7FFFFFFF));
        Assert.False(PlayerCombatStopRule.OwnCurrentBody(10, 20, 10, 0x7470C8, 0x7FFFFFFF, 0x7FFFFFFF));
        Assert.False(PlayerCombatStopRule.OwnCurrentBody(10, 10, 20, 0x7470C8, 0x7FFFFFFF, 0x7FFFFFFF));
        Assert.False(PlayerCombatStopRule.OwnCurrentBody(10, 10, 10, 0x74F5F8, 0x7FFFFFFF, 0x7FFFFFFF));
        Assert.False(PlayerCombatStopRule.OwnCurrentBody(10, 10, 10, 0x7470C8, 0x156, 0x7FFFFFFF));
        Assert.False(PlayerCombatStopRule.OwnCurrentBody(10, 10, 10, 0x7470C8, 0x7F000000, 0x7F000000));
    }

    [Fact]
    public void QueueClearCannotAcknowledgeWhileActiveRoundStillHasTarget()
    {
        Assert.False(PlayerCombatStopRule.ActiveTargetCleared(0x138));
        Assert.False(PlayerCombatStopRule.ActiveTargetCleared(0));
        Assert.True(PlayerCombatStopRule.ActiveTargetCleared(0x7F000000));
    }
}
