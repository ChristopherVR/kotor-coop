using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// The agent-side half of local_combat's census exclusion (docs/research/combat-participation.md
/// §11, §12): which of the owner's published enemies reach this instance's own presence array
/// while the plugin has pulled some of them out to fight for real.
///
/// The exclusion used to be a plain drop — every excluded id was removed from the feed, alive or
/// dead — and that left one hole the 2026-09-02 A-slots pass fell straight into: the client's
/// forwarded damage killed <c>0x3B</c> on the host, the host's census carried it as <c>Dead</c>
/// for the five seconds its corpse existed and then stopped listing it, and the client never saw
/// either, because the id was excluded for the whole of it. Its local copy — min-1-hp pinned, so
/// it cannot die on its own — ran out the 25 s timeout as <c>timed out</c>, was handed back to
/// normal mirroring, and normal mirroring had nothing to say about an id the owner no longer
/// publishes. The result was an immortal Sith that kept shooting the client's PC forty seconds
/// after the host had looted its corpse (<c>combat ground-truth: local PC took 2 damage,
/// attacker=0x3B (unrecognized attacker id)</c>).
///
/// Two signals close it, both decided here so the plugin's <c>DriveOne</c> sees an ordinary
/// <c>Dead</c> entry and runs the ordinary death path (release the slot, unpin, kill the local
/// body — the same corpse/loot/XP pipeline as any mirrored death):
/// <list type="number">
/// <item>An excluded id the owner publishes as <c>Dead</c> is passed THROUGH the exclusion —
/// the exclusion was only ever meant to keep the owner's live driving off a creature this engine
/// is fighting; the owner's verdict on its death is the very thing the fight was for.</item>
/// <item>An excluded id the owner has STOPPED publishing — after this rule saw it in the feed
/// at least once while excluded, then unlisted for <see cref="AbsenceGraceMs"/> since that sighting,
/// and only while the feed is below its cap (so an id shed for capacity is never mistaken for a
/// destroyed one) — is republished from its last seen entry with <c>Dead</c> set: the body was
/// destroyed on the owner, and a copy of it has no business standing here.</item>
/// </list>
/// Everything is keyed on the current exclusion set: an id that leaves the set (the slot was
/// released for any reason) is forgotten at once, and <see cref="Reset"/> forgets everything at
/// a module edge, where ids stop meaning what they meant.
/// </summary>
internal sealed class LocalCombatExclusionRule
{
    /// <summary>How long an excluded id may be missing from the owner's feed before it is taken
    /// as destroyed there. The feed refreshes several times a second; two seconds is many
    /// publishes, and well inside the 25 s the fight would otherwise run on for.</summary>
    public const long AbsenceGraceMs = 2000;

    /// <summary>The owner's census cap — the SharedBlock enemy region holds exactly
    /// <see cref="KotorGameBridge.BlockEnemyCapacity"/> entries (referenced rather than
    /// re-typed, so this cannot drift from the shared-block region's own physical capacity),
    /// and the owner sheds by distance to the nearest player above it, with the overflow
    /// rotated across pumps rather than shed forever (<c>KotorCoop.Memory.EnemyCensusPage</c>).
    /// A debug cap below that (<c>--enemy-census-cap</c>) on the owner is not visible here; with
    /// one set, an id shed for capacity could read as destroyed, which is why that knob is
    /// dev-only.</summary>
    public const int FeedCapacity = KotorGameBridge.BlockEnemyCapacity;

    private readonly Dictionary<uint, Tracked> _tracked = [];

    private sealed class Tracked
    {
        public EnemyState? LastSeen;

        /// <summary>When it was last in the feed — or the last pump whose feed was at capacity,
        /// which proves nothing either way and restarts the clock.</summary>
        public long LastSeenAtMs;
    }

    /// <summary>Why an excluded id was let through, for the pump's log line.</summary>
    public enum Verdict
    {
        /// <summary>The owner still lists it alive: excluded, as before.</summary>
        Excluded,

        /// <summary>The owner lists it dead: passed through so the death runs here.</summary>
        OwnerReportsDead,

        /// <summary>The owner no longer lists it and did before: republished as dead.</summary>
        OwnerNoLongerLists,
    }

    /// <summary>One (id, verdict) per excluded id this pump, for logging on an edge.</summary>
    public readonly List<(uint Id, Verdict Verdict)> Verdicts = [];

    /// <summary>
    /// Filters the owner's feed for this pump. <paramref name="capacity"/> is the feed's own
    /// cap (32): at or above it an absence proves nothing.
    /// </summary>
    public List<EnemyState> Filter(
        IReadOnlyList<EnemyState> enemies, IReadOnlyList<uint> excludedIds, int capacity, long nowMs)
    {
        Verdicts.Clear();
        List<EnemyState> filtered = new(enemies.Count);

        // Drop tracking for anything no longer excluded (slot released, whatever the reason).
        if (_tracked.Count > 0)
        {
            List<uint>? stale = null;
            foreach (uint id in _tracked.Keys)
            {
                if (!excludedIds.Contains(id))
                {
                    (stale ??= []).Add(id);
                }
            }

            if (stale is not null)
            {
                foreach (uint id in stale)
                {
                    _tracked.Remove(id);
                }
            }
        }

        foreach (EnemyState enemy in enemies)
        {
            if (!excludedIds.Contains(enemy.ObjectId))
            {
                filtered.Add(enemy);
                continue;
            }

            Tracked t = Track(enemy.ObjectId);
            t.LastSeen = enemy;
            t.LastSeenAtMs = nowMs;

            if (enemy.Dead)
            {
                Verdicts.Add((enemy.ObjectId, Verdict.OwnerReportsDead));
                filtered.Add(enemy);
            }
            else
            {
                Verdicts.Add((enemy.ObjectId, Verdict.Excluded));
            }
        }

        bool belowCap = enemies.Count < capacity;
        foreach (uint id in excludedIds)
        {
            Tracked t = Track(id);
            bool present = false;
            for (int i = 0; i < Verdicts.Count; i++)
            {
                if (Verdicts[i].Id == id)
                {
                    present = true;
                    break;
                }
            }

            if (present || t.LastSeen is null)
            {
                // Present this pump, or never seen while excluded (nothing to compare an
                // absence against — the plugin armed it off a feed this rule has not read yet).
                continue;
            }

            if (!belowCap)
            {
                // Shed for capacity is indistinguishable from destroyed; restart the clock.
                t.LastSeenAtMs = nowMs;
                Verdicts.Add((id, Verdict.Excluded));
                continue;
            }

            if (nowMs - t.LastSeenAtMs >= AbsenceGraceMs)
            {
                Verdicts.Add((id, Verdict.OwnerNoLongerLists));
                filtered.Add(t.LastSeen with { Dead = true, CurrentHealth = 0 });
            }
            else
            {
                Verdicts.Add((id, Verdict.Excluded));
            }
        }

        return filtered;
    }

    public void Reset()
    {
        _tracked.Clear();
        Verdicts.Clear();
    }

    private Tracked Track(uint id)
    {
        if (!_tracked.TryGetValue(id, out Tracked? t))
        {
            t = new Tracked();
            _tracked[id] = t;
        }

        return t;
    }
}
