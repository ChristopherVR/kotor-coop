using System.Buffers.Binary;
using System.Text;

namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// Sideways copies of the dialogue stunt tracks the player is played through, for the co-op
/// stand-in (docs/research/star-forge-finale.md §7).
///
/// A cutscene conversation's StuntList hands "PLAYER" a stunt model (STUNT_55a's ceremony:
/// m44ac_c01_char02) whose animations carry the body through the scene. The body is drawn where
/// the track says, in the scene's own frame, wherever its object stands: the stand-in given the
/// player's own track was drawn exactly inside the player (live 2026-10-10, staged behind and
/// then beside the player, the same overlap both times). Each animation places the body through
/// one node, <c>cutscenedummy</c>; a copy with that node's position keys moved sideways draws the
/// stand-in next to the player through the whole scene, which is how Dark Sacrifice gives Carth
/// his own track (stunt_sacrifice.mod's sacrifice_char02).
///
/// The game finds a model at the moment a scene asks for it, but its override folder is read
/// at launch, so the copies are written ahead of time: every start, for the next one.
/// </summary>
public static class StuntStandInModels
{
    /// <summary>How far the stand-in's copy is moved along the track's own X axis: a body's width
    /// and a gap. Live on STUNT_55a's balcony this put the stand-in between the player and
    /// Bastila. Variant 1 (a second peer) goes the other way.</summary>
    public const float SideOffset = 1.2f;

    /// <summary>Copies made per stunt: one per stand-in a session can show beside the player.</summary>
    public const int Variants = 2;

    private const int ResourceTypeMdl = 2002;
    private const int ResourceTypeMdx = 3008;
    private const int ResourceTypeDlg = 2029;

    /// <summary>The copy's resref: "kcs", the variant, and a hash of the stunt's own resref, so
    /// the plugin can name it from the resref alone. Must match the plugin's
    /// StuntStandInName.For (a test compares the two).</summary>
    public static string NameFor(string stuntResref, int variant)
    {
        uint hash = 2166136261;
        foreach (char c in stuntResref.ToLowerInvariant())
        {
            hash = (hash ^ (byte)c) * 16777619;
        }

        return $"kcs{variant}_{hash:x8}";
    }

    /// <summary>The sideways offset for <paramref name="variant"/>.</summary>
    public static float OffsetFor(int variant) => variant % 2 == 0 ? SideOffset : -SideOffset;

    /// <summary>
    /// A copy of a binary KOTOR model with every animation's <c>cutscenedummy</c> position keys
    /// moved by (<paramref name="dx"/>, <paramref name="dy"/>) and the model renamed. False when
    /// the bytes are not a model this understands, carry no such node, or the new name does not
    /// fit where the old one was.
    /// </summary>
    public static bool TryShift(byte[] mdl, string newName, float dx, float dy, out byte[] shifted)
    {
        shifted = [];
        try
        {
            byte[] d = (byte[])mdl.Clone();
            ModelReader r = new(d);
            (uint namesOffset, uint namesCount) = (r.U32(184), r.U32(188));
            string[] names = new string[namesCount];
            for (uint i = 0; i < namesCount; i++)
            {
                names[i] = r.CString(r.U32(namesOffset + (4 * i)));
            }

            int moved = 0;
            (uint animsOffset, uint animsCount) = (r.U32(88), r.U32(92));
            byte[] nameBytes = Encoding.ASCII.GetBytes(newName);
            if (nameBytes.Length > 31)
            {
                return false;
            }

            for (uint a = 0; a < animsCount; a++)
            {
                uint anim = r.U32(animsOffset + (4 * a));
                moved += ShiftNode(r, names, r.U32(anim + 40), dx, dy, depth: 0);
                r.WriteFixed(anim + 88, nameBytes, 32);
            }

            if (moved == 0 || namesCount == 0)
            {
                return false;
            }

            // The model's own name, in the header and as name 0.
            r.WriteFixed(8, nameBytes, 32);
            uint first = r.U32(namesOffset);
            int oldLength = names[0].Length;
            if (nameBytes.Length > oldLength)
            {
                return false;
            }

            r.WriteFixed(first, nameBytes, oldLength);
            shifted = d;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
        {
            return false;
        }
    }

    private static int ShiftNode(ModelReader r, string[] names, uint node, float dx, float dy, int depth)
    {
        if (depth > 64)
        {
            throw new InvalidDataException("node tree too deep");
        }

        ushort nameIndex = r.U16(node + 4);
        (uint childrenOffset, uint childrenCount) = (r.U32(node + 44), r.U32(node + 48));
        (uint controllersOffset, uint controllersCount) = (r.U32(node + 56), r.U32(node + 60));
        uint dataOffset = r.U32(node + 68);
        int moved = 0;

        if (nameIndex < names.Length && string.Equals(names[nameIndex], "cutscenedummy", StringComparison.OrdinalIgnoreCase))
        {
            for (uint c = 0; c < controllersCount; c++)
            {
                uint controller = controllersOffset + (16 * c);
                if (r.U32(controller) != 8)
                {
                    continue; // position only
                }

                ushort rows = r.U16(controller + 6);
                ushort valueStart = r.U16(controller + 10);
                byte columns = r.U8(controller + 12);
                int stride = (columns & 0xF) * ((columns & 0x10) != 0 ? 3 : 1);
                for (int k = 0; k < rows; k++)
                {
                    uint at = dataOffset + (uint)(4 * (valueStart + (k * stride)));
                    r.WriteF32(at, r.F32(at) + dx);
                    r.WriteF32(at + 4, r.F32(at + 4) + dy);
                }

                moved++;
            }
        }

        for (uint i = 0; i < childrenCount; i++)
        {
            moved += ShiftNode(r, names, r.U32(childrenOffset + (4 * i)), dx, dy, depth + 1);
        }

        return moved;
    }

    /// <summary>
    /// Every stunt model a dialogue in this install hands the player ("PLAYER" in a StuntList),
    /// from the modules' archives and loose dialogue files in override.
    /// </summary>
    public static IReadOnlyList<string> FindPlayerStunts(string gameDirectory)
    {
        SortedSet<string> found = new(StringComparer.OrdinalIgnoreCase);
        using GameFiles files = new(gameDirectory);

        if (files.ModulesDirectory is string modules)
        {
            foreach (string path in Directory.EnumerateFiles(modules))
            {
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension is not (".rim" or ".mod"))
                {
                    continue;
                }

                try
                {
                    GameArchive archive = extension == ".rim" ? RimArchive.Open(path) : ErfArchive.Open(path);
                    foreach (ArchiveResource resource in archive.Resources)
                    {
                        if (resource.ResourceType == ResourceTypeDlg && archive.TryRead(resource.Resref, "dlg", out byte[] dlg))
                        {
                            AddPlayerStunts(dlg, found);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    // An unreadable archive contributes nothing.
                }
            }
        }

        string? overrides = FindDirectory(gameDirectory, "override");
        if (overrides is not null)
        {
            foreach (string path in Directory.EnumerateFiles(overrides, "*.dlg"))
            {
                try
                {
                    AddPlayerStunts(GameFileIo.ReadAllBytes(path), found);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        return [.. found];
    }

    private static void AddPlayerStunts(byte[] dlg, SortedSet<string> found)
    {
        try
        {
            foreach (GffStruct stunt in Gff.Parse(dlg).Root.GetList("StuntList"))
            {
                if (stunt.TryGetString("Participant", out string who)
                    && string.Equals(who.Trim(), "PLAYER", StringComparison.OrdinalIgnoreCase)
                    && stunt.TryGetResRef("StuntModel", out string model) && model.Length > 0)
                {
                    found.Add(model.ToLowerInvariant());
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // Not a dialogue this reader understands: nothing from it.
        }
    }

    /// <summary>
    /// Writes the missing or stale copies into override. Returns how many files were written;
    /// stunts whose model cannot be read or shifted are skipped.
    /// </summary>
    public static int Install(string gameDirectory, out int stunts)
    {
        IReadOnlyList<string> models = FindPlayerStunts(gameDirectory);
        stunts = models.Count;
        string overrides = FindDirectory(gameDirectory, "override")
            ?? Directory.CreateDirectory(Path.Combine(gameDirectory, "Override")).FullName;
        int written = 0;
        using GameFiles files = new(gameDirectory);

        foreach (string model in models)
        {
            if (!files.TryReadResource(model, "mdl", out byte[] mdl) || !files.TryReadResource(model, "mdx", out byte[] mdx))
            {
                continue;
            }

            for (int variant = 0; variant < Variants; variant++)
            {
                string name = NameFor(model, variant);
                if (!TryShift(mdl, name, OffsetFor(variant), 0f, out byte[] shifted))
                {
                    break;
                }

                written += WriteIfDifferent(Path.Combine(overrides, name + ".mdl"), shifted);
                written += WriteIfDifferent(Path.Combine(overrides, name + ".mdx"), mdx);
            }
        }

        return written;
    }

    /// <summary>Removes every copy this wrote. Returns how many files went.</summary>
    public static int Uninstall(string gameDirectory)
    {
        string? overrides = FindDirectory(gameDirectory, "override");
        if (overrides is null)
        {
            return 0;
        }

        int removed = 0;
        foreach (string path in Directory.EnumerateFiles(overrides, "kcs?_????????.md?"))
        {
            File.Delete(path);
            removed++;
        }

        return removed;
    }

    private static int WriteIfDifferent(string path, byte[] bytes)
    {
        if (File.Exists(path) && GameFileIo.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            return 0;
        }

        File.WriteAllBytes(path, bytes);
        return 1;
    }

    private static string? FindDirectory(string parent, string name)
    {
        try
        {
            return Directory.EnumerateDirectories(parent)
                .FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Offsets in a binary model are counted after its 12-byte file header.</summary>
    private sealed class ModelReader(byte[] data)
    {
        private const int Header = 12;

        private int At(uint offset, int size)
        {
            long at = Header + (long)offset;
            if (at < Header || at + size > data.Length)
            {
                throw new InvalidDataException("offset outside the model");
            }

            return (int)at;
        }

        public byte U8(uint offset) => data[At(offset, 1)];

        public ushort U16(uint offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(At(offset, 2)));

        public uint U32(uint offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(At(offset, 4)));

        public float F32(uint offset) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(At(offset, 4)));

        public void WriteF32(uint offset, float value) =>
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(At(offset, 4)), value);

        public string CString(uint offset)
        {
            int start = At(offset, 1);
            int end = Array.IndexOf(data, (byte)0, start);
            if (end < 0)
            {
                throw new InvalidDataException("unterminated name");
            }

            return Encoding.ASCII.GetString(data, start, end - start);
        }

        /// <summary>Writes <paramref name="text"/> into a <paramref name="width"/>-byte field,
        /// zero-padded.</summary>
        public void WriteFixed(uint offset, byte[] text, int width)
        {
            Span<byte> field = data.AsSpan(At(offset, width), width);
            field.Clear();
            text.AsSpan(0, Math.Min(text.Length, width)).CopyTo(field);
        }
    }
}
