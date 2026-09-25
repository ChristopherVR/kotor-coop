namespace KotorCoop.Memory;

/// <summary>
/// The filesystem half of <see cref="CoopSaveSlot"/>: keeps every mod-driven quicksave out of
/// the player's own <c>000000 - QUICKSAVE</c>.
///
/// WHY A HOLD-BACK/RESTORE DANCE RATHER THAN A NEW ENGINE CALL. The engine's own quicksave
/// (<c>CClientExoAppInternal::DoQuickSave 0x5F4B50</c>, <c>Odyssey.RequestQuickSave</c>) is
/// hard-coded to slot 0, name "QUICKSAVE" — there is no parameter that could redirect it. The
/// one native call that DOES take an arbitrary slot number/name
/// (<c>CServerExoAppInternal::SaveGame</c>, already reachable through
/// <c>Odyssey.ReissueServerSaveGame</c>) only RE-ISSUES a request <c>SaveFlushHook</c> already
/// intercepted — it cannot originate one on its own, and piggy-backing this correctness
/// requirement ("the player's quicksave is never touched") onto that machinery would tie it to a
/// feature flag (coop.ini <c>save_flush</c>) built for something unrelated (the ghost-sweep
/// flush) and — by <c>SaveFlushHook</c>'s own header — still unverified live itself. A directory
/// move needs no new native surface, no live verification of a calling convention, and cannot
/// corrupt an archive the engine never touched — the same trade this codebase already made for
/// every other save-shuffling primitive (<c>PluginMenuLoader.TryStageSaveAsAutosaveFromDirectory</c>,
/// <c>CoopCommandDispatcher.CopyQuickSaveAs</c>).
///
/// THE SEQUENCE, split in two because the save is asynchronous (<c>DoQuickSave</c> only SENDS
/// the request; the directory appears a tick or more later — every existing caller already polls
/// for this, unchanged):
/// <list type="number">
/// <item><see cref="BeginHoldback"/>, called right before the request is sent: if
/// <c>000000 - QUICKSAVE</c> exists, it is the PLAYER's own content — this mechanism guarantees
/// the mod never wrote it — so it is moved aside to a scratch directory.</item>
/// <item>The caller sends the request and polls exactly as it already did. The fresh save still
/// lands under the literal <c>000000 - QUICKSAVE</c> name; that is the one thing this cannot
/// change.</item>
/// <item><see cref="CompleteRedirect"/>, called once the caller's own landed/stable check
/// passes (BEFORE any character extraction — every existing capture-source lookup
/// (<c>ClientCharacterPersistence.FindCaptureSource</c>) enumerates every directory under
/// <c>saves\</c> for the newest <c>SAVEGAME.sav</c>, so it finds the co-op slot exactly as
/// readily as it found <c>000000 - QUICKSAVE</c> before — nothing downstream needs to change):
/// moves the fresh save to the dedicated co-op slot (<see cref="CoopSaveSlot"/>) and moves the
/// held-back directory back over <c>000000 - QUICKSAVE</c> — or, when there was nothing to hold
/// back, leaves the slot absent, exactly as it was before the sequence started. Either way
/// <c>000000 - QUICKSAVE</c>'s own mtime ends up exactly where it started (a same-volume
/// directory rename preserves its files' timestamps), which is what the live recipe checks.</item>
/// </list>
///
/// Best-effort and non-throwing throughout, matching every other save primitive in this tree: a
/// failed holdback still lets the save request proceed (worst case, exactly today's behaviour —
/// the co-op capture lands in the quicksave slot) rather than blocking a capture the mod needs.
/// </summary>
public static class CoopSaveRedirect
{
    /// <summary>The engine's one hard-coded quicksave directory name.</summary>
    public const string QuickSaveFolder = "000000 - QUICKSAVE";

    /// <summary>Scratch name for the held-back player quicksave. Dot-prefixed and outside the
    /// <c>NNNNNN - name</c> shape so the retail Load Game screen — which lists every folder
    /// under <c>saves\</c> — never shows a mid-swap holdback as a save, and so it cannot collide
    /// with any name <see cref="CoopSaveSlot"/> or a player's own <c>save &lt;name&gt;</c> would
    /// ever allocate.</summary>
    public const string HoldbackFolder = ".kcoop-quicksave-holdback";

    /// <summary>
    /// Moves the player's own <c>000000 - QUICKSAVE</c> aside before a mod-driven save request
    /// is sent, so the engine's clean-then-write cannot touch it. A no-op (true, "nothing to
    /// hold back") when the slot does not currently exist. Recovers a stale holdback left by a
    /// crash between <see cref="BeginHoldback"/> and <see cref="CompleteRedirect"/> on a PREVIOUS
    /// run by deleting it first — the player's real quicksave from that earlier, uncompleted
    /// sequence is unrecoverable either way (it was already moved into the stale holdback), so
    /// keeping the newer one is the only sane choice, and no worse than what would already have
    /// happened before this mechanism existed.
    /// </summary>
    public static bool BeginHoldback(string savesDirectory, out string diagnostic)
    {
        try
        {
            Directory.CreateDirectory(savesDirectory);
            string quick = Path.Combine(savesDirectory, QuickSaveFolder);
            string holdback = Path.Combine(savesDirectory, HoldbackFolder);

            if (Directory.Exists(holdback))
            {
                Directory.Delete(holdback, recursive: true);
            }

            if (!Directory.Exists(quick))
            {
                diagnostic = "no existing quicksave to hold back";
                return true;
            }

            Directory.Move(quick, holdback);
            diagnostic = "held back the player's quicksave before a co-op save request";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic = $"could not hold back the quicksave slot: {ex.Message} " +
                "(the co-op save may land in the player's quicksave this one time)";
            return false;
        }
    }

    /// <summary>
    /// Relocates a freshly-landed <c>000000 - QUICKSAVE</c> to the dedicated co-op slot and
    /// restores whatever <see cref="BeginHoldback"/> moved aside (if anything). Call this once —
    /// the caller's own landed/stable check must already have passed; this does not repeat it.
    ///
    /// False (and no directories touched beyond a best-effort attempt) when there is no fresh
    /// quicksave to relocate — nothing for the caller to do differently: the pre-existing
    /// content, if any, is left exactly as <see cref="BeginHoldback"/> left it, and a caller that
    /// unconditionally calls this after every request is safe even when the request was refused
    /// by the engine and nothing ever landed.
    /// </summary>
    public static bool CompleteRedirect(string savesDirectory, out string coopSlotDirectory, out string diagnostic)
    {
        coopSlotDirectory = string.Empty;
        string quick = Path.Combine(savesDirectory, QuickSaveFolder);
        string holdback = Path.Combine(savesDirectory, HoldbackFolder);

        try
        {
            if (!Directory.Exists(quick))
            {
                RestoreHoldbackIfAny(holdback, quick);
                diagnostic = "no fresh quicksave to redirect";
                return false;
            }

            string[] existing = Directory.Exists(savesDirectory)
                ? [.. Directory.GetDirectories(savesDirectory).Select(Path.GetFileName)!]
                : [];
            string folder = CoopSaveSlot.ResolveFolderName(existing);
            string target = Path.Combine(savesDirectory, folder);

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.Move(quick, target);
            coopSlotDirectory = target;

            RestoreHoldbackIfAny(holdback, quick);

            diagnostic = $"redirected the fresh save to '{folder}'";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic = $"could not redirect the fresh save to the co-op slot: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Copies (not moves — <paramref name="sourceDirectory"/> keeps its own content, because
    /// world adoption's staging target, <c>000001 - AUTOSAVE</c>, is a mechanical engine
    /// requirement — <c>CSWGuiSaveLoad::LoadPCAutoSave</c> builds its target name from the
    /// literal "AUTOSAVE", and <c>MenuLoadController</c>'s whole no-input-injection load replay
    /// is built around that ONE native code path, per docs/research/world-adoption.md's own "The
    /// save primitive" section) into the dedicated co-op slot, so the adopted world is ALSO a
    /// normal, retail-Load-Game-menu-visible save distinct from the ambient autosave the
    /// engine's own module-transition autosaves keep churning through during ordinary co-op
    /// play. Best-effort and non-fatal: a failed mirror never blocks adoption, which already
    /// succeeded by the time this runs.
    /// </summary>
    public static bool MirrorIntoCoopSlot(
        string savesDirectory, string sourceDirectory, out string coopSlotDirectory, out string diagnostic)
    {
        coopSlotDirectory = string.Empty;
        try
        {
            string[] existing = Directory.Exists(savesDirectory)
                ? [.. Directory.GetDirectories(savesDirectory).Select(Path.GetFileName)!]
                : [];
            string folder = CoopSaveSlot.ResolveFolderName(existing);
            string target = Path.Combine(savesDirectory, folder);

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }

            Directory.CreateDirectory(target);
            foreach (string file in Directory.GetFiles(sourceDirectory))
            {
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
            }

            // Stamp fresh, the same reason TryStageSaveAsAutosaveFromDirectory does: a copy can
            // preserve the source's own timestamp, and this is a newly-activated co-op save.
            string savegame = Path.Combine(target, "SAVEGAME.sav");
            if (File.Exists(savegame))
            {
                File.SetLastWriteTimeUtc(savegame, DateTime.UtcNow);
            }

            coopSlotDirectory = target;
            diagnostic = $"mirrored the adopted world into the co-op slot '{folder}'";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic = $"could not mirror the adopted world into the co-op slot: {ex.Message}";
            return false;
        }
    }

    private static void RestoreHoldbackIfAny(string holdback, string quick)
    {
        if (!Directory.Exists(holdback))
        {
            return;
        }

        if (Directory.Exists(quick))
        {
            // Should not happen (the quicksave was just moved out above), but never destroy the
            // player's held-back save if it somehow does.
            return;
        }

        Directory.Move(holdback, quick);
    }
}
