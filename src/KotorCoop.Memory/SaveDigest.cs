using KotorCoop.Memory.GameFiles;

namespace KotorCoop.Memory;

/// <summary>
/// Computes the save-identity and per-module GIT digests for the in-world half of the
/// mod-compatibility handshake (<c>WorldDigest</c> on the wire). Object ids are identical
/// across two instances only because both engines dealt them from the same save in the
/// same GIT order — so the save's identity (module + Mod_NextObjId counters + stored GIT
/// id list) is the foundation under every id-keyed channel, and each module's GIT is a
/// separate per-module fact because a mod editing one module's placements shifts ids
/// there and nowhere else.
///
/// Everything here is best-effort and read-only, like <see cref="InstallDigest"/>: a save
/// or module that cannot be read yields "unknown" (false / 0), which under the
/// Unknown-is-not-mismatch rule can only ever warn — a filesystem quirk on one machine
/// must not be able to degrade a session that would otherwise work.
/// </summary>
public static class SaveDigest
{
    private const uint FnvOffset = 2166136261;
    private const uint FnvPrime = 16777619;

    // Container type ids (see ResourceTypes). The module .sav nested inside SAVEGAME.sav
    // is typed 2057 in this build — the classic tables say 2055, which is wrong here (the
    // same measured fact PluginMenuLoader relies on), so both are accepted.
    private const int GitType = 2023;
    private const int IfoType = 2014;
    private const int ModuleSavTypeThisBuild = 2057;
    private const int ModuleSavTypeClassic = 2055;

    /// <summary>
    /// The order in which CSWSArea::LoadGIT (0x0050DD80) calls its per-list loaders,
    /// paired with the blueprint extension whose Equip_ItemList/ItemList decide how many
    /// extra ids each entry's contents consume. Read out of swkotor.exe and verified
    /// 217/217 against a real save (tools/kotorfiles.py predict_object_ids — the spec this
    /// ports). Engine data: must not be reordered or "cleaned up".
    /// </summary>
    private static readonly (string List, string Extension)[] AreaLoadOrder =
    [
        ("Creature List", "utc"),
        ("List", "uti"),            // loose items lying in the area
        ("Door List", "utd"),
        ("TriggerList", "utt"),
        ("Encounter List", "ute"),
        ("WaypointList", "utw"),
        ("SoundList", "uts"),
        ("Placeable List", "utp"),
        ("StoreList", "utm"),
    ];

    /// <summary>Object 0 is the module and object 1 is the area, so the first GIT entry
    /// gets id 2 (verified on end_m01aa: creature[0] == 0x2).</summary>
    private const uint FirstGitObjectId = 2;

    /// <summary>
    /// The save this session is playing, best-effort: the staged co-op autosave when one
    /// exists (both the plugin load route and the save-list click land on it — the GUI
    /// pins the autosave on top), else the newest real save, which is what the top of the
    /// save list resolves to otherwise. Null when no readable save exists; a wrong guess
    /// is caught downstream as a digest mismatch rather than corrupting anything.
    /// </summary>
    public static string? FindSessionSave(string gameDirectory)
    {
        try
        {
            string savesDirectory = Path.Combine(gameDirectory, "saves");
            if (!Directory.Exists(savesDirectory))
            {
                return null;
            }

            // The name PluginMenuLoader stages to (LoadPCAutoSave formats id 1, not 0).
            string staged = Path.Combine(savesDirectory, "000001 - AUTOSAVE");
            if (File.Exists(Path.Combine(staged, "SAVEGAME.sav")))
            {
                return staged;
            }

            return new DirectoryInfo(savesDirectory)
                .GetDirectories()
                .Where(d => File.Exists(Path.Combine(d.FullName, "SAVEGAME.sav")))
                .OrderByDescending(d => d.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>
    /// Digests a save's identity: the module it stands in, its Mod_NextObjId0/1 counters
    /// (the id the next runtime-created object will take — Handoff 4.9's Trask example),
    /// and the stored GIT's explicit ObjectId list. Two saves with the same digest deal
    /// identical ids; anything else and the id-keyed channels would hit wrong objects.
    /// False when any part is unreadable — a partial identity could false-match, so it is
    /// all or nothing.
    /// </summary>
    public static bool TryComputeSaveIdentity(string saveDirectory, out uint digest)
    {
        digest = 0;

        try
        {
            string savegame = Path.Combine(saveDirectory, "SAVEGAME.sav");
            if (!File.Exists(savegame))
            {
                return false;
            }

            ErfArchive outer = ErfArchive.Open(savegame);

            // A save retains previously visited modules. Only the active module's counters
            // and IDs describe this world; archive ordering can point at an old tutorial.
            if (!SavedModuleSelection.TrySelect(saveDirectory, outer, out ArchiveResource moduleSav))
            {
                return TryComputeUnvisitedModuleIdentity(saveDirectory, savegame, outer, out digest);
            }

            if (!outer.TryRead(moduleSav.Resref, moduleSav.Extension, out byte[] inner))
            {
                return false;
            }

            // Module.ifo (the NextObjId counters) lives inside the nested module .sav;
            // the outer archive is tried too in case a build stores it beside the modules.
            if (!TryFindErfMember(inner, IfoType, resref: null, out byte[] ifoBytes)
                && !TryReadOuterMember(outer, IfoType, out ifoBytes))
            {
                return false;
            }

            Gff ifo = Gff.Parse(ifoBytes);
            if (!ifo.Root.TryGetDword("Mod_NextObjId0", out uint nextObjId0))
            {
                return false;
            }

            // NextObjId1 is hashed when present; a build without it hashes as 0 on every
            // peer alike, so the comparison stays symmetric.
            ifo.Root.TryGetDword("Mod_NextObjId1", out uint nextObjId1);

            if (!TryFindErfMember(inner, GitType, resref: null, out byte[] gitBytes))
            {
                return false;
            }

            uint h = FnvString(FnvOffset, moduleSav.Resref.ToLowerInvariant());
            h = FnvUInt(h, nextObjId0);
            h = FnvUInt(h, nextObjId1);
            if (!TryHashSavedGit(h, Gff.Parse(gitBytes), out h))
            {
                // A visited save must carry a complete, unique ObjectId list. Falling back to
                // the pristine module here could produce a believable but wrong id mapping.
                return false;
            }

            // 0 is the wire's "unknown"; a real digest must never collide with it.
            digest = h == 0 ? 1u : h;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// The identity of a save whose active module was never visited: the engine's own
    /// transition autosave, made the moment a party first enters a module, names that module
    /// as LASTMODULE but stores no state for it (live 2026-09-25: `000001 - AUTOSAVE` into
    /// danm14aa held danm13's .sav and no danm14aa one). The engine loads the pristine module
    /// then, so its ids come from the module file. The digest mixes that pristine GIT with
    /// the exact SAVEGAME.sav bytes. Adoption writes the host's file verbatim, so the same
    /// save matches, and any other save that enters the same module differs.
    /// </summary>
    private static bool TryComputeUnvisitedModuleIdentity(
        string saveDirectory, string savegame, ErfArchive outer, out uint digest)
    {
        digest = 0;
        if (!PluginMenuLoader.TryReadSaveLastModuleName(saveDirectory, out string lastModule)
            || string.IsNullOrEmpty(lastModule)
            || outer.Resources.Any(resource => resource.Resref.Equals(lastModule, StringComparison.OrdinalIgnoreCase)))
        {
            return false; // unreadable, or the module IS stored and selection refused it for a reason
        }

        string? savesDirectory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(saveDirectory));
        string? gameDirectory = savesDirectory is null ? null : Path.GetDirectoryName(savesDirectory);
        if (gameDirectory is null
            || !TryComputePristineModuleGit(gameDirectory, lastModule, out uint pristine))
        {
            return false;
        }

        uint h = FnvString(FnvOffset, "unvisited:" + lastModule.ToLowerInvariant());
        h = FnvUInt(h, pristine);
        foreach (byte b in File.ReadAllBytes(savegame))
        {
            h = (h ^ b) * FnvPrime;
        }

        digest = h == 0 ? 1u : h;
        return true;
    }

    /// <summary>
    /// Digests the object ids the named module's GIT hands out. The save's stored module
    /// state wins when this save has visited the module (its GIT carries explicit
    /// ObjectId fields); otherwise the pristine module file the engine would load is used
    /// and the ids are predicted the way LoadGIT deals them. Peers on the same save make
    /// the same source choice, so the digests stay comparable.
    /// </summary>
    public static bool TryComputeModuleGit(
        string gameDirectory, string? saveDirectory, string module, out uint digest)
    {
        digest = 0;

        if (string.IsNullOrEmpty(module))
        {
            return false;
        }

        if (saveDirectory is not null)
        {
            SavedModuleGitReadResult saved = TryReadSavedModuleGit(saveDirectory, module, out byte[] savedGit);
            if (saved == SavedModuleGitReadResult.Invalid)
            {
                // A save that contains this module but cannot provide a trustworthy GIT is
                // unknown. Falling through to pristine here would compare a visited world
                // against the module's untouched archive and can produce a believable but
                // wrong object-id mapping.
                return false;
            }

            if (saved == SavedModuleGitReadResult.Present)
            {
                try
                {
                    if (!TryHashSavedGit(FnvOffset, Gff.Parse(savedGit), out uint h))
                    {
                        // The save was visited, so its explicit ids are authoritative. A
                        // missing or duplicate id is corruption/ambiguity, not an invitation
                        // to compare against the pristine module and risk a false match.
                        return false;
                    }
                    digest = h == 0 ? 1u : h;
                    return true;
                }
                catch (InvalidDataException)
                {
                    // A corrupt stored GIT is unknown. The engine's pristine fallback is not
                    // a safe compatibility identity for a save that claims to have visited it.
                    return false;
                }
            }
        }

        return TryComputePristineModuleGit(gameDirectory, module, out digest);
    }

    /// <summary>
    /// The pristine-file half of <see cref="TryComputeModuleGit"/>: reproduces, offline,
    /// the ids the engine hands out when it loads the module's GIT for the first time.
    /// The rule (verified 217/217 against a real save): lists are visited in
    /// <see cref="AreaLoadOrder"/>, entries in GIT file order; each entry takes the next
    /// id, then its blueprint's owned items (Equip_ItemList + ItemList) take the ids
    /// immediately after it.
    /// </summary>
    public static bool TryComputePristineModuleGit(string gameDirectory, string module, out uint digest)
    {
        digest = 0;

        try
        {
            List<GameArchive> archives = OpenModuleArchives(gameDirectory, module);
            if (archives.Count == 0)
            {
                return false;
            }

            Gff? git = null;
            foreach (GameArchive archive in archives)
            {
                ArchiveResource? resource = archive.Resources.FirstOrDefault(r => r.ResourceType == GitType);
                if (resource is not null && archive.TryRead(resource.Resref, resource.Extension, out byte[] bytes))
                {
                    git = Gff.Parse(bytes);
                    break;
                }
            }

            if (git is null)
            {
                return false;
            }

            // Blueprints resolve module-archives-first, then override/chitin — the
            // engine's own precedence, mirrored from the reference implementation.
            using GameFiles.GameFiles files = new(gameDirectory);

            uint h = FnvOffset;
            uint nextId = FirstGitObjectId;

            foreach ((string list, string extension) in AreaLoadOrder)
            {
                h = FnvString(h, list);

                IReadOnlyList<GffStruct> entries = git.Root.GetList(list);
                for (int i = 0; i < entries.Count; i++)
                {
                    entries[i].TryGetString("TemplateResRef", out string resref);
                    int ownedItems = CountOwnedItems(archives, files, resref, extension);

                    // Index, resref AND the dealt id all go in: the id alone would let two
                    // reorderings with equal item counts collide, and the resref alone
                    // would miss an inserted entry shifting everything after it.
                    h = FnvUInt(h, (uint)i);
                    h = FnvString(h, resref.ToLowerInvariant());
                    h = FnvUInt(h, nextId);
                    h = FnvUInt(h, (uint)ownedItems);

                    nextId += (uint)(1 + ownedItems);
                }
            }

            digest = h == 0 ? 1u : h;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    // ---- pieces ----------------------------------------------------------------

    private enum SavedModuleGitReadResult
    {
        Unvisited,
        Present,
        Invalid,
    }

    /// <summary>Reads the active module named by a save's LASTMODULE metadata and selected
    /// nested module member. Used by the live host transfer provider to reject an archive
    /// whose file identity has settled but whose contents still describe the prior solo leg.</summary>
    public static bool TryReadActiveModuleName(string saveDirectory, out string module)
    {
        module = string.Empty;
        try
        {
            string savegame = Path.Combine(saveDirectory, "SAVEGAME.sav");
            if (!File.Exists(savegame))
            {
                return false;
            }

            ErfArchive outer = ErfArchive.Open(savegame);
            if (!SavedModuleSelection.TrySelect(saveDirectory, outer, out ArchiveResource selected))
            {
                return false;
            }

            module = selected.Resref;
            return module.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Resolves the file-backed identity of the world currently served by the
    /// running game. The compatibility cache uses this live-world view; the legacy
    /// <see cref="FindSessionSave"/> view remains staged-autosave-first for load lineage and
    /// capture bookkeeping. A named historical save is never treated as live merely because
    /// its archive timestamp is newer; only the engine's conventional quicksave can cross the
    /// staged/autosave boundary.</summary>
    public static string? FindLiveSessionSave(string gameDirectory)
    {
        string? staged = FindSessionSave(gameDirectory);
        if (staged is null)
        {
            return null;
        }

        try
        {
            string quick = Path.Combine(gameDirectory, "saves", "000000 - QUICKSAVE");
            FileInfo quickFile = new(Path.Combine(quick, "SAVEGAME.sav"));
            FileInfo stagedFile = new(Path.Combine(staged, "SAVEGAME.sav"));
            if (quickFile.Exists && stagedFile.Exists
                && quickFile.LastWriteTimeUtc > stagedFile.LastWriteTimeUtc)
            {
                return quick;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
        }

        return staged;
    }

    /// <summary>The stored GIT for one module out of a save, when that save has visited
    /// it. An absent module or save has no saved state and may use the pristine archive;
    /// a present but unreadable/ambiguous module is unknown and must not silently fall back
    /// to pristine object ids.</summary>
    private static SavedModuleGitReadResult TryReadSavedModuleGit(
        string saveDirectory, string module, out byte[] git)
    {
        git = [];

        try
        {
            string savegame = Path.Combine(saveDirectory, "SAVEGAME.sav");
            if (!File.Exists(savegame))
            {
                return SavedModuleGitReadResult.Unvisited;
            }

            ErfArchive outer = ErfArchive.Open(savegame);
            ArchiveResource[] moduleSaves = outer.Resources.Where(
                r => r.ResourceType is ModuleSavTypeThisBuild or ModuleSavTypeClassic
                    && string.Equals(r.Resref, module, StringComparison.OrdinalIgnoreCase)).ToArray();

            if (moduleSaves.Length == 0)
            {
                return SavedModuleGitReadResult.Unvisited;
            }

            // Duplicate module members make the saved state ambiguous even if one happens
            // to be readable. Choosing the first would make compatibility depend on archive
            // enumeration order.
            if (moduleSaves.Length != 1)
            {
                return SavedModuleGitReadResult.Invalid;
            }

            ArchiveResource moduleSav = moduleSaves[0];
            if (!outer.TryRead(moduleSav.Resref, moduleSav.Extension, out byte[] inner)
                || !TryFindErfMember(inner, GitType, resref: null, out git))
            {
                return SavedModuleGitReadResult.Invalid;
            }

            return SavedModuleGitReadResult.Present;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // SAVEGAME.sav exists, so an unreadable archive is not evidence that the module
            // was never visited. Preserve the unknown result rather than using pristine data.
            return SavedModuleGitReadResult.Invalid;
        }
    }

    /// <summary>
    /// Folds a save-stored GIT's explicit ObjectId list into the digest. Each list is sorted
    /// by ObjectId before hashing: the save writes runtime creature entries in position order,
    /// so two peers that moved the same creatures can have different file order while retaining
    /// the same id mapping. Pristine GITs use <see cref="TryComputePristineModuleGit"/>'s
    /// separate order-sensitive engine-dealing path. Missing or duplicate ids are rejected so
    /// an ambiguous saved state never degrades into a plausible compatibility fingerprint.
    /// </summary>
    private static bool TryHashSavedGit(uint seed, Gff git, out uint digest)
    {
        digest = seed;
        uint h = seed;
        HashSet<uint> seenIds = [];

        foreach ((string list, _) in AreaLoadOrder)
        {
            h = FnvString(h, list);

            List<(uint Id, string Resref)> entries = [];
            foreach (GffStruct entry in git.Root.GetList(list))
            {
                if (!entry.TryGetDword("ObjectId", out uint id) || id == uint.MaxValue)
                {
                    return false;
                }

                if (!seenIds.Add(id))
                {
                    return false;
                }

                entry.TryGetString("TemplateResRef", out string resref);
                entries.Add((id, resref.ToLowerInvariant()));
            }

            entries.Sort(static (a, b) => a.Id.CompareTo(b.Id));
            for (int i = 0; i < entries.Count; i++)
            {
                h = FnvUInt(h, entries[i].Id);
                h = FnvString(h, entries[i].Resref);
            }
        }

        digest = h;
        return true;
    }

    /// <summary>How many ids a blueprint's contents consume: the combined length of its
    /// Equip_ItemList and ItemList. 0 when the blueprint cannot be found or parsed, which
    /// is also what the engine's loader effectively does with a missing template.</summary>
    private static int CountOwnedItems(
        List<GameArchive> archives, GameFiles.GameFiles files, string resref, string extension)
    {
        if (resref.Length == 0)
        {
            return 0;
        }

        foreach (GameArchive archive in archives)
        {
            if (archive.TryRead(resref, extension, out byte[] bytes) && TryCountItems(bytes, out int count))
            {
                return count;
            }
        }

        return files.TryReadResource(resref, extension, out byte[] loose) && TryCountItems(loose, out int fallback)
            ? fallback
            : 0;
    }

    private static bool TryCountItems(byte[] blueprint, out int count)
    {
        count = 0;

        try
        {
            Gff gff = Gff.Parse(blueprint);
            count = gff.Root.GetList("Equip_ItemList").Count + gff.Root.GetList("ItemList").Count;
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>Every archive that makes up a module, in the engine's search order.</summary>
    private static List<GameArchive> OpenModuleArchives(string gameDirectory, string module)
    {
        string modulesDirectory = Path.Combine(gameDirectory, "modules");
        List<GameArchive> archives = [];

        foreach ((string suffix, bool isRim) in
            ((string, bool)[])[(".mod", false), (".rim", true), ("_s.rim", true), ("_dlg.erf", false)])
        {
            string path = Path.Combine(modulesDirectory, module + suffix);
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                archives.Add(isRim ? RimArchive.Open(path) : ErfArchive.Open(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // One unreadable companion archive should not hide the rest of the module.
            }
        }

        return archives;
    }

    /// <summary>Finds one member of an in-memory ERF (the nested module .sav is a slice of
    /// SAVEGAME.sav, never a file of its own). The walk itself lives on
    /// <see cref="ErfArchive"/>, next to the on-disk reader whose format it shares.</summary>
    private static bool TryFindErfMember(byte[] erf, int resourceType, string? resref, out byte[] data) =>
        ErfArchive.TryFindMember(erf, resourceType, resref, out data);

    private static bool TryReadOuterMember(ErfArchive outer, int resourceType, out byte[] data)
    {
        data = [];
        ArchiveResource? resource = outer.Resources.FirstOrDefault(r => r.ResourceType == resourceType);
        return resource is not null && outer.TryRead(resource.Resref, resource.Extension, out data);
    }

    // ---- FNV-1a helpers, the house hash ---------------------------------------

    private static uint FnvString(uint seed, string value)
    {
        uint h = seed;
        foreach (char c in value)
        {
            h = (h ^ (byte)c) * FnvPrime;
        }

        return h * FnvPrime;
    }

    private static uint FnvUInt(uint seed, uint value)
    {
        uint h = seed;
        for (int i = 0; i < 4; i++)
        {
            h = (h ^ (byte)(value >> (i * 8))) * FnvPrime;
        }

        return h;
    }
}

/// <summary>
/// Session-lived cache over <see cref="SaveDigest"/> for the bridge: the save identity is
/// refreshed when its archive changes, while each module's GIT digest is held for the
/// loaded-world generation that produced it. The pump asks every heavy tick, and re-reading
/// archives from disk at 5 Hz would be pure waste.
///
/// ⚠ NOTHING HERE BLOCKS THE CALLER. Both answers cost real disk work — opening a save's
/// nested ERFs, or a module's RIMs plus one blueprint per GIT entry — and the caller is the
/// agent's heavy pump, which is also what carries a player through a module transition. The
/// transition is the most fragile few seconds in this codebase
/// (<c>docs/research/transition-window-races.md</c>), and the first tick in a new module is
/// exactly when a fresh module's digest is asked for. So a digest that is not ready yet is
/// computed on a background thread and reported as 0 = unknown until it lands, which under
/// the Unknown-is-not-mismatch rule can only ever warn. A digest that arrives a tick or two
/// late costs nothing; a pump that stalls at a transition costs a session.
///
/// The staged save is re-checked rather than latched forever, because world adoption writes
/// it AFTER an agent may already have looked: a joiner that adopted the host's world would
/// otherwise keep reporting the identity of the save it had before, and be told its save
/// does not match the host's when in fact it now literally IS the host's.
/// </summary>
public sealed class SaveDigestCache
{
    private readonly string _gameDirectory;
    private readonly Func<string, string?, string, (bool Success, uint Digest)> _computeModuleGit;
    private readonly Func<string?, (bool Success, uint Digest)> _computeSaveIdentity;
    private readonly object _refreshGate = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, uint> _moduleGit = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _unavailableModuleGit = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ModuleGitJob> _pendingModuleGit = [];

    private readonly record struct ModuleGitJob(string Module, long WorldGeneration);

    public SaveDigestCache(string gameDirectory)
        : this(gameDirectory, static (directory, save, module) =>
            SaveDigest.TryComputeModuleGit(directory, save, module, out uint digest)
                ? (true, digest)
                : (false, 0u))
    {
    }

    internal SaveDigestCache(
        string gameDirectory,
        Func<string, string?, string, (bool Success, uint Digest)> computeModuleGit,
        Func<string?, (bool Success, uint Digest)>? computeSaveIdentity = null)
    {
        _gameDirectory = gameDirectory;
        _computeModuleGit = computeModuleGit;
        _computeSaveIdentity = computeSaveIdentity ?? ComputeSaveIdentity;
    }

    private static (bool Success, uint Digest) ComputeSaveIdentity(string? saveDirectory) =>
        saveDirectory is not null && SaveDigest.TryComputeSaveIdentity(saveDirectory, out uint digest)
            ? (true, digest)
            : (false, 0u);

    private uint _saveIdentity;
    private string? _saveDirectory;
    private bool _identityPending;
    private int? _observedWorldEpoch;
    private long _moduleGitWorldGeneration;

    /// <summary>What the current identity was computed from: the save directory plus its
    /// SAVEGAME.sav size and write time. A change refreshes save identity; it does not by
    /// itself mean the loaded module was replaced.</summary>
    private (string Path, long Length, long Ticks) _identityStamp;

    /// <summary>How often the staged save is re-stat'ed. Two file-system calls, far apart
    /// enough to be free, often enough that an adoption is noticed within a heartbeat.</summary>
    private const int StampCheckIntervalMs = 2000;

    /// <summary>Deliberately not <c>long.MinValue</c>: the staleness test is a subtraction,
    /// and subtracting the minimum overflows to a negative interval that would read as "just
    /// checked" forever, so the very first refresh would never run.</summary>
    private long _lastStampCheckMs = -2 * StampCheckIntervalMs;
    private long _saveRefreshSequence;

    /// <summary>The live/staged save's identity digest, preferring a complete quicksave
    /// written after the staged baseline, and 0 while unknown (including "not computed yet" —
    /// the first ask starts the work and answers on a later tick).</summary>
    public uint SaveIdentity
    {
        get
        {
            RefreshSaveIfStale();

            lock (_gate)
            {
                return _saveIdentity;
            }
        }
    }

    /// <summary>The named module's GIT digest, 0 when unknown or not computed yet.</summary>
    public uint ModuleGit(string module)
    {
        if (string.IsNullOrEmpty(module))
        {
            return 0;
        }

        RefreshSaveIfStale();

        ModuleGitJob job;
        string? saveDirectory;
        (string, long, long) stamp;
        lock (_gate)
        {
            if (_moduleGit.TryGetValue(module, out uint digest))
            {
                return digest;
            }

            if (_unavailableModuleGit.Contains(module))
            {
                return 0; // this loaded world changed on disk before a trustworthy baseline was captured
            }

            // The cache and unavailable set are case-insensitive. Normalize the pending
            // key too, so casing differences cannot launch duplicate archive reads.
            job = new ModuleGitJob(module.ToUpperInvariant(), _moduleGitWorldGeneration);
            if (!_pendingModuleGit.Add(job))
            {
                return 0; // already being computed; the answer lands on a later tick
            }

            saveDirectory = _saveDirectory;
            stamp = _identityStamp;
        }

        Run(() =>
        {
            (bool success, uint computed) = _computeModuleGit(_gameDirectory, saveDirectory, module);
            (string Path, long Length, long Ticks) currentStamp = CurrentSaveStamp();

            lock (_gate)
            {
                // A late read from an earlier loaded world can never become the next world's
                // baseline. Likewise, a save that changed while the first digest was being
                // computed cannot provide evidence of the mapping that was loaded into memory.
                // Existing baselines survive ordinary saves; an uncaptured one stays unknown
                // until a confirmed world generation begins.
                if (_moduleGitWorldGeneration == job.WorldGeneration)
                {
                    if (_identityStamp == stamp && currentStamp == stamp)
                    {
                        _moduleGit[module] = success ? computed : 0;
                    }
                    else if (_identityStamp != stamp || currentStamp != stamp)
                    {
                        _unavailableModuleGit.Add(module);
                    }
                }
            }
        }, () =>
        {
            lock (_gate)
            {
                _pendingModuleGit.Remove(job);
            }
        });

        return 0;
    }

    /// <summary>
    /// Marks the bridge's current loaded-world epoch. A module digest describes the mapping
    /// the engine loaded, not whichever version of its save happened to be written most
    /// recently. Module transitions and confirmed same-module Load Game actions both advance
    /// this epoch; ordinary quicksaves do not.
    /// </summary>
    public void ObserveLoadedWorld(int worldEpoch)
    {
        // Serialize the generation edge with source resolution. Otherwise a concurrent
        // same-epoch caller could see the epoch already updated, skip its own forced refresh,
        // and start a baseline against the previous save directory.
        lock (_refreshGate)
        {
            lock (_gate)
            {
                if (_observedWorldEpoch == worldEpoch)
                {
                    return;
                }

                _observedWorldEpoch = worldEpoch;
                _moduleGitWorldGeneration++;
                _moduleGit.Clear();
                _unavailableModuleGit.Clear();
            }

            // The disk source can change as part of a load/adoption. Do not wait for the
            // ordinary two-second stat interval before the first module baseline in the newly
            // loaded world captures its source path and stamp.
            RefreshSaveIfStale(force: true);
        }
    }

    /// <summary>Invalidates the module mapping baseline when an adoption replaces the world.</summary>
    public void InvalidateLoadedWorld()
    {
        lock (_refreshGate)
        {
            lock (_gate)
            {
                _observedWorldEpoch = null;
                _moduleGitWorldGeneration++;
                _moduleGit.Clear();
                _unavailableModuleGit.Clear();
            }

            RefreshSaveIfStale(force: true);
        }
    }

    /// <summary>
    /// Waits for every digest started so far. For tests and for a caller that genuinely
    /// wants the answer synchronously (the adoption self-check, which runs at a menu with
    /// no world to stall). Returns false on timeout rather than throwing — a slow disk is
    /// not a failure, it is another "unknown".
    /// </summary>
    public bool WaitForPending(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                if (!_identityPending && _pendingModuleGit.Count == 0)
                {
                    return true;
                }
            }

            Thread.Sleep(5);
        }

        lock (_gate)
        {
            return !_identityPending && _pendingModuleGit.Count == 0;
        }
    }

    /// <summary>
    /// Notices a newly staged save (an adoption, or the player loading a different game) and
    /// starts a fresh identity job when one appears. Cheap: a stat of one file, at most every
    /// <see cref="StampCheckIntervalMs"/>.
    /// </summary>
    private void RefreshSaveIfStale(bool force = false)
    {
        lock (_refreshGate)
        {
            RefreshSaveIfStaleCore(force);
        }
    }

    private void RefreshSaveIfStaleCore(bool force)
    {
        long now = Environment.TickCount64;
        long refreshSequence;

        lock (_gate)
        {
            if (!force && now - _lastStampCheckMs < StampCheckIntervalMs)
            {
                return;
            }

            _lastStampCheckMs = now;
            refreshSequence = ++_saveRefreshSequence;
        }

        string? directory = SaveDigest.FindLiveSessionSave(_gameDirectory);
        (string Path, long Length, long Ticks) stamp = StampOf(directory);

        bool startIdentityJob = false;
        lock (_gate)
        {
            // A forced world-generation refresh may have completed while this slower source
            // lookup was in progress. Do not let its older snapshot replace the newer one.
            if (refreshSequence != _saveRefreshSequence)
            {
                return;
            }

            if (stamp == _identityStamp)
            {
                return;
            }

            // The save changed under us. Its save identity must be recomputed, but an already
            // captured module baseline still describes the live in-memory world until a
            // module/load epoch says that world was replaced.
            _identityStamp = stamp;
            _saveDirectory = directory;
            _saveIdentity = 0;
            // Keep already-captured module baselines for this loaded world. Any first-time
            // computation in flight is no longer trustworthy against a changing save and is
            // blocked for this epoch when its result is fenced below.
            foreach (ModuleGitJob pending in _pendingModuleGit)
            {
                if (pending.WorldGeneration == _moduleGitWorldGeneration)
                {
                    _unavailableModuleGit.Add(pending.Module);
                }
            }

            if (!_identityPending)
            {
                _identityPending = true;
                startIdentityJob = true;
            }
        }

        if (startIdentityJob)
        {
            StartIdentityJob(directory, stamp);
        }
    }

    private void StartIdentityJob(string? directory, (string Path, long Length, long Ticks) stamp)
    {
        Run(() =>
        {
            (bool success, uint computed) = _computeSaveIdentity(directory);

            lock (_gate)
            {
                // Only publish if the world has not moved on again while we were reading.
                if (_identityStamp == stamp)
                {
                    _saveIdentity = success ? computed : 0;
                }
            }
        }, () =>
        {
            string? nextDirectory = null;
            (string Path, long Length, long Ticks) nextStamp = default;
            bool startNext = false;

            lock (_gate)
            {
                _identityPending = false;
                if (_identityStamp != stamp)
                {
                    // A world-generation refresh may have found a new source while this
                    // identity worker was reading the old one. Chain the current source now;
                    // the refresh throttle must not leave SaveIdentity unknown indefinitely.
                    nextDirectory = _saveDirectory;
                    nextStamp = _identityStamp;
                    _identityPending = true;
                    startNext = true;
                }
            }

            if (startNext)
            {
                StartIdentityJob(nextDirectory, nextStamp);
            }
        });
    }

    private (string Path, long Length, long Ticks) CurrentSaveStamp()
    {
        string? directory = SaveDigest.FindLiveSessionSave(_gameDirectory);
        return StampOf(directory);
    }

    /// <summary>
    /// Runs one digest job off the caller's thread and clears its pending mark whatever
    /// happens — a job that threw must not wedge the slot forever.
    ///
    /// ⚠ A DEDICATED THREAD, NOT <c>Task.Run</c>. This work is synchronous file I/O
    /// (<c>TryComputeModuleGit</c> opens and parses a module's archives), and every module
    /// a transition passes through can request a fresh generation's work. `Task.Run` schedules
    /// onto the
    /// SHARED thread pool, which this same process also uses for every other async
    /// continuation — including the agent's own per-tick loop. The pool's thread-injection
    /// rate is throttled (new threads are added roughly one at a time, seconds apart, under
    /// sustained demand), so a burst of these synchronous jobs landing together after a
    /// transition can starve unrelated pool work for tens of seconds with no exception, no
    /// deadlock, and no CPU idle time to suggest anything is wrong — a plausible match for a
    /// pump that silently stops for 60-90s and later resumes on its own. A dedicated
    /// background thread per job never competes with the pool at all.
    /// </summary>
    private void Run(Action work, Action completed)
    {
        Thread thread = new(() =>
        {
            try
            {
                work();
            }
            catch (Exception)
            {
                // Best effort by design: the digest stays 0 = unknown, which only warns.
            }
            finally
            {
                completed();
            }
        })
        {
            IsBackground = true,
            Name = "SaveDigest",
        };
        thread.Start();
    }

    private static (string, long, long) StampOf(string? saveDirectory)
    {
        if (saveDirectory is null)
        {
            return (string.Empty, 0, 0);
        }

        try
        {
            FileInfo info = new(Path.Combine(saveDirectory, "SAVEGAME.sav"));
            return info.Exists
                ? (saveDirectory, info.Length, info.LastWriteTimeUtc.Ticks)
                : (saveDirectory, 0, 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (saveDirectory, 0, 0);
        }
    }
}
