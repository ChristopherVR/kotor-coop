using System.Text.RegularExpressions;
using KotorCoop.Agent;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The unattended item-identity harness: the id grammar its verbs share, the refusal
/// sentences they owe, and the one number that has to be the same in two files.
///
/// None of this needs a game, and the parts that do are exactly the parts a unit test cannot
/// help with — which is why the harness exists. What a test CAN pin is that a refusal names
/// its reason (the failure mode that cost a whole live pass on 2026-08-15, when
/// `drop #131` answered "object 0x131 is not a readable item here" for a ground body the
/// plugin log had announced creating four seconds earlier, and the sentence named neither
/// which half had failed nor what the sweep had seen).
/// </summary>
public class ItemHarnessTests
{
    /// <summary>A bridge with no game behind it: enough to reach the dispatcher's decisions.</summary>
    private sealed class NoGameBridge : IGameBridge
    {
        public string Description => "no game";

        public bool IsAttached => false;

        public bool CanWriteRemotes => false;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = default;
            return false;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public List<EnemyState> ReadEnemies(IReadOnlyList<Vector3f> otherPlayers) => [];

        public void Dispose()
        {
        }
    }

    [Theory]
    [InlineData("0x131", 0x131u)]
    [InlineData("#131", 0x131u)]
    [InlineData("#0x131", 0x131u)]
    [InlineData("0X1F", 0x1Fu)]
    public void AnItemIdIsHexAndSaysSo(string token, uint expected)
    {
        Assert.True(ConsoleCommand.TryParseItemId(token, out uint objectId));
        Assert.Equal(expected, objectId);
    }

    /// <summary>
    /// ⚠ A bare number is NOT an item id, on purpose.
    ///
    /// Every id these verbs deal in is copied out of a line that printed it as 0x131, and
    /// reading a bare "131" as decimal would aim at object 0x83 — a different object that
    /// will usually resolve, so the mistake would be silent. A verb whose job is to write
    /// into an object must not have a spelling that quietly picks the wrong one.
    /// </summary>
    [Theory]
    [InlineData("131")]
    [InlineData("")]
    [InlineData("#")]
    [InlineData("0x")]
    [InlineData("g_w_blstrpstl001")]
    [InlineData("#0")]
    public void ABareNumberIsNotAnItemId(string token)
    {
        Assert.False(ConsoleCommand.TryParseItemId(token, out _));
    }

    /// <summary>
    /// Every refusal names its reason. This is the property the whole harness rests on: an
    /// unattended run can only trust an answer that says what it saw, and a drop that
    /// silently does nothing is indistinguishable from a drop that was never asked for.
    /// </summary>
    [Fact]
    public void ADropWithNoSessionRefusesOutLoud()
    {
        NoGameBridge bridge = new();
        using SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });

        ItemDropRequest.Outcome outcome = ItemDropRequest.Drop(manager, bridge, "g_w_blstrpstl001");

        Assert.False(outcome.Dropped);
        Assert.Contains("not connected", outcome.Note, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// ⚠ ONE NUMBER IN TWO FILES, and a half-bump presents as "instance 1 failed to launch"
    /// — the agent simply never finds the block, which reads as a broken install rather than
    /// a version skew. The version is embedded in the byte pattern the agent scans for, so
    /// nothing at runtime can compare the two; this is the only place they can be checked
    /// against each other at all.
    /// </summary>
    [Fact]
    public void TheBlockVersionIsTheSameNumberInBothHalves()
    {
        string root = RepositoryRoot();

        // Bytes 8..11 of the signature are the version as a little-endian dword.
        string[] signatureBytes = KotorGameBridge.PluginSignature.Split(' ');
        Assert.Equal(16, signatureBytes.Length);
        int agentVersion = Convert.ToInt32(signatureBytes[8], 16);

        string pluginHost = File.ReadAllText(Path.Combine(root, "src", "KotorCoop.Plugin", "PluginHost.cs"));
        Match written = Regex.Match(pluginHost, @"_block->Version\s*=\s*(\d+)\s*;");

        Assert.True(written.Success, "PluginHost.cs no longer assigns _block->Version literally");
        Assert.Equal(int.Parse(written.Groups[1].Value), agentVersion);
    }

    /// <summary>Walks up from the test binary to the directory holding the solution file.</summary>
    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KotorCoop.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
