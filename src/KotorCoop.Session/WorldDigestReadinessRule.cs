namespace KotorCoop.Session;

/// <summary>The last load serial used to decide whether a digest module was sampled after
/// the most recently observed world replacement.</summary>
public readonly record struct WorldDigestReadinessState(bool HasSerial, uint LastSerial)
{
    public static WorldDigestReadinessState Initial => new(false, 0);
}

/// <summary>
/// Keeps a standing player row from authorizing a host rejoin on the same publication that
/// first exposes a changed world-load serial. One later successful sample at that serial is
/// enough; no observed away packet is required, so fast same-module reloads remain supported.
/// </summary>
public static class WorldDigestReadinessRule
{
    /// <returns>The updated serial baseline and whether this sample may publish a module.</returns>
    public static (WorldDigestReadinessState Next, bool ModuleReady) Observe(
        WorldDigestReadinessState current, uint loadSerial, bool localReadSucceeded)
    {
        bool serialChanged = current.HasSerial && current.LastSerial != loadSerial;
        var next = new WorldDigestReadinessState(true, loadSerial);
        return (next, localReadSucceeded && !serialChanged);
    }
}
