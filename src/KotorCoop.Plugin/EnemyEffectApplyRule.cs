namespace KotorCoop.Plugin;

/// <summary>What the host does with a client's reported enemy effect.</summary>
internal enum EnemyEffectApplyVerdict
{
    /// <summary>Run <see cref="OdysseyEffects.ApplyEffect"/> on the enemy's real body now.</summary>
    Apply,

    /// <summary>The kind is not one the enemy relay forwards (a buff, a disguise, poison, an
    /// unknown value).</summary>
    RefuseKind,

    /// <summary>The id resolves to nothing in this module.</summary>
    RefuseUnresolved,

    /// <summary>The id resolves to something that is not a server creature (a door or
    /// placeable whose id the client got wrong).</summary>
    RefuseNotCreature,

    /// <summary>The creature is already dead.</summary>
    RefuseDead,

    /// <summary>The id names one of THIS instance's own bodies — the player, a party member or a
    /// peer's phantom. A client may not stun the host's character by naming its id.</summary>
    RefuseOurBody,

    /// <summary>Over the per-enemy cooldown or cap (<see cref="EnemyEffectThrottleRule"/>).</summary>
    RefuseThrottled,
}

/// <summary>
/// The host-side decision for a client's reported enemy effect, pure so the safety rules are
/// testable without an engine (docs/research/enemy-effects.md). The order is cheapest and most
/// decisive first, and the throttle is LAST so a refused effect never spends a cooldown slot.
/// </summary>
internal static class EnemyEffectApplyRule
{
    public static EnemyEffectApplyVerdict Decide(
        uint kind, bool resolved, bool isServerCreature, bool alive, bool isOurBody, bool throttleAdmits)
    {
        if (!EnemyEffectMailboxRule.IsForwardable(kind))
        {
            return EnemyEffectApplyVerdict.RefuseKind;
        }

        if (!resolved)
        {
            return EnemyEffectApplyVerdict.RefuseUnresolved;
        }

        if (!isServerCreature)
        {
            return EnemyEffectApplyVerdict.RefuseNotCreature;
        }

        if (isOurBody)
        {
            return EnemyEffectApplyVerdict.RefuseOurBody;
        }

        if (!alive)
        {
            return EnemyEffectApplyVerdict.RefuseDead;
        }

        return throttleAdmits ? EnemyEffectApplyVerdict.Apply : EnemyEffectApplyVerdict.RefuseThrottled;
    }
}
