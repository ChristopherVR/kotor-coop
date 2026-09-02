namespace KotorCoop.Plugin;

// Galaxy-map departure relay (shared block version 67, region 0x4C00..0x4C18). See
// SharedBlock.GalaxyDeparture.cs for the layout and why the accept click is the only
// observable moment of an interplanetary leg.
internal sealed unsafe partial class SharedBlockView
{
    /// <summary>
    /// Plugin -&gt; agent. Publishes "this instance's own player just confirmed a galaxy-map
    /// destination" — the planet first, the serial LAST, so the agent never pairs a new
    /// serial with the previous leg's planet. Called from
    /// <see cref="GalaxyMapConfirmHook"/>'s ALLOW path only.
    /// </summary>
    public void PublishGalaxyDeparture(int planet)
    {
        *I32(SharedBlock.OffsetGalaxyDeparturePlanet) = planet;
        *U32(SharedBlock.OffsetGalaxyDepartureSerial) = *U32(SharedBlock.OffsetGalaxyDepartureSerial) + 1;
    }

    /// <summary>Plugin -&gt; agent. The raw departure counter (diagnostics, `galaxymap stats`).</summary>
    public uint GalaxyDepartureSerial => *U32(SharedBlock.OffsetGalaxyDepartureSerial);

    /// <summary>Agent -&gt; plugin. A replay request is pending while this is ahead of
    /// <see cref="GalaxyDepartApplied"/>.</summary>
    public uint GalaxyDepartRequestSerial => *U32(SharedBlock.OffsetGalaxyDepartRequestSerial);

    /// <summary>Agent -&gt; plugin. The planet the pending request asks this instance to depart to.</summary>
    public int GalaxyDepartRequestPlanet => *I32(SharedBlock.OffsetGalaxyDepartRequestPlanet);

    /// <summary>Plugin -&gt; agent. Echoes <see cref="GalaxyDepartRequestSerial"/> once the
    /// request has been acted on. Set AFTER <see cref="GalaxyDepartStatus"/>.</summary>
    public uint GalaxyDepartApplied
    {
        get => *U32(SharedBlock.OffsetGalaxyDepartApplied);
        set => *U32(SharedBlock.OffsetGalaxyDepartApplied) = value;
    }

    /// <summary>Plugin -&gt; agent. <see cref="GalaxyDepartStatusCode"/> of the last request.</summary>
    public uint GalaxyDepartStatus
    {
        get => *U32(SharedBlock.OffsetGalaxyDepartStatus);
        set => *U32(SharedBlock.OffsetGalaxyDepartStatus) = value;
    }
}
