using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The IN-ring replay latch behind <c>PeerEffectController.ApplyIncoming</c>. The defect these
/// pin (live 2026-09-03 00:48, effect-sync.md): the latch was cleared at every world edge while
/// the ring itself never is, so every module arrival replayed the whole ring — a minutes-old
/// heal, a temporary-hp buff and a 6 s confused — onto the fresh character.
/// </summary>
public class PeerEffectReplayRuleTests
{
    /// <summary>The defect in one line: after a world edge the slot still holds peer 1's #3,
    /// the latch adopted it, and it must NOT count as new.</summary>
    [Fact]
    public void AnEntryAdoptedAtTheWorldEdgeIsNotReplayed()
    {
        ulong latch = PeerEffectReplayRule.Adopt(originPeerId: 1, serial: 3);
        Assert.False(PeerEffectReplayRule.IsNew(latch, originPeerId: 1, serial: 3));
    }

    /// <summary>...and the next capture the same peer writes into that slot is.</summary>
    [Fact]
    public void TheNextSerialFromTheSamePeerIsNew()
    {
        ulong latch = PeerEffectReplayRule.Adopt(originPeerId: 1, serial: 3);
        Assert.True(PeerEffectReplayRule.IsNew(latch, originPeerId: 1, serial: 4));
    }

    /// <summary>Serials are per-sender: peer 2's #3 landing in a slot that last held peer 1's
    /// #3 is a different effect, and a serial-only latch would have swallowed it.</summary>
    [Fact]
    public void TheSameSerialFromAnotherPeerIsNew()
    {
        ulong latch = PeerEffectReplayRule.Key(originPeerId: 1, serial: 3);
        Assert.True(PeerEffectReplayRule.IsNew(latch, originPeerId: 2, serial: 3));
    }

    /// <summary>An unwritten slot is never applied, whatever the latch says, and adopting one
    /// leaves the latch empty.</summary>
    [Fact]
    public void AnEmptySlotIsNeverNew()
    {
        Assert.Equal(0UL, PeerEffectReplayRule.Adopt(originPeerId: 1, serial: 0));
        Assert.False(PeerEffectReplayRule.IsNew(0, originPeerId: 1, serial: 0));
        Assert.False(PeerEffectReplayRule.IsNew(PeerEffectReplayRule.Key(1, 3), originPeerId: 1, serial: 0));
    }

    /// <summary>A re-read of an unchanged slot — the ordinary every-fifth-frame case — is not
    /// a second application.</summary>
    [Fact]
    public void ARereadOfTheSameEntryIsNotNew()
    {
        ulong latch = PeerEffectReplayRule.Key(originPeerId: 2, serial: 7);
        Assert.False(PeerEffectReplayRule.IsNew(latch, originPeerId: 2, serial: 7));
    }
}
