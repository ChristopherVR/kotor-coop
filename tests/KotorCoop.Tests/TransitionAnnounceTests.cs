using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// "Announced transitions" (docs/research/transition-smoothness.md): the destination of a
/// module transition, sent the instant the mover's own engine has committed to it — earlier
/// and cheaper than <see cref="TransitionMoviePacket"/>'s film, and, unlike it, present for
/// every real transition, movie or none.
///
/// These are the parts of the wire and the pure follow decision that can be asserted without a
/// game. The full <c>PumpTravel</c> integration (fast path vs. fallback, dedup, the on-screen
/// line) lives in <c>TravelPumpTests</c>, beside the pre-existing follow tests it extends.
/// </summary>
public class TransitionAnnounceTests
{
    // ---- the wire --------------------------------------------------------------------

    [Fact]
    public void TheAnnouncementRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        TransitionAnnouncePacket original = new(PeerId: 3, "end_m01ab", "from_01a", Serial: 9);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);

        TransitionAnnouncePacket? read = PacketCodec.ReadTransitionAnnounce(buffer[..length]);
        Assert.NotNull(read);
        Assert.Equal(original.PeerId, read!.PeerId);
        Assert.Equal(original.Module, read.Module);
        Assert.Equal(original.Waypoint, read.Waypoint);
        Assert.Equal(original.Serial, read.Serial);
    }

    /// <summary>A plugin-initiated warp with no waypoint is a real, common case (the
    /// co-op-warp path degrades to "use the default entry"), and the packet must not read as
    /// malformed just because that field is empty.</summary>
    [Fact]
    public void AnAnnouncementWithNoWaypointRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        int length = PacketCodec.Write(buffer, new TransitionAnnouncePacket(1, "tar_m03aa", string.Empty, 2));
        Assert.True(length > 0);

        TransitionAnnouncePacket? read = PacketCodec.ReadTransitionAnnounce(buffer[..length]);
        Assert.NotNull(read);
        Assert.Equal(string.Empty, read!.Waypoint);
    }

    /// <summary>A buffer belonging to a different packet type must not be misread as one of
    /// these — the same discipline every other codec pair in this file already has.</summary>
    [Fact]
    public void ReadingTheWrongPacketTypeReturnsNull()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new TransitionMoviePacket(1, "end_m01aa", 1, ["01c"]));

        Assert.Null(PacketCodec.ReadTransitionAnnounce(buffer[..length]));
    }

    // ---- the numbers ------------------------------------------------------------------

    [Fact]
    public void TheChannelIsWhereTheChangelogSaysItIs()
    {
        Assert.Equal(49, (byte)PacketType.TransitionAnnounce);

        // 47 and 48 were reserved for concurrent work while this channel was built and have
        // since landed as CreatureLiveness and TriggerRelay; the numbering stays contiguous.
        Assert.Equal(47, (byte)PacketType.CreatureLiveness);
        Assert.Equal(48, (byte)PacketType.TriggerRelay);
    }

    [Fact]
    public void TheProtocolVersionIsAtLeast33()
    {
        Assert.True(
            ProtocolConstants.Version >= 33,
            "ProtocolConstants.Version moved backwards past the transition-announce bump");
    }

    // ---- the pure follow decision ------------------------------------------------------

    [Fact]
    public void NoPendingAnnouncementFallsBack()
    {
        Assert.Equal(
            TransitionFollowPath.Fallback,
            TransitionAnnounceRule.Decide(hasPendingAnnounce: false, "tar_m03aa", "end_m01aa"));
    }

    [Fact]
    public void AnAnnouncementNamingSomewhereElseIsFollowedImmediately()
    {
        Assert.Equal(
            TransitionFollowPath.Announced,
            TransitionAnnounceRule.Decide(hasPendingAnnounce: true, "tar_m03aa", "end_m01aa"));
    }

    /// <summary>Already there: nothing to race ahead of, whatever the announce says.</summary>
    [Fact]
    public void AnAnnouncementNamingOurOwnModuleFallsBack()
    {
        Assert.Equal(
            TransitionFollowPath.Fallback,
            TransitionAnnounceRule.Decide(hasPendingAnnounce: true, "end_m01aa", "end_m01aa"));
    }

    [Fact]
    public void AnAnnouncementNamingOurOwnModuleIsCaseInsensitive()
    {
        Assert.Equal(
            TransitionFollowPath.Fallback,
            TransitionAnnounceRule.Decide(hasPendingAnnounce: true, "END_M01AA", "end_m01aa"));
    }

    /// <summary>An unreadable module decodes to empty on this side of the wire; it must not
    /// be treated as a real destination.</summary>
    [Fact]
    public void AnEmptyAnnouncedModuleFallsBack()
    {
        Assert.Equal(
            TransitionFollowPath.Fallback,
            TransitionAnnounceRule.Decide(hasPendingAnnounce: true, string.Empty, "end_m01aa"));
    }

    // ---- the pure client-door-refusal decision ------------------------------------------

    [Fact]
    public void AClientFollowingWithTheFlagOnArmsWhenTheHostIsQuiet()
    {
        Assert.True(ClientDoorRefusalRule.ShouldArm(
            isClient: true, followHostModule: true, refusalEnabled: true, hostRecentlyTransitioning: false));
    }

    [Fact]
    public void TheHostNeverArmsItsOwnRefusal()
    {
        Assert.False(ClientDoorRefusalRule.ShouldArm(
            isClient: false, followHostModule: true, refusalEnabled: true, hostRecentlyTransitioning: false));
    }

    [Fact]
    public void FollowDisabledNeverArms()
    {
        Assert.False(ClientDoorRefusalRule.ShouldArm(
            isClient: true, followHostModule: false, refusalEnabled: true, hostRecentlyTransitioning: false));
    }

    [Fact]
    public void TheIniFlagOffNeverArms()
    {
        Assert.False(ClientDoorRefusalRule.ShouldArm(
            isClient: true, followHostModule: true, refusalEnabled: false, hostRecentlyTransitioning: false));
    }

    /// <summary>The accidental-success case (docs/research/cinematics.md §2.4): a client
    /// crossing the SAME door the host just led through must not be cancelled.</summary>
    [Fact]
    public void ARecentlyTransitioningHostDisarmsTheRefusal()
    {
        Assert.False(ClientDoorRefusalRule.ShouldArm(
            isClient: true, followHostModule: true, refusalEnabled: true, hostRecentlyTransitioning: true));
    }

    // ---- lead-swap (2026-08-22): the refusal's REASON ------------------------------------

    /// <summary>follow_lead=host: today's exact wording and behaviour, unaffected by whether
    /// the host happens to be busy.</summary>
    [Fact]
    public void HostOnlyModeAlwaysRefusesWithHostMustLead()
    {
        Assert.Equal(
            ClientDoorRefusalReason.HostMustLead,
            ClientDoorRefusalRule.Decide(
                isClient: true, followHostModule: true, refusalEnabled: true,
                hostRecentlyTransitioning: false, mode: FollowLeadMode.Host, hostBusy: false));

        Assert.Equal(
            ClientDoorRefusalReason.HostMustLead,
            ClientDoorRefusalRule.Decide(
                isClient: true, followHostModule: true, refusalEnabled: true,
                hostRecentlyTransitioning: false, mode: FollowLeadMode.Host, hostBusy: true));
    }

    /// <summary>follow_lead=any with the host free: the client MAY lead - no refusal at all,
    /// which is what lets the crossing become an announce the host follows.</summary>
    [Fact]
    public void AnyModeWithAFreeHostDoesNotRefuse()
    {
        Assert.Equal(
            ClientDoorRefusalReason.None,
            ClientDoorRefusalRule.Decide(
                isClient: true, followHostModule: true, refusalEnabled: true,
                hostRecentlyTransitioning: false, mode: FollowLeadMode.Any, hostBusy: false));
    }

    /// <summary>follow_lead=any with the host busy (mid its own transition, in dialogue, or in
    /// real combat): refused, but for the DIFFERENT, transient reason.</summary>
    [Fact]
    public void AnyModeWithABusyHostRefusesWithHostBusy()
    {
        Assert.Equal(
            ClientDoorRefusalReason.HostBusy,
            ClientDoorRefusalRule.Decide(
                isClient: true, followHostModule: true, refusalEnabled: true,
                hostRecentlyTransitioning: false, mode: FollowLeadMode.Any, hostBusy: true));
    }

    /// <summary>The master switch still wins over everything, including a busy host under
    /// follow_lead=any.</summary>
    [Fact]
    public void TheIniFlagOffNeverArmsEvenWithABusyHostUnderAnyMode()
    {
        Assert.Equal(
            ClientDoorRefusalReason.None,
            ClientDoorRefusalRule.Decide(
                isClient: true, followHostModule: true, refusalEnabled: false,
                hostRecentlyTransitioning: false, mode: FollowLeadMode.Any, hostBusy: true));
    }

    /// <summary>Calling <c>ShouldArm</c> with no mode/hostBusy arguments (every pre-existing
    /// call site) must keep meaning exactly what it meant before this feature existed.</summary>
    [Fact]
    public void OmittingTheNewParametersDefaultsToTodaysBehaviour()
    {
        Assert.True(ClientDoorRefusalRule.ShouldArm(
            isClient: true, followHostModule: true, refusalEnabled: true, hostRecentlyTransitioning: false));
        Assert.Equal(
            ClientDoorRefusalReason.HostMustLead,
            ClientDoorRefusalRule.Decide(
                isClient: true, followHostModule: true, refusalEnabled: true, hostRecentlyTransitioning: false));
    }

    // ---- LeadFollowRule: the host-side half of lead-swap ---------------------------------

    /// <summary>A host announce is always eligible, under either mode — unaffected by
    /// lead-swap, exactly like every peer's pre-existing follow-the-host behaviour.</summary>
    [Fact]
    public void AHostAnnounceIsAlwaysEligible()
    {
        Assert.True(LeadFollowRule.IsEligibleLeader(weAreHost: false, announcerIsHost: true, FollowLeadMode.Any));
        Assert.True(LeadFollowRule.IsEligibleLeader(weAreHost: false, announcerIsHost: true, FollowLeadMode.Host));
        Assert.True(LeadFollowRule.IsEligibleLeader(weAreHost: true, announcerIsHost: true, FollowLeadMode.Any));
    }

    /// <summary>A client never follows another client, regardless of mode — there is exactly
    /// one shared world, and it is wherever the host is or is about to be.</summary>
    [Fact]
    public void AClientNeverFollowsAnotherClient()
    {
        Assert.False(LeadFollowRule.IsEligibleLeader(weAreHost: false, announcerIsHost: false, FollowLeadMode.Any));
    }

    [Fact]
    public void TheHostFollowsAClientOnlyUnderAnyMode()
    {
        Assert.True(LeadFollowRule.IsEligibleLeader(weAreHost: true, announcerIsHost: false, FollowLeadMode.Any));
        Assert.False(LeadFollowRule.IsEligibleLeader(weAreHost: true, announcerIsHost: false, FollowLeadMode.Host));
    }

    [Fact]
    public void TheHostMaySafelyFollowOnlyWhenAllThreeGatesAreClear()
    {
        Assert.True(LeadFollowRule.HostMaySafelyFollow(
            hostMidTransition: false, hostInDialogue: false, hostInCombat: false));

        Assert.False(LeadFollowRule.HostMaySafelyFollow(
            hostMidTransition: true, hostInDialogue: false, hostInCombat: false));
        Assert.False(LeadFollowRule.HostMaySafelyFollow(
            hostMidTransition: false, hostInDialogue: true, hostInCombat: false));
        Assert.False(LeadFollowRule.HostMaySafelyFollow(
            hostMidTransition: false, hostInDialogue: false, hostInCombat: true));
    }

    /// <summary>A client's lead held past the host's new-game movie must not replay afterwards.</summary>
    [Fact]
    public void AClientAnnounceExpiresAfterTheHostWasBusyTooLong()
    {
        Assert.False(LeadFollowRule.IsHostAnnounceExpired(0));
        Assert.False(LeadFollowRule.IsHostAnnounceExpired(LeadFollowRule.HostAnnounceExpiryMs));
        Assert.True(LeadFollowRule.IsHostAnnounceExpired(LeadFollowRule.HostAnnounceExpiryMs + 1));
        Assert.True(LeadFollowRule.IsHostAnnounceExpired(120_000));
    }

    /// <summary>"announce lost -&gt; fallback": no pending announce is Fallback regardless of
    /// mode or how safe the host is right now — there is nothing to follow.</summary>
    [Fact]
    public void DecideHostFollowFallsBackWithNoPendingAnnounce()
    {
        Assert.Equal(
            TransitionFollowPath.Fallback,
            LeadFollowRule.DecideHostFollow(
                hasPendingAnnounce: false, announceModule: "tar_m03aa", localModule: "end_m01aa",
                announcerIsHost: false, FollowLeadMode.Any,
                hostMidTransition: false, hostInDialogue: false, hostInCombat: false));
    }

    /// <summary>The everyday case: a client's announce, mode=any, host free — followed.</summary>
    [Fact]
    public void DecideHostFollowFollowsAnEligibleSafeClientAnnounce()
    {
        Assert.Equal(
            TransitionFollowPath.Announced,
            LeadFollowRule.DecideHostFollow(
                hasPendingAnnounce: true, announceModule: "tar_m03aa", localModule: "end_m01aa",
                announcerIsHost: false, FollowLeadMode.Any,
                hostMidTransition: false, hostInDialogue: false, hostInCombat: false));
    }

    /// <summary>follow_lead=host: a client's announce is never followed, however safe the
    /// host is — this is "the host never follows anyone", restored exactly.</summary>
    [Fact]
    public void DecideHostFollowIgnoresAClientAnnounceUnderHostOnlyMode()
    {
        Assert.Equal(
            TransitionFollowPath.Fallback,
            LeadFollowRule.DecideHostFollow(
                hasPendingAnnounce: true, announceModule: "tar_m03aa", localModule: "end_m01aa",
                announcerIsHost: false, FollowLeadMode.Host,
                hostMidTransition: false, hostInDialogue: false, hostInCombat: false));
    }

    /// <summary>"both announce simultaneously -&gt; host wins": the host's own transition
    /// having just begun (hostMidTransition) means a client's announce arriving in the same
    /// window is never acted on - the host's own crossing proceeds untouched.</summary>
    [Fact]
    public void BothAnnouncingSimultaneouslyTheHostsOwnTransitionWins()
    {
        Assert.Equal(
            TransitionFollowPath.Fallback,
            LeadFollowRule.DecideHostFollow(
                hasPendingAnnounce: true, announceModule: "tar_m03aa", localModule: "end_m01aa",
                announcerIsHost: false, FollowLeadMode.Any,
                hostMidTransition: true, hostInDialogue: false, hostInCombat: false));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void DecideHostFollowFallsBackWhenTheHostIsBusy(
        bool hostMidTransition, bool hostInDialogue, bool hostInCombat)
    {
        Assert.Equal(
            TransitionFollowPath.Fallback,
            LeadFollowRule.DecideHostFollow(
                hasPendingAnnounce: true, announceModule: "tar_m03aa", localModule: "end_m01aa",
                announcerIsHost: false, FollowLeadMode.Any,
                hostMidTransition, hostInDialogue, hostInCombat));
    }

    /// <summary>Already there: nothing to race ahead of, exactly like the client-side rule.</summary>
    [Fact]
    public void DecideHostFollowFallsBackWhenAlreadyThere()
    {
        Assert.Equal(
            TransitionFollowPath.Fallback,
            LeadFollowRule.DecideHostFollow(
                hasPendingAnnounce: true, announceModule: "end_m01aa", localModule: "end_m01aa",
                announcerIsHost: false, FollowLeadMode.Any,
                hostMidTransition: false, hostInDialogue: false, hostInCombat: false));
    }
}
