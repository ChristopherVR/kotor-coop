namespace KotorCoop.Protocol;

/// <summary>
/// The non-damage effects <c>PhantomEffectHook</c> forwards from a peer's phantom to that
/// peer's real character — the numeric identity that actually crosses the wire, deliberately
/// NOT the engine's own effect-type/sub-code pair.
///
/// See docs/research/effect-sync.md for the full derivation. The short version: every one of
/// these funnels through <c>CSWSEffectListHandler::OnEffectApplied</c>'s 110-entry dispatch
/// table, and five of the eight are all registered under the SAME table entry (effect type 8,
/// <c>OnApplySetState</c>), distinguished only by an integer sub-code the game itself reads out
/// of the effect. Three of those five sub-codes could not be matched to an English name from
/// static analysis alone (no <c>nwscript.nss</c> in this repository to cross-reference) — they
/// are captured and carried on the wire like every other member here (the mechanism does not
/// care what a human calls the effect), but <c>OdysseyEffects</c> has no apply-side constructor
/// for them yet. See the VERIFIED/NEEDS-LIVE table in effect-sync.md §3.1 before trusting any
/// one of these names over what the research doc actually says about it.
/// </summary>
public enum PeerEffectKind : byte
{
    None = 0,

    /// <summary>Engine effect type 8, sub-code 3 — VERIFIED BY NAME
    /// (<c>ExecuteCommandEffectDroidStun</c>), but droid-scoped: whether a separate, general
    /// stun exists among the unnamed sub-codes below is open. Used as this pass's Stun.</summary>
    Stun = 1,

    /// <summary>Engine effect type 8, one of the unnamed <c>EffectComplex</c> sub-codes
    /// {1, 2, 4, 5, 6}. NOT IDENTIFIED — captured and forwarded, no apply-side constructor
    /// yet. See effect-sync.md §3.2/§3.3.</summary>
    Paralyze = 2,

    /// <summary>Engine effect type 20 (<c>OnApplyKnockdown</c>) — VERIFIED numerically, but
    /// only reachable through <c>EffectComplex</c>, whose full parameter list was not
    /// recovered. Captured and forwarded; no apply-side constructor yet (calling
    /// <c>EffectComplex</c> with a guessed argument count risks desyncing the VM stack, which
    /// nothing can catch after the fact). See effect-sync.md §4.</summary>
    Knockdown = 3,

    /// <summary>Engine effect type 29 (<c>OnApplyMovementSpeedDecrease</c>) — VERIFIED, single
    /// <c>int</c> parameter (percent). Fully implemented.</summary>
    Slow = 4,

    /// <summary>Engine effect type 8, sub-code 8 — VERIFIED BY NAME
    /// (<c>ExecuteCommandEffectHorrified</c>). Fully implemented.</summary>
    Frightened = 5,

    /// <summary>Engine effect type 8, one of the unnamed <c>EffectComplex</c> sub-codes.
    /// NOT IDENTIFIED — same status as <see cref="Paralyze"/>.</summary>
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
