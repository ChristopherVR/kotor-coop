namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    private const nint CancelPlayerCombatAddress = 0x004FDAA0;
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> CancelPlayerCombatFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)CancelPlayerCombatAddress;

    /// <summary>CancelCombat(0), as called at native 0x518996/0x518AB7. Unlike
    /// ClearAllQueuedCombatActions, this ends the active round and clears its target,
    /// combat state and hostile actor, then updates the client creature too. Main thread only.</summary>
    public static bool StopLocalPlayerCombat(uint playerId, out string diagnostic)
    {
        diagnostic = "current local player or combat round unavailable";
        nint player = GetPlayerCreaturePointer();
        if (player == 0 || !NativeMethods.IsReadable(player, (nuint)(CreCombatRound + sizeof(nint)))
            || !PlayerCombatStopRule.OwnCurrentBody(player, ResolveServerObject(playerId), GetPlayerCreaturePointer(),
                (uint)*(nint*)player, *(uint*)(player + 4), playerId)) return false;
        nint round = *(nint*)(player + CreCombatRound);
        if (round == 0 || !NativeMethods.IsReadable(round, 0x9D0)) return false;
        ReadOnlySpan<byte> expected = PlayerCombatStopRule.CancelCombatBytes;
        if (!NativeMethods.IsReadable(CancelPlayerCombatAddress, (nuint)expected.Length)
            || !PlayerCombatStopRule.CodeMatches(new ReadOnlySpan<byte>((void*)CancelPlayerCombatAddress, expected.Length)))
        {
            diagnostic = "CancelCombat bytes do not match the verified build";
            return false;
        }
        uint before = *(uint*)(round + 0x9CC);
        if (!ClearQueuedCombatActions(playerId)) return false;
        CancelPlayerCombatFn(player, 0);
        if (ResolveServerObject(playerId) != player || GetPlayerCreaturePointer() != player
            || !NativeMethods.IsReadable(player, (nuint)(CreCombatRound + sizeof(nint)))) return false;
        round = *(nint*)(player + CreCombatRound);
        if (round == 0 || !NativeMethods.IsReadable(round, 0x9D0)) return false;
        uint after = *(uint*)(round + 0x9CC);
        bool stopped = PlayerCombatStopRule.ActiveTargetCleared(after);
        diagnostic = $"active combat target 0x{before:X} -> 0x{after:X}";
        return stopped;
    }
}
