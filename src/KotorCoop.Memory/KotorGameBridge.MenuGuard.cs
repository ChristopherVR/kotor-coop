// Full-screen in-game GUI detection for the menu-guard fairness fix (docs/limitations.md's
// Pausing row / docs/research/effect-sync.md §13): opening a menu freezes combat in
// single-player KOTOR but only pauses the local game in co-op, so a player's stand-in kept
// eating hits on every other screen while they were stuck in a menu with no way to react.
namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    /// <summary>
    /// CGuiInGame + this = nonzero while the FULL-SCREEN in-game GUI is up — the equipment/
    /// inventory/character/abilities/journal/map/options screen the menu key raises, not "some
    /// panel exists". Matches <c>Odyssey.Store.cs</c>'s <c>InGameGuiBusyOffset</c> in the plugin
    /// (VERIFIED there by fresh disasm of <c>ShowStoreGui</c>/<c>ShowGalaxyMapGui</c>'s own
    /// precondition check, and by the two runtime writers found by hardware watchpoint —
    /// <c>CClientExoAppInternal::SetInputClass 0x006200E0</c>, docs/research/
    /// shared-inventory-v2.md §14 / ingame-gui-transition.md). Read here through the SAME
    /// pointer chain <see cref="InGameGuiOffset"/> (KotorGameBridge.Store.cs) already uses for
    /// the external agent — no new offset, only a new field read off an already-resolved
    /// pointer.
    /// </summary>
    private const int InGameGuiBusyOffsetLocal = 0x30;

    /// <summary>
    /// True while the LOCAL player's client has a full-screen menu screen open right now. Pure
    /// read — never written — reused by <c>CoopAgent.PumpOnce</c> to drive
    /// <see cref="KotorCoop.Session.MenuGuardRule"/> and publish
    /// <see cref="KotorCoop.Protocol.PlayerState.FlagMenuGuard"/>. False (not "unknown") when no
    /// client is resolvable, matching every other plain engine-state read in this class.
    /// </summary>
    public bool IsLocalFullScreenGuiOpen =>
        EngineGlobals.TryGetClientInternal(_memory, out nint clientInternal)
        && _memory.TryReadPointer(clientInternal + InGameGuiOffset, out nint gui) && gui != 0
        && _memory.TryReadInt32(gui + InGameGuiBusyOffsetLocal, out int busy) && busy != 0;
}
