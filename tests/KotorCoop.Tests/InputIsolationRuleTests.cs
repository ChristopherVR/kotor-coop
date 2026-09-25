using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure half of coop.ini <c>isolate_real_input</c> (docs/research/isolated-rig.md): the
/// arithmetic <see cref="DirectInputKeyboardHook"/>/<see cref="DirectInputMouseHook"/> apply to a
/// real DirectInput read has no test double for the unsafe pointer work itself, so it is exercised
/// live instead (see the doc's own recipe) — but the decision of WHAT to keep/drop is ordinary
/// arithmetic and is tested directly here.
/// </summary>
public class InputIsolationRuleTests
{
    [Fact]
    public void VisibleRealEventCount_NotIsolating_PassesRealCountThrough()
    {
        Assert.Equal(0u, InputIsolationRule.VisibleRealEventCount(false, 0u));
        Assert.Equal(3u, InputIsolationRule.VisibleRealEventCount(false, 3u));
        Assert.Equal(uint.MaxValue, InputIsolationRule.VisibleRealEventCount(false, uint.MaxValue));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(255u)]
    public void VisibleRealEventCount_Isolating_AlwaysZero(uint realCount)
    {
        Assert.Equal(0u, InputIsolationRule.VisibleRealEventCount(true, realCount));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0x80, 0, 0x80)] // real key down, no synthetic hold -> passes through
    [InlineData(0, 0x80, 0x80)] // no real key, synthetic hold -> passes through
    [InlineData(0x80, 0x80, 0x80)] // both -> still down
    public void MaskKeyboardStateByte_NotIsolating_OrsRealAndHeld(byte realByte, byte heldByte, byte expected)
    {
        Assert.Equal(expected, InputIsolationRule.MaskKeyboardStateByte(false, realByte, heldByte));
    }

    [Fact]
    public void MaskKeyboardStateByte_Isolating_DropsReal_KeepsSynthetic()
    {
        // A real key press must never survive isolation...
        Assert.Equal((byte)0, InputIsolationRule.MaskKeyboardStateByte(true, realByte: 0x80, heldByte: 0));

        // ...but the plugin's own synthetic key (queued via `input key`/the gamepad layer) must
        // still show up, regardless of what the real keyboard reports for that same byte.
        Assert.Equal((byte)0x80, InputIsolationRule.MaskKeyboardStateByte(true, realByte: 0, heldByte: 0x80));
        Assert.Equal((byte)0x80, InputIsolationRule.MaskKeyboardStateByte(true, realByte: 0x80, heldByte: 0x80));
    }

    [Fact]
    public void ShouldNeutralizeMouseState_OnlyWhenIsolatingAndSizeMatchesDIMOUSESTATE()
    {
        Assert.True(InputIsolationRule.ShouldNeutralizeMouseState(isolate: true, size: 0x10));
        Assert.False(InputIsolationRule.ShouldNeutralizeMouseState(isolate: false, size: 0x10));

        // DIMOUSESTATE2 (0x14, 8 buttons) or any other size is not the shape this engine ever
        // requests (mouse-cursor-input.md: c_dfDIMouse, cbData 0x10) - leave it alone rather
        // than guess at an unrecognised shape.
        Assert.False(InputIsolationRule.ShouldNeutralizeMouseState(isolate: true, size: 0x14));
        Assert.False(InputIsolationRule.ShouldNeutralizeMouseState(isolate: true, size: 0));
    }
}
