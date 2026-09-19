using KotorCoop.Memory;
using KotorCoop.Plugin;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Floor-mine sync (docs/research/mines.md): the 0x54 key layout, the absence detector's
/// under-reporting guards, the plugin's removal refusals, and the three-reader ON default.
/// </summary>
public class MineSyncTests
{
    // ---- key layout --------------------------------------------------------------------------

    [Fact]
    public void MineKeyRoundTripsAndCarriesModuleAndId()
    {
        uint hash = MineFlags.HashModule("tar_m02aa");
        ulong key = MineFlags.MakeKey(hash, 0x1234);

        Assert.True(MineFlags.IsMineKey(key));
        Assert.Equal(0x54, ObjectKeyedFlags.TagOf(key));
        Assert.Equal(hash, MineFlags.ModuleHashOf(key));
        Assert.Equal(0x1234u, MineFlags.ObjectIdOf(key));
        Assert.True(ObjectKeyedFlags.BelongsToModule(key, hash));
    }

    [Fact]
    public void MineKeysNeverCollideWithDoorPlaceableOrGlobalKeys()
    {
        uint hash = MineFlags.HashModule("tar_m02aa");
        ulong mine = MineFlags.MakeKey(hash, 0x40);
        ulong door = DoorFlags.MakeKey(hash, 0x40);
        ulong placeable = PlaceableFlags.MakeKey(hash, 0x40);

        Assert.NotEqual(mine, door);
        Assert.NotEqual(mine, placeable);
        Assert.False(DoorFlags.IsDoorKey(mine));
        Assert.False(PlaceableFlags.IsPlaceableKey(mine));
        Assert.False(MineFlags.IsMineKey(door));
        Assert.False(MineFlags.IsMineKey(placeable));
        Assert.False(MineFlags.IsMineKey(GlobalVariableTable.MakeFlagId(GlobalVariableType.Boolean, 0x40)));
        Assert.True(ObjectKeyedFlags.IsObjectKeyed(mine));
    }

    [Fact]
    public void MineValueIsAMonotonicRemovedBit()
    {
        Assert.False(MineFlags.RemovedOf(MineFlags.MakeValue(removed: false)));
        Assert.True(MineFlags.RemovedOf(MineFlags.MakeValue(removed: true)));
        Assert.True(MineFlags.MakeValue(removed: true) > 0);
    }

    [Fact]
    public void TheSameObjectIdInTwoModulesGivesTwoMineKeys()
    {
        Assert.NotEqual(
            MineFlags.MakeKey(MineFlags.HashModule("tar_m02aa"), 0x77),
            MineFlags.MakeKey(MineFlags.HashModule("tar_m02ab"), 0x77));
    }

    // ---- MineVanishTracker -------------------------------------------------------------------

    private static HashSet<uint> Ids(params uint[] ids) => [.. ids];

    /// <summary>Runs the baseline window with the given mines alive.</summary>
    private static MineVanishTracker Baselined(params uint[] alive)
    {
        MineVanishTracker tracker = new();
        for (int i = 0; i < MineVanishTracker.BaselinePasses; i++)
        {
            Assert.Empty(tracker.Observe(true, Ids(alive)));
        }

        Assert.True(tracker.BaselineFrozen);
        return tracker;
    }

    [Fact]
    public void AMineIsReportedOnlyAfterAbsentPassesInARow()
    {
        MineVanishTracker tracker = Baselined(0x10, 0x11, 0x12, 0x13);

        for (int i = 1; i < MineVanishTracker.AbsentPassesToReport; i++)
        {
            Assert.Empty(tracker.Observe(true, Ids(0x10, 0x12, 0x13)));
        }

        IReadOnlyList<uint> gone = tracker.Observe(true, Ids(0x10, 0x12, 0x13));
        Assert.Equal([0x11u], gone);
    }

    [Fact]
    public void AGoneMineIsReportedExactlyOnce()
    {
        MineVanishTracker tracker = Baselined(0x10, 0x11, 0x12, 0x13);
        int reports = 0;
        for (int i = 0; i < 20; i++)
        {
            reports += tracker.Observe(true, Ids(0x10, 0x12, 0x13)).Count;
        }

        Assert.Equal(1, reports);
    }

    [Fact]
    public void AReappearingMineZeroesItsAbsentStreak()
    {
        MineVanishTracker tracker = Baselined(0x10, 0x11, 0x12, 0x13);

        for (int i = 1; i < MineVanishTracker.AbsentPassesToReport; i++)
        {
            Assert.Empty(tracker.Observe(true, Ids(0x10, 0x12, 0x13)));
        }

        // A stale-scan blip: it is seen again, so the streak restarts from zero.
        Assert.Empty(tracker.Observe(true, Ids(0x10, 0x11, 0x12, 0x13)));
        for (int i = 1; i < MineVanishTracker.AbsentPassesToReport; i++)
        {
            Assert.Empty(tracker.Observe(true, Ids(0x10, 0x12, 0x13)));
        }

        Assert.Equal([0x11u], tracker.Observe(true, Ids(0x10, 0x12, 0x13)));
    }

    [Fact]
    public void ADeadWorldCountsNothingAndResetsStreaks()
    {
        MineVanishTracker tracker = Baselined(0x10, 0x11, 0x12, 0x13);

        // An area unload: every mine is "absent" but the world check fails, so nothing is
        // reported however long it lasts...
        for (int i = 0; i < 10; i++)
        {
            Assert.Empty(tracker.Observe(false, Ids()));
        }

        // ...and the streaks were zeroed, so the first world-alive pass with one mine missing
        // does not inherit any of that absence.
        Assert.Empty(tracker.Observe(true, Ids(0x10, 0x12, 0x13)));
        Assert.Empty(tracker.Observe(true, Ids(0x10, 0x12, 0x13)));
        Assert.Equal([0x11u], tracker.Observe(true, Ids(0x10, 0x12, 0x13)));
    }

    [Fact]
    public void AMineFirstSeenAfterTheBaselineWindowIsNeverReported()
    {
        // A player-placed mine has a per-instance dynamic id: it must stay local.
        MineVanishTracker tracker = Baselined(0x10, 0x11);

        Assert.Empty(tracker.Observe(true, Ids(0x10, 0x11, 0x90)));
        for (int i = 0; i < 20; i++)
        {
            Assert.Empty(tracker.Observe(true, Ids(0x10, 0x11)));
        }
    }

    [Fact]
    public void ALateAppearingGitMineInsideTheWindowIsStillBaselined()
    {
        MineVanishTracker tracker = new();
        Assert.Empty(tracker.Observe(true, Ids(0x10)));
        Assert.Empty(tracker.Observe(true, Ids(0x10, 0x11)));
        Assert.Empty(tracker.Observe(true, Ids(0x10, 0x11)));

        for (int i = 1; i < MineVanishTracker.AbsentPassesToReport; i++)
        {
            Assert.Empty(tracker.Observe(true, Ids(0x10)));
        }

        Assert.Equal([0x11u], tracker.Observe(true, Ids(0x10)));
    }

    [Fact]
    public void AWorldDeadPassDoesNotCountTowardTheBaselineWindow()
    {
        MineVanishTracker tracker = new();
        Assert.Empty(tracker.Observe(false, Ids()));
        Assert.False(tracker.BaselineFrozen);
        Assert.Empty(tracker.Observe(true, Ids(0x10)));
        Assert.False(tracker.BaselineFrozen);
    }

    [Fact]
    public void AMassVanishIsDroppedNotReported()
    {
        // Most of the baseline disappearing in one go is an unload the world gate missed.
        MineVanishTracker tracker = Baselined(0x10, 0x11, 0x12, 0x13, 0x14);

        List<uint> reported = [];
        for (int i = 0; i < MineVanishTracker.AbsentPassesToReport + 2; i++)
        {
            reported.AddRange(tracker.Observe(true, Ids(0x14)));
        }

        Assert.Empty(reported);
        Assert.Equal(1, tracker.TrackedCount);
    }

    [Fact]
    public void TwoIndependentDisarmsOutOfManyAreStillReported()
    {
        MineVanishTracker tracker = Baselined(0x10, 0x11, 0x12, 0x13, 0x14, 0x15);

        List<uint> reported = [];
        for (int i = 0; i < MineVanishTracker.AbsentPassesToReport; i++)
        {
            reported.AddRange(tracker.Observe(true, Ids(0x10, 0x11, 0x14, 0x15)));
        }

        reported.Sort();
        Assert.Equal([0x12u, 0x13u], reported);
    }

    [Fact]
    public void AnIdTheSessionAlreadyKnowsIsGoneIsNeverEchoed()
    {
        MineVanishTracker tracker = Baselined(0x10, 0x11, 0x12, 0x13);
        tracker.MarkKnownGone(0x11);

        for (int i = 0; i < 10; i++)
        {
            Assert.Empty(tracker.Observe(true, Ids(0x10, 0x12, 0x13)));
        }
    }

    [Fact]
    public void ResetStartsAFreshBaselineForTheNextModule()
    {
        MineVanishTracker tracker = Baselined(0x10, 0x11, 0x12);
        tracker.Reset();

        Assert.False(tracker.BaselineFrozen);
        Assert.Equal(0, tracker.TrackedCount);
        Assert.Empty(tracker.Observe(true, Ids(0x20)));
    }

    // ---- MineRemovalRule ---------------------------------------------------------------------

    [Fact]
    public void RemovalIsRefusedWhenTheSwitchIsOff()
    {
        Assert.Equal(
            MineRemovalDecision.Disabled,
            MineRemovalRule.Decide(enabled: false, false, true, true, true));
    }

    [Fact]
    public void AnUnresolvedIdIsTheQuietAlreadyGoneCase()
    {
        Assert.Equal(
            MineRemovalDecision.NotResolved,
            MineRemovalRule.Decide(true, false, resolvedAsTrigger: false, false, false));
    }

    [Fact]
    public void AnOrdinaryTriggerIsNeverDeleted()
    {
        Assert.Equal(
            MineRemovalDecision.NotAMine,
            MineRemovalRule.Decide(true, false, true, trapGateSet: false, destroyable: true));
    }

    [Fact]
    public void ANonDestroyableTrapIsLeftAlone()
    {
        Assert.Equal(
            MineRemovalDecision.NotDestroyable,
            MineRemovalRule.Decide(true, false, true, true, destroyable: false));
    }

    [Fact]
    public void ADestroyAlreadyPostedIsNotPostedTwice()
    {
        Assert.Equal(
            MineRemovalDecision.AlreadyRequested,
            MineRemovalRule.Decide(true, alreadyRequested: true, true, true, true));
    }

    [Fact]
    public void ALiveDestroyableTrapIsRemoved()
    {
        Assert.Equal(
            MineRemovalDecision.Remove,
            MineRemovalRule.Decide(true, false, true, true, true));
    }

    // ---- the ON default, three readers ------------------------------------------------------

    [Fact]
    public void MineSyncIsOnWhenTheKeyIsAbsent()
    {
        string path = Path.Combine(Path.GetTempPath(), $"coop-mine-{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, "mode = host");
        try
        {
            CoopConfig? c = CoopConfig.TryLoad(path);
            Assert.NotNull(c);
            Assert.True(c.MineSync);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MineSyncTurnsOffWhenTheKeyIsZero()
    {
        string path = Path.Combine(Path.GetTempPath(), $"coop-mine-{Guid.NewGuid():N}.ini");
        File.WriteAllText(path, "mode = host\nmine_sync = 1");
        try
        {
            Assert.True(CoopConfig.TryLoad(path)!.MineSync);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheAgentCompositionFallbackAgreesOnOn()
    {
        string source = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "KotorCoop.Agent", "AgentComposition.cs"));
        Assert.Contains("MineSync = config?.MineSync ?? true", source);

        string plugin = File.ReadAllText(Path.Combine(
            RepositoryRoot(), "src", "KotorCoop.Plugin", "MineController.cs"));
        Assert.Contains("PluginConfig.ReadBool(\"mine_sync\", true)", plugin);
    }

    private static string RepositoryRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "KotorCoop.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return dir;
    }
}
