using KotorCoop.Plugin;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The announce-edge extension (docs/research/transition-smoothness.md, 2026-08-27): a
/// scripted <c>StartNewModule()</c> transition — a galaxy-map departure, a Leviathan/Star Forge
/// stage change, any other non-door transition — is announced through the SAME
/// <c>TransitionAnnounceRule</c>/<c>LeadFollowRule</c> machinery a door crossing already uses,
/// tagged with a <see cref="TransitionAnnounceKind"/> for the log only.
///
/// Two things this file asserts:
///   1. <see cref="TransitionAnnounceKindClassifier"/> itself — pure, no engine dependency.
///   2. That the kind is genuinely inert to the follow decision: <c>TransitionAnnounceRule</c>
///      and <c>LeadFollowRule</c> never see it (neither signature takes one), so a
///      Script/GalaxyMap-sourced announce is decided identically to a Door-sourced one for the
///      same (module, waypoint) pair — the gating this feature is required to leave unchanged.
/// </summary>
public class TransitionAnnounceKindTests
{
    // ---- TransitionAnnounceKindClassifier ------------------------------------------------

    [Theory]
    [InlineData("ebo_m12aa")]
    [InlineData("ebo_m40aa")]
    [InlineData("ebo_m41aa")]
    [InlineData("EBO_M12AA")]
    public void ALeavingEbonHawkInstanceClassifiesAsGalaxyMap(string sourceModule)
    {
        Assert.Equal(TransitionAnnounceKind.GalaxyMap, TransitionAnnounceKindClassifier.Classify(sourceModule));
    }

    [Theory]
    [InlineData("end_m01aa")]
    [InlineData("tar_m02aa")]
    [InlineData("STUNT_16")]
    [InlineData("m12ab")]
    public void ALeavingAnyOtherModuleClassifiesAsScript(string sourceModule)
    {
        Assert.Equal(TransitionAnnounceKind.Script, TransitionAnnounceKindClassifier.Classify(sourceModule));
    }

    /// <summary>An unreadable source module is the honest "we don't know" answer, never a
    /// guess dressed up as GalaxyMap.</summary>
    [Fact]
    public void AnEmptySourceModuleClassifiesAsScript()
    {
        Assert.Equal(TransitionAnnounceKind.Script, TransitionAnnounceKindClassifier.Classify(string.Empty));
    }

    /// <summary>A resref that merely CONTAINS "ebo_m" must not match — this is an exact
    /// resref comparison against the three known instances, not a substring/prefix guess.</summary>
    [Fact]
    public void APartialResrefMatchDoesNotClassifyAsGalaxyMap()
    {
        Assert.Equal(TransitionAnnounceKind.Script, TransitionAnnounceKindClassifier.Classify("ebo_m12aa_annex"));
    }

    // ---- the kind is inert to the follow decision -----------------------------------------

    /// <summary>A Script-kind announce (an Ebon Hawk exit-corridor dispatch to a real planet
    /// surface module, §1.3 leg 2) is followed exactly like a door announce would be for the
    /// same (module, waypoint) — TransitionAnnounceRule.Decide takes no kind parameter at all.</summary>
    [Fact]
    public void AScriptKindDestinationIsFollowedExactlyLikeADoorDestination()
    {
        // Decide has no kind parameter to branch on - a script-sourced destination and a
        // door-sourced one are indistinguishable to it by construction.
        Assert.Equal(
            TransitionFollowPath.Announced,
            TransitionAnnounceRule.Decide(hasPendingAnnounce: true, "tar_m02aa", "ebo_m12aa"));
    }

    /// <summary>A GalaxyMap-kind announce (leg 1: confirm on the map, back into a fresh Ebon
    /// Hawk instance) still falls back once it names the module we are already in — the same
    /// "already there" rule a door announce gets, unaffected by the kind.</summary>
    [Fact]
    public void AGalaxyMapKindDestinationFallsBackWhenAlreadyThere()
    {
        Assert.Equal(
            TransitionFollowPath.Fallback,
            TransitionAnnounceRule.Decide(hasPendingAnnounce: true, "ebo_m12aa", "ebo_m12aa"));
    }

    /// <summary>The host-side lead-swap gates (eligibility, then the three safety checks) are
    /// unchanged by which engine mechanism raised the announce - a Script/GalaxyMap-kind
    /// announce from a leading CLIENT is judged by the exact same LeadFollowRule.DecideHostFollow
    /// a door-led crossing already uses.</summary>
    [Fact]
    public void AScriptedAnnounceFromAnEligibleClientIsFollowedUnderTheSameGates()
    {
        Assert.Equal(
            TransitionFollowPath.Announced,
            LeadFollowRule.DecideHostFollow(
                hasPendingAnnounce: true, announceModule: "tar_m02aa", localModule: "ebo_m12aa",
                announcerIsHost: false, FollowLeadMode.Any,
                hostMidTransition: false, hostInDialogue: false, hostInCombat: false));
    }

    /// <summary>...and still refused/held exactly as before when the host is busy - the safety
    /// gates do not relax for a scripted transition.</summary>
    [Fact]
    public void AScriptedAnnounceFromAClientStillFallsBackWhenTheHostIsBusy()
    {
        Assert.Equal(
            TransitionFollowPath.Fallback,
            LeadFollowRule.DecideHostFollow(
                hasPendingAnnounce: true, announceModule: "tar_m02aa", localModule: "ebo_m12aa",
                announcerIsHost: false, FollowLeadMode.Any,
                hostMidTransition: false, hostInDialogue: false, hostInCombat: true));
    }
}
