namespace KotorCoop.Plugin;

/// <summary>
/// When a joiner's "Item(s) Received" box goes away by itself. The box is the engine's Status
/// Summary (CGuiInGame::ShowStatusSummary, raised from a short main-loop timer at 0x00603C6F). Items
/// a script hands the player outside a conversation arm it, and it pauses the world until OK is
/// clicked: a `giveloot` on the joiner raised it at once (2026-10-11). On a joiner the shared
/// inventory hands items back the same way while it settles after a world change, so after a
/// host's reload, a rejoin or an arena return the joiner stood frozen behind a list of items it
/// already had (Leviathan and Manaan 2026-10-01, the arena 2026-10-10). The host's box stays. Only
/// a box that comes up within <see cref="AfterLoadWindowMs"/> of the world changing is dismissed,
/// and only once it has been on screen for a moment; a box later in the session waits for the
/// player. Pure, so the tests compile it in.
/// </summary>
internal static class LoadSummaryDismissRule
{
    /// <summary>How long after a world change a box may come up and still count as the load's. The
    /// epoch moves when a load starts and the box comes when it ends: 23 s for a joiner rejoining
    /// a reloaded host on the K1 Full build (2026-10-11).</summary>
    internal const long AfterLoadWindowMs = 60_000;

    /// <summary>How long the box stays up before it is dismissed, so it is not caught half built.</summary>
    internal const long ShownForMs = 750;

    /// <param name="msWorldChangeToShown">From the world change to the moment the box came up.</param>
    internal static bool ShouldDismiss(bool isJoiner, bool summaryOnTop, long msWorldChangeToShown, long msShown)
    {
        return isJoiner
            && summaryOnTop
            && msWorldChangeToShown is >= 0 and <= AfterLoadWindowMs
            && msShown >= ShownForMs;
    }
}
