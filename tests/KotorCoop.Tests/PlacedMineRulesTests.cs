using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pure rules behind party-placed floor-mine replication on the plugin side
/// (docs/research/mines.md §14.4, <c>PlacedMineRules.cs</c>): the single-authority damage rule
/// (<see cref="PlacedMineFireRule"/>), the roster-driven removal fence
/// (<see cref="PlacedMineRosterRule"/>), the local-vanish debounce
/// (<see cref="PlacedMineVanishTracker"/>) and the spec validity gate.
/// </summary>
public class PlacedMineRulesTests
{
    // ---- PlacedMineSpec.IsValid ------------------------------------------------------------

    private static readonly PlacedMineSpec ValidSpec = new(
        TrapType: 3, X: 1f, Y: 2f, Z: 3f, OrientX: 1f, OrientY: 0f, DetectDc: 15, DisarmDc: 20, Faction: 1);

    [Fact]
    public void SpecWithNoTrapTypeSentinelIsInvalid()
    {
        Assert.False((ValidSpec with { TrapType = PlacedMineSpec.NoTrapType }).IsValid);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(200_000f)]
    public void SpecWithAnUnusablePositionIsInvalid(float badCoordinate)
    {
        Assert.False((ValidSpec with { X = badCoordinate }).IsValid);
    }

    [Fact]
    public void OrdinarySpecIsValid()
    {
        Assert.True(ValidSpec.IsValid);
    }

    // ---- PlacedMineFireRule: the single-authority damage gate -------------------------------

    [Fact]
    public void OnlyAClientWithAProvenRoleAndALiveSessionSuppressesPartyMines()
    {
        Assert.False(PlacedMineFireRule.SuppressPartyMines(enabled: false, sessionActive: true, roleKnown: true, isHost: false));
        Assert.False(PlacedMineFireRule.SuppressPartyMines(enabled: true, sessionActive: false, roleKnown: true, isHost: false));
        Assert.False(PlacedMineFireRule.SuppressPartyMines(enabled: true, sessionActive: true, roleKnown: false, isHost: false));
        Assert.False(PlacedMineFireRule.SuppressPartyMines(enabled: true, sessionActive: true, roleKnown: true, isHost: true));
        Assert.True(PlacedMineFireRule.SuppressPartyMines(enabled: true, sessionActive: true, roleKnown: true, isHost: false));
    }

    [Fact]
    public void AnUnknownRoleFailsOpenSoTheHostsMinesAreNeverAccidentallySilenced()
    {
        // roleKnown=false must never suppress, even with every other input asking for it -
        // refusing on an actual host would make every one of its mines harmless.
        Assert.False(PlacedMineFireRule.SuppressPartyMines(enabled: true, sessionActive: true, roleKnown: false, isHost: false));
    }

    [Fact]
    public void RefuseFireOnlyAppliesToAPartyPlacedMineWhileSuppressed()
    {
        Assert.True(PlacedMineFireRule.RefuseFire(suppressPartyMines: true, placedByPartyField: 1));
        Assert.False(PlacedMineFireRule.RefuseFire(suppressPartyMines: true, placedByPartyField: 0)); // GIT mine
        Assert.False(PlacedMineFireRule.RefuseFire(suppressPartyMines: false, placedByPartyField: 1)); // host, or not a client
    }

    // ---- PlacedMineRosterRule: removal fence -------------------------------------------------

    [Fact]
    public void SerialNotOnTheRosterButBelowTheFenceIsRemoved()
    {
        bool shouldRemove = PlacedMineRosterRule.ShouldRemove(
            mappedSerial: 5, rosterWorldSerial: 1, currentWorldSerial: 1, rosterHighest: 10, rosterSerials: [1, 2, 3]);

        Assert.True(shouldRemove);
    }

    [Fact]
    public void SerialStillListedOnTheRosterIsNotRemoved()
    {
        bool shouldRemove = PlacedMineRosterRule.ShouldRemove(
            mappedSerial: 2, rosterWorldSerial: 1, currentWorldSerial: 1, rosterHighest: 10, rosterSerials: [1, 2, 3]);

        Assert.False(shouldRemove);
    }

    [Fact]
    public void SerialAboveTheFenceIsNeverRemoved()
    {
        // Announced after this roster snapshot was built - absence here says nothing yet.
        bool shouldRemove = PlacedMineRosterRule.ShouldRemove(
            mappedSerial: 11, rosterWorldSerial: 1, currentWorldSerial: 1, rosterHighest: 10, rosterSerials: []);

        Assert.False(shouldRemove);
    }

    [Fact]
    public void ARosterBuiltForADifferentWorldNeverRemovesAnything()
    {
        bool shouldRemove = PlacedMineRosterRule.ShouldRemove(
            mappedSerial: 5, rosterWorldSerial: 1, currentWorldSerial: 2, rosterHighest: 10, rosterSerials: []);

        Assert.False(shouldRemove);
    }

    [Fact]
    public void ZeroSerialIsNeverGroundsForRemoval()
    {
        bool shouldRemove = PlacedMineRosterRule.ShouldRemove(
            mappedSerial: 0, rosterWorldSerial: 1, currentWorldSerial: 1, rosterHighest: 10, rosterSerials: []);

        Assert.False(shouldRemove);
    }

    // ---- PlacedMineVanishTracker: local-vanish debounce -------------------------------------

    [Fact]
    public void APassWhileTheWorldIsUnstableReportsNothingAndResetsNoStreak()
    {
        PlacedMineVanishTracker tracker = new();

        List<ulong> reported = tracker.Pass([(1, false), (1, false)], worldStable: false);

        Assert.Empty(reported);
    }

    [Fact]
    public void AMineMustBeAbsentForTwoConsecutiveStablePassesBeforeItIsReported()
    {
        PlacedMineVanishTracker tracker = new();

        Assert.Empty(tracker.Pass([(1, false)], worldStable: true)); // streak 1
        List<ulong> reported = tracker.Pass([(1, false)], worldStable: true); // streak 2

        Assert.Equal([1ul], reported);
    }

    [Fact]
    public void ReappearingBetweenPassesResetsTheStreak()
    {
        PlacedMineVanishTracker tracker = new();

        tracker.Pass([(1, false)], worldStable: true); // streak 1
        tracker.Pass([(1, true)], worldStable: true); // back alive - reset
        List<ulong> reported = tracker.Pass([(1, false)], worldStable: true); // streak 1 again

        Assert.Empty(reported);
    }

    [Fact]
    public void ThreeOrMoreTrackedMinesAllVanishingTogetherReadsAsAnUnloadAndReportsNothing()
    {
        PlacedMineVanishTracker tracker = new();
        var allGone = new List<(ulong Key, bool Alive)> { (1, false), (2, false), (3, false) };

        // Even after the report threshold's worth of passes, a total teardown never reports.
        tracker.Pass(allGone, worldStable: true);
        List<ulong> reported = tracker.Pass(allGone, worldStable: true);

        Assert.Empty(reported);
    }

    [Fact]
    public void TwoMinesVanishingTogetherIsBelowTheMassVanishBarAndStillReports()
    {
        PlacedMineVanishTracker tracker = new();
        var twoGone = new List<(ulong Key, bool Alive)> { (1, false), (2, false) };

        tracker.Pass(twoGone, worldStable: true);
        List<ulong> reported = tracker.Pass(twoGone, worldStable: true);

        Assert.Equal([1ul, 2ul], reported.OrderBy(k => k));
    }

    [Fact]
    public void APartialVanishAmongMoreTrackedMinesIsNotReadAsAnUnload()
    {
        // 3 vanished but 4 total were tracked - not "every one of them", so it is not a teardown.
        PlacedMineVanishTracker tracker = new();
        var mixed = new List<(ulong Key, bool Alive)> { (1, false), (2, false), (3, false), (4, true) };

        tracker.Pass(mixed, worldStable: true);
        List<ulong> reported = tracker.Pass(mixed, worldStable: true);

        Assert.Equal([1ul, 2ul, 3ul], reported.OrderBy(k => k));
    }

    [Fact]
    public void ForgetDropsAPendingStreakSoARetiredKeyCannotLaterFireAStaleReport()
    {
        PlacedMineVanishTracker tracker = new();
        tracker.Pass([(1, false)], worldStable: true); // streak 1

        tracker.Forget(1);
        List<ulong> reported = tracker.Pass([(1, false)], worldStable: true); // would be streak 2 if not forgotten

        Assert.Empty(reported);
    }

    [Fact]
    public void ClearDropsEveryStreak()
    {
        PlacedMineVanishTracker tracker = new();
        tracker.Pass([(1, false)], worldStable: true);

        tracker.Clear();
        List<ulong> reported = tracker.Pass([(1, false)], worldStable: true);

        Assert.Empty(reported);
    }
}
