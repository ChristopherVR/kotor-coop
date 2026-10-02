// Minigame peer lock (docs/research/minigames.md §24): the agent decides, from the peer
// roster, whether a PEER is inside a minigame and this instance's player must therefore be
// held still — and publishes that decision into the shared block for the plugin's own
// MinigameLockController to act on, frame by frame.
//
// The opposite direction from KotorGameBridge.SoloSegment.cs's IsLocalPlayerInSoloSegment
// (plugin -> agent) and for a concrete reason: the decision needs the UNFILTERED peer roster,
// which only the agent has. minigame_hide_peer (default ON) drops a racing peer out of the
// block's remote slots in the very tick their flag turns true, so a plugin-side reader of
// those slots could never see the owner at all.
namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    /// <summary>SharedBlock.OffsetMinigameLockActive (0x4704) — kept as a private literal
    /// here rather than referencing the plugin's own constant, the same "two files, one
    /// number" convention every other block offset in this class uses (the plugin is a
    /// separate NativeAOT assembly this project cannot reference). Pinned against its twin by
    /// <c>SharedBlockNewRegionOffsetMirrorTests</c>.</summary>
    private const int BlockMinigameLockActive = 0x4704;

    /// <summary>SharedBlock.OffsetMinigameLockName (0x4708).</summary>
    private const int BlockMinigameLockName = 0x4708;

    /// <summary>SharedBlock.NameLength.</summary>
    private const int BlockMinigameLockNameLength = 24;

    /// <summary>Whether the last <see cref="TryWriteMinigameLock"/> asserted a lock, so a
    /// no-change tick costs nothing: the flag is a LEVEL the plugin re-reads every frame, and
    /// rewriting the same value several times a second would be pure syscall waste.</summary>
    private bool? _minigameLockWritten;

    private string _minigameLockName = string.Empty;

    /// <summary>
    /// Publishes this tick's minigame peer-lock decision. Idempotent: only writes when
    /// something actually changed. Returns false when there is no plugin block (no plugin, or
    /// not yet resolved), which is also the reason a lock can never be "stuck on" across a
    /// plugin reload — the block is zeroed by the plugin's own startup.
    /// </summary>
    public bool TryWriteMinigameLock(bool locked, string ownerName)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        ownerName ??= string.Empty;

        if (_minigameLockWritten is { } writtenBefore && locked == writtenBefore
            && (!locked || string.Equals(ownerName, _minigameLockName, StringComparison.Ordinal)))
        {
            return true;
        }

        bool ok = true;

        // Name first, flag second: the plugin reads the name only while the flag is set, so
        // writing them in this order means the notice can never draw a stale name for a lock
        // that has just begun.
        if (locked)
        {
            ok &= _memory.TryWriteAsciiString(
                _pluginBlock + BlockMinigameLockName, ownerName, BlockMinigameLockNameLength);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockMinigameLockActive, locked ? 1u : 0u);

        if (ok)
        {
            _minigameLockWritten = locked;
            _minigameLockName = ownerName;
        }

        return ok;
    }
}
