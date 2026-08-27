using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// What this instance believes the shared party item pool holds: a running quantity per
/// <see cref="PartyPoolStackKey"/>, built up by applying add/remove ops from every peer
/// (including this instance's own, published exactly like a peer's — see
/// <c>CoopHost.PartyPool.cs</c>/<c>CoopClient.PartyPool.cs</c>).
///
/// Same shape as <see cref="ContainerLootStore"/> — <c>Version</c>/<c>Digest</c> tripwire,
/// op-sequence dedup so a UDP retransmit is applied exactly once — generalised from a SET of
/// removed pairs to a MULTISET of quantities, because a party-pool op is not naturally
/// idempotent the way a container removal is (adding the same five grenades twice really is
/// ten grenades, not five). Op-sequence dedup is therefore load-bearing here in a way it is
/// only a safety net for <see cref="ContainerLootStore"/>.
/// </summary>
public sealed class PartyPoolStore
{
    private const int MaxRememberedOps = 4096;

    private readonly Dictionary<PartyPoolStackKey, int> _totals = [];
    private readonly HashSet<(uint Origin, uint Sequence)> _appliedOps = [];
    private readonly Queue<(uint Origin, uint Sequence)> _opHistory = new();
    private readonly Lock _gate = new();
    private ulong _digest = EntryDigest.Empty;

    public uint Version { get; private set; }

    /// <summary>Order-independent content fingerprint, beside <see cref="Version"/> for the
    /// same reason <see cref="EventFlagStore.Digest"/> exists — see <see cref="EntryDigest"/>.</summary>
    public ulong Digest
    {
        get
        {
            lock (_gate)
            {
                return _digest;
            }
        }
    }

    /// <summary>Distinct stack keys currently tracked (zero-quantity keys are removed
    /// outright, so this is exactly the size of a <see cref="Snapshot"/>).</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _totals.Count;
            }
        }
    }

    /// <summary>Caller must hold <see cref="_gate"/>.</summary>
    private bool Mutate(PartyPoolOpKind kind, PartyPoolStackKey key, uint countDelta)
    {
        if (countDelta == 0 && kind != PartyPoolOpKind.Set)
        {
            return false;
        }

        int before = _totals.GetValueOrDefault(key);
        int after = kind switch
        {
            PartyPoolOpKind.Add => before + (int)countDelta,
            PartyPoolOpKind.Remove => Math.Max(0, before - (int)countDelta),

            // Set is ABSOLUTE, not additive — see PartyPoolOpKind.Set's own doc. Used only for
            // a host-authored resync, where replaying history as more Adds could double-count
            // a peer that already holds a different total for this key.
            _ => (int)countDelta,
        };

        if (after == before)
        {
            return false;
        }

        if (before == 0 && after > 0 && _totals.Count >= ProtocolConstants.MaxPartyPoolStacks)
        {
            // Cap only refuses a BRAND NEW key — an existing one can still change quantity
            // (including shrinking to zero, which frees a slot for the next new key).
            return false;
        }

        if (before > 0)
        {
            _digest ^= EntryDigest.PartyPoolEntry(key, before);
        }

        if (after > 0)
        {
            _totals[key] = after;
            _digest ^= EntryDigest.PartyPoolEntry(key, after);
        }
        else
        {
            _totals.Remove(key);
        }

        Version++;
        return true;
    }

    /// <summary>
    /// Records a locally-originated change (this instance's own scan detected it, or the host's
    /// own add/remove is about to be broadcast) — same mutation <see cref="ApplyOp"/> performs,
    /// without the op-sequence dedup a self-originated change does not need. Returns true only
    /// when the total actually changed (a cap refusal or a zero delta returns false).
    /// </summary>
    public bool Record(PartyPoolOpKind kind, PartyPoolStackKey key, uint countDelta)
    {
        lock (_gate)
        {
            return Mutate(kind, key, countDelta);
        }
    }

    /// <summary>
    /// Applies a remote op exactly once. UDP means the same op can arrive several times through
    /// retransmission, and — unlike a container removal — applying the same add twice would be
    /// a genuinely different (wrong) total, not a harmless no-op.
    /// </summary>
    public bool ApplyOp(PartyPoolOpPacket op)
    {
        lock (_gate)
        {
            (uint, uint) opKey = (op.OriginPeerId, op.OpSequence);

            if (!_appliedOps.Add(opKey))
            {
                return false;
            }

            _opHistory.Enqueue(opKey);
            while (_opHistory.Count > MaxRememberedOps)
            {
                _appliedOps.Remove(_opHistory.Dequeue());
            }

            return Mutate(op.Kind, op.Key, op.CountDelta);
        }
    }

    /// <summary>The current quantity of one stack key, for tests and for the pump's own
    /// expectation bookkeeping.</summary>
    public int Get(PartyPoolStackKey key)
    {
        lock (_gate)
        {
            return _totals.GetValueOrDefault(key);
        }
    }

    /// <summary>Every stack key currently held, with its quantity — what the agent re-declares
    /// toward and what a resync offers.</summary>
    public Dictionary<PartyPoolStackKey, int> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<PartyPoolStackKey, int>(_totals);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _totals.Clear();
            _appliedOps.Clear();
            _opHistory.Clear();
            _digest = EntryDigest.Empty;
            Version = 0;
        }
    }
}
