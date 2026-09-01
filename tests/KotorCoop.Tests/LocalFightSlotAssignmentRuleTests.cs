using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// N-slot local_combat (docs/research/combat-participation.md §11) — the pure top-K assignment
/// arithmetic behind <c>EnemyPresenceController.SelectImmediateCandidate</c>: given every
/// eligible candidate's priority/distance and which physical local-combat slots are free, which
/// candidate (if any) each free slot arms this frame.
/// </summary>
public class LocalFightSlotAssignmentRuleTests
{
    private const LocalFightPriority Proximity = LocalFightPriority.Proximity;
    private const LocalFightPriority TargetingUs = LocalFightPriority.TargetingUs;

    private static (int[] assigned, bool[] used) Run(
        LocalFightPriority[] priority, float[] distance, int candidateCount,
        bool[] slotOccupied, int slotCount)
    {
        int[] assigned = new int[slotCount];
        bool[] used = new bool[Math.Max(candidateCount, 1)];

        LocalFightSlotAssignmentRule.Assign(
            priority, distance, candidateCount, slotOccupied, slotCount, used, assigned);

        return (assigned, used);
    }

    [Fact]
    public void OneFreeSlotOneCandidate_AssignsIt()
    {
        LocalFightPriority[] priority = [Proximity];
        float[] distance = [5f];
        bool[] occupied = [false];

        (int[] assigned, _) = Run(priority, distance, 1, occupied, 1);

        Assert.Equal(0, assigned[0]);
    }

    [Fact]
    public void OneFreeSlot_ReproducesTheOldSingleWinnerRanking()
    {
        // Slots=1 must behave exactly like the pre-N-slot single-winner Beats() ranking: the
        // higher-priority, closer candidate wins regardless of scan order.
        LocalFightPriority[] priority = [Proximity, TargetingUs, Proximity];
        float[] distance = [1f, 10f, 0.5f];
        bool[] occupied = [false];

        (int[] assigned, _) = Run(priority, distance, 3, occupied, 1);

        // TargetingUs always outranks Proximity, however far, per LocalFightCandidateRule.Beats.
        Assert.Equal(1, assigned[0]);
    }

    [Fact]
    public void OneFreeSlot_TiedPriority_ClosestWins()
    {
        LocalFightPriority[] priority = [Proximity, Proximity, Proximity];
        float[] distance = [5f, 1f, 3f];
        bool[] occupied = [false];

        (int[] assigned, _) = Run(priority, distance, 3, occupied, 1);

        Assert.Equal(1, assigned[0]);
    }

    [Fact]
    public void NoCandidates_LeavesEveryFreeSlotUnassigned()
    {
        LocalFightPriority[] priority = [];
        float[] distance = [];
        bool[] occupied = [false, false, false];

        (int[] assigned, _) = Run(priority, distance, 0, occupied, 3);

        Assert.All(assigned, a => Assert.Equal(-1, a));
    }

    [Fact]
    public void OccupiedSlot_IsNeverAssignedRegardlessOfCandidates()
    {
        LocalFightPriority[] priority = [Proximity, Proximity];
        float[] distance = [1f, 2f];
        bool[] occupied = [true, false];

        (int[] assigned, _) = Run(priority, distance, 2, occupied, 2);

        Assert.Equal(-1, assigned[0]);
        Assert.NotEqual(-1, assigned[1]);
    }

    [Fact]
    public void ThreeFreeSlotsThreeCandidates_EachSlotGetsADistinctCandidate()
    {
        LocalFightPriority[] priority = [Proximity, TargetingUs, Proximity];
        float[] distance = [3f, 1f, 8f];
        bool[] occupied = [false, false, false];

        (int[] assigned, _) = Run(priority, distance, 3, occupied, 3);

        // Slot 0 gets the best overall (TargetingUs, idx 1); slot 1 the next best (Proximity,
        // dist 3, idx 0); slot 2 whatever remains (idx 2).
        Assert.Equal(1, assigned[0]);
        Assert.Equal(0, assigned[1]);
        Assert.Equal(2, assigned[2]);

        // The same candidate index must never appear twice.
        Assert.Equal(3, new HashSet<int> { assigned[0], assigned[1], assigned[2] }.Count);
    }

    [Fact]
    public void FewerCandidatesThanFreeSlots_TheSameIdCannotHoldTwoSlots()
    {
        // Two free slots, one candidate: only one slot may arm it — the other stays -1 rather
        // than the same candidate winning both.
        LocalFightPriority[] priority = [Proximity];
        float[] distance = [4f];
        bool[] occupied = [false, false];

        (int[] assigned, bool[] used) = Run(priority, distance, 1, occupied, 2);

        Assert.Equal(0, assigned[0]);
        Assert.Equal(-1, assigned[1]);
        Assert.True(used[0]);
    }

    [Fact]
    public void MixOfOccupiedAndFree_OnlyFreeSlotsAreRanked()
    {
        LocalFightPriority[] priority = [Proximity, TargetingUs];
        float[] distance = [1f, 1f];
        bool[] occupied = [true, false, true];

        (int[] assigned, _) = Run(priority, distance, 2, occupied, 3);

        Assert.Equal(-1, assigned[0]);
        Assert.Equal(1, assigned[1]); // TargetingUs beats Proximity at equal distance
        Assert.Equal(-1, assigned[2]);
    }
}
