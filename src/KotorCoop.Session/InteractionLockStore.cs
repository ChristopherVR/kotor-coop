using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// The host's arbiter for per-object interaction locks.
///
/// docs/research/shared-state-audit.md §4: ownership is one-directional. The host owns
/// enemies, companions, doors and containers; a client that opens a conversation with a
/// host-owned NPC is talking to a puppet, while the host's AI — never told — keeps walking
/// the real one around. This is the missing "hold this one still, I am using it" channel.
///
/// ⚠ A LEASE, NOT A HANDSHAKE. Every entry expires unless the claimant refreshes it, and
/// that is deliberate: the failure that matters is a client dying mid-conversation, and any
/// design where the release is a MESSAGE is a design where losing that message strands an NPC
/// for the rest of the session. Same discipline as the encounter-trigger channel — a level
/// re-sent every tick, aged out here.
/// </summary>
public sealed class InteractionLockStore
{
    /// <summary>
    /// How long a claim survives without a refresh. Equal to
    /// <see cref="ProtocolConstants.PeerTimeoutMs"/> ON PURPOSE: a lock must not be able to
    /// outlive the connection that holds it, and the host drops the peer at the same instant.
    /// Claimants send at the tick rate, so this is many ticks of slack for a dropped datagram
    /// or a loading screen.
    /// </summary>
    public const long DefaultLeaseMs = ProtocolConstants.PeerTimeoutMs;

    private readonly record struct Entry(
        uint PeerId, string Module, string Tag, InteractionKind Kind, long RefreshedAtMs);

    /// <summary>Keyed by (module, lowercased tag) — the question this store answers is "who
    /// owns this object", not "what is this peer doing".</summary>
    private readonly Dictionary<(string Module, string Tag), Entry> _byObject = [];

    private readonly object _gate = new();
    private readonly long _leaseMs;

    public InteractionLockStore(long leaseMs = DefaultLeaseMs) => _leaseMs = leaseMs;

    /// <summary>
    /// Records or refreshes a claim. Returns true when this peer holds the object afterwards.
    ///
    /// FIRST FRESH CLAIM WINS. A second peer claiming a live lock is ignored outright rather
    /// than queued: the loser's own dialogue gate refuses its conversation start, so it finds
    /// out the same way a player already finds out that a peer holds the token.
    /// </summary>
    public bool Claim(InteractionClaimPacket packet, long nowMs)
    {
        if (packet.PeerId == 0 || string.IsNullOrEmpty(packet.Tag))
        {
            return false;
        }

        (string, string) key = Key(packet.Module, packet.Tag);

        lock (_gate)
        {
            bool held = _byObject.TryGetValue(key, out Entry current)
                && nowMs - current.RefreshedAtMs <= _leaseMs;

            if (!packet.Active)
            {
                // Only the holder may release. A stray release from anyone else must not be
                // able to take an NPC away from the player who is talking to it.
                if (held && current.PeerId == packet.PeerId)
                {
                    _byObject.Remove(key);
                }

                return false;
            }

            if (held)
            {
                if (current.PeerId != packet.PeerId)
                {
                    return false; // someone else got here first and is still here
                }

                _byObject[key] = current with { RefreshedAtMs = nowMs };
                return true;
            }

            _byObject[key] = new Entry(
                packet.PeerId, packet.Module ?? string.Empty, packet.Tag, packet.Kind, nowMs);
            return true;
        }
    }

    /// <summary>
    /// Who holds <paramref name="tag"/> in <paramref name="module"/> right now, or 0.
    ///
    /// This is the dialogue arbiter's VETO input: the conversation token may not be granted
    /// over a lock somebody else already holds. Without it the host wins every contested
    /// conversation by construction — its own claim runs the arbiter in-process at zero
    /// latency while a client's costs a round trip.
    /// </summary>
    public uint HolderOf(string module, string tag, long nowMs)
    {
        if (string.IsNullOrEmpty(tag))
        {
            return 0;
        }

        (string, string) key = Key(module, tag);

        lock (_gate)
        {
            return _byObject.TryGetValue(key, out Entry entry) && nowMs - entry.RefreshedAtMs <= _leaseMs
                ? entry.PeerId
                : 0;
        }
    }

    /// <summary>Drops everything a peer holds, so nothing it last said can still hold an
    /// object after it has gone. The lease would do this anyway; calling it on Bye and on the
    /// timeout sweep makes it immediate.</summary>
    public void Forget(uint peerId)
    {
        lock (_gate)
        {
            foreach ((string, string) key in _byObject
                .Where(pair => pair.Value.PeerId == peerId)
                .Select(pair => pair.Key)
                .ToList())
            {
                _byObject.Remove(key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _byObject.Clear();
        }
    }

    /// <summary>
    /// The locks live right now in <paramref name="module"/>: fresh, same module, capped, and
    /// ordered (peer id, then tag) so the same input always produces the same broadcast.
    /// Expired entries are dropped here rather than swept on a timer.
    /// </summary>
    public IReadOnlyList<InteractionLock> Collect(string module, long nowMs)
    {
        List<InteractionLock> live = [];

        lock (_gate)
        {
            List<(string, string)>? expired = null;

            foreach (((string, string) key, Entry entry) in _byObject)
            {
                if (nowMs - entry.RefreshedAtMs > _leaseMs)
                {
                    (expired ??= []).Add(key);
                    continue;
                }

                if (string.Equals(entry.Module, module, StringComparison.OrdinalIgnoreCase))
                {
                    live.Add(new InteractionLock(entry.PeerId, entry.Tag, entry.Kind));
                }
            }

            if (expired is not null)
            {
                foreach ((string, string) key in expired)
                {
                    _byObject.Remove(key);
                }
            }
        }

        live.Sort(static (a, b) => a.PeerId != b.PeerId
            ? a.PeerId.CompareTo(b.PeerId)
            : string.CompareOrdinal(a.Tag, b.Tag));

        return live.Count <= ProtocolConstants.MaxInteractionLocks
            ? live
            : live[..ProtocolConstants.MaxInteractionLocks];
    }

    private static (string, string) Key(string? module, string tag) =>
        ((module ?? string.Empty).ToLowerInvariant(), tag.ToLowerInvariant());
}
