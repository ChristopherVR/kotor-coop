using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The suppression ledger <c>ScriptItemGrantPump</c>'s companion pumps feed and
/// <see cref="ScriptItemGrantRule"/> consumes — docs/research/dialogue-consequences.md's
/// script-item-sync section.
/// </summary>
public sealed class RecentlySyncedItemsTests
{
    [Fact]
    public void ARecordedItemIsConsumedExactlyOnce()
    {
        RecentlySyncedItems ledger = new();
        ledger.Record("g_w_blstrpstl001", 1, nowMs: 0);

        Assert.True(ledger.TryConsume("g_w_blstrpstl001", nowMs: 10));
        Assert.False(ledger.TryConsume("g_w_blstrpstl001", nowMs: 20));
    }

    [Fact]
    public void ConsumingAResrefNeverRecordedFails()
    {
        RecentlySyncedItems ledger = new();
        Assert.False(ledger.TryConsume("g_w_blstrpstl001", nowMs: 0));
    }

    [Fact]
    public void RecordingSeveralUnitsAllowsThatManyConsumes()
    {
        RecentlySyncedItems ledger = new();
        ledger.Record("g_w_blstrpstl001", 3, nowMs: 0);

        Assert.True(ledger.TryConsume("g_w_blstrpstl001", nowMs: 0));
        Assert.True(ledger.TryConsume("g_w_blstrpstl001", nowMs: 0));
        Assert.True(ledger.TryConsume("g_w_blstrpstl001", nowMs: 0));
        Assert.False(ledger.TryConsume("g_w_blstrpstl001", nowMs: 0));
    }

    [Fact]
    public void AnEntryExpiresAfterTheWindowElapses()
    {
        RecentlySyncedItems ledger = new(windowMs: 5000);
        ledger.Record("g_w_blstrpstl001", 1, nowMs: 0);

        // Still inside the window.
        Assert.False(ledger.TryConsume("other_resref", nowMs: 4999)); // proves no cross-resref leak
        Assert.True(ledger.Count > 0);

        // Past the window: the entry is pruned lazily on the next touch and no longer matches.
        Assert.False(ledger.TryConsume("g_w_blstrpstl001", nowMs: 5001));
    }

    [Fact]
    public void DifferentResrefsDoNotCrossMatch()
    {
        RecentlySyncedItems ledger = new();
        ledger.Record("g_w_blstrpstl001", 1, nowMs: 0);

        Assert.False(ledger.TryConsume("g_a_class4001", nowMs: 0));
        Assert.True(ledger.TryConsume("g_w_blstrpstl001", nowMs: 0));
    }

    [Fact]
    public void RecordingZeroOrNegativeCountIsANoOp()
    {
        RecentlySyncedItems ledger = new();
        ledger.Record("g_w_blstrpstl001", 0, nowMs: 0);
        ledger.Record("g_w_blstrpstl001", -1, nowMs: 0);

        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public void RecordingAnEmptyResrefIsANoOp()
    {
        RecentlySyncedItems ledger = new();
        ledger.Record(string.Empty, 1, nowMs: 0);

        Assert.Equal(0, ledger.Count);
    }

    [Fact]
    public void ClearDropsEveryPendingEntry()
    {
        RecentlySyncedItems ledger = new();
        ledger.Record("g_w_blstrpstl001", 2, nowMs: 0);
        ledger.Clear();

        Assert.Equal(0, ledger.Count);
        Assert.False(ledger.TryConsume("g_w_blstrpstl001", nowMs: 0));
    }

    [Fact]
    public void MatchIsCaseInsensitive()
    {
        RecentlySyncedItems ledger = new();
        ledger.Record("G_W_BLSTRPSTL001", 1, nowMs: 0);

        Assert.True(ledger.TryConsume("g_w_blstrpstl001", nowMs: 0));
    }
}
