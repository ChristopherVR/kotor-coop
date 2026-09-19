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
    [InlineData((ushort)8, 2, PeerEffectKind.Fear)]         // EffectFrightened (the real one), routine 158 — effect-sync.md §12
    [InlineData((ushort)8, 4, PeerEffectKind.Stunned)]      // EffectStunned (general, non-droid), routine 161 — effect-sync.md §12
    [InlineData((ushort)35, 0, PeerEffectKind.Poison)]      // OnApplyPoison, routine 250 — effect-sync.md §12, once per application
    [InlineData((ushort)62, 0, PeerEffectKind.Disguise)]    // OnApplyDisguise, routine 463 — effect-sync.md §12
    public void MapsEveryVerifiedTypeAndSubCode(ushort engineType, int subCode, PeerEffectKind expected)
    {
        Assert.True(PeerEffectRules.TryMapAllowlist(engineType, subCode, out PeerEffectKind kind));
        Assert.Equal(expected, kind);
    }

    /// <summary>The two type-8 sub-codes effect-sync.md §7.3 named as DISTINCT natives from the
    /// ones already shipped map to their OWN kinds, not to the look-alikes: sub-code 2 is not
    /// <see cref="PeerEffectKind.Frightened"/> (that is Horrified, sub-code 8) and sub-code 4 is
    /// not <see cref="PeerEffectKind.Stun"/> (that is DroidStun, sub-code 3).</summary>
    [Fact]
    public void FearAndStunnedAreDistinctFromTheirLookAlikes()
    {
        Assert.True(PeerEffectRules.TryMapAllowlist(8, 2, out PeerEffectKind fear));
        Assert.True(PeerEffectRules.TryMapAllowlist(8, 8, out PeerEffectKind horrified));
        Assert.NotEqual(fear, horrified);

        Assert.True(PeerEffectRules.TryMapAllowlist(8, 4, out PeerEffectKind stunned));
        Assert.True(PeerEffectRules.TryMapAllowlist(8, 3, out PeerEffectKind droidStun));
        Assert.NotEqual(stunned, droidStun);
    }

    [Theory]
    [InlineData((ushort)8, 9)]   // never minted by anything this pass found
    [InlineData((ushort)8, 0)]   // sub-code only meaningful for type 8; 0 is not one of them
    [InlineData((ushort)38, 0)]  // OnApplyDamage — a real handler, but PhantomHitHook's job
    [InlineData((ushort)19, 0)]  // OnApplyDeath — nothing to do with crowd control
    [InlineData((ushort)0, 0)]   // never dispatched by the engine (type <= 0 is refused upstream)
    [InlineData((ushort)48, 0)]  // OnApplyACIncrease — deliberately NOT shipped, effect-sync.md §10: a
                                  // stat buff on a phantom is meaningless, not the "peer's real body
                                  // feels it" case this channel exists for
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
    [InlineData(PeerEffectKind.Fear, true)]              // routine 158 — effect-sync.md §12
    [InlineData(PeerEffectKind.Stunned, true)]           // routine 161 — effect-sync.md §12
    [InlineData(PeerEffectKind.Poison, true)]            // routine 250 — effect-sync.md §12
    [InlineData(PeerEffectKind.Disguise, true)]          // routine 463 — effect-sync.md §12
    [InlineData(PeerEffectKind.None, false)]
    public void ApplyConstructorCoverageMatchesEffectSyncMd(PeerEffectKind kind, bool expected)
    {
        Assert.Equal(expected, PeerEffectRules.HasApplyConstructor(kind));
    }

    // ---- receiver-side gates (effect-sync.md §12) ---------------------------------------

    /// <summary>Only the two kinds whose apply half has never run live carry their own coop.ini
    /// key on top of <c>effect_sync</c>; Fear and Stunned reuse the live-verified
    /// <c>ApplyComplexEffect</c> path and ride <c>effect_sync</c> alone.</summary>
    [Theory]
    [InlineData(PeerEffectKind.Poison, true)]
    [InlineData(PeerEffectKind.Disguise, true)]
    [InlineData(PeerEffectKind.Fear, false)]
    [InlineData(PeerEffectKind.Stunned, false)]
    [InlineData(PeerEffectKind.Stun, false)]
    [InlineData(PeerEffectKind.Heal, false)]
    public void OnlyPoisonAndDisguiseNeedTheirOwnApplyGate(PeerEffectKind kind, bool expected)
    {
        Assert.Equal(expected, PeerEffectRules.NeedsOwnApplyGate(kind));
    }

    /// <summary>A Disguise magnitude is an appearance.2da row and is refused, not defaulted,
    /// outside the u16 the engine stores it in — zero included (it is "not published", never
    /// row 0). Mirrors <c>OdysseyEffects.IsUsableDisguiseRow</c> / <c>PeerIdentityRule
    /// .IsUsableAppearance</c>.</summary>
    [Theory]
    [InlineData(0u, false)]
    [InlineData(1u, true)]
    [InlineData(114u, true)]      // k_coop_phantom's own row, as a familiar mid-table value
    [InlineData(0xFFFFu, true)]
    [InlineData(0x10000u, false)]
    public void DisguiseRowsAreUsableOnlyInsideTheEngineField(uint row, bool expected)
    {
        Assert.Equal(expected, PeerEffectRules.IsUsableDisguiseRow(row));
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

    /// <summary>The wire's <c>Kind</c> is one byte; every enum member has to fit it, and the
    /// sixteen shipped so far are contiguous 1..16 (the plugin's range check stops at its own
    /// <c>PeerEffectKindValues.Max</c>, which this pins from the protocol side).</summary>
    [Fact]
    public void KindsAreContiguousAndFitTheWireByte()
    {
        PeerEffectKind[] kinds = Enum.GetValues<PeerEffectKind>();
        Assert.Equal(17, kinds.Length); // None + 16
        for (int i = 0; i < kinds.Length; i++)
        {
            Assert.Equal(i, (int)kinds[i]);
        }

        Assert.Equal(PeerEffectKind.Disguise, kinds[^1]);
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
