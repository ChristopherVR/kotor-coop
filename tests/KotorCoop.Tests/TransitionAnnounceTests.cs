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
}
