namespace KotorCoop.Protocol;

/// <summary>What the arena boss can do to the party (docs/research/chungus-boss.md). The host
/// picks one and says so to everyone; each game then plays it out for its own player.</summary>
public enum BossPower : byte
{
    None = 0,

    /// <summary>The snap: everyone loses half the health they have left and is thrown down.</summary>
    Snap = 1,

    /// <summary>Power stone: one player takes a heavy hit and is thrown back.</summary>
    Blast = 2,

    /// <summary>Space stone: everyone is lifted off the floor and spun.</summary>
    Lift = 3,

    /// <summary>Reality stone: one player is turned into a gizka for a while.</summary>
    Gizka = 4,

    /// <summary>Mind stone: everyone is frightened for a few seconds.</summary>
    Fright = 5,

    /// <summary>Soul stone: one player is choked while the boss heals.</summary>
    Drain = 6,

    /// <summary>Time stone: everyone is slowed.</summary>
    Slow = 7,

    /// <summary>He arrives (sound only).</summary>
    Entrance = 8,

    /// <summary>He goes down (sound only).</summary>
    Defeat = 9,
}

/// <summary>
/// One use of a <see cref="BossPower"/>, packed into the <c>Other</c> word of a
/// <see cref="PvpNoticeCode.BossPower"/> notice: the power in the low byte, how hard it lands
/// (a percentage, meaning depends on the power) in the next, and the peer it is aimed at in the
/// top half (0 = everyone).
/// </summary>
public readonly record struct BossCue(BossPower Power, byte Strength, ushort TargetPeer)
{
    public uint Pack() => (uint)Power | ((uint)Strength << 8) | ((uint)TargetPeer << 16);

    public static BossCue Unpack(uint word) =>
        new((BossPower)(word & 0xFF), (byte)((word >> 8) & 0xFF), (ushort)(word >> 16));

    public bool IsKnown => Power is >= BossPower.Snap and <= BossPower.Defeat;

    /// <summary>Whether <paramref name="peer"/> is hit by this use.</summary>
    public bool Hits(uint peer) => TargetPeer == 0 || TargetPeer == peer;
}
