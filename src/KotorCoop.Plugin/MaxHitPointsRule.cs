namespace KotorCoop.Plugin;

/// <summary>
/// The engine's own <c>CSWSCreature::GetMaxHitPoints</c> branch (0x004ED310), pulled out as
/// pure arithmetic so the DECISION can be tested without a live process — the memory reads
/// that feed it (<c>Odyssey.GetEffectiveMaxHitPoints</c>, Odyssey.Health.cs) cannot be.
///
/// <c>CSWSObject::GetMaxHitPoints</c> (0x004D01A0) is a one-line read of the stored field at
/// offset 0xE0, which is correct for doors, placeables, and ordinary creatures. But a
/// PC-flagged <c>CSWSCreature</c> never has that field written at all — the engine re-sums the
/// per-level records every time it is asked instead (the loop at 0x004ED360..0x004ED3CA) — so
/// reading the stored field on a player character always reads a slot the engine never writes.
/// This is what made <c>topup</c> refuse with "0 max hp" for every player, not just after
/// combat; combat only made it LOOK combat-shaped, because nobody had tried to heal before
/// taking damage. See docs/research/playthrough-taris.md's dated follow-up.
///
/// <c>KotorGameBridge.Attach.cs</c>'s <c>TryReadMaxHitPoints</c> reproduces this exact branch
/// out-of-process and was VERIFIED live against both instances of the rig: a level-1 PC with a
/// rolled 10 and a +2 CON modifier read back 12, matching a full-health reading exactly. The
/// offsets here are the same ones, reused verbatim for the same branch, in-process.
/// </summary>
public static class MaxHitPointsRule
{
    /// <summary>A defensive bound on the level list a torn or corrupt read cannot make this
    /// walk far past — matching <c>KotorGameBridge.Attach.cs</c>'s own <c>MaxCharacterLevels</c>,
    /// not a real gameplay limit (KOTOR 1 caps a character at level 20).</summary>
    public const int MaxCharacterLevels = 40;

    /// <summary>
    /// The non-PC path: whatever the stored field says. Unchanged from what
    /// <c>Odyssey.GetMaxHitPoints</c> already did — this exists only so a caller that does not
    /// yet know which branch applies can ask through one function.
    /// </summary>
    public static int ForNonPc(ushort storedMax) => storedMax;

    /// <summary>
    /// The PC path: sum <c>(level.hitPoints + conModifier)</c> over every resolved level.
    /// Returns 0 — every caller's own "unknown, cannot heal past this" signal, matching every
    /// other Odyssey read's contract — when the level list could not be read at all (an empty
    /// list, or a count outside <see cref="MaxCharacterLevels"/>'s bound). This IS the fallback
    /// the stored field's zero should have been all along: a genuinely unresolved stats block
    /// still reports 0 rather than a guess, but a RESOLVED one reports the character's real
    /// base+level hit points instead of the never-written stored field.
    /// </summary>
    public static int ForPc(int conModifier, ReadOnlySpan<int> levelHitPoints)
    {
        if (levelHitPoints.Length == 0 || levelHitPoints.Length > MaxCharacterLevels)
        {
            return 0;
        }

        int total = 0;
        foreach (int rolled in levelHitPoints)
        {
            total += rolled + conModifier;
        }

        return total;
    }
}
