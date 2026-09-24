using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>What the agent should do next for one roster entry.</summary>
public enum PlacedMineStepKind
{
    None,

    /// <summary>File a spawn-mailbox request: build a replica, or (with a capture serial)
    /// register this instance's own already-built mine under the host's serial.</summary>
    Request,

    /// <summary>This instance's own mine vanished before the host named it; report the serial
    /// gone instead of asking the plugin for anything.</summary>
    ReportGone,
}

public readonly record struct PlacedMineStep(PlacedMineStepKind Kind, PlacedMineRosterEntry Entry, uint CaptureToRegister);

/// <summary>
/// Pure decisions of <see cref="Pumps.PlacedMinePump"/> (docs/research/mines.md §14.3): which
/// roster entry to hand the plugin next, and whether a roster applies to this instance at all.
/// Entries are served strictly in ascending serial order because the plugin's identity map
/// accepts registrations only in increasing order; an entry already settled (spawned,
/// registered, refused, reported gone) for the current world is skipped.
/// </summary>
public static class PlacedMineReplicaPlanner
{
    /// <summary>A roster describes this instance's world only when it is for the same module.</summary>
    public static bool RosterApplies(PlacedMineRosterPacket? roster, uint localModuleHash) =>
        roster is not null && localModuleHash != 0 && roster.ModuleHash == localModuleHash;

    public static PlacedMineStep Next(
        IReadOnlyList<PlacedMineRosterEntry> entries,
        IReadOnlySet<uint> settled,
        uint localPeerId,
        IReadOnlySet<uint> ownCaptures,
        IReadOnlySet<uint> ownVanishedCaptures)
    {
        uint previous = 0;
        foreach (PlacedMineRosterEntry entry in entries)
        {
            if (entry.Serial <= previous)
            {
                // Not ascending: the codec refuses such a roster, so this is a programming error;
                // stop rather than feed the plugin out of order.
                return default;
            }

            previous = entry.Serial;
            if (settled.Contains(entry.Serial))
            {
                continue;
            }

            bool own = localPeerId != 0 && entry.OwnerPeerId == localPeerId && entry.CaptureSerial != 0;
            if (own && ownVanishedCaptures.Contains(entry.CaptureSerial))
            {
                return new PlacedMineStep(PlacedMineStepKind.ReportGone, entry, 0);
            }

            return own && ownCaptures.Contains(entry.CaptureSerial)
                ? new PlacedMineStep(PlacedMineStepKind.Request, entry, entry.CaptureSerial)
                : new PlacedMineStep(PlacedMineStepKind.Request, entry, 0);
        }

        return default;
    }

    /// <summary>Whether a plugin status ends the attempt for this serial in this world.</summary>
    public static bool IsFinal(uint status) =>
        status is Memory.PlacedMineSpawnStatusCodes.Spawned
            or Memory.PlacedMineSpawnStatusCodes.Registered
            or Memory.PlacedMineSpawnStatusCodes.Refused
            or Memory.PlacedMineSpawnStatusCodes.OwnCaptureGone
            or Memory.PlacedMineSpawnStatusCodes.Disabled;
}
