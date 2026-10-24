using KotorCoop.Agent;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class SteamLaunchRuleTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("+connect")]
    [InlineData("--steam-join")]
    public void RetailLaunchInviteReachesTheAgentWithItsEndpointAndPassword(string? wrapper)
    {
        string connect = SteamConnectStringRule.Format("127.0.0.1", 27140, "fixture-password")!;
        string[] game = wrapper is null ? ["swkotor.exe", connect] : ["swkotor.exe", wrapper, connect];
        string[] forwarded = SteamLaunchRule.AgentArguments(game, firstLaunch: true);
        AgentOptions options = AgentOptions.Parse(["run", "--pid", "42", "--kotor", .. forwarded]);
        Assert.Equal(AgentCommand.Run, options.Command);
        Assert.Equal(CoopMode.Join, options.Mode);
        Assert.False(options.MenuConnect);
        Assert.Equal("127.0.0.1", options.HostEndPoint!.Address.ToString());
        Assert.Equal(27140, options.HostEndPoint.Port);
        Assert.Equal("fixture-password", options.Password);
        Assert.True(options.CanStartWithoutLoadedWorld(JoinMode.Save));
    }

    [Fact]
    public void RestartDoesNotReplayTheOldInvitation()
    {
        Assert.Equal(["--menu"], SteamLaunchRule.AgentArguments(
            ["swkotor.exe", "kc1/127.0.0.1:27140"], firstLaunch: false));
    }

    [Theory]
    [InlineData("kc1/host:0")]
    [InlineData("kc1/host:27140/\" --host")]
    [InlineData("kc1/host:27140/a\\b")]
    [InlineData("kc1/host:27140/abc def")]
    [InlineData("kc2/host:27140")]
    [InlineData("-windowed")]
    public void UnusableOrUnrelatedArgumentsLeaveTheGameAtTheMenu(string input)
    {
        Assert.Equal(["--menu"], SteamLaunchRule.AgentArguments(["swkotor.exe", input], true));
    }

    [Fact]
    public void MissingOrAmbiguousInvitesNeverChooseAnEndpoint()
    {
        Assert.Equal(["--menu"], SteamLaunchRule.AgentArguments(["swkotor.exe", "+connect"], true));
        Assert.Equal(["--menu"], SteamLaunchRule.AgentArguments(
            ["swkotor.exe", "kc1/127.0.0.1:27140", "kc1/127.0.0.2:27140"], true));
        Assert.Equal(["--menu"], SteamLaunchRule.AgentArguments(
            ["swkotor.exe", "kc1/127.0.0.1:27140", "+connect", "kc1/127.0.0.2:27140"], true));
    }

    [Fact]
    public void OnlyArgumentsAfterTheExecutableCanBeInvites()
    {
        Assert.Equal(["--menu"], SteamLaunchRule.AgentArguments(["kc1/127.0.0.1:27140"], true));
    }

    [Fact]
    public void OnlyInvitationAndMenuFlowsMayStartSaveModeWithoutAWorld()
    {
        AgentOptions scripted = AgentOptions.Parse(["run", "--join", "127.0.0.1:27140"]);
        Assert.False(scripted.CanStartWithoutLoadedWorld(JoinMode.Save));
        Assert.True(scripted.CanStartWithoutLoadedWorld(JoinMode.Auto));
        AgentOptions menu = AgentOptions.Parse(["run", "--menu"]);
        Assert.True(menu.CanStartWithoutLoadedWorld(JoinMode.Save));
        AgentOptions host = AgentOptions.Parse(["run", "--host"]);
        Assert.False(host.CanStartWithoutLoadedWorld(JoinMode.Save));
    }
}
