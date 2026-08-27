// Per-object interaction claims — "hold this one still, I am using it" — leased, not released.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IInteractionLockSession
{
    /// <summary>
    /// Claims (or stops claiming) a single world object — "hold this one still, I am using
    /// it". An empty <paramref name="tag"/> (and, for <see cref="InteractionKind.Container"/>,
    /// a zero <paramref name="targetId"/>) claims nothing and only keeps the session's idea
    /// of the current module fresh, so this is safe and expected to call every tick.
    ///
    /// <paramref name="targetId"/> is the placeable object id a
    /// <see cref="InteractionKind.Container"/> claim carries (docs/research/container-sync.md);
    /// ignored for every other kind, which keys on <paramref name="tag"/> instead — see
    /// <c>InteractionLockStore</c>'s own remarks for why the two cannot share a key.
    ///
    /// A LEASE: the claim is re-sent while it is held and expires on its own if the claimant
    /// goes away. There is no release message to lose, which is the point — see
    /// <c>InteractionLockStore</c>.
    /// </summary>
    void PublishInteractionClaim(
        string module, string tag, InteractionKind kind, bool active, uint targetId = 0) { }

    /// <summary>Every object currently held, by anyone, in the local module. Each peer applies
    /// this to the objects IT owns.</summary>
    IReadOnlyList<InteractionLock> GetInteractionLocks() => [];

    /// <summary>
    /// Asks the host to arbitrate a <see cref="InteractionKind.Container"/> claim RIGHT NOW,
    /// rather than leaving it to <see cref="PublishInteractionClaim"/>'s periodic level to
    /// eventually converge — the fix for the container-loot race
    /// (docs/roadmap-seamless.md Tier 1 item 4, docs/research/container-sync.md's
    /// <c>container_lock_arbitration</c> section): two near-simultaneous clicks on the same
    /// container both used to proceed locally before either claim had crossed the wire.
    ///
    /// Symmetric by construction: a host's own implementation answers synchronously,
    /// in-process (it IS the arbiter); a client's implementation sends one packet to the host
    /// and the answer arrives later through <see cref="TryGetLockResponse"/>. Either way the
    /// caller polls <see cref="TryGetLockResponse"/> with the same <paramref name="requestSerial"/>
    /// — there is no host/client branch on the calling side. <paramref name="requestSerial"/>
    /// must be nonzero (0 is reserved for "no request pending"); a zero <paramref name="targetId"/>
    /// or <paramref name="requestSerial"/> is silently ignored. Default no-op for a session that
    /// predates this.
    /// </summary>
    void RequestContainerLock(string module, uint targetId, uint requestSerial) { }

    /// <summary>
    /// The answer to the most recent <see cref="RequestContainerLock"/> whose serial matches
    /// <paramref name="requestSerial"/>, if one has arrived yet. The caller (the plugin's
    /// deferral state machine, polled through the shared block — see
    /// docs/research/container-sync.md) is expected to give up and fall back to "proceed as
    /// before" after its own short timeout if this never returns true; a lost
    /// <see cref="KotorCoop.Protocol.LockResponsePacket"/> is exactly as safe to abandon as a
    /// lost <see cref="KotorCoop.Protocol.InteractionLockPacket"/> already is. Default: never
    /// answers, for a session that predates this.
    /// </summary>
    bool TryGetLockResponse(uint requestSerial, out bool granted)
    {
        granted = false;
        return false;
    }
}
