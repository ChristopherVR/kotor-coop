namespace KotorCoop.Plugin;

internal static class DoorApplyCompletionRule
{
    internal static bool IsComplete(byte requested, byte current, byte pending) =>
        requested <= 3 && current == requested && pending == requested;
}
