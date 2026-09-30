namespace KotorCoop.Plugin;

// The `save` verb: ask the game to write a save through its own quicksave path.
internal sealed unsafe partial class CheatController
{
    private void CoopSave()
    {
        int number = block.CheatAmount;
        if (number < 2 || ReadCheatText() != "Co-op" || !Odyssey.RequestCoopSave(number))
        {
            Answer(SharedBlock.CheatStatusEngineRefused, "dedicated Co-op save refused; world or slot not ready");
            return;
        }
        PluginHost.Log($"save: dedicated Co-op save #{number} requested through SendSaveGameRequest");
        Answer(SharedBlock.CheatStatusApplied, "Co-op save requested (wait for the archive to finish)");
    }

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
    /// this answer. With a partner beside this player the request is additionally HELD by
    /// <see cref="SaveFlushHook"/> for up to ~120 frames while the co-op spawns are destroyed
    /// (docs/research/save-ghosts.md §4), so "a tick or two" can be a couple of seconds.
    /// </summary>
    private void QuickSave()
    {
        if (!Odyssey.RequestQuickSave())
        {
            Answer(SharedBlock.CheatStatusNoTarget, "player world is not ready to save yet");
            return;
        }

        PluginHost.Log("save: quicksave requested through the engine's own DoQuickSave");
        Answer(
            SharedBlock.CheatStatusApplied,
            "quicksave requested (the engine writes it a tick or two later; check saves\\)");
    }
}
