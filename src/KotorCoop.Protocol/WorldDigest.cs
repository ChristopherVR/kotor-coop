namespace KotorCoop.Protocol;

/// <summary>
/// A peer's in-world content fingerprint — the facts about the running game that only
/// exist once a save is loaded, as opposed to the install-time <see cref="ContentDigest"/>
/// exchanged at Hello. The digest answers questions about the id-keyed channels, plus the
/// local runtime difficulty that affects player damage:
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
/// <item><see cref="LoadSerial"/> — roadmap item 14 phase 2 (docs/research/session-save.md):
/// the sender's own count of world teardowns its plugin judged UNANNOUNCED — a Load Game
/// action, not an ordinary module transition (<c>KotorCoop.Plugin.WorldLoadRule</c>,
/// <c>SharedBlock.OffsetWorldLoadSerial</c>). Riding here, unmodified, is what lets a
/// RECEIVER classify a peer's <see cref="SaveIdentity"/> change as a save or a load without a
/// new packet: the same edge-plus-digest correlation each side already runs on its OWN
/// readings (<c>KotorCoop.Session.SaveOrLoadClassifier</c>) works identically against a
/// peer's reported pair. A plain counter, not a bool, survives a lost datagram on this 1 Hz
/// channel without losing the edge — the receiver's own <c>SerialEdgeWatch</c> only cares
/// that the value it last saw differs, not by how much.</item>
/// <item><see cref="DifficultyLevel"/> — the live client-options difficulty slider (0..2).
/// A disagreement warns that PC/party damage scaling differs; it never disables a channel.</item>
/// </list>
///
/// Digest fields follow the house convention: 0 (or an empty module) means "not resolved",
/// and per the Unknown-is-not-mismatch rule an unresolved field can never degrade
/// anything — only two peers that both computed a value can disagree. <see cref="LoadSerial"/>
/// is the one exception to "0 means not resolved": 0 is its legitimate starting value (no
/// load observed yet this process), so it is read only as an EDGE (did it change), never
/// compared for equality/mismatch. <see cref="DifficultyLevel"/> is nullable for the same
/// reason that a missing sample must remain distinct from the valid Easy value 0.
/// </summary>
public readonly record struct WorldDigest(
    uint GlobalsCatalogue,
    uint SaveIdentity,
    string Module,
    uint ModuleGit,
    uint LoadSerial = 0,
    byte? DifficultyLevel = null,
    uint ScriptRefreshRevision = 0,
    uint ScriptRefreshSaveIdentity = 0,
    uint NativeWorldEpoch = 0)
{
    /// <summary>Sent when the runtime setting could not be read safely.</summary>
    public const byte UnknownDifficulty = byte.MaxValue;

    public static WorldDigest Empty => new(0, 0, string.Empty, 0, 0, null);

    /// <summary>True when nothing here is worth putting on the wire yet.</summary>
    public bool IsEmpty => GlobalsCatalogue == 0 && SaveIdentity == 0 && ModuleGit == 0
        && DifficultyLevel is null;

    /// <summary>
    /// Judges the three in-world mismatches, identically on host and client, for
    /// <see cref="ContentCompatibility.ApplyWorldMismatch"/>. A field is a mismatch only
    /// when BOTH sides resolved it and the values differ; the module GIT additionally
    /// requires both peers to be reporting the SAME module — while one peer is mid-transit
    /// the digests describe different areas and comparing them would flag every ordinary
    /// module change as a mod difference. That same rule is what restores the id-keyed
    /// channels when the pair walks into a module whose GITs agree.
    ///
    /// ⚠ SAVE IDENTITY GETS THE SAME GATE, AND FOR THE SAME REASON.
    /// <c>SaveDigest.TryComputeSaveIdentity</c> hashes the module the save is CURRENTLY
    /// standing in alongside the id-dealing state, so it legitimately changes the instant
    /// each side's own engine autosaves into a new module — and the two peers never do that
    /// in the same instant; one follows the other by however long its own travel and load
    /// take. Comparing <see cref="SaveIdentity"/> while the peers report DIFFERENT modules
    /// cannot tell "still travelling" from "a genuinely different save", so — like the GIT —
    /// it is judged only once both stand in the same place. This is also harmless to skip
    /// while apart: the id-keyed channels it guards (enemies, doors, ground items, party)
    /// are already scoped to the module each peer is currently in, so there is nothing for a
    /// save-identity verdict to protect until the two are standing in the same one anyway.
    /// Measured live crossing <c>tar_m10aa</c> as a `[W_SAVE]` false alarm caused purely by
    /// timing lag between the two sides' follow-transitions — see
    /// docs/research/shared-state-audit.md §2. A residual few seconds of same-module timing
    /// lag past this gate (each side's digest cache re-stats on its own schedule) is smoothed
    /// by <see cref="SaveIdentitySettle"/>, which the caller applies to this comparison's
    /// <see cref="WorldDigestComparison.SaveIdentityMismatch"/>.
    /// </summary>
    public static WorldDigestComparison Compare(in WorldDigest local, in WorldDigest remote)
    {
        bool globals = local.GlobalsCatalogue != 0 && remote.GlobalsCatalogue != 0
            && local.GlobalsCatalogue != remote.GlobalsCatalogue;

        // Module is a string field off the wire, so null (a default-constructed digest
        // that never crossed the codec) reads as "not resolved" too.
        string localModule = local.Module ?? string.Empty;
        bool sameModule = localModule.Length > 0
            && string.Equals(localModule, remote.Module ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        bool save = sameModule && local.SaveIdentity != 0 && remote.SaveIdentity != 0
            && local.SaveIdentity != remote.SaveIdentity;

        bool git = sameModule && local.ModuleGit != 0 && remote.ModuleGit != 0
            && local.ModuleGit != remote.ModuleGit;
        bool difficulty = local.DifficultyLevel is <= 2 && remote.DifficultyLevel is <= 2
            && local.DifficultyLevel != remote.DifficultyLevel;

        return new WorldDigestComparison(
            globals, save, git, sameModule ? localModule : string.Empty, difficulty);
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
    string Module = "",
    bool DifficultyMismatch = false)
{
    public bool Any => GlobalsCatalogueMismatch || SaveIdentityMismatch || ModuleGitMismatch
        || DifficultyMismatch;
}
