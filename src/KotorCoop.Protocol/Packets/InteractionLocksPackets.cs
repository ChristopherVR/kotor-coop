namespace KotorCoop.Protocol;

// The per-object interaction lease. Matches PacketCodec.InteractionLocks.cs.

/// <summary>What a lock is for. The release rules differ per kind, and adding one later must
/// not renumber.
///
/// <see cref="Container"/> (version 37, docs/research/container-sync.md) is keyed on
/// <see cref="InteractionClaimPacket.TargetId"/> instead of <see cref="InteractionClaimPacket.Tag"/>:
/// placeables commonly repeat a tag (every generic footlocker in a module is often the SAME
/// tag), where object ids are unique per instance and stable across both installs the same way
/// a door's or an enemy's is — see docs/research/placeables-containers.md. A container claim
/// therefore carries an empty <see cref="InteractionClaimPacket.Tag"/>.</summary>
public enum InteractionKind : byte
{
    None = 0,
    Conversation = 1,
    Container = 2,
}

/// <summary>
/// peer -&gt; host: "I am interacting with <paramref name="Tag"/> (or, for
/// <see cref="InteractionKind.Container"/>, <paramref name="TargetId"/>) right now."
///
/// A TAG, not an object id, for <see cref="InteractionKind.Conversation"/> — the reason the
/// dialogue channel already uses one: the tag is what the dialogue gate compares and what
/// <c>CreatureFinder.FindByTag</c> resolves, and it survives being read on an instance that
/// never loaded the same body. <see cref="InteractionKind.Container"/> is the opposite case —
/// see <see cref="InteractionKind"/>'s own remarks — so it carries <paramref name="TargetId"/>
/// (the placeable's object id) and leaves <paramref name="Tag"/> empty.
///
/// <paramref name="Module"/> rides along for the usual reason — tags and ids are only
/// meaningful where they were read, so the owner must never act on a claim from a peer
/// standing somewhere else.
///
/// Sent every tick while the claim is live. <paramref name="Active"/> false is a release; it
/// is an optimisation, not the safety net (see <see cref="PacketType.InteractionClaim"/>).
/// </summary>
public sealed record InteractionClaimPacket(
    uint PeerId, string Module, string Tag, InteractionKind Kind, bool Active, uint TargetId = 0);

/// <summary>One granted lock, as the host's arbiter sees it. <see cref="TargetId"/> is only
/// meaningful for <see cref="InteractionKind.Container"/> — see <see cref="InteractionKind"/>.</summary>
public readonly record struct InteractionLock(uint PeerId, string Tag, InteractionKind Kind, uint TargetId = 0);

/// <summary>
/// host -&gt; all: every lock currently granted, broadcast each tick.
///
/// The SET is the message. A lock that stops being broadcast is released — there is no
/// "released" entry to lose, which is what makes a client crashing mid-conversation cost a
/// lease rather than an NPC.
/// </summary>
public sealed record InteractionLockPacket(
    long ServerTimeMs, string Module, IReadOnlyList<InteractionLock> Locks);
