using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>
/// docs/research/personal-inventory.md's "a rejoin duplicates the host's purse and backpack"
/// fix: a pure save-file transform run at adoption STAGING time, right after
/// <see cref="PersonalPazaak"/> restores the personal-card overlay onto the freshly-staged
/// world — the same place the character is transplanted in, <see cref="WorldAdoption"/>.
///
/// THE BUG THIS FIXES. A live, in-party PC's "backpack" IS the party's shared repository
/// (<c>CSWPartyTable::GetPartyItems()</c>), never a private per-creature store, and party gold
/// is the same shared field (<c>CSWPartyTable+0xFC</c>) — docs/research/key-items-and-solo.md
/// §1, itself a full disassembly of <c>CSWSCreature::GetItemRepository</c>/<c>GetGold</c>
/// (<c>src/KotorCoop.Plugin/Odyssey.Store.cs</c>). On disk that is
/// <c>SAVEGAME.sav -&gt; INVENTORY.res -&gt; ItemList</c> (the party stash) and
/// <c>PARTYTABLE.res -&gt; PT_GOLD</c> (the party purse) — NEITHER of which
/// <c>PlayerIfo.TryExtractPlayer</c>/<c>BuildPifo</c> ever reads: they copy only
/// <c>Mod_PlayerList[0]</c> (worn gear, stats, alignment), because loose items and credits
/// live at the WORLD level, not the character level. Adoption stages the HOST's save
/// verbatim, so every rejoin under <c>inventory_mode</c>/<c>credits_mode = personal</c> used
/// to hand the joiner a full copy of the host's purse and backpack — duplication, not a
/// mirror, because <c>party_pool_sync=2</c>/the credits ledger only ever mirror LIVE engines;
/// staging a save once at adoption time is outside either channel's reach.
///
/// THE FIX. Overwrite the STAGED world's own <c>PARTYTABLE.res</c>/<c>INVENTORY.res</c> so the
/// character logging in reads:
/// <list type="bullet">
/// <item>gold = the joiner's OWN gold as of their latest co-op capture — the dedicated Co-op
/// save slot (<see cref="CoopSaveSlot"/>; <c>ClientCharacterPersistence</c>'s periodic/
/// disconnect/chargen captures have all landed there since 2026-09-25,
/// docs/research/session-save.md). 0 when no such capture exists yet (a true first join) —
/// never the host's purse.</item>
/// <item>backpack = the joiner's own NON-quest items from that SAME capture, PLUS the host
/// world's QUEST items (<see cref="QuestItemCatalog"/> — the engine's own Plot flag, present
/// on every item struct in a save's <c>INVENTORY.res</c>, OR a census tag/resref). The host's
/// own non-quest items never appear. With no own capture, host quest items only.</item>
/// </list>
/// Equipped gear is untouched by any of this — it lives on
/// <c>Mod_PlayerList[0].Equip_ItemList</c>, a different resource this class never reads, so
/// nothing here can double it.
///
/// ⚠ ORDERING. <see cref="WorldAdoption"/> must call this BEFORE
/// <c>CoopSaveRedirect.MirrorIntoCoopSlot</c>, which runs later in the SAME adoption and
/// overwrites the dedicated Co-op slot with a copy of the just-staged (host) world — reading
/// the joiner's own capture happens here, first, or there would be nothing left of it to read.
///
/// A pure no-op, in EITHER direction independently, when the matching mode is <c>shared</c>:
/// the staged gold/backpack are left byte-for-byte as the host sent them, exactly today's
/// (pre-2026-09-25) behaviour — the explicit regression guarantee this feature's own design
/// doc asks for.
/// </summary>
public static class PersonalInventoryGraft
{
    /// <summary>ResourceTypes "res" — the container type id both <c>INVENTORY.res</c> (inside
    /// <c>SAVEGAME.sav</c>) and <c>PARTYTABLE.res</c> (a loose file beside it) share.</summary>
    private const int ResType = 0;

    /// <summary>
    /// Applies the transform to <paramref name="stagedSaveDirectory"/> (the autosave slot
    /// world adoption just staged). Does nothing when both modes are <c>shared</c> — the
    /// common case, and the one every pre-existing caller/test gets by not passing either flag.
    /// </summary>
    /// <param name="gameDirectory">This install's game directory — where <c>saves\</c>,
    /// and inside it the dedicated Co-op slot holding the joiner's own latest capture, live.</param>
    /// <param name="stagedSaveDirectory">The just-staged (host) world, about to be logged
    /// into — <c>saves\000001 - AUTOSAVE</c> in production.</param>
    public static void Apply(
        string gameDirectory,
        string stagedSaveDirectory,
        bool inventoryPersonal,
        bool creditsPersonal,
        Action<string>? log)
    {
        if (!inventoryPersonal && !creditsPersonal)
        {
            return;
        }

        string? ownCapture = FindOwnCaptureDirectory(gameDirectory, stagedSaveDirectory);

        if (creditsPersonal)
        {
            ApplyGold(stagedSaveDirectory, ownCapture, log);
        }

        if (inventoryPersonal)
        {
            ApplyInventory(stagedSaveDirectory, ownCapture, log);
        }
    }

    // -- gold ----------------------------------------------------------------------

    private static void ApplyGold(string stagedSaveDirectory, string? ownCapture, Action<string>? log)
    {
        uint gold = 0;
        string source = "0 (no earlier co-op capture on this install)";

        if (ownCapture is not null && TryReadPartyGold(ownCapture, out uint ownGold))
        {
            gold = ownGold;
            source = $"your own {gold} (from '{Path.GetFileName(ownCapture)}')";
        }

        string tablePath = Path.Combine(stagedSaveDirectory, "PARTYTABLE.res");
        GffStruct table = Gff.Parse(File.ReadAllBytes(tablePath)).Root;
        GffWriter writer = new("PT");
        GffCopy.CopyInto(table, writer.Root);
        writer.Root.SetDword("PT_GOLD", gold);
        WriteAtomic(tablePath, writer.ToBytes());

        log?.Invoke($"world adoption: credits_mode = personal - party gold set to {source}, not the host's");
    }

    private static bool TryReadPartyGold(string saveDirectory, out uint gold)
    {
        gold = 0;
        string path = Path.Combine(saveDirectory, "PARTYTABLE.res");
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            GffStruct table = Gff.Parse(File.ReadAllBytes(path)).Root;
            return table.TryGetDword("PT_GOLD", out gold);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            gold = 0;
            return false;
        }
    }

    // -- backpack --------------------------------------------------------------------

    private static void ApplyInventory(string stagedSaveDirectory, string? ownCapture, Action<string>? log)
    {
        string savegamePath = Path.Combine(stagedSaveDirectory, "SAVEGAME.sav");
        if (!File.Exists(savegamePath))
        {
            return; // Nothing this class can do without the file WorldTransfer just wrote.
        }

        byte[] erf = File.ReadAllBytes(savegamePath);
        IReadOnlyList<GffStruct> hostItems = ReadInventoryItems(erf);
        List<GffStruct> questItems = [];
        int hostNonQuestCount = 0;
        foreach (GffStruct item in hostItems)
        {
            if (IsQuestItem(item))
            {
                questItems.Add(item);
            }
            else
            {
                hostNonQuestCount++;
            }
        }

        List<GffStruct> ownNonQuestItems = [];
        if (ownCapture is not null && TryReadInventoryFile(ownCapture, out byte[] ownErf))
        {
            foreach (GffStruct item in ReadInventoryItems(ownErf))
            {
                if (!IsQuestItem(item))
                {
                    ownNonQuestItems.Add(item);
                }
            }
        }

        GffWriter inventory = new("INV ");
        GffWriterList list = inventory.Root.AddList("ItemList");
        foreach (GffStruct item in ownNonQuestItems)
        {
            GffCopy.CopyInto(item, list.Add(item.StructId));
        }

        foreach (GffStruct item in questItems)
        {
            GffCopy.CopyInto(item, list.Add(item.StructId));
        }

        byte[] updated = ErfWriter.ReplaceMember(erf, "INVENTORY", ResType, inventory.ToBytes());
        WriteAtomic(savegamePath, updated);

        log?.Invoke(
            "world adoption: inventory_mode = personal - backpack set to your own "
            + $"{ownNonQuestItems.Count} item(s) plus {questItems.Count} shared quest item(s) from the host "
            + $"(the host's own {hostNonQuestCount} item(s) do not carry over)");
    }

    private static bool TryReadInventoryFile(string saveDirectory, out byte[] erf)
    {
        erf = [];
        string path = Path.Combine(saveDirectory, "SAVEGAME.sav");
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            erf = File.ReadAllBytes(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            erf = [];
            return false;
        }
    }

    private static IReadOnlyList<GffStruct> ReadInventoryItems(byte[] erf)
    {
        try
        {
            return ErfArchive.TryFindMember(erf, ResType, "INVENTORY", out byte[] data)
                ? Gff.Parse(data).Root.GetList("ItemList")
                : [];
        }
        catch (InvalidDataException)
        {
            return [];
        }
    }

    /// <summary>The engine's own Plot flag (present on every item struct a save's
    /// <c>INVENTORY.res</c> carries — unlike the LIVE party-pool wire key, which has no room
    /// for it, docs/research/personal-inventory.md §2.1) OR a census tag/resref match. Strictly
    /// MORE reliable than the live channel's own resref-only gate, because the save-file struct
    /// hands us the flag directly.</summary>
    private static bool IsQuestItem(GffStruct item)
    {
        bool plot = item.TryGetByte("Plot", out byte plotByte) && plotByte != 0;
        item.TryGetString("Tag", out string tag);
        return QuestItemCatalog.IsQuestItem(plot, tag);
    }

    // -- locating the joiner's own capture --------------------------------------------

    private static string? FindOwnCaptureDirectory(string gameDirectory, string stagedSaveDirectory)
    {
        string savesDirectory = Path.Combine(gameDirectory, "saves");
        if (!Directory.Exists(savesDirectory))
        {
            return null;
        }

        string? name = Array.Find(
            Directory.GetDirectories(savesDirectory),
            full => CoopSaveSlot.IsCoopSlot(Path.GetFileName(full)));

        if (name is null)
        {
            return null;
        }

        // Never treat the staging TARGET as if it were "our own" — cannot happen in production
        // (the Co-op slot and the autosave slot are always distinct folders), guarded anyway
        // since nothing downstream should ever read a file it is about to overwrite.
        return string.Equals(
            Path.GetFullPath(name), Path.GetFullPath(stagedSaveDirectory), StringComparison.OrdinalIgnoreCase)
            ? null
            : name;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temporary = path + ".kcoop.tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
