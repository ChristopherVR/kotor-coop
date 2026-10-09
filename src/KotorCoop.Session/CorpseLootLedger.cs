using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Shared corpse loot (coop.ini <c>corpse_loot = shared</c>, the default): one copy of an enemy's
/// loot for the whole party. Each engine makes its own body bag when a creature dies, so the two
/// bags never share an id (live 2026-10-09: host 0x135 and joiner 0x149 at the same spot, and
/// both players could empty theirs). Bags are paired by POSITION and items by BLUEPRINT instead:
///
///   - a take: an item that left one of this game's bags without this ledger removing it, kept as
///     "N x blueprint from the bag at (x, y)" and published as a level;
///   - an apply: for every other player's entry, the part of its count not applied yet is removed
///     from this game's bag within <see cref="PairRadius"/> of that spot holding that blueprint.
///     A bag that is not there yet (its creature still dying here) is retried next pass.
///
/// Items this ledger removed are remembered, so their disappearance is never mistaken for a take
/// of this player's (which would echo back and empty a second copy). One instance per agent;
/// <see cref="Reset"/> on a module change, since bags and ids belong to one world.
/// </summary>
public sealed class CorpseLootLedger
{
    /// <summary>How far apart the two engines' bags for one creature can sit: a stand-in dies
    /// where the mirror last drove it, a fraction of a step behind the host's body.</summary>
    public const float PairRadius = 2.0f;

    /// <summary>Takes of one blueprint at bags this close are one entry.</summary>
    private const float SameBagRadius = 0.5f;

    private readonly Dictionary<uint, BodyBagState> _shadow = [];
    private readonly HashSet<uint> _removedForPeers = [];
    private readonly List<CorpseTakeEntry> _ownTakes = [];
    private readonly Dictionary<(uint Origin, int X, int Y, string Resref), int> _applied = [];
    private readonly List<ContainerLootRemoval> _removals = [];

    /// <summary>This player's takes in the current module.</summary>
    public IReadOnlyList<CorpseTakeEntry> OwnTakes => _ownTakes;

    /// <summary>Every removal this ledger has asked for in the current module, for the bridge's
    /// idempotent container-loot apply.</summary>
    public IReadOnlyList<ContainerLootRemoval> Removals => _removals;

    private readonly HashSet<uint> _reconciled = [];

    public void Reset()
    {
        _reconciled.Clear();
        _shadow.Clear();
        _removedForPeers.Clear();
        _ownTakes.Clear();
        _applied.Clear();
        _removals.Clear();
    }

    /// <summary>One pass: record this player's takes since the last pass, then apply every other
    /// player's. Returns the removals newly requested this pass.</summary>
    public List<ContainerLootRemoval> Update(
        IReadOnlyList<BodyBagState> bags,
        IReadOnlyList<(uint Origin, IReadOnlyList<CorpseTakeEntry> Entries)> remote)
    {
        RecordTakes(bags);
        return ApplyRemote(bags, remote);
    }

    private void RecordTakes(IReadOnlyList<BodyBagState> bags)
    {
        Dictionary<uint, BodyBagState> current = bags.ToDictionary(b => b.Id);
        foreach (BodyBagState before in _shadow.Values)
        {
            HashSet<uint> still = current.TryGetValue(before.Id, out BodyBagState? now)
                ? [.. now.Items.Select(i => i.ItemId)]
                : [];

            foreach ((uint itemId, string resref, _) in before.Items)
            {
                if (still.Contains(itemId) || _removedForPeers.Remove(itemId))
                {
                    continue;
                }

                AddOwnTake(before.X, before.Y, resref);
            }
        }

        _shadow.Clear();
        foreach (BodyBagState bag in bags)
        {
            _shadow[bag.Id] = bag;
        }
    }

    /// <summary>
    /// Joiner, once per bag: makes this game's body bag hold what the host's bag at that spot
    /// holds. The two engines fill their bags separately (a random treasure roll, a stand-in's
    /// mirrored items, credits the engine adds on drop), so they start out different; after
    /// this, takes keep them in step. Items this game has that the host's bag does not are
    /// removed (and not counted as this player's takes); the host's items this bag lacks are
    /// returned for the caller to give into it. A bag with no host bag near it yet waits.
    /// </summary>
    public List<(uint BagId, BodyBagItemEntry Item)> Reconcile(
        IReadOnlyList<BodyBagState> bags, IReadOnlyList<HostBodyBagPacket> hostBags)
    {
        List<(uint, BodyBagItemEntry)> gives = [];
        foreach (BodyBagState bag in bags)
        {
            if (_reconciled.Contains(bag.Id))
            {
                continue;
            }

            HostBodyBagPacket? host = null;
            float nearest = PairRadius;
            foreach (HostBodyBagPacket candidate in hostBags)
            {
                float distance = Distance(candidate.X, candidate.Y, bag.X, bag.Y);
                if (distance <= nearest)
                {
                    host = candidate;
                    nearest = distance;
                }
            }

            if (host is null)
            {
                continue;
            }

            _reconciled.Add(bag.Id);
            List<BodyBagItem> unmatchedHere = [.. bag.Items.Where(i => !_removedForPeers.Contains(i.ItemId))];
            foreach (BodyBagItemEntry wanted in host.Items)
            {
                int match = unmatchedHere.FindIndex(i =>
                    i.Stack == wanted.Stack && string.Equals(i.Resref, wanted.Resref, StringComparison.OrdinalIgnoreCase));
                if (match >= 0)
                {
                    unmatchedHere.RemoveAt(match);
                }
                else
                {
                    gives.Add((bag.Id, wanted));
                }
            }

            foreach (BodyBagItem extra in unmatchedHere)
            {
                _removedForPeers.Add(extra.ItemId);
                _removals.Add(new ContainerLootRemoval(bag.Id, extra.ItemId));
            }
        }

        return gives;
    }

    private void AddOwnTake(float x, float y, string resref)
    {
        for (int i = 0; i < _ownTakes.Count; i++)
        {
            CorpseTakeEntry entry = _ownTakes[i];
            if (string.Equals(entry.Resref, resref, StringComparison.OrdinalIgnoreCase)
                && Distance(entry.X, entry.Y, x, y) <= SameBagRadius)
            {
                _ownTakes[i] = entry with { Count = (ushort)Math.Min(entry.Count + 1, ushort.MaxValue) };
                return;
            }
        }

        if (_ownTakes.Count < CorpseTakesPacket.MaxEntries)
        {
            _ownTakes.Add(new CorpseTakeEntry(x, y, resref, 1));
        }
    }

    private List<ContainerLootRemoval> ApplyRemote(
        IReadOnlyList<BodyBagState> bags,
        IReadOnlyList<(uint Origin, IReadOnlyList<CorpseTakeEntry> Entries)> remote)
    {
        List<ContainerLootRemoval> fresh = [];
        foreach ((uint origin, IReadOnlyList<CorpseTakeEntry> entries) in remote)
        {
            foreach (CorpseTakeEntry entry in entries)
            {
                var key = (origin, (int)MathF.Round(entry.X * 10), (int)MathF.Round(entry.Y * 10), entry.Resref.ToLowerInvariant());
                int done = _applied.GetValueOrDefault(key);
                while (done < entry.Count && TryTakeFor(bags, entry, out ContainerLootRemoval removal))
                {
                    fresh.Add(removal);
                    done++;
                }

                _applied[key] = done;
            }
        }

        _removals.AddRange(fresh);
        return fresh;
    }

    private bool TryTakeFor(IReadOnlyList<BodyBagState> bags, CorpseTakeEntry entry, out ContainerLootRemoval removal)
    {
        removal = default;
        BodyBagState? best = null;
        uint bestItem = 0;
        float bestDistance = PairRadius;
        foreach (BodyBagState bag in bags)
        {
            float distance = Distance(bag.X, bag.Y, entry.X, entry.Y);
            if (distance > bestDistance)
            {
                continue;
            }

            foreach ((uint itemId, string resref, _) in bag.Items)
            {
                if (!_removedForPeers.Contains(itemId)
                    && string.Equals(resref, entry.Resref, StringComparison.OrdinalIgnoreCase))
                {
                    best = bag;
                    bestItem = itemId;
                    bestDistance = distance;
                    break;
                }
            }
        }

        if (best is null)
        {
            return false;
        }

        _removedForPeers.Add(bestItem);
        removal = new ContainerLootRemoval(best.Id, bestItem);
        return true;
    }

    private static float Distance(float x1, float y1, float x2, float y2) =>
        MathF.Sqrt(((x1 - x2) * (x1 - x2)) + ((y1 - y2) * (y1 - y2)));
}
