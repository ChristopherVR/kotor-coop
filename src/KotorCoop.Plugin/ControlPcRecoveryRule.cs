namespace KotorCoop.Plugin;

/// <summary>Pure planning for the narrow connect-time PC-member recovery.</summary>
internal static class ControlPcRecoveryRule
{
    internal const uint ClientNpcFlag = 0x80000000;
    internal const uint ObjectInvalid = 0x7F000000;

    internal readonly record struct Slot(uint RawClientId, bool IsLiveCreature);

    internal readonly record struct PlanResult(
        int ExistingIndex,
        int SlotToWrite,
        int NewCount,
        uint ClientId)
    {
        internal bool HasExisting => ExistingIndex >= 0;
        internal bool CanWrite => SlotToWrite >= 0;
    }

    /// <summary>
    /// Converts a verified server object id to the client-space id used by CSWParty.
    /// KOTOR's client representation is the server id with the party/client bit set;
    /// this deliberately derives the value from the live server id instead of assuming
    /// the usual PC sentinel <c>0x7FFFFFFF</c>.
    /// </summary>
    internal static uint ToClientId(uint serverId)
    {
        if (serverId is 0 or ObjectInvalid || (serverId & ClientNpcFlag) != 0)
        {
            return 0;
        }

        return serverId | ClientNpcFlag;
    }

    /// <summary>
    /// Finds an already-present PC id, or chooses the first safe stale slot beyond the
    /// current live count. <paramref name="maximumCount"/> is the independently-read
    /// CSWPartyTable companion count plus one; it prevents a stale third slot from being
    /// made live when the party can only contain the PC and one companion.
    /// </summary>
    internal static PlanResult Plan(
        ReadOnlySpan<Slot> slots,
        int liveCount,
        int maximumCount,
        uint serverPcId,
        bool serverPcVerified)
    {
        uint clientId = ToClientId(serverPcId);
        if (clientId == 0 || !serverPcVerified || slots.Length == 0)
        {
            return new(-1, -1, liveCount, 0);
        }

        if (liveCount is < 0 or > 3 || maximumCount is < 1 or > 3)
        {
            return new(-1, -1, liveCount, clientId);
        }

        int boundedLiveCount = liveCount;
        int boundedMaximum = maximumCount;
        boundedMaximum = Math.Min(boundedMaximum, slots.Length);

        for (int i = 0; i < slots.Length; i++)
        {
            if ((slots[i].RawClientId & ~ClientNpcFlag) == serverPcId)
            {
                return new(i, -1, liveCount, clientId);
            }
        }

        if (boundedLiveCount >= boundedMaximum)
        {
            return new(-1, -1, liveCount, clientId);
        }

        for (int i = boundedLiveCount; i < boundedMaximum; i++)
        {
            if (!slots[i].IsLiveCreature)
            {
                return new(-1, i, i + 1, clientId);
            }
        }

        return new(-1, -1, liveCount, clientId);
    }
}
