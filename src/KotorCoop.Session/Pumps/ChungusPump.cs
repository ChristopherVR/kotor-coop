using System.Globalization;
using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// The arena boss fight on this game (docs/research/chungus-boss.md). Two jobs:
///
/// <list type="bullet">
/// <item><b>Every game:</b> when the host says the boss used a power, play it out here: the
/// wind-up (his animation and its sound) at once, and the landing (its sound, and what it does
/// to this game's own player) when the animation gets there. Each game hurts its own character,
/// so nothing depends on one game being allowed to reach into another's.</item>
/// <item><b>The host:</b> put the boss in the ring when the fight goes live, decide his powers
/// (<see cref="BossBrain"/>), watch his health, and tell the round when he is down. Remove him
/// before the party leaves, so he is not written into the arena's saved state.</item>
/// </list>
///
/// Everything it asks of the game goes through the one cheat mailbox, one request at a time: a
/// request is sent, and the next waits until the plugin has answered it (or a second has passed).
/// </summary>
internal sealed class ChungusPump(IGameBridge bridge, ICoopSession session, Action<string> log)
{
    private const long AnswerTimeoutMs = 1_000;
    private const long SpawnRetryMs = 2_000;
    private const int MaxSpawnTries = 5;
    private const long WatchIntervalMs = 500;

    /// <summary>The boss has to be missing from the game's creature list this long, after having
    /// been seen, to count as gone (a busy frame can skip him once).</summary>
    private const long MissingMeansDownMs = 3_000;

    /// <summary>How long the round waits after he drops before everyone is taken home, so the
    /// fall is seen.</summary>
    private const long DownHoldMs = 3_000;

    private readonly List<(long DueMs, CheatRequest Request, bool IsSpawn)> _queue = [];
    private uint _waitingSerial;
    private bool _waitingIsSpawn;
    private long _waitingSinceMs;

    // Host only.
    private uint _round;
    private BossBrain? _brain;
    private uint _bossId;
    private int _spawnTries;
    private long _spawnLastMs;
    private bool _spawnAsked;
    private long _watchLastMs;
    private bool _bossSeen;
    private long _bossMissingSinceMs;
    private int _bossHealthPercent = -1;
    private long _bossDownAtMs;
    private bool _downReported;
    private bool _despawned;
    private Random? _whim;
    private long _blinkAtMs;

    /// <summary>The longest he stays where he is before he is suddenly somewhere else, and the
    /// shortest; and how long after a power he waits, so its animation is not cut off.</summary>
    private const int BlinkEveryMinMs = 5_000;
    private const int BlinkEveryMaxMs = 8_000;
    private const int BlinkAfterPowerMs = 4_000;

    public void Pump(in PlayerState local)
    {
        PvpView view = session.PvpState;
        long now = SessionClock.NowMs;
        bool isHost = session.IsEnemyAuthority;

        foreach (BossCue cue in session.DrainBossCues())
        {
            if (!cue.IsKnown)
            {
                continue;
            }

            // The row this install's copy of him wears rides along, so a joiner's game can pick
            // him out of the creatures it mirrors.
            bool hitsMe = cue.Hits(session.LocalPeerId);
            uint word = ChungusCheat.Word(cue, hitsMe, isHost);
            uint row = (uint)Math.Clamp(session.BossAppearanceRow, 0, ushort.MaxValue);
            long landAt = now + BossPowers.LandDelayMs(cue.Power);
            _queue.Add((now,
                new CheatRequest(CheatKinds.Chungus, Target: word, Amount: ChungusCheat.OpWindUp, Charges: row), false));

            if (cue.Power == BossPower.Blink)
            {
                // Only how he gets about: the host's game moves him, and every other game sees
                // its copy of him follow.
                // Half the time he lands beside the player, and half the time across the ring from
                // them, so that he comes back the other way he has of getting about: rolling.
                if (isHost && PositionOf(cue.TargetPeer, local) is { } at)
                {
                    QueueBlink(now + 250, Random.Shared.Next(2) == 0
                        ? ArenaFlow.BesideInRing(at, 3.2f)
                        : ArenaFlow.RingSpot(Random.Shared.NextDouble(), 0.8));
                }

                continue;
            }

            if (cue.Power == BossPower.Lift)
            {
                // The space stone: this game's player is somewhere else in the ring when it
                // lands (each game picks its own spot), and so is he.
                if (hitsMe)
                {
                    (float x, float y, float z) = ArenaFlow.RingSpot(Random.Shared.NextDouble(), 0.35 + (0.5 * Random.Shared.NextDouble()));
                    _queue.Add((landAt, new CheatRequest(
                        CheatKinds.Teleport, Amount: ArenaFlow.FacingTowardCentre(new Vector3f(x, y, z)), X: x, Y: y, Z: z), false));
                }

                if (isHost)
                {
                    QueueBlink(landAt, ArenaFlow.RingSpot(Random.Shared.NextDouble(), 0.3));
                }
            }

            if (cue.Power != BossPower.Defeat)
            {
                _queue.Add((landAt,
                    new CheatRequest(CheatKinds.Chungus, Target: word, Amount: ChungusCheat.OpLand, Charges: row), false));
            }

            if (cue.Power == BossPower.Snap)
            {
                // Original nonverbal resonator chime.
                _queue.Add((landAt + BossPowers.SnapLineDelayMs,
                    new CheatRequest(CheatKinds.Chungus, Amount: ChungusCheat.OpSound, Text: BossPowers.SnapLine), false));
            }

            if (cue.Power == BossPower.Slow && isHost)
            {
                // Time has stopped for everyone but him: he uses it to stand next to somebody.
                List<uint> frozen = [session.LocalPeerId, .. session.GetRemotePeers().Select(peer => peer.PeerId)];
                if (PositionOf(frozen[Random.Shared.Next(frozen.Count)], local) is { } at)
                {
                    QueueBlink(landAt + 500, ArenaFlow.BesideInRing(at, 2.2f));
                }
            }

            log($"chungus: {cue.Power}{(cue.TargetPeer != 0 ? $" at peer {cue.TargetPeer}" : string.Empty)}");
        }

        if (isHost)
        {
            PumpHost(view, local, now);
        }

        FlushQueue(now);
    }

    public void Reset()
    {
        _queue.Clear();
        _waitingSerial = 0;
        ResetHost();
    }

    private void ResetHost()
    {
        _round = 0;
        _brain = null;
        _bossId = 0;
        _spawnTries = 0;
        _spawnLastMs = 0;
        _spawnAsked = false;
        _watchLastMs = 0;
        _bossSeen = false;
        _bossMissingSinceMs = 0;
        _bossHealthPercent = -1;
        _bossDownAtMs = 0;
        _downReported = false;
        _despawned = false;
        _whim = null;
        _blinkAtMs = 0;
    }

    /// <summary>Where a player in the fight stands, as this game knows it: its own, or the last
    /// it heard of another's. Null for a player who is not in the arena with it.</summary>
    private Vector3f? PositionOf(uint peer, in PlayerState local)
    {
        if (peer == 0 || peer == session.LocalPeerId)
        {
            return local.Position;
        }

        string module = local.Module;
        return session.GetRemotePeers().FirstOrDefault(p => p.PeerId == peer && p.IsInModule(module))?.State.Position;
    }

    private void QueueBlink(long dueMs, (float X, float Y, float Z) to) =>
        _queue.Add((dueMs, new CheatRequest(
            CheatKinds.Chungus, Amount: ChungusCheat.OpBlink, X: to.X, Y: to.Y, Z: to.Z,
            Charges: (uint)Math.Clamp(session.BossAppearanceRow, 0, ushort.MaxValue)), false));

    private void PumpHost(in PvpView view, in PlayerState local, long now)
    {
        if (!view.BossActive)
        {
            if (_round != 0)
            {
                ResetHost();
            }

            return;
        }

        if (_round != view.RoundId)
        {
            ResetHost();
            _round = view.RoundId;
        }

        bool inArena = string.Equals(local.Module, PvpDuelRound.ArenaModule, StringComparison.OrdinalIgnoreCase);

        if (view.Phase == PvpPhase.Leaving)
        {
            // Before the warp home: take him (or what is left of him) out of the arena.
            if (!_despawned && _spawnAsked && inArena)
            {
                _despawned = true;
                _queue.Add((now, new CheatRequest(CheatKinds.Chungus, Amount: ChungusCheat.OpDespawn), false));
                log("chungus: removing the boss before leaving");
            }

            return;
        }

        if (view.Phase != PvpPhase.Live || !inArena)
        {
            return;
        }

        if (_bossId == 0)
        {
            if (_spawnTries < MaxSpawnTries && (_spawnTries == 0 || now - _spawnLastMs >= SpawnRetryMs)
                && !_queue.Any(entry => entry.IsSpawn) && !_waitingIsSpawn)
            {
                _spawnTries++;
                _spawnLastMs = now;
                _spawnAsked = true;
                (float x, float y, float z) = ArenaFlow.BossSpot;
                _queue.Add((now, new CheatRequest(CheatKinds.Chungus, Amount: ChungusCheat.OpSpawn, X: x, Y: y, Z: z), true));
                log($"chungus: asking the plugin to spawn the boss at ({x:F1}, {y:F1}) (try {_spawnTries})");
            }
            else if (_spawnTries >= MaxSpawnTries && !_downReported && now - _spawnLastMs >= SpawnRetryMs)
            {
                // No boss, no fight: call it off so nobody is left standing in an empty ring.
                _downReported = true;
                log("chungus: the boss could not be spawned - calling the fight off");
                session.RequestPvp(PvpOp.Yield);
            }

            return;
        }

        Watch(now);
        if (_bossDownAtMs != 0)
        {
            if (!_downReported && now - _bossDownAtMs >= DownHoldMs)
            {
                _downReported = true;
                session.ReportBossDown();
            }

            return;
        }

        _brain ??= new BossBrain(view.RoundId);
        List<uint> players = [session.LocalPeerId, .. session.GetRemotePeers().Select(peer => peer.PeerId)];
        _whim ??= new Random(unchecked((int)view.RoundId));
        if (_blinkAtMs == 0)
        {
            _blinkAtMs = now + BlinkAfterPowerMs;
        }

        if (_brain.Next(now, _bossHealthPercent, players) is { } cue)
        {
            session.AnnounceBossCue(cue);
            _blinkAtMs = now + BossPowers.LandDelayMs(cue.Power) + BlinkAfterPowerMs;
        }
        else if (now >= _blinkAtMs)
        {
            // He does not walk across a room like anything else in this game.
            _blinkAtMs = now + _whim.Next(BlinkEveryMinMs, BlinkEveryMaxMs + 1);
            uint at = players[_whim.Next(players.Count)];
            if (at <= ushort.MaxValue)
            {
                session.AnnounceBossCue(new BossCue(BossPower.Blink, 0, (ushort)at));
            }
        }
    }

    /// <summary>Reads the boss out of the game's creature list: how hurt he is, and whether he
    /// is down.</summary>
    private void Watch(long now)
    {
        if (now - _watchLastMs < WatchIntervalMs)
        {
            return;
        }

        _watchLastMs = now;
        EnemyState? boss = bridge.ReadEnemies([]).FirstOrDefault(enemy => enemy.ObjectId == _bossId);
        if (boss is null)
        {
            if (!_bossSeen)
            {
                return;
            }

            _bossMissingSinceMs = _bossMissingSinceMs == 0 ? now : _bossMissingSinceMs;
            if (now - _bossMissingSinceMs >= MissingMeansDownMs)
            {
                BossIsDown(now, "gone from the creature list");
            }

            return;
        }

        _bossSeen = true;
        _bossMissingSinceMs = 0;
        _bossHealthPercent = boss.MaxHealth > 0 ? Math.Clamp(boss.CurrentHealth * 100 / boss.MaxHealth, 0, 100) : -1;
        if (boss.Dead)
        {
            BossIsDown(now, "dead");
        }
    }

    private void BossIsDown(long now, string how)
    {
        if (_bossDownAtMs != 0)
        {
            return;
        }

        _bossDownAtMs = now;
        log($"chungus: the boss is down ({how})");
        session.AnnounceBossCue(new BossCue(BossPower.Defeat, 0, 0));
    }

    /// <summary>Sends the next request that is due, once the previous one has been answered.</summary>
    private void FlushQueue(long now)
    {
        if (_waitingSerial != 0)
        {
            bool answered = bridge.TryReadCheatAnswer(out uint applied, out int status, out string detail)
                && applied == _waitingSerial;
            if (!answered && now - _waitingSinceMs < AnswerTimeoutMs)
            {
                return;
            }

            if (answered && _waitingIsSpawn)
            {
                OnSpawnAnswer(status, detail);
            }

            _waitingSerial = 0;
            _waitingIsSpawn = false;
        }

        int index = _queue.FindIndex(entry => entry.DueMs <= now);
        if (index < 0)
        {
            return;
        }

        (_, CheatRequest request, bool isSpawn) = _queue[index];
        _queue.RemoveAt(index);
        if (!bridge.SupportsCheats || !bridge.TryRequestCheat(request, out uint serial))
        {
            return;
        }

        _waitingSerial = serial;
        _waitingIsSpawn = isSpawn;
        _waitingSinceMs = now;
    }

    /// <summary>The plugin answers a spawn with "boss 0x…": the id the host then watches.</summary>
    private void OnSpawnAnswer(int status, string detail)
    {
        if (TryParseBossId(detail, out uint id))
        {
            _bossId = id;
            log($"chungus: the boss is in the ring as 0x{id:X}");
            session.AnnounceBossCue(new BossCue(BossPower.Entrance, 0, 0));
            return;
        }

        log($"chungus: the spawn was not carried out (status {status}): {detail}");
    }

    public static bool TryParseBossId(string detail, out uint id)
    {
        id = 0;
        const string prefix = "boss 0x";
        if (!detail.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> rest = detail.AsSpan(prefix.Length);
        int end = 0;
        while (end < rest.Length && Uri.IsHexDigit(rest[end]))
        {
            end++;
        }

        return end > 0 && uint.TryParse(rest[..end], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out id) && id != 0;
    }
}
