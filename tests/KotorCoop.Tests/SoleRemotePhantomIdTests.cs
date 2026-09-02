using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins <see cref="SharedBlockView.SoleRemotePhantomId"/>'s current fallback behaviour for
/// <c>damage_creator_attribution</c> (docs/research/peer-damage-ledger.md §8.6/§8.8 item 4):
/// with exactly one occupied phantom slot the id is unambiguous and returned; with zero, or
/// with two or more (a 3+-peer session), it returns 0 rather than guess.
///
/// This is the enemy-damage path's whole story. <see cref="PeerDamageController.ApplyOwed"/>
/// (src/KotorCoop.Plugin/PeerDamageController.cs) cannot do better than this: its
/// <c>DamageOwedLedger</c> is keyed by ENEMY and merges every contributing peer's rise into
/// ONE total before the controller ever sees a row (peer-damage-ledger.md §3.1, "bank each
/// peer's rise per enemy") — the merge that discards peer identity happens upstream, on the
/// host/session side, not here. There is no per-attacker breakdown left to consult for this
/// direction, unlike the incoming-damage-to-player direction
/// (<see cref="IncomingDamageController.RunPerAttackerPass"/>, which keeps distinct attacker
/// ids because there is only ever one victim — the local player — so nothing is merged away).
/// So this is documentation-only for the enemy path, verified here at the primitive
/// <see cref="SharedBlockView"/> level: no protocol change, no plugin change: same shape as
/// <see cref="MidModuleMovieRelayTests.NewScratchBlock"/>'s scratch-block pattern.
/// </summary>
public sealed class SoleRemotePhantomIdTests
{
    private static unsafe SharedBlockView NewScratchBlock(out byte[] storage)
    {
        storage = GC.AllocateArray<byte>(SharedBlock.TotalSize, pinned: true);
        return new SharedBlockView((byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref storage[0]));
    }

    [Fact]
    public void NoOccupiedSlotsMeansNoAttribution()
    {
        SharedBlockView block = NewScratchBlock(out _);

        Assert.Equal(0u, block.SoleRemotePhantomId());
    }

    [Fact]
    public void ExactlyOneOccupiedSlotIsUnambiguous()
    {
        SharedBlockView block = NewScratchBlock(out _);
        block.SetPhantomId(0, 0x4200_0007);

        Assert.Equal(0x4200_0007u, block.SoleRemotePhantomId());
    }

    [Fact]
    public void TwoRemotePeersFallsBackToZeroRatherThanGuess()
    {
        // The 3+-peer topology this task is about, in its simplest shape: two occupied
        // phantom slots (two remote peers connected) is already enough to make "whose
        // phantom dealt this" ambiguous for a per-enemy MERGED total, so the fallback is 0 —
        // not the first slot, not the last, not a guess.
        SharedBlockView block = NewScratchBlock(out _);
        block.SetPhantomId(0, 0x4200_0007);
        block.SetPhantomId(1, 0x4200_0009);

        Assert.Equal(0u, block.SoleRemotePhantomId());
    }

    [Fact]
    public void ThreeRemotePeersAlsoFallsBackToZero()
    {
        SharedBlockView block = NewScratchBlock(out _);
        block.SetPhantomId(0, 0x4200_0007);
        block.SetPhantomId(1, 0x4200_0009);
        block.SetPhantomId(2, 0x4200_000B);

        Assert.Equal(0u, block.SoleRemotePhantomId());
    }

    [Fact]
    public void ANonAdjacentSingleOccupiedSlotIsStillUnambiguous()
    {
        // The scan does not assume slot 0 is filled first (a peer can occupy any slot as
        // others disconnect and reconnect) — only the COUNT of occupied slots decides
        // ambiguity, not which slot.
        SharedBlockView block = NewScratchBlock(out _);
        block.SetPhantomId(5, 0x4200_0011);

        Assert.Equal(0x4200_0011u, block.SoleRemotePhantomId());
    }
}
