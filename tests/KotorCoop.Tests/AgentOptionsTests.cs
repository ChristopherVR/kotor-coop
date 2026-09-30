using KotorCoop.Agent;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public class AgentOptionsTests
{
    [Fact]
    public void PortExplicitIsFalseWhenPortIsNotPassed()
    {
        // No --port at all: Port keeps the built-in default, but PortExplicit records that the
        // CLI said nothing, so a caller (AgentComposition.Compose) can tell "the CLI wants
        // 27015" apart from "the CLI did not say" and let coop.ini's own port win instead.
        AgentOptions options = AgentOptions.Parse(["run", "--host"]);

        Assert.Equal(AgentCommand.Run, options.Command);
        Assert.Equal(ProtocolConstants.DefaultPort, options.Port);
        Assert.False(options.PortExplicit);
    }

    [Fact]
    public void PortExplicitIsTrueWhenPortIsPassed()
    {
        AgentOptions options = AgentOptions.Parse(["run", "--host", "--port", "27140"]);

        Assert.Equal(27140, options.Port);
        Assert.True(options.PortExplicit);
    }

    [Fact]
    public void ExplicitPortEvenAtTheDefaultValueStillCountsAsExplicit()
    {
        // Passing --port 27015 explicitly (same number as the built-in default) must still be
        // distinguishable from not passing --port at all, otherwise --join host --port 27015
        // could not reliably override a --join host:1234 endpoint's own port.
        AgentOptions options = AgentOptions.Parse(["run", "--host", "--port", "27015"]);

        Assert.Equal(ProtocolConstants.DefaultPort, options.Port);
        Assert.True(options.PortExplicit);
    }

    [Fact]
    public void ExplicitPortOverridesTheJoinEndpointsOwnPort()
    {
        AgentOptions options = AgentOptions.Parse(
            ["run", "--join", "10.0.0.5:9999", "--port", "27140"]);

        Assert.NotNull(options.HostEndPoint);
        Assert.Equal(27140, options.HostEndPoint!.Port);
    }

    [Fact]
    public void JoinEndpointKeepsItsOwnPortWithNoExplicitPortFlag()
    {
        AgentOptions options = AgentOptions.Parse(["run", "--join", "10.0.0.5:9999"]);

        Assert.NotNull(options.HostEndPoint);
        Assert.Equal(9999, options.HostEndPoint!.Port);
        Assert.False(options.PortExplicit);
    }
    [Theory]
    [InlineData("10", 10)]
    [InlineData("99", 99)]
    [InlineData("100", 99)]
    [InlineData("0", 1)]
    public void InstanceNamesFitTheSevenByteNativeMutexReplacement(string requested, int expected)
    {
        AgentOptions options = AgentOptions.Parse(["launch", "--instance", requested]);

        Assert.Equal(expected, options.Instance);
        Assert.True($"kcoop{options.Instance}".Length <= "swkotor".Length);
    }
}
