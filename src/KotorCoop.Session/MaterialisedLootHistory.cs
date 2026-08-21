using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Every enemy death-loot op this host has published in the CURRENT module, keyed by owner
/// enemy id, retained past the point <see cref="MaterialisedLootSender"/> forgets it — that
/// sender only tracks ops still in flight, so an op every THEN-connected peer acked (it
/// RETIRED) before a peer disconnected and reconnected is gone from it for good. This is the
/// missing permanent store docs/research/enemy-materialisation.md §6 named as item 11(b): the
/// <see cref="ContainerLootStore"/> pattern, applied to a channel keyed by owner enemy id
/// instead of a (container, item) pair.
///
/// Bounded rather than growing forever across a long session — a death is worth re-offering to
/// a peer that reconnects soon after, not indefinitely, and one from 300 kills ago is not
/// coming back for. Cleared on a host module change (see <see cref="CoopHost.PublishEnemies"/>):
/// owner enemy ids are reassigned per load, so an entry from the last module names a different
/// creature now (or nothing), and re-offering it to a joiner in the new module would at best be
/// refused by <c>MaterialisedEnemies.ToLocal</c> finding no mapping and at worst collide with a
/// fresh materialisation that happens to reuse the same number.
/// </summary>
public sealed class MaterialisedLootHistory(int maxEntries = 256)
{
    private readonly Dictionary<uint, IReadOnlyList<MaterialisedLootItem>> _byOwner = [];

    /// <summary>Insertion order, oldest first — what eviction and replay both walk.</summary>
    private readonly List<uint> _order = [];

    private readonly Lock _gate = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _order.Count;
            }
        }
    }

    /// <summary>
    /// Remembers one owner's captured death-loot. A creature dies exactly once
    /// (docs/research/enemy-materialisation.md §3's lifecycle notes), so a second call for an
    /// owner id already known overwrites its items in place rather than duplicating the entry
    /// or disturbing the eviction order.
    /// </summary>
    public void Record(uint ownerEnemyId, IReadOnlyList<MaterialisedLootItem> items)
    {
        lock (_gate)
        {
            if (!_byOwner.ContainsKey(ownerEnemyId))
            {
                _order.Add(ownerEnemyId);

                if (_order.Count > maxEntries)
                {
                    uint evicted = _order[0];
                    _order.RemoveAt(0);
                    _byOwner.Remove(evicted);
                }
            }

            _byOwner[ownerEnemyId] = items;
        }
    }

    /// <summary>Every remembered death, oldest first, for a (re)joining peer to catch up on.
    /// </summary>
    public List<(uint OwnerEnemyId, IReadOnlyList<MaterialisedLootItem> Items)> Snapshot()
    {
        lock (_gate)
        {
            List<(uint, IReadOnlyList<MaterialisedLootItem>)> result = new(_order.Count);
            foreach (uint ownerEnemyId in _order)
            {
                result.Add((ownerEnemyId, _byOwner[ownerEnemyId]));
            }

            return result;
        }
    }

    /// <summary>Called on a host module change — owner enemy ids are per-module, so nothing
    /// remembered from the last one belongs in the next.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _byOwner.Clear();
            _order.Clear();
        }
    }
}
