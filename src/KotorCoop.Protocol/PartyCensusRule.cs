namespace KotorCoop.Protocol;

/// <summary>
/// Who counts as "one of ours" when a census decides what to publish as an ENEMY, and what a
/// mirror is allowed to build a body for.
///
/// ⚠ THE BUG THIS CLOSES (docs/research/name-plates.md, "the census leak"). The enemy census
/// used to exclude a creature only when it was the player or when its party-follow pointer
/// (<c>CSWSCreature+0x4C0</c>) was attached. That pointer is NOT a membership flag — it is
/// follow-AI state the engine attaches some time AFTER the body exists, and this project
/// already has two controllers (<c>PartyFollowController</c>, <c>FollowPhaseReleaseRule</c>)
/// whose whole reason to exist is that it is missing when you expect it: across a module
/// transition, and on any body the plugin has claimed. A companion observed inside that window
/// looks exactly like a hostile stranger, so the host publishes Trask/Carth into the enemy
/// census, and the peer — which does not necessarily hold that companion under the same runtime
/// object id, because party bodies are spawned by the party table at load rather than placed
/// and numbered in the GIT — materialises a STAND-IN for it: a companion-shaped body
/// (appearance 378 for Trask) standing next to the party after a relocation, which is the shape
/// of the "clients show as a Trask icon" report.
///
/// The fix is to ask the party TABLE instead. <c>CSWPartyTable+0x0C</c> is <c>int[9]</c>, the
/// spawned object id of each NPC roster slot, written when the body is created and left at
/// <see cref="ObjectInvalid"/> when there is none. It is a membership record, not derived AI
/// state, so it is right in exactly the windows the follow pointer is wrong.
///
/// The follow-pointer test is kept as a widening belt: it is never wrong when TRUE (a body
/// running follow-AI really is a party member), only when false. And the player's own test
/// stays — see the warning at the end of docs/research/name-plates.md about why
/// <c>!IsPartyMember</c> must never be used to identify the player: the PC's own follow pointer
/// reads non-null, measured.
/// </summary>
public static class PartyCensusRule
{
    /// <summary>The engine's OBJECT_INVALID sentinel, which a roster slot with no live body
    /// holds.</summary>
    public const uint ObjectInvalid = 0x7F000000;

    /// <summary>Whether a value read out of a party-table roster slot names a real body. 0 is
    /// "nothing written here" and <see cref="ObjectInvalid"/> is the engine's own "no body";
    /// neither is an id a creature can carry, so neither may be matched against one.</summary>
    public static bool IsRosterId(uint objectId) => objectId is not (0 or ObjectInvalid);

    /// <summary>
    /// Whether this creature belongs to THIS instance's own party and must therefore never be
    /// published as an enemy.
    ///
    /// <paramref name="followPointerAttached"/> is the old <c>CreatureRef.IsPartyMember</c>
    /// signal: trustworthy when true, meaningless when false (see the class doc). The roster is
    /// what makes the answer correct in the transition window.
    /// </summary>
    public static bool IsOwnParty(
        uint objectId, bool isPlayer, bool followPointerAttached, IReadOnlyCollection<uint>? rosterIds) =>
        isPlayer
        || followPointerAttached
        || (rosterIds is not null && IsRosterId(objectId) && rosterIds.Contains(objectId));

    /// <summary>
    /// How close a published enemy has to be to one of our own companions, wearing that
    /// companion's exact appearance row, before we call it the same creature rather than a
    /// coincidence. A leaked companion is published AT the companion's own position by
    /// construction, so this is metres, not rooms.
    /// </summary>
    public const float CompanionCoincidenceRadius = 3.0f;

    /// <summary>
    /// Whether a published enemy is really one of our own live companions arriving under a
    /// foreign id.
    ///
    /// ⚠ Appearance alone is NOT enough and must never be used alone: refusing every enemy that
    /// shares a companion's appearance row would make a genuine hostile invisible while it is
    /// still able to hurt us, which is the exact failure the materialiser exists to remove. The
    /// position is what makes it safe — a leaked companion stands where our copy of that
    /// companion already stands, and a real enemy that happens to look like Trask does not.
    /// </summary>
    public static bool CoincidesWithOwnCompanion(uint appearance, uint companionAppearance, float distanceSquared) =>
        appearance != 0
        && appearance == companionAppearance
        && distanceSquared <= CompanionCoincidenceRadius * CompanionCoincidenceRadius;

    /// <summary>
    /// Whether a mirror may build a stand-in body for an owner-published enemy id.
    ///
    /// ⚠ The receiving half of the same defect, and it is worth refusing twice: the publisher
    /// may be an older build, and the two engines number party bodies independently, so the
    /// owner's companion id can collide with nothing here and sail straight past the "does this
    /// id already exist locally" check that stops every other kind of duplicate.
    /// </summary>
    public static bool MayMaterialise(bool ownerIdIsOwnRosterBody, bool coincidesWithOwnCompanion) =>
        !ownerIdIsOwnRosterBody && !coincidesWithOwnCompanion;
}
