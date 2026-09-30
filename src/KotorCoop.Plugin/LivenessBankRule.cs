namespace KotorCoop.Plugin;
internal static class LivenessBankRule
{
    public static bool CanRead(uint sequence, uint expectedSerial, uint currentSerial,
        uint expectedEpoch, uint currentEpoch) =>
        sequence != 0 && (sequence & 1) == 0 && expectedSerial == currentSerial
        && expectedEpoch != 0 && expectedEpoch == currentEpoch;
}
