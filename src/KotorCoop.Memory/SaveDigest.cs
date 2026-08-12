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

            // The nested module .sav names the module the save is standing in.
            ArchiveResource? moduleSav = outer.Resources.FirstOrDefault(
                r => r.ResourceType is ModuleSavTypeThisBuild or ModuleSavTypeClassic);
            if (moduleSav is null || !outer.TryRead(moduleSav.Resref, moduleSav.Extension, out byte[] inner))
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
            h = HashSavedGit(h, Gff.Parse(gitBytes));

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

        if (saveDirectory is not null && TryReadSavedModuleGit(saveDirectory, module, out byte[] savedGit))
        {
            try
            {
                uint h = HashSavedGit(FnvOffset, Gff.Parse(savedGit));
                digest = h == 0 ? 1u : h;
                return true;
            }
            catch (InvalidDataException)
            {
                // A corrupt stored GIT falls through to the pristine file, which is what
                // the engine would fall back on failing to restore the area too.
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

    /// <summary>The stored GIT for one module out of a save, when that save has visited
    /// it. False (never a throw) when the save is unreadable or the module is unvisited —
    /// the caller cannot tell those apart, and both mean "use the pristine file".</summary>
    private static bool TryReadSavedModuleGit(string saveDirectory, string module, out byte[] git)
    {
        git = [];

        try
        {
            string savegame = Path.Combine(saveDirectory, "SAVEGAME.sav");
            if (!File.Exists(savegame))
            {
                return false;
            }

            ErfArchive outer = ErfArchive.Open(savegame);
            ArchiveResource? moduleSav = outer.Resources.FirstOrDefault(
                r => r.ResourceType is ModuleSavTypeThisBuild or ModuleSavTypeClassic
                    && string.Equals(r.Resref, module, StringComparison.OrdinalIgnoreCase));

            return moduleSav is not null
                && outer.TryRead(moduleSav.Resref, moduleSav.Extension, out byte[] inner)
                && TryFindErfMember(inner, GitType, resref: null, out git);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// Folds a save-stored GIT's explicit ObjectId list into the digest. File order is
    /// hashed as-is: the save writes its GIT sorted by X, but two copies of the same save
    /// are byte-identical, which is exactly the identity being tested — the order is never
    /// interpreted as the engine's load order (Handoff 4.9's warning).
    /// </summary>
    private static uint HashSavedGit(uint seed, Gff git)
    {
        uint h = seed;

        foreach ((string list, _) in AreaLoadOrder)
        {
            h = FnvString(h, list);

            foreach (GffStruct entry in git.Root.GetList(list))
            {
                // A save GIT entry always carries its id; 0xFFFFFFFF marks a malformed
                // one so it still perturbs the digest rather than vanishing.
                h = FnvUInt(h, entry.TryGetDword("ObjectId", out uint id) ? id : 0xFFFFFFFF);

                entry.TryGetString("TemplateResRef", out string resref);
                h = FnvString(h, resref.ToLowerInvariant());
            }
        }

        return h;
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
            ((string, bool)[])[(".rim", true), ("_s.rim", true), (".mod", false), ("_dlg.erf", false)])
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
/// Session-lived cache over <see cref="SaveDigest"/> for the bridge: the identity is
/// computed once (the staged save exists before the world does, or never will), and each
/// module's GIT digest once per module — the pump asks every heavy tick, and re-reading
/// archives from disk at 5 Hz would be pure waste. Failures latch as 0 for the same
/// reason: a save that is unreadable now will not become readable next tick, and the
/// soft-fail rule says unknown only ever warns.
/// </summary>
public sealed class SaveDigestCache(string gameDirectory)
{
    private readonly Dictionary<string, uint> _moduleGit = new(StringComparer.OrdinalIgnoreCase);
    private bool _saveResolved;
    private uint _saveIdentity;
    private string? _saveDirectory;

    /// <summary>The staged/current save's identity digest, 0 when unknown.</summary>
    public uint SaveIdentity
    {
        get
        {
            EnsureSaveResolved();
            return _saveIdentity;
        }
    }

    /// <summary>The named module's GIT digest, 0 when unknown.</summary>
    public uint ModuleGit(string module)
    {
        if (string.IsNullOrEmpty(module))
        {
            return 0;
        }

        EnsureSaveResolved();

        if (!_moduleGit.TryGetValue(module, out uint digest))
        {
            digest = SaveDigest.TryComputeModuleGit(gameDirectory, _saveDirectory, module, out uint computed)
                ? computed
                : 0;
            _moduleGit[module] = digest;
        }

        return digest;
    }

    private void EnsureSaveResolved()
    {
        if (_saveResolved)
        {
            return;
        }

        _saveResolved = true;
        _saveDirectory = SaveDigest.FindSessionSave(gameDirectory);

        if (_saveDirectory is not null
            && SaveDigest.TryComputeSaveIdentity(_saveDirectory, out uint digest))
        {
            _saveIdentity = digest;
        }
    }
}
