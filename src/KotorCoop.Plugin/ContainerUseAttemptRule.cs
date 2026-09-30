namespace KotorCoop.Plugin;

internal static class ContainerUseAttemptRule
{
    // A close may be observed before the poll ever sees its corresponding open attempt.
    // Consume that attempt now; otherwise stale canonical Open can complete it again.
    public static uint ConsumeClosedAttempt(uint seenSerial, uint attemptId, uint attemptSerial,
        uint closedId, uint closedAttemptSerial) =>
        attemptId != 0 && attemptId == closedId && attemptSerial != 0
            && attemptSerial == closedAttemptSerial ? attemptSerial : seenSerial;

    public static bool IsUnseen(uint serial, uint seenSerial) => serial != 0 && serial != seenSerial;
}
