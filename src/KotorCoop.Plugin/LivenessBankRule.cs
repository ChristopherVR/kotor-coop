namespace KotorCoop.Plugin;
internal static class LivenessBankRule
{
    public static bool IsSamePublication(uint selected, uint current, uint expectedSerial,
        uint currentSerial, uint expectedEpoch, uint currentEpoch) =>
        selected == current && CanRead(current, expectedSerial, currentSerial, expectedEpoch, currentEpoch);
    public static bool CanRead(uint sequence, uint expectedSerial, uint currentSerial,
        uint expectedEpoch, uint currentEpoch) =>
        sequence != 0 && (sequence & 1) == 0 && expectedSerial == currentSerial
        && expectedEpoch != 0 && expectedEpoch == currentEpoch;
}
