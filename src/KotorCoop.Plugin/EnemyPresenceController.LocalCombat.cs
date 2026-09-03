namespace KotorCoop.Plugin;

// The widely-referenced static oracle surface: LocalCombat/LocalCombatBisectMask/
// LocalCombatBypassId (the coop.ini-backed knobs) and IsLocallyEngaged/
// IsLocallyFightingCreature/IsDrivenCensusCreature (the read-only queries seven other
// files call as EnemyPresenceController.X) plus the static state that backs them. Same
// class, same member names as before the split - no other file needs to change.
internal sealed unsafe partial class EnemyPresenceController
{
    /// <summary>
    /// Whether this instance lets its mirrored enemies fight for themselves.
    ///
    /// OFF preserves the behaviour every other channel was built against: the enemy is a
    /// body in the right place that never acts, and everything that happens to this player
    /// arrives as forwarded damage from the owner.
    ///
    /// ON hands the local copies back to their own AI so they can complete combat rounds
    /// here — which is the only way this engine's combat state can rise, since the engine
    /// derives it from being fought rather than from a flag anyone can set (measured: a
    /// written flag is reverted in 0-2 ms — docs/research/combat-participation.md's
    /// 2026-08-27 section).
    ///
    /// ⚠ TURNING THIS ON WITHOUT MOVING DAMAGE AUTHORITY DOUBLE-COUNTS EVERY HIT: the local
    /// enemy swings here AND the owner forwards the same swing that landed on this player's
    /// phantom over there. IncomingDamageController reads this same flag and stands down when
    /// it is set, so the two always move together.
    ///
    /// ON BY DEFAULT as of round 39 (2026-08-21) of docs/research/shared-state-audit.md §2.
    /// This default was paid for three times before it was earned: the shared-state audit's
    /// adversarial review left four structural items open ("do not default this on"), a bare
    /// `dev` commit flipped it to on anyway, and the predicted failure happened live on
    /// tat_m20aa (2026-08-14) — 15 points of forwarded damage absorbed as "attributed to
    /// mirrored enemies we are driving" while this engine's own in-combat flag read FALSE, so
    /// the player was simply invulnerable for that hit. The audit's items 4-7 closed the
    /// risks in a working mechanism; the mechanism itself then spent rounds 9-38 provably NOT
    /// arming a mirrored creature's native combat round under any input tried. Round 39 found
    /// why (a creature still enumerated in this instance's own published census — regardless
    /// of whether anything actually drives it — silently blocks its own AI from ever picking
    /// a target) and built the fix directly from that: the one creature genuinely fighting the
    /// local player is pulled out of the census for the duration of the fight (StartLocalFight
    /// / MaintainLocalFight below) and its own `k_def_combend01` event script is fired through
    /// the engine's real dispatcher — Strategy 2, proven live rounds 27-39 — instead of trying
    /// to replicate the native arming calls by hand, which rounds 9-27 spent proving is a
    /// permanent dead end for a mirrored copy specifically. Re-verified live this round after
    /// a diagnostic-logging refactor: full automatic lifecycle (trigger on engagement, 25s
    /// timeout, disengage detection, clean re-inclusion) cycling correctly end to end, and a
    /// real invulnerability bug this round's own live stress test found (every nearby driven
    /// enemy was licensing the absorb standdown, not just the one actually fighting) fixed and
    /// reconfirmed closed before this flip.
    /// </summary>
    public static readonly bool LocalCombat = PluginConfig.ReadBool("local_combat", true);

    /// <summary>
    /// coop.ini <c>local_combat_engage_fallback</c> (default ON). Whether
    /// <see cref="MaintainLocalFightSlot"/> may drive an armed creature's attack on the local
    /// player directly, once, through <see cref="Odyssey.AttackTarget"/> when the +0x260 script
    /// fire alone leaves it with no attack-queue target after a grace window — see
    /// docs/research/combat-participation.md §12 ("Idle hostiles never engage") for why the
    /// script fire alone does nothing for a creature with no combat round already running (a
    /// genuinely idle GIT hostile, e.g. the tar_m10aa Vulkars, versus an already-mid-round one
    /// like the Endar Spire `end_gensith` pair, which the script fire alone was always enough
    /// for). <see cref="LocalFightEngageFallbackRule"/> is the pure one-shot-per-fight decision;
    /// this only gates whether it is ever consulted at all.
    /// </summary>
    private static readonly bool LocalCombatEngageFallback =
        PluginConfig.ReadBool("local_combat_engage_fallback", true);

    /// <summary>
    /// How long <see cref="MaintainLocalFightSlot"/> waits, after a slot's fight was armed, for
    /// the script fire (or the creature's own AI, independently) to give the creature a real
    /// attack-queue target before concluding neither did and driving the fallback — see
    /// <see cref="LocalFightEngageFallbackRule"/>'s own doc comment. Not exposed as its own
    /// coop.ini key (unlike <see cref="LocalCombatEngageFallback"/> itself): a judgment call, not
    /// a measured value — long enough that a script fire which DOES work is never contested
    /// mid-arm by a redundant direct drive, short enough that a genuinely idle hostile does not
    /// sit frozen for long. NEEDS-LIVE: never timed against a real fight yet.
    /// </summary>
    private const double LocalCombatEngageFallbackGraceSeconds = 0.75;

    /// <summary>
    /// How many mirrored enemies this instance's own engine may fight simultaneously via
    /// local_combat, coop.ini <c>local_combat_slots</c> (default 3, clamped 1..
    /// <see cref="SharedBlock.LocalCombatSlotCapacity"/> — the shared-block excluded-id array's
    /// own fixed width, docs/research/combat-participation.md §11). 1 reproduces the original
    /// single-fight behaviour bit for bit: the exact same candidate ranking
    /// (<see cref="LocalFightSlotAssignmentRule"/> with one free slot reduces to the old
    /// highest-<see cref="LocalFightCandidateRule.Beats"/> winner), the exact same
    /// <see cref="StartLocalFight"/>/<see cref="MaintainLocalFight"/> machinery, running for
    /// physical slot 0 only — slots 1 and 2 simply never arm.
    /// </summary>
    internal static readonly int LocalCombatSlots = LoadSlotCount();

    private static int LoadSlotCount()
    {
        int slots = Math.Clamp(
            PluginConfig.ReadInt("local_combat_slots", 3), 1, SharedBlock.LocalCombatSlotCapacity);
        PluginHost.Log($"local_combat_slots={slots} (default 3, clamped 1..{SharedBlock.LocalCombatSlotCapacity})");
        return slots;
    }

    /// <summary>
    /// DIAGNOSTIC (docs/research/shared-state-audit.md §2, round 47), dev-only. `coop.ini local_combat_bisect_mask`, hex,
    /// default 0. Each bit DISABLES exactly one distinct per-frame engine write/call this
    /// controller performs on a mirrored (not-yet-fighting) creature — see the bit table on
    /// <see cref="Odyssey.ClaimCreature(nint, bool, uint)"/> for the full list and
    /// <see cref="Odyssey.BisectAllOps"/> for "every op off, creature still counted in the
    /// census" (round 47's own "run 0"). Strictly additive: 0 (unset) reproduces today's
    /// shipped behaviour bit for bit. Never gates <see cref="StartLocalFight"/>,
    /// <see cref="MaintainLocalFight"/>, or the handoff-frame restore in
    /// <see cref="DriveOne"/> — only the ONGOING per-frame drive that runs on a creature
    /// before it is ever handed to local_combat, which is exactly the half of round 46's fact
    /// pattern this bisection targets. See docs/research/shared-state-audit.md §2, round 47.
    ///
    /// ROUND 48 EXTENSION: two more bits (11, 12 — <see cref="Odyssey.BisectBitHealthPin"/>,
    /// <see cref="Odyssey.BisectBitHealthMirror"/>) gate <see cref="CombatController"/>'s own
    /// per-40-frame writes to a census enemy, which neither round 47 nor any round before it
    /// ever masked — see Odyssey.cs's own "ROUND 48 EXTENSION" comment for why those two,
    /// specifically, were the real gap. Read directly by <see cref="CombatController"/>
    /// (internal, same assembly) rather than threaded through as a constructor argument,
    /// matching how every other consumer of this mask already reads it as a static.
    /// </summary>
    internal static readonly uint LocalCombatBisectMask = LoadBisectMask();

    /// <summary>
    /// DIAGNOSTIC (docs/research/shared-state-audit.md §2, round 50), dev-only. `coop.ini local_combat_bypass_id` (hex,
    /// default 0). Forty-nine rounds of write-level bisection (round 47's
    /// <see cref="LocalCombatBisectMask"/>, round 48's health-pin/mirror extension) proved the
    /// freeze is not caused by any single engine WRITE this plugin makes, but every one of
    /// those tests still left the OTHER consumers of census membership running normally: this
    /// controller's own <see cref="Resolve"/> (which caches an engine pointer and calls the
    /// engine's own <c>GetGameObject</c>), <see cref="MaterialisedEnemies.Seen"/> (a managed
    /// timestamp write, called for every published slot before <see cref="DriveOne"/> ever
    /// runs), <see cref="CombatController"/>'s own resolve-and-pin pass, and
    /// <see cref="PeerDamageController"/>'s owed-damage apply. Round 29's diagnostic
    /// (<see cref="DiagUndrivenObjectId"/>) came close but still let Resolve()/Seen() run;
    /// round 37/38's census-cap exclusion removed the creature from the list entirely, which
    /// is not "listed but untouched" either. This id, when set, is skipped completely by
    /// every one of those consumers while the owning agent keeps publishing it in the census
    /// exactly as before (CoopAgent.cs's own <c>TryWriteEnemies</c> call is never gated by
    /// this key) — genuinely "listed, but touched by nothing this plugin runs, automatically,
    /// every frame." See docs/research/shared-state-audit.md §2, round 50, for the full
    /// consumer table and what each early-return actually skips; several consumers this
    /// round's audit found (PhantomController.ApplyTargetable, InteractionLockController's
    /// HoldCreatureStill, AnimationHook, PhantomHitHook, CheatController) are read-only,
    /// operator-driven, or keyed to a completely different id space (phantom bodies, NPC
    /// tags) and are documented rather than gated, because there is nothing on a census enemy
    /// for them to touch. 0 (default, unset) leaves every consumer exactly as it runs today.
    /// </summary>
    internal static readonly uint LocalCombatBypassId = LoadBypassId();

    private static uint LoadBypassId()
    {
        uint id = ParseHexOrZero(PluginConfig.Read("local_combat_bypass_id"));
        if (id != 0)
        {
            PluginHost.Log(
                $"local_combat_bypass_id=0x{id:X} - this creature will be published in the "
                + "census as normal but touched by NO plugin consumer (round 50 decisive control)");
        }

        return id;
    }

    private static uint LoadBisectMask()
    {
        uint mask = ParseHexOrZero(PluginConfig.Read("local_combat_bisect_mask"));
        PluginHost.Log(mask == 0
            ? "local_combat_bisect_mask=0x0 (all ops active, default)"
            : $"local_combat_bisect_mask=0x{mask:X} (bits disabled: "
              + $"commandable={(mask & Odyssey.BisectBitClaimCommandable) != 0}, "
              + $"actionWipe={(mask & Odyssey.BisectBitClaimActionWipe) != 0}, "
              + $"movePermit={(mask & Odyssey.BisectBitClaimMovePermit) != 0}, "
              + $"permitAttack={(mask & Odyssey.BisectBitPermitAttack) != 0}, "
              + $"oneShotAnim={(mask & Odyssey.BisectBitOneShotAnim) != 0}, "
              + $"facing={(mask & Odyssey.BisectBitDriveFacing) != 0}, "
              + $"runMoveState={(mask & Odyssey.BisectBitDriveRunMoveState) != 0}, "
              + $"animation={(mask & Odyssey.BisectBitDriveAnimation) != 0}, "
              + $"position={(mask & Odyssey.BisectBitDrivePosition) != 0}, "
              + $"twin={(mask & Odyssey.BisectBitDriveTwin) != 0}, "
              + $"clearPositionDirty={(mask & Odyssey.BisectBitClearPositionDirty) != 0}, "
              + $"healthPin={(mask & Odyssey.BisectBitHealthPin) != 0}, "
              + $"healthMirror={(mask & Odyssey.BisectBitHealthMirror) != 0})");
        return mask;
    }

    private static uint ParseHexOrZero(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        string trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[2..];
        }

        return uint.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out uint parsed) ? parsed : 0;
    }

    /// <summary>Owner-side ids driven on the most recent pass whose local body stood close
    /// enough to this player for its own AI to be fighting them, and when that was recorded.
    /// Frame thread only, both writes and reads.</summary>
    private static readonly uint[] EngagedOwnerId = new uint[SharedBlock.EnemyCapacity];
    private static int _engagedCount;
    private static long _engagedAtTicks;

    /// <summary>Past this the record is not evidence of anything — no drive pass has run at
    /// all (a module change, the conversation hold, an ended session), so nothing local is
    /// fighting and every forwarded point is this player's to take.</summary>
    private static readonly long EngagedFreshTicks = TimeSpan.FromSeconds(1).Ticks;

    // ---- ROUND 52: ground-truth diagnostic support for PhantomHitHook.OnDamage. Nothing
    // below this point changes what gets driven or absorbed — it only lets the damage hook
    // (which cannot call DriveOne or read this controller's instance fields directly)
    // classify an attacker id it sees land on the LOCAL PC. See
    // docs/research/shared-state-audit.md §2, round 52.

    /// <summary>Owner-space ids of every creature <see cref="StartLocalFight"/> currently has
    /// armed (up to <see cref="SharedBlock.LocalCombatSlotCapacity"/>), mirrored into a static
    /// array every frame right where <c>block.SetLocalCombatExcludedIds</c> already publishes
    /// the same values — see the <c>OnFrame</c> call site. A zero entry means that physical
    /// slot is free. The N-wide sibling of the pre-N-slot single <c>_locallyFightingIdShared</c>
    /// field.</summary>
    private static readonly uint[] _locallyFightingIdsShared = new uint[SharedBlock.LocalCombatSlotCapacity];

    /// <summary>Owner-space ids this instance actually drove (placed, claimed, permitted to
    /// attack) on the most recent pass — the general "driven/mirrored census creature" set,
    /// broader than <see cref="IsLocallyEngaged"/> (which also requires proximity) and
    /// broader than <see cref="_locallyFightingIdsShared"/> (which is at most
    /// <see cref="SharedBlock.LocalCombatSlotCapacity"/> ids).
    /// Populated only at DriveOne's genuine "driven this frame" exit, not its early-return
    /// skips. Frame thread only.</summary>
    private static readonly uint[] DrivenOwnerId = new uint[SharedBlock.EnemyCapacity];
    private static int _drivenCount;

    /// <summary>Whether <paramref name="ownerObjectId"/> is one of the creatures currently
    /// pulled out of the census to fight the local player for real, in any slot.</summary>
    public static bool IsLocallyFightingCreature(uint ownerObjectId)
    {
        if (ownerObjectId == 0)
        {
            return false;
        }

        for (int i = 0; i < _locallyFightingIdsShared.Length; i++)
        {
            if (_locallyFightingIdsShared[i] == ownerObjectId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether <paramref name="ownerObjectId"/> was one of the enemies this
    /// instance mirrored on the most recent pass, regardless of engagement/proximity.</summary>
    public static bool IsDrivenCensusCreature(uint ownerObjectId)
    {
        if (ownerObjectId is 0 or Odyssey.ObjectInvalid)
        {
            return false;
        }

        for (int i = 0; i < _drivenCount; i++)
        {
            if (DrivenOwnerId[i] == ownerObjectId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="ownerObjectId"/> — an attacker id in the OWNER's id space, the
    /// space the enemy census and the phantom-damage channel both use — is an enemy this
    /// instance is genuinely driving AND is standing close enough to this player to be dealing
    /// them damage here.
    ///
    /// ⚠ THIS IS THE ENTIRE LICENCE FOR ABSORBING FORWARDED DAMAGE, and every class of damage
    /// the old blanket absorb swallowed silently fails one of the two tests: an enemy shed by
    /// the census cap or by distance was never driven; a creature the resolver skipped was
    /// never driven; a phantom-id collision returns before the drive; a mine, a trap or a
    /// scripted EffectDamage names an attacker that is not in the census at all; the
    /// conversation hold stops the pass so the record goes stale; and a fight 120 units away
    /// has no local body near this player. All of them now APPLY.
    /// </summary>
    public static bool IsLocallyEngaged(uint ownerObjectId)
    {
        if (ownerObjectId is 0 or Odyssey.ObjectInvalid
            || DateTime.UtcNow.Ticks - _engagedAtTicks > EngagedFreshTicks)
        {
            return false;
        }

        for (int i = 0; i < _engagedCount; i++)
        {
            if (EngagedOwnerId[i] == ownerObjectId)
            {
                return true;
            }
        }

        return false;
    }
}
