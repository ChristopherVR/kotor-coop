using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure decision behind client-side host-loss detection — see <see cref="HostLossRule"/>'s
/// own doc comment for how this fits alongside the explicit-Bye trigger, which has no timing
/// decision to make and so is not exercised here (see HostLossTests.cs for that, over a real
/// loopback session).
/// </summary>
public class HostLossRuleTests
{
    [Fact]
    public void DoesNotDeclareBeforeTheTimeoutIsCrossed()
    {
        Assert.False(HostLossRule.ShouldDeclare(enabled: true, alreadyLost: false, silenceMs: 5000, timeoutMs: 5000));
        Assert.False(HostLossRule.ShouldDeclare(enabled: true, alreadyLost: false, silenceMs: 4999, timeoutMs: 5000));
    }

    [Fact]
    public void DeclaresTheInstantSilenceStrictlyExceedsTheTimeout()
    {
        Assert.True(HostLossRule.ShouldDeclare(enabled: true, alreadyLost: false, silenceMs: 5001, timeoutMs: 5000));
    }

    [Fact]
    public void NeverDeclaresWhenTheFeatureIsDisabled()
    {
        Assert.False(
            HostLossRule.ShouldDeclare(enabled: false, alreadyLost: false, silenceMs: 999_999, timeoutMs: 5000));
    }

    [Fact]
    public void IsIdempotentOnceAlreadyDeclared()
    {
        // A caller that keeps evaluating this every tick (CoopClient.Tick does) must never see
        // it flip true a second time on its own — only the caller clearing alreadyLost (a fresh
        // Welcome) can do that.
        Assert.False(
            HostLossRule.ShouldDeclare(enabled: true, alreadyLost: true, silenceMs: 999_999, timeoutMs: 5000));
    }

    [Fact]
    public void ASeparateTimeoutFromPeerTimeoutMsIsHonoured()
    {
        // A player who configured host_loss_timeout_ms higher than the reconnect trigger gets
        // the extra grace this rule alone is responsible for enforcing.
        Assert.False(HostLossRule.ShouldDeclare(enabled: true, alreadyLost: false, silenceMs: 6000, timeoutMs: 10_000));
        Assert.True(HostLossRule.ShouldDeclare(enabled: true, alreadyLost: false, silenceMs: 10_001, timeoutMs: 10_000));
    }
}
