using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Round 55's immediate proximity trigger for <c>local_combat</c>
/// (docs/research/shared-state-audit.md §2, round 55) — the pure priority arithmetic that
/// decides which of this frame's eligible census creatures, if any, should be pulled out to
/// fight the local player right now, instead of waiting on census enumeration order or the
/// 25s timeout. The three hard gates (hostility, GIT identity, a real +0x260 script) must all
/// pass before distance or the phantom-attacker hint matter at all; among candidates that pass
/// all three, one currently hitting this player's phantom on the owner's side outranks one that
/// is merely near.
/// </summary>
public class LocalFightCandidateRuleTests
{
    private const int Hostile = 2;
    private const int Friendly = 1;
    private const int Neutral = 0;

    [Fact]
    public void HostileInRangeWithScriptAndGitIdentity_IsProximity()
    {
        LocalFightPriority priority = LocalFightCandidateRule.Evaluate(
            reputation: Hostile, gitIdentical: true, hasScript: true,
            distance: 5f, engageRange: 11f, targetingUs: false);

        Assert.Equal(LocalFightPriority.Proximity, priority);
    }

    [Fact]
    public void HostileInRangeAndTargetingUs_OutranksPlainProximity()
    {
        LocalFightPriority priority = LocalFightCandidateRule.Evaluate(
            reputation: Hostile, gitIdentical: true, hasScript: true,
            distance: 5f, engageRange: 11f, targetingUs: true);

        Assert.Equal(LocalFightPriority.TargetingUs, priority);
        Assert.True(priority > LocalFightPriority.Proximity);
    }

    [Theory]
    [InlineData(Friendly)]
    [InlineData(Neutral)]
    public void NonHostileReputation_IsNeverACandidate_EvenWhileTargetingUs(int reputation)
    {
        // Round 49's own gate: a friendly/neutral census actor (presence-sync only) must never
        // be pulled out for a fight it was never going to have, no matter how strong the other
        // signals read.
        LocalFightPriority priority = LocalFightCandidateRule.Evaluate(
            reputation, gitIdentical: true, hasScript: true,
            distance: 1f, engageRange: 11f, targetingUs: true);

        Assert.Equal(LocalFightPriority.None, priority);
    }

    [Fact]
    public void MaterialisedStandIn_IsNeverACandidate()
    {
        // A stand-in's local body wears a phantom blueprint, not the real enemy's own script
        // set — round 27/39's own restriction, unchanged.
        LocalFightPriority priority = LocalFightCandidateRule.Evaluate(
            reputation: Hostile, gitIdentical: false, hasScript: true,
            distance: 1f, engageRange: 11f, targetingUs: true);

        Assert.Equal(LocalFightPriority.None, priority);
    }

    [Fact]
    public void EmptyScriptSlot_IsNeverACandidate()
    {
        // Firing an empty +0x260 slot is a silent no-op that would otherwise wedge the
        // creature into a permanent re-exclude/timeout loop (round 39).
        LocalFightPriority priority = LocalFightCandidateRule.Evaluate(
            reputation: Hostile, gitIdentical: true, hasScript: false,
            distance: 1f, engageRange: 11f, targetingUs: true);

        Assert.Equal(LocalFightPriority.None, priority);
    }

    [Fact]
    public void OutsideEngageRange_IsNeverACandidate_EvenWhileTargetingUs()
    {
        // A creature far from THIS player has nothing local to swing back at even if the
        // owner's copy is hitting the phantom over there — pulling it out of the census would
        // strand a fight nobody local can see, exactly the failure this whole channel exists
        // to avoid for the mirrored-drive case.
        LocalFightPriority priority = LocalFightCandidateRule.Evaluate(
            reputation: Hostile, gitIdentical: true, hasScript: true,
            distance: 11.01f, engageRange: 11f, targetingUs: true);

        Assert.Equal(LocalFightPriority.None, priority);
    }

    [Fact]
    public void ExactlyAtEngageRange_IsStillEligible()
    {
        LocalFightPriority priority = LocalFightCandidateRule.Evaluate(
            reputation: Hostile, gitIdentical: true, hasScript: true,
            distance: 11f, engageRange: 11f, targetingUs: false);

        Assert.Equal(LocalFightPriority.Proximity, priority);
    }

    [Fact]
    public void Beats_HigherPriorityAlwaysWinsRegardlessOfDistance()
    {
        Assert.True(LocalFightCandidateRule.Beats(
            challenger: LocalFightPriority.TargetingUs, challengerDistance: 10f,
            incumbent: LocalFightPriority.Proximity, incumbentDistance: 0.5f));
    }

    [Fact]
    public void Beats_EqualPriorityWinsOnlyByBeingCloser()
    {
        Assert.True(LocalFightCandidateRule.Beats(
            challenger: LocalFightPriority.Proximity, challengerDistance: 3f,
            incumbent: LocalFightPriority.Proximity, incumbentDistance: 5f));

        Assert.False(LocalFightCandidateRule.Beats(
            challenger: LocalFightPriority.Proximity, challengerDistance: 5f,
            incumbent: LocalFightPriority.Proximity, incumbentDistance: 3f));
    }

    [Fact]
    public void Beats_EqualPriorityAndEqualDistance_IncumbentHoldsTheTie()
    {
        // The very first candidate found keeps the slot rather than flapping between two
        // equally-good picks frame to frame.
        Assert.False(LocalFightCandidateRule.Beats(
            challenger: LocalFightPriority.Proximity, challengerDistance: 4f,
            incumbent: LocalFightPriority.Proximity, incumbentDistance: 4f));
    }

    [Fact]
    public void Beats_NoneNeverBeatsARealCandidate()
    {
        // The caller never actually offers a None challenger a slot in practice (Evaluate's
        // None result is filtered out before Beats is ever consulted) — this only pins that a
        // non-candidate can never displace one that already cleared every gate.
        Assert.False(LocalFightCandidateRule.Beats(
            challenger: LocalFightPriority.None, challengerDistance: 0f,
            incumbent: LocalFightPriority.Proximity, incumbentDistance: float.MaxValue));
    }
}
