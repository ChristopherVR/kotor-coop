using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Host-side canonical set for party-placed floor mines (docs/research/mines.md §14.3,
/// <see cref="PlacedMineAuthority"/>). Covers the properties the design calls out: session
/// serials are assigned in strictly increasing order regardless of which peer reports, a
/// (owner, capture) pair is idempotent, a world change drops everything and bumps the
/// generation so late reports cannot resurrect a retired mine, and the retail per-area cap and
/// "host's own module only" rule are both enforced before a serial is minted.
/// </summary>
public class PlacedMineAuthorityTests
{
    private static readonly PlacedMineRecord Record = new(
        TrapType: 3, X: 1f, Y: 2f, Z: 3f, OrientX: 1f, OrientY: 0f, DetectDc: 15, DisarmDc: 20, Faction: 1);

    private static PlacedMineAuthority WithWorld(uint moduleHash = 0xAAAA, uint worldGeneration = 1)
    {
        PlacedMineAuthority authority = new();
        authority.SetWorld(moduleHash, worldGeneration);
        return authority;
    }

    [Fact]
    public void PlaceBeforeAnyWorldIsRefused()
    {
        PlacedMineAuthority authority = new();

        PlacedMinePlaceResult result = authority.Place(ownerPeerId: 1, captureSerial: 1, moduleHash: 0xAAAA, Record, out uint serial);

        Assert.Equal(PlacedMinePlaceResult.NoWorld, result);
        Assert.Equal(0u, serial);
    }

    [Fact]
    public void SerialsAreAssignedOneUpwardsAcrossDifferentOwners()
    {
        PlacedMineAuthority authority = WithWorld();

        Assert.Equal(PlacedMinePlaceResult.Accepted, authority.Place(1, 1, 0xAAAA, Record, out uint first));
        Assert.Equal(PlacedMinePlaceResult.Accepted, authority.Place(2, 1, 0xAAAA, Record, out uint second));
        Assert.Equal(PlacedMinePlaceResult.Accepted, authority.Place(1, 2, 0xAAAA, Record, out uint third));

        Assert.Equal(1u, first);
        Assert.Equal(2u, second);
        Assert.Equal(3u, third);
    }

    [Fact]
    public void SameOwnerAndCaptureIsDuplicateAndReturnsTheOriginalSerial()
    {
        PlacedMineAuthority authority = WithWorld();
        authority.Place(1, 7, 0xAAAA, Record, out uint original);

        PlacedMinePlaceResult result = authority.Place(1, 7, 0xAAAA, Record, out uint repeated);

        Assert.Equal(PlacedMinePlaceResult.Duplicate, result);
        Assert.Equal(original, repeated);
    }

    [Fact]
    public void SameCaptureNumberFromDifferentOwnersIsNotADuplicate()
    {
        // Capture serials are per-instance counters; owner+capture is the real key.
        PlacedMineAuthority authority = WithWorld();

        Assert.Equal(PlacedMinePlaceResult.Accepted, authority.Place(1, 1, 0xAAAA, Record, out uint ownerOneSerial));
        Assert.Equal(PlacedMinePlaceResult.Accepted, authority.Place(2, 1, 0xAAAA, Record, out uint ownerTwoSerial));

        Assert.NotEqual(ownerOneSerial, ownerTwoSerial);
    }

    [Fact]
    public void PlacementInADifferentModuleThanTheHostIsRefused()
    {
        PlacedMineAuthority authority = WithWorld(moduleHash: 0xAAAA);

        PlacedMinePlaceResult result = authority.Place(1, 1, moduleHash: 0xBBBB, Record, out uint serial);

        Assert.Equal(PlacedMinePlaceResult.WrongModule, result);
        Assert.Equal(0u, serial);
    }

    [Fact]
    public void InvalidRecordOrZeroCaptureSerialIsRefusedBeforeMinting()
    {
        PlacedMineAuthority authority = WithWorld();
        PlacedMineRecord invalid = Record with { TrapType = PlacedMineRecord.NoTrapType };

        Assert.Equal(PlacedMinePlaceResult.Invalid, authority.Place(1, 1, 0xAAAA, invalid, out uint a));
        Assert.Equal(PlacedMinePlaceResult.Invalid, authority.Place(1, captureSerial: 0, 0xAAAA, Record, out uint b));
        Assert.Equal(0u, a);
        Assert.Equal(0u, b);
    }

    [Fact]
    public void RetailPerAreaCapOfFifteenIsEnforced()
    {
        PlacedMineAuthority authority = WithWorld();
        for (uint i = 1; i <= PlacedMineRosterPacket.MaxEntries; i++)
        {
            Assert.Equal(PlacedMinePlaceResult.Accepted, authority.Place(1, i, 0xAAAA, Record, out _));
        }

        PlacedMinePlaceResult result = authority.Place(1, captureSerial: 100, 0xAAAA, Record, out uint serial);

        Assert.Equal(PlacedMinePlaceResult.Full, result);
        Assert.Equal(0u, serial);
    }

    [Fact]
    public void RemoveIsIdempotentAndOnlyBumpsGenerationOnce()
    {
        PlacedMineAuthority authority = WithWorld();
        authority.Place(1, 1, 0xAAAA, Record, out uint serial);
        uint generationAfterPlace = authority.Generation;

        Assert.True(authority.Remove(serial));
        uint generationAfterRemove = authority.Generation;
        Assert.True(generationAfterRemove > generationAfterPlace);

        Assert.False(authority.Remove(serial)); // already gone
        Assert.Equal(generationAfterRemove, authority.Generation); // no further bump
    }

    [Fact]
    public void RemovingAnUnknownOrRetiredSerialIsHarmless()
    {
        PlacedMineAuthority authority = WithWorld();

        Assert.False(authority.Remove(999));
    }

    [Fact]
    public void RemovedMineIsGoneFromTheSnapshotAndARetransmittedPlacementCannotResurrectIt()
    {
        // Remove() deliberately does NOT free the (owner, capture) dedup key: a client's
        // placement intent keeps retransmitting until acked, and if that retransmit arrives
        // after the mine has already been legitimately destroyed (a walk-on detonation, a
        // disarm), replaying it must report the same (now-removed) serial as a duplicate —
        // never mint a fresh one that resurrects a mine the engine has already torn down
        // everywhere.
        PlacedMineAuthority authority = WithWorld();
        authority.Place(1, 5, 0xAAAA, Record, out uint serial);
        authority.Remove(serial);

        PlacedMineRosterPacket snapshot = authority.Snapshot();
        Assert.DoesNotContain(snapshot.Entries, e => e.Serial == serial);

        PlacedMinePlaceResult result = authority.Place(1, 5, 0xAAAA, Record, out uint repeated);
        Assert.Equal(PlacedMinePlaceResult.Duplicate, result);
        Assert.Equal(serial, repeated);
        Assert.DoesNotContain(authority.Snapshot().Entries, e => e.Serial == serial); // still not resurrected
    }

    [Fact]
    public void WorldChangeDropsEveryRecordButSerialsStaySessionWideAndNeverReuse()
    {
        // mines.md §14.3: "session-wide and strictly increasing — never reused, not even across
        // module changes" - so a world change must NOT reset the counter back to 1, even though
        // it drops every active record.
        PlacedMineAuthority authority = WithWorld(moduleHash: 0xAAAA, worldGeneration: 1);
        authority.Place(1, 1, 0xAAAA, Record, out uint serial);

        bool dropped = authority.SetWorld(0xAAAA, worldGeneration: 2); // same module, host reloaded

        Assert.True(dropped);
        Assert.DoesNotContain(authority.Snapshot().Entries, e => e.Serial == serial);

        authority.Place(1, 1, 0xAAAA, Record, out uint newSerial);
        Assert.True(newSerial > serial, $"expected a serial above {serial}, got {newSerial}");
    }

    [Fact]
    public void SettingTheSameWorldAgainIsANoOpAndDoesNotDropAnything()
    {
        PlacedMineAuthority authority = WithWorld(moduleHash: 0xAAAA, worldGeneration: 1);
        authority.Place(1, 1, 0xAAAA, Record, out uint serial);
        uint generationBefore = authority.Generation;

        bool dropped = authority.SetWorld(0xAAAA, worldGeneration: 1);

        Assert.False(dropped);
        Assert.Equal(generationBefore, authority.Generation);
        Assert.Contains(authority.Snapshot().Entries, e => e.Serial == serial);
    }

    [Fact]
    public void SnapshotIsOrderedAscendingBySerialAndCarriesTheHighWaterMark()
    {
        PlacedMineAuthority authority = WithWorld();
        authority.Place(1, 1, 0xAAAA, Record, out uint a);
        authority.Place(2, 1, 0xAAAA, Record, out uint b);
        authority.Place(1, 2, 0xAAAA, Record, out uint c);
        authority.Remove(b); // b is gone but must still count toward the fence

        PlacedMineRosterPacket snapshot = authority.Snapshot();

        Assert.Equal([a, c], snapshot.Entries.Select(e => e.Serial));
        Assert.Equal(c, snapshot.HighestSerial);
    }

    [Fact]
    public void ClearForgetsEverythingAndRequiresANewWorldBeforePlacingAgain()
    {
        PlacedMineAuthority authority = WithWorld();
        authority.Place(1, 1, 0xAAAA, Record, out _);

        authority.Clear();

        Assert.Equal(PlacedMinePlaceResult.NoWorld, authority.Place(1, 2, 0xAAAA, Record, out uint serial));
        Assert.Equal(0u, serial);
    }
}
