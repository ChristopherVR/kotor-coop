// Module travel: requesting a warp through the plugin's engine call, and reading back
// transition movies/announcements and mid-module movie play requests.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ---- module travel ---------------------------------------------------------

    /// <summary>Travel needs the in-process plugin: the warp is an engine call.</summary>
    public bool SupportsTravel => _pluginBlock != 0;

    /// <summary>
    /// Asks the plugin to move this instance to another module through the engine's own
    /// move-to-module path. Fire-and-forget: the plugin flushes everything it spawned
    /// first (so nothing is serialised into the stored module on the way out), then the
    /// engine's MainLoop performs the transition exactly as the shipped `warp` cheat does.
    /// </summary>
    public bool TryRequestTravel(string module, string waypoint = "", IReadOnlyList<string>? movies = null)
    {
        if (_pluginBlock == 0 || string.IsNullOrEmpty(module) || module.Length >= BlockTravelNameLength)
        {
            return false;
        }

        bool ok = _memory.TryWriteAsciiString(_pluginBlock + BlockTravelModule, module, BlockTravelNameLength);
        ok &= _memory.TryWriteAsciiString(_pluginBlock + BlockTravelWaypoint, waypoint, BlockTravelNameLength);

        // The cinematic this warp should carry. The COUNT IS ALWAYS WRITTEN, including the
        // zero: without that, a previous request's movies would still be sitting in the block
        // and would ride along with the next unrelated transition — the same "left armed"
        // hazard the staging arm expires to avoid, but with no expiry to save it.
        int count = 0;
        if (movies is not null)
        {
            for (int i = 0; i < movies.Count && count < BlockCinematicCapacity; i++)
            {
                string name = movies[i];
                if (string.IsNullOrEmpty(name) || name.Length >= BlockCinematicNameLength)
                {
                    continue;
                }

                ok &= _memory.TryWriteAsciiString(
                    _pluginBlock + BlockTravelMovieNames + (count * BlockCinematicNameLength),
                    name,
                    BlockCinematicNameLength);
                count++;
            }
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockTravelMovieCount, count);

        // Serial last, so the plugin never observes a half-written request. Continue from
        // whatever the block already holds: the plugin outlives the agent, and restarting
        // from zero would replay whichever request the previous agent left behind.
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockTravelSerial, out uint serial);
        ok &= _memory.TryWrite(_pluginBlock + BlockTravelSerial, serial + 1);
        return ok;
    }

    /// <inheritdoc />
    public bool TryReadTransitionMovies(out uint serial, out string[] movies)
    {
        serial = 0;
        movies = [];

        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockCinematicSerial, out serial)
            || serial == 0
            || !_memory.TryReadInt32(_pluginBlock + BlockCinematicCount, out int count)
            || count is <= 0 or > BlockCinematicCapacity)
        {
            return false;
        }

        List<string> names = new(count);
        for (int i = 0; i < count; i++)
        {
            if (_memory.TryReadAsciiString(
                    _pluginBlock + BlockCinematicNames + (i * BlockCinematicNameLength),
                    BlockCinematicNameLength,
                    out string name)
                && name.Length > 0)
            {
                names.Add(name);
            }
        }

        movies = [.. names];
        return movies.Length > 0;
    }

    /// <inheritdoc />
    public bool TryReadTransitionAnnounce(out uint serial, out string module, out string waypoint)
    {
        serial = 0;
        module = string.Empty;
        waypoint = string.Empty;

        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockTransitionAnnounceSerial, out serial)
            || serial == 0)
        {
            return false;
        }

        if (!_memory.TryReadAsciiString(
                _pluginBlock + BlockTransitionAnnounceModule, BlockTransitionAnnounceNameLength, out module)
            || module.Length == 0)
        {
            return false;
        }

        _memory.TryReadAsciiString(
            _pluginBlock + BlockTransitionAnnounceWaypoint, BlockTransitionAnnounceNameLength, out waypoint);
        return true;
    }

    /// <inheritdoc />
    public bool TryArmClientDoorRefusal(ClientDoorRefusalReason reason)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        // The same slot the old boolean "armed" flag used - nonzero still means armed, so this
        // is a value-range widening (None/HostMustLead/HostBusy), not a new field.
        return _memory.TryWrite(_pluginBlock + BlockClientDoorRefusalArmed, (uint)reason);
    }

    // ---- mid-module movie relay -------------------------------------------------

    /// <inheritdoc />
    public bool TryReadMidModuleMovie(out uint serial, out string resRef)
    {
        serial = 0;
        resRef = string.Empty;

        if (_pluginBlock == 0
            || !_memory.TryReadUInt32(_pluginBlock + BlockMidModuleMovieSerial, out serial)
            || serial == 0)
        {
            return false;
        }

        return _memory.TryReadAsciiString(
                _pluginBlock + BlockMidModuleMovieResRef, BlockMidModuleMovieNameLength, out resRef)
            && resRef.Length > 0;
    }

    /// <inheritdoc />
    public bool TryRequestMidModulePlay(string resRef)
    {
        if (_pluginBlock == 0 || string.IsNullOrEmpty(resRef) || resRef.Length >= BlockMidModuleMovieNameLength)
        {
            return false;
        }

        bool ok = _memory.TryWriteAsciiString(
            _pluginBlock + BlockMidModulePlayRequestResRef, resRef, BlockMidModuleMovieNameLength);

        // Serial last, so the plugin never observes a half-written request. Continue from
        // whatever the block already holds, the same convention TryRequestTravel uses.
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockMidModulePlayRequestSerial, out uint serial);
        ok &= _memory.TryWrite(_pluginBlock + BlockMidModulePlayRequestSerial, serial + 1);
        return ok;
    }

    /// <inheritdoc />
    public bool TryReadMidModulePlayStatus(out uint applied, out uint status)
    {
        applied = 0;
        status = 0;

        return _pluginBlock != 0
            && _memory.TryReadUInt32(_pluginBlock + BlockMidModulePlayApplied, out applied)
            && _memory.TryReadUInt32(_pluginBlock + BlockMidModulePlayStatus, out status);
    }

    // ---- galaxy-map departure relay ---------------------------------------------

    /// <inheritdoc />
    public bool TryReadGalaxyDeparture(out uint serial, out int planet)
    {
        serial = 0;
        planet = -1;

        // Serial first, planet second - the reverse of the plugin's write order - so a torn
        // read pairs a serial with the planet written before it, never a newer one.
        return _pluginBlock != 0
            && _memory.TryReadUInt32(_pluginBlock + BlockGalaxyDepartureSerial, out serial)
            && serial != 0
            && _memory.TryReadInt32(_pluginBlock + BlockGalaxyDeparturePlanet, out planet);
    }

    /// <inheritdoc />
    public bool TryRequestGalaxyDepart(int planet)
    {
        if (_pluginBlock == 0 || planet is < 0 or > 15)
        {
            return false;
        }

        bool ok = _memory.TryWrite(_pluginBlock + BlockGalaxyDepartRequestPlanet, planet);

        // Serial last, so the plugin never observes a half-written request. Continue from
        // whatever the block already holds, the same convention TryRequestMidModulePlay uses.
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockGalaxyDepartRequestSerial, out uint serial);
        ok &= _memory.TryWrite(_pluginBlock + BlockGalaxyDepartRequestSerial, serial + 1);
        return ok;
    }

    /// <inheritdoc />
    public bool TryReadGalaxyDepartStatus(out uint applied, out uint status)
    {
        applied = 0;
        status = 0;

        return _pluginBlock != 0
            && _memory.TryReadUInt32(_pluginBlock + BlockGalaxyDepartApplied, out applied)
            && _memory.TryReadUInt32(_pluginBlock + BlockGalaxyDepartStatus, out status);
    }

}
