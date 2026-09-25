namespace KotorCoop.Plugin;

/// <summary>
/// Pure decision logic behind coop.ini <c>isolate_real_input</c> (docs/research/isolated-rig.md
/// "Input isolation"): when the isolated rig runs two invisible, click-through KOTOR windows on
/// the user's own monitor while they play something else, NOTHING the user's real keyboard or
/// mouse does may reach either game — while the plugin's own synthetic input (the `input`/
/// `gamepad` console verbs, <see cref="InputInjectionController"/>, <see cref="GamepadController"/>)
/// must keep working exactly as before.
///
/// Kept separate from <see cref="DirectInputKeyboardHook"/>/<see cref="DirectInputMouseHook"/> —
/// which do the actual unsafe DirectInput proxy work and cannot run outside the game process —
/// so this arithmetic is unit-tested directly rather than only exercised live.
/// </summary>
internal static class InputIsolationRule
{
    /// <summary>
    /// How many of the REAL buffered device events (keyboard <c>GetDeviceData</c>) a caller may
    /// still see. Isolating drops all of them (0) regardless of what the real device reported;
    /// the plugin's own queued synthetic events are appended separately afterwards and are
    /// never affected by this.
    /// </summary>
    public static uint VisibleRealEventCount(bool isolate, uint realCount) => isolate ? 0u : realCount;

    /// <summary>
    /// One byte of a 256-byte DIK immediate-state read (<c>GetDeviceState</c>). Isolating drops
    /// whatever the real keyboard reported for this key and shows only the plugin's own held
    /// (synthetic) state; not isolating ORs them together exactly as before isolation existed.
    /// </summary>
    public static byte MaskKeyboardStateByte(bool isolate, byte realByte, byte heldByte) =>
        isolate ? heldByte : (byte)(realByte | heldByte);

    /// <summary>
    /// Whether a mouse <c>DIMOUSESTATE</c> read (<paramref name="size"/> 0x10 — three LONGs
    /// lX/lY/lZ plus 4 button bytes) should be replaced with an all-neutral state. The engine's
    /// only consumer of this device, <c>CExoInputInternal::UpdateMouseDelta</c>, turns lX/lY
    /// straight into the camera look axes (docs/research/mouse-cursor-input.md's answer sheet),
    /// so an all-neutral state means "the real mouse did not move and no button is down" — the
    /// isolated rig's whole point. A size other than the plain <c>DIMOUSESTATE</c> the engine
    /// actually requests is left alone (nothing here recognises it, so nothing here should touch
    /// it).
    /// </summary>
    public static bool ShouldNeutralizeMouseState(bool isolate, uint size) => isolate && size == 0x10;
}
