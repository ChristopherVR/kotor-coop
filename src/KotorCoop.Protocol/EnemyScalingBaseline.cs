using System.Text;

namespace KotorCoop.Protocol;

/// <summary>Exact save identity used to bind a health-baseline sidecar. Slot is the selected
/// save slot, or <see cref="EnemyScalingSaveSlot.NoSaveSlot"/> for a direct-directory load; in that case Name is
/// the exact directory name. The content hash protects against revisions of the same slot,
/// while these fields independently guard against binding to the wrong selected save.</summary>
public sealed record EnemyScalingSaveKey(int Slot, string Name, uint SaveIdentity);

/// <summary>No numeric save slot was present in a direct-directory load request.</summary>
public static class EnemyScalingSaveSlot
{
    public const int NoSaveSlot = int.MinValue;
}

/// <summary>One saved creature's unscaled maximum and the percentage already represented in
/// the archive's stored maximum.</summary>
public sealed record EnemyScalingBaselineRecord(
    string Module,
    uint ObjectId,
    string Tag,
    string Template,
    int BaseMaximum,
    int AppliedPercent);

/// <summary>Schema for the mod-owned enemy-health baseline sidecar. The archive SHA-256 is
/// calculated from the settled SAVEGAME.sav when the store writes this document.
///
/// Schema 2 adds <see cref="CoveredModules"/>: the saved modules whose COMPLETE scaled set
/// the records describe. A covered module with no records is proof that none of its saved
/// creatures carries a scaled maximum; a saved module absent from the list is unknown and
/// fails closed on load. A null list (never written by this build) means "the modules the
/// records name".</summary>
public sealed record EnemyScalingBaselineDocument(
    int SchemaVersion,
    int SaveSlot,
    string SaveName,
    uint SaveIdentity,
    string ArchiveSha256,
    IReadOnlyList<EnemyScalingBaselineRecord> Records,
    IReadOnlyList<string>? CoveredModules = null)
{
    public const int CurrentSchemaVersion = 2;
    public const int MaximumCoveredModules = 256;
    public const int MaximumRecords = 16_384;
    public const int MaximumSaveNameLength = 128;
    public const int MaximumModuleLength = 32;
    public const int MaximumTagLength = 64;
    public const int MaximumTemplateLength = 16;
}

/// <summary>Fail-closed validation and baseline selection, kept free of filesystem and engine
/// access so the identity and no-compounding rules can be tested directly.</summary>
public static class EnemyScalingBaselineRule
{
    public const int MinimumPercent = 50;
    public const int MaximumPercent = 400;
    public const uint InvalidObjectId = 0x7F00_0000;
    public const uint PlayerObjectId = 0x7FFF_FFFF;

    public static bool IsValidDocument(EnemyScalingBaselineDocument? document, bool requireHash)
    {
        if (document is null
            || document.SchemaVersion != EnemyScalingBaselineDocument.CurrentSchemaVersion
            || document.SaveIdentity == 0
            || !IsValidSaveSlot(document.SaveSlot)
            || !IsValidName(document.SaveName, EnemyScalingBaselineDocument.MaximumSaveNameLength)
            || document.Records is null
            || document.Records.Count > EnemyScalingBaselineDocument.MaximumRecords
            || (requireHash && !IsValidHash(document.ArchiveSha256)))
        {
            return false;
        }

        HashSet<string>? covered = null;
        if (document.CoveredModules is not null)
        {
            if (document.CoveredModules.Count > EnemyScalingBaselineDocument.MaximumCoveredModules)
            {
                return false;
            }

            covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string? module in document.CoveredModules)
            {
                if (!IsValidResRef(module, EnemyScalingBaselineDocument.MaximumModuleLength) || !covered.Add(module!))
                {
                    return false;
                }
            }
        }

        HashSet<(string Module, uint ObjectId)> identities = [];
        foreach (EnemyScalingBaselineRecord? record in document.Records)
        {
            if (!IsValidRecord(record)
                || !identities.Add((record!.Module.ToLowerInvariant(), record.ObjectId))
                || (covered is not null && !covered.Contains(record.Module)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The modules a valid document proves complete: the explicit schema-2 list, or
    /// the modules its records name when the list is absent.</summary>
    public static IReadOnlySet<string> CoveredModules(EnemyScalingBaselineDocument document)
    {
        HashSet<string> modules = new(StringComparer.OrdinalIgnoreCase);
        if (document.CoveredModules is not null)
        {
            modules.UnionWith(document.CoveredModules);
        }
        else
        {
            foreach (EnemyScalingBaselineRecord record in document.Records)
            {
                modules.Add(record.Module);
            }
        }

        return modules;
    }

    public static bool IsValidRecord(EnemyScalingBaselineRecord? record) =>
        record is not null
        && IsValidResRef(record.Module, EnemyScalingBaselineDocument.MaximumModuleLength)
        && record.ObjectId is > 0 and not InvalidObjectId and not PlayerObjectId and not uint.MaxValue
        && IsValidName(record.Tag, EnemyScalingBaselineDocument.MaximumTagLength)
        && IsValidTemplate(record.Template)
        && record.BaseMaximum is > 0 and <= ushort.MaxValue
        && record.AppliedPercent is >= MinimumPercent and <= MaximumPercent;

    /// <summary>Returns a baseline only when archive bytes, selected-save identity, creature
    /// identity, and the saved maximum all agree. The last check prevents a malformed or
    /// stale record from being treated as proof that an already-scaled max has this baseline.</summary>
    public static bool TrySelect(
        EnemyScalingBaselineDocument document,
        EnemyScalingSaveKey selectedSave,
        string archiveSha256,
        string module,
        uint objectId,
        string tag,
        string template,
        int storedMaximum,
        out EnemyScalingBaselineRecord? baseline)
    {
        baseline = null;
        if (!IsValidDocument(document, requireHash: true)
            || !IsValidSaveKey(selectedSave)
            || !IsValidHash(archiveSha256)
            || !string.Equals(document.ArchiveSha256, archiveSha256, StringComparison.Ordinal)
            || document.SaveSlot != selectedSave.Slot
            || document.SaveIdentity != selectedSave.SaveIdentity
            || !string.Equals(document.SaveName, selectedSave.Name, StringComparison.Ordinal)
            || !IsValidResRef(module, EnemyScalingBaselineDocument.MaximumModuleLength)
            || objectId is 0 or InvalidObjectId or PlayerObjectId or uint.MaxValue
            || !IsValidName(tag, EnemyScalingBaselineDocument.MaximumTagLength)
            || !IsValidTemplate(template))
        {
            return false;
        }

        EnemyScalingBaselineRecord? match = null;
        foreach (EnemyScalingBaselineRecord record in document.Records)
        {
            if (record.ObjectId != objectId
                || !string.Equals(record.Module, module, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(record.Tag, tag, StringComparison.Ordinal)
                || !string.Equals(record.Template, template, StringComparison.OrdinalIgnoreCase)
                || match is not null)
            {
                return false;
            }

            match = record;
        }

        if (match is null || ScaleMaximum(match.BaseMaximum, match.AppliedPercent) != storedMaximum)
        {
            return false;
        }

        baseline = match;
        return true;
    }

    /// <summary>Scales from the preserved vanilla baseline with the same half-up rounding
    /// and ushort ceiling as <c>EnemyScalingRule.ScaledHitPoints</c> in the native plugin.
    /// Repeated selection at the saved percentage therefore returns the already-stored
    /// maximum instead of multiplying it.</summary>
    public static int ScaleMaximum(int baseMaximum, int percent)
    {
        if (baseMaximum is <= 0 or > ushort.MaxValue || percent is < MinimumPercent or > MaximumPercent)
        {
            return 0;
        }

        long scaled = ((long)baseMaximum * percent + 50) / 100;
        return (int)Math.Clamp(scaled, 1, ushort.MaxValue);
    }

    public static bool IsValidSaveKey(EnemyScalingSaveKey? key) =>
        key is not null
        && IsValidSaveSlot(key.Slot)
        && key.SaveIdentity != 0
        && IsValidName(key.Name, EnemyScalingBaselineDocument.MaximumSaveNameLength);

    public static bool IsValidHash(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidSaveSlot(int slot) => slot >= -1 || slot == EnemyScalingSaveSlot.NoSaveSlot;

    public static bool IsValidModule(string? value) =>
        IsValidResRef(value, EnemyScalingBaselineDocument.MaximumModuleLength);

    public static bool IsValidTag(string? value) =>
        IsValidName(value, EnemyScalingBaselineDocument.MaximumTagLength);

    /// <summary>A saved creature's GIT record carries no <c>TemplateResRef</c> (0 of 3,221
    /// creature records across 40 GOG saves, 2026-09-24), so the template is optional: empty,
    /// or a well-formed resref when a record does name one. Object id plus Tag stay the
    /// identity.</summary>
    public static bool IsValidTemplate(string? value) =>
        value is not null
        && (value.Length == 0 || IsValidResRef(value, EnemyScalingBaselineDocument.MaximumTemplateLength));

    private static bool IsValidResRef(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length > maxLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidName(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (char.IsControl(c))
            {
                return false;
            }
        }

        return Encoding.UTF8.GetByteCount(value) <= maxLength * 4;
    }
}
