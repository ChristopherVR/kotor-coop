namespace KotorCoop.Plugin;

/// <summary>
/// Why a player who has taken enough damage to die is still standing.
///
/// The numbers are wire values: they are published into the shared block for the agent and
/// must not be renumbered. 0 means "nothing is stopping it" — a player who is still up with
/// this reason has simply not been hurt enough yet.
/// </summary>
internal enum DownBlockReason : uint
{
    /// <summary>No floor is armed; damage will down this player normally.</summary>
    None = 0,

    /// <summary>The creature's own min-1-hp flag (CSWSObject +0x200). In practice this means
    /// a MODULE pinned the player — the Endar Spire command deck and the Taris duel ring both
    /// do, from script, for as long as their scene lasts.</summary>
    MinOneHitPointFlag = 1,

    /// <summary>The plot flag (+0xF8): incoming damage is zeroed outright, so hit points do
    /// not even fall.</summary>
    PlotFlag = 2,

    /// <summary>The difficulty table's `MinHP1` rule, which floors any killing blow on a
    /// creature flagged is_pc. Unreachable in retail KOTOR 1 (see the research note) — if
    /// this ever shows up, the difficulty level is not what the engine hard-codes.</summary>
    DifficultyMinHitPoint1 = 3,
}

/// <summary>
/// Turns the three engine flags that can keep a player alive into one reason, and into a
/// sentence a log reader can act on.
///
/// This is deliberately free of engine calls so it can be unit-tested: getting the ORDER
/// wrong is the failure that costs an evening, because plot and min-1-hp produce visibly
/// different symptoms (plot: hit points never move at all; min-1-hp: hit points fall to
/// exactly 1 and stop) and reporting the wrong one sends the next session hunting the wrong
/// flag.
/// </summary>
internal static class DownBlock
{
    /// <summary>
    /// Classifies in the order the engine applies the floors, most upstream first: plot
    /// zeroes damage before anything else looks at it (OnApplyDamage 0x004DFAC8), the
    /// difficulty rule clamps the damage figure next (CSWSCreature::DoDamage 0x004F39B8),
    /// and min-1-hp clamps the hit-point write last (CSWSObject::DoDamage 0x004CCFAB).
    /// Whichever comes first is the one the player would have to remove to die.
    /// </summary>
    public static DownBlockReason Classify(bool plot, bool difficultyMinHitPoint1, bool minOneHitPoint)
    {
        if (plot)
        {
            return DownBlockReason.PlotFlag;
        }

        if (difficultyMinHitPoint1)
        {
            return DownBlockReason.DifficultyMinHitPoint1;
        }

        return minOneHitPoint ? DownBlockReason.MinOneHitPointFlag : DownBlockReason.None;
    }

    /// <summary>One line for the log, written for whoever reads it at 2am wondering why the
    /// other player will not die.</summary>
    public static string Describe(DownBlockReason reason) => reason switch
    {
        DownBlockReason.MinOneHitPointFlag =>
            "the player carries min-1-hp, so damage floors at 1 hit point and no death path runs. "
            + "KOTOR's own modules set this: the Endar Spire command deck pins the PC on area enter "
            + "and releases it in the starboard section, and the Taris duel ring does it per duel. "
            + "Nobody can be downed there, in co-op or in a normal playthrough",
        DownBlockReason.PlotFlag =>
            "the player is plot-flagged, so incoming damage is zeroed before it is applied",
        DownBlockReason.DifficultyMinHitPoint1 =>
            "the difficulty table's MinHP1 rule is armed, which floors any killing blow on the PC. "
            + "Retail KOTOR 1 hard-codes a difficulty level that leaves this off, so something has "
            + "written CServerInfo+0x108",
        _ => "nothing is stopping it",
    };
}
