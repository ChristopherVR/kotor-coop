namespace KotorCoop.Plugin;

/// <summary>
/// The decision half of the effective-behaviour-flags channel (docs/research/mod-compatibility.md's
/// "Behaviour-flag negotiation" gap, closed 2026-08-27), separated from each of the five
/// consuming controllers so it can be unit-tested without a running game — the same split
/// <c>LivenessRule</c> and <c>MapRevealRule</c> already use for their own controllers.
///
/// <c>SharedBlock.OffsetBehaviourOverrideMask</c>/<c>Values</c> carries which of the five
/// plugin-gated coop.ini keys (effect_sync, trigger_relay, liveness_sync, map_reveal_sync,
/// dialogue_recruit_sync) KotorCoop.Session's Welcome comparison overrode for this session,
/// and what value each was overridden to — one bit per key (<c>SharedBlock.OverrideBit*</c>,
/// mirrored by hand as <c>KotorCoop.Memory.BehaviourOverrideBits</c> and cross-checked by
/// <c>BehaviourOverrideBitsSyncTests.cs</c> the same way <c>CheatKindSyncTests.cs</c> already
/// checks <c>SharedBlock.CheatKind*</c>).
/// </summary>
internal static class BehaviourOverrideRule
{
    /// <summary>
    /// The effective value for one key: the host's adopted value when <paramref name="mask"/>
    /// has this session's Welcome comparison overriding it, otherwise whatever
    /// <paramref name="localValue"/> (PluginConfig/coop.ini) already says. A cleared mask bit
    /// (no session, or no override on this key) always falls through to
    /// <paramref name="localValue"/> — the plugin needs no separate "am I connected" check
    /// here.
    /// </summary>
    public static bool Resolve(uint mask, uint values, uint bit, bool localValue) =>
        (mask & bit) != 0 ? (values & bit) != 0 : localValue;

    /// <summary>Whether this key is overridden at all — the difference between "the host's
    /// value happens to match coop.ini" and "coop.ini's value is being used because nothing
    /// overrode it", which only the log line cares about.</summary>
    public static bool IsOverridden(uint mask, uint bit) => (mask & bit) != 0;
}
