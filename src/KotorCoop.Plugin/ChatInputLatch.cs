namespace KotorCoop.Plugin;

/// <summary>One Enter hold belongs to one UI context, even if that context closes mid-hold.</summary>
internal sealed class ChatInputLatch
{
    private bool _wasDown;
    private bool _armed = true;

    public bool ShouldOpen(bool down, bool consoleOpen, bool inGame, bool conversation, bool guiOpen)
    {
        bool rising = down && !_wasDown;
        _wasDown = down;
        if (!down)
        {
            _armed = true;
            return false;
        }

        if (consoleOpen || !inGame || conversation || guiOpen)
        {
            _armed = false;
            return false;
        }

        if (!_armed || !rising)
            return false;

        _armed = false;
        return true;
    }
}
