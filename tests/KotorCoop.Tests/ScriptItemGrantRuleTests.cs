using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pure coverage for <see cref="ScriptItemGrantRule"/> — the decision
/// <c>KotorCoop.Session.Pumps.ScriptItemGrantPump</c> drives, docs/research/
/// dialogue-consequences.md's script-item-sync section. No bridge, no session, no game: two
/// possessed-item snapshots and a suppression ledger in, the net-new rows to publish (and the
/// next baseline) out.
/// </summary>
public sealed class ScriptItemGrantRuleTests
{
    private static (string Resref, ItemIdentity Identity) Row(
        string resref, ushort stack = 1, byte charges = 0, byte maxCharges = 0,
        uint upgrades = 0, byte flags = 0) =>
        (resref, new ItemIdentity(stack, charges, maxCharges, upgrades, flags));

    /// <summary>A resref none of the already-synced channels fed the ledger for is a genuine
    /// script grant: an OnEnter/OnDeath/heartbeat script created it, nothing else explains it.</summary>
    [Fact]
    public void AScriptGrantWithNoMatchingLedgerEntryIsReported()
    {
        List<(string, ItemIdentity)> before = [];
        List<(string, ItemIdentity)> after = [Row("g_w_lghtsbr01", charges: 3)];
        RecentlySyncedItems ledger = new();

        ScriptItemGrantRule.Result result =
            ScriptItemGrantRule.Evaluate(before, after, rebaselineOnly: false, ledger, nowMs: 0);

        MaterialisedLootItem item = Assert.Single(result.ToPublish);
        Assert.Equal("g_w_lghtsbr01", item.Resref);
        Assert.Equal(3, item.Identity.Charges);
        Assert.Equal(after, result.NewBaseline);
    }

    /// <summary>A ground-item pickup: WorldStatePump records the resref into the ledger the
    /// moment the item vanishes from the floor, before the rule ever sees the new row.</summary>
    [Fact]
    public void ANewRowExplainedByAGroundPickupIsSuppressed()
    {
        List<(string, ItemIdentity)> before = [];
        List<(string, ItemIdentity)> after = [Row("g_w_blstrpstl001")];
        RecentlySyncedItems ledger = new();
        ledger.Record("g_w_blstrpstl001", 1, nowMs: 0); // fed by WorldStatePump.PumpItems

        ScriptItemGrantRule.Result result =
            ScriptItemGrantRule.Evaluate(before, after, rebaselineOnly: false, ledger, nowMs: 0);

        Assert.Empty(result.ToPublish);
        Assert.Equal(after, result.NewBaseline);
    }

    /// <summary>Container/corpse loot: LootPump.PumpContainerLoot records the resref (resolved
    /// via IItemBridge.TryGetItemResref) for a removal this instance's own player made.</summary>
    [Fact]
    public void ANewRowExplainedByContainerLootIsSuppressed()
    {
        List<(string, ItemIdentity)> before = [];
        List<(string, ItemIdentity)> after = [Row("g_i_medeqpmnt01")];
        RecentlySyncedItems ledger = new();
        ledger.Record("g_i_medeqpmnt01", 1, nowMs: 0); // fed by LootPump.PumpContainerLoot

        ScriptItemGrantRule.Result result =
            ScriptItemGrantRule.Evaluate(before, after, rebaselineOnly: false, ledger, nowMs: 0);

        Assert.Empty(result.ToPublish);
    }

    /// <summary>A peer's own dialogue or script grant, applied here through the give-loot cheat
    /// channel: LootPump.PumpMaterialisedLoot records it while draining DrainDialogueGrants,
    /// before the automatic-give queue ever reaches the engine.</summary>
    [Fact]
    public void ANewRowExplainedByAPeerGrantAppliedViaGiveLootIsSuppressed()
    {
        List<(string, ItemIdentity)> before = [];
        List<(string, ItemIdentity)> after = [Row("g_w_blstrpstl001")];
        RecentlySyncedItems ledger = new();
        ledger.Record("g_w_blstrpstl001", 1, nowMs: 0); // fed by LootPump draining a peer grant

        ScriptItemGrantRule.Result result =
            ScriptItemGrantRule.Evaluate(before, after, rebaselineOnly: false, ledger, nowMs: 0);

        Assert.Empty(result.ToPublish);
    }

    /// <summary>A human's own manual `giveloot` targeting the local player: CoopAgent.
    /// NoteLocalItemGiven feeds the same ledger.</summary>
    [Fact]
    public void ANewRowExplainedByAManualGiveLootCheatIsSuppressed()
    {
        List<(string, ItemIdentity)> before = [];
        List<(string, ItemIdentity)> after = [Row("g_w_lghtsbr01", stack: 5)];
        RecentlySyncedItems ledger = new();
        ledger.Record("g_w_lghtsbr01", 1, nowMs: 0); // fed by CoopAgent.NoteLocalItemGiven

        ScriptItemGrantRule.Result result =
            ScriptItemGrantRule.Evaluate(before, after, rebaselineOnly: false, ledger, nowMs: 0);

        Assert.Empty(result.ToPublish);
    }

    /// <summary>Only ONE suppression is consumed per ledger entry: a second, genuinely new copy
    /// of the same resref that the ledger has no budget left for is still reported.</summary>
    [Fact]
    public void ASecondCopyBeyondTheLedgersBudgetIsStillReported()
    {
        List<(string, ItemIdentity)> before = [];
        List<(string, ItemIdentity)> after =
            [Row("g_w_blstrpstl001"), Row("g_w_blstrpstl001")];
        RecentlySyncedItems ledger = new();
        ledger.Record("g_w_blstrpstl001", 1, nowMs: 0); // only one pickup was recorded

        ScriptItemGrantRule.Result result =
            ScriptItemGrantRule.Evaluate(before, after, rebaselineOnly: false, ledger, nowMs: 0);

        MaterialisedLootItem item = Assert.Single(result.ToPublish);
        Assert.Equal("g_w_blstrpstl001", item.Resref);
    }

    /// <summary>rebaselineOnly (module change, first sample since attach, or the tick right
    /// after the dialogue pump releases its window) never diffs — it just adopts current as
    /// the next baseline, exactly like DialoguePump's own "just started owning" branch.</summary>
    [Fact]
    public void RebaselineOnlyReportsNothingAndAdoptsCurrent()
    {
        List<(string, ItemIdentity)> before = [];
        List<(string, ItemIdentity)> after = [Row("g_w_lghtsbr01")];
        RecentlySyncedItems ledger = new();

        ScriptItemGrantRule.Result result =
            ScriptItemGrantRule.Evaluate(before, after, rebaselineOnly: true, ledger, nowMs: 0);

        Assert.Empty(result.ToPublish);
        Assert.Equal(after, result.NewBaseline);
    }

    /// <summary>Stack growth on an existing row is not a grant here either — the rule delegates
    /// the actual diff to DialogueItemDiff.Compute, so it inherits that exact behaviour.</summary>
    [Fact]
    public void StackGrowthOnAnExistingStackIsNotAGrant()
    {
        List<(string, ItemIdentity)> before = [Row("g_i_medeqpmnt01", stack: 5)];
        List<(string, ItemIdentity)> after = [Row("g_i_medeqpmnt01", stack: 8)];
        RecentlySyncedItems ledger = new();

        ScriptItemGrantRule.Result result =
            ScriptItemGrantRule.Evaluate(before, after, rebaselineOnly: false, ledger, nowMs: 0);

        Assert.Empty(result.ToPublish);
        Assert.Equal(after, result.NewBaseline);
    }

    /// <summary>A ledger entry for a DIFFERENT resref must never suppress an unrelated genuine
    /// script grant landing in the same window.</summary>
    [Fact]
    public void ASuppressionEntryForADifferentResrefDoesNotCrossSuppress()
    {
        List<(string, ItemIdentity)> before = [];
        List<(string, ItemIdentity)> after = [Row("g_w_lghtsbr01")];
        RecentlySyncedItems ledger = new();
        ledger.Record("g_w_blstrpstl001", 1, nowMs: 0); // an unrelated ground pickup

        ScriptItemGrantRule.Result result =
            ScriptItemGrantRule.Evaluate(before, after, rebaselineOnly: false, ledger, nowMs: 0);

        MaterialisedLootItem item = Assert.Single(result.ToPublish);
        Assert.Equal("g_w_lghtsbr01", item.Resref);
    }
}
