using System.Buffers.Binary;
using System.Security.Cryptography;
using KotorCoop.Memory.GameFiles;

namespace KotorCoop.Memory;

/// <summary>One saved creature as the archive stores it. <see cref="HitPoints"/> is the GIT
/// creature's <c>HitPoints</c> SHORT, which is exactly the live stored maximum
/// <c>CSWSObject+0xE0</c>: <c>CSWSCreatureStats::SaveStats</c> writes it from
/// <c>movsx eax, word [creature+0xE0]</c> (0x5B1FCD..0x5B1FDD) and
/// <c>ReadStatsFromGff</c> reads it straight back into +0xE0 (0x5B1264..0x5B127B). The
/// separate <c>MaxHitPoints</c> field is the derived <c>GetMaxHitPoints(1)</c> and is not
/// used. -1 = the field was absent or not an integer.</summary>
public sealed record EnemyScalingArchiveCreature(uint ObjectId, string Tag, string Template, int HitPoints);

/// <summary>One module's saved creature table, keyed by the saved ObjectId (the id the
/// engine restores on load). Null <see cref="Creatures"/> = the module is present in the
/// archive but its state is unreadable or ambiguous (duplicate member, missing GIT,
/// duplicate or missing ObjectId): nothing about it may be proven.</summary>
public sealed record EnemyScalingArchiveModule(
    string Module,
    IReadOnlyDictionary<uint, EnemyScalingArchiveCreature>? Creatures);

/// <summary>The exact bytes of one SAVEGAME.sav, their SHA-256, and every saved module.</summary>
public sealed record EnemyScalingArchiveContents(
    string Sha256,
    long Length,
    IReadOnlyDictionary<string, EnemyScalingArchiveModule> Modules);

/// <summary>
/// Reads the creature identities an enemy-health baseline must be joined against, from one
/// in-memory copy of <c>SAVEGAME.sav</c> whose hash is returned alongside, so the join and
/// the sidecar's archive binding can never describe two different sets of bytes. Read-only
/// and bounds-checked; a hostile or truncated archive reads as "not readable".
/// </summary>
public static class EnemyScalingArchiveReader
{
    public const long MaximumArchiveBytes = 64L * 1024 * 1024;

    private const int GitType = 2023;
    private const int ModuleSavTypeThisBuild = 2057;
    private const int ModuleSavTypeClassic = 2055;

    public static bool TryRead(string saveDirectory, out EnemyScalingArchiveContents? contents, out string diagnostic)
    {
        contents = null;
        try
        {
            string path = Path.Combine(saveDirectory, "SAVEGAME.sav");
            if (!File.Exists(path))
            {
                diagnostic = "no SAVEGAME.sav";
                return false;
            }

            byte[] bytes;
            using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length is <= 0 or > MaximumArchiveBytes)
                {
                    diagnostic = "archive empty or outside size limit";
                    return false;
                }

                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }

            return TryParse(bytes, out contents, out diagnostic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Security.SecurityException or ArgumentException)
        {
            diagnostic = $"archive unreadable ({ex.GetType().Name})";
            return false;
        }
    }

    public static bool TryParse(byte[] bytes, out EnemyScalingArchiveContents? contents, out string diagnostic)
    {
        contents = null;
        if (!TryListMembers(bytes, out List<(string Resref, int Type, int Offset, int Size)> members))
        {
            diagnostic = "archive is not a readable ERF";
            return false;
        }

        Dictionary<string, EnemyScalingArchiveModule> modules = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> duplicated = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string resref, int type, int offset, int size) in members)
        {
            if (type is not (ModuleSavTypeThisBuild or ModuleSavTypeClassic) || resref.Length == 0)
            {
                continue;
            }

            if (modules.ContainsKey(resref))
            {
                duplicated.Add(resref);
                continue;
            }

            modules[resref] = new EnemyScalingArchiveModule(
                resref, ReadModule(bytes.AsSpan(offset, size).ToArray()));
        }

        foreach (string module in duplicated)
        {
            // Two members for one module make the saved state ambiguous.
            modules[module] = new EnemyScalingArchiveModule(module, null);
        }

        contents = new EnemyScalingArchiveContents(
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes.LongLength, modules);
        diagnostic = "read";
        return true;
    }

    private static IReadOnlyDictionary<uint, EnemyScalingArchiveCreature>? ReadModule(byte[] inner)
    {
        try
        {
            if (!ErfArchive.TryFindMember(inner, GitType, resref: null, out byte[] gitBytes))
            {
                return null;
            }

            Gff git = Gff.Parse(gitBytes);
            Dictionary<uint, EnemyScalingArchiveCreature> creatures = [];
            foreach (GffStruct entry in git.Root.GetList("Creature List"))
            {
                if (!entry.TryGetDword("ObjectId", out uint id) || id == uint.MaxValue || creatures.ContainsKey(id))
                {
                    return null;
                }

                entry.TryGetString("Tag", out string tag);
                entry.TryGetString("TemplateResRef", out string template);
                int hitPoints = entry.TryGetShort("HitPoints", out short hp) ? hp : -1;
                creatures.Add(id, new EnemyScalingArchiveCreature(id, tag, template, hitPoints));
            }

            return creatures;
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IndexOutOfRangeException
            or OverflowException)
        {
            return null;
        }
    }

    private static bool TryListMembers(byte[] erf, out List<(string Resref, int Type, int Offset, int Size)> members)
    {
        members = [];
        if (erf.Length < 160)
        {
            return false;
        }

        string signature = System.Text.Encoding.ASCII.GetString(erf, 0, 4);
        string version = System.Text.Encoding.ASCII.GetString(erf, 4, 4);
        if (version != "V1.0" || signature.Trim().Length == 0)
        {
            return false;
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(erf.AsSpan(0x10, 4));
        uint keysOffset = BinaryPrimitives.ReadUInt32LittleEndian(erf.AsSpan(0x18, 4));
        uint valuesOffset = BinaryPrimitives.ReadUInt32LittleEndian(erf.AsSpan(0x1C, 4));
        if (count > 4096)
        {
            return false;
        }

        for (uint i = 0; i < count; i++)
        {
            long key = keysOffset + (24L * i);
            long value = valuesOffset + (8L * i);
            if (key + 24 > erf.Length || value + 8 > erf.Length)
            {
                return false;
            }

            int type = erf[key + 20] | (erf[key + 21] << 8);
            int nul = Array.IndexOf(erf, (byte)0, (int)key, 16);
            int length = nul < 0 ? 16 : nul - (int)key;
            string resref = System.Text.Encoding.Latin1.GetString(erf, (int)key, length);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(erf.AsSpan((int)value, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(erf.AsSpan((int)value + 4, 4));
            if ((long)offset + size > erf.Length)
            {
                return false;
            }

            members.Add((resref, type, (int)offset, (int)size));
        }

        return true;
    }
}
