using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The long-run key collision from docs/review-seamless-2026-09-18.md §2 / roadmap #47: door
/// and placeable keys used to fold the module name to one byte, so over a whole playthrough two
/// modules shared a byte with near certainty and a reused low object id fused their entries.
/// These tests build the keys for EVERY module the base game ships, with overlapping object
/// ids, and assert no two ever meet.
/// </summary>
public class ObjectKeyedFlagsTests
{
    /// <summary>Every module in a stock KOTOR install's <c>modules\</c> folder (the .rim /
    /// _s.rim pairs and the .mod files, de-duplicated and case-folded), listed from the rig's
    /// GOG install 2026-09-18. 117 names — every place a playthrough can load.</summary>
    public static readonly string[] ShippedModules =
    [
        "danm13", "danm14aa", "danm14ab", "danm14ac", "danm14ad", "danm14ae", "danm15", "danm16",
        "ebo_m12aa", "ebo_m40aa", "ebo_m40ad", "ebo_m41aa", "ebo_m46ab",
        "end_m01aa", "end_m01ab",
        "kas_m22aa", "kas_m22ab", "kas_m23aa", "kas_m23ab", "kas_m23ac", "kas_m23ad", "kas_m24aa", "kas_m25aa",
        "korr_m33aa", "korr_m33ab", "korr_m34aa", "korr_m35aa", "korr_m36aa", "korr_m37aa", "korr_m38aa",
        "korr_m38ab", "korr_m39aa",
        "lev_m40aa", "lev_m40ab", "lev_m40ac", "lev_m40ad", "liv_m99aa", "m12ab",
        "manm26aa", "manm26ab", "manm26ac", "manm26ad", "manm26ae", "manm26mg", "manm27aa", "manm28aa",
        "manm28ab", "manm28ac", "manm28ad",
        "sta_m45aa", "sta_m45ab", "sta_m45ac", "sta_m45ad",
        "stunt_00", "stunt_03a", "stunt_06", "stunt_07", "stunt_12", "stunt_14", "stunt_16", "stunt_18",
        "stunt_19", "stunt_31b", "stunt_34", "stunt_35", "stunt_42", "stunt_44", "stunt_50a", "stunt_51a",
        "stunt_54a", "stunt_55a", "stunt_56a", "stunt_57",
        "tar_m02aa", "tar_m02ab", "tar_m02ac", "tar_m02ad", "tar_m02ae", "tar_m02af", "tar_m03aa", "tar_m03ab",
        "tar_m03ad", "tar_m03ae", "tar_m03af", "tar_m03mg", "tar_m04aa", "tar_m05aa", "tar_m05ab", "tar_m08aa",
        "tar_m09aa", "tar_m09ab", "tar_m10aa", "tar_m10ab", "tar_m10ac", "tar_m11aa", "tar_m11ab",
        "tat_m17aa", "tat_m17ab", "tat_m17ac", "tat_m17ad", "tat_m17ae", "tat_m17af", "tat_m17ag", "tat_m17mg",
        "tat_m18aa", "tat_m18ab", "tat_m18ac", "tat_m20aa",
        "unk_m41aa", "unk_m41ab", "unk_m41ac", "unk_m41ad", "unk_m42aa", "unk_m43aa", "unk_m44aa", "unk_m44ab",
        "unk_m44ac",
    ];

    [Fact]
    public void TheShippedListIsWhatItClaimsToBe()
    {
        Assert.Equal(117, ShippedModules.Length);
        Assert.Equal(ShippedModules.Length, ShippedModules.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void TheOldEightBitFoldReallyDidCollideAcrossTheShippedModules()
    {
        // The bug being fixed, stated as a measurement rather than a probability: fold every
        // shipped module's hash to a byte the way the 32-bit key did and count the modules that
        // share one. (Pinning this keeps the fix honest — if the fold had been collision-free
        // over the real list, the widening would have been unnecessary.)
        HashSet<byte> seen = [];
        int collisions = 0;
        foreach (string module in ShippedModules)
        {
            uint hash = ObjectKeyedFlags.HashModule(module);
            byte folded = (byte)(hash ^ (hash >> 8) ^ (hash >> 16) ^ (hash >> 24));
            if (!seen.Add(folded))
            {
                collisions++;
            }
        }

        Assert.True(collisions > 0, "the 8-bit fold happened to be collision-free over the shipped list");
    }

    [Fact]
    public void NoTwoShippedModulesShareAFullModuleHash()
    {
        Dictionary<uint, string> byHash = [];
        foreach (string module in ShippedModules)
        {
            uint hash = ObjectKeyedFlags.HashModule(module);
            Assert.True(
                byHash.TryAdd(hash, module),
                $"{module} and {byHash.GetValueOrDefault(hash)} hash to the same 32-bit value");
        }
    }

    [Fact]
    public void NoDoorOrPlaceableKeyCollidesAcrossAWholePlaythrough()
    {
        // Every shipped module, every object id a module realistically assigns (the bridge
        // refuses ids above 0xFFFF; a big module has a few hundred objects), both channels:
        // 117 x 1024 x 2 keys, and every one of them must be distinct — from each other and
        // from every global-variable flag id.
        const uint MaxObjectId = 0x400;
        HashSet<ulong> keys = new(ShippedModules.Length * (int)MaxObjectId * 2);

        foreach (string module in ShippedModules)
        {
            uint hash = ObjectKeyedFlags.HashModule(module);
            for (uint objectId = 1; objectId <= MaxObjectId; objectId++)
            {
                ulong door = DoorFlags.MakeKey(hash, objectId);
                ulong placeable = PlaceableFlags.MakeKey(hash, objectId);
                Assert.True(keys.Add(door), $"door key collision at {module} / 0x{objectId:X}");
                Assert.True(keys.Add(placeable), $"placeable key collision at {module} / 0x{objectId:X}");
                Assert.Equal(hash, ObjectKeyedFlags.ModuleHashOf(door));
                Assert.Equal(objectId, ObjectKeyedFlags.ObjectIdOf(placeable));
            }
        }

        for (int index = 0; index < GlobalVariableTable.BooleanByteCount * 8; index++)
        {
            Assert.DoesNotContain(GlobalVariableTable.MakeFlagId(GlobalVariableType.Boolean, index), keys);
        }

        for (int index = 0; index < GlobalVariableTable.NumberCount; index++)
        {
            Assert.DoesNotContain(GlobalVariableTable.MakeFlagId(GlobalVariableType.Number, index), keys);
        }
    }

    [Fact]
    public void TheSameObjectIdInTwoModulesIsTwoStoreEntries()
    {
        // The scenario the review named: a chest searched on the Endar Spire must not mark a
        // chest with the same low id searched in a later module.
        EventFlagStore store = new();
        ulong spire = PlaceableFlags.MakeKey(PlaceableFlags.HashModule("end_m01aa"), 0x4A);
        ulong lowerCity = PlaceableFlags.MakeKey(PlaceableFlags.HashModule("tar_m03aa"), 0x4A);

        store.Set(spire, PlaceableFlags.MakeValue(open: true, unlocked: true));

        Assert.NotEqual(spire, lowerCity);
        Assert.False(store.TryGet(lowerCity, out _));
        Assert.True(store.TryGet(spire, out sbyte value));
        Assert.True(PlaceableFlags.OpenOf(value));
    }

    [Fact]
    public void BelongsToModuleSelectsOneModulesObjectKeysAndNeverAGlobal()
    {
        uint spire = ObjectKeyedFlags.HashModule("end_m01aa");
        uint city = ObjectKeyedFlags.HashModule("tar_m03aa");

        Assert.True(ObjectKeyedFlags.BelongsToModule(DoorFlags.MakeKey(spire, 1), spire));
        Assert.True(ObjectKeyedFlags.BelongsToModule(PlaceableFlags.MakeKey(spire, 1), spire));
        Assert.False(ObjectKeyedFlags.BelongsToModule(DoorFlags.MakeKey(city, 1), spire));

        // A global whose low bits happen to equal the module hash is still not object-keyed.
        Assert.False(ObjectKeyedFlags.BelongsToModule(spire, spire));
        Assert.False(ObjectKeyedFlags.BelongsToModule(GlobalVariableTable.MakeFlagId(GlobalVariableType.Number, 3), spire));
    }

    [Fact]
    public void PruneDropsOneModulesObjectKeysAndLeavesGlobalsAndTheClockAlone()
    {
        EventFlagStore store = new();
        uint spire = DoorFlags.HashModule("end_m01aa");
        uint city = DoorFlags.HashModule("tar_m03aa");
        ulong global = GlobalVariableTable.MakeFlagId(GlobalVariableType.Boolean, 77);

        store.Set(DoorFlags.MakeKey(spire, 0x4A), DoorFlags.MakeValue(2, true));
        store.Set(PlaceableFlags.MakeKey(spire, 0x51), PlaceableFlags.MakeValue(open: true, unlocked: false));
        store.Set(DoorFlags.MakeKey(city, 0x4A), DoorFlags.MakeValue(1, false));
        store.Set(global, 1);
        uint clockBefore = store.Version;

        int removed = store.Prune(key => ObjectKeyedFlags.BelongsToModule(key, spire));

        Assert.Equal(2, removed);
        Assert.Equal(2, store.Count);
        Assert.False(store.TryGet(DoorFlags.MakeKey(spire, 0x4A), out _));
        Assert.True(store.TryGet(DoorFlags.MakeKey(city, 0x4A), out _));
        Assert.True(store.TryGet(global, out sbyte g) && g == 1);
        Assert.Equal(clockBefore, store.Version);

        // The digest must describe the surviving content exactly: a store freshly built from
        // the same two entries agrees with it, so the resync tripwire does not fire forever.
        EventFlagStore rebuilt = new();
        rebuilt.Seed(store.Snapshot());
        Assert.Equal(rebuilt.Digest, store.Digest);
    }

    [Fact]
    public void PruneNeverLetsAStaleReplayLowerTheClock()
    {
        // Pruning removes entries, not history: a later local change still mints a serial
        // above everything ever observed, so a pruned key re-announced by a peer with an old
        // serial loses to a genuinely newer local write.
        EventFlagStore store = new();
        ulong key = DoorFlags.MakeKey(DoorFlags.HashModule("end_m01aa"), 0x4A);
        store.ApplyOp(new FlagOpPacket(OriginPeerId: 2, OpSequence: 1, FlagId: key, Value: 1, Serial: 40));
        store.Prune(_ => true);

        Assert.True(store.Set(key, 6));
        Assert.True(store.TryGetStamp(key, out uint serial, out _));
        Assert.True(serial > 40);
        Assert.False(store.ApplyOp(new FlagOpPacket(OriginPeerId: 2, OpSequence: 2, FlagId: key, Value: 1, Serial: 40)));
        Assert.True(store.TryGet(key, out sbyte value) && value == 6);
    }

    [Fact]
    public void AFullSnapshotPageOfWidenedEntriesFitsOneDatagram()
    {
        // MaxFlagEntriesPerPacket was re-derived for the 17-byte entry; pin the arithmetic
        // against the codec rather than trusting the constant's comment.
        List<FlagSnapshotEntry> page = [];
        for (int i = 0; i < ProtocolConstants.MaxFlagEntriesPerPacket; i++)
        {
            page.Add(new FlagSnapshotEntry(
                DoorFlags.MakeKey(0xFFFF_FFFF, (uint)(0xFFFF - i)), 7, uint.MaxValue, uint.MaxValue));
        }

        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new FlagSnapshotPacket(uint.MaxValue, page));

        Assert.True(length > 0);
        Assert.True(length <= ProtocolConstants.MaxPacketSize);
        Assert.True(length + 17 > ProtocolConstants.MaxPacketSize, "one more entry should not have fit");

        FlagSnapshotPacket? decoded = PacketCodec.ReadFlagSnapshot(buffer.AsSpan(0, length));
        Assert.NotNull(decoded);
        Assert.Equal(page, decoded.Flags);
    }

    [Fact]
    public void AFlagOpCarriesTheWholeSixtyFourBitKey()
    {
        ulong key = PlaceableFlags.MakeKey(PlaceableFlags.HashModule("unk_m44ac"), 0x3F1);
        Assert.True(key > uint.MaxValue);

        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        FlagOpPacket op = new(OriginPeerId: 2, OpSequence: 9, FlagId: key, Value: 3, Serial: 11);
        int length = PacketCodec.Write(buffer, op);

        Assert.Equal(ProtocolConstants.HeaderSize + 4 + 4 + 8 + 1 + 4, length);
        Assert.Equal(op, PacketCodec.ReadFlagOp(buffer.AsSpan(0, length)));
    }
}
