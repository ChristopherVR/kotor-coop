# PvP engine feasibility (research 2026-10-09)

Static read of code and existing research only. Nothing was run, built or edited. Tags: VERIFIED = read in code or in a cited, previously verified doc; INFERRED = deduced; NEEDS-LIVE = untested.

## 1. What stops player A attacking B's phantom today

The phantom is a creature in B's slot, driven by `PhantomController`. Four independent layers keep A from hurting it deliberately.

1. **Faction.** `PhantomController.Drive.cs:770-797` puts the phantom in Friendly_1 (id 2, `Odyssey.FactionFriendly1`, `Odyssey.Faction.cs:24`) when `partner_buffs` is on (default), else Player (0) or Neutral (5). `PhantomTargetRule.FactionFor` (`PhantomTargetRule.cs:68`) picks the faction. Friendly_1 reads 100 from the Player row, so `GetIsFriend` holds and A's PC and companions never treat it as an enemy (`partner-buffs.md`). Neutral (50) is not hostile either. Hostility is decided by `CSWSObject::GetReputation 0x0057CB80` -> `GetCreatureReputation 0x00513FF0` -> faction matrix (`combat-participation.md` section 2). There is no per-creature reputation in K1 (VERIFIED).
2. **Client targeting.** The cursor and click menu are built from the client reaction byte `CSWCCreature+0x2DC` (`IsHostile 0x006170C0`, `== 2`), which the server derives from reputation. `GetInterfaceTargetType 0x0060FCC0` returns 4 (hostile: attack list) or 3 (non-hostile: empty list). A non-hostile phantom therefore cannot be clicked to attack (`partner-buffs.md` section 3.2, VERIFIED).
3. **Attack bit.** `Odyssey.MarkAsRemotePlayer` (`Odyssey.Combat.cs`, around line 400) sets min1hp, clears plot, and clears `CreAttackPermittedBit` on the phantom, so it never swings. Min1hp means A's game can never kill it (`GetDead` is never true), so it also never releases attackers by dying. `PacifyPhantom` and the Neutral flip are the release levers. This is consistent with PvP: B's real PC does the fighting in B's game.
4. **Plugin holds.** `ApplyTargetable` (`PhantomController.Drive.cs:~715-760`) forces Neutral when `holdNeutral` (local_combat on a mirroring client), the peer is dead, stealthed (`stealth_sync`) or menu-guarded. `PhantomHitHook.OnDamage` also drops hits when `_menuGuarded[slot]` and while `PlacedMineFireGateHook.PartyMineWindowOpen`.

**To make a phantom a legal PvP target (only inside a PvP context):**
- Add a `PvpActive` level (session-negotiated, consenting peers only). When it is true and the peer is alive, `PhantomTargetRule.FactionFor` returns a new `Kind.PvpHostile`, so the rest of the plumbing is reused. The faction must read `<= 10` from A's PC and must NOT be one that A's enemies fight. Hostile_1 reads 0 from the Player row, so A and A's party attack it. Hostile creatures are the same faction, so they ignore it.
- `GetReputation` also has a party override (`GetCreatureReputation`: party members count as faction 0), so A's companions become hostile to it automatically.
- The phantom still never swings. Hits on it are real engine damage events, so `OnApplyDamage` fires.
- Release on PvP end by flipping back to Friendly_1 plus `PacifyPhantom`, using test 6 (`GetReputation > 10`) at the next round boundary (`CreateNewAttackActions 0x005B6A46`).
- Must not weaken the stealth, menu-guard and peer-dead holds; a PvP hold should also release if the peer is protected.
- Caveat (NEEDS-LIVE): the reaction byte refreshes via the creature update, so a faction flip may lag a frame or two before the cursor turns red.
- The blunt alternative, the global `[0x007A2068]` GetReputation override (-1 = off, `combat-participation.md` section 2.1), affects everything and is rejected.

## 2. Damage routing A -> B

The channel exists. `PhantomHitHook` (detour on `CSWSEffectListHandler::OnApplyDamage 0x004DFA40`) already treats a hit on a watched phantom as a hit on the peer's real character, whoever the attacker is. The "slot >= 0" branch of `OnDamage` records any creator, including A's own PC or companions. It sums per-type amounts after hit, miss, crit and sneak attack, as a cumulative total plus a per-attacker table (`_cumulative[slot]`, `_attackerTables`, `PhantomHitHook.cs:~950-975`). It travels as `PhantomDamage` (PacketType 31, `PacketCodec.Damage.cs`) with a per-attacker breakdown. B's `IncomingDamageController` (cap 200 per application, `RunPerAttackerPass`) applies the delta with `Odyssey.ApplyDamageEffect` (VM `EffectDamage`), and `damage_creator_attribution` can stamp the creator (`TrySetEffectCreator`).

What is missing or wrong for PvP:
- **Double reduction.** The hook measures damage after A's engine already ran A's formulas against the phantom's stats, then B's engine applies B's resistances and difficulty scaling again (`peer-damage-ledger.md` sections 1-2). It is probably acceptable. For fairness, apply as raw typed damage or normalise.
- **Scaling.** `ScaleHostileDamage` (`enemy_damage`) rewrites hits on player bodies whenever slot >= 0, so it would scale PvP hits like enemy hits. It needs a PvP bypass.
- **Attribution.** The wire carries A's attacker ids, but those ids are A's. B needs to map them to the A phantom or A's companion bodies. The creator write is untested for aggro (it did not retarget, section 8.10).
- **Crits and feedback.** A hit's crit flag is not carried. B sees only a damage floaty and a health drop, with no hit animation, so B's own game gives no reactive feedback of "who hit me".
- **Non-damage effects.** Force powers and Stun/Choke/Knockdown on a phantom are captured by `PhantomEffectHook` (effect-sync.md section 1) and forwarded as `PeerEffect` events (kinds 1, 4, 7, 14 and the rest). That would apply to B's real PC. Check it is not silently gated: `effect_sync` today is applied to B as buffs (Heal) and as debuffs from enemies, so a hostile cast reaching B should work, but it is NEEDS-LIVE. Force Push/Lightning/Drain damage rides the normal damage path, while Stun/Paralysis/Sleep rely on the effect-sync ring. Capture is gated by `PhantomHitHook.HasAttackers` (the attacker set must be non-empty) and the enemy-effect rules, so PvP needs its own allowlist and a recheck of the "hostile to a PC" semantics in `enemy-effects.md`.
- **Visual hit reaction** on the phantom is local only (animation-sync).

## 3. Who is authoritative?

Current model: B's game owns B's health (health is the shared truth), the host owns enemies and the ledger, damage is cumulative and idempotent (a lost packet costs a tick, duplicates cost nothing).

- **Defender-authoritative (recommended).** A reports "I dealt N (cumulative, per attacker, per type)". B's game applies it, so B's HP is only ever changed by B's engine. Cheating risk: A could under- or over-report (the ledger has a plausibility gate, `MaxPlausibleHit = 2000`, and the MaxPerApplication cap of 200 per pass). Latency: A sees the hit land immediately on the phantom (cosmetic) and B takes it within one packet plus the `IntervalFrames` pass (3-10 frames).
- **Attacker-authoritative.** A decides it killed B. That means A's view is the truth of B's HP, which contradicts the model.
- **Host-arbitrated.** Cleanest for a 3+ player round (the host already merges ledgers and sits in the middle of the star topology) but adds a hop and a new rule that peers cannot trust the host in a host-vs-guest duel.

The A -> B channel does not touch the host unless the host is B. Given the existing model, defender-authoritative with host-side plausibility accounting and the host as referee for round state (not HP) fits best. Resolution of misses is client-side on A (A's hit roll), which is the usual cheating hole and is acceptable for a friendly game.

## 4. Death and defeat

Existing machinery: B's PC at HP <= 0 becomes temp-dead (`GetDeadTemp`). `OnApplyDeath` auto-swaps control to the next living party member, which funnels through `ChangeCharacter 0x005F6B60`, already suppressed by the control-lock hook (`death-handling.md` section 2). `TempDeathUpdate 0x004B6DA0` calls `RunDeathSequence` (game over) from one site (0x004B6E99). `DeathController` swallows it behind `SuppressGameOver` and reports a `GameOverSerial` to the agent, which clears the suppression only on a genuine all-players-down wipe (`WipeRecoveryRule` then forces a rejoin or reload). Resurrection (type-4 effect, `Odyssey.Downed.cs`, requires `is_raiseable`) stands a PC back up, and the engine auto-revives when no hostile is visible within 10 m for 5 s.

For PvP:
- In an active round, the agent must treat "peer down" as a PvP defeat, not a party wipe: keep `SuppressGameOver` on for the duration and never count a PvP-downed peer in the all-players-down vote. A PvP loss should not run `WipeRecoveryRule` (a session reload, with host save and re-adopt).
- A defeat can be a no-death floor instead: with min1hp (`SetMinOneHitPoint`, `+0x200`) on both PCs, HP floors at 1 and nobody goes down, and the round ends on "HP <= threshold" decided by the defender's game. This avoids the wipe path completely, at the cost of stat-pinned immortals. It has side effects for real enemies in the same area, so only use it in a PvP arena or an empty area.
- Alternative: let B go down (temp-death), then revive with `Odyssey.Downed`'s Resurrection at the round end, and clear aggro with `PacifyPhantom`.
- Respawn/end: teleport both, restore HP to the pre-round value (snapshot), clear the PvP flag. The peer-damage ledger totals are cumulative, so the PvP window needs its own baseline (new serial or reset on both sides) to avoid replaying history (`peer-damage-ledger.md` sections 3, 7).
- Note `death-handling.md` section 5, point 1: an unkillable 1-HP PC never disengages hostiles; keep min1hp PvP-scoped.

## 5. Companions, stealth, mines, grenades

- **Companions.** Companions are host-authoritative bodies, mirrored on the joiner (`companion-state-sync.md`; `companion_local_combat` is OFF). A's companions are in the local party, so with a hostile phantom they attack it by AI. Their damage is counted as A's hits (`BuildLocalPartyIds`: PC plus roster, `PeerDamageController.cs:270`) and forwarded to B as part of the same cumulative. B's companions are mirrored, not real, in A's game and would need the same treatment. The mirrored copy of B's companion in A's game is just a stand-in with no authority. Simplest rule: companions are benched or set to non-aggressive in PvP, or only the PC fights.
- **Stealth.** `stealth_sync` holds the phantom Neutral when B is stealthed (protocol 53), which in PvP is a real hide mechanic that works automatically, but must also be treated as an A-side cheat control (B can toggle stealth to dodge).
- **Mines/grenades.** Mines require a hostile faction (`mines.md` line 95, `GetReputation <= 10`) and `PlacedMineFireGateHook` currently drops party mines' hits on stand-ins. PvP would need its own window flag so a mine A places hurts B's phantom. Grenades and area effects are normal damage events and would work through the phantom path (including friendly-fire splash that already forwards today).
- **Menu guard.** `menu_guard_sync` stops attacks while B has a menu open, capped by `MenuGuardRule`, so it is a bounded shield.

## 6. Risks and work (ranked)

1. **Faction/reputation flip side effects (medium risk, small code).** Hostile faction for A's companions and for area AI, release correctness, reaction byte lag. Reuses `ApplyTargetable` edge-latch.
2. **Death and wipe interaction (high risk, medium).** PvP down must not reach `RunDeathSequence`/WipeRecovery or companions' dead-state logic; needs a session-level PvP round state and tests on both ends.
3. **Damage fidelity and cheating (medium risk, medium).** Double resistances, `enemy_damage` scaling, per-attacker identity mapping, no crit/hit flags, host referee.
4. **Non-damage effects (medium risk, medium).** Allowlist, stun-lock, Choke, hostile Force power semantics.
5. **Consent/UI (low risk, medium).** Invitation, in-world arena or area gate, round state, scoring, chat command.
6. **Mines, grenades, companions (low risk, small each).**
7. **Latency (low risk, small).** 3-10 frame apply interval is acceptable for a duel.

**Estimate:** faction mode plus PvP round state: small-medium. Damage path reuse with scaling bypass and attribution: small-medium. Death floor and round end: medium. Effects and mines: medium-large and deferrable.

**Milestone 1 "duel":** two consenting players in the same module, `/duel` handshake, both min1hp pinned PCs, companions benched, phantom switched to the PvP faction, existing PhantomDamage -> `IncomingDamageController` path with `enemy_damage` scaling bypassed, round ends when either published HP <= 25% of max (agent decides, host referees), both restored to the snapshot HP, factions released, no death, no game-over. Everything outside plain weapon and Force damage (stun-type effects, mines, stealth) stays disabled. Verify live: the cursor goes red, hits apply, nothing reaches `RunDeathSequence`.
