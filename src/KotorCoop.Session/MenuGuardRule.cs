namespace KotorCoop.Session;

/// <summary>
/// The timer behind the fair-pause parity fix for docs/limitations.md's "Pausing" row: in
/// single-player KOTOR, opening a full-screen menu (inventory, map, journal, character sheet,
/// equipment, options) freezes combat outright. In co-op only YOUR game freezes — the engine
/// has no idea a partner exists, so enemies on every other screen keep swinging at your
/// stand-in while you are stuck in a menu with no way to react. <see cref="PhantomTargetRule"/>
/// already has a lever for "hold this peer's stand-in out of local target selection" (built for
/// <c>stealth_sync</c>); this rule decides WHEN that lever is pulled for an open menu, and for
/// how long, so it closes the gap without becoming a way to grief-proof yourself by parking on
/// the inventory screen.
///
/// Pure, like <see cref="AwayRule"/>/<see cref="HostAwayRule"/>: state is held by the caller
/// (<c>CoopAgent</c>, one player's own <c>openSinceMs</c>/<c>cooldownUntilMs</c> pair) and
/// advanced through these free functions every pump, so the timing is tested without a game, a
/// session or a clock.
///
/// <b>The shape, in order:</b>
/// <list type="number">
/// <item>The instant a full-screen menu opens, protection starts — no delay, because the whole
/// point is that the player had no warning either.</item>
/// <item>Protection lasts at most <see cref="DefaultCapMs"/> (coop.ini
/// <c>menu_guard_cap_ms</c>) of the SAME opening, even if the menu is still open past it — the
/// "parked on the inventory screen forever" case this exists to prevent.</item>
/// <item>Protection ends the INSTANT the menu closes, capped or not — this is not a substitute
/// for the real pause, only a shield against unfair hits taken with no way to respond.</item>
/// <item>An opening that burns through the full cap starts a cooldown
/// (<see cref="DefaultCooldownMs"/>, coop.ini <c>menu_guard_cooldown_ms</c>): closing and
/// immediately reopening the menu does NOT grant a fresh cap while the cooldown is running,
/// which is what stops "tap the menu key every 12 seconds" from being a standing shield. A
/// SHORT check well under the cap — the ordinary way a player actually uses an inventory
/// screen mid-fight — never triggers a cooldown at all.</item>
/// </list>
/// </summary>
public static class MenuGuardRule
{
    /// <summary>coop.ini <c>menu_guard_cap_ms</c> default: 12 s. Inside the 10-15 s window the
    /// feature was scoped to — generous enough for a real equip/rearrange, short enough that a
    /// player who simply parks on the screen loses the shield well before anyone could call it
    /// invulnerability.</summary>
    public const int DefaultCapMs = 12_000;

    /// <summary>coop.ini <c>menu_guard_cooldown_ms</c> default: 20 s, comfortably longer than
    /// the cap itself so a capped-out opening cannot be renewed back-to-back by tapping the
    /// menu key.</summary>
    public const int DefaultCooldownMs = 20_000;

    /// <summary>
    /// Advances the "open since" stamp: 0 while the menu is closed, the time it was first seen
    /// open once it is (HELD, not bumped, on later ticks — the same "advances once, holds"
    /// shape <see cref="HostAwayRule.Track"/> uses), and back to 0 the instant it closes. A
    /// fresh 0 here is what gives a later reopening a full, fresh cap — see
    /// <see cref="IsProtected"/>.
    /// </summary>
    public static long TrackOpenSince(long openSinceMs, bool menuOpenNow, long nowMs) =>
        menuOpenNow ? (openSinceMs == 0 ? nowMs : openSinceMs) : 0;

    /// <summary>
    /// True once the CURRENT opening (<paramref name="openSinceMs"/> != 0) has run at least
    /// <paramref name="capMs"/> milliseconds — the edge that should start a cooldown. False the
    /// instant the menu closes (<paramref name="openSinceMs"/> resets to 0 by
    /// <see cref="TrackOpenSince"/>), so a cooldown is only ever earned by actually holding a
    /// menu open past the cap, never by the mere act of closing one that was.
    /// </summary>
    public static bool CapExhausted(long openSinceMs, int capMs, long nowMs) =>
        openSinceMs != 0 && capMs > 0 && nowMs - openSinceMs >= capMs;

    /// <summary>
    /// Advances the cooldown stamp: while <paramref name="capExhaustedNow"/> is true this is
    /// continuously refreshed to <c>nowMs + cooldownMs</c> (so the cooldown's clock effectively
    /// starts counting from whenever the over-cap opening is finally closed, not from the
    /// moment the cap was first reached), and HELD at its last value once the opening ends or
    /// never reached the cap at all. A short opening that never exhausts the cap never touches
    /// this — see the class doc's point 4.
    /// </summary>
    public static long TrackCooldown(long cooldownUntilMs, bool capExhaustedNow, int cooldownMs, long nowMs) =>
        capExhaustedNow ? nowMs + Math.Max(cooldownMs, 0) : cooldownUntilMs;

    /// <summary>
    /// Whether menu-guard protection is active RIGHT NOW: a menu is open
    /// (<paramref name="openSinceMs"/> != 0), this opening has not yet run past
    /// <paramref name="capMs"/>, and no earlier capped-out opening's cooldown is still
    /// counting down. The three failure paths are exactly the class doc's points 2-4.
    /// </summary>
    public static bool IsProtected(long openSinceMs, long cooldownUntilMs, int capMs, long nowMs) =>
        openSinceMs != 0
        && nowMs >= cooldownUntilMs
        && (capMs <= 0 || nowMs - openSinceMs < capMs);
}
