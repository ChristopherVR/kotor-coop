using System.Diagnostics;
using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// Drives this instance's copy of each enemy to wherever the owning peer's engine has it,
/// every frame, on the game's thread.
///
/// <see cref="CombatController"/> makes the two instances AGREE about enemy health and
/// death; this makes them agree about the fight you can actually see. Left alone, each
/// instance runs its own AI over its own copy of every creature, so the same Sith soldier
/// charges the host on one screen and stands in a doorway on the other. The owner's agent
/// publishes each enemy's position, facing and animation with the health it already sends,
/// and this controller places the local body there using the same recipe that made
/// phantoms seamless: write the server object, then pin the client twin's desired position
/// with a zero ease budget (docs/research/realtime-creature-movement.md — the renderer
/// reads the twin, and the twin otherwise eases over a fixed 512 ms).
///
/// The local AI must not fight the drive, so every driven enemy is re-claimed each pass:
/// commandable, action queue cleared, movement permitted. That is a deliberate model
/// change, not a side effect — on the mirroring instance enemies stop acting locally and
/// become a faithful presentation of the owner's fight. The local player still attacks
/// them (damage min-merges to the owner as before); what no longer happens is the private
/// local-only fight that could not be seen by anyone else. Enemy threat TO the mirroring
/// player is combat-participation work, tracked separately.
///
/// Kill switch: <c>enemy_drive=0</c> in coop.ini falls back to health-only mirroring.
/// </summary>
internal sealed unsafe partial class EnemyPresenceController(SharedBlockView block)
{
    /// <summary>The host's census ids seen alive this pass — handed to
    /// <see cref="ScriptDestroyGuardHook"/>.</summary>
    private readonly HashSet<uint> _aliveOwnerIds = [];

    /// <summary><c>script_destroy_guard</c> (default ON), set once at startup.</summary>
    public static bool ScriptDestroyGuardEnabled = true;

    /// <summary>CSWSObject + this = object type; a creature is 5 (verified, the AI
    /// master's own gate at 0x004B0C20 tests it).
    ///
    /// ⚠ RESOLVED, 2026-08-11 — AND IT WAS THE WRONG FIELD WIDTH ALL ALONG.
    ///
    /// This used to compare the whole DWORD, and on a live census enemy 0x14 read 5 while
    /// 0x25 — standing in the right place, at full health, driven fine by every other
    /// channel — read 0x40005. The old note called that "flag bits above the type" and left
    /// the test strict, on the theory that widening it to the vtable check was right but not
    /// understood (a live run of that widening also broke the peer-damage battery).
    ///
    /// The type is a WORD, not a dword: docs/re/object-model.md has it as `+0x008 | object
    /// type | word`, and the engine's own gate at 0x004B0C5A reads it as a BYTE
    /// (`mov al, [esi+8]` / `cmp al, [0x746633]`). The dword compare was dragging in
    /// adjacent per-creature state — 0x40005 is type 5 with 0x0004 living at +0x0A. So the
    /// fix is a NARROWING to the documented width, not the widening that was tried and
    /// reverted, which is a much safer change and does not weaken the test at all.
    ///
    /// This mattered for more than the census. The skipped creatures made
    /// MaterialisedEnemies' resolver return 0 for a creature that was present, so it fell
    /// through to a full-process CreatureFinder sweep to prove absence — measured live at
    /// five sweeps 200 ms apart, and it never stopped, because the false-alarm log caps at
    /// five lines while the loop runs on. See docs/research/mirror-sweep-storm.md.
    /// </summary>
    /// <summary>CSWSCreature's vtable — the reliable identity test. The object-type word
    /// at +0x08 carries FLAG BITS on live creatures (0x40005 measured), so an equality
    /// test against 5 rejects real bodies; the first live encounter spawn on tar_m03aa
    /// had its stand-in rejected by exactly that, destroyed, and respawned every
    /// half-second — one burned object id per loop.</summary>
    private const nint CreatureVtable = 0x007470C8;

    /// <summary>CSWSObject + this = object id, for cheap per-frame revalidation.</summary>
    private const int ObjId = 0x04;

    /// <summary>CSWSCreature + this = party follow info. Non-zero means the party system
    /// queues follow actions on it — that is a mirrored companion, owned by party sync,
    /// and driving it here would have two writers again.</summary>
    private const int CrePartyFollowInfo = 0x4C0;

    private readonly bool _enabled = PluginConfig.ReadBool("enemy_drive", true);

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    // Per-slot resolution cache. A pointer is revalidated every frame (id + type still
    // matching) and re-resolved when it stops — the 0g lesson: a module change frees every
    // creature and reused memory reads as plausible garbage.
    private readonly uint[] _trackedId = new uint[SharedBlock.EnemyCapacity];
    private readonly nint[] _creature = new nint[SharedBlock.EnemyCapacity];
    private readonly bool[] _retainedCandidateThisPass = new bool[SharedBlock.EnemyCapacity];

    // ROUND 46: the engine's own pre-claim state for each slot's creature, captured once on
    // first resolve (see Resolve()) so a creature handed to local_combat can be restored to
    // what the engine actually had rather than whatever the last claim/drive pass left. See
    // Odyssey.ClaimSnapshot's own doc comment and StartLocalFight's call site below.
    private readonly Odyssey.ClaimSnapshot[] _originalState = new Odyssey.ClaimSnapshot[SharedBlock.EnemyCapacity];
    private readonly bool[] _originalCaptured = new bool[SharedBlock.EnemyCapacity];

    // One-shot replay dedupe: the serial applied last, primed on first sight of a slot so
    // a swing from before tracking began (a join, a module entry) is never replayed late.
    private readonly uint[] _appliedOneShot = new uint[SharedBlock.EnemyCapacity];
    private readonly bool[] _oneShotPrimed = new bool[SharedBlock.EnemyCapacity];

    // The measure-smooth-decide half, shared with the party controller.
    //
    // Its own knob, not the phantom's smooth_ms: phantoms arrive at the 30 Hz tick, where
    // 60 ms reads as continuous, but mirrored creatures arrive on the agent's combat pump
    // (~150 ms apart). A 60 ms tau consumes most of each hop before the next lands — fast,
    // stop, fast, a visible pulse at the stream rate. A tau near the arrival interval
    // spreads each hop across the whole gap, at the cost of trailing truth by a step.
    private readonly MotionSmoother _smoother = new(
        SharedBlock.EnemyCapacity,
        Math.Clamp(PluginConfig.ReadFloat("mirror_smooth_ms", 150f), 0f, 600f) / 1000f);

    private int _drivenLogged = -1;
    private bool _offLogged;

    /// <summary>
    /// ROUND 29 DIAGNOSTIC, TEMPORARY. An owner-space object id (hex, e.g. `0x35`) that this
    /// controller will not touch AT ALL — no ClaimCreature, no PermitAttacking, no Drive
    /// (position/facing), no ClearPositionDirty, no RecordEngagement, no QueueAttackAction.
    /// Everything the base presence path does for every OTHER mirrored enemy is skipped for
    /// this one, unconditionally, regardless of local_combat. Its position on THIS instance
    /// will drift from the owner's real position — expected, not a bug, for this diagnostic.
    ///
    /// Exists to answer the one question rounds 1-28 never isolated: does a mirrored creature
    /// fail to fight because of something local_combat's OWN extra calls do (already tested,
    /// round 28, and refuted with local_combat=0 — but that still left the BASE per-frame
    /// SetPosition/SetFacing/ClaimCreature running), or because of the base presence-drive
    /// itself, which is unconditional and was never actually turned off for any creature
    /// before this round. See docs/research/shared-state-audit.md §2, Round 29.
    ///
    /// 0 (default, coop.ini unset) disables this entirely — every creature is driven as
    /// normal. Remove this field once Round 29's question is settled either way.
    /// </summary>
    private static readonly uint DiagUndrivenObjectId = ParseHexOrZero(PluginConfig.Read("local_combat_diag_undriven_id"));


    /// <summary>
    /// How close a driven enemy's local body must be to this player before its owner's report
    /// may be absorbed, in world units (coop.ini <c>local_combat_range</c>).
    ///
    /// ⚠ This closes the hole the identity test alone leaves open. A mirrored enemy is placed
    /// where the OWNER's engine has it, which is next to the OWNER's player — so when the two
    /// players are apart the local copy can never reach this player, never lands a hit, and a
    /// blanket absorb would make this player invulnerable for the whole fight. The measured
    /// "apart" case is ~120 units (docs/research/combat-when-apart.md §1b), so 20 separates
    /// the two cases with a wide margin instead of trying to model weapon reach.
    /// </summary>
    private static readonly float EngagementRangeSquared =
        Squared(Math.Clamp(PluginConfig.ReadFloat("local_combat_range", 20f), 1f, 200f));

    private static float Squared(float value) => value * value;

    private static bool IsLehonModule()
    {
        byte* module = stackalloc byte[9];
        module[0] = (byte)'u';
        module[1] = (byte)'n';
        module[2] = (byte)'k';
        module[3] = (byte)'_';
        module[4] = (byte)'m';
        module[5] = (byte)'4';
        module[6] = (byte)'4';
        module[7] = (byte)'a';
        module[8] = (byte)'c';
        return Odyssey.ModuleNameEquals(module, 9);
    }

    /// <summary>
    /// ROUND 55: how close a hostile census creature must stand to THIS player before it may be
    /// pulled out of the census to fight immediately, in world units (coop.ini
    /// <c>local_combat_engage_range</c>). Deliberately its own, tighter knob from
    /// <see cref="EngagementRangeSquared"/> above: that one licenses absorbing forwarded damage
    /// for whichever creature IS already fighting (wide, ~20u, because a driven body's position
    /// is the owner's and only needs to be "close enough to be hitting this player here"); this
    /// one decides whether to START a fight at all, and defaults to
    /// `tar10_vulkpoo002`'s own stock `PerceptionRange` (11, read from its `.utc` — round 54's
    /// no-plugin control) rather than a value picked for the absorb math, since the whole point
    /// is "would this creature's own perception plausibly have noticed the player," not "is a
    /// swing physically reaching."
    /// </summary>
    private static readonly float ImmediateEngageRange =
        Math.Clamp(PluginConfig.ReadFloat("local_combat_engage_range", 11f), 1f, 200f);


    // ---- ROUND 39 / N-SLOT (docs/research/combat-participation.md §11): the actual
    // local_combat mechanism, built from what rounds 9-38 proved works (excluded from the
    // census entirely, script-fired) rather than what the original design guessed would work
    // (QueueAttackAction, proven a permanent no-op — see docs/research/shared-state-audit.md
    // §2). Up to LocalCombatSlots simultaneous fights (default 3, clamp 1..
    // SharedBlock.LocalCombatSlotCapacity): a creature beyond that still simply waits its turn,
    // exactly as it always did with one slot. Frame thread only, both writes and reads, same as
    // the engagement record above.

    /// <summary>Per physical slot: 0 = that slot is not currently letting anything fight
    /// locally. Non-zero = the OWNER-space id of the mirrored enemy this instance has pulled
    /// out of the census, in that slot, so its own AI can complete a real combat round. Sized
    /// to <see cref="SharedBlock.LocalCombatSlotCapacity"/> regardless of
    /// <see cref="LocalCombatSlots"/> — slots at or past that configured count simply never get
    /// written, which is exactly what makes <c>local_combat_slots=1</c> reproduce the original
    /// single-fight behaviour bit for bit.</summary>
    private readonly uint[] _locallyFightingId = new uint[SharedBlock.LocalCombatSlotCapacity];

    /// <summary>Per physical slot: when that slot's current local fight started
    /// (Stopwatch-relative seconds, same clock <see cref="OnFrame"/> already uses), for the
    /// timeout safety net.</summary>
    private readonly double[] _locallyFightingSince = new double[SharedBlock.LocalCombatSlotCapacity];

    /// <summary>Per physical slot: whether <see cref="LocalFightEngageFallbackRule"/>'s direct-drive
    /// fallback has already fired once for the CURRENTLY-armed fight in that slot — the one-shot
    /// guard (docs/research/combat-participation.md §12). Reset to false whenever a slot's fight
    /// ends (either release path in <see cref="MaintainLocalFightSlot"/>/<see cref="DriveOne"/>)
    /// or a fresh one arms (<see cref="StartLocalFight"/>), and by <see cref="Invalidate"/> on a
    /// module change, same discipline as every other per-slot fight-scoped array here.</summary>
    private readonly bool[] _engageFallbackIssued = new bool[SharedBlock.LocalCombatSlotCapacity];

    /// <summary>Per physical slot: when that slot last had evidence its fight is still real —
    /// <see cref="LocalFightActivityRule"/>'s own clock, docs/research/combat-participation.md
    /// §13. Distinct from <see cref="_locallyFightingSince"/> (the fight's START, which the
    /// diagnostic log and the engage fallback's grace window both still key off) — this is what
    /// <see cref="LocalCombatTimeoutSeconds"/> actually measures against now: seconds since the
    /// last damage-dealt rise or "targeting us" read, not seconds since the fight armed.
    /// Initialised to the arming timestamp by <see cref="StartLocalFight"/>, same as
    /// <see cref="_locallyFightingSince"/>, so a brand-new fight gets the same grace an idle one
    /// would have gotten before ever showing evidence.</summary>
    private readonly double[] _lastActivityAt = new double[SharedBlock.LocalCombatSlotCapacity];

    /// <summary>Per physical slot: <see cref="DamageDealtTable"/>'s cumulative total for that
    /// slot's creature as of the last pass, so <see cref="LocalFightActivityRule"/> can be told
    /// whether it ROSE rather than re-deriving the comparison itself — see
    /// <see cref="MaintainLocalFightSlot"/>. Seeded from the table's own current total when
    /// <see cref="StartLocalFight"/> arms a slot (not zero — a creature already fought earlier
    /// this module load, then re-armed after a release, must not have its OLD total read back
    /// as a fresh rise the instant the new fight starts).</summary>
    private readonly uint[] _lastDealtSeen = new uint[SharedBlock.LocalCombatSlotCapacity];

    /// <summary>Per physical slot: rate limit for the "still active, timeout extended" log line
    /// — <see cref="ActivityRefreshLogIntervalSeconds"/>, not every pass, since a real fight
    /// refreshes constantly. <see cref="double.NegativeInfinity"/> (never logged) whenever
    /// <see cref="StartLocalFight"/> arms a slot, so the very first refresh of a fresh fight
    /// always logs immediately rather than inheriting a stale slot's throttle.</summary>
    private readonly double[] _lastActivityLogAt = new double[SharedBlock.LocalCombatSlotCapacity];

    /// <summary>How often the "still active" refresh log may repeat for one slot. A real fight
    /// can refresh every single pass; logging that would drown everything else this file
    /// prints.</summary>
    private const double ActivityRefreshLogIntervalSeconds = 5.0;

    /// <summary>TEMPORARY DIAGNOSTIC (2026-08-21, "does it close distance"), per physical slot —
    /// last time that slot's excluded creature's own position was logged against the player's,
    /// so a full fight cycle produces a position-over-time curve instead of one measurement.
    /// Remove once the distance-closing question is settled.</summary>
    private readonly double[] _lastPositionLogAt = new double[SharedBlock.LocalCombatSlotCapacity];

    /// <summary>DIAGNOSTIC (docs/research/shared-state-audit.md §2, round 41), per physical
    /// slot, edge-triggered: the id this leak was last reported for in that slot, so a
    /// genuinely NEW race (a different excluded id, or the same id after a gap) logs
    /// immediately while an ONGOING one does not repeat every throttled second for a fight's
    /// whole duration. 0 when nothing is currently being reported for that slot. See
    /// <see cref="_stillSeeingLoggedAt"/> for the coarse keep-alive that still covers a leak
    /// stuck open for the whole fight.</summary>
    private readonly uint[] _stillSeeingReportedId = new uint[SharedBlock.LocalCombatSlotCapacity];

    /// <summary>Once a slot's leak is already being reported
    /// (<see cref="_stillSeeingReportedId"/>[slot] != 0), a 5s keep-alive so a leak that
    /// genuinely never closes for an entire fight still shows up in the log instead of going
    /// silent after its one edge — round 55's own live pass measured this resolving within 1-2
    /// agent pumps almost always, but round 55's player-style pass (2026-08-22) found stretches
    /// where it did not, and those are exactly the case worth keeping visible. 5s, not 1s: an
    /// edge-triggered diagnostic's whole point is fewer lines, and DriveOne's own skip already
    /// makes every one of these frames safe regardless of how long the upstream leak
    /// persists.</summary>
    private readonly double[] _stillSeeingLoggedAt = new double[SharedBlock.LocalCombatSlotCapacity];

    /// <summary>Hard ceiling on how long one creature may sit excluded from the census WITH NO
    /// EVIDENCE the fight is still real. A fight that never ends any other way (the player walks
    /// off without the engine's own disengage logic catching it, a stuck state this investigation
    /// has not observed but has no way to rule out either) still returns the creature to normal
    /// mirroring on its own. coop.ini `local_combat_timeout_s`. This is a FALLBACK ceiling, not
    /// the trigger: round 55's <see cref="SelectImmediateCandidate"/> arms a fresh fight the same
    /// frame a slot frees, so a second nearby hostile no longer has to wait out this whole window
    /// for its turn.
    ///
    /// ⚠ §13 (2026-09-02): measured against <see cref="_lastActivityAt"/>, NOT
    /// <see cref="_locallyFightingSince"/> — this bounds how long ONE fight may run with no
    /// <see cref="LocalFightActivityRule"/> evidence, not how long it may run at all. Before this,
    /// the clock was the fight's own START, so a genuinely ongoing fight (the host was applying
    /// the client's forwarded hits on the excluded creature the whole time — tar_m10aa, live) got
    /// released and immediately re-armed every single window regardless. Shared by every slot;
    /// there is no per-slot knob.</summary>
    private static readonly double LocalCombatTimeoutSeconds =
        Math.Clamp(PluginConfig.ReadFloat("local_combat_timeout_s", 25f), 5f, 120f);

    // ---- ROUND 55 / N-SLOT: the immediate proximity/targeting-us trigger
    // (docs/research/shared-state-audit.md §2, round 55; docs/research/combat-participation.md
    // §11). Before round 55 the ONLY thing that ever pulled a creature into local_combat was
    // DriveOne finding it eligible in whatever order the owner's census happened to enumerate
    // that frame — first found wins, which in practice meant a creature only got its turn once
    // whatever slot was already fighting timed out, up to LocalCombatTimeoutSeconds later,
    // looking indistinguishable from a round-robin timer even though nothing in this file ever
    // scheduled one. SelectImmediateCandidate looks at every eligible candidate THIS FRAME
    // before arming anything, ranks them with LocalFightCandidateRule.Beats, and hands the
    // ranked list to LocalFightSlotAssignmentRule to fill every FREE physical slot at once
    // (not just one) — the creature(s) that start fighting are whichever the rule ranks
    // highest, rather than an enumeration-order accident, and a freed slot is refilled the very
    // next frame.

    /// <summary>Per physical slot: the CENSUS index (<c>i</c> into
    /// <c>block.GetEnemyPresence</c>) <see cref="SelectImmediateCandidate"/> chose to trigger
    /// into that slot THIS frame, or -1 when that slot stayed occupied or nothing was offered
    /// to it. Computed once per <see cref="OnFrame"/> pass, before the main drive loop runs, so
    /// <see cref="DriveOne"/> only ever has to ask "was I chosen for some free slot" instead of
    /// re-deriving eligibility itself.</summary>
    private readonly int[] _immediateTriggerCensusIndex = new int[SharedBlock.LocalCombatSlotCapacity];

    /// <summary>Per physical slot: the <see cref="LocalFightPriority"/> the winner assigned to
    /// <see cref="_immediateTriggerCensusIndex"/>[slot] was chosen with, for
    /// <see cref="StartLocalFight"/>'s own arm-reason log line.</summary>
    private readonly LocalFightPriority[] _immediateTriggerPriority = new LocalFightPriority[SharedBlock.LocalCombatSlotCapacity];

    /// <summary>Edge-triggered log dedupe, per physical slot: only the OWNER id that most
    /// recently won that slot, so a creature that keeps winning frame after frame (nothing else
    /// nearby yet) does not spam a line every single pass.</summary>
    private readonly uint[] _lastLoggedImmediateOwnerId = new uint[SharedBlock.LocalCombatSlotCapacity];

    /// <summary>The owner-space cumulative total <see cref="block"/>'s <c>IncomingDamage</c>
    /// read on the most recent frame — mirrors <see cref="IncomingDamageController"/>'s own
    /// "cumulative, never a delta" reasoning so a genuine NEW hit can be told apart from the
    /// same total re-read every frame.</summary>
    private uint _lastIncomingDamageSeen;

    /// <summary>Owner-space id of whichever creature most recently landed a NEW hit on this
    /// player's phantom, per the owner's own damage report — the strongest "is this creature
    /// fighting us right now" signal this design has, since it is not inferred from position at
    /// all, it is the owner's engine saying so. 0 when nothing has hit recently or the last
    /// known attacker id was invalidated by a module change (<see cref="Invalidate"/>).</summary>
    private uint _phantomAttackerId;

    /// <summary>When <see cref="_phantomAttackerId"/> was last confirmed by a genuine NEW hit
    /// (Stopwatch-relative seconds, same clock <see cref="OnFrame"/> already uses).</summary>
    private double _phantomAttackerAt = double.NegativeInfinity;

    /// <summary>Past this the attacker id is stale evidence — the wire total simply has not
    /// moved in a while, which is the ordinary "not currently being hit" case, not proof this
    /// creature stopped. coop.ini <c>local_combat_targeting_us_fresh_s</c>.</summary>
    private static readonly double PhantomAttackerFreshSeconds =
        Math.Clamp(PluginConfig.ReadFloat("local_combat_targeting_us_fresh_s", 2f), 0.5f, 10f);

    // ---- ITEM 14 (docs/roadmap-seamless.md Tier 2): surfacing the one-fight-at-a-time limit.
    // _locallyFightingId above holds exactly one creature; everything else that clears
    // LocalFightCandidateRule.Evaluate's gates this scan was, until now, silently deferred with
    // nothing recording it. LocalCombatDeferralRule is the pure set arithmetic; everything below
    // just feeds it live facts and turns "the deferred set changed" into a log line and
    // (optionally) a HUD line.

    /// <summary>Every owner id SelectImmediateCandidate found eligible THIS scan (hostile,
    /// GIT-identical, scripted, in range — cleared every LocalFightCandidateRule.Evaluate gate),
    /// regardless of whether it won a slot. Rebuilt from scratch each scan.</summary>
    private readonly uint[] _eligibleOwnerId = new uint[SharedBlock.EnemyCapacity];

    /// <summary>Parallel to <see cref="_eligibleOwnerId"/>: that candidate's
    /// <see cref="LocalFightPriority"/>, fed to <see cref="LocalFightSlotAssignmentRule"/>.</summary>
    private readonly LocalFightPriority[] _eligiblePriority = new LocalFightPriority[SharedBlock.EnemyCapacity];

    /// <summary>Parallel to <see cref="_eligibleOwnerId"/>: that candidate's planar distance to
    /// the player, fed to <see cref="LocalFightSlotAssignmentRule"/>.</summary>
    private readonly float[] _eligibleDistance = new float[SharedBlock.EnemyCapacity];

    /// <summary>Parallel to <see cref="_eligibleOwnerId"/>: that candidate's CENSUS index (the
    /// <c>i</c> <see cref="DriveOne"/> will see it at), so a winner can be threaded back to
    /// <see cref="_immediateTriggerCensusIndex"/> without a second census scan.</summary>
    private readonly int[] _eligibleCensusIndex = new int[SharedBlock.EnemyCapacity];

    /// <summary>Scratch space <see cref="LocalFightSlotAssignmentRule.Assign"/> uses to mark an
    /// eligible candidate already won by an earlier (lower-numbered) physical slot this scan —
    /// the "a creature can never hold two slots" guarantee. Reset by the rule itself every
    /// call.</summary>
    private readonly bool[] _eligibleUsedScratch = new bool[SharedBlock.EnemyCapacity];

    /// <summary>The deferred set computed from <see cref="_eligibleOwnerId"/> this scan —
    /// <see cref="LocalCombatDeferralRule.ComputeDeferred"/>'s output.</summary>
    private readonly uint[] _deferredOwnerId = new uint[SharedBlock.EnemyCapacity];
    private int _deferredCount;

    /// <summary>The deferred set as of the last scan that actually logged/redrew — compared
    /// against <see cref="_deferredOwnerId"/> every scan so a genuinely new set (not merely a
    /// census re-sort of the same ids) is what triggers the edge.</summary>
    private readonly uint[] _lastDeferredOwnerId = new uint[SharedBlock.EnemyCapacity];
    private int _lastDeferredCount;

    /// <summary>Per physical slot: the owner id that HOLDS it as of this scan's verdict — either
    /// still fighting from a previous frame, or this frame's freshly-assigned winner, or 0 if
    /// the slot stays free. Fed to <see cref="LocalCombatDeferralRule.ComputeDeferred"/> as the
    /// holder SET (N-wide, not a single id) and to the log line's holder summary.</summary>
    private readonly uint[] _slotHolderOwnerId = new uint[SharedBlock.LocalCombatSlotCapacity];

    /// <summary>How often (frames) the deferred-set scan runs WHILE a fight is already in
    /// progress — a separate throttle from the immediate-trigger scan above, which must stay
    /// every-frame to arm a fresh fight without delay. "How many others are waiting" does not
    /// need that precision: every gate here costs an engine call
    /// (<see cref="Odyssey.GetAIStateReputation"/>, <see cref="Odyssey.HasCreatureScriptSlot0x260"/>)
    /// per candidate, and running that at the full frame rate for the whole length of every fight
    /// would be real, avoidable cost for a diagnostic line. 20 frames is still several times a
    /// second at this hook's ~317 Hz — fast enough that "N deferred" never looks stuck, far
    /// cheaper than every frame.</summary>
    private const int DeferredScanIntervalFrames = 20;
    private int _deferredScanCountdown;

    // Row 9, column 8: the "in-world overlay" column DialogueSpectateController (row 4),
    // InteractionLockController (rows 6/7) and MinigameNoticeController (row 8) already share —
    // see MinigameNoticeController's own row inventory comment. Row 9 is the first free one on
    // that column, and clear of column 3's connectivity stack (rows 3/5/6) too.
    private const int DeferredNoticeColumn = 8;
    private const int DeferredNoticeRow = 9;
    private const float DeferredNoticeLife = 0.5f;

    /// <summary>coop.ini `local_combat_deferred_notice` (default ON). Gates ONLY the HUD line —
    /// the log line below always fires regardless, since surfacing the limit at all is this
    /// item's whole point.</summary>
    private readonly bool _deferredNoticeEnabled = PluginConfig.ReadBool("local_combat_deferred_notice", true);

    private string? _deferredNoticeText;
    private byte[] _deferredNoticeBytes = [];

    /// <summary>
    /// Forgets everything on a module change: ids reset across transitions, every cached
    /// pointer is freed memory, and the published slots are stale until the owning agent's
    /// next pump.
    /// </summary>
    public void Invalidate()
    {
        for (int i = 0; i < SharedBlock.EnemyCapacity; i++)
        {
            Forget(i);
        }

        block.EnemyPresenceCount = 0;
        _drivenLogged = -1;

        // Ids are per module load: a stale engagement record would license absorbing damage
        // billed to a completely different creature.
        _engagedCount = 0;
        _engagedAtTicks = 0;

        // ROUND 39 / N-SLOT: same reason — a fight tracked by an id from the module just left
        // behind must not keep a completely different creature in the new module excluded from
        // the census by coincidence of numeric id reuse. Every physical slot, not just one.
        for (int slot = 0; slot < SharedBlock.LocalCombatSlotCapacity; slot++)
        {
            _locallyFightingId[slot] = 0;

            // 2026-08-22 / N-SLOT: an id from the module just left behind must not suppress the
            // "STILL SEEING" diagnostic's fresh edge for a same-numbered id that turns up
            // genuinely new in this one.
            _stillSeeingReportedId[slot] = 0;

            // §12: same reason — an id from the module just left behind must not leave the
            // fallback's one-shot guard latched against a completely different creature that
            // happens to arm this same physical slot next.
            _engageFallbackIssued[slot] = false;

            // §13: same reason again — an id from the module just left behind must not leave
            // this slot's inactivity clock, dealt-damage baseline or log throttle latched
            // against a completely different creature next.
            _lastActivityAt[slot] = 0;
            _lastDealtSeen[slot] = 0;
            _lastActivityLogAt[slot] = double.NegativeInfinity;

            _immediateTriggerCensusIndex[slot] = -1;
            _lastLoggedImmediateOwnerId[slot] = 0;
            _slotHolderOwnerId[slot] = 0;

            // ROUND 52: same reason, for the ground-truth diagnostic's own static mirrors.
            _locallyFightingIdsShared[slot] = 0;
        }

        block.LocalCombatExcludedId = 0;
        block.SetLocalCombatExcludedIds(_locallyFightingId);

        _drivenCount = 0;

        // ROUND 55: same reason again — the cumulative wire total does NOT reset on a module
        // change (IncomingDamageController's own doc comment: resetting it would replay the
        // session's whole damage history), so re-sync to whatever it already reads rather than
        // zeroing it, or the very next frame would read it as one giant "new hit" and hand a
        // stale, wrong-module attacker id top priority. The id itself is invalidated
        // unconditionally — an old module's id could coincidentally name a live creature here.
        _lastIncomingDamageSeen = block.IncomingDamage;
        _phantomAttackerId = 0;
        _phantomAttackerAt = double.NegativeInfinity;

        // ITEM 14: a stale deferred-hostiles line must not keep drawing after the session that
        // produced it is gone — DrawDeferredNotice runs ahead of every early return in OnFrame,
        // so this is the only place that stops it once the session itself has ended.
        _deferredCount = 0;
        _lastDeferredCount = 0;
        _deferredNoticeText = null;
        _deferredNoticeBytes = [];
        _deferredScanCountdown = 0;
    }

    public void OnFrame()
    {
        double now = _clock.Elapsed.TotalSeconds;

        // ROUND 55: ahead of everything else, same discipline as the engagement reset below —
        // a hit can land on our phantom regardless of what else this pass does or skips, and a
        // stale read here would misattribute "who is fighting us right now" to whatever id
        // happened to be here last.
        UpdatePhantomAttackerTracking(now);

        // Rebuilt from scratch every pass, so a body that stops being driven — or walks out
        // of range — stops licensing an absorb on the very next pass rather than decaying.
        // Reset HERE, ahead of everything below including MaintainLocalFight, so its own
        // contribution (the excluded creature's id, re-added every frame it is still a real
        // fight) is not immediately wiped by a reset that used to run much later.
        _engagedCount = 0;

        // ROUND 52: same reset shape for the ground-truth diagnostic's "driven this pass"
        // set — rebuilt from scratch every frame so a creature that stops being mirrored
        // stops being reported as one on the very next pass.
        _drivenCount = 0;

        // ROUND 39: ahead of every early return below, on purpose. A conversation opening,
        // the session command changing, or this controller being disabled mid-fight must
        // never be able to leave a creature excluded from the census with nothing left that
        // ever checks on it — the timeout safety net only means anything if it is actually
        // reached every single frame, not just the frames where the rest of this method also
        // runs. Also covers the count==0 case below directly: excluding the one creature
        // being locally fought is exactly what can make EnemyPresenceCount drop to 0.
        //
        // Resolved fresh, never taken from the block: the agent-written pointer survives a
        // module transition stale, and RecordEngagement/MaintainLocalFight both dereference
        // this (same fault class as the EncounterController travel-follow crash).
        nint player = Odyssey.GetPlayerCreaturePointer();
        MaintainLocalFight(now, player);
        block.SetLocalCombatExcludedIds(_locallyFightingId);

        // Backward-compat scalar mirror (see SharedBlock.OffsetLocalCombatExcludedId's own
        // comment): slot 0 only, for one release.
        block.LocalCombatExcludedId = _locallyFightingId[0];

        // ROUND 52 / N-SLOT: mirrored into the static array PhantomHitHook.OnDamage reads to
        // recognise "a currently locally-fighting creature" for the ground-truth log.
        Array.Copy(_locallyFightingId, _locallyFightingIdsShared, SharedBlock.LocalCombatSlotCapacity);

        // ITEM 14: ahead of every early return below, same discipline as MaintainLocalFight
        // above — the queued text node fades on its own life countdown (~0.5s) the instant this
        // stops being called, so redrawing here unconditionally is what makes the notice clear
        // itself the moment the fight it describes ends, with no separate cleanup path needed.
        DrawDeferredNotice();

        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            MaterialisedEnemies.ClearRetainedAliases();
            // Still sweep: a session that ended leaves materialised bodies with nobody
            // refreshing them, and they must not stand around (or reach a save) forever.
            MaterialisedEnemies.Sweep(now);
            PublishMaterialisedOwnerIds();
            return;
        }

        if (!_enabled)
        {
            MaterialisedEnemies.DisableRetainedAliases();
            if (!_offLogged)
            {
                _offLogged = true;
                PluginHost.Log("enemy drive: disabled by coop.ini (enemy_drive=0)");
            }

            return;
        }

        uint count = Math.Min(block.EnemyPresenceCount, (uint)SharedBlock.EnemyCapacity);
        bool hostRosterValid = block.TryGetHostPartyRoster(
            out uint hostAvailable, out uint hostParty);
        bool localRosterValid = Odyssey.TryReadPartyRoster(out uint localAvailable, out uint localParty);
        bool lehonModule = IsLehonModule();

        if (lehonModule && hostRosterValid)
        {
            for (int rosterSlot = 4; rosterSlot <= 5; rosterSlot++)
            {
                uint bit = 1u << rosterSlot;
                uint hostId = block.GetHostPartyRosterObjectId(rosterSlot);
                uint localId = block.GetLocalPartyRosterObjectId(rosterSlot);
                if (PartyCensusRule.IsRosterId(hostId)
                    && localId != 0
                    && (hostAvailable & bit) == 0
                    && (hostParty & bit) == 0)
                {
                    MaterialisedEnemies.QuarantineRetainedLocal(localId, rosterSlot);
                }
            }
        }

        // Owner/local aliases are routable only while the exact host enemy row and both
        // dismissed roster states are visible this frame. A zero census or unreadable snapshot
        // revokes them immediately; the normal five-second lease is not authorization for hits.
        if (MaterialisedEnemies.RetainedCount != 0)
        {
            MaterialisedEnemies.PruneUnprovenRetained(entry =>
                MaterialisedEnemies.TryGetCurrentRetainedLocal(entry.OwnerId, block, out _));
        }

        // A local conversation owns the stage: dialogue positions/disarms creatures, so this
        // pass still does not drive them. Retained translations were nevertheless revalidated
        // above before any damage hook can use stale campaign state.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        if (count == 0)
        {
            MaterialisedEnemies.Sweep(now);
            PublishMaterialisedOwnerIds();
            return;
        }

        Array.Clear(_retainedCandidateThisPass, 0, (int)count);

        // Mark exact host candidates before local-combat candidate selection. Until PartyPresence
        // successfully writes the local faction, these are deliberately not routable aliases and
        // never receive an enemy stand-in.
        for (int i = 0; i < count; i++)
        {
            EnemyPresenceView enemy = block.GetEnemyPresence(i);
            if (enemy.Faction != DarkSideHostileCompanionRule.HostileFaction
                || !MaterialisedEnemies.IsRetainedOwnerCandidate(enemy.ObjectId, block))
            {
                continue;
            }

            _retainedCandidateThisPass[i] = true;
            for (int rosterSlot = 4; rosterSlot <= 5; rosterSlot++)
            {
                if (block.GetHostPartyRosterObjectId(rosterSlot) == enemy.ObjectId)
                {
                    uint bit = 1u << rosterSlot;
                    uint localId = block.GetLocalPartyRosterObjectId(rosterSlot);
                    if (localId != 0)
                    {
                        MaterialisedEnemies.QuarantineRetainedLocal(localId, rosterSlot);
                        if (localRosterValid
                            && (localAvailable & bit) == 0
                            && (localParty & bit) == 0)
                        {
                            PhantomHitHook.ClearCompanionWatch(localId);
                        }
                    }

                    break;
                }
            }
        }

        // ROUND 55: decided ONCE, before any slot is claimed or driven this pass, so DriveOne
        // never has to pick "whichever slot got here first" — see the field's own doc comment.
        SelectImmediateCandidate(count, player);

        int driven = 0;
        _aliveOwnerIds.Clear();

        for (int i = 0; i < count; i++)
        {
            EnemyPresenceView enemy = block.GetEnemyPresence(i);
            if (!enemy.Dead)
            {
                _aliveOwnerIds.Add(enemy.ObjectId);
            }

            bool alreadyRetained = MaterialisedEnemies.IsRetainedOwner(enemy.ObjectId);
            if (_retainedCandidateThisPass[i] && !alreadyRetained)
            {
                continue; // do not resolve/materialise a reserved Lehon slot before handoff commit
            }

            if (alreadyRetained && (!hostRosterValid || !localRosterValid || !lehonModule))
            {
                MaterialisedEnemies.ForgetOwner(enemy.ObjectId);
                continue;
            }

            int matchingRosterSlot = -1;
            if (hostRosterValid)
            {
                for (int rosterSlot = 4; rosterSlot <= 5; rosterSlot++)
                {
                    if (enemy.ObjectId == block.GetHostPartyRosterObjectId(rosterSlot))
                    {
                        matchingRosterSlot = rosterSlot;
                        break;
                    }
                }
            }

            if (alreadyRetained && matchingRosterSlot < 0)
            {
                MaterialisedEnemies.ForgetOwner(enemy.ObjectId);
                continue;
            }

            bool retainedCandidate = lehonModule
                && matchingRosterSlot >= 0
                && enemy.Faction == DarkSideHostileCompanionRule.HostileFaction
                && (hostAvailable & (1u << matchingRosterSlot)) == 0
                && (hostParty & (1u << matchingRosterSlot)) == 0;

            if (matchingRosterSlot >= 0 && !retainedCandidate)
            {
                // The host has changed faction or recruited the slot again. Drop only the
                // translation so the party channel can own its native body again.
                MaterialisedEnemies.ForgetOwner(enemy.ObjectId);
                continue;
            }

            if (retainedCandidate)
            {
                if (!alreadyRetained)
                {
                    // PartyPresence commits after a successful faction write later in the frame.
                    // In the meantime this is neither a drive target nor a materialisation target.
                    continue;
                }

                PhantomHitHook.ClearCompanionWatch(block.GetLocalPartyRosterObjectId(matchingRosterSlot));
            }

            // ROUND 50: the decisive control. Skip this id COMPLETELY — no Seen (so no
            // materialised-body lease refresh either, if it ever were one), no Resolve, no
            // ClaimCreature, no PermitAttacking, no Drive, no engagement recording, no trigger
            // check. Still counted in `count` above (still published by the owner, still
            // "listed") — just never reached by anything below this line. See
            // LocalCombatBypassId's own doc comment.
            if (LocalCombatBypassId != 0 && enemy.ObjectId == LocalCombatBypassId)
            {
                continue;
            }

            // Every listed id — dead or alive — keeps its materialised body's lease
            // fresh; a corpse the owner still publishes must keep lying there.
            MaterialisedEnemies.Seen(enemy.ObjectId, now);

            if (DriveOne(i, enemy, now, player))
            {
                driven++;
            }
        }

        MaterialisedEnemies.Sweep(now);
        PublishMaterialisedOwnerIds();

        // The host keeps these alive: a local script may not destroy them (Rilka, 2026-09-26).
        if (ScriptDestroyGuardEnabled)
        {
            ScriptDestroyGuardHook.SetProtected(_aliveOwnerIds);
        }

        // ⚠ STAMPED ONLY HERE, at the bottom of a pass that actually ran. Every early return
        // above -- no session, drive disabled, a local conversation, an empty census -- leaves
        // the stamp where it was, so IsLocallyEngaged goes false within a second and
        // IncomingDamageController APPLIES instead of absorbing. That is deliberate: the
        // conversation hold in particular used to be absorbed, when the pre-local_combat code
        // held that debt on purpose.
        _engagedAtTicks = DateTime.UtcNow.Ticks;

        // One line when the census changes, not per frame.
        if (driven != _drivenLogged)
        {
            _drivenLogged = driven;
            PluginHost.Log($"enemy drive: mirroring {driven} of the owner's {count} enemies");
        }
    }

    /// <summary>
    /// ROUND 55. Reads the owner's own damage report for a genuinely NEW hit on this player's
    /// phantom (block.IncomingDamage is cumulative, exactly like IncomingDamageController's own
    /// owed/applied pair, so a rising total is the "new hit" edge and a flat one is not) and, if
    /// one arrived, records its attacker as "fighting us right now" for
    /// <see cref="PhantomAttackerFreshSeconds"/>. Independent of local_combat's own absorb
    /// state — this total keeps arriving whether or not anything is currently being absorbed,
    /// which is exactly what makes it useful as a trigger signal rather than only an apply
    /// target.
    /// </summary>
    private void UpdatePhantomAttackerTracking(double now)
    {
        uint incoming = block.IncomingDamage;
        if (incoming > _lastIncomingDamageSeen)
        {
            _phantomAttackerId = block.IncomingDamageAttacker;
            _phantomAttackerAt = now;
        }

        _lastIncomingDamageSeen = incoming;
    }

    /// <summary>Whether <paramref name="ownerObjectId"/> is the creature the owner's own damage
    /// report most recently named as landing a hit on this player's phantom, within the
    /// freshness window — see <see cref="UpdatePhantomAttackerTracking"/>.</summary>
    private bool IsAttackingOurPhantom(uint ownerObjectId, double now) =>
        ownerObjectId != 0 && ownerObjectId == _phantomAttackerId
        && now - _phantomAttackerAt <= PhantomAttackerFreshSeconds;

    /// <summary>
    /// ROUND 55 / N-SLOT (docs/research/combat-participation.md §11). Chooses which FREE
    /// physical slot(s), if any, <see cref="DriveOne"/> should arm into a real fight THIS frame
    /// — every eligible candidate is considered before anything is claimed, ranked by
    /// <see cref="LocalFightCandidateRule"/> (attacking our phantom right now, else nearest
    /// hostile within <see cref="ImmediateEngageRange"/>), and handed to
    /// <see cref="LocalFightSlotAssignmentRule"/> to fill every free slot at once rather than
    /// whichever slot the owner's census happened to enumerate first. Only ever considers
    /// GIT-identical creatures (a materialised stand-in is never eligible — see DriveOne's own
    /// "GIT-IDENTICAL CREATURES ONLY" note) and reuses <see cref="Resolve"/>'s own cache, so
    /// calling it here and again from the main loop a few lines later costs nothing extra on the
    /// common cache-hit path.
    /// </summary>
    private void SelectImmediateCandidate(uint count, nint player)
    {
        for (int slot = 0; slot < SharedBlock.LocalCombatSlotCapacity; slot++)
        {
            _immediateTriggerCensusIndex[slot] = -1;
        }

        if (!LocalCombat || player == 0)
        {
            _deferredScanCountdown = 0;
            UpdateDeferredNotice(eligibleCount: 0);
            return;
        }

        int freeSlotCount = 0;
        for (int slot = 0; slot < LocalCombatSlots; slot++)
        {
            if (_locallyFightingId[slot] == 0)
            {
                freeSlotCount++;
            }
        }

        if (freeSlotCount == 0)
        {
            // ITEM 14: winner selection is moot while every slot is already occupied — only the
            // deferred-set scan below still needs to run, and it is deliberately throttled (see
            // DeferredScanIntervalFrames's own doc comment) rather than paying an engine call per
            // candidate every single frame for the whole length of a fight.
            if (_deferredScanCountdown > 0)
            {
                _deferredScanCountdown--;
                return;
            }

            _deferredScanCountdown = DeferredScanIntervalFrames;
        }
        else
        {
            // The instant a slot frees, the very next scan should be fresh — a fight that just
            // ended must not wait out whatever was left of the previous countdown.
            _deferredScanCountdown = 0;
        }

        uint playerId = Odyssey.GetPlayerObjectId();
        if (playerId == 0)
        {
            UpdateDeferredNotice(eligibleCount: 0);
            return;
        }

        double now = _clock.Elapsed.TotalSeconds;
        int eligibleCount = 0;

        for (int i = 0; i < count; i++)
        {
            EnemyPresenceView enemy = block.GetEnemyPresence(i);

            if (enemy.ObjectId == 0 || enemy.ObjectId == Odyssey.ObjectInvalid || enemy.Dead
                || (LocalCombatBypassId != 0 && enemy.ObjectId == LocalCombatBypassId)
                || (DiagUndrivenObjectId != 0 && enemy.ObjectId == DiagUndrivenObjectId)
                || _retainedCandidateThisPass[i]
                || MaterialisedEnemies.ToLocal(enemy.ObjectId) != 0
                || FindLocalCombatSlot(enemy.ObjectId) >= 0)
            {
                // Dead/absent slots, the two diagnostics, materialised stand-ins, and a creature
                // already holding a slot (defence in depth — the upstream census filter should
                // already have excluded it, see DriveOne's own "STILL SEEING" note) are never
                // candidates — StartLocalFight only ever fires a GIT-identical creature's own
                // script.
                continue;
            }

            nint creature = Resolve(i, enemy.ObjectId);
            if (creature == 0 || creature == player
                || *(uint*)(creature + CrePartyFollowInfo) != 0)
            {
                // Unresolvable, the player's own body (defensive — should never collide here),
                // or a mirrored companion party sync already owns.
                continue;
            }

            float distance = PlanarDistance(creature, player);
            if (distance > ImmediateEngageRange || !Odyssey.HasCreatureScriptSlot0x260(creature))
            {
                // Cheap gates first: an engine call (reputation, next) is only worth making once
                // range and the script-slot requirement already pass.
                continue;
            }

            int reputation = Odyssey.GetAIStateReputation(creature, playerId);
            bool targetingUs = IsAttackingOurPhantom(enemy.ObjectId, now);

            LocalFightPriority priority = LocalFightCandidateRule.Evaluate(
                reputation, gitIdentical: true, hasScript: true, distance, ImmediateEngageRange,
                targetingUs);

            if (priority == LocalFightPriority.None)
            {
                continue;
            }

            // ITEM 14: every candidate that clears the gates is a member of this scan's eligible
            // set, whether or not it goes on to win a slot below.
            if (eligibleCount < _eligibleOwnerId.Length)
            {
                _eligibleOwnerId[eligibleCount] = enemy.ObjectId;
                _eligiblePriority[eligibleCount] = priority;
                _eligibleDistance[eligibleCount] = distance;
                _eligibleCensusIndex[eligibleCount] = i;
                eligibleCount++;
            }
        }

        // N-SLOT: rank the eligible set and fill every free physical slot at once. Occupied
        // slots start as their own holder; a slot LocalFightSlotAssignmentRule leaves
        // unassigned (nothing eligible left to offer it) stays free at 0.
        bool[] slotOccupied = _slotOccupiedScratch;
        for (int slot = 0; slot < LocalCombatSlots; slot++)
        {
            slotOccupied[slot] = _locallyFightingId[slot] != 0;
            _slotHolderOwnerId[slot] = _locallyFightingId[slot];
        }

        LocalFightSlotAssignmentRule.Assign(
            _eligiblePriority, _eligibleDistance, eligibleCount,
            slotOccupied, LocalCombatSlots, _eligibleUsedScratch, _immediateTriggerCensusIndexScratch);

        for (int slot = 0; slot < LocalCombatSlots; slot++)
        {
            int candidateIndex = _immediateTriggerCensusIndexScratch[slot];
            if (candidateIndex < 0)
            {
                _immediateTriggerCensusIndex[slot] = -1;

                if (_lastLoggedImmediateOwnerId[slot] != 0)
                {
                    _lastLoggedImmediateOwnerId[slot] = 0;
                }

                continue;
            }

            _immediateTriggerCensusIndex[slot] = _eligibleCensusIndex[candidateIndex];
            _immediateTriggerPriority[slot] = _eligiblePriority[candidateIndex];

            uint winnerOwnerId = _eligibleOwnerId[candidateIndex];
            _slotHolderOwnerId[slot] = winnerOwnerId;

            if (winnerOwnerId != _lastLoggedImmediateOwnerId[slot])
            {
                _lastLoggedImmediateOwnerId[slot] = winnerOwnerId;
                PluginHost.Log(
                    $"local_combat: slot {slot + 1}/{LocalCombatSlots} selected 0x{winnerOwnerId:X} "
                    + $"(priority={_eligiblePriority[candidateIndex]}, "
                    + $"dist={_eligibleDistance[candidateIndex]:F2}, "
                    + $"engageRange={ImmediateEngageRange:F1})");
            }
        }

        UpdateDeferredNotice(eligibleCount);
    }

    /// <summary>Physical slot index currently holding <paramref name="ownerId"/>, or -1 if none
    /// of the first <see cref="LocalCombatSlots"/> slots does.</summary>
    private int FindLocalCombatSlot(uint ownerId)
    {
        if (ownerId == 0)
        {
            return -1;
        }

        for (int slot = 0; slot < LocalCombatSlots; slot++)
        {
            if (_locallyFightingId[slot] == ownerId)
            {
                return slot;
            }
        }

        return -1;
    }

    // Reusable scratch for SelectImmediateCandidate's call into LocalFightSlotAssignmentRule —
    // sized to capacity, only the first LocalCombatSlots entries are ever touched, avoids a
    // per-frame allocation.
    private readonly bool[] _slotOccupiedScratch = new bool[SharedBlock.LocalCombatSlotCapacity];
    private readonly int[] _immediateTriggerCensusIndexScratch = new int[SharedBlock.LocalCombatSlotCapacity];

    /// <summary>
    /// ITEM 14 / N-SLOT. Turns this scan's eligible-candidate set into the deferred set
    /// (<see cref="LocalCombatDeferralRule"/>, holder SET this time rather than a single id) and,
    /// if it genuinely changed since the last scan, logs the edge and refreshes the HUD text
    /// <see cref="LocalCombatDeferredNoticeRule"/> builds. Called from every path through
    /// <see cref="SelectImmediateCandidate"/> that reaches a verdict — including "nothing
    /// eligible right now" — so the notice clears itself the same way it was set, on the edge,
    /// with no separate cleanup path needed.
    /// </summary>
    private void UpdateDeferredNotice(int eligibleCount)
    {
        _deferredCount = LocalCombatDeferralRule.ComputeDeferred(
            _eligibleOwnerId, eligibleCount, _slotHolderOwnerId, LocalCombatSlots, _deferredOwnerId);

        if (!LocalCombatDeferralRule.HasChanged(
                _lastDeferredOwnerId, _lastDeferredCount, _deferredOwnerId, _deferredCount))
        {
            return;
        }

        Array.Copy(_deferredOwnerId, _lastDeferredOwnerId, _deferredCount);
        _lastDeferredCount = _deferredCount;

        PluginHost.Log(_deferredCount > 0
            ? $"local_combat: fighting {FormatSlotHolders()}, {_deferredCount} other "
              + (_deferredCount == 1 ? "hostile" : "hostiles") + " deferred (mirrored)"
            : "local_combat: no hostiles deferred");

        _deferredNoticeText = LocalCombatDeferredNoticeRule.Text(_deferredCount);
        _deferredNoticeBytes = _deferredNoticeText is null ? [] : EncodeAscii(_deferredNoticeText);
    }

    /// <summary>Comma-joined hex ids of every currently-held slot, for the deferred-set log
    /// line — "none" when every slot is free (only reachable transiently, since a nonzero
    /// deferred count implies at least one eligible candidate exists to be deferred FROM, but
    /// kept explicit rather than assuming a holder is always present).</summary>
    private string FormatSlotHolders()
    {
        System.Text.StringBuilder sb = new();
        for (int slot = 0; slot < LocalCombatSlots; slot++)
        {
            uint id = _slotHolderOwnerId[slot];
            if (id == 0)
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(", ");
            }

            sb.Append("0x").Append(id.ToString("X"));
        }

        return sb.Length > 0 ? sb.ToString() : "none";
    }

    /// <summary>ITEM 14. Redraws the deferred-hostiles HUD line every frame while one is active —
    /// the queued text node's own life (docs/research/text-render-queued.md) means a line that
    /// stops being re-posted fades out within <see cref="DeferredNoticeLife"/> seconds on its
    /// own, which is exactly the behaviour wanted the instant the last deferred hostile clears or
    /// the fight itself ends. coop.ini <c>local_combat_deferred_notice</c> (default on) gates
    /// ONLY this draw — the log line in <see cref="UpdateDeferredNotice"/> always fires
    /// regardless, since surfacing the limit at all is this item's whole point.</summary>
    private void DrawDeferredNotice()
    {
        if (!_deferredNoticeEnabled || _deferredNoticeBytes.Length == 0)
        {
            return;
        }

        fixed (byte* p = _deferredNoticeBytes)
        {
            Odyssey.PostTextLine(p, DeferredNoticeColumn, DeferredNoticeRow, DeferredNoticeLife);
        }
    }

    /// <summary>
    /// Publishes <c>MaterialisedEnemies</c>' own owner-id table verbatim into the shared block
    /// (version 65), so the agent can skip a doomed giveloot round trip for an owner id with no
    /// local stand-in before it ever reaches the single-slot cheat channel. Called right
    /// alongside every <c>MaterialisedEnemies.Sweep</c> — Sweep is what actually changes the
    /// table (a destroy clears a slot, a fresh materialise fills one), so publishing at the
    /// same cadence keeps the two in lockstep with no separate schedule to reason about. See
    /// docs/research/enemy-materialisation.md's 2026-09-02 section.
    /// </summary>
    private void PublishMaterialisedOwnerIds() =>
        block.SetMaterialisedOwnerIds(MaterialisedEnemies.OwnerIdsSnapshot);

    private static byte[] EncodeAscii(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        byte[] bytes = new byte[text.Length + 1];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bytes[i] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
        }

        bytes[^1] = 0;
        return bytes;
    }

    /// <summary>Planar distance between two resolved creatures, in world units. Shared by
    /// <see cref="SelectImmediateCandidate"/> (immediate-trigger ranking) — <see cref="IsEngaged"/>
    /// below computes the same dx/dy for its own squared-range test, kept separate because one
    /// needs the real distance for ranking and logging and the other only ever needs a
    /// threshold comparison.</summary>
    private static float PlanarDistance(nint a, nint b)
    {
        Vec3 pa = Odyssey.GetPosition(a);
        Vec3 pb = Odyssey.GetPosition(b);
        float dx = pa.X - pb.X;
        float dy = pa.Y - pb.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private bool DriveOne(int slot, EnemyPresenceView enemy, double now, nint player)
    {
        // ROUND 41 DIAGNOSTIC confirmed the gap this guard now closes: the agent-side filter
        // (CoopAgent.cs, bridge.ReadLocalCombatExcludedIds) is the ONLY thing that was ever
        // supposed to keep every currently-excluded creature out of this instance's own enemy
        // list, and a live capture caught it failing — "STILL SEEING excluded id 0x6 at slot
        // 0" fired repeatedly while the creature sat frozen at melee range under
        // StartLocalFight, never landing a hit. Relying on a single upstream filter with no
        // defence here meant every pass this creature's own script-driven fight was fighting
        // ClaimCreatureForFight's re-claim and PermitAttacking's re-assert for it at the same
        // time — round 14 proved that pairing structurally cannot corrupt round-processing
        // FIELDS on this single-threaded engine, but it never checked whether re-claiming a
        // creature's action queue every frame while ITS OWN script is trying to queue an
        // attack on it starves the attack before it can ever land. Skip it here too, so a
        // stale or racy upstream read can never reach the claim/permit/drive calls below —
        // this instance leaves it alone exactly as intended, defence in depth rather than a
        // single point of failure. N-SLOT: checked against every physical slot, not just one.
        int fightingSlot = FindLocalCombatSlot(enemy.ObjectId);
        if (fightingSlot >= 0 && enemy.Dead)
        {
            // OWNER'S VERDICT (2026-09-02, A-slots pass). The agent's exclusion filter lets an
            // excluded id through ONLY as a Dead entry (LocalCombatExclusionRule: the owner
            // reported the corpse, or stopped listing the body altogether) — this is the one
            // way a creature whose local copy is min-1-hp pinned can ever end its fight by
            // dying. Measured before this: the host looted 0x3B's corpse while the client's
            // copy fought on for the rest of its 25 s window and then stood there immortal,
            // shooting the PC, because normal mirroring never got to see an id the owner no
            // longer published. Release the slot here and fall through to the ordinary Dead
            // path below (Forget; CombatController's KillOnce unpins and kills the body).
            PluginHost.Log(
                $"local_combat: slot {fightingSlot + 1}/{LocalCombatSlots} released id=0x{enemy.ObjectId:X} "
                + "reason=owner reports dead or gone");
            PluginEvents.RaiseLocalCombatEngagementChanged(false, enemy.ObjectId);
            _locallyFightingId[fightingSlot] = 0;
            _stillSeeingReportedId[fightingSlot] = 0;
            _engageFallbackIssued[fightingSlot] = false;
            fightingSlot = -1;
        }

        if (fightingSlot >= 0)
        {
            // EDGE-TRIGGERED (2026-08-22 player-style pass), per physical slot. The 2026-08-21
            // version logged on a flat 1s throttle, which was fine for the race window round 41
            // built this guard for (measured live, round 55: usually 1-2 lines, the agent
            // catching up within a pump or two) but turned into genuine spam — thousands of
            // lines an hour — on the stretches where the same excluded id kept leaking for a
            // whole 7-25s fight, one line every single second of it. Log once on the EDGE (a
            // leak this method has not already reported for this SLOT's id since the fight
            // armed), then only a 5s keep-alive for as long as the SAME id keeps leaking, so a
            // genuinely stuck leak is still visible without flooding. _stillSeeingReportedId
            // resets to 0 per slot in Invalidate() and in MaintainLocalFight's own fight-end
            // path, so the NEXT fight in that slot's excluded id — a different id by
            // construction, see StartLocalFight — always logs its own fresh edge rather than
            // inheriting this one's keep-alive countdown.
            uint fightingId = _locallyFightingId[fightingSlot];
            bool freshEdge = _stillSeeingReportedId[fightingSlot] != fightingId;
            if (freshEdge || now - _stillSeeingLoggedAt[fightingSlot] >= 5.0)
            {
                _stillSeeingReportedId[fightingSlot] = fightingId;
                _stillSeeingLoggedAt[fightingSlot] = now;
                PluginHost.Log(
                    $"local combat diag: STILL SEEING excluded id 0x{fightingId:X} "
                    + $"at census slot {slot} (local_combat slot {fightingSlot + 1}/{LocalCombatSlots}) "
                    + "in the upstream feed - skipped here instead of driven, "
                    + $"block.EnemyPresenceCount={block.EnemyPresenceCount}, "
                    + $"blockAddress=0x{block.Address:X}"
                    + (freshEdge ? string.Empty : " (still leaking, 5s keep-alive)"));
            }

            return true;
        }

        if (enemy.ObjectId == 0 || enemy.ObjectId == Odyssey.ObjectInvalid || enemy.Dead)
        {
            // A dead enemy's body belongs to the engine's death path (CombatController
            // runs it); driving a corpse would stand it back up.
            Forget(slot);
            return false;
        }

        if (!IsPlausiblePosition(enemy.Position))
        {
            return false;
        }

        // Translate through the materialisation map. An owner id with no local mapping
        // usually resolves directly (GIT identity); when it does not, the enemy exists
        // only in the owner's instance and a stand-in body is built for it.
        uint mapped = MaterialisedEnemies.ToLocal(enemy.ObjectId);

        // ⚠ An unmapped owner id that numerically equals a body THIS instance spawned — a
        // peer's phantom, or our stand-in for a DIFFERENT owner enemy — must never be resolved
        // as if it were GIT-identical: host encounter/script spawns allocate ids in the same
        // range as our own spawns, so the collision is routine with many spawned enemies. The
        // enemy has no local body of its own; build one. Live 2026-09-24: 52 host-spawned
        // enemies, and every colliding id was left without a body for the rest of the module.
        if (mapped == 0
            && (IsOwnPhantomId(block, enemy.ObjectId) || MaterialisedEnemies.ToOwner(enemy.ObjectId) != 0))
        {
            MaterialisedEnemies.TryMaterialise(enemy, now, knownCollision: true);
            return false;
        }

        nint creature = Resolve(slot, mapped != 0 ? mapped : enemy.ObjectId);
        if (creature == 0)
        {
            if (mapped == 0)
            {
                MaterialisedEnemies.TryMaterialise(enemy, now);
            }
            else if (!MaterialisedEnemies.InBirthGrace(enemy.ObjectId, now))
            {
                // The mapped body has been unresolvable past its birth grace: treat it as
                // genuinely gone. A FRESH body is left alone instead — measured live, a
                // stand-in's first resolve can fail transiently, and condemning it on that
                // one frame burned a body (and an object id) per spawn cooldown until one
                // stuck. Log what the resolver saw, because "could not be resolved" alone
                // told us nothing about why.
                nint raw = Odyssey.ResolveServerObject(mapped);
                PluginHost.Log(
                    $"enemy drive: giving up on stand-in 0x{mapped:X} for owner "
                    + $"0x{enemy.ObjectId:X} (resolver returned 0x{raw:X}"
                    + (raw != 0 ? $", vtable 0x{*(nint*)raw:X}, id 0x{*(uint*)(raw + ObjId):X})" : ")"));
                MaterialisedEnemies.ForgetOwner(enemy.ObjectId);
            }

            return false;
        }

        if (creature == player)
        {
            return false;
        }

        // A mirrored companion is party sync's to move, never ours.
        bool retainedHostile = MaterialisedEnemies.IsRetainedOwner(enemy.ObjectId);
        if (*(uint*)(creature + CrePartyFollowInfo) != 0 && !retainedHostile)
        {
            return false;
        }

        // Preserve the retained native NPC body and scripts, but restore its hostile faction
        // after PartyPresenceController relinquishes the old companion ownership this frame.
        if (retainedHostile
            && Odyssey.GetCreatureFaction(creature) != DarkSideHostileCompanionRule.HostileFaction)
        {
            if (!Odyssey.SetCreatureFaction(
                    *(uint*)(creature + ObjId), DarkSideHostileCompanionRule.HostileFaction))
            {
                return false; // keep party ownership intact and retry the faction handoff next frame
            }
        }

        // ROUND 29 DIAGNOSTIC: this one hand-picked creature is left completely untouched —
        // no claim, no permit, no drive, no engagement recording, no attack queuing. See
        // DiagUndrivenObjectId's own doc comment. Still counts as present (return true), it is
        // simply never written to by this controller.
        if (DiagUndrivenObjectId != 0 && enemy.ObjectId == DiagUndrivenObjectId)
        {
            return true;
        }

        // ROUND 55 / N-SLOT: decided by SelectImmediateCandidate BEFORE this loop ever started,
        // not re-derived per slot. Every gate that used to live here — LocalCombat on, a player
        // to fight, a free physical slot, GIT identity, range, a real +0x260 script, AND (round
        // 49) the engine's own rep==2 hostility check — is exactly what SelectImmediateCandidate
        // already required of the winner before naming it; re-checking any of it here would only
        // risk disagreeing with the very decision this IS supposed to carry out. mapped==0 holds
        // by construction whenever this is true (SelectImmediateCandidate only ever considers
        // GIT-identical slots), so nothing downstream that used to gate on `mapped == 0` here
        // needs to change. A census index can be the winner for at most one physical slot this
        // frame (LocalFightSlotAssignmentRule's own guarantee), so this scan is a plain lookup,
        // not a race.
        int triggeringPhysicalSlot = -1;
        if (LocalCombat && player != 0)
        {
            for (int s = 0; s < LocalCombatSlots; s++)
            {
                if (_locallyFightingId[s] == 0 && _immediateTriggerCensusIndex[s] == slot)
                {
                    triggeringPhysicalSlot = s;
                    break;
                }
            }
        }

        bool triggeringLocalFight = triggeringPhysicalSlot >= 0;

        // Re-claim every pass, not once: the local AI re-queues combat and movement
        // actions between frames, and an armed MoveToPoint at the queue head is the
        // contended writer that made direct placement look broken for a month.
        //
        // With local combat on, the queue is LEFT ALONE — the same wipe that stops the
        // enemy walking away also deletes its swings, which is why a mirroring instance is
        // never in combat. See Odyssey.ClaimCreature(nint, bool).
        // With local combat on the wipe is TARGETED rather than absent. "Leave the queue
        // alone" was the first attempt and it is worse than the bug it replaced: this
        // controller overwrites the body position from the owner every pass, so a queued move
        // never reduces its own distance, never completes, and wedges the queue head
        // permanently -- the attacks behind it never run and this instance still never enters
        // combat, only now the body also fights our placement. Odyssey reads the head node's
        // action type (node+0x00, dispatched by RunActions 0x0057F4A0) and wipes only when it
        // is a move-class action.
        //
        // ⚠ ROUND 46: SKIPPED ENTIRELY on the exact frame this creature is about to be
        // started into local combat. ClaimCreatureForFight's conditional wipe calls the
        // engine's own ClearAllActions(creature, 1) whenever the head action is move-class —
        // ordinary for a body whose position this controller force-overwrites every frame —
        // and ClearAllActions's own bIsCurrentOnly=1 branch (confirmed live in the
        // disassembly, 0x004CCEC9 -> 0x004CCEDE call 0x004D37E0) walks the creature's own
        // CSWSCombatRound sub-object (`creature+0x9C8`) 5 times, once per round sub-structure,
        // and fully resets each one via 0x004D3010 -- which frees any queued action nodes at
        // sub+0x88 and zeroes sub+0xB0, the exact field rounds 9-37 of this investigation
        // spent 27+ rounds proving is THE write that arms a round to process a queued attack.
        // Doing that on the SAME frame StartLocalFight fires the creature's own +0x260 script
        // would disarm the very mechanism the script is about to rely on, one frame before it
        // needs it. Every OTHER frame this creature is normally mirrored still gets the
        // targeted wipe exactly as before -- this is a one-frame exception at the handoff,
        // not a change to ongoing mirroring.
        if (triggeringLocalFight)
        {
            *(int*)(creature + Odyssey.ObjCommandable) = 1;
        }
        else if (LocalCombat)
        {
            Odyssey.ClaimCreatureForFight(creature, LocalCombatBisectMask);
        }
        else
        {
            Odyssey.ClaimCreature(creature, clearActions: true, LocalCombatBisectMask);
        }

        // Re-assert every pass, not once at spawn: a materialised body is marked as a
        // mirrored enemy when it is created and lives for the rest of the module, so a
        // spawn-time decision about whether it may swing cannot be trusted to still hold.
        //
        // ROUND 46: NOT called here on the triggering frame -- it runs once more, after the
        // pre-claim state restore below, so the restore cannot silently drop the
        // attack-permitted bit this plugin has always guaranteed for a creature it starts
        // fighting.
        if (LocalCombat && !triggeringLocalFight)
        {
            Odyssey.PermitAttacking(creature, LocalCombatBisectMask);
        }

        // Replay any NEW fire-and-forget the owner's copy played — the swings and parries
        // that turn a mirrored fight from bodies-in-the-right-places into a fight.
        if (!_oneShotPrimed[slot])
        {
            _oneShotPrimed[slot] = true;
            _appliedOneShot[slot] = enemy.OneShotSerial;
        }
        else if (enemy.OneShotSerial != _appliedOneShot[slot])
        {
            _appliedOneShot[slot] = enemy.OneShotSerial;
            if (enemy.OneShotAnimation != 0 && (LocalCombatBisectMask & Odyssey.BisectBitOneShotAnim) == 0)
            {
                Odyssey.PlayOneShotAnimation(creature, (ushort)enemy.OneShotAnimation);
            }
        }

        Drive(slot, creature, enemy, now);

        if (LocalCombat)
        {
            // The other half of the position fight: our own SetPosition suppresses the dirty
            // bit, the local AI does not. See Odyssey.ClearPositionDirty.
            if ((LocalCombatBisectMask & Odyssey.BisectBitClearPositionDirty) == 0)
            {
                Odyssey.ClearPositionDirty(creature);
            }

            // ⚠ ROUND 39: RecordEngagement for EVERY nearby driven enemy USED TO run here
            // unconditionally, licensing the absorb-standdown for all of them. That was
            // harmless while QueueAttackAction never actually armed anything (rounds 9-32) —
            // nothing was genuinely dealing local damage either way, so which ids were
            // "engaged" made no real difference. It stopped being harmless the moment ONE
            // specific creature could genuinely fight for real: every OTHER nearby enemy was
            // still marked engaged by mere proximity, absorbing ITS forwarded damage too even
            // though nothing local was replacing it — invulnerability against every enemy
            // except the one actually fighting. Caught live, first real local_combat pass:
            // "absorbed 2 forwarded damage... last attacker 0xE; this engine says
            // in-combat=False" — the existing watchdog's own warning text, firing exactly as
            // designed. MaintainLocalFight is now the ONLY place that marks engagement, and
            // only for the one id it is actually driving a real fight for.
            //
            // ROUND 39: QueueAttackAction (Odyssey.cs) is PROVEN a permanent no-op for a
            // creature still in the census — 30 rounds of live testing, never once armed.
            // The only mechanism 38 rounds of investigation found that actually works is
            // pulling the creature OUT of the census and letting its own engine-native AI
            // take over, script-fired the same way round 27 first proved live. One fight at
            // a time (see _locallyFightingId's own doc comment); a second engaged creature
            // waits for the first to finish rather than starting a concurrent exclusion.
            //
            // ⚠ GIT-IDENTICAL CREATURES ONLY (mapped == 0). Round 27's proof — and every
            // round since — only ever fired +0x260 on a creature's OWN assigned combat
            // script. A materialised stand-in's local body is a k_coop_phantom-blueprint
            // spawn wearing the owner's appearance; it does not carry the real enemy's
            // script set at all, and firing whatever (if anything) actually sits in that
            // phantom's own +0x260 slot is unproven and, measured live, destructive: a real
            // ambush stand-in's body was gone — ResolveServerObject returning 0 — within 5ms
            // of being pulled out to fight, while the owner's real creature was still alive
            // and dealing forwarded damage the whole time. Restrict to what is actually
            // proven safe until materialised stand-ins get their own investigation.
            //
            // ⚠ ALSO REQUIRES A NON-EMPTY +0x260 SLOT. Round 27's proof was never universal
            // across every GIT-placed creature — measured live (2026-08-21, tar_m03aa),
            // `tar03_bekcs2` carries no script there at all, so firing it is a silent no-op:
            // the creature sits frozen forever, even at true melee range, and — worse —
            // without this check it would be re-excluded from the census on every 25s
            // timeout indefinitely, licensing permanent forwarded-damage immunity against an
            // enemy that can never actually resolve the fight. See
            // Odyssey.HasCreatureScriptSlot0x260's own doc for the full account. A creature
            // that fails this check is simply left on ordinary mirrored/forwarded-damage
            // combat, same as before local_combat existed.
            if (triggeringLocalFight)
            {
                // ROUND 46: hand the creature back to the engine in the state it actually
                // had before this plugin ever claimed it, rather than whatever the last
                // claim/drive pass happened to leave — see Odyssey.ClaimSnapshot's own doc
                // comment for why CreMoveState in particular matters (DriveEnemy assigns it,
                // not ORs it, every driven frame). PermitAttacking runs AFTER the restore on
                // purpose, so the attack-permitted bit cannot be lost if the pre-claim
                // snapshot happened not to have it.
                if (_originalCaptured[slot])
                {
                    Odyssey.RestoreClaimState(creature, _originalState[slot]);
                }

                Odyssey.PermitAttacking(creature);
                StartLocalFight(
                    triggeringPhysicalSlot, enemy.ObjectId, creature, now, player,
                    _immediateTriggerPriority[triggeringPhysicalSlot]);
            }
        }

        // ROUND 52: this is the genuine "driven this frame" exit — every early return above
        // (still-excluded, dead, unresolvable, a phantom-id collision, the diag-undriven id)
        // skips this line on purpose, so the ground-truth diagnostic's "driven/mirrored
        // census creature" classification only ever names an id this pass actually placed.
        if (_drivenCount < DrivenOwnerId.Length)
        {
            DrivenOwnerId[_drivenCount++] = enemy.ObjectId;
        }

        return true;
    }

    /// <summary>
    /// ROUND 39 / N-SLOT: pulls one mirrored enemy out of the census, into physical slot
    /// <paramref name="slot"/>, and lets the engine's own AI fight it for real, via the exact
    /// mechanism 11 rounds (27-38) spent proving actually works: firing the creature's own
    /// +0x260 event script through the engine's real dispatcher, not replicating its native
    /// combat calls by hand. See docs/research/shared-state-audit.md §2 for the full trail;
    /// <c>Odyssey.QueueAttackAction</c> (kept, not deleted — its own doc now says why it never
    /// worked) is the thing this replaces.
    /// </summary>
    private void StartLocalFight(
        int slot, uint ownerId, nint creature, double now, nint player, LocalFightPriority reason)
    {
        _locallyFightingId[slot] = ownerId;
        _locallyFightingSince[slot] = now;
        _engageFallbackIssued[slot] = false;

        // §13: the inactivity clock starts here too — a fresh fight gets the same grace an
        // idle one would have gotten before ever showing evidence, exactly like the engage
        // fallback's own grace window keys off this same arming moment. Seeded from the
        // table's CURRENT total, not zero: this creature may have been fought (and its slot
        // released) earlier this module load, and that old total must not read as a fresh rise
        // the instant this new fight starts.
        _lastActivityAt[slot] = now;
        _lastDealtSeen[slot] = PhantomHitHook.Dealt.TotalFor(ownerId);
        _lastActivityLogAt[slot] = double.NegativeInfinity;

        // ROUND 48 PROBE, one-shot, at the exact moment this creature is armed for local
        // combat — before the +0x260 script fires. This is the evidence for the round's own
        // prime hypothesis: if hostility already reads wrong here (a bad faction, a reputation
        // answer that is not hostile, or a move-permitted/plot/min1hp bit that would gate
        // GN_DetermineCombatRound before it ever gets to arming a round), that IS the
        // explanation and no per-op mask matters. If it reads clean, the mask bisection below
        // is what settles it instead.
        uint playerId = player != 0 ? Odyssey.GetPlayerObjectId() : 0;
        int creatureFaction = Odyssey.GetCreatureFaction(creature);
        int playerFaction = player != 0 ? Odyssey.GetCreatureFaction(player) : -1;
        int repCreatureVsPlayer = player != 0 ? Odyssey.GetAIStateReputation(creature, playerId) : -1;
        int repPlayerVsCreature = player != 0 ? Odyssey.GetAIStateReputation(player, ownerId) : -1;
        int movePermitted = *(int*)(creature + Odyssey.CreMovePermitted);
        bool plot = Odyssey.IsPlot(creature);
        bool min1hp = Odyssey.GetMinOneHitPoint(creature);
        short hp = Odyssey.GetCurrentHitPoints(creature);
        ushort maxHp = Odyssey.GetMaxHitPoints(creature);

        PluginHost.Log(
            $"local combat probe: slot {slot + 1}/{LocalCombatSlots} 0x{ownerId:X} "
            + $"creatureFaction={creatureFaction} playerFaction={playerFaction} "
            + $"rep(creature,player)={repCreatureVsPlayer} rep(player,creature)={repPlayerVsCreature} "
            + "(2=hostile,1=friendly,0=neutral) "
            + $"CreMovePermitted=0x{movePermitted:X} plot={plot} min1hp={min1hp} hp={hp}/{maxHp} "
            + $"bisectMask=0x{LocalCombatBisectMask:X}");

        Odyssey.FireCreatureScriptSlot0x260(creature);

        string reasonText = reason == LocalFightPriority.TargetingUs ? "targeting-us" : "proximity";
        PluginHost.Log(
            $"local_combat: slot {slot + 1}/{LocalCombatSlots} armed id=0x{ownerId:X} "
            + $"reason={reasonText} (timeout {LocalCombatTimeoutSeconds:F0}s)");
        PluginEvents.RaiseLocalCombatEngagementChanged(true, ownerId);
    }

    /// <summary>
    /// ROUND 39 / N-SLOT: the per-frame heartbeat for every physical slot
    /// <see cref="StartLocalFight"/> currently has armed. Runs every single frame regardless of
    /// every other early return in <see cref="OnFrame"/> — see the call site's own comment for
    /// why that is load-bearing, not incidental.
    /// </summary>
    private void MaintainLocalFight(double now, nint player)
    {
        // §13: read once per pass, not once per slot — cheap, but there is no reason to pay
        // for it N times when it cannot change between slots in the same frame.
        uint playerId = player != 0 ? Odyssey.GetPlayerObjectId() : 0;

        for (int slot = 0; slot < LocalCombatSlots; slot++)
        {
            MaintainLocalFightSlot(slot, now, player, playerId);
        }
    }

    /// <summary>
    /// One physical slot's share of <see cref="MaintainLocalFight"/>. Three ways a fight ends,
    /// checked in the order a player would actually notice them: the engine's own creature
    /// reads dead (<see cref="Odyssey.IsCreatureDead"/> — genuinely dead OR simply no longer
    /// resolvable, which are the same "nothing left to fight" case from here), the player has
    /// moved out of engagement range (the same test <see cref="IsEngaged"/> already licenses
    /// the absorb with), or the INACTIVITY timeout has run out (§13 — the safety net for
    /// anything the first two do not catch, and, since 2026-09-02, a measure of "no evidence
    /// for this long," not "this fight has run this long": see <see cref="LocalFightActivityRule"/>
    /// and <see cref="_lastActivityAt"/> for why a fight with fresh evidence never hits this no
    /// matter how long it has actually been running). Any of the three clears
    /// <see cref="_locallyFightingId"/>[slot], which is the ENTIRE re-inclusion mechanism for
    /// that slot: the creature's id simply stops being read out of
    /// <see cref="SharedBlock.OffsetLocalCombatExcludedIds"/> on the very next pump, the agent
    /// resumes writing it into the published census, and normal mirroring picks it back up
    /// exactly like any other tracked enemy — there is nothing else to reconcile on rejoin,
    /// because nothing outside this controller's own per-creature slot cache
    /// (<see cref="Forget"/>, already re-run for a fresh id) ever knew it had left.
    /// </summary>
    private void MaintainLocalFightSlot(int slot, double now, nint player, uint playerId)
    {
        uint fightingId = _locallyFightingId[slot];
        if (fightingId == 0)
        {
            return;
        }

        // ⚠ fightingId is the OWNER's id (StartLocalFight stores enemy.ObjectId, not the
        // resolved local pointer's own id — see its doc comment). For a materialised stand-in
        // that id is NOT locally resolvable at all; resolving it raw always returns 0, and
        // IsCreatureDead(0) is true by design, so every tick read "dead or gone" immediately
        // after the fight started, cleared the slot, and the very next DriveOne pass picked the
        // SAME still-alive, still-engaged owner id right back up — an infinite same-frame churn
        // (measured live: "fight ended (dead or gone)" / "pulled out of the census" alternating
        // every ~4ms, hundreds of times a second). Translate through the same map DriveOne
        // already uses before resolving, exactly like Resolve's own fallback (mapped local id
        // if this is a stand-in, the raw id itself if it is GIT-identical).
        uint mappedId = MaterialisedEnemies.ToLocal(fightingId);
        nint creature = Odyssey.ResolveServerObject(mappedId != 0 ? mappedId : fightingId);
        bool dead = Odyssey.IsCreatureDead(creature);
        bool disengaged = !dead && (player == 0 || !IsEngaged(creature, player));

        // §13: only worth reading this pass's evidence for a fight that is not already ending
        // for one of the other two reasons — a dead or disengaged creature's activity does not
        // matter, and every read here (a DamageDealtTable scan, one engine field chase) is cost
        // worth skipping on the hot per-slot path once the fight is over anyway.
        bool timedOut = !dead && !disengaged && UpdateActivity(slot, fightingId, creature, playerId, now);

        // TEMPORARY DIAGNOSTIC: once a second per slot, log the excluded creature's real
        // position against the player's, so a full fight cycle produces a position-over-time
        // curve (frozen vs. slow-pathing vs. close-but-not-hitting) instead of one snapshot.
        if (!dead && creature != 0 && player != 0 && now - _lastPositionLogAt[slot] >= 1.0)
        {
            _lastPositionLogAt[slot] = now;
            Vec3 body = Odyssey.GetPosition(creature);
            Vec3 them = Odyssey.GetPosition(player);
            float dist = MathF.Sqrt(((body.X - them.X) * (body.X - them.X)) + ((body.Y - them.Y) * (body.Y - them.Y)));
            PluginHost.Log(
                $"local combat diag: slot {slot + 1}/{LocalCombatSlots} "
                + $"t={now - _locallyFightingSince[slot]:F1}s 0x{fightingId:X} "
                + $"at ({body.X:F2},{body.Y:F2}) player at ({them.X:F2},{them.Y:F2}) dist={dist:F2}");
        }

        if (dead || disengaged || timedOut)
        {
            string reason = dead
                ? "dead or gone"
                : disengaged
                    ? "player disengaged"
                    : $"timed out (no activity for {LocalCombatTimeoutSeconds:F0}s)";
            PluginHost.Log(
                $"local_combat: slot {slot + 1}/{LocalCombatSlots} released id=0x{fightingId:X} "
                + $"reason={reason}");
            PluginEvents.RaiseLocalCombatEngagementChanged(false, fightingId);
            _locallyFightingId[slot] = 0;

            // So the NEXT fight in this slot's excluded id — always a different id,
            // StartLocalFight only ever arms a fresh one — gets its own fresh "STILL SEEING"
            // edge in DriveOne rather than silently inheriting this one's 5s keep-alive
            // countdown.
            _stillSeeingReportedId[slot] = 0;

            // §12: same reason as the "STILL SEEING" reset above — this fight is over, so its
            // one-shot fallback guard must not carry over to whatever creature next arms this
            // physical slot.
            _engageFallbackIssued[slot] = false;

            // §13: same reason again — this slot's inactivity clock, dealt-damage baseline and
            // log throttle are all scoped to THIS fight; StartLocalFight re-seeds every one of
            // them fresh the next time this slot arms, same discipline as the fallback guard
            // above.
            _lastActivityAt[slot] = 0;
            _lastDealtSeen[slot] = 0;
            _lastActivityLogAt[slot] = double.NegativeInfinity;
            return;
        }

        // §12: the script fire alone does nothing for a creature with no combat round already
        // running (docs/research/combat-participation.md §12) — before this existed, an idle
        // hostile like the tar_m10aa Vulkars simply sat frozen for the whole
        // LocalCombatTimeoutSeconds window with nobody re-checking whether it ever got a target.
        // Runs every pass, same as everything else in this method — the one-shot guard inside
        // the rule itself is what keeps it from firing more than once per fight.
        MaybeEngageFallback(slot, fightingId, creature, now);

        // ⚠ The excluded id is missing from block.EnemyPresenceCount for the whole fight (the
        // entire point of the exclusion), so DriveOne's own MaterialisedEnemies.Seen call
        // never runs for it here — and MaterialisedEnemies.Sweep destroys anything unseen for
        // 5 seconds, well inside a fight that can legitimately run up to
        // LocalCombatTimeoutSeconds. Measured live: a real materialised stand-in got swept out
        // from under an active local fight, forcing a full re-materialisation mid-round (and
        // burning the one-spawn-per-30-frames budget genuinely NEW encounter spawns were
        // waiting on). A GIT-identical creature (not in the table at all) is a no-op here.
        MaterialisedEnemies.Seen(fightingId, now);

        // Same licence RecordEngagement grants a normally-driven creature — this one just
        // earns it here instead, since DriveOne no longer runs for an id the census does not
        // publish any more.
        if (_engagedCount < EngagedOwnerId.Length)
        {
            EngagedOwnerId[_engagedCount++] = fightingId;
        }

        // Matches the stamp at the bottom of a normal OnFrame pass exactly. Stamped here too
        // because the count==0 early return (the common case once every tracked enemy is one
        // now excluded) would otherwise never reach that stamp at all, and a stale engagement
        // record is indistinguishable from a genuinely ended one to IsLocallyEngaged. Safe to
        // stamp once per active slot — the same DateTime.UtcNow.Ticks value, or near enough
        // within the 1s freshness window, regardless of how many slots are active this frame.
        _engagedAtTicks = DateTime.UtcNow.Ticks;
    }

    /// <summary>
    /// §13 (docs/research/combat-participation.md): this pass's share of one slot's inactivity
    /// clock. Gathers the two pieces of evidence <see cref="LocalFightActivityRule"/> looks at —
    /// whether <see cref="DamageDealtTable"/>'s cumulative total for <paramref name="fightingId"/>
    /// rose since the last pass (<see cref="_lastDealtSeen"/>), and whether <paramref
    /// name="creature"/>'s own live combat round currently names the local player as its target
    /// (<see cref="Odyssey.GetCurrentAttackTargetId"/>) — feeds them to the rule, and applies the
    /// verdict: bumps <see cref="_lastActivityAt"/>[slot] and logs a rate-limited line on a
    /// refresh, or reports the timeout back to the caller. Returns true only when the rule says
    /// <see cref="LocalFightActivityAction.TimedOut"/> — the caller's own <c>dead</c>/<c>disengaged</c>
    /// checks already cover the other two release reasons, so this is never called once either of
    /// those is already true (see the call site).
    /// </summary>
    private bool UpdateActivity(int slot, uint fightingId, nint creature, uint playerId, double now)
    {
        uint dealtNow = PhantomHitHook.Dealt.TotalFor(fightingId);
        uint dealtBefore = _lastDealtSeen[slot];
        bool damageDealtRose = dealtNow > dealtBefore;
        _lastDealtSeen[slot] = dealtNow;

        bool targetingUs = playerId != 0 && Odyssey.GetCurrentAttackTargetId(creature) == playerId;

        LocalFightActivityAction action = LocalFightActivityRule.Decide(
            damageDealtRose, targetingUs, _lastActivityAt[slot], now, LocalCombatTimeoutSeconds);

        if (action != LocalFightActivityAction.Refresh)
        {
            return action == LocalFightActivityAction.TimedOut;
        }

        _lastActivityAt[slot] = now;

        if (now - _lastActivityLogAt[slot] >= ActivityRefreshLogIntervalSeconds)
        {
            _lastActivityLogAt[slot] = now;

            string reasonText = damageDealtRose && targetingUs
                ? $"dealt +{dealtNow - dealtBefore} to 0x{fightingId:X} / it targets us"
                : damageDealtRose
                    ? $"dealt +{dealtNow - dealtBefore} to 0x{fightingId:X}"
                    : "it targets us";

            PluginHost.Log(
                $"local_combat: slot {slot + 1}/{LocalCombatSlots} still active "
                + $"({reasonText}), timeout extended");
        }

        return false;
    }

    /// <summary>
    /// §12 (docs/research/combat-participation.md): consults <see cref="LocalFightEngageFallbackRule"/>
    /// for slot <paramref name="slot"/> and, on <see cref="LocalFightEngageFallbackAction.EngageDirectly"/>,
    /// drives <paramref name="creature"/>'s attack on the local player directly through
    /// <see cref="Odyssey.AttackTarget"/> — never queues anything on top of a target the
    /// creature already has (the rule itself gates on an empty action-queue head), and only ever
    /// once per fight (the rule's own one-shot guard, backed by
    /// <see cref="_engageFallbackIssued"/>). <paramref name="attackerObjectId"/> is
    /// <c>fightingId</c>, the creature's OWNER-space id — safe to pass straight through as the
    /// attacker id to <see cref="Odyssey.AttackTarget"/> because <see cref="StartLocalFight"/>
    /// only ever arms GIT-identical creatures (mapped == 0 by construction), so owner id and
    /// local resolvable id are the same number here.
    /// </summary>
    private void MaybeEngageFallback(int slot, uint attackerObjectId, nint creature, double now)
    {
        if (_engageFallbackIssued[slot] || creature == 0)
        {
            return;
        }

        int headActionType = Odyssey.GetHeadActionType(creature);
        LocalFightEngageFallbackAction action = LocalFightEngageFallbackRule.Decide(
            LocalCombatEngageFallback, _engageFallbackIssued[slot], headActionType,
            _locallyFightingSince[slot], now, LocalCombatEngageFallbackGraceSeconds);

        if (action != LocalFightEngageFallbackAction.EngageDirectly)
        {
            return;
        }

        _engageFallbackIssued[slot] = true;

        uint playerId = Odyssey.GetPlayerObjectId();
        if (playerId == 0)
        {
            return;
        }

        double sinceArmedMs = (now - _locallyFightingSince[slot]) * 1000.0;
        bool ok = Odyssey.AttackTarget(attackerObjectId, playerId, out int failure);
        PluginHost.Log(ok
            ? $"local_combat: slot {slot + 1}/{LocalCombatSlots} engaged 0x{attackerObjectId:X} "
              + $"directly (no attack target {sinceArmedMs:F0}ms after the script fire)"
            : $"local_combat: slot {slot + 1}/{LocalCombatSlots} could not engage 0x{attackerObjectId:X} "
              + $"directly (failure {failure}, no attack target {sinceArmedMs:F0}ms after the script fire)");
    }

    /// <summary>Planar distance test shared by <see cref="MaintainLocalFight"/> (licenses
    /// damage absorption for the ONE creature actually fighting) and the local_combat
    /// attack-initiation gate — one fight radius, one place it is computed. The Z of a driven
    /// body is the owner's, and a ramp between two players standing in the same fight is a
    /// metre of height, not a different floor.</summary>
    private static bool IsEngaged(nint creature, nint player)
    {
        Vec3 body = Odyssey.GetPosition(creature);
        Vec3 them = Odyssey.GetPosition(player);
        float dx = body.X - them.X;
        float dy = body.Y - them.Y;
        return (dx * dx) + (dy * dy) <= EngagementRangeSquared;
    }

    /// <summary>Resolves and caches the slot's creature, revalidating the cached pointer
    /// every call — id and object type must both still match.</summary>
    private nint Resolve(int slot, uint objectId)
    {
        nint cached = _creature[slot];
        if (cached != 0 && _trackedId[slot] == objectId
            && *(uint*)(cached + ObjId) == objectId
            && *(nint*)cached == CreatureVtable)
        {
            return cached;
        }

        Forget(slot);

        nint creature = Odyssey.ResolveServerObject(objectId);
        if (creature == 0 || *(nint*)creature != CreatureVtable)
        {
            return 0;
        }

        _creature[slot] = creature;
        _trackedId[slot] = objectId;

        // ROUND 46: capture the engine's own state for this creature BEFORE this plugin's
        // claim ever runs on it — the only point in DriveOne's pipeline where that is still
        // true. See Odyssey.ClaimSnapshot's own doc comment.
        _originalState[slot] = Odyssey.CaptureClaimState(creature);
        _originalCaptured[slot] = true;

        return creature;
    }

    /// <summary>The same measure-smooth-drive as PhantomController.DriveDirect, minus the
    /// camera separation (an enemy is allowed to stand next to the player).</summary>
    private void Drive(int slot, nint creature, EnemyPresenceView enemy, double now)
    {
        (Vec3 shown, float speed, bool moving) = _smoother.Step(slot, enemy.Position, now);
        Odyssey.DriveEnemy(creature, shown, enemy.Facing, speed, moving, enemy.Animation, LocalCombatBisectMask);
    }

    private void Forget(int slot)
    {
        if (_creature[slot] != 0)
        {
            Odyssey.ForgetGait(_creature[slot]);
        }

        _creature[slot] = 0;
        _trackedId[slot] = 0;
        _appliedOneShot[slot] = 0;
        _oneShotPrimed[slot] = false;
        _originalCaptured[slot] = false;
        _smoother.Reset(slot);
    }

    private static bool IsOwnPhantomId(SharedBlockView block, uint objectId)
    {
        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            if (block.GetPhantomId(i) == objectId)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsPlausiblePosition(Vec3 p) =>
        Vec3.IsWorldPlausible(p) && (p.X != 0f || p.Y != 0f);
}
