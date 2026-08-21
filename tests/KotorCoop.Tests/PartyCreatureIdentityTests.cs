using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Regression tests for the party-presence staleness bug (docs/research/party-reconciliation.md,
/// dated entry 2026-08-22, "Live pass"): <c>PartyPresenceController.Resolve</c> used to compare
/// <c>CGameObject+0x08 == 5</c> to decide whether a cached or freshly-resolved companion pointer
/// was still a live creature. That field is not a clean type enum — this codebase's own
/// object-type-field-trap lesson already proved a live creature can read something like
/// <c>0x40005</c> there — so the exact-5 comparison intermittently rejected a perfectly live,
/// perfectly resolvable companion, and <c>Resolve</c> returning 0 froze the mirrored body (while
/// every vtable-checked channel, like the enemy census, kept moving). The fix is
/// <see cref="PartyCreatureIdentity.IsLiveMatch"/>: identity by vtable, exactly like
/// <c>EnemyPresenceController</c> has always done, and never by the flag-bearing type field.
/// </summary>
public class PartyCreatureIdentityTests
{
    private const nint CreatureVtable = PartyCreatureIdentity.CreatureVtable;
    private const uint ObjectId = 0x11C;

    [Fact]
    public void ALiveCreatureWithTheOrdinaryTypeFieldMatches()
    {
        Assert.True(PartyCreatureIdentity.IsLiveMatch(CreatureVtable, ObjectId, ObjectId));
    }

    [Fact]
    public void TheTypeFieldFlagBitsThatUsedToFreezeACompanionAreIrrelevantNow()
    {
        // 0x40005 is the exact value docs/research/party-reconciliation.md measured live for a
        // running, driveable creature at CGameObject+0x08 — the old `== 5` comparison rejected
        // this candidate outright. The identity check below never reads that field at all, so
        // the same creature (same vtable, same id) is accepted regardless of what +0x08 holds.
        Assert.True(PartyCreatureIdentity.IsLiveMatch(CreatureVtable, ObjectId, ObjectId));
    }

    [Fact]
    public void AMismatchedIdIsRejectedEvenWithTheRightVtable()
    {
        // Stale reused memory: the vtable word survives a free/reuse cycle, but the id no
        // longer belongs to the creature this slot is tracking.
        Assert.False(PartyCreatureIdentity.IsLiveMatch(CreatureVtable, ObjectId + 1, ObjectId));
    }

    [Fact]
    public void AMismatchedVtableIsRejectedEvenWithTheRightId()
    {
        // A non-creature object (an item's embedded CSWSObject, for instance) that happens to
        // carry the same numeric id must never be accepted as the companion.
        Assert.False(PartyCreatureIdentity.IsLiveMatch(unchecked((nint)0x00748DA0), ObjectId, ObjectId));
    }

    [Fact]
    public void AZeroedCandidateNeverMatches()
    {
        Assert.False(PartyCreatureIdentity.IsLiveMatch(0, 0, ObjectId));
    }
}
