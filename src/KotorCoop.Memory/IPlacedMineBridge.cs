// Party-placed floor-mine replication (docs/research/mines.md §14): the agent's view of the
// plugin's capture and gone rings, and its writes of the roster level and the spawn mailbox.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>One placement the plugin captured (a party member on this instance placed a mine).</summary>
public readonly record struct PlacedMineCapture(uint CaptureSerial, uint World, PlacedMineRecord Record);

/// <summary>One "gone here" event: <paramref name="MineSerial"/> names a mapped mine; when it is 0,
/// <paramref name="CaptureSerial"/> names an own capture that vanished before its serial arrived.</summary>
public readonly record struct PlacedMineGone(uint Sequence, uint World, uint MineSerial, uint CaptureSerial);

/// <summary>The plugin's <c>PlacedMineSpawnStatus</c> values (KEEP IN SYNC with
/// src/KotorCoop.Plugin/SharedBlock.PlacedMines.cs; PlacedMineRelayTests reads both).</summary>
public static class PlacedMineSpawnStatusCodes
{
    public const uint None = 0;
    public const uint Spawned = 1;
    public const uint Registered = 2;
    public const uint StaleWorld = 3;
    public const uint Retry = 4;
    public const uint Refused = 5;
    public const uint OwnCaptureGone = 6;
    public const uint Disabled = 7;
}

public interface IPlacedMineBridge
{
    /// <summary>False when this bridge has no plugin block with the placed-mine region.</summary>
    bool SupportsPlacedMineSync => false;

    /// <summary>The plugin's placed-mine world token (bumped on every new world scope, including
    /// ordinary door transitions, unlike the world-load serial). 0 / false = no scope yet.</summary>
    bool TryReadPlacedMineWorld(out uint world)
    {
        world = 0;
        return false;
    }

    /// <summary>The plugin's two ring heads, for baselining a fresh agent against a block that
    /// outlived an earlier session in the same process.</summary>
    bool TryReadPlacedMineHeads(out uint captureHead, out uint goneHead)
    {
        captureHead = 0;
        goneHead = 0;
        return false;
    }

    /// <summary>Captures published after <paramref name="cursor"/>, oldest first; advances the
    /// cursor. A capture the ring already overwrote is skipped (and counted in
    /// <paramref name="lost"/>).</summary>
    IReadOnlyList<PlacedMineCapture> ReadPlacedMineCaptures(ref uint cursor, out int lost)
    {
        lost = 0;
        return [];
    }

    /// <summary>Gone events published after <paramref name="cursor"/>, oldest first.</summary>
    IReadOnlyList<PlacedMineGone> ReadPlacedMineGone(ref uint cursor, out int lost)
    {
        lost = 0;
        return [];
    }

    /// <summary>Writes the roster level under its seqlock (odd while writing, even when done).</summary>
    bool TryWritePlacedMineRoster(uint world, uint highestSerial, IReadOnlyList<uint> serials) => false;

    /// <summary>Files one spawn/register request; returns the request serial written (0 = failed).</summary>
    uint RequestPlacedMineSpawn(uint world, uint mineSerial, uint captureSerial, PlacedMineRecord record) => 0;

    /// <summary>The plugin's ack of the last request.</summary>
    bool TryReadPlacedMineSpawnAck(out uint applied, out uint status, out uint localId)
    {
        applied = 0;
        status = 0;
        localId = 0;
        return false;
    }
}
