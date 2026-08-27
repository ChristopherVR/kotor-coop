// Solo-segment spectate (docs/research/star-forge-finale.md §3.3/§3.5): the plugin-side
// SoloSegmentController detects, in-process, whether the local player is the OWNER of an
// active story-forced solo segment (the Bastila/Malak duels) and publishes it as a plain
// level in the shared block — exactly the same shape KotorGameBridge.Player.cs's own
// IsLocalPlayerDowned already reads for FlagDowned.
namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    /// <summary>SharedBlock.OffsetLocalInSoloSegment (0x40C0) — kept as a private literal here
    /// rather than referencing the plugin's own SharedBlock constant, the same "two files, one
    /// number" convention every other block offset in this class already uses (the plugin is a
    /// separate NativeAOT assembly this project cannot reference).</summary>
    private const int BlockLocalInSoloSegment = 0x40C0;

    /// <summary>Plugin-published: this instance's own local player is the active OWNER of a
    /// story-forced solo segment (<c>SoloSegmentController</c>). See
    /// <c>KotorCoop.Protocol.PlayerState.FlagSoloSegment</c>.</summary>
    public bool IsLocalPlayerInSoloSegment =>
        _pluginBlock != 0
        && _memory.TryReadUInt32(_pluginBlock + BlockLocalInSoloSegment, out uint value)
        && value != 0;
}
