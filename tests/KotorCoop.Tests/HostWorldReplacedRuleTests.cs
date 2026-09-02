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
    public void NothingHappensWithoutAnEdge()
    {
        (bool trigger, HostWorldReplacedState next) =
            HostWorldReplacedRule.Observe(HostWorldReplacedState.Initial, hostLoadEdge: false, Standing());

        Assert.False(trigger);
        Assert.False(next.Armed);
    }

    [Fact]
    public void AnEdgeWithTheHostAlreadyStandingFiresAtOnce()
    {
        // The serial rides the host's digest packet, which may only be polled after the host's
        // ~2 s reload has already finished: the edge and the standing row arrive together.
        (bool trigger, HostWorldReplacedState next) =
            HostWorldReplacedRule.Observe(HostWorldReplacedState.Initial, hostLoadEdge: true, Standing());

        Assert.True(trigger);
        Assert.False(next.Armed);
    }

    [Fact]
    public void AnEdgeSeenMidTeardownWaitsForTheHostToStand()
    {
        (bool trigger, HostWorldReplacedState next) =
            HostWorldReplacedRule.Observe(HostWorldReplacedState.Initial, hostLoadEdge: true, Away());
        Assert.False(trigger);
        Assert.True(next.Armed);

        (trigger, next) = HostWorldReplacedRule.Observe(next, hostLoadEdge: false, NoModule());
        Assert.False(trigger);
        Assert.True(next.Armed);

        (trigger, next) = HostWorldReplacedRule.Observe(next, hostLoadEdge: false, null);
        Assert.False(trigger);
        Assert.True(next.Armed);

        (trigger, next) = HostWorldReplacedRule.Observe(next, hostLoadEdge: false, Standing());
        Assert.True(trigger);
        Assert.False(next.Armed);
    }

    [Fact]
    public void ItFiresOncePerEdge()
    {
        (bool trigger, HostWorldReplacedState next) =
            HostWorldReplacedRule.Observe(HostWorldReplacedState.Initial, hostLoadEdge: true, Standing());
        Assert.True(trigger);

        (trigger, next) = HostWorldReplacedRule.Observe(next, hostLoadEdge: false, Standing());
        Assert.False(trigger);
        Assert.False(next.Armed);
    }

    [Fact]
    public void DisarmForgetsAPendingEdge()
    {
        // Another trigger (the digest-correlated HostReload, or the wipe watch) already started
        // the rejoin for this same load: the pending edge must not start a second one.
        (_, HostWorldReplacedState armed) =
            HostWorldReplacedRule.Observe(HostWorldReplacedState.Initial, hostLoadEdge: true, Away());
        Assert.True(armed.Armed);

        HostWorldReplacedState disarmed = HostWorldReplacedRule.Disarm();
        (bool trigger, HostWorldReplacedState next) =
            HostWorldReplacedRule.Observe(disarmed, hostLoadEdge: false, Standing());

        Assert.False(trigger);
        Assert.False(next.Armed);
    }
}
