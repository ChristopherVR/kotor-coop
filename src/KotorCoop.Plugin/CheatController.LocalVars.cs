using System.Text;
using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

// The `localvar get|set|watch` live-test verb (docs/research/quest-sync.md §8.3): reads or writes
// one per-object local slot through the engine's own CSWVarTable accessors, and lets a rig add a
// session-only allowlist watch so the whole local_var_sync pipe can be driven on any object.
internal sealed unsafe partial class CheatController
{
    private void LocalVar()
    {
        if (!LocalVarSyncRule.TryParseRequest(ReadCheatText(), out string op, out LocalVarKind kind, out byte slot, out string tagText))
        {
            Answer(SharedBlock.CheatStatusRefused, "localvar request must be get|set|watch, b|n, a valid slot, and a tag or id");
            return;
        }

        uint objectId = block.CheatTarget;
        nint obj;
        string why;
        if (objectId != 0)
        {
            if (!Odyssey.TryResolveLocalVarTable(objectId, out obj, out _, out why))
            {
                Answer(SharedBlock.CheatStatusRefused, $"localvar: {why}");
                return;
            }
        }
        else
        {
            byte[] ascii = Encoding.ASCII.GetBytes(tagText);
            obj = CreatureFinder.FindByTag(ascii);
            if (obj == 0)
            {
                obj = CreatureFinder.FindPlaceableByTag(ascii);
            }

            if (obj == 0)
            {
                obj = CreatureFinder.FindDoorByTag(ascii);
            }

            if (obj == 0)
            {
                Answer(SharedBlock.CheatStatusRefused, $"localvar: no creature, placeable or door tagged '{tagText}' here (triggers: pass the id)");
                return;
            }

            objectId = *(uint*)(obj + 0x04);
            if (!Odyssey.TryResolveLocalVarTable(objectId, out obj, out _, out why))
            {
                Answer(SharedBlock.CheatStatusRefused, $"localvar: '{tagText}' resolved to 0x{objectId:X} but {why}");
                return;
            }
        }

        nint table = obj + Odyssey.ObjLocalVarTable;
        byte* tag = stackalloc byte[LocalVarWatchTable.TagCapacity];
        Odyssey.ReadObjectTag(obj, tag, LocalVarWatchTable.TagCapacity);
        int tagLength = 0;
        while (tagLength < LocalVarWatchTable.TagCapacity && tag[tagLength] != 0)
        {
            tagLength++;
        }

        string liveTag = Encoding.ASCII.GetString(tag, tagLength);
        string name = $"'{liveTag}' 0x{objectId:X} {LocalVarController.Describe(kind, slot)}";

        switch (op)
        {
            case "get":
            {
                sbyte value = Odyssey.ReadLocalVar(table, kind, slot);
                PluginHost.Log($"localvar: get {name} = {value}");
                Answer(SharedBlock.CheatStatusApplied, $"{name} = {value}");
                return;
            }

            case "set":
            {
                sbyte requested = LocalVarSyncRule.NormaliseValue(kind, block.CheatAmount);
                if (kind == LocalVarKind.Number && block.CheatAmount is < sbyte.MinValue or > sbyte.MaxValue)
                {
                    Answer(SharedBlock.CheatStatusRefused, $"localvar: a local number is -128..127 (the engine's own VM range), not {block.CheatAmount}");
                    return;
                }

                sbyte before = Odyssey.ReadLocalVar(table, kind, slot);
                Odyssey.WriteLocalVar(table, kind, slot, requested);
                sbyte after = Odyssey.ReadLocalVar(table, kind, slot);
                bool published = LocalVarController.Enabled
                    && LocalVarSyncHook.PublishConsoleWrite(obj, objectId, kind, slot, after, out _);
                PluginHost.Log($"localvar: set {name} {before} -> {after} (published {(published ? "yes" : "no")})");
                Answer(
                    after == requested ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
                    $"{name} {before} -> {after}; " + (published
                        ? "published to the session"
                        : LocalVarController.Enabled ? "not published (slot not watched here)" : "not published (local_var_sync off)"));
                return;
            }

            case "watch":
            {
                if (!LocalVarController.Enabled)
                {
                    Answer(SharedBlock.CheatStatusRefused, "localvar watch needs local_var_sync = 1 in coop.ini (both players)");
                    return;
                }

                byte* module = stackalloc byte[32];
                int moduleLength = Odyssey.ReadCurrentModuleName(module, 32);
                if (moduleLength <= 0 || tagLength == 0 || !LocalVarSyncRule.IsGitObjectId(objectId))
                {
                    Answer(SharedBlock.CheatStatusRefused, $"localvar: cannot watch {name} (no module, no tag, or not a GIT id)");
                    return;
                }

                uint moduleHash = LocalVarSyncRule.HashModuleAscii(new ReadOnlySpan<byte>(module, moduleLength));
                int index = LocalVarWatchTable.AddRuntimeWatch(moduleHash, tag, tagLength, kind, slot, (ushort)objectId);
                if (index < 0)
                {
                    Answer(SharedBlock.CheatStatusRefused, $"localvar: watch table full ({LocalVarWatchTable.MaxRuntimeWatches} session watches)");
                    return;
                }

                string moduleName = Encoding.ASCII.GetString(module, moduleLength);
                PluginHost.Log($"localvar: watch {name} in {moduleName} (0x{moduleHash:X8}) index {index}"
                    + (LocalVarWatchTable.IsRuntime(index) ? " [session-only TEST watch]" : " [allowlisted]"));
                Answer(SharedBlock.CheatStatusApplied, $"watching {name} in {moduleName} (session only)");
                return;
            }
        }
    }
}
