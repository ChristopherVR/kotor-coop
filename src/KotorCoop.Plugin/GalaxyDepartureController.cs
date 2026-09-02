namespace KotorCoop.Plugin;

/// <summary>Outcomes of a galaxy-departure replay request, logged and echoed to the agent via
/// <see cref="SharedBlockView.GalaxyDepartStatus"/>.</summary>
internal enum GalaxyDepartStatusCode : uint
{
    None = 0,
    Departed = 1,
    EngineRefused = 2,
    BadPlanet = 3,
}

/// <summary>
/// The follower half of the galaxy-map departure relay (SharedBlock.GalaxyDeparture.cs): acts
/// on the agent's "replay a peer's departure to planet n" request by running
/// <see cref="Odyssey.DepartToPlanet"/> — the same four live-proven staging primitives the
/// leader's accept click exercised, in one frame — and acks the request with what happened.
///
/// The leader half needs no controller: <see cref="GalaxyMapConfirmHook"/> publishes the
/// departure at the click itself, because nothing per-frame runs again on that instance until
/// the whole scripted leg is over (the main thread is frozen through movie + StartNewModule).
///
/// Runs from the same <see cref="PluginHost.OnFrame"/> schedule <see cref="CheatController"/>
/// does — the `galaxyaccept` verb ran the identical accept dispatch from there three times
/// live without incident (docs/research/galaxy-map-and-party-select.md, 2026-09-03).
/// </summary>
internal sealed unsafe class GalaxyDepartureController(SharedBlockView block)
{
    private uint _appliedRequestSerial;

    /// <summary>Module change: a request filed for the OLD world must not fire in the new one
    /// — the agent re-decides against the module it now sees.</summary>
    public void Invalidate() => _appliedRequestSerial = block.GalaxyDepartRequestSerial;

    public void OnFrame()
    {
        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        uint serial = block.GalaxyDepartRequestSerial;
        if (serial == 0 || serial == _appliedRequestSerial)
        {
            return;
        }

        _appliedRequestSerial = serial;
        int planet = block.GalaxyDepartRequestPlanet;

        GalaxyDepartStatusCode status;
        if (planet is < 0 or > 0x0F)
        {
            status = GalaxyDepartStatusCode.BadPlanet;
            PluginHost.Log($"galaxy-departure: refused replay request #{serial} - planet {planet} out of range");
        }
        else
        {
            bool departed = Odyssey.DepartToPlanet(planet, out string detail);
            status = departed ? GalaxyDepartStatusCode.Departed : GalaxyDepartStatusCode.EngineRefused;
            PluginHost.Log(departed
                ? $"galaxy-departure: replayed a peer's departure #{serial} - {detail}"
                : $"galaxy-departure: could not replay departure #{serial} - {detail}");
        }

        // Status first, applied last: the agent reads applied, then status, so a torn read
        // can only ever show "not handled yet".
        block.GalaxyDepartStatus = (uint)status;
        block.GalaxyDepartApplied = serial;
    }
}
