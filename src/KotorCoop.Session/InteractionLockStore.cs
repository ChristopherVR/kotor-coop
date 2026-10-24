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
///
/// KEYED PER KIND (version 37, docs/research/container-sync.md): a <see cref="InteractionKind.Conversation"/>
/// claim is keyed on its TAG, exactly as before — NPC tags are authored unique. A
/// <see cref="InteractionKind.Container"/> claim is keyed on its object id instead: the base
/// game routinely places several placeables with the SAME tag (every generic footlocker in a
/// module, for instance), so keying a container the way a conversation is keyed would let two
/// unrelated footlockers fight over one lock, or worse, let a lock on footlocker A silently
/// "protect" footlocker B. Object ids are unique per instance and stable across both installs
/// (same save, same deterministic GIT order) the same way a door's or an enemy's already is.
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
        uint PeerId, string Module, string Tag, InteractionKind Kind, uint TargetId, long RefreshedAtMs,
        uint RequestSerial);

    /// <summary>Keyed by (module, discriminated object key) — the question this store answers
    /// is "who owns this object", not "what is this peer doing". See the class remarks for why
    /// the key differs by <see cref="InteractionKind"/>.</summary>
    private readonly Dictionary<(string Module, string Key), Entry> _byObject = [];

    private readonly object _gate = new();
    private readonly long _leaseMs;

    public InteractionLockStore(long leaseMs = DefaultLeaseMs) => _leaseMs = leaseMs;

    /// <summary>
    /// Records or refreshes a claim. Returns true when this peer holds the object afterwards.
    ///
    /// FIRST FRESH CLAIM WINS. A second peer claiming a live lock is ignored outright rather
    /// than queued: the loser's own gate refuses its conversation/container-use start, so it
    /// finds out the same way a player already finds out that a peer holds the token.
    /// </summary>
    public bool Claim(InteractionClaimPacket packet, long nowMs)
    {
        if (packet.PeerId == 0 || !TryKey(packet.Kind, packet.Tag, packet.TargetId, out string key))
        {
            return false;
        }

        (string, string) fullKey = (Normalise(packet.Module), key);

        lock (_gate)
        {
            bool fresh = _byObject.TryGetValue(fullKey, out Entry current)
                && nowMs - current.RefreshedAtMs <= _leaseMs;

            if (!packet.Active)
            {
                // ContainerUseRule.MayRelease: only the current fresh holder may release.
                // An older close can arrive after a new open reservation, including while
                // its grant is still on the wire. Only the close for this reservation may
                // release it. Unarbitrated/legacy claims retain their zero-serial behavior.
                bool matchingAttempt = packet.Kind != InteractionKind.Container
                    || current.RequestSerial == 0 || packet.RequestSerial == current.RequestSerial;
                if (matchingAttempt && ContainerUseRule.MayRelease(current.PeerId, fresh, packet.PeerId))
                {
                    _byObject.Remove(fullKey);
                }

                return false;
            }

            if (!ContainerUseRule.MayClaim(current.PeerId, fresh, packet.PeerId))
            {
                return false; // someone else got here first and is still here
            }

            // Ordinary active levels have serial zero; they refresh rather than erase the
            // reservation epoch. A fresh explicit request by the owner begins a new epoch.
            uint epoch = packet.Kind == InteractionKind.Container ? packet.RequestSerial : 0;
            if (fresh && epoch != 0 && current.RequestSerial != 0
                && unchecked((int)(epoch - current.RequestSerial)) < 0)
                return false;

            _byObject[fullKey] = fresh
                ? current with { RefreshedAtMs = nowMs, RequestSerial = epoch != 0 ? epoch : current.RequestSerial }
                : new Entry(
                    packet.PeerId, packet.Module ?? string.Empty, packet.Tag ?? string.Empty,
                    packet.Kind, packet.TargetId, nowMs, epoch);
            return true;
        }
    }

    /// <summary>
    /// Who holds the <see cref="InteractionKind.Conversation"/> claim on <paramref name="tag"/>
    /// in <paramref name="module"/> right now, or 0.
    ///
    /// This is the dialogue arbiter's VETO input: the conversation token may not be granted
    /// over a lock somebody else already holds. Without it the host wins every contested
    /// conversation by construction — its own claim runs the arbiter in-process at zero
    /// latency while a client's costs a round trip.
    /// </summary>
    public uint HolderOf(string module, string tag, long nowMs)
    {
        if (string.IsNullOrEmpty(tag) || !TryKey(InteractionKind.Conversation, tag, 0, out string key))
        {
            return 0;
        }

        return HolderOfKey(module, key, nowMs);
    }

    /// <summary>
    /// Who holds the <see cref="InteractionKind.Container"/> claim on placeable
    /// <paramref name="targetId"/> in <paramref name="module"/> right now, or 0.
    ///
    /// The container-use gate's VETO input — see docs/research/container-sync.md. A claim with
    /// no holder (0) is never refused: the engine's own scripted open must always go through.
    /// </summary>
    public uint HolderOfContainer(string module, uint targetId, long nowMs)
    {
        if (targetId == 0 || !TryKey(InteractionKind.Container, string.Empty, targetId, out string key))
        {
            return 0;
        }

        return HolderOfKey(module, key, nowMs);
    }

    private uint HolderOfKey(string module, string key, long nowMs)
    {
        (string, string) fullKey = (Normalise(module), key);

        lock (_gate)
        {
            return _byObject.TryGetValue(fullKey, out Entry entry) && nowMs - entry.RefreshedAtMs <= _leaseMs
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
    /// ordered (peer id, then tag, then target id) so the same input always produces the same
    /// broadcast. Expired entries are dropped here rather than swept on a timer.
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
                    live.Add(new InteractionLock(entry.PeerId, entry.Tag, entry.Kind, entry.TargetId));
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

        live.Sort(static (a, b) =>
        {
            if (a.PeerId != b.PeerId)
            {
                return a.PeerId.CompareTo(b.PeerId);
            }

            int byTag = string.CompareOrdinal(a.Tag, b.Tag);
            return byTag != 0 ? byTag : a.TargetId.CompareTo(b.TargetId);
        });

        return live.Count <= ProtocolConstants.MaxInteractionLocks
            ? live
            : live[..ProtocolConstants.MaxInteractionLocks];
    }

    /// <summary>
    /// The discriminated object key for a claim: a <see cref="InteractionKind.Conversation"/>
    /// is keyed on its lowercased TAG (unchanged from before this kind existed), a
    /// <see cref="InteractionKind.Container"/> on its object id — see the class remarks for
    /// why the two cannot share a keying scheme. Returns false for a claim missing the field
    /// its own kind actually needs (an empty tag for a conversation, a zero id for a
    /// container) — the same "an empty tag claims nothing" refusal the store always had, now
    /// per kind rather than universal.
    /// </summary>
    private static bool TryKey(InteractionKind kind, string? tag, uint targetId, out string key)
    {
        switch (kind)
        {
            case InteractionKind.Container:
                if (targetId == 0)
                {
                    key = string.Empty;
                    return false;
                }

                key = "id:" + targetId.ToString("X8");
                return true;

            default: // Conversation, and any future kind that keys on tag until told otherwise
                if (string.IsNullOrEmpty(tag))
                {
                    key = string.Empty;
                    return false;
                }

                key = "tag:" + tag.ToLowerInvariant();
                return true;
        }
    }

    private static string Normalise(string? module) => (module ?? string.Empty).ToLowerInvariant();
}
