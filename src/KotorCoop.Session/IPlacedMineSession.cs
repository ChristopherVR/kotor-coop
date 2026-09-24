// Party-placed floor-mine replication (docs/research/mines.md §14): the host owns the canonical
// set; a client reports placements and removals and reads the host's roster.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IPlacedMineSession
{
    /// <summary>True on the host: this session owns <see cref="PlacedMineAuthority"/>.</summary>
    bool IsPlacedMineAuthority => false;

    /// <summary>Host only: the host's current world (module hash + its plugin's world-load
    /// serial). A change drops the canonical set.</summary>
    void SetPlacedMineHostWorld(uint moduleHash, uint worldGeneration) { }

    /// <summary>A party member on THIS instance placed a mine. Host: straight into the authority.
    /// Client: a reliable intent to the host.</summary>
    void PublishPlacedMine(uint captureSerial, uint moduleHash, PlacedMineRecord record) { }

    /// <summary>The canonical mine <paramref name="mineSerial"/> is gone on THIS instance
    /// (detonated, disarmed or recovered by the engine here).</summary>
    void PublishPlacedMineGone(uint mineSerial) { }

    /// <summary>The canonical set as this instance currently knows it: the host's own snapshot,
    /// or the latest roster a client received. Null before any.</summary>
    PlacedMineRosterPacket? CurrentPlacedMineRoster => null;

    /// <summary>Human-readable notes from the session layer (accepted/refused placements, relayed
    /// removals) for the agent log, oldest first.</summary>
    IReadOnlyList<string> DrainPlacedMineNotes() => [];
}
