using KotorCoop.Agent;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure halves of <see cref="WindowsFirewall"/>: the exact <c>netsh</c> commands the
/// installer runs or prints (program-scoped, one rule name for both entries, the session port
/// plus the discovery port on UDP and the session port alone on TCP), the <c>coop.ini</c> port
/// lookup with the same fallback <c>CoopConfig</c> applies, and the exit-code reading of
/// <c>show rule</c>. Nothing here spawns a process.
/// </summary>
public sealed class WindowsFirewallTests
{
    [Fact]
    public void AddCommandsCoverUdpSessionAndDiscoveryPortsAndTcpSessionPortUnderOneName()
    {
        IReadOnlyList<string> commands = WindowsFirewall.AddCommands(@"C:\Games\swkotor\KotorCoop.Agent.exe", 27140);

        Assert.Equal(2, commands.Count);
        Assert.All(commands, c => Assert.StartsWith("netsh advfirewall firewall add rule name=\"KotorCoop Agent\"", c));
        Assert.All(commands, c => Assert.Contains("program=\"C:\\Games\\swkotor\\KotorCoop.Agent.exe\"", c));
        Assert.All(commands, c => Assert.Contains("dir=in action=allow", c));

        Assert.Contains("protocol=UDP", commands[0]);
        Assert.Contains($"localport=27140,{ServerDiscovery.DiscoveryPort}", commands[0]);
        Assert.Contains("protocol=TCP", commands[1]);
        Assert.Contains("localport=27140 ", commands[1]);
    }

    [Fact]
    public void DeleteAndShowNameTheSameRule()
    {
        Assert.Equal("netsh advfirewall firewall delete rule name=\"KotorCoop Agent\"", WindowsFirewall.DeleteCommand());
        Assert.Equal("netsh advfirewall firewall show rule name=\"KotorCoop Agent\"", WindowsFirewall.ShowCommand());
    }

    [Fact]
    public void SessionPortReadsTheIniAndFallsBackToTheCodeDefault()
    {
        Assert.Equal(27140, WindowsFirewall.SessionPort("; comment\nname = Revan\nport = 27140\n"));
        Assert.Equal(27140, WindowsFirewall.SessionPort("Port=27140 ; trailing comment"));
        Assert.Equal(ProtocolConstants.DefaultPort, WindowsFirewall.SessionPort(null));
        Assert.Equal(ProtocolConstants.DefaultPort, WindowsFirewall.SessionPort("name = Revan\n"));
        Assert.Equal(ProtocolConstants.DefaultPort, WindowsFirewall.SessionPort("port = not-a-number"));
        Assert.Equal(ProtocolConstants.DefaultPort, WindowsFirewall.SessionPort("port = 70000"));
        // A commented-out port line is not a setting.
        Assert.Equal(ProtocolConstants.DefaultPort, WindowsFirewall.SessionPort("; port = 27140\n# port = 27141\n"));
    }

    [Fact]
    public void ShowRuleExitCodeZeroMeansTheRuleExists()
    {
        Assert.True(WindowsFirewall.RuleListed(0));
        Assert.False(WindowsFirewall.RuleListed(1));
    }

    [Fact]
    public void TheAgentExecutableLivesInTheGameDirectory()
    {
        Assert.Equal(
            Path.Combine(@"C:\Games\swkotor", "KotorCoop.Agent.exe"),
            WindowsFirewall.AgentExecutable(@"C:\Games\swkotor"));
    }
}
