using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// The game-thread half of <c>local_var_sync</c> (docs/research/quest-sync.md §8.3): logs what
/// <see cref="LocalVarSyncHook"/> captured, and applies the session's values for this module's
/// allowlisted slots to the peer's copy of each object.
///
/// Apply rules, each one a refusal rather than a guess:
/// <list type="bullet">
/// <item>only while the session drives the world (<c>CommandSpawnAndDrive</c>), a player exists,
/// and no move-to-module is pending — never during teardown or a load;</item>
/// <item>only a list the agent built for THIS world generation (the agent echoes
/// <see cref="SharedBlockView.LocalVarGeneration"/>; <see cref="Invalidate"/> advances it on
/// every world edge, so a list from the previous module is inert until rebuilt);</item>
/// <item>only an entry whose module hash is the live module's, whose (kind, slot) is watched,
/// whose id resolves to an ordinary object (not an area/module) carrying the watched TAG;</item>
/// <item>never over a local write the agent has not drained yet (the list carries the capture
/// cursor it had drained; a newer local capture for the same watch wins until it catches up);</item>
/// <item>only when the live value differs — read through the engine's own getter first — and
/// then through the engine's own setter (<see cref="Odyssey.WriteLocalVar"/>), never a raw poke.</item>
/// </list>
/// </summary>
internal sealed unsafe class LocalVarController(SharedBlockView block)
{
    public static readonly bool Enabled = PluginConfig.ReadBool("local_var_sync", true);

    private const int IntervalFrames = 15;
    private const int MaxLogged = 60;

    private readonly uint[] _moduleHashes = new uint[SharedBlock.LocalVarApplyCapacity];
    private readonly uint[] _entryWords = new uint[SharedBlock.LocalVarApplyCapacity];

    /// <summary>Refusals already logged this world (key + value), so a standing refusal is one
    /// line, not one every pass.</summary>
    private readonly HashSet<ulong> _refusalsLogged = [];

    private int _countdown;
    private int _logged;
    private uint _loggedCursor;

    /// <summary>A world edge: every id now names a different object. Advancing the generation
    /// makes the agent's current list (and any capture still stamped with the old one) inert.</summary>
    public void Invalidate()
    {
        uint generation = block.AdvanceLocalVarGeneration();
        _refusalsLogged.Clear();
        if (Enabled)
        {
            LogFew($"localvar: world generation -> {generation}");
        }
    }

    public void OnFrame()
    {
        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;
        LogCaptures();

        if (!Enabled || block.Command != SharedBlock.CommandSpawnAndDrive
            || !Odyssey.HasPlayer || Odyssey.IsMoveToModulePending())
        {
            return;
        }

        if (!block.TryReadLocalVarApplyList(_moduleHashes, _entryWords, out int count, out uint generation, out uint drained)
            || count == 0 || generation != block.LocalVarGeneration)
        {
            return;
        }

        byte* module = stackalloc byte[32];
        int moduleLength = Odyssey.ReadCurrentModuleName(module, 32);
        if (moduleLength <= 0)
        {
            return;
        }

        uint liveModule = LocalVarSyncRule.HashModuleAscii(new ReadOnlySpan<byte>(module, moduleLength));
        byte* tag = stackalloc byte[LocalVarWatchTable.TagCapacity];

        for (int i = 0; i < count; i++)
        {
            if (_moduleHashes[i] != liveModule)
            {
                continue;
            }

            LocalVarSyncRule.UnpackEntry(_entryWords[i], out ushort objectId, out LocalVarKind kind, out byte slot, out sbyte value);
            if (!LocalVarSyncRule.IsGitObjectId(objectId) || !LocalVarSyncRule.IsValidSlot(kind, slot))
            {
                continue;
            }

            if (!Odyssey.TryResolveLocalVarTable(objectId, out nint obj, out nint table, out string why))
            {
                LogRefusal(_moduleHashes[i], _entryWords[i], $"localvar: apply 0x{objectId:X} {Describe(kind, slot)} = {value} refused - {why}");
                continue;
            }

            Odyssey.ReadObjectTag(obj, tag, LocalVarWatchTable.TagCapacity);
            int tagLength = 0;
            while (tagLength < LocalVarWatchTable.TagCapacity && tag[tagLength] != 0)
            {
                tagLength++;
            }

            int watch = LocalVarWatchTable.Find(liveModule, tag, tagLength, kind, slot);
            if (watch < 0)
            {
                // Either no watch for this slot here, or the id names an object with another
                // tag on this engine — both mean "not the object the peer wrote".
                LogRefusal(_moduleHashes[i], _entryWords[i], $"localvar: apply 0x{objectId:X} {Describe(kind, slot)} = {value} refused - "
                    + $"'{new string((sbyte*)tag, 0, tagLength)}' is not watched for that slot here");
                continue;
            }

            if (LocalVarWatchTable.LastCaptureOf(watch) > drained)
            {
                // Our own engine wrote this slot after the list was built; the agent has not
                // published that yet. Let the newer local value win until the list catches up.
                continue;
            }

            sbyte live = Odyssey.ReadLocalVar(table, kind, slot);
            if (live == value)
            {
                continue;
            }

            Odyssey.WriteLocalVar(table, kind, slot, value);
            sbyte after = Odyssey.ReadLocalVar(table, kind, slot);
            ushort expected = LocalVarWatchTable.ExpectedIdOf(watch);
            LogFew($"localvar: applied '{LocalVarWatchTable.TagOf(watch)}' 0x{objectId:X} {Describe(kind, slot)} "
                + $"{live} -> {after} (session value {value})"
                + (expected != 0 && expected != objectId ? $" [audit predicted id 0x{expected:X}]" : string.Empty));
        }
    }

    /// <summary>Logs each new capture the hook published (budgeted), so a live pass can see the
    /// capture half without the agent.</summary>
    private void LogCaptures()
    {
        uint cursor = block.LocalVarCaptureCursor;
        if (cursor == _loggedCursor)
        {
            return;
        }

        uint first = cursor - _loggedCursor > SharedBlock.LocalVarCaptureCapacity
            ? cursor - SharedBlock.LocalVarCaptureCapacity
            : _loggedCursor;
        for (uint n = first + 1; n <= cursor; n++)
        {
            if (block.TryReadLocalVarCapture(n, out uint moduleHash, out uint word, out uint generation))
            {
                LocalVarSyncRule.UnpackEntry(word, out ushort objectId, out LocalVarKind kind, out byte slot, out sbyte value);
                LogFew($"localvar: captured #{n} module 0x{moduleHash:X8} object 0x{objectId:X} "
                    + $"{Describe(kind, slot)} = {value} (generation {generation})");
            }
        }

        _loggedCursor = cursor;
    }

    private void LogRefusal(uint moduleHash, uint entryWord, string message)
    {
        if (_refusalsLogged.Add(((ulong)moduleHash << 32) | entryWord))
        {
            LogFew(message);
        }
    }

    public static string Describe(LocalVarKind kind, int slot) => (kind == LocalVarKind.Boolean ? "b" : "n") + slot;

    private void LogFew(string message)
    {
        if (_logged < MaxLogged)
        {
            _logged++;
            PluginHost.Log(message);
        }
    }
}
