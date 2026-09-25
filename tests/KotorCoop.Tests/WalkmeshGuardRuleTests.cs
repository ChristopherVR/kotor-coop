using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure half of the walkmesh guard (docs/research/walkmesh-guard.md): the sanity checks a
/// destination must pass with no engine call — NaN/infinity/out-of-world magnitude and the
/// exact-origin poison sentinel. The engine call half (CSWSObject::GetArea then
/// CSWSArea::PositionWalkable, in <see cref="Odyssey.TryValidateMoveDestination"/>) has no
/// test double and is exercised live instead (see the doc's own recipe).
/// </summary>
public class WalkmeshGuardRuleTests
{
    [Theory]
    [InlineData(187.3f, 74.3f, 12.6f)] // the live host position that crashed the day this shipped
    [InlineData(0.001f, 0f, 0f)] // near-origin but not the exact sentinel
    [InlineData(-9999f, 9999f, -9999f)] // finite and bounded, however implausible a real point
    public void OrdinaryDestinations_ClassifyAsNone(float x, float y, float z)
    {
        Vec3 destination = new() { X = x, Y = y, Z = z };

        Assert.Equal(MoveRejectReason.None, WalkmeshGuardRule.ClassifyDestination(destination));
        Assert.True(WalkmeshGuardRule.IsRemotePositionDriveable(destination));
    }

    [Theory]
    [InlineData(float.NaN, 0f, 0f)]
    [InlineData(0f, float.PositiveInfinity, 0f)]
    [InlineData(0f, 0f, float.NegativeInfinity)]
    [InlineData(10_000f, 0f, 0f)] // at Vec3.MaxWorldCoordinate, not under it
    [InlineData(-4.15e34f, 0f, -1.06e37f)] // the live session-save.md read that hung a client
    public void NotFiniteOrOutOfWorld_ClassifiesAsNotPlausible(float x, float y, float z)
    {
        Vec3 destination = new() { X = x, Y = y, Z = z };

        Assert.Equal(MoveRejectReason.NotPlausible, WalkmeshGuardRule.ClassifyDestination(destination));
        Assert.False(WalkmeshGuardRule.IsRemotePositionDriveable(destination));
    }

    /// <summary>
    /// The exact scenario that crashed a JOINER on danm14aa (docs/research/walkmesh-guard.md
    /// §3): the host's own agent republished (0,0,0) during the host's module load, and
    /// PhantomController's per-frame drive loop had nothing screening it.
    /// </summary>
    [Fact]
    public void ExactOrigin_ClassifiesAsOrigin_NotNotPlausible()
    {
        Vec3 origin = new() { X = 0f, Y = 0f, Z = 0f };

        Assert.Equal(MoveRejectReason.Origin, WalkmeshGuardRule.ClassifyDestination(origin));
        Assert.False(WalkmeshGuardRule.IsRemotePositionDriveable(origin));
    }

    [Fact]
    public void OriginWithNonZeroZ_IsStillPlausibleFinite_ButRealDestinationsAreNeverExactlyThat()
    {
        // Vec3.IsOrigin only fires on the EXACT all-zero vector (see WorldPlausibilityTests /
        // Vec3's own doc comment) - a point at world X=0,Y=0 with a nonzero Z is technically
        // "plausible" by the finite/bounded test alone, but this rule's job is only to catch
        // the two named poison shapes, not to invent a third. PartyPresenceController's own
        // (X==0 && Y==0) screen is the stricter sibling used on a REMOTE party member's
        // position; this rule matches Teleport/WalkTo's own established origin check instead.
        Vec3 nearOrigin = new() { X = 0f, Y = 0f, Z = 5f };

        Assert.Equal(MoveRejectReason.None, WalkmeshGuardRule.ClassifyDestination(nearOrigin));
    }

    [Fact]
    public void PlausibilityIsCheckedBeforeOrigin()
    {
        // Degenerate (NaN happens to satisfy neither IsOrigin's equality nor a real check), but
        // pins the documented order: NotPlausible must win whenever both could apply.
        Vec3 destination = new() { X = float.NaN, Y = 0f, Z = 0f };

        Assert.Equal(MoveRejectReason.NotPlausible, WalkmeshGuardRule.ClassifyDestination(destination));
    }
}
