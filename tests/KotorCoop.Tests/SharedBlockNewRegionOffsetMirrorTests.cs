using System.Text.RegularExpressions;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The five SharedBlock regions this round added (companion equipment, behaviour overrides,
/// solo-segment spectate, container-lock arbitration, item-template-spawn ring — versions
/// 54-58) are each declared twice, by hand: once as <c>SharedBlock.Offset*</c>
/// (src/KotorCoop.Plugin, NativeAOT win-x86) and once as <c>KotorGameBridge.Block*</c>
/// (src/KotorCoop.Memory, x64) — the two halves cannot share a type across that boundary, the
/// same reason <c>CheatKindSyncTests</c>/<c>BehaviourOverrideBitsSyncTests</c> exist. A drifted
/// offset does not fail loudly: the agent reads or writes the wrong bytes and the symptom is a
/// field that never updates, or a "plugin not found" if it happens to land outside the block.
///
/// No such cross-check test existed for any SharedBlock region before this one (checked: no
/// test file references KotorGameBridge.PluginBlock.cs). This one is scoped to the five new
/// regions rather than a repo-wide scan of every Offset*/Block* pair, because a repo-wide name
/// match has a real false-positive: <c>SharedBlock.OffsetPeerEffectSerial</c> (a region base)
/// and <c>SharedBlock.PeerEffectSerialOffset</c> (an intra-slot field offset) both reduce to
/// the key "PeerEffectSerial" once a bare "Offset" prefix/suffix is stripped, and only the
/// agent's <c>BlockPeerEffectSerial</c> (which mirrors the latter) exists — a naive scan reads
/// that as a mismatch (0x3A00 vs 0x14) when both sides are actually correct.
/// </summary>
public class SharedBlockNewRegionOffsetMirrorTests
{
    private static readonly (string Name, string PluginFile, string AgentFile)[] Regions =
    [
        ("CompanionEquipSlots", "SharedBlock.Party.cs", "KotorGameBridge.Party.cs"),
        ("BehaviourOverrideMask", "SharedBlock.BehaviourOverrides.cs", "KotorGameBridge.PluginBlock.cs"),
        ("BehaviourOverrideValues", "SharedBlock.BehaviourOverrides.cs", "KotorGameBridge.PluginBlock.cs"),
        ("LocalInSoloSegment", "SharedBlock.SoloSegment.cs", "KotorGameBridge.SoloSegment.cs"),
        ("LocalContainerLockRequestTargetId", "SharedBlock.Dialogue.cs", "KotorGameBridge.PluginBlock.cs"),
        ("LocalContainerLockRequestSerial", "SharedBlock.Dialogue.cs", "KotorGameBridge.PluginBlock.cs"),
        ("ContainerLockResponseSerial", "SharedBlock.Dialogue.cs", "KotorGameBridge.PluginBlock.cs"),
        ("ContainerLockResponseGranted", "SharedBlock.Dialogue.cs", "KotorGameBridge.PluginBlock.cs"),
        ("ItemTemplateSpawnRing", "SharedBlock.ItemTemplateSpawns.cs", "KotorGameBridge.PluginBlock.cs"),
        ("ItemTemplateSpawnGenerations", "SharedBlock.ItemTemplateSpawns.cs", "KotorGameBridge.PluginBlock.cs"),
        ("ItemTemplateSpawnGeneration", "SharedBlock.ItemTemplateSpawns.cs", "KotorGameBridge.PluginBlock.cs"),
        ("ItemTemplateSpawnTickets", "SharedBlock.ItemTemplateSpawns.cs", "KotorGameBridge.PluginBlock.cs"),
        ("ItemTemplateSpawnWorldSerials", "SharedBlock.ItemTemplateSpawns.cs", "KotorGameBridge.PluginBlock.cs"),

        // The new-character (coop.ini join_mode = newcharacter) capture handshake. Its agent
        // half lives in PluginMenuLoader rather than KotorGameBridge for the same reason the
        // menu-load offsets next to it do: the bridge anchors on the player creature, which
        // does not exist at the main menu, and this region's whole life is spent there.
        // OffsetJoinChargenApplied is deliberately absent — the agent never reads the echo, so
        // there is no second copy of it to drift.
        ("JoinChargenRequest", "SharedBlock.JoinChargen.cs", "PluginMenuLoader.cs"),
        ("JoinChargenState", "SharedBlock.JoinChargen.cs", "PluginMenuLoader.cs"),
        ("JoinChargenBytes", "SharedBlock.JoinChargen.cs", "PluginMenuLoader.cs"),
        ("JoinChargenSaveDir", "SharedBlock.JoinChargen.cs", "PluginMenuLoader.cs"),

        ("MinigameLockActive", "SharedBlock.MinigameLock.cs", "KotorGameBridge.MinigameLock.cs"),
        ("MinigameLockName", "SharedBlock.MinigameLock.cs", "KotorGameBridge.MinigameLock.cs"),

        // The live executable-image digest, which is what a Steam↔DRM-free session now passes
        // its compatibility handshake on (docs/research/steam-support.md §8). A drifted offset
        // here reads as "the plugin published nothing", which silently turns the one FATAL
        // compatibility check into a warning — exactly the failure mode this test exists for.
        ("ExeImageDigest", "SharedBlock.ExeImage.cs", "KotorGameBridge.PluginBlock.cs"),
        ("ExeImageDigestKnown", "SharedBlock.ExeImage.cs", "KotorGameBridge.PluginBlock.cs"),
        ("LootMailboxModuleHash", "SharedBlock.Items.cs", "KotorGameBridge.ContainerLoot.cs"),
        ("LootMailboxAckSerial", "SharedBlock.Items.cs", "KotorGameBridge.ContainerLoot.cs"),
        ("LootMailboxPageSerial", "SharedBlock.Items.cs", "KotorGameBridge.ContainerLoot.cs"),
        ("LootMailboxCount", "SharedBlock.Items.cs", "KotorGameBridge.ContainerLoot.cs"),
        ("LootMailboxSlots", "SharedBlock.Items.cs", "KotorGameBridge.ContainerLoot.cs"),
        ("LootMailboxConsumedCount", "SharedBlock.Items.cs", "KotorGameBridge.ContainerLoot.cs"),
        ("LootMailboxEpoch", "SharedBlock.Items.cs", "KotorGameBridge.ContainerLoot.cs"),
        ("LootMailboxAckEpoch", "SharedBlock.Items.cs", "KotorGameBridge.ContainerLoot.cs"),

        // Version 76, exact enemy-health baseline persistence: a drift here would read the
        // plugin's latch export as garbage and silently leave every saved world refused.
        ("EnemyBaselineExportSequence", "SharedBlock.EnemyScalingBaseline.cs", "KotorGameBridge.EnemyScalingBaseline.cs"),
        ("EnemyBaselineExportAck", "SharedBlock.EnemyScalingBaseline.cs", "KotorGameBridge.EnemyScalingBaseline.cs"),
        ("EnemyBaselineImportSequence", "SharedBlock.EnemyScalingBaseline.cs", "KotorGameBridge.EnemyScalingBaseline.cs"),
        ("EnemyBaselineImportAck", "SharedBlock.EnemyScalingBaseline.cs", "KotorGameBridge.EnemyScalingBaseline.cs"),
        ("EnemyBaselineSaveSequence", "SharedBlock.EnemyScalingBaseline.cs", "KotorGameBridge.EnemyScalingBaseline.cs"),
        // local_var_sync (version 77, docs/research/quest-sync.md §8.3).
        ("LocalVarCaptureCursor", "SharedBlock.LocalVars.cs", "KotorGameBridge.LocalVars.cs"),
        ("LocalVarStatus", "SharedBlock.LocalVars.cs", "KotorGameBridge.LocalVars.cs"),
        ("LocalVarGeneration", "SharedBlock.LocalVars.cs", "KotorGameBridge.LocalVars.cs"),
        ("LocalVarCaptureRing", "SharedBlock.LocalVars.cs", "KotorGameBridge.LocalVars.cs"),
        ("LocalVarApplySerial", "SharedBlock.LocalVars.cs", "KotorGameBridge.LocalVars.cs"),
        ("LocalVarApplyCount", "SharedBlock.LocalVars.cs", "KotorGameBridge.LocalVars.cs"),
        ("LocalVarApplyGeneration", "SharedBlock.LocalVars.cs", "KotorGameBridge.LocalVars.cs"),
        ("LocalVarApplyDrainedCursor", "SharedBlock.LocalVars.cs", "KotorGameBridge.LocalVars.cs"),
        ("LocalVarApplyEntries", "SharedBlock.LocalVars.cs", "KotorGameBridge.LocalVars.cs"),
        // Party-placed floor-mine replication (version 78, docs/research/mines.md §14). A
        // drifted offset here would feed the plugin a spawn request for the wrong serial, or read
        // a capture's record out of the neighbouring slot.
        ("PlacedMineCaptureHead", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineCaptureRing", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineGoneHead", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineGoneRing", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineRosterGeneration", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineRosterWorldSerial", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineRosterHighest", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineRosterCount", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineRosterSerials", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineSpawnRequest", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineSpawnWorldSerial", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineSpawnSerial", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineSpawnCapture", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineSpawnX", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineSpawnY", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineSpawnZ", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineSpawnDetectDc", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineSpawnDisarmDc", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineSpawnTrapType", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineSpawnFaction", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineSpawnApplied", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineSpawnStatus", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineSpawnLocalId", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),
        ("PlacedMineWorld", "SharedBlock.PlacedMines.cs", "KotorGameBridge.PlacedMines.cs"),

        // Version 80, "no cap really" (docs/research/creature-liveness.md): the five
        // capacity-scaled enemy tables relocated into a new 0x6000 region so EnemyCapacity
        // could widen from 32 to 128. Their agent-side mirrors live in
        // KotorGameBridge.Enemies.cs (BlockMaterialisedOwnerIds) and
        // KotorGameBridge.PluginBlock.cs (the rest) rather than a single dedicated file, the
        // same split the pre-existing enemy region already used.
        ("EnemySlots", "SharedBlock.Combat.cs", "KotorGameBridge.PluginBlock.cs"),
        ("EnemyPresenceSlots", "SharedBlock.Combat.cs", "KotorGameBridge.PluginBlock.cs"),
        ("MaterialisedOwnerIds", "SharedBlock.Combat.cs", "KotorGameBridge.Enemies.cs"),
        ("DamageDealtSlots", "SharedBlock.Combat.cs", "KotorGameBridge.PluginBlock.cs"),
        ("DamageOwedSlots", "SharedBlock.Combat.cs", "KotorGameBridge.PluginBlock.cs"),
    ];

    [Fact]
    public void EveryNewRegionOffsetMatchesItsAgentSideMirror()
    {
        string root = RepositoryRoot();
        List<string> failures = [];

        foreach ((string name, string pluginFile, string agentFile) in Regions)
        {
            string pluginPath = Path.Combine(root, "src", "KotorCoop.Plugin", pluginFile);
            string agentPath = Path.Combine(root, "src", "KotorCoop.Memory", agentFile);

            int pluginValue = ReadOffsetConstant(pluginPath, $"Offset{name}");
            int agentValue = ReadOffsetConstant(agentPath, $"Block{name}");

            if (pluginValue != agentValue)
            {
                failures.Add(
                    $"{name}: SharedBlock.Offset{name} ({pluginFile}) = 0x{pluginValue:X} but "
                    + $"KotorGameBridge.Block{name} ({agentFile}) = 0x{agentValue:X}");
            }
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    private static int ReadOffsetConstant(string path, string constantName)
    {
        Assert.True(File.Exists(path), $"expected to find {path}");

        string text = File.ReadAllText(path);
        Match match = Regex.Match(
            text, $@"public const int {Regex.Escape(constantName)}\s*=\s*(0x[0-9A-Fa-f]+);");

        if (!match.Success)
        {
            match = Regex.Match(
                text, $@"private const int {Regex.Escape(constantName)}\s*=\s*(0x[0-9A-Fa-f]+);");
        }

        Assert.True(match.Success, $"no declaration of {constantName} found in {path}");
        return Convert.ToInt32(match.Groups[1].Value, 16);
    }

    /// <summary>Walks up from the test binary to the directory holding the solution file.
    /// Fails with the path it reached rather than a null reference, so a moved test project
    /// reads as a moved test project.</summary>
    private static string RepositoryRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("KOTOR_COOP_ROOT");
        if (configured is not null && File.Exists(Path.Combine(configured, "KotorCoop.slnx")))
            return configured;

        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KotorCoop.slnx")))
        {
            directory = directory.Parent;
        }

        // Focused validation deliberately builds into an ignored directory outside the
        // repository, so the binary path may not have the source tree as an ancestor.
        if (directory is null)
        {
            directory = new(Environment.CurrentDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KotorCoop.slnx")))
                directory = directory.Parent;
        }

        Assert.True(directory is not null, $"no KotorCoop.slnx above {AppContext.BaseDirectory}");
        return directory!.FullName;
    }
}
