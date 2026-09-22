namespace KotorCoop.Plugin;

internal static partial class PluginHost
{
    private static bool _korribanScriptObserversReady;

    private static void SetKorribanScriptObserversReady(bool ready) =>
        _korribanScriptObserversReady = ready;

    private static void ObserveKorribanDialogueScript(
        ReadOnlySpan<byte> module,
        ReadOnlySpan<byte> scriptResref,
        uint objectSelf,
        bool isClient,
        uint sequence)
    {
        if (_korribanScriptObserversReady)
        {
            _soloSegment?.ObserveKorribanDialogueScript(module, scriptResref, objectSelf, isClient, sequence);
        }
    }

    private static void ObserveKorribanAreaScript(
        ReadOnlySpan<byte> module,
        ReadOnlySpan<byte> scriptResref,
        uint objectSelf,
        bool isClient,
        uint sequence)
    {
        if (_korribanScriptObserversReady)
        {
            _soloSegment?.ObserveKorribanAreaScript(module, scriptResref, objectSelf, isClient, sequence);
        }
    }
}
