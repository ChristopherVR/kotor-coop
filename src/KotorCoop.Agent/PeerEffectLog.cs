using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Agent;

/// <summary>
/// Remembers the last few peer-effect block-ring entries this agent has seen, with the time it
/// first saw each one, so the <c>effects</c> console verb can show the whole pipeline —
/// captured/forwarded (the OUT ring, <see cref="KotorGameBridge.ReadPeerEffectsOut"/>) and
/// applied (the IN ring, <see cref="KotorGameBridge.ReadPeerEffectsIn"/>) — without anyone
/// grepping the plugin or agent log. Built for the live-verification gap
/// docs/research/effect-sync.md's "Live pass (2026-08-22)" section names: there was no way to
/// read this pipeline in one place before this, and no way to trigger it either (see
/// <c>applyeffect</c> in <c>ConsoleCommand</c>).
///
/// Both rings carry only a monotonic per-source SERIAL, never a wall-clock time, so the
/// timestamp here is an OBSERVATION time, not a capture time — accurate to within one dispatcher
/// tick (150ms) as long as <see cref="Observe"/> is called every tick, which
/// <c>CoopCommandDispatcher</c> does. A caller that polls less often sees a later "first seen"
/// time than the actual capture.
/// </summary>
public sealed class PeerEffectLog
{
    /// <summary>How many rows <c>effects</c> shows — the reply is read by a human during a live
    /// pass, not dumped as a log, so this stays small on purpose.</summary>
    public const int Capacity = 8;

    public readonly record struct Entry(
        string Category,
        uint SubjectId,
        uint Kind,
        uint DurationMs,
        uint Magnitude,
        uint Serial,
        DateTime ObservedAt);

    private readonly List<Entry> _entries = [];
    private uint _lastCapturedSerial;
    private uint _lastAppliedSerial;

    /// <summary>The most recent entries across both rings, newest first, capped at
    /// <see cref="Capacity"/>.</summary>
    public IReadOnlyList<Entry> Recent => _entries;

    /// <summary>
    /// Folds in whatever is new since the last call. Each ring publishes a strictly increasing
    /// serial per source while its hook is alive — <c>PhantomEffectHook</c>'s shared sequence
    /// counter for the OUT ring, the sender's own packet sequence for the IN ring — so "new" is
    /// exactly "serial above the highest one already recorded," the same watermark discipline
    /// <c>PeerEffectRules.SelectNew</c> already uses for the wire publish itself.
    /// </summary>
    public void Observe(IReadOnlyList<PeerEffectRecord> captured, IReadOnlyList<PeerEffectRecord> applied, DateTime now)
    {
        ObserveRing(captured, "captured/forwarded", ref _lastCapturedSerial, now);
        ObserveRing(applied, "applied", ref _lastAppliedSerial, now);
    }

    /// <summary>
    /// One ring's pass. A module change (or a reconnect) resets the source hook's own sequence
    /// counter back to zero — so a ring whose CURRENT highest serial reads BELOW the remembered
    /// watermark can only mean a reset happened underneath us, never that every entry is stale.
    /// Forgetting the watermark there is what keeps `effects` from going silent for a whole
    /// fresh ring's worth of captures after every transition — the same silent-guard failure
    /// this codebase's other channels are written to avoid.
    /// </summary>
    private void ObserveRing(
        IReadOnlyList<PeerEffectRecord> ring, string category, ref uint watermark, DateTime now)
    {
        uint highestNow = 0;
        foreach (PeerEffectRecord record in ring)
        {
            if (record.Serial > highestNow)
            {
                highestNow = record.Serial;
            }
        }

        if (highestNow > 0 && highestNow < watermark)
        {
            watermark = 0;
        }

        foreach (PeerEffectRecord record in ring)
        {
            if (record.Serial == 0 || record.Serial <= watermark)
            {
                continue;
            }

            Add(category, record, now);
        }

        if (highestNow > watermark)
        {
            watermark = highestNow;
        }
    }

    private void Add(string category, PeerEffectRecord record, DateTime now)
    {
        _entries.Insert(
            0,
            new Entry(category, record.PhantomId, record.Kind, record.DurationMs, record.Magnitude, record.Serial, now));

        while (_entries.Count > Capacity)
        {
            _entries.RemoveAt(_entries.Count - 1);
        }
    }

    /// <summary>One row, formatted for the in-game reply field / agent log: the kind by name
    /// where the numeric value is a known allowlist member, the subject id (a phantom id for a
    /// captured/forwarded row, the origin peer's id for an applied one), the duration/magnitude
    /// the effect actually carried, and how long ago this agent first saw it.</summary>
    public static string Format(Entry entry, DateTime now)
    {
        PeerEffectKind kindValue = (PeerEffectKind)(byte)entry.Kind;
        string kindName = Enum.IsDefined(kindValue) ? kindValue.ToString().ToLowerInvariant() : $"kind{entry.Kind}";

        double secondsAgo = Math.Max(0, (now - entry.ObservedAt).TotalSeconds);

        return $"{entry.Category} {kindName} subject 0x{entry.SubjectId:X} dur {entry.DurationMs}ms "
            + $"mag {entry.Magnitude} serial {entry.Serial} {secondsAgo:F1}s ago";
    }
}
