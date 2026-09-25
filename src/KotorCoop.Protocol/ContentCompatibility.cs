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
/// Why a channel was switched off, or what a player should know about a difference that
/// switched nothing off. Machine-readable on purpose: a degradation is the normal outcome
/// of two differently-modded installs meeting, and the only thing worse than a channel
/// silently dying is a channel silently dying with an explanation buried in a log file.
/// Anything that reads a verdict — the dashboard, the in-game overlay, a future UI —
/// switches on the code and never parses the sentence.
/// </summary>
public enum CompatibilityCode
{
    None = 0,

    /// <summary>One side could fingerprint nothing, so none of the safety below is in force.</summary>
    Unchecked,

    /// <summary>The two peers are playing different saves, so object ids were dealt
    /// independently — see <c>docs/research/world-adoption.md</c> for the cure.</summary>
    Save,

    /// <summary>The module both peers are standing in hands out different object ids on the
    /// two installs (a mod edited its GIT).</summary>
    ModuleGit,

    /// <summary>The global-variable catalogues differ, so (type, index) keys name different
    /// variables.</summary>
    Globals,

    /// <summary><c>appearance.2da</c> differs; a replicated row index would paint the wrong
    /// body or index off the end of the table.</summary>
    Appearance,

    /// <summary><c>global.jrl</c> differs; a peer's quest tag may not exist locally.</summary>
    Journal,

    /// <summary><c>dialog.tlk</c> differs. Cosmetic — nothing reads the TLK today.</summary>
    Tlk,

    /// <summary>The override folders differ. The expected case for two modded installs.</summary>
    Override,

    /// <summary>Some other 2DA differs.</summary>
    TwoDa,

    /// <summary>The module file censuses differ, so some module may desync later.</summary>
    Modules,

    /// <summary>The live GUI difficulty differs, so PC/party damage scaling can differ.</summary>
    Difficulty,
}

/// <summary>
/// One machine-readable finding from a content comparison: what differed, which channels it
/// cost, and the sentence a player should be shown.
/// </summary>
/// <param name="Code">The finding, for anything that wants to act rather than print.</param>
/// <param name="Dropped">The channels this finding switched off —
/// <see cref="SessionCapabilities.None"/> for a warning that costs nothing.</param>
/// <param name="Text">The player-facing sentence. Says what stopped working AND why, because
/// a channel that quietly stops is indistinguishable from a bug.</param>
public readonly record struct CompatibilityNote(
    CompatibilityCode Code,
    SessionCapabilities Dropped,
    string Text)
{
    /// <summary>A stable short tag for logs and the wire (<c>W_SAVE</c>, <c>W_GIT</c>, ...).
    /// Kept separate from the enum name so renaming a C# member never changes what a player
    /// or a script sees.</summary>
    public string Tag => Code switch
    {
        CompatibilityCode.Unchecked => "W_UNCHECKED",
        CompatibilityCode.Save => "W_SAVE",
        CompatibilityCode.ModuleGit => "W_GIT",
        CompatibilityCode.Globals => "W_GLOBALS",
        CompatibilityCode.Appearance => "W_APPEARANCE",
        CompatibilityCode.Journal => "W_JOURNAL",
        CompatibilityCode.Tlk => "W_TLK",
        CompatibilityCode.Override => "W_OVERRIDE",
        CompatibilityCode.TwoDa => "W_2DA",
        CompatibilityCode.Modules => "W_MODULES",
        CompatibilityCode.Difficulty => "W_DIFFICULTY",
        _ => "W_NONE",
    };

    /// <summary>True when this finding cost the session a channel, as opposed to being a
    /// difference worth mentioning that broke nothing.</summary>
    public bool IsDegradation => Dropped != SessionCapabilities.None;
}

/// <summary>
/// The outcome of comparing two peers' content: either a refusal, or an allowed capability
/// set plus the findings that shaped it. Computed identically on host and client from the
/// identical digests, so both ends agree without negotiating.
/// </summary>
public sealed record CompatibilityVerdict(
    RefusalReason Refusal,
    SessionCapabilities Allowed,
    IReadOnlyList<CompatibilityNote> Notes)
{
    private IReadOnlyList<string>? _warnings;

    public bool Admitted => Refusal == RefusalReason.None;

    /// <summary>The channels this verdict switched off, for a caller that wants the shape of
    /// the damage rather than the reasons.</summary>
    public SessionCapabilities Dropped => SessionCapabilities.All & ~Allowed;

    /// <summary>The findings as plain sentences, for readouts that only want to print. The
    /// codes are on <see cref="Notes"/>; this is the projection, never the source.</summary>
    public IReadOnlyList<string> Warnings
    {
        get
        {
            if (_warnings is null)
            {
                string[] texts = new string[Notes.Count];
                for (int i = 0; i < Notes.Count; i++)
                {
                    texts[i] = Notes[i].Text;
                }

                _warnings = texts;
            }

            return _warnings;
        }
    }

    /// <summary>A short machine + human line for a RejectPacket or the dashboard.</summary>
    public string Summary => Refusal switch
    {
        RefusalReason.ProtocolVersion => "E_PROTO: incompatible co-op version",
        RefusalReason.Executable => "E_EXE: different swkotor.exe (addresses would not match)",
        RefusalReason.SaveGame => "E_SAVE: different save; rejoin to adopt the host's world",
        _ => Notes.Count == 0 ? "compatible" : $"compatible, {Notes.Count} note(s): {Tags}",
    };

    /// <summary>The findings' short tags, space separated — the machine-readable half of
    /// <see cref="Summary"/>.</summary>
    public string Tags => string.Join(' ', Notes.Select(n => n.Tag));
}

/// <summary>
/// Judges whether two installs can share a session, and to what extent. The policy: refuse
/// only what is unrecoverable, degrade the specific channel a mismatch invalidates, and warn
/// about the rest — because two players having different cosmetic mods is the expected case,
/// not an error.
///
/// WHAT COUNTS AS UNRECOVERABLE MOVED. It used to include a different save: object ids are
/// dealt by the save, so two different saves could never line up and the join was refused.
/// World adoption (<c>docs/research/world-adoption.md</c>) changed that — a joiner is handed
/// the host's world and carries only their character in, so identical saves are now something
/// the session MAKES true rather than merely demands. A save mismatch therefore means
/// adoption did not happen (or did not finish), which is recoverable by rejoining, so it
/// degrades the id-keyed channels loudly instead of ending the session. A host that genuinely
/// wants the old behaviour sets <see cref="RequireIdenticalSave"/>.
///
/// Pure and deterministic: same inputs, same verdict, on either peer.
/// </summary>
public static class ContentCompatibility
{
    /// <summary>
    /// Whether a save mismatch is fatal. Off by default, because world adoption makes the
    /// saves match by construction and a mismatch is now evidence of a failed adoption rather
    /// than a configuration two players chose — and evicting someone already standing in the
    /// world helps nobody. Set it to demand identical saves anyway (the pre-adoption rule),
    /// in which case the mismatch produces a real <see cref="RefusalReason.SaveGame"/>.
    /// </summary>
    public static bool RequireIdenticalSave { get; set; }

    /// <summary>
    /// Install-time comparison (from Hello/Welcome). Decides admission and the initial
    /// capability set. World-time facts (globals catalogue, save identity, per-module GIT)
    /// refine it later through <see cref="ApplyWorldMismatch"/>.
    /// </summary>
    public static CompatibilityVerdict Compare(in ContentDigest local, in ContentDigest remote)
    {
        List<CompatibilityNote> notes = [];
        SessionCapabilities caps = SessionCapabilities.All;

        // A peer that could fingerprint nothing (no game-file access) cannot be checked.
        // Admit it — refusing on absent information would block plenty of working setups —
        // but say so, because it means none of the safety below is in force.
        if (local.IsEmpty || remote.IsEmpty)
        {
            notes.Add(new CompatibilityNote(
                CompatibilityCode.Unchecked,
                SessionCapabilities.None,
                "content could not be fingerprinted on one side; compatibility unchecked"));
            return new CompatibilityVerdict(RefusalReason.None, caps, notes);
        }

        // EXE — refuse. Every engine address the mod uses is absolute and per-binary; a
        // different exe means every write could land somewhere unrelated.
        //
        // ⚠ THIS IS THE RUNNING IMAGE, NOT THE FILE (ExeImageDigestRule, published by the
        // plugin from inside the decrypted process before it installs a single hook). The
        // file hash used to be what landed here, and it refused Steam↔DRM-free sessions over
        // nothing: Steam ships the SAME LINK under SteamStub v3, which encrypts .text on disk
        // and decrypts it in place before the game's entry point runs, so two installs whose
        // running code is byte-identical hashed differently. Hashing the image instead is
        // strictly stronger — it also catches a runtime patcher a file hash never sees — and
        // it is the honest statement of what this check is actually about.
        // docs/research/steam-support.md §8.
        if (BothKnow(local, remote, ContentDigest.Field.Exe) && local.ExeDigest != remote.ExeDigest)
        {
            return Refuse(RefusalReason.Executable);
        }

        // ...and say so when it could NOT be checked. One side without a published image
        // digest (an older plugin, or an agent attached to a game that has none) leaves the
        // single fatal check silently inactive, which is exactly the kind of thing that must
        // be visible in the session log rather than inferred from its absence.
        if (!BothKnow(local, remote, ContentDigest.Field.Exe))
        {
            notes.Add(new CompatibilityNote(
                CompatibilityCode.Unchecked,
                SessionCapabilities.None,
                "executable identity unchecked: one side published no live image digest"));
        }

        // APPEARANCE — a reordered or resized appearance.2da makes a replicated row index
        // paint the wrong body, or index off the end of the table (a crash). Drop the
        // appearance write; the phantom keeps its blueprint look.
        if (BothKnow(local, remote, ContentDigest.Field.Appearance)
            && (local.AppearanceDigest != remote.AppearanceDigest
                || local.AppearanceRows != remote.AppearanceRows))
        {
            caps &= ~SessionCapabilities.Appearance;
            notes.Add(new CompatibilityNote(
                CompatibilityCode.Appearance,
                SessionCapabilities.Appearance,
                "appearance.2da differs; phantoms keep their default appearance"));
        }

        // JOURNAL — a quest tag from the peer that this install's global.jrl lacks would be
        // appended as a phantom entry. Degrade: the journal channel filters to known tags.
        if (BothKnow(local, remote, ContentDigest.Field.Journal) && local.JournalDigest != remote.JournalDigest)
        {
            notes.Add(new CompatibilityNote(
                CompatibilityCode.Journal,
                SessionCapabilities.None,
                "global.jrl differs; journal limited to quests both installs know"));
        }

        // TLK / OVERRIDE / MODULES / other 2DAs — expected to differ whenever either side
        // has mods, and none of them corrupts state on their own. Warn so the difference is
        // visible (it explains any equipment or item that silently fails to appear), never
        // block.
        if (BothKnow(local, remote, ContentDigest.Field.Tlk) && local.TlkDigest != remote.TlkDigest)
        {
            notes.Add(new CompatibilityNote(
                CompatibilityCode.Tlk,
                SessionCapabilities.None,
                "dialog.tlk differs (different text mods); harmless"));
        }

        if (BothKnow(local, remote, ContentDigest.Field.Override) && local.OverrideCensus != remote.OverrideCensus)
        {
            notes.Add(new CompatibilityNote(
                CompatibilityCode.Override,
                SessionCapabilities.None,
                $"override folders differ ({local.OverrideCount} vs {remote.OverrideCount} files); "
                    + "gear or items unique to one side may not appear on the other"));
        }

        if (BothKnow(local, remote, ContentDigest.Field.TwoDa) && local.TwoDaDigest != remote.TwoDaDigest)
        {
            notes.Add(new CompatibilityNote(
                CompatibilityCode.TwoDa,
                SessionCapabilities.None,
                "2DA edits differ between installs"));
        }

        if (BothKnow(local, remote, ContentDigest.Field.Modules) && local.ModulesCensus != remote.ModulesCensus)
        {
            notes.Add(new CompatibilityNote(
                CompatibilityCode.Modules,
                SessionCapabilities.None,
                "module files differ; a module edited on one side may desync there"));
        }

        return new CompatibilityVerdict(RefusalReason.None, caps, notes);
    }

    /// <summary>The channels that are only ever safe because both engines dealt the same
    /// object ids. Everything keyed on an id — enemies, doors and placeables, ground items,
    /// and the party slots — lives or dies together, so the set is named once.
    ///
    /// <see cref="SessionCapabilities.Store"/> belongs here (unlike
    /// <see cref="SessionCapabilities.PartyPool"/>, which does not — see that member's own
    /// remarks): a store is a real, GIT-placed <c>CGameObject</c> both instances share an id for
    /// (docs/research/store-sync.md §1.2), so a save/GIT mismatch is exactly as unsafe for it as
    /// for a door or a container.</summary>
    public const SessionCapabilities ObjectIdKeyed =
        SessionCapabilities.Enemies | SessionCapabilities.Doors
        | SessionCapabilities.GroundItems | SessionCapabilities.Party | SessionCapabilities.Store;

    /// <summary>
    /// The session-wide verdict a host shows when several peers are connected: every channel
    /// ANY peer's verdict dropped stays dropped (the same intersection
    /// <c>CoopHost.Capabilities</c> takes), and each finding is kept once, from the first peer
    /// that raised it. Live 2026-09-25: with two joiners, one on the host's save and one not, the
    /// host published whichever peer's digest arrived last. Its announcer then logged
    /// "shared again" about once a second for the whole session.
    /// </summary>
    public static CompatibilityVerdict Combine(IEnumerable<CompatibilityVerdict> verdicts)
    {
        ArgumentNullException.ThrowIfNull(verdicts);
        SessionCapabilities allowed = SessionCapabilities.All;
        List<CompatibilityNote> notes = [];
        HashSet<CompatibilityCode> seen = [];
        RefusalReason refusal = RefusalReason.None;
        foreach (CompatibilityVerdict verdict in verdicts)
        {
            allowed &= verdict.Allowed;
            if (refusal == RefusalReason.None)
            {
                refusal = verdict.Refusal;
            }

            foreach (CompatibilityNote note in verdict.Notes)
            {
                if (seen.Add(note.Code))
                {
                    notes.Add(note);
                }
            }
        }

        return new CompatibilityVerdict(refusal, allowed, notes);
    }

    /// <summary>
    /// Refines a verdict with world-time facts. A different globals catalogue drops world
    /// state and doors (the keys would name different variables); a different save drops
    /// every object-id-keyed channel; a different GIT for the module both players are
    /// standing in drops the same channels for exactly as long as they stand there. Called
    /// on join and on every module change, so walking into a module whose GITs agree
    /// restores what the last one cost.
    /// </summary>
    /// <param name="module">The module both peers are in, when they are in the same one —
    /// purely so a GIT degradation can name it on screen. Never affects the decision.</param>
    public static CompatibilityVerdict ApplyWorldMismatch(
        CompatibilityVerdict install,
        bool globalsCatalogueMismatch,
        bool saveIdentityMismatch,
        bool moduleGitMismatch,
        string? module = null,
        bool difficultyMismatch = false)
    {
        ArgumentNullException.ThrowIfNull(install);

        if (!install.Admitted)
        {
            return install;
        }

        List<CompatibilityNote> notes = [.. install.Notes];
        SessionCapabilities caps = install.Allowed;

        if (saveIdentityMismatch)
        {
            // The pre-adoption rule, kept for a host that explicitly asks for it.
            if (RequireIdenticalSave)
            {
                return Refuse(RefusalReason.SaveGame);
            }

            caps &= ~ObjectIdKeyed;
            notes.Add(new CompatibilityNote(
                CompatibilityCode.Save,
                ObjectIdKeyed,
                "different save: enemies, doors, items and party are off — "
                    + "rejoin to adopt the host's world and get them back"));
        }
        else if (moduleGitMismatch)
        {
            // The module's saved/object placement fingerprint differs. This can be an
            // install mod, or a same-lineage solo/save excursion that rewrote explicit ids;
            // either way the id-keyed channels are unsafe until the worlds are re-adopted or
            // the next module agrees.
            caps &= ~ObjectIdKeyed;
            notes.Add(new CompatibilityNote(
                CompatibilityCode.ModuleGit,
                ObjectIdKeyed,
                string.IsNullOrEmpty(module)
                    ? "this module's object mapping differs between the worlds: enemies, doors "
                        + "and items are off here until the worlds are re-adopted or a matching module is reached"
                    : $"module {module}'s object mapping differs between the worlds: enemies, doors "
                        + "and items are off here until the worlds are re-adopted or a matching module is reached"));
        }

        if (globalsCatalogueMismatch)
        {
            caps &= ~(SessionCapabilities.WorldState | SessionCapabilities.Doors);
            notes.Add(new CompatibilityNote(
                CompatibilityCode.Globals,
                SessionCapabilities.WorldState | SessionCapabilities.Doors,
                "global variable tables differ; quest/world state and doors are not shared"));
        }

        if (difficultyMismatch)
        {
            notes.Add(new CompatibilityNote(
                CompatibilityCode.Difficulty,
                SessionCapabilities.None,
                "live difficulty settings differ; PC and party damage scaling may differ, but co-op remains enabled"));
        }

        return new CompatibilityVerdict(RefusalReason.None, caps, notes);
    }

    /// <summary>Convenience over the three-flag form for callers holding a
    /// <see cref="WorldDigestComparison"/>, which already carries the module the two peers
    /// share (or none, while one of them is mid-transit).</summary>
    public static CompatibilityVerdict ApplyWorldMismatch(
        CompatibilityVerdict install, in WorldDigestComparison mismatch) =>
        ApplyWorldMismatch(
            install,
            mismatch.GlobalsCatalogueMismatch,
            mismatch.SaveIdentityMismatch,
            mismatch.ModuleGitMismatch,
            mismatch.Module,
            mismatch.DifficultyMismatch);

    private static bool BothKnow(in ContentDigest a, in ContentDigest b, ContentDigest.Field field) =>
        a.Knows(field) && b.Knows(field);

    private static CompatibilityVerdict Refuse(RefusalReason reason) =>
        new(reason, SessionCapabilities.None, []);
}
