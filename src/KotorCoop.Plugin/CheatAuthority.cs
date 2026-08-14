namespace KotorCoop.Plugin;

/// <summary>What the staging layer is allowed to do about a requested kill.</summary>
internal enum SlayVerdict
{
    /// <summary>Nothing here answers to that name or id.</summary>
    RefuseMissing,

    /// <summary>A player's own character. Yours is yours; a peer's is theirs.</summary>
    RefusePlayer,

    /// <summary>A peer's phantom — a body this instance renders on their behalf.</summary>
    RefusePhantom,

    /// <summary>A party companion. The host drives them and every client mirrors them, so a
    /// kill staged on the wrong side is a desync rather than a death.</summary>
    RefuseCompanion,

    /// <summary>The module flagged it plot; the module decides who is un-killable, not us.</summary>
    RefusePlot,

    /// <summary>Ours to kill: run the engine's own death right here.</summary>
    KillHere,

    /// <summary>Somebody else's enemy: spend the kill through the damage ledger and let the
    /// owner's engine produce the death.</summary>
    ForwardToOwner,
}

/// <summary>
/// The co-op authority rules for the staging cheats, as pure arithmetic over facts the
/// caller has already measured — no engine pointers, no block, so it compiles into the
/// 64-bit test project and the rules can be asserted instead of described.
///
/// This exists because the interesting half of a cheat in co-op is not the poke, it is
/// WHO IS ALLOWED TO DO IT. "kill that Sith soldier" means two completely different
/// operations depending on which instance typed it, and getting that wrong does not fail
/// loudly — it desyncs a fight and looks like a bug in the combat mirror days later. The
/// same reasoning, and the same shape, as <see cref="MirrorHealthRule"/>.
/// </summary>
internal static class CheatAuthority
{
    /// <summary>
    /// The damage a forwarded kill spends. Deliberately far above any base-game creature's
    /// hit points and far below the ledger's saturating ceiling: the owner applies the sum
    /// as one real damage effect, so the kill falls out of the arithmetic exactly the way a
    /// peer's ordinary contribution does (docs/research/peer-damage-ledger.md), and nothing
    /// about the channel has to learn a new "it is dead" message.
    /// </summary>
    public const uint ForwardedKillDamage = 100_000;

    /// <summary>
    /// Sorts out WHICH ID SPACE a requested target is in, on an instance that has two of
    /// them: the owner's ids (what the enemy census carries, and the only ids a forwarded
    /// debt can be keyed by) and this engine's own (what can actually be resolved here).
    ///
    /// ⚠ THE CONSOLE CAN HAND OVER EITHER, and until this existed the caller assumed the
    /// second. A bare <c>slay</c> on a mirroring instance now picks the nearest enemy out of
    /// the OWNER's published census, so the id it hands down is an owner id — which resolves
    /// locally only by the luck of GIT identity. Where the mirror had to materialise a
    /// stand-in there is no such luck: the owner's <c>0x1A</c> is our <c>0x135</c>, resolving
    /// <c>0x1A</c> here finds nothing (or, worse, a different creature that happens to hold
    /// that number) and the kill is refused as "no creature here answers to that".
    ///
    /// The two maps are asymmetric on purpose, so both directions are covered:
    /// <paramref name="mappedLocal"/> is <c>MaterialisedEnemies.ToLocal(requested)</c> — non-zero
    /// only when the request was an OWNER id we have a body for — and
    /// <paramref name="mappedOwner"/> is <c>ToOwner(requested)</c>, non-zero only when it was
    /// one of OUR stand-in ids. With neither, the ordinary GIT-identity assumption stands: the
    /// same creature carries the same id in both engines.
    /// </summary>
    /// <returns>The id to resolve a body with here, and the id to key a forwarded debt by.</returns>
    public static (uint Local, uint Owner) MirrorIds(uint requested, uint mappedLocal, uint mappedOwner)
    {
        if (mappedLocal != 0)
        {
            return (mappedLocal, requested);
        }

        if (mappedOwner != 0)
        {
            return (requested, mappedOwner);
        }

        return (requested, requested);
    }

    /// <summary>
    /// Decides what a <c>slay</c> against this target means on this instance.
    ///
    /// <paramref name="mirrorsEnemies"/> is the authority discriminator and it is not
    /// guessed: the agent sets it on exactly the instances whose enemies belong to somebody
    /// else (SharedBlock.OffsetDamageDealtEnabled), which is the same flag the damage ledger
    /// and the phantom-neutrality hold already key off. On such an instance every enemy copy
    /// is pinned at one hit point, so killing it locally cannot work anyway — the pin would
    /// have to come off, the corpse would exist here and nowhere else, and the owner's next
    /// mirror pass would stand it back up.
    /// </summary>
    public static SlayVerdict ForSlay(
        bool targetResolved,
        bool targetIsLocalPlayer,
        bool targetIsPhantom,
        bool targetIsCompanion,
        bool targetIsPlot,
        bool mirrorsEnemies)
    {
        if (!targetResolved)
        {
            return SlayVerdict.RefuseMissing;
        }

        // Every "that body belongs to a person" check comes before plot: a player's or a
        // companion's body is never a legitimate target of this verb, whatever else happens
        // to be true of it.
        if (targetIsLocalPlayer)
        {
            return SlayVerdict.RefusePlayer;
        }

        if (targetIsPhantom)
        {
            return SlayVerdict.RefusePhantom;
        }

        if (targetIsCompanion)
        {
            return SlayVerdict.RefuseCompanion;
        }

        if (targetIsPlot)
        {
            return SlayVerdict.RefusePlot;
        }

        return mirrorsEnemies ? SlayVerdict.ForwardToOwner : SlayVerdict.KillHere;
    }

    /// <summary>The sentence that goes back to the player and into the log for a refusal —
    /// the "name what you saw" half of a guard that fails safe.</summary>
    public static string Explain(SlayVerdict verdict, uint objectId) => verdict switch
    {
        SlayVerdict.RefuseMissing => "no creature here answers to that",
        SlayVerdict.RefusePlayer => $"0x{objectId:X} is your own character",
        SlayVerdict.RefusePhantom => $"0x{objectId:X} is a peer's phantom, not an enemy",
        SlayVerdict.RefuseCompanion => $"0x{objectId:X} is a party companion",
        SlayVerdict.RefusePlot => $"0x{objectId:X} is plot-flagged by the module",
        SlayVerdict.KillHere => $"killed 0x{objectId:X}",
        SlayVerdict.ForwardToOwner => $"0x{objectId:X} is the host's; sent the kill to them",
        _ => "unhandled verdict",
    };
}
