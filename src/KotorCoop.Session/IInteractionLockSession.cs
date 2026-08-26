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
}
