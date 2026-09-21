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
    /// Checks the CSWParty entries that would remain exposed after moving one entry to
    /// member[0].  The native caller supplies <see cref="Slot.IsLiveCreature"/> from
    /// the client-side resolver used by the engine's party consumer; a server-side
    /// object match alone is not sufficient.  <paramref name="replacementClientId"/>
    /// is used for a verified entry that has not been written yet (the connect-time
    /// PC recovery path).
    /// </summary>
    internal static bool CanExposeAfterFrontRotation(
        ReadOnlySpan<Slot> slots,
        int targetIndex,
        int exposedCount,
        uint replacementClientId = 0)
    {
        if (slots.Length == 0
            || targetIndex is < 0
            || targetIndex >= slots.Length
            || exposedCount is <= 0
            || exposedCount > slots.Length)
        {
            return false;
        }

        for (int outputIndex = 0; outputIndex < exposedCount; outputIndex++)
        {
            int sourceIndex = outputIndex == 0
                ? targetIndex
                : outputIndex <= targetIndex
                    ? outputIndex - 1
                    : outputIndex;
            Slot source = slots[sourceIndex];
            if (sourceIndex == targetIndex && replacementClientId != 0)
            {
                source = new(replacementClientId, true);
            }

            uint maskedId = source.RawClientId & ~ClientNpcFlag;
            if (maskedId is 0 or ObjectInvalid || !source.IsLiveCreature)
            {
                return false;
            }
        }

        return true;
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
            // A reconnect can leave the same client id copied into an out-of-count
            // slot.  It is not a second creature: the engine's visible prefix already
            // owns that id, so reclaiming this duplicate cannot remove an exposed body.
            // Keep refusing a distinct live creature; that may still be a real member
            // waiting for the table/count view to settle.
            bool duplicateOfExposed = false;
            uint candidateId = slots[i].RawClientId & ~ClientNpcFlag;
            if (candidateId is not 0 and not ObjectInvalid)
            {
                for (int exposed = 0; exposed < boundedLiveCount; exposed++)
                {
                    uint exposedId = slots[exposed].RawClientId & ~ClientNpcFlag;
                    if (exposedId == candidateId && slots[exposed].IsLiveCreature)
                    {
                        duplicateOfExposed = true;
                        break;
                    }
                }
            }

            if (!slots[i].IsLiveCreature || duplicateOfExposed)
            {
                return new(-1, i, i + 1, clientId);
            }
        }

        return new(-1, -1, liveCount, clientId);
    }
}
