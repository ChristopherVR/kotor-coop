namespace KotorCoop.Protocol;

/// <summary>
/// Scope marker carried by party-pool staging requests. The shared cheat slot has no spare
/// integer dedicated to load identity, so pool requests use Target for the captured world-load
/// serial and the otherwise-unused X field for this exact marker. The plugin must reject an
/// unmarked or stale request before resolving or mutating any engine object.
/// </summary>
public static class PartyPoolMailboxFence
{
    public const float Marker = 1f;

    public static bool IsCurrent(float marker, uint requestLoadSerial, uint currentLoadSerial) =>
        marker == Marker && requestLoadSerial == currentLoadSerial;
}
