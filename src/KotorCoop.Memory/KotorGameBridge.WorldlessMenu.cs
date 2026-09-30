namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    public bool IsVerifiedWorldlessMenu()
    {
        if (_pluginBlock == 0 || !TryReadWorldLoadSerial(out uint serial)
            || !TryReadLivenessEpoch(out uint epoch)
            || !_memory.TryReadPointer(EngineGlobals.AppManagerPtr, out nint app) || app == 0
            || !_memory.TryReadPointer(app + 8, out nint server) || server != 0
            || !EngineGlobals.TryGetClientInternal(_memory, out nint client)
            || !_memory.TryReadPointer(client + 0x14C, out nint channel) || channel == 0
            || !_memory.TryReadPointer(client + 0x280, out nint menu) || menu == 0
            || !_memory.TryReadUInt32(_pluginBlock + 0x9A4, out uint requested)
            || !_memory.TryReadUInt32(_pluginBlock + 0x9A8, out uint applied) || requested != applied
            || !_memory.TryReadUInt32(_pluginBlock + BlockEnginePlayerPointer, out uint player) || player != 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockEnginePlayerId, out uint id) || id != 0)
            return false;
        return _memory.TryReadPointer(EngineGlobals.AppManagerPtr, out nint afterApp) && afterApp == app
            && _memory.TryReadPointer(app + 8, out nint afterServer) && afterServer == 0
            && _memory.TryReadPointer(client + 0x280, out nint afterMenu) && afterMenu == menu
            && _memory.TryReadUInt32(_pluginBlock + 0x9A4, out uint afterRequest) && afterRequest == requested
            && TryReadWorldLoadSerial(out uint afterSerial) && afterSerial == serial
            && TryReadLivenessEpoch(out uint afterEpoch) && afterEpoch == epoch;
    }
}
