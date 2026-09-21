# Healing and buffing your partner: what the engine actually tests (2026-09-19)

Static analysis only (`tools/disasm.py`, `tools/xrefs.py`, `tools/q.py`, a linear disassembly of
the whole `.text` grepped for field offsets, and the K1 `nwscript.nss`, `k_inc_force.nss`,
`k_sup_healing.nss`, `k_sp1_generic.nss` and `spells.2da` / `repute.2da` extracted from the GOG
install with `tools/kotorfiles.py`). Nothing was run: every engine write below is NEEDS-LIVE.
Addresses are the GOG `swkotor.exe`. Tags: VERIFIED = read in the disassembly or the shipped
script source, INFERRED = a reading of it.

The review's gap (`review-seamless-2026-09-18.md` section 2, "No way to heal or buff your
partner") was written from `effect-sync.md` 10.1 and 11.4, which say a heal forwards once it lands
on a peer's stand-in, and assumed medpacs, Cure/Heal and Valor/Speed/Aura **target** a party member
that the stand-in is not. **That premise is wrong for most of the list.** Reading the scripts
that run the powers, four different mechanisms are in play and only one of them is a target test.

## 1. Verdict

| What the player does | What the engine actually does | Does the stand-in pass? | The one condition |
| --- | --- | --- | --- |
| Medpac, adrenal, stim, repair kit (`k_sup_healing`, `k_sup_comshots`) | The script heals `OBJECT_SELF` only: the USER. There is no "use a medpac on X" in K1; each character uses its own from its own party-portrait menu. | not applicable, no target exists | there is no partner target to satisfy |
| Force Heal (28), Force Cure (10) | Loops `GetPartyMemberByIndex(0..2)` (the `CSWParty` roster), heals each within 15 m. No target, no reputation test. | **fails** | not being in the `CSWParty` roster |
| Recovery Stim (item spell 129) | Same `GetPartyMemberByIndex` loop, heal 30 and resurrection. | **fails** | not in the `CSWParty` roster |
| Force Valor 22 / Knight Valor 33 / Master Valor 36 (`FORCE_POWER_FORCE_MIND` / `KNIGHT_MIND` / `MIND_MASTERY`) | Sphere 30 m around the caster, every creature for which `GetIsFriend` holds. This is the only genuine friend test. | **fails** | `GetReputation(caster, stand-in) >= 90` reads **50** |
| Force Speed (8/34/37) | `Sp_ApplyEffects(..., OBJECT_SELF, 0.0, 1, ...)`: the caster only. | not applicable | self-only by design |
| Force Aura / Armor / Shield, Resist Force | Count 1 on `GetSpellTargetObject()`, and `spells.2da` gives them range `P` (personal, 0 m), so the target is the caster. Same `GetIsFriend` gate inside `Sp_ApplyEffects` if a target were ever supplied. | not reachable | no UI supplies another target |
| Click a friendly creature in the world | Builds **no** action list at all (section 3.2). | n/a | the client offers nothing on a friendly body, companion or stand-in |

**The single failing condition, for the one path a faction can fix (Valor family):**
`CFactionManager::GetNPCFactionReputation` (`0x0052B420`). The caster is a party member, so
`CSWSCreature::GetCreatureReputation` (`0x00513FF0`) sets its own faction to 0 (Player); the
stand-in is not `+0xA88` in-party, so its faction is read as stored, and a Player-faction stand-in
is 0 too. The matrix lookup is asked for (0, 0): `0x0052B43C  cmp esi,1 / jl 0x0052B481` (and the
`edx` test just above it) fall to `0x0052B481  mov eax, 0x32` and the answer is **50**. Player
against Player is not a cell in `repute.2da`. `GetIsFriend` (routine 236, in the shared handler at
`0x00537CE0` that the symbol DB mislabels `ExecuteCommandGetEmotions`) then does
`0x00537DB9  call 0x0057CB80` then `0x00537DC0  cmp eax,0x5A / setge`: 50 < 90, no friend. It is not an enemy either
(`> 10`), so the stand-in is plainly neutral to your own party, which is exactly why heal and buff
scripts walk past it and hostile ones do too. A stand-in held Neutral (faction 5) reads 50 as well.

For the heal path the condition is different and no faction fixes it: the scripts never ask
reputation at all, they ask the party roster.

## 2. The server side: no target validation to satisfy

Both `CSWSCreature::AIActionCastSpell` (`0x00514AF0`) and `AIActionItemCastSpell` (`0x0050F170`)
were scanned for every call target across their whole bodies. Neither calls `GetReputation`,
`GetFactionOfObject`, `IsPartyMember`, or any friend/enemy helper on the TARGET. What they do:

- `CServerExoApp::GetGameObject(target)` must resolve (`0x00514D40`), then a creature target is
  refused if `[vtbl+0x94]` or `GetDeadTemp` (`0x004EF890`) is true for it (`0x00514DB8`,
  `0x00514DCD`; INFERRED to be the dead/down tests). The stand-in is alive and resolvable: **passes**.
- The only `CSWParty` touch (`CClientExoApp::GetSWParty 0x005ED8B0`, `CSWParty::GetIndex
  0x006349C0`, `0x00514B5A-0x00514B61`) is on the CASTER, to stamp a combat message on the
  caster's own party-member record. It is not a target test.
- `CSWSCreature::UseItem` (`0x004FC210`, called from the input handler at `0x00525852`): item must
  exist and pass `CanUseItem` (`0x0051B440`), have an active `CastSpell` property, and the target
  must resolve. One extra refusal at `0x004FC2C5-0x004FC2D5` looks the target up in the caster's
  own perception list (`0x00517A20`, a linear search of `+0x920`) and refuses when
  `(node[+4] & 0xC) == 4`. That is a visibility-node bit test, not a friend or party test; its
  exact meaning was not established. A stand-in in plain view is not expected to trip it.
- Then `SpellCastAndImpact` (`0x004CDF50`) runs the spell's impact script. The engine has no
  `TargetType` column in K1's `spells.2da` (the 53 columns are `category`, `range`,
  `forcehostile`, `forcefriendly`, `forcepassive`, `hostilesetting`, `itemtargeting`, ...). Column
  `hostilesetting` is 0 for every helpful power and medpac, 1 for the harmful ones. `range` is
  `T` (touch, 2.25 m, `ranges.2da`) for Heal, Cure, medpacs; `P` for Valor, Speed, Aura, Armor.

**Everything that looks like target validation lives in the impact scripts** (`k_inc_force`
`Sp_RunForcePowers`, `Sp_ApplyEffects`; `k_sup_healing`), quoted in section 4.

## 3. The client side

### 3.1 The reaction byte: why a friendly stand-in would show green, not why it can't be picked

`CSWCCreature+0x2DC` is a per-creature reaction byte. It is written from the server's creature
update (`CSWCMessage::HandleServerToPlayerCreatureUpdate_Update`, `0x00666A4B`), initialised to 2 in
the constructor (`0x00616DF7`), and read by `CSWCCreature::IsHostile` (`0x006170C0`, vtable slot
`+0x138`, `== 2`) and by the target highlight (`CSWCArea::HiliteAreaObjects`, `0x00605D0C`: 1 is
the friendly tint). It is a display and menu-selection input, not a permission.

### 3.2 There is no friendly targeting on the client, for anyone

`CSWCCreature::GetInterfaceTargetType` (`0x0060FCC0`) returns 4 for a hostile creature and 3 for a
non-hostile one. `CSWCCreature::GetTargetActions` (`0x00619C20`) switches on it: type 4 builds the
attack, combat-feat, hostile Force power and hostile item lists (`0x006191F0`, `0x006198E0`,
`0x00619950`); type 3 goes through the placeable branch, whose `[vtbl+0x4C]` is null for a
creature, and returns at `0x00619D97` with an empty list. A companion is a non-hostile creature,
so **clicking a companion offers no heal either**. The stand-in is treated exactly like one.

The place a helpful power or item is used is the party portrait's own menu:
`CSWCCreature::GetSelfForcePowers` (`0x00616230`) and `GetUsableItems` (`0x006197D0`) build it,
each entry calling `CSWCCreature::PartyMenuUseItem` (`0x0060F590`) with `this` = the portrait's
creature. That sends `SendPlayerToServerInput_UseItem` (`0x00677BD0`) with the portrait creature's
own id (`[this+4]`) in the message and `OBJECT_INVALID` as the target (INFERRED: the item is used
by, and heals, the portrait's creature). The list of portraits is the `CSWParty` roster, so there is nothing to click
for a stand-in, and no click-targeting rule can be satisfied by a faction.

`CSWCCreature::PerformUseItem` (`0x0060EE60`) and `PerformCastSpell` (`0x0060EF10`) accept an
arbitrary target id and pass it straight to the server, but their only caller is the tutorial box
(`0x006AA429`, `0x006AA341`).

### 3.3 Stand-in scorecard

| Check | Address | Stand-in (Player or Neutral faction, not in party) |
| --- | --- | --- |
| target object resolves | `GetGameObject 0x004AE750` | passes |
| target alive | `GetDeadTemp 0x004EF890` | passes (min-1-hp keeps it above dead) |
| `GetReputation(caster, target) >= 90` (`GetIsFriend`) | `0x00537DC0` / `0x0052B481` | **fails, 50** |
| `GetReputation(caster, target) <= 10` (`GetIsEnemy`) | `0x00537E03` | fails (correctly: not an enemy) |
| target in `CSWParty` (`GetPartyMemberByIndex`, `IsObjectPartyMember`) | `0x0053C870`, `0x0053F160` | **fails** |
| target's `+0xA88` in-party flag (the reputation override) | `GetCreatureReputation 0x00513FF0` | fails (0) |
| client offers a target on a friendly world click | `GetTargetActions 0x00619C20` | fails, as it does for every real companion |
| party portrait exists | `CSWParty` roster | fails |

## 4. The scripts that do the testing (VERIFIED, shipped source)

`k_inc_force.nss`, `Sp_ApplyEffects` (the gate is the `if` right after the shape iteration):

```
if ((GetIsEnemy(oTarget) && SWFP_HARMFUL == TRUE) || (GetIsFriend(oTarget) && SWFP_HARMFUL == FALSE))
```

Valor (`FORCE_POWER_FORCE_MIND`): `SWFP_HARMFUL = FALSE; Sp_ApplyEffects(FALSE, OBJECT_SELF, 30.0, 1000, eLink1, 20.0, ...)`
where `eLink1` is three saving-throw increases and six ability increases (+2 each; Knight Valor +3,
Master Valor +5 with `EffectImmunity` poison on the last). `GetFirstObjectInShape` finds every
creature in a 30 m sphere; only friends are buffed.

Heal and Cure: `int nHeal = WIS + CHA + 10 (5 for Cure) + GetHitDice(OBJECT_SELF)`; then, if
`IsObjectPartyMember(OBJECT_SELF)`, `oParty = GetPartyMemberByIndex(nCnt)` for `nCnt` 0..2, each
healed when valid, not a droid, and `GetDistanceBetween(OBJECT_SELF, oParty) < 15.0`. A caster who
is not a party member heals itself and the nearest friendly creatures instead.

`k_sup_healing.nss` (`ITEM_ABILITY_MED_PACK` 64, advanced 65, life support 66, repair kits 84/127/128):
`ApplyEffectToObject(DURATION_TYPE_INSTANT, EffectHeal(nSkill), OBJECT_SELF)`, with `nSkill` built
from the user's Treat Injury or Repair rank and Wisdom or Intelligence. Item 129 (Recovery Stim)
loops `GetPartyMemberByIndex(0..2)` and heals 30, resurrecting the dead.

## 5. What forwards today, and the buff types that do not

Forwarded kinds (`PeerEffectKind`, capture in `PhantomEffectHook`, copy in `PeerEffectRules`,
apply in `OdysseyEffects`): Stun, Paralyze, Knockdown, Slow, Frightened(Horrified), Sleep, Choke,
ForcePush, Whirlwind, **Heal (39)**, **TemporaryHitpoints (15)**, Confused = the original 12;
Fear, Stunned, Poison, Disguise = the +4 (`effect-sync.md` 12). Heal and temporary hit points are
the only helpful members, and both need the effect to LAND on the stand-in.

Not on the allowlist, and staying off it in this pass: `OnApplySavingThrowIncrease` (26),
`OnApplyAbilityIncrease` (36), `OnApplyACIncrease` (48), `OnApplySkillIncrease` (55),
`OnApplyMovementSpeedIncrease` (28), haste (3, 41), attack and damage increase (10, 13), force
resistance increase (33). The rule for adding a kind is that its receive-side apply path is already
live-verified; `OdysseyEffects` has no constructor for any of these (grep: no `AbilityIncrease`,
`ACIncrease`, `SavingThrowIncrease`, `Haste`). Valor is nine linked components of exactly the
unverified kinds (three 26, six 36), two of them multi-integer, so it needs a magnitude packing and
a linked-effect capture decision (`OnApplyLink`, 40) that nothing here has measured. Speed and Aura
are also `28`/`48`/`26` mixtures. Documented, not shipped.

## 6. Ways to make the stand-in satisfy the tests

| Option | Satisfies | Cost | Decision |
| --- | --- | --- | --- |
| Seat the stand-in in **Friendly_1** (faction 2) instead of Player | `GetIsFriend` (reputation 100 from the Player row) | changes how a handful of creatures see the stand-in (below) | **shipped behind `partner_buffs`, OFF, NEEDS-LIVE** |
| Write the `+0xA88` in-party flag on the stand-in | `GetCreatureReputation` returns 100; `GetIsFriend` holds | it is the same flag `SaveCreatures` (`0x00507680`) and party-follow (`AIActionPartyFollowLeader 0x00511130`) key on, and the roster-adjacent state that has crashed the game before (`party-reconciliation.md`, the engine-write rules) | rejected: a real party-member flag by another name |
| Detour `CSWSObject::GetReputation` (`0x0057CB80`, 51 call sites) or the `GetIsFriend` handler | everything friend-shaped | a hot AI function, every tick, for every creature | rejected: far more blast radius than a faction id |
| Add the stand-in to `CSWParty` | heal, Cure, stim, medpac-by-portrait, Valor | roster, HUD portrait, save and follow state; the memory rules forbid it | rejected |
| Mirror the cast (design, not built): see section 8 | heal and Cure for real | a new capture site | designed only |

### 6.1 What Friendly_1 changes about who fights the stand-in

From `repute.2da`, an enemy row E against the stand-in reads `row E, column Friendly_1`; against a
Player-faction body it reads `Player row, column E` (`GetNPCFactionReputation`'s swap when one id
is below 1). Comparing every faction (hostile <= 10, friend >= 90, else neutral):

- Same verdict for Hostile_1, Hostile_2, Insane, ptat_Tuskan, Zone_Controller, Sacrifice, Neutral,
  every Surrender, Predator, Prey, Rancor and Gizka row, and for Friendly_1 itself: the ordinary
  monsters and soldiers still attack it.
- **Czerka, Trap and Endar_Spire creatures stop attacking it** (Player row 0, Friendly_1 column 50).
- **Friendly_2 creatures start attacking it** (Player row 100, Friendly_1 column 0). Their hits
  forward as real damage to the partner, which is the reason this stays a default-OFF developer
  switch until a rig pass shows who those creatures are in the modules a run visits.

Friendly_2 (faction 4) and gbl_xor (8) are strictly worse candidates (`gbl_xor` also flips
Zone_Controller from hostile to friendly). When the stand-in is held Neutral for any existing
reason (peer down, stealthed, `phantom_targetable` off, the `local_combat` authority split) it
stays Neutral whatever `partner_buffs` says, because a Friendly_1 body is attacked by Hostile_1.
That means **buffs cannot reach the stand-in on an instance that holds peers Neutral for the
authority split**, by design.

## 7. What shipped

`coop.ini` `partner_buffs`, default OFF, read once by `PhantomController` through
`PluginConfig.ReadBool("partner_buffs", false)`. It is plugin-only: the agent never reads it and
it is not a digest field, so there is no second or third default to keep in step; it is listed in
the developer-key tables of both `PluginConfig.cs` and `CoopConfig.cs` so it is not logged as
unknown. The one engine write is the existing `Odyssey.SetCreatureFaction` with faction id 2
(`Odyssey.FactionFriendly1`) in place of 0, chosen by the pure rule
`PhantomTargetRule.FactionFor(wantsPlayerFaction, partnerBuffs)` (unit-tested in
`PhantomTargetRuleTests`). With the key OFF the code path is unchanged.

**What it does and does not buy.** With the key on, a Force Valor family cast by the local player
lands on the partner's stand-in on the caster's screen. It does NOT reach the partner's real body:
the effect kinds are not forwardable (section 5). Heal, Cure, Recovery Stim and medpacs are
untouched, because none of them ever asks about friendship. This pass therefore proves the
engine-side half (the stand-in becomes a friend) and stops there.

### 7.1 NEEDS-LIVE recipe (two instances, both agents, `partner_buffs = 1` in the caster's `coop.ini`)

1. Both players in the same module, partner standing within 10 m, host/joiner does not matter.
2. On the caster: console `faction <phantomId> me`. Before the change the stand-in reads neutral
   (`GetAIStateReputation` 0, raw faction 0 or 5); with the key on it must read friendly (1) with
   raw faction 2. `creatures.ps1` or the `items`/`effects` verbs give the stand-in's id; the plugin
   log line `phantom 0x... joined the friendly faction (partner_buffs)` names it.
3. Spawn a hostile next to the pair with `spawn n_sithsoldier001 1` (host) and confirm it still
   attacks the stand-in (the Hostile_1 row); repeat with a Czerka guard and a Friendly_2 NPC to
   see the two deviations in section 6.1.
4. Give the caster Force Valor (a save that has it; there is no grant verb, see
   `class-and-power-grants.md`) and cast it from the quickbar with the partner in range. Expect the
   stand-in to show the Valor visual on the caster's screen, `effects` on the caster to list no
   captured kind (Valor is not on the allowlist: this is the negative control), and nothing on the
   partner's screen. Without the key, the same cast leaves the stand-in unaffected.
5. Click-targeting negative control: with the stand-in selected (click it) the radial shows no
   Heal/Cure/medpac entries, the same as clicking a real companion. This is expected, not a
   failure, and it confirms section 3.2.
6. Restore: set `partner_buffs = 0` (or remove it), reload the module, `faction <id> me` reads
   raw faction 0 again.

Existing staging that already exercises the forwarding half: `applyeffect heal <phantom>` (aimed at
a stand-in) captures and forwards for real, both directions live-verified (`effect-sync.md` 10, 11).

### 7.2 Live result, faction step (2026-09-19)

Recipe step 2 on the rig with `partner_buffs = 1`: the plugin logged `phantom 0x115: joined the friendly faction (partner_buffs)` and `faction 0x115 me` on the host read `0x115(faction 2) vs 0x7FFFFFFF(faction 0): GetAIStateReputation(0x115,0x7FFFFFFF)=1 GetAIStateReputation(0x7FFFFFFF,0x115)=1` - friendly both ways (the stand-in was raw faction 5 / neutral without the key). Steps 3-5 (a hostile still attacking the stand-in, the Valor cast itself, the negative controls) were NOT run: there is no console verb that casts a Force power, so they need a save with Valor and a quickbar click. `partner_buffs` stays OFF.

## 8. The design that would actually heal a partner

Because Heal, Cure and the stim iterate the `CSWParty` roster, only a cast MIRROR can reach a
partner: when the local player's Heal or Cure lands on a local party member, also apply the same
amount to the partner's real body, provided the partner's stand-in is within 15 m of the caster
(Valor: 30 m). The existing pieces cover most of it:

- Capture point: `PhantomEffectHook`'s `OnEffectApplied` splice already sees every effect. The
  sender-side case is `engineType == 39`, `effect.creator == local PC`, `effect.spellId` in
  `{28, 10}`, a positive `integer_list[0]`, and a target in the local PC/party identity set.
  A target that is already a watched stand-in is refused.
- Publish: reuse the `PeerEffectKind.Heal` record with the amount from `integer_list[0]`; the
  receive side (`OdysseyEffects.ApplyEffect`, live-verified) already heals the real body.
- Distance check on the sender from the stand-in's and the caster's positions.
- No protocol change is required; the new state is a sender-side rule.

The creator and spell fields are now supported by an offline disassembly of the installed GOG
binary. `ExecuteCommandGetEffectCreator` (`0x00537AA0`) resolves the effect, then reads
`[effect+0x18]` at `0x00537AD7`; `GetEffectSpellId` (`0x00537B90`) resolves the same effect,
then reads `[effect+0x1C]` at `0x00537BBC`. The hook uses those verified raw offsets rather than
calling either VM helper from the hot dispatcher. The generic effect hook writes a Heal event
to the existing OUT ring, once per nearby phantom per frame/spell key, so the agent's existing
phantom-id-to-peer map and `PeerEffect` packet remain unchanged. The receiver applies the
instant Heal constructor with its existing `OdysseyEffects.ApplyEffect` path; that synthetic
effect has no local cast creator and therefore cannot echo back into this mirror.

The retail script source was also read from `data/scripts.bif` (`k_inc_force.nss`) in the same
offline GOG install. Both Cure and Heal enter a direct `while (nCnt < 3)` loop, resolve each
`GetPartyMemberByIndex(nCnt)`, and call `ApplyEffectToObject(... EffectHeal(nHeal), oParty)`
inline after the 15 m test. There is no `DelayCommand` or deferred action around these three
applications. The frame/spell dedup therefore collapses the repeated roster effects from one
script invocation while allowing the next frame's cast and a changed phantom slot to claim a
fresh event.

The implementation is `partner_buffs`-gated and remains **NEEDS-LIVE**: no game cast was run
in this pass. The dedup key remains conservative until the rig watches an actual cast.

## 9. Status line

Friendship of the stand-in is fixable with one faction id behind `partner_buffs` (OFF,
NEEDS-LIVE) and reaches only the Valor family, locally. The Heal/Cure sender mirror is now
implemented behind the same switch (OFF, NEEDS-LIVE); it supplies the remote body through the
existing Heal event path while preserving the party-roster target check. Medpacs are self-only
in K1, so that half of the gap was never real.
