namespace KotorCoop.Plugin;

/// <summary>
/// The fields of one party-placed floor mine that a peer needs to rebuild it — the plugin-side
/// twin of <c>KotorCoop.Protocol.PlacedMineRecord</c> (the plugin cannot reference the agent's
/// assemblies). docs/research/mines.md §14.
/// </summary>
internal readonly record struct PlacedMineSpec(
    byte TrapType,
    float X,
    float Y,
    float Z,
    float OrientX,
    float OrientY,
    short DetectDc,
    short DisarmDc,
    ushort Faction)
{
    /// <summary>The constructor's "no trap type" value (<c>CSWSTrigger::Constructor</c>
    /// initialises +0x2D4 to 0xFF).</summary>
    public const byte NoTrapType = 0xFF;

    public bool IsValid =>
        TrapType != NoTrapType
        && float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z)
        && MathF.Abs(X) < 100_000f && MathF.Abs(Y) < 100_000f && MathF.Abs(Z) < 100_000f;
}

/// <summary>
/// The single-authority damage rule for party-placed mines (docs/research/mines.md §14.4).
///
/// Enemies are host-owned: the host's engine runs their AI and their deaths, and a client's copy
/// is a mirror whose health the census overwrites and whose damage the peer-damage ledger
/// forwards to the host. A mine that fired on a client would therefore either be lost (the
/// mirror's health is overwritten) or counted twice (the ledger forwards what the host's own
/// copy of the same mine also dealt). So exactly one engine may run a party mine's trap script
/// against a creature: the host's. On a client, every party-placed mine — a replica AND the
/// client placer's own mine — is inert: <c>CSWSTrigger::GetCanFireMineOnObject 0x0058D4A0</c>
/// answers 0 through <see cref="PlacedMineFireGateHook"/>, so <c>OnEnterMine</c> returns before
/// its trap script and before its own event-11 self-destruction. The client learns of the
/// detonation from the host's roster and removes its copy through the engine's DestroyObject.
///
/// GIT-placed mines (<c>+0x314 == 0</c>) are untouched on every instance: they are the
/// player-facing hazard each engine already resolves for its own walker (mines.md §6).
/// </summary>
internal static class PlacedMineFireRule
{
    /// <summary>
    /// Whether THIS instance must refuse to fire party-placed mines. True only with the feature
    /// on, a live session (the agent's spawn-and-drive command), and a proven client role —
    /// an unknown role fails OPEN (host behaviour: the retail rule), because refusing on the
    /// host would make its mines harmless everywhere.
    /// </summary>
    public static bool SuppressPartyMines(bool enabled, bool sessionActive, bool roleKnown, bool isHost) =>
        enabled && sessionActive && roleKnown && !isHost;

    /// <summary>What the hook stub decides for one fire check: refuse only a party-placed mine
    /// while <see cref="SuppressPartyMines"/> holds. Mirrors the stub's two compares.</summary>
    public static bool RefuseFire(bool suppressPartyMines, int placedByPartyField) =>
        suppressPartyMines && placedByPartyField != 0;
}

/// <summary>
/// Reconciles this instance's mapped serials against the agent's roster level: a mapped serial
/// the roster no longer lists (and that is at or below its fence) was removed by the authority
/// and must be destroyed here, once. Pure so the fence and world checks are unit-tested.
/// </summary>
internal static class PlacedMineRosterRule
{
    /// <summary>
    /// True when <paramref name="mappedSerial"/> must be removed here. A roster built for a
    /// different world load, or a serial above the fence (announced after this roster was
    /// built), is never grounds for removal.
    /// </summary>
    public static bool ShouldRemove(
        ulong mappedSerial,
        uint rosterWorldSerial,
        uint currentWorldSerial,
        uint rosterHighest,
        ReadOnlySpan<uint> rosterSerials)
    {
        if (mappedSerial == 0 || rosterWorldSerial != currentWorldSerial || mappedSerial > rosterHighest)
        {
            return false;
        }

        foreach (uint serial in rosterSerials)
        {
            if (serial == mappedSerial)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Decides when a tracked party-placed mine has really gone from THIS engine (detonated,
/// disarmed or recovered here) rather than flickered through a teardown. A mine must be absent
/// for <see cref="AbsentPassesToReport"/> consecutive scans taken while the world was stable
/// (same world epoch and area as when the mine was tracked, player resolvable); a pass taken
/// while it was not is ignored outright and resets nothing. A stable pass in which at least
/// <see cref="MassVanishCount"/> tracked mines vanished together, and they were all of them, is
/// read as a teardown and reports nothing — the same "an unload looks like everything
/// vanishing" guard the GIT-mine publisher uses (mines.md §6). Two mines can genuinely go in
/// one pass (two enemies, two mines), so the bar is three. The false-negative cost is a stale
/// replica on a peer; the false-positive cost would be deleting a live mine everywhere.
/// </summary>
internal sealed class PlacedMineVanishTracker
{
    public const int AbsentPassesToReport = 2;

    public const int MassVanishCount = 3;

    private readonly Dictionary<ulong, int> _absentStreak = [];

    public void Clear() => _absentStreak.Clear();

    public void Forget(ulong key) => _absentStreak.Remove(key);

    /// <summary>
    /// Feeds one scan. <paramref name="tracked"/> lists every key tracked this pass with whether
    /// its object still validated as a live party-placed mine. Returns the keys to report gone.
    /// </summary>
    public List<ulong> Pass(IReadOnlyList<(ulong Key, bool Alive)> tracked, bool worldStable)
    {
        List<ulong> report = [];
        if (!worldStable)
        {
            return report;
        }

        int vanishedThisPass = 0;
        foreach ((ulong key, bool alive) in tracked)
        {
            if (!alive)
            {
                vanishedThisPass++;
            }
        }

        bool looksLikeUnload = vanishedThisPass >= MassVanishCount && vanishedThisPass == tracked.Count;

        foreach ((ulong key, bool alive) in tracked)
        {
            if (alive || looksLikeUnload)
            {
                _absentStreak.Remove(key);
                continue;
            }

            int streak = _absentStreak.GetValueOrDefault(key) + 1;
            if (streak >= AbsentPassesToReport)
            {
                _absentStreak.Remove(key);
                report.Add(key);
            }
            else
            {
                _absentStreak[key] = streak;
            }
        }

        return report;
    }
}
