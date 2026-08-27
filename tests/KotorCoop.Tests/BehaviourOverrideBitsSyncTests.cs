using System.Text.RegularExpressions;
using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The two halves of this mod cannot share a type — the plugin is NativeAOT win-x86 and the
/// agent is x64 — so the effective-behaviour-flags bit table is written twice by hand, in
/// <c>SharedBlock.OverrideBit*</c> (src/KotorCoop.Plugin/SharedBlock.BehaviourOverrides.cs)
/// and <c>KotorCoop.Memory.BehaviourOverrideBits</c>. Both headers say KEEP IN SYNC, and a
/// mismatch does not fail loudly: the agent would tell the plugin "the host overrode
/// map_reveal_sync" using the bit the plugin reads as trigger_relay, and the wrong channel
/// would silently adopt the host's value while the one actually named stays on local
/// coop.ini. Reading the sources is unusual for a unit test, but the alternative is a third
/// copy of the table in the test — one more thing to drift. Follows
/// <c>CheatKindSyncTests.cs</c>'s exact discipline for the same reason.
/// </summary>
public class BehaviourOverrideBitsSyncTests
{
    [Fact]
    public void TheTwoHalvesAgreeOnEveryOverrideBit()
    {
        string root = RepositoryRoot();

        Dictionary<string, uint> plugin = ReadConstants(
            Path.Combine(root, "src", "KotorCoop.Plugin", "SharedBlock.BehaviourOverrides.cs"),
            @"public const uint OverrideBit(\w+)\s*=\s*(\d+);");

        Dictionary<string, uint> agent = ReadConstants(
            Path.Combine(root, "src", "KotorCoop.Memory", "BehaviourOverrideBits.cs"),
            @"public const uint (\w+)\s*=\s*(\d+);");

        Assert.NotEmpty(plugin);
        Assert.Equal(5, plugin.Count);

        foreach ((string name, uint value) in plugin)
        {
            Assert.True(
                agent.TryGetValue(name, out uint mirrored),
                $"the plugin has override bit {name} = {value} and BehaviourOverrideBits does not");

            Assert.True(
                mirrored == value,
                $"override bit {name} is {value} in the plugin and {mirrored} in the agent - "
                + "an adopted override would land on the wrong channel");
        }
    }

    /// <summary>A bit used twice is a real collision: setting one key's override bit would
    /// also flip another key's, which reads as "the host overrode a channel it never
    /// mentioned".</summary>
    [Fact]
    public void NoTwoOverrideBitsShareAValue()
    {
        Dictionary<string, uint> plugin = ReadConstants(
            Path.Combine(RepositoryRoot(), "src", "KotorCoop.Plugin", "SharedBlock.BehaviourOverrides.cs"),
            @"public const uint OverrideBit(\w+)\s*=\s*(\d+);");

        List<IGrouping<uint, string>> clashes =
        [
            .. plugin.GroupBy(entry => entry.Value, entry => entry.Key).Where(group => group.Count() > 1),
        ];

        Assert.True(
            clashes.Count == 0,
            "two override bits share a value: "
            + string.Join("; ", clashes.Select(c => $"{c.Key} = {string.Join(" and ", c)}")));
    }

    /// <summary>Every bit fits in the paired mask/values dwords, and specifically stays below
    /// bit 5 — the five keys named at the top of this file — so a sixth key added later is
    /// caught here (a new bit must join this list, not silently work by accident) rather than
    /// silently overlapping a future flag.</summary>
    [Fact]
    public void EveryOverrideBitIsAPowerOfTwoBelowBitFive()
    {
        Dictionary<string, uint> plugin = ReadConstants(
            Path.Combine(RepositoryRoot(), "src", "KotorCoop.Plugin", "SharedBlock.BehaviourOverrides.cs"),
            @"public const uint OverrideBit(\w+)\s*=\s*(\d+);");

        foreach ((string name, uint value) in plugin)
        {
            Assert.True(value is > 0 and < 32, $"{name} = {value} is out of the expected 5-bit range");
            Assert.True((value & (value - 1)) == 0, $"{name} = {value} is not a single bit");
        }
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
