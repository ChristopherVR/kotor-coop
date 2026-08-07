namespace KotorCoop.Plugin;

/// <summary>
/// What health to write onto a mirrored enemy, given the owner's latest value.
///
/// Three designs have been tried here and the history is the explanation:
///
/// 1. ABSOLUTE, BOTH WAYS. The owner's value was written whichever way it differed, eight
///    times a second. That quietly erased this player's whole contribution to every fight:
///    a hit landed here, our copy dropped, and ~125 ms later this put it straight back
///    before the once-a-second census could report it. Measured — 15 hp to 11 hp over
///    ninety seconds and then nothing at all.
/// 2. LOWER ONLY. Fixed the erasure by never raising, which worked, and cost the other
///    direction entirely: an enemy healed on the owner's side stayed hurt here forever.
/// 3. LOWER ABSOLUTELY, RAISE BY DELTA — this. A peer's damage no longer travels as health
///    at all (it is a separate cumulative ledger), so the erasure of design 1 cannot
///    happen; but writing the owner's value back absolutely would still make our copy's
///    health bar jump UP for the second or so it takes our damage to round-trip. So a rise
///    is applied as the owner's own DELTA — "the owner healed it by 5" heals our copy by 5
///    — which propagates healing without ever undoing a local hit that has not been
///    accounted for yet.
///
/// Both instances are applying the same set of deltas to the same creature, so the copies
/// converge: any drift is pulled out by the lower-absolutely leg on the next pass where the
/// owner is not healing.
/// </summary>
internal static class MirrorHealthRule
{
    /// <summary>
    /// The health to write, or <paramref name="current"/> to leave the creature alone.
    /// </summary>
    /// <param name="current">What our copy reads right now.</param>
    /// <param name="max">Our copy's maximum, as a ceiling on a heal. 0 = no ceiling known.</param>
    /// <param name="ownerHealth">The owner's latest value. Never below 1 — the owner says
    /// "dead" through its own flag, and a zero written here would be a body the engine
    /// treats as alive with no death path run for it.</param>
    /// <param name="lastOwnerHealth">The owner's previous value, for the delta.</param>
    /// <param name="primed">False before we have ever seen this enemy, when there is no
    /// delta to take and the owner's value is simply adopted.</param>
    public static int Target(int current, int max, int ownerHealth, int lastOwnerHealth, bool primed)
    {
        if (ownerHealth < 1)
        {
            ownerHealth = 1;
        }

        // First sighting: no history to diff against, so take the owner's word for it. This
        // is also what re-syncs a copy that has drifted for any reason we have not thought
        // of, because a module change re-primes every enemy.
        if (!primed)
        {
            return Clamp(ownerHealth, max);
        }

        int delta = ownerHealth - lastOwnerHealth;

        if (delta > 0)
        {
            // The owner healed it. Heal ours by the same amount rather than jumping to the
            // owner's number, which would also undo damage of ours the owner has not
            // applied yet.
            return Clamp(current + delta, max);
        }

        // The owner is steady or has taken it down. Never above the owner: its damage is
        // authoritative and ours is already accounted for through the damage ledger.
        return Clamp(Math.Min(current, ownerHealth), max);
    }

    private static int Clamp(int value, int max)
    {
        if (value < 1)
        {
            value = 1;
        }

        return max > 0 && value > max ? max : value;
    }
}
