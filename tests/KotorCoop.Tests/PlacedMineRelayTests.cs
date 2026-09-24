using System.Text.RegularExpressions;
using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The two halves of this mod cannot share a type — the plugin is NativeAOT win-x86 and the
/// agent is x64 — so the placed-mine spawn-mailbox outcome codes are written twice, by hand: the
/// plugin's <c>PlacedMineSpawnStatus</c> enum (src/KotorCoop.Plugin/SharedBlock.PlacedMines.cs)
/// and <c>KotorCoop.Memory.PlacedMineSpawnStatusCodes</c>, whose own header already promises this
/// cross-check. A mismatch does not fail loudly: <see cref="Session.PlacedMineReplicaPlanner"/>
/// would read the plugin's ack as the wrong outcome — e.g. treat a live <c>Retry</c> as
/// <c>Refused</c> and give up on a mine that was only waiting for the area to finish loading, or
/// treat <c>Refused</c> as final-but-different and mis-log the result. Follows
/// <c>CheatKindSyncTests.cs</c>'s exact discipline for the same reason.
/// </summary>
public class PlacedMineRelayTests
{
    [Fact]
    public void TheTwoHalvesAgreeOnEverySpawnStatusCode()
    {
        string root = RepositoryRoot();

        Dictionary<string, uint> plugin = ReadEnumValues(
            Path.Combine(root, "src", "KotorCoop.Plugin", "SharedBlock.PlacedMines.cs"),
            "PlacedMineSpawnStatus");

        Dictionary<string, uint> agent = ReadConstants(
            Path.Combine(root, "src", "KotorCoop.Memory", "IPlacedMineBridge.cs"),
            @"public const uint (\w+)\s*=\s*(\d+);");

        Assert.NotEmpty(plugin);

        // Anchors the extraction itself against the real, compiled agent-side constants, so a
        // regex bug that quietly matched nothing (both dictionaries empty) cannot pass vacuously.
        Assert.Equal(PlacedMineSpawnStatusCodes.Spawned, agent["Spawned"]);
        Assert.Equal(PlacedMineSpawnStatusCodes.Refused, agent["Refused"]);

        foreach ((string name, uint value) in plugin)
        {
            Assert.True(
                agent.TryGetValue(name, out uint mirrored),
                $"the plugin's PlacedMineSpawnStatus has {name} = {value} and PlacedMineSpawnStatusCodes does not");

            Assert.True(
                mirrored == value,
                $"placed-mine spawn status {name} is {value} in the plugin and {mirrored} in the agent - "
                + "the planner would misread the plugin's ack");
        }
    }

    /// <summary>A number used twice on either side would make two outcomes indistinguishable
    /// over the wire (both halves already "agree" on the value, but one name becomes
    /// unreachable) — the same real hazard <c>CheatKindSyncTests</c> guards against.</summary>
    [Fact]
    public void NoTwoStatusCodesShareANumberOnEitherSide()
    {
        string root = RepositoryRoot();

        AssertNoClash(ReadEnumValues(
            Path.Combine(root, "src", "KotorCoop.Plugin", "SharedBlock.PlacedMines.cs"), "PlacedMineSpawnStatus"));
        AssertNoClash(ReadConstants(
            Path.Combine(root, "src", "KotorCoop.Memory", "IPlacedMineBridge.cs"),
            @"public const uint (\w+)\s*=\s*(\d+);"));
    }

    private static void AssertNoClash(Dictionary<string, uint> values)
    {
        List<IGrouping<uint, string>> clashes =
        [
            .. values.GroupBy(entry => entry.Value, entry => entry.Key).Where(group => group.Count() > 1),
        ];

        Assert.True(
            clashes.Count == 0,
            "two placed-mine spawn status codes share a value: "
            + string.Join("; ", clashes.Select(c => $"{c.Key} = {string.Join(" and ", c)}")));
    }

    private static Dictionary<string, uint> ReadConstants(string path, string pattern)
    {
        Assert.True(File.Exists(path), $"expected to find {path}");

        Dictionary<string, uint> found = [];
        foreach (Match match in Regex.Matches(File.ReadAllText(path), pattern))
        {
            found[match.Groups[1].Value] = uint.Parse(match.Groups[2].Value);
        }

        return found;
    }

    /// <summary>Extracts just the body of the named <c>enum ... : uint { ... }</c> block, so a
    /// same-named constant declared elsewhere in the file can never be mistaken for a member.</summary>
    private static Dictionary<string, uint> ReadEnumValues(string path, string enumName)
    {
        Assert.True(File.Exists(path), $"expected to find {path}");
        string text = File.ReadAllText(path);

        Match block = Regex.Match(text, $@"enum {enumName}\s*:\s*uint\s*\{{(?<body>[^}}]*)\}}");
        Assert.True(block.Success, $"expected to find 'enum {enumName} : uint {{ ... }}' in {path}");

        Dictionary<string, uint> found = [];
        foreach (Match member in Regex.Matches(block.Groups["body"].Value, @"(\w+)\s*=\s*(\d+)"))
        {
            found[member.Groups[1].Value] = uint.Parse(member.Groups[2].Value);
        }

        return found;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KotorCoop.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, $"no KotorCoop.slnx above {AppContext.BaseDirectory}");
        return directory!.FullName;
    }
}
