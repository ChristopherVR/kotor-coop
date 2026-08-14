using System.Text.RegularExpressions;
using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The two halves of this mod cannot share a type — the plugin is NativeAOT win-x86 and the
/// agent is x64 — so the cheat-kind table is written twice, by hand, in
/// <c>SharedBlock.CheatKind*</c> and <c>CheatKinds</c>. The header on both says KEEP IN SYNC,
/// and a mismatch does not fail loudly: the plugin simply runs a DIFFERENT VERB from the one
/// that was typed.
///
/// ⚠ THIS TEST EXISTS BECAUSE THE HAZARD IS REAL AND RECENT. Three agents working in parallel
/// each claimed "the next number" for a new verb; two of them landed on 7, and the collision
/// was caught by a human reading a merge and renumbering both files by hand. The next one
/// would not be caught by anything. Reading the sources is unusual for a unit test, but the
/// alternative is a third copy of the table in the test — one more thing to drift.
/// </summary>
public class CheatKindSyncTests
{
    [Fact]
    public void TheTwoHalvesAgreeOnEveryCheatKind()
    {
        string root = RepositoryRoot();

        Dictionary<string, uint> plugin = ReadConstants(
            Path.Combine(root, "src", "KotorCoop.Plugin", "SharedBlock.cs"),
            @"public const uint CheatKind(\w+)\s*=\s*(\d+);");

        Dictionary<string, uint> agent = ReadConstants(
            Path.Combine(root, "src", "KotorCoop.Memory", "IGameBridge.cs"),
            @"public const uint (\w+)\s*=\s*(\d+);");

        Assert.NotEmpty(plugin);

        foreach ((string name, uint value) in plugin)
        {
            Assert.True(
                agent.TryGetValue(name, out uint mirrored),
                $"the plugin has cheat kind {name} = {value} and the agent's CheatKinds does not");

            Assert.True(
                mirrored == value,
                $"cheat kind {name} is {value} in the plugin and {mirrored} in the agent - "
                + "the plugin would run a different verb from the one typed");
        }
    }

    /// <summary>A number used twice is the collision that actually happened; the halves would
    /// still "agree", and one of the two verbs would be unreachable.</summary>
    [Fact]
    public void NoTwoCheatKindsShareANumber()
    {
        Dictionary<string, uint> plugin = ReadConstants(
            Path.Combine(RepositoryRoot(), "src", "KotorCoop.Plugin", "SharedBlock.cs"),
            @"public const uint CheatKind(\w+)\s*=\s*(\d+);");

        List<IGrouping<uint, string>> clashes =
        [
            .. plugin.GroupBy(entry => entry.Value, entry => entry.Key).Where(group => group.Count() > 1),
        ];

        Assert.True(
            clashes.Count == 0,
            "two cheat kinds share a number: "
            + string.Join("; ", clashes.Select(c => $"{c.Key} = {string.Join(" and ", c)}")));
    }

    /// <summary>The agent's own mirror must round-trip too — a stale copy here is what the
    /// dispatcher actually sends.</summary>
    [Fact]
    public void TheAgentsResolveKindIsTheOneTheBlockDeclares()
    {
        Dictionary<string, uint> plugin = ReadConstants(
            Path.Combine(RepositoryRoot(), "src", "KotorCoop.Plugin", "SharedBlock.cs"),
            @"public const uint CheatKind(\w+)\s*=\s*(\d+);");

        Assert.Equal(CheatKinds.Resolve, plugin["Resolve"]);
        Assert.Equal(CheatKinds.Slay, plugin["Slay"]);
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

    /// <summary>Walks up from the test binary to the directory holding the solution file.
    /// Fails with the path it reached rather than a null reference, so a moved test project
    /// reads as a moved test project.</summary>
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
