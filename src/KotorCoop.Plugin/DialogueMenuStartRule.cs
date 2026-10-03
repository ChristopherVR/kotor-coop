namespace KotorCoop.Plugin;

internal static class DialogueMenuStartRule
{
    public static bool Defer(bool coopKeepAlive, bool fullScreenGui, bool conversation)
        => coopKeepAlive && fullScreenGui && !conversation;
}
