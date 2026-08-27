namespace KotorCoop.Plugin;

// The `setglobal` staging cheat: writes a script global by name through the engine's own
// type-checked setter (Odyssey.TryWriteGlobal). See docs/research/quest-sync.md's 2026-08-27
// addendum.
internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// Writes global <see cref="SharedBlock.CheatText"/> to <see cref="SharedBlock.CheatAmount"/>
    /// (signed), through <see cref="Odyssey.TryWriteGlobal"/>. <see cref="SharedBlock.CheatItemFlags"/>
    /// 1 selects the boolean setter, anything else the number setter — reused field, no new
    /// layout.
    /// </summary>
    private void SetGlobal()
    {
        string text = ReadCheatText();
        if (text.Length == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, "setglobal needs a global name");
            return;
        }

        byte* name = block.CheatText;
        int length = 0;
        while (length < SharedBlock.CheatTextLength - 1 && name[length] != 0)
        {
            length++;
        }

        bool asBoolean = block.CheatItemFlags == 1;
        int value = block.CheatAmount;

        if (!Odyssey.TryWriteGlobal(name, length, asBoolean, value, out int before, out int after, out string reason))
        {
            Answer(SharedBlock.CheatStatusRefused, $"setglobal '{text}': {reason}");
            return;
        }

        string kind = asBoolean ? "boolean" : "number";
        PluginHost.Log($"setglobal: '{text}' ({kind}) {before} -> {after} (requested {value})");

        // The getter zeroes its out-param on ANY refusal (undeclared name, or declared as the
        // other type) — see Odyssey.TryWriteGlobal's own remarks — so "after == requested" is
        // the strongest signal available from in-process, not a guarantee: a global that was
        // ALREADY at the requested value reads identically to one that just got refused there.
        bool looksApplied = asBoolean ? (after != 0) == (value != 0) : after == value;

        Answer(
            looksApplied ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            looksApplied
                ? $"'{text}' ({kind}) {before} -> {after}"
                : $"'{text}' ({kind}) still reads {after} after requesting {value} - name may be "
                    + $"undeclared or declared as the other type (engine refused silently)");
    }
}
