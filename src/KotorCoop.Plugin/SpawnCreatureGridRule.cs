namespace KotorCoop.Plugin;

/// <summary>
/// Pure per-creature offset arithmetic for `spawncreature`'s multi-spawn grid: the same fixed
/// +X-then-wrap idea <see cref="MoveNearDestinationRule"/> already uses for a single target,
/// extended to N creatures so a large spawn (up to 32, docs/research/creature-liveness.md's
/// 2026-09-03 checklist item 6) does not stack every body on one exact point — the engine
/// tolerates that, but it makes a freshly spawned population impossible to eyeball or click
/// individually. Applied identically whether the anchor is the local player's own position or an
/// explicit `x y [z]` (see <c>CheatController.SpawnCreature</c>): both are just a starting point
/// this rule spreads out from.
///
/// Separated from <c>CheatController.SpawnCreature</c> for the same reason
/// <see cref="MoveNearDestinationRule"/> was separated from <c>CheatController.MoveNear</c>: pure
/// arithmetic that is unit-testable without a live engine, while the actual
/// <c>Odyssey.SpawnCreature</c> call (which has no test double) stays in the controller.
/// </summary>
internal static class SpawnCreatureGridRule
{
    /// <summary>Spacing between adjacent grid cells. Deliberately the SAME value as
    /// <see cref="MoveNearDestinationRule.DefaultOffsetX"/> — a reader who already knows one
    /// number knows both, and spawning exactly one creature (index 0) reproduces `movenear`'s
    /// own default point exactly.</summary>
    public const float Spacing = MoveNearDestinationRule.DefaultOffsetX;

    /// <summary>How many creatures sit in one row (along +X) before wrapping to the next row
    /// (along +Y). Keeps a full 32-creature spawn (<c>SpawnCreatureVerbRule.MaxCount</c> on the
    /// session side) inside a compact square patch rather than a single 64-unit line nobody
    /// could stand in the middle of.</summary>
    public const int RowWidth = 8;

    /// <summary>
    /// The <paramref name="index"/>'th creature's spawn point (0-based), relative to
    /// <paramref name="anchor"/>. Index 0 sits at <c>anchor + (Spacing, 0, 0)</c> — the exact
    /// point a single `movenear`/`spawncreature` (default, count 1) already uses.
    /// </summary>
    public static Vec3 OffsetFor(Vec3 anchor, int index)
    {
        int row = index / RowWidth;
        int col = index % RowWidth;

        return new Vec3
        {
            X = anchor.X + Spacing + (col * Spacing),
            Y = anchor.Y + (row * Spacing),
            Z = anchor.Z,
        };
    }
}
