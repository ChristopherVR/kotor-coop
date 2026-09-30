using System.Linq;
using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — The mirrored enemy roster this client's engine displays — a client never owns
/// enemy state, so publishing is a deliberate no-op; only the host's snapshot is
/// applied here, module-gated so stale ids from elsewhere are dropped rather than
/// misapplied.
///
/// Accumulated across however many pages one tick's census needed (protocol 70,
/// <see cref="EnemySnapshotAssembler"/>), the same shape <see cref="GetCreatureLiveness"/>
/// already established for its own paged channel, except pruning here runs every COMPLETE
/// tick rather than on a generation change: the census is a per-tick level, not a multi-second
/// cycle, so an enemy that drops out of every page of one complete tick has genuinely left the
/// census (died and aged out, went out of combat/range, or the owner changed module) and must
/// stop being mirrored on the very next apply.
/// </summary>
public sealed partial class CoopClient
{
    /// <summary>A client never owns enemy state; it mirrors the host's.</summary>
    public bool IsEnemyAuthority => false;

    public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies)
    {
        // Only the host is authoritative, so a client's own view is deliberately dropped.
    }

    /// <summary>
    /// The host's latest enemy view for <paramref name="module"/>, in the same
    /// fighting-first-then-nearest priority order the owner's census produced it in
    /// (<see cref="_enemyOrder"/> preserves first-seen order rather than dictionary order, so
    /// <see cref="EnemyBlockPage"/>'s own priority split stays meaningful downstream).
    /// </summary>
    public IReadOnlyList<EnemyState> GetEnemies(string module)
    {
        lock (_gate)
        {
            // Ids from another module are not stale, they are WRONG: the same numbers name
            // different creatures here. Dropping the snapshot leaves our own copies alone
            // until we are back in the same place.
            if (_enemyModule.Length == 0
                || module.Length == 0
                || !string.Equals(_enemyModule, module, StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            List<EnemyState> ordered = new(_enemyOrder.Count);
            foreach (uint id in _enemyOrder)
            {
                ordered.Add(_enemiesById[id]);
            }

            return ordered;
        }
    }

    /// <summary>Accumulates a tick's <see cref="EnemySnapshotPacket"/> page(s) as they arrive —
    /// see its own remarks for why a page's entries apply the instant it lands, independent of
    /// any sibling page of the same tick.</summary>
    private readonly EnemySnapshotAssembler _enemySnapshotAssembler = new();

    private readonly Dictionary<uint, EnemyState> _enemiesById = [];

    /// <summary>First-seen order of <see cref="_enemiesById"/>'s keys — a plain
    /// <c>Dictionary</c>'s enumeration order is an implementation detail, not a policy, and the
    /// owner's fighting-first-then-nearest priority is meaningful downstream (see
    /// <see cref="EnemyBlockPage"/>), so it is tracked explicitly rather than assumed.</summary>
    private readonly List<uint> _enemyOrder = [];

    private string _enemyModule = string.Empty;

    private readonly DamageReportWorldWatch _damageReportWorld = new();

    public void ReportEnemyDamage(string module, uint generation, IReadOnlyList<EnemyDamageEntry> damage, uint localNativeEpoch)
    {
        lock (_gate)
        {
            // The native world can turn over before the next managed digest tick. Do not
            // bind its new generation against the old advertised host/local pairing.
            if (localNativeEpoch == 0 || localNativeEpoch != _localWorld.NativeWorldEpoch) return;
            ReportEnemyDamage(module, generation, damage);
        }
    }

    public void ReportEnemyDamage(string module, uint generation, IReadOnlyList<EnemyDamageEntry> damage)
    {
        EnemyDamagePacket packet;
        lock (_gate)
        {
            if (_localPeerId == 0 || _livenessLocalLoadQuarantined || !_damageReportWorld.Accept(_hostSessionId, _hostWorld.Module,
                _hostWorld.LoadSerial, _hostWorld.NativeWorldEpoch, module, generation)) return;
            if (damage.Count == 0) return;
            packet = new EnemyDamagePacket(_localPeerId, module, generation, damage, _settings.DamageIncarnation,
                _hostSessionId, _hostWorld.LoadSerial, _hostWorld.NativeWorldEpoch);
        }

        // Unreliable and re-sent every tick, like the state update it travels beside. Safe
        // precisely because the totals are cumulative: a lost datagram costs one tick of
        // staleness rather than a hit, and a duplicate applies nothing.
        //
        // ⚠ The caller's thread, not the tick thread — see CoopClient._apiBuffer. Sharing the
        // tick buffer here produced a torn EnemyDamagePacket whose cumulative total decoded
        // LOWER than the peer's true running figure; PeerDamageLedger moves its per-peer
        // baseline to whatever it is told, so the next honest report was read as a fresh rise
        // and the same fight was banked a second time.
        SendFromCaller(
            packet,
            static (buffer, packet) => PacketCodec.Write(buffer, packet));
    }

    public IReadOnlyList<PeerEnemyDamage> GetEnemyDamage(string module) => [];

    private void HandleEnemySnapshot(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadEnemySnapshot(span) is not { } page)
        {
            return;
        }

        lock (_gate)
        {
            // A module change drops the whole accumulated set outright: ids are reassigned
            // per module load, so anything remembered from the last one names a different
            // creature now, or nothing. Same guard GetEnemies applies on read.
            if (!string.Equals(_enemyModule, page.Module, StringComparison.OrdinalIgnoreCase))
            {
                _enemiesById.Clear();
                _enemyOrder.Clear();
                _enemyModule = page.Module;
            }

            // This page's own entries are complete and ready to apply right now — never gated
            // on whether a sibling page of the same tick has (or has not) arrived. See
            // EnemySnapshotAssembler's own remarks.
            foreach (EnemyState entry in _enemySnapshotAssembler.Apply(page))
            {
                if (_enemiesById.TryAdd(entry.ObjectId, entry))
                {
                    // First sighting: append, preserving the owner's priority order.
                    _enemyOrder.Add(entry.ObjectId);
                }
                else
                {
                    // A refresh of an id we already have: update in place rather than moving
                    // it to the back, so a fight that keeps re-sorting to the owner's front
                    // does not visibly reshuffle here every tick.
                    _enemiesById[entry.ObjectId] = entry;
                }
            }

            // Prune only once every page of a complete tick is accounted for — pruning off a
            // partial picture would wrongly drop an enemy whose entry simply lives on a page
            // that has not arrived yet. A page that never arrives just means this tick's prune
            // is skipped; the very next tick starts a fresh, independent cycle.
            if (_enemySnapshotAssembler.TickComplete && _enemyOrder.Count > 0)
            {
                IReadOnlyCollection<uint> present = _enemySnapshotAssembler.PresentIds;
                List<uint> gone = _enemyOrder.Where(id => !present.Contains(id)).ToList();
                foreach (uint id in gone)
                {
                    _enemyOrder.Remove(id);
                    _enemiesById.Remove(id);
                }
            }
        }
    }
}
