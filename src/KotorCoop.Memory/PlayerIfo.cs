using KotorCoop.Memory.GameFiles;

namespace KotorCoop.Memory;

/// <summary>
/// Lifts a player character out of one save and re-emits it as <c>Temp\pifo.ifo</c>, so the
/// engine can log that character into a world it did not come from.
///
/// This is the file half of world adoption. The engine's login chain forks on
/// <c>DoingStartNewModule</c> (<c>serverInternal+0x1007C</c>): cleared, it reads the PC out
/// of the loaded module's own <c>module.ifo</c>; set, it calls
/// <c>CSWSPlayer::LoadCharacterFromIFO(-1)</c> (0x561E30), which reads the PC out of
/// <c>TEMP:pifo</c> — a standalone one-player IFO with no tie to the world being loaded.
/// That second flavour is exactly "load THIS world, but with THAT character", which is what
/// a joining player needs; retail only ever uses it for autosaves and chargen, because those
/// are the only cases that write a pifo. See docs/research/character-login-chain.md.
///
/// ⚠ The character is copied VERBATIM (117 fields, nested lists and all — see
/// <see cref="GffCopy"/>). Nothing here interprets a stat, and the one field worth naming is
/// the one deliberately left alone: a saved PC's <c>ObjectId</c> reads <c>0x7FFFFFFF</c>,
/// the invalid sentinel — measured on two real saves — so the engine mints a fresh id on
/// load and a character carried into a foreign world cannot collide with an object already
/// in it.
/// </summary>
public static class PlayerIfo
{
    // Container type ids, matching SaveDigest's (the module .sav nested inside SAVEGAME.sav
    // is typed 2057 in this build; the classic tables say 2055, so both are accepted).
    private const int IfoType = 2014;
    private const int ModuleSavTypeThisBuild = 2057;
    private const int ModuleSavTypeClassic = 2055;

    /// <summary>The resource the engine looks for, and the file it becomes: restype 0x7DE
    /// ("ifo") under resref "pifo" is <c>&lt;game&gt;\Temp\pifo.ifo</c>.</summary>
    private const string PifoResRef = "pifo";

    /// <summary>What a neutralised pifo member is renamed to. Ours by name so an operator
    /// reading a hex dump knows who did it, and 12 characters so it fits the 16-byte key
    /// cell with room for the NUL padding.</summary>
    private const string NeutralisedPifoName = "kcoopoldpifo";

    /// <summary>
    /// Reads the primary player character out of a save directory's <c>SAVEGAME.sav</c>.
    ///
    /// The PC lives in <c>&lt;module&gt;.sav → module.ifo → Mod_PlayerList</c>, written by
    /// <c>CSWSModule::SavePlayers</c> (0x4C7870). Entry selection follows
    /// <c>CSWSModule::GetPrimaryPlayerIndex</c> (0x4C5D90), which walks for the is-PC mark:
    /// on disk that is <c>Mod_IsPrimaryPlr</c>, with <c>IsPC</c> as the fallback and index 0
    /// as the last resort (every single-player save measured has exactly one entry).
    /// </summary>
    public static bool TryExtractPlayer(string saveDirectory, out GffStruct player, out string diagnostic)
    {
        player = null!;

        try
        {
            string savegame = Path.Combine(saveDirectory, "SAVEGAME.sav");
            if (!File.Exists(savegame))
            {
                diagnostic = $"no SAVEGAME.sav in '{saveDirectory}'";
                return false;
            }

            ErfArchive outer = ErfArchive.Open(savegame);

            ArchiveResource? moduleSav = outer.Resources.FirstOrDefault(
                r => r.ResourceType is ModuleSavTypeThisBuild or ModuleSavTypeClassic);

            if (moduleSav is null || !outer.TryRead(moduleSav.Resref, moduleSav.Extension, out byte[] inner))
            {
                diagnostic = $"'{saveDirectory}' holds no module .sav";
                return false;
            }

            if (!ErfArchive.TryFindMember(inner, IfoType, resref: null, out byte[] ifoBytes))
            {
                diagnostic = $"'{moduleSav.Resref}.sav' holds no module.ifo";
                return false;
            }

            IReadOnlyList<GffStruct> players = Gff.Parse(ifoBytes).Root.GetList("Mod_PlayerList");

            if (players.Count == 0)
            {
                // The measured failure mode this guards: a save whose Mod_PlayerList is
                // empty produces a blank level-0 PC at the module start point rather than
                // an error, because the engine's 0xF login branch ignores the return value
                // of LoadCharacterFromIFO. Catching it here is the preflight the login-chain
                // research asked for.
                diagnostic = "the save's Mod_PlayerList is empty (no character to carry)";
                return false;
            }

            player = SelectPrimary(players);
            diagnostic = $"carried the character from '{Path.GetFileName(saveDirectory)}' "
                + $"({player.Count} fields, module '{moduleSav.Resref}')";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            diagnostic = $"could not read the character out of '{saveDirectory}': {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Builds the standalone pifo bytes for one player entry: an "IFO " GFF whose root
    /// carries a single-element <c>Mod_PlayerList</c>.
    ///
    /// Deliberately minimal. <c>LoadCharacterFromIFO(-1)</c> reads the top-level struct,
    /// takes <c>Mod_PlayerList</c> and indexes it by <c>[player+0x80]</c> — which is 0 from
    /// the constructor on this path — and reads nothing else in the file. Emitting one entry
    /// makes that index correct by construction whichever entry the source save marked, and
    /// copying the rest of a foreign <c>module.ifo</c> would ship module state that nothing
    /// reads and that could only ever disagree with the world actually loading.
    /// </summary>
    public static byte[] BuildPifo(GffStruct player)
    {
        ArgumentNullException.ThrowIfNull(player);

        GffWriter writer = new("IFO");

        // The member keeps its source struct id (48813 in every save measured): list-member
        // ids are data, and renumbering them is the kind of silent change that only shows up
        // in a reader that switches on them.
        GffCopy.CopyInto(player, writer.Root.AddList("Mod_PlayerList").Add(player.StructId));

        return writer.ToBytes();
    }

    /// <summary>
    /// Writes <c>&lt;gameDirectory&gt;\Temp\pifo.ifo</c>, creating <c>Temp\</c> if the engine
    /// has not yet (it does so itself inside <c>LoadPCAutoSave</c> at 0x6CA542).
    ///
    /// ⚠ CALL THIS BOTH BEFORE AND AFTER <c>LoadPCAutoSave</c>. Before, because the login
    /// must find the file; after, because that function touches <c>Temp\</c> on its own path
    /// and whether it can clear an existing pifo is NEEDS-LIVE. Re-writing is idempotent and
    /// costs a few hundred microseconds, and the read that matters happens frames later —
    /// <c>LoadCharacterFromIFO</c> registers <c>TEMP:</c> itself (0x561EAE) at login time,
    /// which the module load reaches seconds after the call returns.
    /// </summary>
    public static bool TryWritePifo(string gameDirectory, GffStruct player, out string diagnostic)
    {
        try
        {
            string temp = Path.Combine(gameDirectory, "Temp");
            Directory.CreateDirectory(temp);

            string path = Path.Combine(temp, $"{PifoResRef}.ifo");
            File.WriteAllBytes(path, BuildPifo(player));

            diagnostic = $"wrote {path}";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            diagnostic = $"could not write Temp\\pifo.ifo: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Carries the character from <paramref name="saveDirectory"/> into
    /// <c>Temp\pifo.ifo</c>, ready for an adopted world to log in.
    /// </summary>
    public static bool TryStagePlayer(string gameDirectory, string saveDirectory, out string diagnostic)
    {
        if (!TryExtractPlayer(saveDirectory, out GffStruct player, out diagnostic))
        {
            return false;
        }

        string carried = diagnostic;

        if (!TryWritePifo(gameDirectory, player, out diagnostic))
        {
            return false;
        }

        diagnostic = $"{carried}; {diagnostic}";
        return true;
    }

    /// <summary>
    /// Renames any <c>pifo</c> member of an adopted <c>SAVEGAME.sav</c> so it cannot be
    /// exported over the one we staged.
    ///
    /// ⚠ THIS IS THE TRAP THAT WOULD PUT A JOINER IN THE HOST'S BODY. <c>LoadPCAutoSave</c>
    /// exports the staged save to <c>GAMEINPROGRESS:</c> and then copies
    /// <c>&lt;export&gt;\pifo</c> over <c>TEMP:pifo</c> (<c>CExoResMan::FileCopy</c> at
    /// 0x6CA634). A manual save has no pifo, which is why the existing load path is
    /// unaffected — but a genuine AUTOSAVE does, and it holds the character of whoever made
    /// it. Adopting a host's autosave verbatim would therefore overwrite the joiner's staged
    /// character with the host's, and both players would come up as the same person.
    ///
    /// The edit is a 16-byte overwrite of the member's key cell, in place: offsets, sizes
    /// and every other member are untouched, so the archive stays valid and the payload is
    /// still there under a name nothing looks for. Rebuilding the ERF to drop the member
    /// would be a far larger operation for no gain.
    ///
    /// Returns true when a pifo was found and renamed; false when there was none (the
    /// common case) or the buffer is not a readable ERF.
    /// </summary>
    public static bool TryNeutralisePifo(byte[] savegame)
    {
        ArgumentNullException.ThrowIfNull(savegame);

        if (savegame.Length < 160)
        {
            return false;
        }

        uint count = ReadU32(savegame, 0x10);
        uint keysOffset = ReadU32(savegame, 0x18);

        for (uint i = 0; i < count && i < 4096; i++)
        {
            long key = keysOffset + (24L * i);
            if (key + 24 > savegame.Length)
            {
                return false;
            }

            int type = savegame[key + 20] | (savegame[key + 21] << 8);
            if (type != IfoType)
            {
                continue;
            }

            if (!string.Equals(
                GameFileText.CString(savegame.AsSpan((int)key, 16)),
                PifoResRef,
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 16-byte NUL-padded cell: clear it, then write the replacement over the front.
            Span<byte> cell = savegame.AsSpan((int)key, 16);
            cell.Clear();
            for (int c = 0; c < NeutralisedPifoName.Length; c++)
            {
                cell[c] = (byte)NeutralisedPifoName[c];
            }

            return true;
        }

        return false;
    }

    /// <summary>The entry <c>GetPrimaryPlayerIndex</c> would pick: the is-PC mark first,
    /// <c>IsPC</c> as its on-disk twin, index 0 as the last resort.</summary>
    private static GffStruct SelectPrimary(IReadOnlyList<GffStruct> players)
    {
        foreach (GffStruct entry in players)
        {
            if (entry.TryGetInt("Mod_IsPrimaryPlr", out int primary) && primary != 0)
            {
                return entry;
            }
        }

        foreach (GffStruct entry in players)
        {
            if (entry.TryGetInt("IsPC", out int isPc) && isPc != 0)
            {
                return entry;
            }
        }

        return players[0];
    }

    private static uint ReadU32(byte[] buffer, long offset) =>
        (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));
}
