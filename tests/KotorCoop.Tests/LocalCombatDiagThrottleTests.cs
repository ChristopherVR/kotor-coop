using KotorCoop.Session.Pumps;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The rate-limiting decision behind CombatPump.PumpEnemies' "local combat diag (agent)"
/// TEMPORARY DIAGNOSTIC lines (docs/research/combat-participation.md §11): a fight used to
/// print the same numbers several times a SECOND, since PumpEnemies runs on the ~150ms combat
/// cadence and both lines fired unconditionally whenever local_combat's exclusion filter had
/// anything excluded.
/// </summary>
public class LocalCombatDiagThrottleTests
{
    private static readonly uint[] Ids = [0x4D, 0x4A];

    [Fact]
    public void FirstEverTickAlwaysLogsWithNoPriorSnapshot()
    {
        LocalCombatDiagSnapshot current = new(20, 18, LocalCombatDiagThrottle.ExcludedKey(Ids));

        Assert.True(LocalCombatDiagThrottle.ShouldLog(current, last: null, nowMs: 0, lastLoggedMs: 0));
    }

    [Fact]
    public void UnchangedValuesDoNotLogAgainBeforeTheInterval()
    {
        LocalCombatDiagSnapshot snapshot = new(20, 18, LocalCombatDiagThrottle.ExcludedKey(Ids));

        bool shouldLog = LocalCombatDiagThrottle.ShouldLog(
            snapshot, last: snapshot, nowMs: 5_000, lastLoggedMs: 0);

        Assert.False(shouldLog);
    }

    [Fact]
    public void UnchangedValuesLogAgainOnceTheIntervalElapses()
    {
        LocalCombatDiagSnapshot snapshot = new(20, 18, LocalCombatDiagThrottle.ExcludedKey(Ids));

        bool shouldLog = LocalCombatDiagThrottle.ShouldLog(
            snapshot, last: snapshot, nowMs: LocalCombatDiagThrottle.MinIntervalMs, lastLoggedMs: 0);

        Assert.True(shouldLog);
    }

    [Fact]
    public void UnchangedValuesDoNotLogExactlyOneMsBeforeTheInterval()
    {
        LocalCombatDiagSnapshot snapshot = new(20, 18, LocalCombatDiagThrottle.ExcludedKey(Ids));

        bool shouldLog = LocalCombatDiagThrottle.ShouldLog(
            snapshot, last: snapshot, nowMs: LocalCombatDiagThrottle.MinIntervalMs - 1, lastLoggedMs: 0);

        Assert.False(shouldLog);
    }

    [Fact]
    public void AChangedEnemyCountLogsImmediately()
    {
        LocalCombatDiagSnapshot last = new(20, 18, LocalCombatDiagThrottle.ExcludedKey(Ids));
        LocalCombatDiagSnapshot current = last with { EnemyCount = 19 };

        Assert.True(LocalCombatDiagThrottle.ShouldLog(current, last, nowMs: 1, lastLoggedMs: 0));
    }

    [Fact]
    public void AChangedFilteredCountLogsImmediately()
    {
        LocalCombatDiagSnapshot last = new(20, 18, LocalCombatDiagThrottle.ExcludedKey(Ids));
        LocalCombatDiagSnapshot current = last with { FilteredCount = 17 };

        Assert.True(LocalCombatDiagThrottle.ShouldLog(current, last, nowMs: 1, lastLoggedMs: 0));
    }

    [Fact]
    public void AChangedExcludedIdSetLogsImmediately()
    {
        LocalCombatDiagSnapshot last = new(20, 18, LocalCombatDiagThrottle.ExcludedKey(Ids));
        LocalCombatDiagSnapshot current = new(20, 18, LocalCombatDiagThrottle.ExcludedKey([0x4D]));

        Assert.True(LocalCombatDiagThrottle.ShouldLog(current, last, nowMs: 1, lastLoggedMs: 0));
    }

    [Fact]
    public void ExcludedKeyIsOrderIndependent()
    {
        // The owner's excluded-id array order is not guaranteed stable tick to tick; a reorder
        // of the SAME ids must not read as a change.
        string a = LocalCombatDiagThrottle.ExcludedKey([0x4D, 0x4A]);
        string b = LocalCombatDiagThrottle.ExcludedKey([0x4A, 0x4D]);

        Assert.Equal(a, b);
    }

    [Fact]
    public void ExcludedKeyDistinguishesDifferentSets()
    {
        string a = LocalCombatDiagThrottle.ExcludedKey([0x4D, 0x4A]);
        string b = LocalCombatDiagThrottle.ExcludedKey([0x4D]);

        Assert.NotEqual(a, b);
    }
}
