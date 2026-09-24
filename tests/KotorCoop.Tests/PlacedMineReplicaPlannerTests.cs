using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The agent's fairness rule for driving the placed-mine spawn mailbox one request at a time
/// (docs/research/mines.md §14.3, <see cref="PlacedMineReplicaPlanner"/>). The plugin's identity
/// map accepts registrations only in strictly increasing serial order, so a busy module (many
/// party mines placed at once) must not let a slow or refused entry starve every later one, and
/// must not let a later entry jump ahead of an earlier one still waiting.
/// </summary>
public class PlacedMineReplicaPlannerTests
{
    private static readonly PlacedMineRecord Record = new(
        TrapType: 3, X: 1f, Y: 2f, Z: 3f, OrientX: 1f, OrientY: 0f, DetectDc: 15, DisarmDc: 20, Faction: 1);

    private static PlacedMineRosterEntry Entry(uint serial, uint owner = 9, uint capture = 0) =>
        new(serial, owner, capture, Record);

    private static readonly IReadOnlySet<uint> None = new HashSet<uint>();

    private static IReadOnlySet<uint> Only(params uint[] values) => values.ToHashSet();

    [Fact]
    public void RosterOnlyAppliesWhenItsModuleMatchesThisInstances()
    {
        PlacedMineRosterPacket roster = new(1, ModuleHash: 0xAAAA, HighestSerial: 0, []);

        Assert.True(PlacedMineReplicaPlanner.RosterApplies(roster, localModuleHash: 0xAAAA));
        Assert.False(PlacedMineReplicaPlanner.RosterApplies(roster, localModuleHash: 0xBBBB));
        Assert.False(PlacedMineReplicaPlanner.RosterApplies(roster, localModuleHash: 0));
        Assert.False(PlacedMineReplicaPlanner.RosterApplies(null, localModuleHash: 0xAAAA));
    }

    [Fact]
    public void TheLowestUnsettledSerialIsAlwaysServedFirst()
    {
        List<PlacedMineRosterEntry> entries = [Entry(1), Entry(2), Entry(3)];

        PlacedMineStep step = PlacedMineReplicaPlanner.Next(entries, None, localPeerId: 1, None, None);

        Assert.Equal(PlacedMineStepKind.Request, step.Kind);
        Assert.Equal(1u, step.Entry.Serial);
    }

    [Fact]
    public void ASettledEntryIsSkippedInFavourOfTheNextUnsettledOneRegardlessOfHowManyAreDone()
    {
        // A busy module: the first two are already spawned/refused/reported. The third, not the
        // first, must be the one driven next - a stalled or refused mine never starves the rest.
        List<PlacedMineRosterEntry> entries = [Entry(1), Entry(2), Entry(3), Entry(4)];
        HashSet<uint> settled = [1, 2];

        PlacedMineStep step = PlacedMineReplicaPlanner.Next(entries, settled, localPeerId: 1, None, None);

        Assert.Equal(3u, step.Entry.Serial);
    }

    [Fact]
    public void EveryEntrySettledLeavesNothingToDo()
    {
        List<PlacedMineRosterEntry> entries = [Entry(1), Entry(2)];
        HashSet<uint> settled = [1, 2];

        PlacedMineStep step = PlacedMineReplicaPlanner.Next(entries, settled, localPeerId: 1, None, None);

        Assert.Equal(PlacedMineStepKind.None, step.Kind);
    }

    [Fact]
    public void ANonAscendingRosterIsTreatedAsAProgrammingErrorAndStopsRatherThanMisorderTheMailbox()
    {
        // The very first unsettled entry is returned immediately, so the ordering check only
        // bites once the scan has to walk PAST already-settled entries to reach the violator.
        List<PlacedMineRosterEntry> outOfOrder = [Entry(1), Entry(3), Entry(2)];
        HashSet<uint> settled = [1, 3];

        PlacedMineStep step = PlacedMineReplicaPlanner.Next(outOfOrder, settled, localPeerId: 1, None, None);

        Assert.Equal(PlacedMineStepKind.None, step.Kind);
    }

    [Fact]
    public void OwnCaptureStillPendingIsRequestedWithItsCaptureNumberSoThePluginRegistersRatherThanRebuilds()
    {
        List<PlacedMineRosterEntry> entries = [Entry(serial: 5, owner: 1, capture: 42)];

        PlacedMineStep step = PlacedMineReplicaPlanner.Next(entries, None, localPeerId: 1, ownCaptures: Only(42), None);

        Assert.Equal(PlacedMineStepKind.Request, step.Kind);
        Assert.Equal(42u, step.CaptureToRegister);
    }

    [Fact]
    public void AnotherPeersEntryIsAlwaysBuiltAsAReplicaEvenIfItsCaptureNumberCoincidentallyMatchesOurOwn()
    {
        // OwnerPeerId must gate this, not merely "is this capture serial in my own set" - two
        // different placers number their own captures from 1 independently.
        List<PlacedMineRosterEntry> entries = [Entry(serial: 5, owner: 2, capture: 42)];

        PlacedMineStep step = PlacedMineReplicaPlanner.Next(entries, None, localPeerId: 1, ownCaptures: Only(42), None);

        Assert.Equal(PlacedMineStepKind.Request, step.Kind);
        Assert.Equal(0u, step.CaptureToRegister); // build a replica, do not "register" someone else's mine
    }

    [Fact]
    public void OwnCaptureThatVanishedBeforeItsSerialArrivedReportsGoneInsteadOfRequestingASpawn()
    {
        List<PlacedMineRosterEntry> entries = [Entry(serial: 5, owner: 1, capture: 42)];

        PlacedMineStep step = PlacedMineReplicaPlanner.Next(
            entries, None, localPeerId: 1, ownCaptures: Only(42), ownVanishedCaptures: Only(42));

        Assert.Equal(PlacedMineStepKind.ReportGone, step.Kind);
        Assert.Equal(5u, step.Entry.Serial);
    }

    [Fact]
    public void ZeroLocalPeerIdNeverMatchesAnOwnerSoNothingIsMistakenForOwnCapture()
    {
        // localPeerId==0 means "not connected yet / role unknown" - must not accidentally treat
        // a roster entry whose OwnerPeerId also happens to be 0 as our own.
        List<PlacedMineRosterEntry> entries = [Entry(serial: 5, owner: 0, capture: 42)];

        PlacedMineStep step = PlacedMineReplicaPlanner.Next(entries, None, localPeerId: 0, ownCaptures: Only(42), None);

        Assert.Equal(PlacedMineStepKind.Request, step.Kind);
        Assert.Equal(0u, step.CaptureToRegister);
    }

    [Theory]
    [InlineData(PlacedMineSpawnStatusCodes.Spawned, true)]
    [InlineData(PlacedMineSpawnStatusCodes.Registered, true)]
    [InlineData(PlacedMineSpawnStatusCodes.Refused, true)]
    [InlineData(PlacedMineSpawnStatusCodes.OwnCaptureGone, true)]
    [InlineData(PlacedMineSpawnStatusCodes.Disabled, true)]
    [InlineData(PlacedMineSpawnStatusCodes.Retry, false)]
    [InlineData(PlacedMineSpawnStatusCodes.StaleWorld, false)]
    public void IsFinalMatchesExactlyTheStatusesThatEndTheAttempt(uint status, bool expected)
    {
        Assert.Equal(expected, PlacedMineReplicaPlanner.IsFinal(status));
    }
}
