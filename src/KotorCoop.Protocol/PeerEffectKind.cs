namespace KotorCoop.Protocol;

/// <summary>
/// The non-damage effects <c>PhantomEffectHook</c> forwards from a peer's phantom to that
/// peer's real character — the numeric identity that actually crosses the wire, deliberately
/// NOT the engine's own effect-type/sub-code pair.
///
/// See docs/research/effect-sync.md for the full derivation. The short version: every one of
/// these funnels through <c>CSWSEffectListHandler::OnEffectApplied</c>'s 110-entry dispatch
/// table; most (Stun, Paralyze, Sleep, Choke, Frightened, Whirlwind, Confused) are registered
/// under the SAME table entry (effect type 8, <c>OnApplySetState</c>), distinguished only by an
/// integer sub-code the game itself reads out of the effect, while Knockdown/Slow/ForcePush/
/// Heal/TemporaryHitpoints each have their own dedicated table entry. As of effect-sync.md §7
/// (2026-08-22) — extended §10 (2026-09-02) for Heal/TemporaryHitpoints/Confused — every member
/// of this enum has a real, VERIFIED-by-name identity and a real apply-side constructor in
/// <c>OdysseyEffects</c>. Knockdown/Paralyze/Sleep were named and wired up by disassembling
/// <c>CSWVirtualMachineCommands::InitializeCommands</c>' routine-table population directly and
/// cross-checking against a K1 <c>nwscript.nss</c>'s declaration order. See the
/// VERIFIED/NEEDS-LIVE table in effect-sync.md §3.1/§7/§10 before trusting any one of these
/// names over what the research doc actually says about it — and see
/// <see cref="Frightened"/>'s own note for one case where the ENGLISH NAME (not the numeric
/// identity) turned out to be a pre-existing guess that a genuinely different, still-unshipped
/// engine effect actually owns.
/// </summary>
public enum PeerEffectKind : byte
{
    None = 0,

    /// <summary>Engine effect type 8, sub-code 3 — VERIFIED BY NAME
    /// (<c>ExecuteCommandEffectDroidStun</c>), but droid-scoped: whether a separate, general
    /// stun exists among the unnamed sub-codes below is open. Used as this pass's Stun.</summary>
    Stun = 1,

    /// <summary>Engine effect type 8, sub-code 5 — VERIFIED BY NAME (routine 148,
    /// <c>EffectParalyze</c>; effect-sync.md §7, 2026-08-22). Fully implemented. NOTE:
    /// Knockdown's capture path already worked before Paralyze/Sleep were named; Paralyze's
    /// capture path needs <c>PhantomEffectHook.cs</c>'s copy of the allowlist updated too
    /// (out of scope for the pass that named this — see effect-sync.md §7's desync note). The
    /// apply-side constructor works and is locally testable regardless.</summary>
    Paralyze = 2,

    /// <summary>Engine effect type 20 (<c>OnApplyKnockdown</c>) — VERIFIED numerically AND by
    /// name (routine 134, <c>EffectKnockdown</c>; effect-sync.md §7, 2026-08-22). Reachable
    /// through the shared <c>ExecuteCommandEffectComplex</c> handler at its own verified
    /// routine number — the handler dispatches on the CALLING routine number (not a popped
    /// script argument), so it is safe to invoke with zero pushed arguments. Fully
    /// implemented, both capture and apply.</summary>
    Knockdown = 3,

    /// <summary>Engine effect type 29 (<c>OnApplyMovementSpeedDecrease</c>) — VERIFIED, single
    /// <c>int</c> parameter (percent). Fully implemented.</summary>
    Slow = 4,

    /// <summary>Engine effect type 8, sub-code 8 — VERIFIED BY NAME
    /// (<c>ExecuteCommandEffectHorrified</c>). Fully implemented.
    /// ⚠ NAMING NOTE (effect-sync.md §7, 2026-08-22): K1's nwscript.nss declares a SEPARATE,
    /// distinct native <c>EffectFrightened()</c> (routine 158, engine sub-code 2) — NOT this
    /// one. This wire member's English name was a semantic guess made before either name was
    /// recoverable; its NUMERIC identity (sub-code 8 / <c>EffectHorrified</c>) is correct and
    /// unchanged, and it is kept as-is (not renamed) to avoid a wire-breaking change for an
    /// already-shipped, live-tested kind. The real <c>EffectFrightened</c> (sub-code 2) is
    /// <see cref="Fear"/> (effect-sync.md §12, 2026-09-18).</summary>
    Frightened = 5,

    /// <summary>Engine effect type 8, sub-code 6 — VERIFIED BY NAME (routine 154,
    /// <c>EffectSleep</c>; effect-sync.md §7, 2026-08-22). Fully implemented. See
    /// <see cref="Paralyze"/>'s note — the apply-side constructor works and is locally
    /// testable; the capture-side allowlist in <c>PhantomEffectHook.cs</c> needs a matching
    /// update (out of scope here) before a peer-forwarded live test will see it.</summary>
    Sleep = 6,

    /// <summary>Engine effect type 8, sub-code 7 — VERIFIED BY NAME
    /// (<c>ExecuteCommandEffectChoke</c>). Fully implemented.</summary>
    Choke = 7,

    /// <summary>Engine effect type 60 (<c>OnApplyForcePush</c>) — VERIFIED, zero parameters.
    /// Fully implemented.</summary>
    ForcePush = 8,

    /// <summary>Engine effect type 8, sub-code 10 — VERIFIED BY NAME
    /// (<c>ExecuteCommandEffectWhirlWind</c>). Fully implemented.</summary>
    Whirlwind = 9,

    /// <summary>Engine effect type 39 (<c>OnApplyHeal</c>), VM constructor
    /// <c>ExecuteCommandEffectHeal</c> (<c>0x00532A70</c>, routine 78) — VERIFIED single
    /// <c>int nDamageToHeal</c> parameter, effect-sync.md §10 (2026-09-02): the constructor
    /// gates on the caller's declared param count (refuses up front if not exactly 1), pops
    /// the int through the shared VM stack-pop helper, and self-neuters into a null/no-op
    /// effect type if the popped value is not positive. Applied INSTANT (like ForcePush), not
    /// durable. The one member of this channel that forwards something HELPFUL rather than
    /// harmful: a medpac/Cure/stim applied to a peer's phantom reaches their real body the
    /// same way a hostile effect does — real gameplay, not cosmetics.</summary>
    Heal = 10,

    /// <summary>Engine effect type 15 (<c>OnApplyTemporaryHitpoints</c>), VM constructor
    /// <c>ExecuteCommandEffectTemporaryHitpoints</c> (<c>0x00534490</c>, routine 314) —
    /// VERIFIED single <c>int nHitPoints</c> parameter, effect-sync.md §10 (2026-09-02); same
    /// one-int shape as Heal, but applied DURABLE (temporary, real duration) rather than
    /// instant.</summary>
    TemporaryHitpoints = 11,

    /// <summary>Engine effect type 8, sub-code 1 (routine 157, <c>EffectConfused</c>) —
    /// VERIFIED BY NAME effect-sync.md §7.3 (2026-08-22; named but not shipped that pass).
    /// Shipped effect-sync.md §10 (2026-09-02) through the same
    /// <c>ExecuteCommandEffectComplex</c> shared-handler routing Knockdown/Paralyze/Sleep
    /// already use — zero pushed script arguments, dispatched on the calling routine
    /// number.</summary>
    Confused = 12,

    /// <summary>Engine effect type 8, sub-code 2 (routine 158, <c>EffectFrightened</c>) — the
    /// genuine <c>EffectFrightened</c> native, named in effect-sync.md §7.3 (2026-08-22) and
    /// shipped §12 (2026-09-18). NOT the member called <see cref="Frightened"/> above, which is
    /// really <c>EffectHorrified</c> (sub-code 8) and keeps its old label for wire stability.
    /// Same <c>ExecuteCommandEffectComplex</c> zero-pushed-argument routing as
    /// Knockdown/Paralyze/Sleep/Confused, at its own verified routine number.</summary>
    Fear = 13,

    /// <summary>Engine effect type 8, sub-code 4 (routine 161, <c>EffectStunned</c>) — the
    /// GENERAL, non-droid stun; <see cref="Stun"/> above is the droid-scoped
    /// <c>EffectDroidStun</c> (sub-code 3). Named §7.3, shipped §12 (2026-09-18) through the
    /// same <c>ExecuteCommandEffectComplex</c> routing as <see cref="Fear"/>.</summary>
    Stunned = 14,

    /// <summary>Engine effect type 35 (<c>OnApplyPoison</c>), VM constructor
    /// <c>ExecuteCommandEffectPoison</c> (<c>0x00533560</c>, routine 250) — VERIFIED single
    /// <c>int nPoisonType</c> parameter (poison.2da row), effect-sync.md §12 (2026-09-18). The
    /// magnitude carries the poison type. Captured ONCE per application (the dispatcher sees
    /// <c>OnApplyPoison</c> once; the ticks are ordinary type-38 damage, §10.3), applied durable
    /// for the effect's own duration. Apply-side gated by coop.ini <c>poison_sync</c> (default
    /// OFF, NEEDS-LIVE — see §12 for the double-count question it must answer first).</summary>
    Poison = 15,

    /// <summary>Engine effect type 62 (<c>OnApplyDisguise</c>), VM constructor
    /// <c>EffectDisguise(int nDisguiseAppearance)</c> = routine 463, one of the
    /// <c>ExecuteCommandEffectComplex</c> cases that DOES pop one script argument
    /// (effect-sync.md §12, 2026-09-18). The magnitude carries the appearance.2da row. Note
    /// the peer's stand-in already follows a disguise WITHOUT this kind: the engine's own
    /// <c>CSWSCreature::Disguise</c> writes the wearer's <c>+0xA60</c> appearance row, which
    /// is the field <c>PlayerState.Appearance</c> samples every tick. This kind covers the
    /// other direction — a disguise effect that lands on a stand-in is replayed on the real
    /// body — and its apply side is gated by coop.ini <c>disguise_sync</c> (default OFF,
    /// NEEDS-LIVE).</summary>
    Disguise = 16,
}
