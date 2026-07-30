namespace KotorCoop.Protocol;

/// <summary>Why a peer was refused admission, machine-readable so the client can act on it
/// rather than only print it.</summary>
public enum RefusalReason
{
    None = 0,
    ProtocolVersion,
    Executable,
    SaveGame,
}

/// <summary>
/// The outcome of comparing two peers' content: either a refusal, or an allowed capability
/// set plus any warnings. Computed identically on host and client from the identical
/// digests, so both ends agree without negotiating.
/// </summary>
public sealed record CompatibilityVerdict(
    RefusalReason Refusal,
    SessionCapabilities Allowed,
    IReadOnlyList<string> Warnings)
{
    public bool Admitted => Refusal == RefusalReason.None;

    /// <summary>A short machine + human line for a RejectPacket or the dashboard.</summary>
    public string Summary => Refusal switch
    {
        RefusalReason.ProtocolVersion => "E_PROTO: incompatible co-op version",
        RefusalReason.Executable => "E_EXE: different swkotor.exe (addresses would not match)",
        RefusalReason.SaveGame => "E_SAVE: different save; object ids would not line up",
        _ => Warnings.Count == 0 ? "compatible" : $"compatible, {Warnings.Count} warning(s)",
    };
}

/// <summary>
/// Judges whether two installs can share a session, and to what extent. The policy: refuse
/// only what is unrecoverable (a different executable, a different save), degrade the
/// specific channel a mismatch invalidates, and warn about the rest — because two players
/// having different cosmetic mods is the expected case, not an error.
///
/// Pure and deterministic: same inputs, same verdict, on either peer.
/// </summary>
public static class ContentCompatibility
{
    /// <summary>
    /// Whether allowing a session between mismatched saves is permitted. When true, a save
    /// mismatch degrades to the id-independent channels instead of refusing outright — for
    /// players who deliberately want to run different saves together and accept that
    /// enemies, doors and items will not line up.
    /// </summary>
    public static bool AllowDifferentSave { get; set; }

    /// <summary>
    /// Install-time comparison (from Hello/Welcome). Decides admission and the initial
    /// capability set. World-time facts (globals catalogue, save identity, per-module GIT)
    /// refine it later through <see cref="ApplyWorldMismatch"/>.
    /// </summary>
    public static CompatibilityVerdict Compare(in ContentDigest local, in ContentDigest remote)
    {
        List<string> warnings = [];
        SessionCapabilities caps = SessionCapabilities.All;

        // A peer that could fingerprint nothing (no game-file access) cannot be checked.
        // Admit it — refusing on absent information would block plenty of working setups —
        // but say so, because it means none of the safety below is in force.
        if (local.IsEmpty || remote.IsEmpty)
        {
            warnings.Add("content could not be fingerprinted on one side; compatibility unchecked");
            return new CompatibilityVerdict(RefusalReason.None, caps, warnings);
        }

        // EXE — refuse. Every engine address the mod uses is absolute and per-binary; a
        // different exe means every write could land somewhere unrelated.
        if (BothKnow(local, remote, ContentDigest.Field.Exe) && local.ExeDigest != remote.ExeDigest)
        {
            return Refuse(RefusalReason.Executable);
        }

        // APPEARANCE — a reordered or resized appearance.2da makes a replicated row index
        // paint the wrong body, or index off the end of the table (a crash). Drop the
        // appearance write; the phantom keeps its blueprint look.
        if (BothKnow(local, remote, ContentDigest.Field.Appearance)
            && (local.AppearanceDigest != remote.AppearanceDigest
                || local.AppearanceRows != remote.AppearanceRows))
        {
            caps &= ~SessionCapabilities.Appearance;
            warnings.Add("appearance.2da differs; phantoms keep their default appearance");
        }

        // JOURNAL — a quest tag from the peer that this install's global.jrl lacks would be
        // appended as a phantom entry. Degrade: the journal channel filters to known tags.
        if (BothKnow(local, remote, ContentDigest.Field.Journal) && local.JournalDigest != remote.JournalDigest)
        {
            warnings.Add("global.jrl differs; journal limited to quests both installs know");
        }

        // TLK / OVERRIDE / MODULES / other 2DAs — expected to differ whenever either side
        // has mods, and none of them corrupts state on their own. Warn so the difference is
        // visible (it explains any equipment or item that silently fails to appear), never
        // block.
        if (BothKnow(local, remote, ContentDigest.Field.Tlk) && local.TlkDigest != remote.TlkDigest)
        {
            warnings.Add("dialog.tlk differs (different text mods); harmless");
        }

        if (BothKnow(local, remote, ContentDigest.Field.Override) && local.OverrideCensus != remote.OverrideCensus)
        {
            warnings.Add($"override folders differ ({local.OverrideCount} vs {remote.OverrideCount} files); "
                + "gear or items unique to one side may not appear on the other");
        }

        if (BothKnow(local, remote, ContentDigest.Field.TwoDa) && local.TwoDaDigest != remote.TwoDaDigest)
        {
            warnings.Add("2DA edits differ between installs");
        }

        if (BothKnow(local, remote, ContentDigest.Field.Modules) && local.ModulesCensus != remote.ModulesCensus)
        {
            warnings.Add("module files differ; a module edited on one side may desync there");
        }

        return new CompatibilityVerdict(RefusalReason.None, caps, warnings);
    }

    /// <summary>
    /// Refines a verdict with world-time facts. A different globals catalogue drops world
    /// state and doors (the keys would name different variables); a different save, or a
    /// different GIT for the loaded module, drops the object-id-keyed channels for that
    /// module. Called on join and on every module change.
    /// </summary>
    public static CompatibilityVerdict ApplyWorldMismatch(
        CompatibilityVerdict install,
        bool globalsCatalogueMismatch,
        bool saveIdentityMismatch,
        bool moduleGitMismatch)
    {
        if (!install.Admitted)
        {
            return install;
        }

        List<string> warnings = [.. install.Warnings];
        SessionCapabilities caps = install.Allowed;

        if (saveIdentityMismatch)
        {
            if (!AllowDifferentSave)
            {
                return Refuse(RefusalReason.SaveGame);
            }

            caps &= ~(SessionCapabilities.Enemies | SessionCapabilities.Doors
                | SessionCapabilities.GroundItems | SessionCapabilities.Party);
            warnings.Add("different save (allowed): enemies, doors, items and party are not shared");
        }
        else if (moduleGitMismatch)
        {
            // Same save, but a mod edited THIS module's placements — ids shift here only.
            caps &= ~(SessionCapabilities.Enemies | SessionCapabilities.Doors | SessionCapabilities.GroundItems);
            warnings.Add("this module differs between installs; enemies, doors and items paused here");
        }

        if (globalsCatalogueMismatch)
        {
            caps &= ~(SessionCapabilities.WorldState | SessionCapabilities.Doors);
            warnings.Add("global variable tables differ; quest/world state and doors are not shared");
        }

        return new CompatibilityVerdict(RefusalReason.None, caps, warnings);
    }

    private static bool BothKnow(in ContentDigest a, in ContentDigest b, ContentDigest.Field field) =>
        a.Knows(field) && b.Knows(field);

    private static CompatibilityVerdict Refuse(RefusalReason reason) =>
        new(reason, SessionCapabilities.None, []);
}
