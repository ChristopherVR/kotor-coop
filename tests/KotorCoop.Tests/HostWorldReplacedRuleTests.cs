using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="HostWorldReplacedRule"/> — the serial-only fallback for a host load the digest
/// classifier never sees. The live shape this pins (2026-09-02, docs/research/session-save.md):
/// the host ran <c>loadgame 0</c> beside the client, its world-load serial ticked, its digest
/// did not change (the save was the one already staged), and the client sat in a diverged
/// world with no trigger at all.
/// </summary>
public sealed class HostWorldReplacedRuleTests
{
    private static PlayerState Standing(string module = "end_m01aa") =>
        new(Vector3f.Zero, 0f, module, 12, 0, MaxHealth: 12);

    private static PlayerState Away() =>
        new(Vector3f.Zero, 0f, "end_m01aa", 12, 0, MaxHealth: 12, Flags: PlayerState.FlagAway);

    private static PlayerState NoModule() =>
        new(Vector3f.Zero, 0f, "", 12, 0, MaxHealth: 12);

    [Fact]
    public void HostLoadEdgeWaitsThroughoutControlledSceneThenTriggersAfterNativeReturn()
    {
        PlayerState controlled = Standing("bos_ebovis") with { Flags = PlayerState.FlagSoloSegment };
        var (trigger, armed) = HostWorldReplacedRule.Observe(HostWorldReplacedState.Initial, true, controlled, controlled.Module);
        Assert.False(trigger);
        Assert.True(armed.Armed);
        (trigger, armed) = HostWorldReplacedRule.Observe(armed, false, controlled, controlled.Module);
        Assert.False(trigger);
        Assert.True(armed.Armed);
        (trigger, armed) = HostWorldReplacedRule.Observe(armed, false, controlled with { Flags = 0 }, controlled.Module);
        Assert.True(trigger);
        Assert.False(armed.Armed);
    }

    [Fact]
    public void NothingHappensWithoutAnEdge()
    {
        (bool trigger, HostWorldReplacedState next) =
            HostWorldReplacedRule.Observe(HostWorldReplacedState.Initial, hostLoadEdge: false, Standing(), "end_m01aa");

        Assert.False(trigger);
        Assert.False(next.Armed);
    }

    [Fact]
    public void EdgePublicationArmsThenReadySameModuleTriggers()
    {
        // CoopAgent intentionally blanks the digest module on the publication that first
        // observes a new serial. Even if the retained row looks standing, wait for the next
        // successful sample at that serial before following the host.
        (bool trigger, HostWorldReplacedState next) =
            HostWorldReplacedRule.Observe(HostWorldReplacedState.Initial, hostLoadEdge: true, Standing(), "");

        Assert.False(trigger);
        Assert.True(next.Armed);

        (trigger, next) = HostWorldReplacedRule.Observe(next, hostLoadEdge: false, Standing(), "end_m01aa");
        Assert.True(trigger);
        Assert.False(next.Armed);
    }

    [Fact]
    public void AnEdgeSeenMidTeardownWaitsForTheHostToStand()
    {
        (bool trigger, HostWorldReplacedState next) =
            HostWorldReplacedRule.Observe(HostWorldReplacedState.Initial, hostLoadEdge: true, Away(), "");
        Assert.False(trigger);
        Assert.True(next.Armed);

        (trigger, next) = HostWorldReplacedRule.Observe(next, hostLoadEdge: false, NoModule(), "");
        Assert.False(trigger);
        Assert.True(next.Armed);

        (trigger, next) = HostWorldReplacedRule.Observe(next, hostLoadEdge: false, null, "");
        Assert.False(trigger);
        Assert.True(next.Armed);

        (trigger, next) = HostWorldReplacedRule.Observe(next, hostLoadEdge: false, Standing(), "");
        Assert.False(trigger); // stale standing row cannot prove the new host world is ready
        Assert.True(next.Armed);

        (trigger, next) = HostWorldReplacedRule.Observe(next, hostLoadEdge: false, Standing(), "end_m01aa");
        Assert.True(trigger);
        Assert.False(next.Armed);
    }

    [Fact]
    public void ItFiresOncePerEdge()
    {
        (bool trigger, HostWorldReplacedState next) =
            HostWorldReplacedRule.Observe(HostWorldReplacedState.Initial, hostLoadEdge: true, Standing(), "");
        Assert.False(trigger);

        (trigger, next) = HostWorldReplacedRule.Observe(next, hostLoadEdge: false, Standing(), "end_m01aa");
        Assert.True(trigger);

        (trigger, next) = HostWorldReplacedRule.Observe(next, hostLoadEdge: false, Standing(), "end_m01aa");
        Assert.False(trigger);
        Assert.False(next.Armed);
    }

    [Fact]
    public void DisarmForgetsAPendingEdge()
    {
        // Another trigger (the digest-correlated HostReload, or the wipe watch) already started
        // the rejoin for this same load: the pending edge must not start a second one.
        (_, HostWorldReplacedState armed) =
            HostWorldReplacedRule.Observe(HostWorldReplacedState.Initial, hostLoadEdge: true, Away(), "");
        Assert.True(armed.Armed);

        HostWorldReplacedState disarmed = HostWorldReplacedRule.Disarm();
        (bool trigger, HostWorldReplacedState next) =
            HostWorldReplacedRule.Observe(disarmed, hostLoadEdge: false, Standing(), "end_m01aa");

        Assert.False(trigger);
        Assert.False(next.Armed);
    }

    [Fact]
    public void StandingRowWithEmptyOrDifferentDigestModuleIsNotReady()
    {
        Assert.False(HostWorldReplacedRule.IsHostReady(Standing(), ""));
        Assert.False(HostWorldReplacedRule.IsHostReady(Standing(), "tar_m03aa"));
        Assert.True(HostWorldReplacedRule.IsHostReady(Standing(), "END_M01AA"));
    }

    [Fact]
    public void LoadEdgeAndStaleStandingRowWaitForMatchingDigest()
    {
        (bool trigger, HostWorldReplacedState next) =
            HostWorldReplacedRule.Observe(HostWorldReplacedState.Initial, hostLoadEdge: true, Standing(), "");

        Assert.False(trigger);
        Assert.True(next.Armed);

        (trigger, next) = HostWorldReplacedRule.Observe(next, hostLoadEdge: false, Standing(), "tar_m03aa");
        Assert.False(trigger);
        Assert.True(next.Armed);

        (trigger, next) = HostWorldReplacedRule.Observe(next, hostLoadEdge: false, Standing(), "end_m01aa");
        Assert.True(trigger);
        Assert.False(next.Armed);
    }

    [Fact]
    public void FailedLocalReadPublishesUnreadyModuleButKeepsTeardownSerial()
    {
        WorldDigest lastGood = new(
            GlobalsCatalogue: 0x1234,
            SaveIdentity: 0x5678,
            Module: "end_m01aa",
            ModuleGit: 0x9ABC,
            LoadSerial: 42,
            DifficultyLevel: 1);

        WorldDigest duringTeardown = CoopAgent.ForCurrentReadAvailability(lastGood, readSucceeded: false);

        Assert.Equal(string.Empty, duringTeardown.Module);
        Assert.Equal(0u, duringTeardown.ModuleGit);
        Assert.Equal(42u, duringTeardown.LoadSerial);
        Assert.Equal(0x1234u, duringTeardown.GlobalsCatalogue);
        Assert.False(HostWorldReplacedRule.IsHostReady(Standing(), duringTeardown.Module));
    }

    [Fact]
    public void SuccessfulCurrentReadPreservesModuleForFastReadyPath()
    {
        WorldDigest current = new(
            GlobalsCatalogue: 0x1234,
            SaveIdentity: 0x5678,
            Module: "end_m01aa",
            ModuleGit: 0x9ABC,
            LoadSerial: 42,
            DifficultyLevel: 1);

        Assert.Equal(current, CoopAgent.ForCurrentReadAvailability(current, readSucceeded: true));
    }
}
