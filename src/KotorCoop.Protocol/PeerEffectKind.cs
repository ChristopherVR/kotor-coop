namespace KotorCoop.Protocol;

/// <summary>
/// The non-damage effects <c>PhantomEffectHook</c> forwards from a peer's phantom to that
/// peer's real character — the numeric identity that actually crosses the wire, deliberately
/// NOT the engine's own effect-type/sub-code pair.
///
/// See docs/research/effect-sync.md for the full derivation. The short version: every one of
/// these funnels through <c>CSWSEffectListHandler::OnEffectApplied</c>'s 110-entry dispatch
/// table, and five of the nine are all registered under the SAME table entry (effect type 8,
/// <c>OnApplySetState</c>), distinguished only by an integer sub-code the game itself reads out
/// of the effect. As of effect-sync.md §7 (2026-08-22) every member of this enum has a real,
/// VERIFIED-by-name identity and a real apply-side constructor in <c>OdysseyEffects</c> — the
/// three that did not (Knockdown, Paralyze, Sleep) were named and wired up by disassembling
/// <c>CSWVirtualMachineCommands::InitializeCommands</c>' routine-table population directly and
/// cross-checking against a K1 <c>nwscript.nss</c>'s declaration order. See the
/// VERIFIED/NEEDS-LIVE table in effect-sync.md §3.1/§7 before trusting any one of these names
/// over what the research doc actually says about it — and see <see cref="Frightened"/>'s own
/// note for one case where the ENGLISH NAME (not the numeric identity) turned out to be a
/// pre-existing guess that a genuinely different, still-unshipped engine effect actually owns.
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
    /// already-shipped, live-tested kind. The real <c>EffectFrightened</c> (sub-code 2) has no
    /// wire kind of its own yet.</summary>
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
}
