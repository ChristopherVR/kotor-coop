using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// ROUND 101 (docs/research/party-reconciliation.md): a pure ordering test for
/// <see cref="PartyPresenceController.ReleaseSession"/>'s own step sequence, extracted into
/// <see cref="PartyPresenceReleaseOrderRule"/> specifically so this guarantee is asserted in code
/// rather than trusted from prose. <see cref="PartyPresenceReleaseStep.ReleaseFollowPhase"/> and
/// <see cref="PartyPresenceReleaseStep.ReleaseFaction"/> both read
/// <c>PartyPresenceController._trackedId</c>; <see cref="PartyPresenceReleaseStep.Invalidate"/> is
/// the only step that clears it. A future edit that reorders these three calls would silently
/// turn <see cref="Odyssey.TryReleaseFollowPhaseOnUnmirror"/> into a no-op for every companion —
/// the exact class of regression round 100's live A/B spent a whole rig round narrowing down to.
/// </summary>
public sealed class PartyPresenceReleaseOrderRuleTests
{
    [Fact]
    public void Steps_Phase7ReleaseOn_RunsFollowPhaseReleaseBeforeInvalidate()
    {
        IReadOnlyList<PartyPresenceReleaseStep> steps = PartyPresenceReleaseOrderRule.Steps(phase7ReleaseEnabled: true);

        int followPhaseIndex = IndexOf(steps, PartyPresenceReleaseStep.ReleaseFollowPhase);
        int invalidateIndex = IndexOf(steps, PartyPresenceReleaseStep.Invalidate);

        Assert.True(followPhaseIndex >= 0, "ReleaseFollowPhase must run when the flag is on.");
        Assert.True(invalidateIndex >= 0, "Invalidate must always run.");

        // THE contract: the step that reads _trackedId must run strictly before the step that
        // clears it — otherwise TryReleaseFollowPhaseOnUnmirror is called for every slot with
        // objectId 0 and silently does nothing for all of them.
        Assert.True(followPhaseIndex < invalidateIndex,
            "ReleaseFollowPhase must run while _trackedId is still populated (before Invalidate).");
    }

    [Fact]
    public void Steps_Phase7ReleaseOn_RunsFactionReleaseBeforeInvalidate()
    {
        IReadOnlyList<PartyPresenceReleaseStep> steps = PartyPresenceReleaseOrderRule.Steps(phase7ReleaseEnabled: true);

        int factionIndex = IndexOf(steps, PartyPresenceReleaseStep.ReleaseFaction);
        int invalidateIndex = IndexOf(steps, PartyPresenceReleaseStep.Invalidate);

        // ReleaseAllHeld() also reads _trackedId (and never clears it) — same guarantee, same
        // reasoning, see that method's own doc comment.
        Assert.True(factionIndex >= 0 && invalidateIndex >= 0 && factionIndex < invalidateIndex,
            "ReleaseFaction must run while _trackedId is still populated (before Invalidate).");
    }

    [Fact]
    public void Steps_Phase7ReleaseOff_OmitsFollowPhaseReleaseEntirely()
    {
        IReadOnlyList<PartyPresenceReleaseStep> steps = PartyPresenceReleaseOrderRule.Steps(phase7ReleaseEnabled: false);

        Assert.DoesNotContain(PartyPresenceReleaseStep.ReleaseFollowPhase, steps);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Steps_Invalidate_AlwaysRunsLast(bool phase7ReleaseEnabled)
    {
        IReadOnlyList<PartyPresenceReleaseStep> steps = PartyPresenceReleaseOrderRule.Steps(phase7ReleaseEnabled);

        Assert.NotEmpty(steps);
        Assert.Equal(PartyPresenceReleaseStep.Invalidate, steps[^1]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Steps_ReleaseFaction_AlwaysRunsFirst(bool phase7ReleaseEnabled)
    {
        IReadOnlyList<PartyPresenceReleaseStep> steps = PartyPresenceReleaseOrderRule.Steps(phase7ReleaseEnabled);

        Assert.Equal(PartyPresenceReleaseStep.ReleaseFaction, steps[0]);
    }

    private static int IndexOf(IReadOnlyList<PartyPresenceReleaseStep> steps, PartyPresenceReleaseStep step)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i] == step)
            {
                return i;
            }
        }

        return -1;
    }
}
