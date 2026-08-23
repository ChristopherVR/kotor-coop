using KotorCoop.Memory.GameFiles;

namespace KotorCoop.Memory;

/// <summary>
/// Builds an ADDITIVE, offline copy of a save directory with one or more of its own
/// declared global booleans/numbers forced to a chosen value — the tool half of the
/// swoop-race investigation in <c>docs/research/minigames.md</c> §11.
///
/// Exists because that investigation's live rounds (§9, §10) reached and crossed the
/// swoop-track trigger (<c>tar02_swoop02ab</c>, script <c>k_ptar_swoop02ab</c>) cleanly but
/// the race never started, and the leading theory is that the trigger's <c>OnEnter</c>
/// checks a global the rig's only save (an Endar-Spire-origin save with no Taris story
/// progress) never set. There is no NWScript disassembler in this tree
/// (<c>tools/kotorfiles.py</c> reads GFF/BIF/ERF containers, not compiled <c>.ncs</c>
/// bytecode), so the exact name and required value are NOT known with certainty — see the
/// candidate list this class's caller supplies. What IS certain, read directly out of a
/// real save's own <c>GLOBALVARS.res</c> catalogue (<c>tools/globalvars.py dump</c>'s same
/// file format), is the set of names that actually exist to be set; see
/// <see cref="SwoopFlagCandidates.Names"/>.
///
/// Deliberately narrow in scope, matching the risk the task accepted: this NEVER touches
/// the copy's module, position, player struct or GIT — only the two <c>GLOBALVARS.res</c>
/// VOID blobs (<c>ValBoolean</c>, <c>ValNumber</c>) that back the catalogue's own declared
/// names. A module/position change would need either a full synthetic module .sav (risky,
/// unverifiable without a live engine to load it against) or the real pristine
/// <c>tar_m02ab</c> module files (which this investigation's own sandboxing rules kept out
/// of reach this round — see the doc). Global-flag surgery is the smallest, safest
/// intervention that can plausibly unblock the trigger, and it can never corrupt the
/// archive framing: <c>GLOBALVARS.res</c> is a standalone GFF file beside
/// <c>SAVEGAME.sav</c>, not a member nested inside an ERF the way the module state is.
/// </summary>
public static class GlobalFlagCraft
{
    private const string GlobalVarsFile = "GLOBALVARS.res";

    /// <summary>One requested-flag outcome: whether the name was in the save's own
    /// catalogue (and therefore actually changed) or not (left alone, reported so the
    /// caller does not assume it worked).</summary>
    public sealed record FlagResult(string Name, string Type, bool Found, int? PreviousValue, int? NewValue);

    /// <summary>
    /// Copies every file in <paramref name="sourceSaveDirectory"/> into a freshly created
    /// <paramref name="destSaveDirectory"/>, then rewrites the copy's <c>GLOBALVARS.res</c>
    /// so each name in <paramref name="booleanFlags"/>/<paramref name="numberFlags"/> that
    /// is already declared in that save's own catalogue takes the requested value.
    ///
    /// A name NOT already in the catalogue is left untouched and reported as not-found —
    /// never fabricated. The engine's own <c>SetGlobalBoolean</c> silently no-ops on an
    /// unknown name (<c>tools/globalvars.py</c>'s <c>ENGINE_NOTES</c>: "the catalogue is
    /// FIXED"), and inventing a new catalogue entry this tool cannot verify the runtime
    /// would even recognise would be exactly the guess the task said not to make.
    /// </summary>
    public static bool TryCraft(
        string sourceSaveDirectory,
        string destSaveDirectory,
        IReadOnlyDictionary<string, bool>? booleanFlags,
        IReadOnlyDictionary<string, int>? numberFlags,
        out IReadOnlyList<FlagResult> results,
        out string diagnostic)
    {
        results = [];

        if (string.IsNullOrWhiteSpace(sourceSaveDirectory) || !Directory.Exists(sourceSaveDirectory))
        {
            diagnostic = $"no such source save directory: '{sourceSaveDirectory}'";
            return false;
        }

        string sourceGlobals = Path.Combine(sourceSaveDirectory, GlobalVarsFile);
        if (!File.Exists(sourceGlobals))
        {
            diagnostic = $"'{sourceSaveDirectory}' has no {GlobalVarsFile} — not a save directory";
            return false;
        }

        if (Directory.Exists(destSaveDirectory) && Directory.EnumerateFileSystemEntries(destSaveDirectory).Any())
        {
            diagnostic = $"destination '{destSaveDirectory}' already exists and is not empty — " +
                "refusing to overwrite an existing save";
            return false;
        }

        try
        {
            Directory.CreateDirectory(destSaveDirectory);

            foreach (string file in Directory.EnumerateFiles(sourceSaveDirectory))
            {
                File.Copy(file, Path.Combine(destSaveDirectory, Path.GetFileName(file)), overwrite: false);
            }

            string destGlobals = Path.Combine(destSaveDirectory, GlobalVarsFile);
            Gff parsed = Gff.Parse(File.ReadAllBytes(destGlobals));

            List<FlagResult> outcome = [];

            byte[] boolBlob = GetVoid(parsed.Root, "ValBoolean");
            IReadOnlyList<GffStruct> boolCat = parsed.Root.GetList("CatBoolean");
            bool boolBlobChanged = false;

            foreach ((string name, bool value) in booleanFlags ?? new Dictionary<string, bool>())
            {
                int index = FindCatalogueIndex(boolCat, name);
                if (index < 0)
                {
                    outcome.Add(new FlagResult(name, "boolean", Found: false, PreviousValue: null, NewValue: null));
                    continue;
                }

                int byteIndex = index >> 3;
                int bit = 7 - (index & 7);
                if (byteIndex >= boolBlob.Length)
                {
                    // The catalogue declares more entries than ValBoolean has bytes for —
                    // a malformed source file. Report, do not throw: one bad name must not
                    // sink every other requested flag.
                    outcome.Add(new FlagResult(name, "boolean", Found: false, PreviousValue: null, NewValue: null));
                    continue;
                }

                int previous = (boolBlob[byteIndex] >> bit) & 1;
                int updated = value ? 1 : 0;

                if (updated == 1)
                {
                    boolBlob[byteIndex] |= (byte)(1 << bit);
                }
                else
                {
                    boolBlob[byteIndex] &= (byte)~(1 << bit);
                }

                boolBlobChanged = true;
                outcome.Add(new FlagResult(name, "boolean", Found: true, PreviousValue: previous, NewValue: updated));
            }

            byte[] numberBlob = GetVoid(parsed.Root, "ValNumber");
            IReadOnlyList<GffStruct> numberCat = parsed.Root.GetList("CatNumber");
            bool numberBlobChanged = false;

            foreach ((string name, int value) in numberFlags ?? new Dictionary<string, int>())
            {
                int index = FindCatalogueIndex(numberCat, name);
                if (index < 0 || index >= numberBlob.Length)
                {
                    outcome.Add(new FlagResult(name, "number", Found: false, PreviousValue: null, NewValue: null));
                    continue;
                }

                sbyte previous = unchecked((sbyte)numberBlob[index]);
                sbyte clamped = (sbyte)Math.Clamp(value, sbyte.MinValue, sbyte.MaxValue);
                numberBlob[index] = unchecked((byte)clamped);
                numberBlobChanged = true;
                outcome.Add(new FlagResult(name, "number", Found: true, PreviousValue: previous, NewValue: clamped));
            }

            results = outcome;

            if (boolBlobChanged || numberBlobChanged)
            {
                GffWriter writer = new(parsed.FileType);
                GffCopy.CopyInto(parsed.Root, writer.Root);

                if (boolBlobChanged)
                {
                    writer.Root.SetVoid("ValBoolean", boolBlob);
                }

                if (numberBlobChanged)
                {
                    writer.Root.SetVoid("ValNumber", numberBlob);
                }

                File.WriteAllBytes(destGlobals, writer.ToBytes());
            }

            int foundCount = outcome.Count(r => r.Found);
            int missingCount = outcome.Count - foundCount;
            diagnostic = $"crafted '{destSaveDirectory}' from '{sourceSaveDirectory}': " +
                $"{foundCount} flag(s) set, {missingCount} name(s) not in this save's catalogue";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            diagnostic = $"could not craft '{destSaveDirectory}': {ex.Message}";
            return false;
        }
    }

    /// <summary>Reads back a crafted save's GLOBALVARS.res and confirms each requested flag
    /// round-tripped to the value it was set to — the structural self-check this class's own
    /// caller runs before handing a save off, mirroring the discipline
    /// <c>SaveDigest</c>/<c>SaveDigestTests</c> already use for save fixtures.</summary>
    public static bool TryVerify(
        string saveDirectory,
        IReadOnlyDictionary<string, bool>? expectedBooleans,
        IReadOnlyDictionary<string, int>? expectedNumbers,
        out IReadOnlyList<string> mismatches,
        out string diagnostic)
    {
        List<string> problems = [];
        mismatches = problems;

        string globalsPath = Path.Combine(saveDirectory, GlobalVarsFile);
        if (!File.Exists(globalsPath))
        {
            diagnostic = $"'{saveDirectory}' has no {GlobalVarsFile}";
            return false;
        }

        try
        {
            Gff parsed = Gff.Parse(File.ReadAllBytes(globalsPath));

            byte[] boolBlob = GetVoid(parsed.Root, "ValBoolean");
            IReadOnlyList<GffStruct> boolCat = parsed.Root.GetList("CatBoolean");

            foreach ((string name, bool expected) in expectedBooleans ?? new Dictionary<string, bool>())
            {
                int index = FindCatalogueIndex(boolCat, name);
                if (index < 0)
                {
                    problems.Add($"{name}: not in catalogue");
                    continue;
                }

                int byteIndex = index >> 3;
                int bit = 7 - (index & 7);
                int actual = byteIndex < boolBlob.Length ? (boolBlob[byteIndex] >> bit) & 1 : 0;
                if (actual != (expected ? 1 : 0))
                {
                    problems.Add($"{name}: expected {(expected ? 1 : 0)}, found {actual}");
                }
            }

            byte[] numberBlob = GetVoid(parsed.Root, "ValNumber");
            IReadOnlyList<GffStruct> numberCat = parsed.Root.GetList("CatNumber");

            foreach ((string name, int expected) in expectedNumbers ?? new Dictionary<string, int>())
            {
                int index = FindCatalogueIndex(numberCat, name);
                if (index < 0 || index >= numberBlob.Length)
                {
                    problems.Add($"{name}: not in catalogue");
                    continue;
                }

                sbyte actual = unchecked((sbyte)numberBlob[index]);
                if (actual != Math.Clamp(expected, sbyte.MinValue, sbyte.MaxValue))
                {
                    problems.Add($"{name}: expected {expected}, found {actual}");
                }
            }

            diagnostic = problems.Count == 0
                ? $"'{saveDirectory}' verified: every requested flag round-tripped correctly"
                : $"'{saveDirectory}' has {problems.Count} mismatch(es)";
            return problems.Count == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            diagnostic = $"could not verify '{saveDirectory}': {ex.Message}";
            return false;
        }
    }

    private static int FindCatalogueIndex(IReadOnlyList<GffStruct> catalogue, string name)
    {
        for (int i = 0; i < catalogue.Count; i++)
        {
            if (catalogue[i].TryGetString("Name", out string entryName)
                && string.Equals(entryName, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private static byte[] GetVoid(GffStruct root, string field) =>
        root.Fields.TryGetValue(field, out object? raw) && raw is byte[] blob
            ? (byte[])blob.Clone()
            : [];
}

/// <summary>
/// The candidate global-variable names for the swoop-race trigger investigation
/// (<c>docs/research/minigames.md</c> §11), read directly out of the live rig's own
/// <c>saves\000002 - Game1\GLOBALVARS.res</c> catalogue (via a single read-only file
/// inspection, not a bulk script — the round that produced this list was explicitly
/// barred from scripted access to the live install; see the doc for exactly how these
/// names were recovered). Every name here is REAL — it exists in that save's own
/// catalogue — but WHICH of them (if any) actually gates <c>k_ptar_swoop02ab</c>, and
/// what value it must hold, is NOT verified: there is no NWScript disassembler in this
/// tree to read the compiled trigger script directly. Ranked by how directly the name
/// reads as "have you been introduced to Taris swoop racing" rather than "how many times
/// have you raced" or "which upgrade did you find".
/// </summary>
public static class SwoopFlagCandidates
{
    /// <summary>Boolean candidates, most-plausible-precondition first.</summary>
    public static readonly IReadOnlyList<string> Booleans =
    [
        "TAR_FIRST_SWOOP_USE",
        "Tar_SwoopRace",
        "Tar_SwoopNew",
        "tat_oneofficialrace", // Tatooine's equivalent name, listed only for pattern comparison
        "TAR_STARTED",
    ];

    /// <summary>Number/counter candidates — plausibly state machines ("not started / in
    /// progress / won") rather than gates, but cheap to try since they are real, declared
    /// names.</summary>
    public static readonly IReadOnlyList<string> Numbers =
    [
        "Tar_SwoopStatus",
        "Tar_SwoopRaceCounter",
    ];
}
