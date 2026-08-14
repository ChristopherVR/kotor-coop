namespace KotorCoop.Protocol;

/// <summary>
/// A peer's in-world content fingerprint — the facts about the running game that only
/// exist once a save is loaded, as opposed to the install-time <see cref="ContentDigest"/>
/// exchanged at Hello. Three fields, each answering one question the id-keyed channels
/// depend on:
///
/// <list type="bullet">
/// <item><see cref="GlobalsCatalogue"/> — do the two saves' (type, index) global-variable
/// keys name the same variables? (Mismatch silently fuses unrelated quest flags.)</item>
/// <item><see cref="SaveIdentity"/> — are the two peers playing the same save? Object ids
/// are only identical across instances because both engines dealt them from the same save
/// (same module, same Mod_NextObjId counters, same GIT id list), so this is the foundation
/// under enemies, doors, ground items and party sync.</item>
/// <item><see cref="Module"/> + <see cref="ModuleGit"/> — in the module both peers are
/// standing in NOW, does the GIT hand out the same ids? A mod that edits one module's
/// placements shifts ids there and nowhere else, so this is judged per module and
/// re-judged on every module change.</item>
/// </list>
///
/// Every field follows the house convention: 0 (or an empty module) means "not resolved",
/// and per the Unknown-is-not-mismatch rule an unresolved field can never degrade
/// anything — only two peers that both computed a value can disagree.
/// </summary>
public readonly record struct WorldDigest(
    uint GlobalsCatalogue,
    uint SaveIdentity,
    string Module,
    uint ModuleGit)
{
    public static WorldDigest Empty => new(0, 0, string.Empty, 0);

    /// <summary>True when nothing here is worth putting on the wire yet.</summary>
    public bool IsEmpty => GlobalsCatalogue == 0 && SaveIdentity == 0 && ModuleGit == 0;

    /// <summary>
    /// Judges the three in-world mismatches, identically on host and client, for
    /// <see cref="ContentCompatibility.ApplyWorldMismatch"/>. A field is a mismatch only
    /// when BOTH sides resolved it and the values differ; the module GIT additionally
    /// requires both peers to be reporting the SAME module — while one peer is mid-transit
    /// the digests describe different areas and comparing them would flag every ordinary
    /// module change as a mod difference. That same rule is what restores the id-keyed
    /// channels when the pair walks into a module whose GITs agree.
    /// </summary>
    public static WorldDigestComparison Compare(in WorldDigest local, in WorldDigest remote)
    {
        bool globals = local.GlobalsCatalogue != 0 && remote.GlobalsCatalogue != 0
            && local.GlobalsCatalogue != remote.GlobalsCatalogue;

        bool save = local.SaveIdentity != 0 && remote.SaveIdentity != 0
            && local.SaveIdentity != remote.SaveIdentity;

        // Module is a string field off the wire, so null (a default-constructed digest
        // that never crossed the codec) reads as "not resolved" too.
        string localModule = local.Module ?? string.Empty;
        bool sameModule = localModule.Length > 0
            && string.Equals(localModule, remote.Module ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        bool git = sameModule && local.ModuleGit != 0 && remote.ModuleGit != 0
            && local.ModuleGit != remote.ModuleGit;

        return new WorldDigestComparison(globals, save, git, sameModule ? localModule : string.Empty);
    }
}

/// <summary>The outcome of <see cref="WorldDigest.Compare"/>: which in-world facts the two
/// peers disagree on. Feeds <see cref="ContentCompatibility.ApplyWorldMismatch"/> verbatim.</summary>
/// <param name="Module">The module both peers were standing in when this was judged, empty
/// while they are apart. Carried purely so a GIT degradation can NAME the module on screen —
/// "enemies are off here" is only actionable if the player knows where "here" is.</param>
public readonly record struct WorldDigestComparison(
    bool GlobalsCatalogueMismatch,
    bool SaveIdentityMismatch,
    bool ModuleGitMismatch,
    string Module = "")
{
    public bool Any => GlobalsCatalogueMismatch || SaveIdentityMismatch || ModuleGitMismatch;
}
