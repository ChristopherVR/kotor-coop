namespace KotorCoop.Memory;

/// <summary>
/// Bit assignment for the plugin's shared block "effective behaviour flags" region — one bit
/// per PLUGIN-gated coop.ini key a host-authoritative <c>BehaviourDigest</c> comparison can
/// override for a session (docs/research/mod-compatibility.md's "Behaviour-flag negotiation"
/// gap, closed 2026-08-27). Used by <see cref="KotorGameBridge.SetBehaviourOverrides"/> and by
/// <c>KotorCoop.Session.BehaviourOverrideEncoding</c> to pack a session's
/// <c>SessionOverrides</c> into the two dwords that call carries.
///
/// ⚠ KEEP IN SYNC with <c>SharedBlock.OverrideBit*</c> in the plugin. The two halves of this
/// mod cannot share a type — the plugin is NativeAOT win-x86 and the agent is x64 — so this
/// table is written twice on purpose, exactly like <c>CheatKinds</c> mirrors
/// <c>SharedBlock.CheatKind*</c>. <c>BehaviourOverrideBitsSyncTests.cs</c> cross-checks the
/// two the same way <c>CheatKindSyncTests.cs</c> already does.
/// </summary>
public static class BehaviourOverrideBits
{
    public const uint EffectSync = 1;
    public const uint TriggerRelay = 2;
    public const uint LivenessSync = 4;
    public const uint MapRevealSync = 8;
    public const uint DialogueRecruitSync = 16;
}
