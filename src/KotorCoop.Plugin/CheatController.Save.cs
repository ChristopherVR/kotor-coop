namespace KotorCoop.Plugin;

// The `save` verb: ask the game to write a save through its own quicksave path.
internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// Saves the game the way the retail quicksave key does —
    /// <see cref="Odyssey.RequestQuickSave"/>.
    ///
    /// WHY A SAVE VERB EXISTS. <c>join_mode = newcharacter</c> needs the character a first-time
    /// joiner has just created, and the only portable form KOTOR has for a character is the one
    /// world adoption already reads out of a save directory
    /// (<c>&lt;module&gt;.sav → module.ifo → Mod_PlayerList</c>). Retail chargen does not write
    /// one — docs/research/world-adoption.md's 2026-08-29 correction: the chargen writer emits
    /// <c>Temp\temp.bic</c>, not <c>TEMP:pifo</c> — so the new character has to be saved once,
    /// in-world, before anything can carry it. Independently, several rig rounds have wanted a
    /// way to produce a genuinely fresh save on demand (docs/research/session-save.md's rounds
    /// staged saves by copying directories about, for want of exactly this).
    ///
    /// ⚠ "APPLIED" MEANS REQUESTED, NOT WRITTEN. <c>DoQuickSave</c> sends a client → server
    /// message; the directory appears a tick or more later, and the server may still refuse it
    /// (<c>CSWSPlayer::GetIsAllowedToSave</c>). Anything that depends on the save existing must
    /// poll the filesystem — which is what the new-character capture does — rather than trust
    /// this answer.
    /// </summary>
    private void QuickSave()
    {
        if (!Odyssey.RequestQuickSave())
        {
            Answer(SharedBlock.CheatStatusNoTarget, "no world loaded here to save");
            return;
        }

        PluginHost.Log("save: quicksave requested through the engine's own DoQuickSave");
        Answer(
            SharedBlock.CheatStatusApplied,
            "quicksave requested (the engine writes it a tick or two later; check saves\\)");
    }
}
