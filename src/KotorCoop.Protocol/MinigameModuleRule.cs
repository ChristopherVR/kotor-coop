namespace KotorCoop.Protocol;

/// <summary>
/// "Never follow a peer onto a minigame track."
///
/// <c>TransitionPump.PumpTravel</c>'s whole job is to keep players together: when the peer
/// this instance follows changes module, this instance warps after it. A KOTOR minigame is the
/// one place that rule is actively harmful — the swoop track (<c>tar_m03mg</c>) and the turret
/// deck (<c>m12ab</c>) are SINGLE-SEAT areas whose <c>.are</c> carries a <c>MiniGame</c> struct
/// with the whole race's player/enemy/obstacle layout, and there is no second bike. Following
/// the racer in gets the follower a camera bolted to a track they cannot drive, and (measured
/// live, 2026-08-29, before this rule existed) the follow-warp into the race module was the
/// last thing the client instance did before it died.
///
/// Two independent gates, because neither alone is enough:
///   * the follow TARGET names a known minigame module — catches the warp before it happens,
///     even in the tick before the owner's own flag has been read and published;
///   * the peer being followed publishes <c>PlayerState.FlagInMinigame</c> — catches a track
///     module this catalogue has never heard of (a mod's own, or a KOTOR 2 port), at the cost
///     of one poll's latency.
///
/// The built-in catalogue is not guessed: it is every module in a stock GOG v1.0.3 install
/// whose area file actually contains a <c>MiniGame</c> struct, found by reading all of them
/// (docs/research/minigames.md §24). <c>coop.ini minigame_modules</c> adds more for a mod that
/// ships its own.
/// </summary>
public static class MinigameModuleRule
{
    /// <summary>
    /// Every stock module whose <c>.are</c> carries a <c>MiniGame</c> struct — verified by
    /// scanning all of <c>modules/*.rim</c> in a GOG v1.0.3 install rather than assumed:
    /// <c>tar_m03mg</c> (the Taris swoop track, <c>MiniGame/Type = 1</c>), <c>manm26mg</c>
    /// (Manaan swoop), <c>tat_m17mg</c> (Tatooine swoop) and <c>M12ab</c> (the Leviathan
    /// turret, <c>Type = 2</c>). Matched case-insensitively — the engine's own module names
    /// are inconsistently cased and this project has been bitten by that before.
    /// </summary>
    public static readonly string[] BuiltIn =
        ["tar_m03mg", "manm26mg", "tat_m17mg", "m12ab"];

    /// <summary>True when <paramref name="module"/> is a known single-seat minigame area.
    /// An empty or unreadable name is never a match — a follow decision made on a placeholder
    /// is the caller's own separate guard (<c>ModuleNameGuard</c>).</summary>
    public static bool IsMinigameModule(string module, IReadOnlyList<string>? extra = null)
    {
        if (module.Length == 0)
        {
            return false;
        }

        foreach (string known in BuiltIn)
        {
            if (string.Equals(module, known, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (extra is not null)
        {
            foreach (string known in extra)
            {
                if (known.Length > 0 && string.Equals(module, known, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Should this tick's follow be refused? True when the peer being followed is inside a
    /// minigame, OR when the module they went to is one. Gated by
    /// <paramref name="enabled"/> (coop.ini <c>minigame_lock_peers</c>) so one key turns the
    /// whole "one races, the rest wait" behaviour off — a session that would rather have the
    /// old, broken follow gets it by asking.
    /// </summary>
    public static bool ShouldRefuseFollow(
        bool enabled, bool ownerInMinigame, string targetModule, IReadOnlyList<string>? extra = null) =>
        enabled && (ownerInMinigame || IsMinigameModule(targetModule, extra));
}
