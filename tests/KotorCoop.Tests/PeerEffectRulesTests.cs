using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure decision functions behind the peer-effect channel — see
/// docs/research/effect-sync.md for the engine research these numbers come from.
/// <c>PhantomEffectHook</c> (plugin, NativeAOT, untestable directly) carries its own copy of
/// the allowlist table asserted here; a mismatch between the two is a silent under-capture,
/// not a crash, but these tests are what keeps the copies honest.
/// </summary>
public class PeerEffectRulesTests
{
    // ---- allowlist mapping ---------------------------------------------------------

    [Theory]
    [InlineData((ushort)8, 3, PeerEffectKind.Stun)]        // EffectDroidStun
    [InlineData((ushort)8, 5, PeerEffectKind.Paralyze)]     // EffectParalyze, routine 148 — effect-sync.md §7
    [InlineData((ushort)8, 6, PeerEffectKind.Sleep)]        // EffectSleep, routine 154 — effect-sync.md §7
    [InlineData((ushort)8, 7, PeerEffectKind.Choke)]        // EffectChoke
    [InlineData((ushort)8, 8, PeerEffectKind.Frightened)]   // EffectHorrified
    [InlineData((ushort)8, 10, PeerEffectKind.Whirlwind)]   // EffectWhirlWind
    [InlineData((ushort)20, 0, PeerEffectKind.Knockdown)]   // OnApplyKnockdown, no sub-code
    [InlineData((ushort)29, 0, PeerEffectKind.Slow)]        // OnApplyMovementSpeedDecrease
    [InlineData((ushort)60, 0, PeerEffectKind.ForcePush)]   // OnApplyForcePush
    [InlineData((ushort)8, 1, PeerEffectKind.Confused)]     // EffectConfused, routine 157 — effect-sync.md §10
    [InlineData((ushort)39, 0, PeerEffectKind.Heal)]        // OnApplyHeal, routine 78 — effect-sync.md §10
    [InlineData((ushort)15, 0, PeerEffectKind.TemporaryHitpoints)] // OnApplyTemporaryHitpoints, routine 314 — effect-sync.md §10
    public void MapsEveryVerifiedTypeAndSubCode(ushort engineType, int subCode, PeerEffectKind expected)
    {
        Assert.True(PeerEffectRules.TryMapAllowlist(engineType, subCode, out PeerEffectKind kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData((ushort)8, 2)]   // EffectFrightened, routine 158 — NAMED but not shipped (distinct from sub-code 8)
    [InlineData((ushort)8, 4)]   // EffectStunned (general, non-droid), routine 161 — NAMED but not shipped
    [InlineData((ushort)8, 9)]   // never minted by anything this pass found
    [InlineData((ushort)8, 0)]   // sub-code only meaningful for type 8; 0 is not one of them
    [InlineData((ushort)38, 0)]  // OnApplyDamage — a real handler, but PhantomHitHook's job
    [InlineData((ushort)19, 0)]  // OnApplyDeath — nothing to do with crowd control
    [InlineData((ushort)0, 0)]   // never dispatched by the engine (type <= 0 is refused upstream)
    [InlineData((ushort)48, 0)]  // OnApplyACIncrease — deliberately NOT shipped, effect-sync.md §10: a
                                  // stat buff on a phantom is meaningless, not the "peer's real body
                                  // feels it" case this channel exists for
    [InlineData((ushort)35, 0)]  // OnApplyPoison — deliberately NOT shipped, effect-sync.md §10: poison
                                  // damage already re-enters the existing OnApplyDamage/PhantomHitHook
                                  // channel, no new kind needed
    [InlineData((ushort)31, 0)]  // OnApplyAreaOfEffect — deliberately NOT shipped, effect-sync.md §10:
                                  // spawns a script-driven trigger volume, not a "one effect landed on a
                                  // phantom" event this channel's model covers
    public void RefusesEverythingNotOnTheAllowlist(ushort engineType, int subCode)
    {
        Assert.False(PeerEffectRules.TryMapAllowlist(engineType, subCode, out PeerEffectKind kind));
        Assert.Equal(PeerEffectKind.None, kind);
    }

    // ---- apply-side constructor coverage ---------------------------------------------

    [Theory]
    [InlineData(PeerEffectKind.Stun, true)]
    [InlineData(PeerEffectKind.Slow, true)]
    [InlineData(PeerEffectKind.Frightened, true)]
    [InlineData(PeerEffectKind.Choke, true)]
    [InlineData(PeerEffectKind.ForcePush, true)]
    [InlineData(PeerEffectKind.Whirlwind, true)]
    [InlineData(PeerEffectKind.Knockdown, true)]    // routine 134 — effect-sync.md §7
    [InlineData(PeerEffectKind.Paralyze, true)]     // routine 148 — effect-sync.md §7
    [InlineData(PeerEffectKind.Sleep, true)]        // routine 154 — effect-sync.md §7
    [InlineData(PeerEffectKind.Heal, true)]              // routine 78 — effect-sync.md §10
    [InlineData(PeerEffectKind.TemporaryHitpoints, true)] // routine 314 — effect-sync.md §10
    [InlineData(PeerEffectKind.Confused, true)]          // routine 157 — effect-sync.md §10
    [InlineData(PeerEffectKind.None, false)]
    public void ApplyConstructorCoverageMatchesEffectSyncMd(PeerEffectKind kind, bool expected)
    {
        Assert.Equal(expected, PeerEffectRules.HasApplyConstructor(kind));
    }

    /// <summary>Every allowlist member has an apply-side constructor (effect-sync.md §7,
    /// 2026-08-22; extended §10, 2026-09-02) — pinned as its own fact so a future regression
    /// (someone reverting one of the added cases) fails loudly rather than only showing up as
    /// a parametrized-theory gap.</summary>
    [Fact]
    public void EveryAllowlistMemberHasAnApplyConstructor()
    {
        foreach (PeerEffectKind kind in Enum.GetValues<PeerEffectKind>())
        {
            if (kind == PeerEffectKind.None)
            {
                continue;
            }

            Assert.True(
                PeerEffectRules.HasApplyConstructor(kind),
                $"{kind} should have an apply-side constructor as of effect-sync.md §7");
        }
    }

    // ---- dedup (SelectNew) -----------------------------------------------------------

    [Fact]
    public void SelectsOnlyEntriesAboveTheWatermark()
    {
        uint[] serials = [5, 10, 3, 12, 0];

        (System.Collections.Generic.IReadOnlyList<uint> indices, uint watermark) =
            PeerEffectRules.SelectNew(serials, watermark: 5);

        // index 0 (serial 5) is AT the watermark, not above it — excluded.
        // index 2 (serial 3) is below — excluded. index 4 (serial 0) is the "never
        // written" sentinel — always excluded, watermark or not.
        Assert.Equal([1u, 3u], indices);
        Assert.Equal(12u, watermark);
    }

    [Fact]
    public void EmptyRingSelectsNothingAndKeepsTheWatermark()
    {
        (System.Collections.Generic.IReadOnlyList<uint> indices, uint watermark) =
            PeerEffectRules.SelectNew([], watermark: 41);

        Assert.Empty(indices);
        Assert.Equal(41u, watermark);
    }

    [Fact]
    public void EverythingAtOrBelowTheWatermarkIsDropped()
    {
        uint[] serials = [1, 2, 3];

        (System.Collections.Generic.IReadOnlyList<uint> indices, uint watermark) =
            PeerEffectRules.SelectNew(serials, watermark: 3);

        Assert.Empty(indices);
        Assert.Equal(3u, watermark);
    }

    [Fact]
    public void ABurstLargerThanTheRingStillReturnsEveryEntryInOneRead()
    {
        // The ring itself can only ever hold RingCapacity entries at once, but SelectNew
        // does not know or care how many slots produced the serials it is handed — it is
        // pure over whatever list it is given. A full ring's worth of NEW entries in one
        // read must all come back, not just the first one found.
        uint[] serials = new uint[PeerEffectRules.RingCapacity];
        for (int i = 0; i < serials.Length; i++)
        {
            serials[i] = (uint)(i + 1);
        }

        (System.Collections.Generic.IReadOnlyList<uint> indices, uint watermark) =
            PeerEffectRules.SelectNew(serials, watermark: 0);

        Assert.Equal(PeerEffectRules.RingCapacity, indices.Count);
        Assert.Equal((uint)PeerEffectRules.RingCapacity, watermark);
    }

    // ---- ring index advance ------------------------------------------------------

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(6, 7)]
    public void RingIndexAdvancesByOne(int current, int expected)
    {
        Assert.Equal(expected, PeerEffectRules.NextRingIndex(current));
    }

    [Fact]
    public void RingIndexWrapsAtCapacity()
    {
        Assert.Equal(0, PeerEffectRules.NextRingIndex(PeerEffectRules.RingCapacity - 1));
    }

    [Fact]
    public void RingCapacityMatchesTheSharedBlockRegion()
    {
        // SharedBlock.PeerEffectCapacity (plugin) and PeerEffectRules.RingCapacity
        // (protocol) are two numbers for the same fact; this is the closest a test outside
        // the plugin assembly can come to asserting they agree.
        Assert.Equal(8, PeerEffectRules.RingCapacity);
    }
}
