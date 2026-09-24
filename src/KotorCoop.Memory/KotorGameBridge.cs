// Shared state and lifecycle for the KOTOR game bridge: the constructor, the fields every
// concern partial reads (the memory handle, the resolved player/phantoms, the plugin block
// address), the small always-true properties, and the failure/dispose paths. Every other
// concern lives in a KotorGameBridge.<Concern>.cs partial alongside this file.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>A live creature object found in the game.</summary>
public sealed record CreatureRef(nint Address, uint ObjectId, string Tag, bool IsPlayer, bool IsPartyMember)
{
    public override string ToString() =>
        $"0x{Address:X} id=0x{ObjectId:X} {(Tag.Length > 0 ? Tag : "(untagged)")}"
        + (IsPlayer ? " [player]" : IsPartyMember ? " [party]" : string.Empty);
}

/// <summary>
/// A KOTOR-specific bridge built on the public Ghidra symbol database for
/// swkotor.exe v1.0.3 (LaneDibello's kotor1_0_3.db), whose game_version table sha256 matches
/// this exact Steam binary. Because the image has no ASLR, every constant below is a fixed
/// runtime address.
///
/// This replaces the offset-profile approach for KOTOR, which could not express what is
/// needed here: find every creature, pick the one flagged as the player, and drive a
/// selection of the others. See docs/re/object-model.md.
/// </summary>
public sealed partial class KotorGameBridge : IGameBridge
{
    private readonly ProcessMemory _memory;
    private readonly bool _ownsMemory;
    private readonly int _maxPhantoms;
    private readonly GlobalVariableTable _globals;

    private CreatureRef? _player;
    private List<CreatureRef> _phantoms = [];
    private int _consecutiveFailures;
    private nint _pluginBlock;

    /// <summary>16-byte signature: "KOCOOP01" + version 77 + guard 0xC0FFEE01 (77: the
    /// local_var_sync region 0x5800..0x5A00, KotorGameBridge.LocalVars.cs). The version is
    /// part of the pattern deliberately: an agent must not attach to a plugin whose block
    /// layout it does not know, and a stale pair fails to find each other rather than reading
    /// the wrong offsets. ⚠ Bump this WITH PluginHost's `_block->Version` — they are one
    /// number in two files, and a half-bump reads as "plugin shared block not found", which
    /// looks like a broken install rather than a version skew.
    /// See docs/research/shared-state-audit.md for the per-version history of what each bump
    /// added to the block.
    /// <b>ItemHarnessTests.TheBlockVersionIsTheSameNumberInBothHalves reads both files and
    /// fails the build if they disagree</b> — added with 42, because nothing had ever
    /// enforced the warning above. ⚠ TransitionMovieTests.TheBlockVersionMovedInBothHalves
    /// (an existing test this split does not touch) also reads this constant straight out of
    /// THIS file's text, by its literal path — keep it declared here, not in a partial.</summary>
    public const string PluginSignature = "4B 4F 43 4F 4F 50 30 31 4D 00 00 00 01 EE FF C0";

    /// <summary>
    /// True when the in-process plugin is present. Changes the strategy completely: instead
    /// of poking existing creatures' positions from outside, the agent hands remote players
    /// to the plugin, which spawns real creatures and walks them through the engine.
    /// </summary>
    public bool PluginAttached => _pluginBlock != 0;

    public nint PluginBlockAddress => _pluginBlock;

    public KotorGameBridge(
        ProcessMemory memory, int maxPhantoms = 4, bool ownsMemory = true, int enemyCensusCap = 0)
    {
        _memory = memory;
        _maxPhantoms = maxPhantoms;
        _ownsMemory = ownsMemory;
        _globals = new GlobalVariableTable(memory);

        // DEBUG ONLY (docs/research/creature-liveness.md item 1, AgentOptions.EnemyCensusCap's
        // own doc comment): 0/out-of-range keeps the built-in BlockEnemyCapacity untouched —
        // this can only ever LOWER the cap, never raise it past what the fixed-size SharedBlock
        // region actually holds.
        _enemyCensusCap = enemyCensusCap is > 0 and <= BlockEnemyCapacity
            ? enemyCensusCap
            : BlockEnemyCapacity;
    }

    /// <summary>See the constructor's <c>enemyCensusCap</c> parameter. Read by
    /// <see cref="ReadEnemies"/> in place of <see cref="BlockEnemyCapacity"/> when set below the
    /// built-in 32, so a live test can push an ordinary module's population past the count the
    /// combat census actually carries without needing real content that places 33+ creatures.
    /// <see cref="TryWriteEnemies"/>'s own wire write stays bounded by
    /// <see cref="BlockEnemyCapacity"/> regardless — the SharedBlock region has exactly 32
    /// physical slots, and this can only ever select FEWER of them, never more.</summary>
    private readonly int _enemyCensusCap;

    public string Description =>
        $"KOTOR [pid {_memory.ProcessId}] player={(_player is null ? "?" : $"0x{_player.Address:X}")} "
        + $"phantoms={_phantoms.Count}";

    public bool IsAttached => _memory.IsOpen && _player is not null;

    /// <summary>True once at least one drivable creature has been claimed.</summary>
    public bool CanWriteRemotes => _pluginBlock != 0 || _phantoms.Count > 0;

    public CreatureRef? Player => _player;

    public IReadOnlyList<CreatureRef> Phantoms => _phantoms;

    /// <summary>
    /// True for "area1"/"area1F"-style names: what the module reads as while a transition is
    /// in flight. Not a module on disk — an object id wearing a module's clothes — and every
    /// pointer the scan holds while one is showing belongs to a world being freed.
    ///
    /// Shared by two concerns, which is why it lives here rather than in either partial:
    /// <see cref="DisableLocalEncounters"/> (KotorGameBridge.Encounters.cs) refuses to write
    /// into a transitional world, and <see cref="TryReadLocalState"/> (KotorGameBridge.Player.cs)
    /// treats a transitional-named read as a failed read rather than adopting it as the player.
    /// The latter was missing until 2026-08-27 (live-verified FAILED first —
    /// docs/research/disconnect-reconnect.md §10): a genuine "quit to the main menu" tears the
    /// player object down, the cached pointer correctly stops validating, but the very next
    /// tick's scan-based re-resolve (<see cref="TryResolve"/>) reacquires a transitional-looking
    /// ghost — module <c>'area1'</c>, plausible hit points — that passes every cheap validation
    /// <see cref="StillLooksLikeThePlayer"/> applies, so the read "succeeds" against a ghost and
    /// <see cref="KotorCoop.Session.AwayRule"/>'s consecutive-failure counter never advances.
    /// </summary>
    internal static bool IsTransitionalModuleName(string module)
    {
        if (module.Length <= 4 || !module.StartsWith("area", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int i = 4; i < module.Length; i++)
        {
            if (!Uri.IsHexDigit(module[i]))
            {
                return false;
            }
        }

        return true;
    }

    private void OnFailure()
    {
        _consecutiveFailures++;

        // A module transition rebuilds every object, so re-scan rather than read garbage.
        if (_consecutiveFailures >= 10)
        {
            _player = null;
            _phantoms = [];
            _consecutiveFailures = 0;
        }
    }

    public void Dispose()
    {
        if (_ownsMemory)
        {
            _memory.Dispose();
        }
    }
}
