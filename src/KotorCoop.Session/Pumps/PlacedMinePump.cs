using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// The agent half of party-placed floor-mine replication (docs/research/mines.md §14, coop.ini
/// <c>placed_mine_sync</c>, default OFF, NEEDS-LIVE). Every tick:
/// <list type="number">
/// <item>host: tell the authority which world the host is in (a change drops the old set);</item>
/// <item>drain the plugin's capture ring — a party member here placed a mine — and publish it
/// (host: straight into the authority; client: a reliable intent);</item>
/// <item>drain the gone ring — a mapped mine is gone here — and publish the serial;</item>
/// <item>write the canonical set for this module to the plugin as the roster level (the plugin
/// removes a mapped mine that left it);</item>
/// <item>drive one spawn-mailbox request at a time, ascending serial order
/// (<see cref="PlacedMineReplicaPlanner"/>).</item>
/// </list>
/// All plugin traffic is scoped to the plugin's placed-mine world token; a new world resets every
/// per-world set here, so nothing filed for the old world can act in the new one.
/// </summary>
internal sealed class PlacedMinePump(IGameBridge bridge, ICoopSession session, AgentSettings settings, Action<string> log)
{
    private const long AckTimeoutMs = 3000;
    private const long RetryDelayMs = 500;
    private const int MaxAttempts = 20;

    private bool _baselined;
    private uint _captureCursor;
    private uint _goneCursor;

    private uint _world;
    private readonly HashSet<uint> _settled = [];
    private readonly HashSet<uint> _ownCaptures = [];
    private readonly HashSet<uint> _ownVanished = [];
    private readonly HashSet<uint> _goneReported = [];

    private uint _rosterWrittenGeneration;
    private uint _rosterWrittenWorld;
    private uint _rosterWrittenModule;

    private uint _inFlightRequest;
    private uint _inFlightSerial;
    private long _inFlightSinceMs;
    private uint _retrySerial;
    private int _attempts;
    private long _retryAtMs;

    public void Pump(in PlayerState local)
    {
        if (!settings.PlacedMineSync || !bridge.SupportsPlacedMineSync || !session.IsConnected)
        {
            return;
        }

        if (!_baselined)
        {
            // The block can outlive an earlier session in the same process: start at its heads.
            if (!bridge.TryReadPlacedMineHeads(out _captureCursor, out _goneCursor))
            {
                return;
            }

            _baselined = true;
        }

        if (ModuleNameGuard.IsFallbackModuleName(local.Module) || !bridge.TryReadPlacedMineWorld(out uint world))
        {
            return;
        }

        uint moduleHash = MineFlags.HashModule(local.Module);
        if (world != _world)
        {
            ResetWorld(world);
        }

        if (session.IsPlacedMineAuthority)
        {
            session.SetPlacedMineHostWorld(moduleHash, world);
        }

        foreach (string note in session.DrainPlacedMineNotes())
        {
            log($"placed mines: {note}");
        }

        DrainCaptures(world, moduleHash);
        DrainGone(world);

        PlacedMineRosterPacket? roster = session.CurrentPlacedMineRoster;
        if (!PlacedMineReplicaPlanner.RosterApplies(roster, moduleHash))
        {
            return;
        }

        WriteRoster(roster!, world, moduleHash);
        DriveSpawns(roster!, world);
    }

    private void ResetWorld(uint world)
    {
        _world = world;
        _settled.Clear();
        _ownCaptures.Clear();
        _ownVanished.Clear();
        _goneReported.Clear();
        _inFlightRequest = 0;
        _inFlightSerial = 0;
        _retrySerial = 0;
        _attempts = 0;
        _rosterWrittenGeneration = 0;
    }

    private void DrainCaptures(uint world, uint moduleHash)
    {
        IReadOnlyList<PlacedMineCapture> captures = bridge.ReadPlacedMineCaptures(ref _captureCursor, out int lost);
        if (lost > 0)
        {
            log($"placed mines: {lost} capture(s) overwritten before they were read - those mines stay local");
        }

        foreach (PlacedMineCapture capture in captures)
        {
            if (capture.World != world)
            {
                continue; // captured in a world this instance has already left
            }

            _ownCaptures.Add(capture.CaptureSerial);
            PlacedMineRecord r = capture.Record;
            log($"placed mines: local placement #{capture.CaptureSerial} (row {r.TrapType}, DC {r.DetectDc}/{r.DisarmDc}, "
                + $"faction {r.Faction}, at {r.X:F2},{r.Y:F2},{r.Z:F2}) - "
                + (session.IsPlacedMineAuthority ? "entering the canonical set" : "reporting to the host"));
            session.PublishPlacedMine(capture.CaptureSerial, moduleHash, r);
        }
    }

    private void DrainGone(uint world)
    {
        IReadOnlyList<PlacedMineGone> events = bridge.ReadPlacedMineGone(ref _goneCursor, out int lost);
        if (lost > 0)
        {
            log($"placed mines: {lost} gone event(s) overwritten before they were read");
        }

        foreach (PlacedMineGone gone in events)
        {
            if (gone.World != world)
            {
                continue;
            }

            if (gone.MineSerial != 0)
            {
                ReportGone(gone.MineSerial, "the engine removed it here");
            }
            else if (gone.CaptureSerial != 0)
            {
                // Resolved to a serial when the roster names it (PlacedMineReplicaPlanner).
                _ownVanished.Add(gone.CaptureSerial);
                log($"placed mines: own placement #{gone.CaptureSerial} is gone before the host named it");
            }
        }
    }

    private void ReportGone(uint serial, string why)
    {
        if (!_goneReported.Add(serial))
        {
            return;
        }

        _settled.Add(serial);
        log($"placed mines: session mine #{serial} gone ({why}) - reporting");
        session.PublishPlacedMineGone(serial);
    }

    private void WriteRoster(PlacedMineRosterPacket roster, uint world, uint moduleHash)
    {
        if (roster.Generation == _rosterWrittenGeneration && world == _rosterWrittenWorld && moduleHash == _rosterWrittenModule)
        {
            return;
        }

        List<uint> serials = [.. roster.Entries.Select(e => e.Serial)];
        if (bridge.TryWritePlacedMineRoster(world, roster.HighestSerial, serials))
        {
            _rosterWrittenGeneration = roster.Generation;
            _rosterWrittenWorld = world;
            _rosterWrittenModule = moduleHash;
        }
    }

    private void DriveSpawns(PlacedMineRosterPacket roster, uint world)
    {
        long now = SessionClock.NowMs;
        if (_inFlightRequest != 0)
        {
            PollAck(now);
            return;
        }

        PlacedMineStep step = PlacedMineReplicaPlanner.Next(
            roster.Entries, _settled, session.LocalPeerId, _ownCaptures, _ownVanished);

        switch (step.Kind)
        {
            case PlacedMineStepKind.ReportGone:
                ReportGone(step.Entry.Serial, $"own placement #{step.Entry.CaptureSerial} vanished before its serial arrived");
                return;

            case PlacedMineStepKind.Request:
                break;

            default:
                return;
        }

        uint serial = step.Entry.Serial;
        if (serial == _retrySerial && now < _retryAtMs)
        {
            return;
        }

        if (serial != _retrySerial)
        {
            _retrySerial = serial;
            _attempts = 0;
        }

        uint request = bridge.RequestPlacedMineSpawn(world, serial, step.CaptureToRegister, step.Entry.Record);
        if (request == 0)
        {
            return;
        }

        _attempts++;
        _inFlightRequest = request;
        _inFlightSerial = serial;
        _inFlightSinceMs = now;
        log(step.CaptureToRegister != 0
            ? $"placed mines: registering own placement #{step.CaptureToRegister} as session mine #{serial}"
            : $"placed mines: asking the plugin to build session mine #{serial} (owner peer {step.Entry.OwnerPeerId}, attempt {_attempts})");
    }

    private void PollAck(long now)
    {
        bool acked = bridge.TryReadPlacedMineSpawnAck(out uint applied, out uint status, out uint localId)
            && applied == _inFlightRequest;
        if (!acked)
        {
            if (now - _inFlightSinceMs < AckTimeoutMs)
            {
                return;
            }

            status = PlacedMineSpawnStatusCodes.Retry;
            log($"placed mines: no answer for session mine #{_inFlightSerial} within {AckTimeoutMs} ms");
        }

        uint serial = _inFlightSerial;
        _inFlightRequest = 0;
        _inFlightSerial = 0;

        if (status == PlacedMineSpawnStatusCodes.StaleWorld)
        {
            return; // the world moved; ResetWorld on the next tick re-plans from scratch
        }

        if (status == PlacedMineSpawnStatusCodes.OwnCaptureGone)
        {
            ReportGone(serial, "own placement vanished here before the host named it");
            return;
        }

        if (PlacedMineReplicaPlanner.IsFinal(status))
        {
            _settled.Add(serial);
            log($"placed mines: session mine #{serial} -> {Describe(status)}{(localId != 0 ? $" (local 0x{localId:X})" : string.Empty)}");
            return;
        }

        if (_attempts >= MaxAttempts)
        {
            _settled.Add(serial);
            log($"placed mines: giving up on session mine #{serial} after {_attempts} attempts");
            return;
        }

        _retryAtMs = now + RetryDelayMs;
    }

    private static string Describe(uint status) => status switch
    {
        PlacedMineSpawnStatusCodes.Spawned => "replica built",
        PlacedMineSpawnStatusCodes.Registered => "own mine registered",
        PlacedMineSpawnStatusCodes.Refused => "refused by the plugin",
        PlacedMineSpawnStatusCodes.OwnCaptureGone => "own mine already gone here",
        PlacedMineSpawnStatusCodes.Disabled => "placed_mine_sync is off in the plugin",
        _ => $"status {status}",
    };
}
