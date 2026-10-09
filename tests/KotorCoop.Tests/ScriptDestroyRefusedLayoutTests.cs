using System.Text.RegularExpressions;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>The refused-script-destroy ring (SharedBlock.ScriptDestroy.cs): written by the
/// plugin's guard, read by the agent's local-only check, so both sides must name the same bytes.</summary>
public sealed class ScriptDestroyRefusedLayoutTests
{
    private static string Source(string relative)
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "KotorCoop.slnx"))) root = Directory.GetParent(root)!.FullName;
        return File.ReadAllText(Path.Combine(root, relative));
    }

    private static int Constant(string source, string name)
    {
        string value = Regex.Match(source, $@"\b{name} = (0x[0-9A-Fa-f]+|\d+);").Groups[1].Value;
        return value.StartsWith("0x", StringComparison.Ordinal) ? Convert.ToInt32(value, 16) : int.Parse(value);
    }

    [Fact]
    public void PluginAndAgentNameTheSameBytesAfterTheEnemyEffectNamespace()
    {
        string plugin = Source("src/KotorCoop.Plugin/SharedBlock.ScriptDestroy.cs");
        string effects = Source("src/KotorCoop.Plugin/SharedBlock.EnemyEffect.cs");
        string block = Source("src/KotorCoop.Plugin/SharedBlock.cs");
        string bridge = Source("src/KotorCoop.Memory/KotorGameBridge.PluginBlock.cs");

        int count = Constant(plugin, "OffsetScriptDestroyRefusedCount");
        int ids = Constant(plugin, "OffsetScriptDestroyRefusedIds");
        int capacity = Constant(plugin, "ScriptDestroyRefusedCapacity");

        Assert.True(count >= Constant(effects, "OffsetEnemyEffectSourceLocalLoad") + 4);
        Assert.Equal(count + 4, ids);
        Assert.True(ids + capacity * 4 <= Constant(block, "TotalSize"));
        Assert.Equal(count, Constant(bridge, "BlockScriptDestroyRefusedCount"));
        Assert.Equal(ids, Constant(bridge, "BlockScriptDestroyRefusedIds"));
        Assert.Equal(capacity, Constant(bridge, "BlockScriptDestroyRefusedCapacity"));
    }
}
