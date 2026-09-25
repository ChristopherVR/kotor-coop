namespace KotorCoop.Protocol;

/// <summary>
/// Zhar's saber-construction reward (docs/research/class-and-power-grants.md §10) creates
/// exactly ONE lightsaber on the builder's own engine — whoever is running that specific
/// conversation — coloured for whatever class THAT player happens to hold
/// (<c>k_pdan_saber16</c> keys the colour off the shared <c>DAN_PATH_STATE</c> global, which in
/// single-player always agrees with the builder's own class because both come from the SAME
/// dialogue choice). The existing dialogue/script item-grant channel (<c>dialogue_item_sync</c>/
/// <c>script_item_sync</c>, docs/research/dialogue-consequences.md §3) then mirrors that SAME
/// resref onto every other connected player — handing them the BUILDER's colour, not their own.
///
/// This is the pure decision the receiving side's <c>LootPump</c> applies to one incoming grant
/// item, immediately before it would otherwise be queued for a local give unchanged: given the
/// resref the sender actually created and what THIS player's own class list and possessed items
/// look like, decide whether to pass the item through untouched, swap in the resref this
/// player's own class would have produced, or drop the give entirely because this player
/// already has a first saber (never grant a second one).
///
/// Deliberately narrow and one-directional: nothing here changes how any OTHER item is granted,
/// and nothing here touches the SENDING side's detection (<c>DialoguePump</c>/
/// <c>ScriptItemGrantPump</c>) at all — every peer keeps announcing exactly what its own engine
/// created, and each RECEIVER independently decides, from its own locally-read class, what that
/// announcement should become for itself. See docs/research/class-and-power-grants.md §10 for
/// why this is the isolated design choice and how it depends on (and stays compatible with a
/// future change to) the shared-inventory model.
/// </summary>
public static class FirstLightsaberColorRule
{
    /// <summary>
    /// The three starter single-blade sabers Zhar's construction script (<c>k_pdan_saber16</c>)
    /// can create, and the <c>classes.2da</c> row each is tied to — VERIFIED by static decode of
    /// the shipped script against the sha256-matched GOG resources (2026-09-25),
    /// docs/research/class-and-power-grants.md §10.1. Class ids match
    /// <c>k_pdan_makejedi</c>'s own <c>AddMultiClass</c> calls (already documented in §3/§4).
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> ResrefForClass = new Dictionary<int, string>
    {
        [3] = "g_w_lghtsbr01", // Guardian - blue
        [5] = "g_w_lghtsbr04", // Sentinel - yellow
        [4] = "g_w_lghtsbr03", // Consular - green
    };

    private static readonly HashSet<string> CanonicalResrefs =
        new(ResrefForClass.Values, StringComparer.OrdinalIgnoreCase);

    /// <summary>True for exactly the three resrefs in <see cref="ResrefForClass"/> — the "does
    /// this player already have a first saber" check the receiver runs against its own
    /// possessed items before ever applying this rule, so a resend/resync/rejoin/late join is
    /// naturally idempotent (checked fresh every call, never a one-shot flag).</summary>
    public static bool IsCanonicalFirstSaber(string resref) => CanonicalResrefs.Contains(resref);

    public enum Decision
    {
        /// <summary>Not one of the three canonical starter sabers, or this receiver's own class
        /// already matches the incoming colour, or this receiver has no resolvable Jedi class
        /// yet — apply the grant exactly as announced (today's behaviour).</summary>
        PassThrough,

        /// <summary>Apply the grant, but with <see cref="Result.Resref"/> in place of what the
        /// sender announced — this receiver's own class calls for a different colour.</summary>
        Substitute,

        /// <summary>Drop the give entirely. This receiver already possesses one of the three
        /// canonical starter sabers (or received one earlier this session) — never grant a
        /// second first saber.</summary>
        Skip,
    }

    public readonly record struct Result(Decision Decision, string Resref);

    /// <param name="incomingResref">What the sender actually created and announced.</param>
    /// <param name="receiverClassIds">This player's own current class ids, in slot order
    /// (0-2 entries, <c>classes.2da</c> rows) — <c>IPlayerBridge.ReadLocalClassIds</c>.</param>
    /// <param name="receiverAlreadyHasFirstSaber">True if this player's own possessed items
    /// already include one of the three canonical starter sabers.</param>
    public static Result Evaluate(
        string incomingResref,
        IReadOnlyList<int> receiverClassIds,
        bool receiverAlreadyHasFirstSaber)
    {
        if (!IsCanonicalFirstSaber(incomingResref))
        {
            return new Result(Decision.PassThrough, incomingResref);
        }

        if (receiverAlreadyHasFirstSaber)
        {
            return new Result(Decision.Skip, incomingResref);
        }

        foreach (int classId in receiverClassIds)
        {
            if (ResrefForClass.TryGetValue(classId, out string? resref))
            {
                return string.Equals(resref, incomingResref, StringComparison.OrdinalIgnoreCase)
                    ? new Result(Decision.PassThrough, incomingResref)
                    : new Result(Decision.Substitute, resref);
            }
        }

        // This receiver has no Jedi class slot yet (or class_grant_sync/the parallel per-player
        // class work has not reached them at the moment this fires) - there is nothing to
        // substitute TO, so fall back to the builder's own colour rather than silently
        // dropping their first saber. No worse than today's behaviour, and never a raw poke of
        // a class this player does not actually have.
        return new Result(Decision.PassThrough, incomingResref);
    }
}
