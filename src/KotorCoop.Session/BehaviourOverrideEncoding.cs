using KotorCoop.Memory;

namespace KotorCoop.Session;

/// <summary>
/// Packs a session's adopted <see cref="SessionOverrides"/> into the two dwords
/// <see cref="IPlayerBridge.SetBehaviourOverrides"/> carries into the plugin's shared block —
/// docs/research/mod-compatibility.md's "Behaviour-flag negotiation" gap, closed 2026-08-27.
///
/// Only the five keys the PLUGIN itself gates (effect_sync, trigger_relay, liveness_sync,
/// map_reveal_sync, dialogue_recruit_sync — see <see cref="BehaviourOverrideBits"/>) have a
/// bit here. The other four host-authoritative keys <see cref="SessionOverrides"/> also
/// carries (party_sync, script_item_sync, dialogue_item_sync, movie_relay) are already
/// consulted entirely inside <c>KotorCoop.Session</c>'s own pumps (<c>PartyPump</c>,
/// <c>ScriptItemGrantPump</c>, <c>DialoguePump</c>, <c>TransitionPump</c> — see
/// <c>ICapabilitySession.Overrides</c>'s own doc comment) and need no wire into the separate
/// plugin process at all.
/// </summary>
public static class BehaviourOverrideEncoding
{
    /// <summary>A field left <c>null</c> (no override on that key) leaves both the mask and
    /// value bit 0 for it, so the plugin falls through to its own coop.ini — exactly what
    /// <see cref="SessionOverrides.None"/> already means everywhere else this record is
    /// consulted.</summary>
    public static (uint Mask, uint Values) Encode(SessionOverrides overrides)
    {
        uint mask = 0;
        uint values = 0;

        Fold(overrides.EffectSync, BehaviourOverrideBits.EffectSync, ref mask, ref values);
        Fold(overrides.TriggerRelay, BehaviourOverrideBits.TriggerRelay, ref mask, ref values);
        Fold(overrides.LivenessSync, BehaviourOverrideBits.LivenessSync, ref mask, ref values);
        Fold(overrides.MapRevealSync, BehaviourOverrideBits.MapRevealSync, ref mask, ref values);
        Fold(overrides.DialogueRecruitSync, BehaviourOverrideBits.DialogueRecruitSync, ref mask, ref values);

        return (mask, values);
    }

    private static void Fold(bool? value, uint bit, ref uint mask, ref uint values)
    {
        if (value is not { } v)
        {
            return;
        }

        mask |= bit;
        if (v)
        {
            values |= bit;
        }
    }
}
