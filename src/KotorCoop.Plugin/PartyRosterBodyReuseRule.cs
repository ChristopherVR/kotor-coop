namespace KotorCoop.Plugin;

/// <summary>
/// The pure decision half of <c>PartyRosterController.Reconcile</c>'s GIT-identity reuse check
/// (docs/research/dialogue-consequences.md's 2026-08-24 "stale mirrored body" entry), split out
/// the same way <see cref="PartyRosterRecruitRule"/> already is: the engine calls this decision
/// gates — <see cref="CreatureFinder.FindByObjectId"/>, <see cref="Odyssey.TrySetSlotAvailable"/>
/// — do not exist in the test runner, but the id-shaped reasoning around them does.
///
/// ⚠ THE BUG THIS CLOSES. A companion recruited on one instance through the real dialogue path
/// (<c>dialogue_recruit_sync</c>) used to always be rebuilt on a peer from scratch
/// (<see cref="Odyssey.AddAvailableNpcByTemplate"/> then <see cref="Odyssey.SpawnAvailableNpc"/>)
/// — even when that companion was ALSO a placed NPC the peer's own engine had independently
/// spawned at module load (live-confirmed 2026-08-24: Zaalbar, <c>tar_m05aa</c>, still standing
/// at his pre-recruit spot, unconverted, after the client recruited him purely through the wire).
/// Object ids are assigned deterministically in GIT order — the same assumption
/// <c>EnemyPresenceController</c>/<c>MaterialisedEnemies</c> already lean on throughout this
/// project for enemies — so the peer's own party-table object id for a slot (published every
/// frame over the roster channel this reuse check rides alongside) names the SAME body here too,
/// if one exists. <c>PartyRosterController.Reconcile</c> looks that body up
/// (<see cref="CreatureFinder.FindByObjectId"/>) before ever calling
/// <see cref="Odyssey.AddAvailableNpcByTemplate"/>, and this rule is what decides whether the
/// lookup was even worth attempting and whether its result means "reuse this one".
/// </summary>
internal static class PartyRosterBodyReuseRule
{
    /// <summary>The engine's OBJECT_INVALID sentinel (<see cref="Odyssey.ObjectInvalid"/>,
    /// 0x7F000000) — duplicated here as a plain literal rather than referenced, the same way
    /// this project's other pure-logic tests already avoid touching <c>Odyssey.cs</c> directly
    /// (see e.g. <c>EncounterTriggerStoreTests</c>/<c>TriggerRelayStoreTests</c>' own literal
    /// use of 0x7F000000).</summary>
    public const uint ObjectInvalid = 0x7F000000;

    /// <summary>
    /// Whether a peer-published object id is worth a local lookup at all — not the "no id
    /// published" sentinel (0: an older peer build, or the peer simply has no live body for
    /// that slot right now) and not <see cref="ObjectInvalid"/> (a value the real engine never
    /// hands out as a genuine id, so a peer that sent it is not the build it claims to be — the
    /// same reasoning <c>PacketCodec.ReadPartyRoster</c> already applies to the two roster
    /// masks it validates before this id is ever reached).
    /// </summary>
    public static bool IsWorthLookingUp(uint hostBodyId) => hostBodyId is not (0 or ObjectInvalid);

    /// <summary>
    /// Whether <c>Reconcile</c> should reuse the already-placed body it found instead of falling
    /// back to the off-stage recruit + fresh spawn path. True only when the id was worth looking
    /// up in the first place AND a live creature actually resolved under it locally — a 0/invalid
    /// id is never "found" by construction, so this does not need to re-check
    /// <see cref="IsWorthLookingUp"/> on its own, but callers are expected to have skipped the
    /// lookup entirely rather than pass <paramref name="foundLocally"/> true for one.
    ///
    /// ⚠ AND NEVER ONE OF OUR OWN SPAWNED BODIES. The premise of the whole check is GIT
    /// identity — the peer's id names the same PLACED creature here — and the bodies this
    /// plugin creates itself (a peer's phantom, a materialised enemy stand-in) are the one
    /// class of object that premise is false for: they are allocated at runtime, from a
    /// counter the two instances advance independently, so the peer's roster id can collide
    /// with one of ours by pure arithmetic. Reusing that body would seat a REMOTE PLAYER's
    /// avatar in this instance's party — party bar portrait, roster slot and all — which is
    /// the wrong-identity failure class docs/research/name-plates.md exists for, in the one
    /// place that shows a portrait at all.
    /// </summary>
    public static bool ShouldReuse(uint hostBodyId, bool foundLocally, bool isOurOwnSpawnedBody) =>
        IsWorthLookingUp(hostBodyId) && foundLocally && !isOurOwnSpawnedBody;
}
