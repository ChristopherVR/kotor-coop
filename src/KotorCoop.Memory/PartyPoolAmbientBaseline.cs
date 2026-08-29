using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>
/// How <see cref="KotorGameBridge.TryScanPartyPoolTotals"/> keeps ambient-script noise out of the
/// totals it reports. Extracted from the scan loop so the arithmetic is unit-testable on its own
/// (<c>PartyPoolAmbientBaselineTests</c>) without a game process, following this codebase's own
/// "pure rule class carries the logic" idiom.
///
/// ⚠ 2026-08-29 (docs/research/shared-inventory-v2.md §12) — this used to be "skip the suppressed
/// item", and that was wrong in a way three live rig rounds kept tripping over:
///
/// Skipping is only correct while an item id keeps its own separate stack forever, and the engine
/// does not work that way. Same-key stacks merge, and a merge can destroy EITHER id. A suppressed
/// 6-unit ambient stack merging into a counted stack therefore made the COUNTED total jump by 6
/// with nothing in the pool having actually changed — which <c>Pumps.PartyPoolPumpV2</c>'s
/// belief-delta detect step read as a genuine local deposit of 6 and published to the host as one.
/// That is exactly the unprompted "local add of 6x" §10.1 recorded, and the same shape §6.2's
/// doubling ramp took on <c>g_w_blstrpstl001</c>/<c>g_a_clothes01</c> — end_m01aa's own ambient
/// tutorial gear, keys nobody in that round ever touched. The reverse merge produced the
/// mirror-image phantom REMOVE (§10.1's "remove of 4x ... no matching stack found").
///
/// A per-KEY baseline is immune to it: the aggregate over every item of a key is stable however
/// the engine reshuffles ids between stacks, and the ambient contribution comes off it as a
/// constant offset. Suppression itself (<see cref="KotorGameBridge.IsAmbientPartyPoolNoise"/>, a
/// permanent per-id decision, docs/research/party-pool-sync.md §20) is unchanged — only what is
/// DONE with the answer.
/// </summary>
internal sealed class PartyPoolAmbientBaseline
{
    private readonly Dictionary<PartyPoolStackKey, int> _baseline = [];

    private readonly HashSet<uint> _counted = [];

    /// <summary>Test seam: what is currently being subtracted from each key.</summary>
    internal IReadOnlyDictionary<PartyPoolStackKey, int> Current => _baseline;

    /// <summary>
    /// Records <paramref name="itemId"/>'s own <paramref name="stackSize"/> against
    /// <paramref name="key"/> as ambient content, once and once only for that id however many
    /// scans see it. Returns true the first time.
    /// </summary>
    internal bool NoteSuppressed(uint itemId, PartyPoolStackKey key, int stackSize)
    {
        if (stackSize <= 0 || !_counted.Add(itemId))
        {
            return false;
        }

        _baseline[key] = _baseline.GetValueOrDefault(key) + stackSize;
        return true;
    }

    /// <summary>
    /// The sync-relevant totals for a scan whose RAW aggregate (every item of a key, suppressed
    /// or not) is <paramref name="raw"/>. Also self-corrects the baseline against what is
    /// actually there: an offset can never exceed a key's real raw total (part of the ambient
    /// content was consumed), and a key gone from the pool entirely drops its offset outright —
    /// otherwise a used-up ambient medpac would silently eat an unrelated genuine deposit of the
    /// same resref later in the session. Keys that net to zero are absent from the result, the
    /// same way a genuinely empty key always has been.
    /// </summary>
    internal Dictionary<PartyPoolStackKey, int> Net(IReadOnlyDictionary<PartyPoolStackKey, int> raw)
    {
        List<PartyPoolStackKey>? goneEntirely = null;
        foreach ((PartyPoolStackKey key, int baseline) in _baseline)
        {
            if (!raw.TryGetValue(key, out int rawTotal))
            {
                (goneEntirely ??= []).Add(key);
            }
            else if (baseline > rawTotal)
            {
                _baseline[key] = rawTotal;
            }
        }

        if (goneEntirely is not null)
        {
            foreach (PartyPoolStackKey key in goneEntirely)
            {
                _baseline.Remove(key);
            }
        }

        Dictionary<PartyPoolStackKey, int> net = [];
        foreach ((PartyPoolStackKey key, int rawTotal) in raw)
        {
            int value = rawTotal - _baseline.GetValueOrDefault(key);
            if (value > 0)
            {
                net[key] = value;
            }
        }

        return net;
    }

    internal void Clear()
    {
        _baseline.Clear();
        _counted.Clear();
    }
}
