namespace KotorCoop.Plugin;

// The `addclass <class>` dev-console verb (docs/research/class-and-power-grants.md's live
// checklist): simulates Dorak's k_pdan_makejedi on the local player through the SAME VM
// handler a script's AddMultiClass reaches (Odyssey.AddMultiClass, NOT flagged as a replay),
// so a rig can exercise AddMultiClassHook's capture, the ClassGrantPacket relay and the peer's
// apply without playing to Dantooine. The class list is read back before and after so the
// verb's own answer says what the engine did.
internal sealed unsafe partial class CheatController
{
    private void AddClass(uint playerId)
    {
        int classId = block.CheatAmount;
        if (!ClassGrantCaptureRule.IsKnownClass(classId))
        {
            Answer(SharedBlock.CheatStatusRefused, $"addclass: {classId} is not a classes.2da row (0-8)");
            return;
        }

        bool readable = Odyssey.TryReadLocalClassList(out int count, out int class0, out int level0, out int class1, out int level1);
        string before = readable ? $"{count}:[{class0}@{level0},{class1}@{level1}]" : "unreadable";

        ClassGrantApplyVerdict verdict = ClassGrantApplyRule.Decide(classId, readable, count, class0, class1);
        if (verdict != ClassGrantApplyVerdict.Apply)
        {
            PluginHost.Log($"addclass: refused class {classId} - {verdict} (classes {before})");
            Answer(SharedBlock.CheatStatusRefused, $"addclass {classId}: {verdict} (classes {before})");
            return;
        }

        bool applied = Odyssey.AddMultiClass(playerId, classId, isReplay: false, out int failure);
        if (!applied)
        {
            PluginHost.Log($"addclass: engine refused class {classId} - failure {failure}");
            Answer(SharedBlock.CheatStatusEngineRefused, $"addclass {classId} failed: failure {failure}");
            return;
        }

        Odyssey.TryReadLocalClassList(out count, out class0, out level0, out class1, out level1);
        string after = $"{count}:[{class0}@{level0},{class1}@{level1}]";
        PluginHost.Log($"addclass: class {classId} added to the local player; classes {before} -> {after}; "
            + $"hook installed={AddMultiClassHook.IsInstalled}, captures={AddMultiClassHook.Sequence}, "
            + $"published grants={block.ClassGrantSerial}");
        Answer(SharedBlock.CheatStatusApplied,
            $"classes {before} -> {after} (hook {(AddMultiClassHook.IsInstalled ? "armed" : "NOT installed")}, "
            + $"captures {AddMultiClassHook.Sequence}, published {block.ClassGrantSerial})");
    }
}
