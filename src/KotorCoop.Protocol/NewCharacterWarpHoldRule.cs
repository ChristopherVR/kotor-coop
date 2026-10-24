namespace KotorCoop.Protocol;

/// <summary>
/// Whether a first-time joiner's follow warp must wait for its forced character creation.
///
/// 2026-10-24 (Steam joiner against a remote host): the new game came up on the Endar Spire and
/// the session warped it to the host's tar_m02ae 6.7 s later, before the new character had been
/// saved ("newchar: save request 1 refused - no world loaded"). The save then ran inside a
/// fresh-game copy of the host's module, the host reported W_GLOBALS / W_GIT / W_SAVE, and the
/// adoption load that followed replaced that world anyway. The warp was an extra module load
/// that bought nothing and broke the save it raced.
///
/// So the warp is held while the capture is armed, and after the capture until the adoption
/// load is applied (the menu-load serial moves) or <see cref="MaxHoldAfterCaptureMs"/> passes,
/// so a capture whose adoption never comes cannot strand the joiner on the Endar Spire. Once
/// released, the adopted world is normally the host's module already and the request is
/// acknowledged without a load.
/// </summary>
public static class NewCharacterWarpHoldRule
{
    public const long MaxHoldAfterCaptureMs = 45000;

    /// <param name="captureArmed">The new-character capture is waiting for its save.</param>
    /// <param name="capturedAtMs">Tick of the capture, or 0 if none happened.</param>
    /// <param name="menuLoadAppliedAtCapture">The applied menu-load serial when it was captured.</param>
    /// <param name="menuLoadApplied">The applied menu-load serial now.</param>
    /// <param name="nowMs">Current tick.</param>
    public static bool Holds(
        bool captureArmed, long capturedAtMs, uint menuLoadAppliedAtCapture, uint menuLoadApplied, long nowMs)
    {
        if (captureArmed)
        {
            return true;
        }

        if (capturedAtMs <= 0)
        {
            return false;
        }

        return menuLoadApplied == menuLoadAppliedAtCapture && nowMs - capturedAtMs < MaxHoldAfterCaptureMs;
    }
}
