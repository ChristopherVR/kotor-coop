using System.Buffers.Binary;
using System.Text;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public static partial class PersonalInventoryGraft
{
    // The live loot relay exempts this per-engine locker. Adoption must respect the same
    // ownership: the host can have emptied theirs while our stripped gear is still inside.
    private static byte[] PreservePrivateLocker(
        string stagedDirectory, string? ownCapture, byte[] stagedErf,
        IReadOnlyList<GffStruct> sharedQuestItems, Action<string>? log,
        Func<string, bool>? verifiedOwnCapture)
    {
        ErfArchive staged = ErfArchive.Open(Path.Combine(stagedDirectory, "SAVEGAME.sav"));
        foreach (ArchiveResource module in staged.Resources)
        {
            if (module.ResourceType is not (2055 or 2057)
                || !ContainerLootExemptionRule.IsExempt(module.Resref, "lev40_ptylocker")) continue;
            stagedErf = PreservePrivateLockerModule(module, ownCapture, stagedErf,
                sharedQuestItems, log, verifiedOwnCapture);
        }
        return stagedErf;
    }

    private static byte[] PreservePrivateLockerModule(ArchiveResource module, string? ownCapture,
        byte[] stagedErf, IReadOnlyList<GffStruct> sharedQuestItems, Action<string>? log,
        Func<string, bool>? verifiedOwnCapture)
    {
        if (!ErfArchive.TryFindMember(stagedErf, module.ResourceType, module.Resref, out byte[] inner))
        {
            return stagedErf;
        }

        string gitName = UniqueGitName(inner);
        if (!ErfArchive.TryFindMember(inner, 2023, gitName, out byte[] gitBytes))
        {
            throw new InvalidDataException("private locker: staged module has no readable GIT");
        }
        GffStruct git = Gff.Parse(gitBytes).Root;
        IReadOnlyList<GffStruct> placeables = git.GetList("Placeable List");
        GffStruct[] hostLockers = placeables.Where(IsPrivateLocker).ToArray();
        if (hostLockers.Length == 0) return stagedErf;
        if (hostLockers.Length != 1)
            throw new InvalidDataException("private locker: ambiguous staged locker");

        GffStruct? ownLocker = null;
        if (ownCapture is not null)
        {
            ErfArchive own = ErfArchive.Open(Path.Combine(ownCapture, "SAVEGAME.sav"));
            ArchiveResource[] ownModules = own.Resources.Where(resource =>
                resource.ResourceType is 2055 or 2057
                && resource.Resref.Equals(module.Resref, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (ownModules.Length > 1)
                throw new InvalidDataException("private locker: ambiguous own module archive");
            if (ownModules.SingleOrDefault() is { } ownModule
                && own.TryRead(ownModule.Resref, ownModule.Extension, out byte[] ownInner))
            {
                if (verifiedOwnCapture is null || !verifiedOwnCapture(ownCapture))
                    throw new InvalidDataException("private locker: own save no longer matches a verified character capture; save your own character before reconnecting");
                string ownGitName = UniqueGitName(ownInner);
                if (!ErfArchive.TryFindMember(ownInner, 2023, ownGitName, out byte[] ownGitBytes))
                    throw new InvalidDataException("private locker: own module has no readable GIT");
                GffStruct[] ownLockers = Gff.Parse(ownGitBytes).Root.GetList("Placeable List")
                    .Where(IsPrivateLocker).ToArray();
                if (ownLockers.Length > 1)
                    throw new InvalidDataException("private locker: ambiguous own locker");
                ownLocker = ownLockers.SingleOrDefault();
            }
        }

        // A first join has no stripped personal gear in this world. Retain shared quest
        // items only. When there is an own locker, retain every item field and stack.
        // Quest copies already supplied in the shared backpack must not be returned twice.
        HashSet<string> sharedTags = new(StringComparer.OrdinalIgnoreCase);
        foreach (GffStruct item in sharedQuestItems)
            if (item.TryGetString("Tag", out string tag)) sharedTags.Add(tag);
        IReadOnlyList<GffStruct> source = (ownLocker ?? hostLockers[0]).GetList("ItemList");
        GffStruct[] kept = source.Where(item =>
            (ownLocker is not null || IsQuestItem(item))
            && !(IsQuestItem(item) && item.TryGetString("Tag", out string tag) && sharedTags.Contains(tag)))
            .ToArray();

        GffWriter rewritten = new("GIT ");
        GffCopy.CopyInto(git, rewritten.Root);
        GffWriterList output = rewritten.Root.AddList("Placeable List");
        foreach (GffStruct placeable in placeables)
        {
            GffWriterStruct copy = output.Add(placeable.StructId);
            GffCopy.CopyInto(placeable, copy);
            if (!IsPrivateLocker(placeable)) continue;
            GffWriterList items = copy.AddList("ItemList");
            foreach (GffStruct item in kept) GffCopy.CopyInto(item, items.Add(item.StructId));
        }
        byte[] updatedInner = ErfWriter.ReplaceMember(inner, gitName, 2023, rewritten.ToBytes());
        if (ownLocker is not null && (verifiedOwnCapture is null || !verifiedOwnCapture(ownCapture!)))
            throw new InvalidDataException("private locker: own save changed while its items were read; adoption held");
        log?.Invoke($"world adoption: private locker retained {kept.Length} item(s) from "
            + $"{(ownLocker is null ? "shared quest state" : "your own capture")}; shared backpack quest copies excluded");
        return ErfWriter.ReplaceMember(stagedErf, module.Resref, module.ResourceType, updatedInner);
    }

    private static bool IsPrivateLocker(GffStruct placeable) =>
        placeable.TryGetString("Tag", out string tag)
        && ContainerLootExemptionRule.IsExempt("lev_m40aa", tag);

    private static string UniqueGitName(byte[] inner)
    {
        if (inner.Length < 160) throw new InvalidDataException("private locker: truncated module");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(inner.AsSpan(16, 4));
        uint keys = BinaryPrimitives.ReadUInt32LittleEndian(inner.AsSpan(24, 4));
        if (count > 4096 || keys + 24L * count > inner.Length)
            throw new InvalidDataException("private locker: invalid module keys");
        string? name = null;
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> key = inner.AsSpan(checked((int)keys + 24 * i), 24);
            if (BinaryPrimitives.ReadUInt16LittleEndian(key[20..]) != 2023) continue;
            if (name is not null) throw new InvalidDataException("private locker: ambiguous module GIT");
            ReadOnlySpan<byte> text = key[..16];
            int end = text.IndexOf((byte)0);
            name = Encoding.ASCII.GetString(end < 0 ? text : text[..end]);
        }
        return name ?? throw new InvalidDataException("private locker: missing module GIT");
    }
}
