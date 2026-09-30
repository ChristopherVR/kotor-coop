using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// The client-cast enemy-effect relay's agent half (docs/research/enemy-effects.md, coop.ini
/// <c>enemy_effect_sync</c>). Enemies are host-authoritative, so a Stasis, Stun, Fear,
/// Whirlwind, Force Push, Slow, Paralysis or Sleep a CLIENT casts on a mirrored enemy landed on
/// the client's local body only and the host's real one kept acting.
///
/// Two independent directions, each a single-entry mailbox with a handshake:
/// <list type="bullet">
/// <item>OUT (a client): reads the effect the plugin captured, acks it so the plugin offers the
/// next, and reports it reliably to the host (<see cref="IEnemyEffectSession.PublishEnemyEffect"/>);</item>
/// <item>IN (the host): drains what clients reported, drops anything for another module (object
/// ids are per world), and feeds the plugin one at a time, waiting for its "handled" echo before
/// the next — the plugin applies it to the real enemy body.</item>
/// </list>
/// Both directions run on both roles, harmlessly: the host's plugin never captures (its attacker
/// set is empty) and a client's inbox is always empty.
/// </summary>
internal sealed class EnemyEffectPump(
    IGameBridge bridge, ICoopSession session, AgentSettings settings, Action<string> log)
{
    /// <summary>How long the plugin gets to echo an IN serial before the pump stops waiting and
    /// moves on (a wedged or missing plugin must not stall the queue).</summary>
    private const long InAckTimeoutMs = 1_000;

    /// <summary>Reports waiting for the plugin. Bounded: an effect is only worth its freshest
    /// replay, so the oldest is dropped.</summary>
    private const int PendingCapacity = 32;

    private uint _lastOutSerial;
    private bool _baselined;

    private readonly Queue<EnemyEffectPacket> _pending = new();

    private uint _nextInSerial;
    private bool _inSeeded;
    private uint _inFlightSerial;
    private long _inFlightSinceMs;

    private int _logged;

    public int PendingCount => _pending.Count + (_inFlightSerial != 0 ? 1 : 0);

    public void Pump(in PlayerState local, bool changedModule)
    {
        if (!settings.EnemyEffectSync)
        {
            return;
        }

        bridge.TryPublishEnemyEffectCaptureWorld(session.EnemyEffectCaptureSource);
        bridge.TrySetEnemyEffectHostSession(session.EnemyEffectHostSessionId);
        PublishOwn(local.Module);
        FeedPlugin(local.Module, changedModule);
    }

    private void PublishOwn(string module)
    {
        // Baseline on the FIRST read whatever it says: the block outlives sessions in one
        // process, so whatever the mailbox holds when this pump first looks is history.
        bool readable = bridge.TryReadEnemyEffectOut(out EnemyEffectSample sample);
        if (!_baselined)
        {
            _baselined = true;
            _lastOutSerial = readable ? sample.Serial : 0;
            return;
        }

        if (!readable || sample.Serial == _lastOutSerial)
        {
            return;
        }

        _lastOutSerial = sample.Serial;
        bridge.TryAckEnemyEffectOut(sample.Serial);

        if (ModuleNameGuard.IsFallbackModuleName(module) || !EnemyEffectRules.IsForwardable(sample.Kind))
        {
            return;
        }

        session.PublishEnemyEffect(
            module, sample.EnemyId, (PeerEffectKind)sample.Kind, sample.DurationMs, sample.Magnitude, sample.Serial, sample.NativeEpoch, sample.LoadSerial, sample.HostSessionId, sample.HostLoadSerial, sample.HostNativeEpoch, sample.Incarnation);
        Log($"reporting kind {sample.Kind} on enemy 0x{sample.EnemyId:X} to the host "
            + $"({sample.DurationMs} ms, magnitude {sample.Magnitude})");
    }

    private void FeedPlugin(string module, bool changedModule)
    {
        if (changedModule)
        {
            // Every object id was reassigned; what is queued names strangers now.
            _pending.Clear();
            _inFlightSerial = 0;
        }

        foreach (EnemyEffectPacket packet in session.DrainEnemyEffects())
        {
            if (!session.EnemyEffectWorldCurrent(packet) || !string.Equals(packet.Module, module, StringComparison.OrdinalIgnoreCase))
            {
                Log($"dropped a client's effect on 0x{packet.EnemyId:X}: it was cast in '{packet.Module}', "
                    + $"this world is '{module}'");
                continue;
            }

            if (_pending.Count >= PendingCapacity)
            {
                _pending.Dequeue();
            }

            _pending.Enqueue(packet);
        }

        long now = SessionClock.NowMs;

        if (_inFlightSerial != 0)
        {
            bool handled = bridge.TryReadEnemyEffectApplied(out uint applied) && applied == _inFlightSerial;
            if (!handled && now - _inFlightSinceMs < InAckTimeoutMs)
            {
                return;
            }

            _inFlightSerial = 0;
        }

        if (_pending.Count == 0)
        {
            return;
        }

        // Continue from whatever the plugin last handled: a restarted agent that began again at 1
        // would collide with an already-handled serial and be swallowed as a repeat.
        if (!_inSeeded)
        {
            if (!bridge.TryReadEnemyEffectApplied(out uint seed))
            {
                return;
            }

            _nextInSerial = seed;
            _inSeeded = true;
        }

        EnemyEffectPacket next = _pending.Dequeue();
        if (!session.EnemyEffectWorldCurrent(next)) return;
        _nextInSerial = EnemyEffectMailbox.NextSerial(_nextInSerial);

        EnemyEffectSample sample = new(_nextInSerial, next.EnemyId, (uint)next.Kind, next.DurationMs, next.Magnitude, next.HostNativeEpoch, next.HostLoadSerial, next.HostSessionId);
        if (!bridge.TryWriteEnemyEffectIn(sample))
        {
            return;
        }

        _inFlightSerial = _nextInSerial;
        _inFlightSinceMs = now;
        Log($"handing a client's kind {(uint)next.Kind} on enemy 0x{next.EnemyId:X} to the plugin "
            + $"({next.DurationMs} ms, magnitude {next.Magnitude})");
    }

    private void Log(string message)
    {
        if (_logged < 60)
        {
            _logged++;
            log($"enemy-effect: {message}");
        }
    }
}
