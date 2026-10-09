# The Star Forge finale — Lehon through the two endings, in a live co-op session (2026-08-27)

The endgame this project's own research had never opened.
`galaxy-map-and-party-select.md` §3 says so explicitly: "the Star Forge finale has no RE
coverage." This pass reads the real compiled scripts for the whole chain — Lehon
(`unk_m41aa`..`unk_m44ac`), the Star Forge (`sta_m45aa`..`sta_m45ad`), and both endings
(`STUNT_42`/`44`/`50a`/`51a`/`54a`/`55a`/`56a`/`57`) — and checks every forced solo/party-split
beat against the mechanisms this mod already ships.

RESEARCH ONLY. No code changed, no game launched.

Tag key: **VERIFIED** = read directly out of the game's own compiled bytecode or an existing
VERIFIED claim in another doc · **INFERRED** = a reasoned deduction from the evidence, not
independently proven · **NEEDS-LIVE** = must be confirmed on the running two-instance rig.

Read alongside [cinematics.md](cinematics.md) (the movie-queue relay design this doc leans on
throughout), [dialogue-sync.md](dialogue-sync.md) (`DialogueSpectateController`),
[transition-smoothness.md](transition-smoothness.md) (`follow_lead`, door
`transition_announce`), [minigames.md](minigames.md) (the park/hide precedent and its own NCS
disassembler), [peer-damage-ledger.md](peer-damage-ledger.md) (owner-side healing propagation),
[quest-sync.md](quest-sync.md) (the global-variable merge), and
[galaxy-map-and-party-select.md](galaxy-map-and-party-select.md) §3 (the gap this doc closes).

---

## 0. Correction to the brief, up front

The finale is **not** three or five modules — it is **eighteen**, plus the credits chain:

* Lehon: `unk_m41aa`/`ab`/`ac`/`ad` (beach), `unk_m42aa` (temple approach), `unk_m43aa` (the
  Rakata gate fight), `unk_m44aa`/`ab`/`ac` (temple interior — the actual trials), plus
  `ebo_m41aa` (the Ebon Hawk parked on Lehon) — **nine** modules, not one.
* Star Forge: `sta_m45aa` (docking bay), `sta_m45ab` (the droid-horde production floor),
  `sta_m45ac` (the shield-generator floor and Bastila's chamber), `sta_m45ad` (Malak's chamber)
  — **four** modules. There is no `sta_m45ae`; `layouts.bif` also has an `m45mg.lyt` (an unused
  minigame layout, never instanced in any `.rim`) — a dead file, not a fifth playable module.
* The endings: `STUNT_42`/`STUNT_44` (Carth's and Bastila's light-side vignettes),
  `STUNT_50a`/`51a`/`54a`/`55a`/`56a`/`57` (the branching credits chain), plus `ebo_m46ab` (an
  unrelated late-game Ebon Hawk puzzle room — Rakatan riddles and a screaming prisoner — that
  shares no globals with the finale and is out of scope below).

## 1. Method

`tools/kotorfiles.py` has no NCS disassembler; this project's own one does —
`src/KotorCoop.Memory/GameFiles/NcsReader.cs` and `NwscriptActions.cs`, built and live-tested
against real game files in `minigames.md` §13. It ships with six resolved action-routine ids
(`GetGlobalBoolean`/`SetGlobalBoolean`/`GetGlobalNumber`/`SetGlobalNumber` VERIFIED,
`GetEnteringObject`/`GetIsPC` INFERRED). This pass needed more — `SetPartyLeader`,
`RemovePartyMember`, `ActionJumpToObject`, `ActionStartConversation`, `SetLockOrientationInDialog`
— none of which the plugin calls directly (they're script-only NWScript actions), so
`engine-api.md` has no address for them either.

**Resolved the same way `cinematics.md` resolved `StartNewModule`=509 and `PlayMovie`=733**:
`CSWVirtualMachineCommands::InitializeCommands 0x0054C960` fills a 772-slot table with
`mov [reg + id*4], handler` stores; the symbol database names each handler
(`ExecuteCommandRemovePartyMember 0x00541C00`, etc. — found via `tools/q.py func`), and
`tools/disasm.py 0x0054C960 60000` plus a grep for each handler address gives `id = offset/4`
directly. Cross-checked against two pre-existing independent claims that had to fall out of the
same table: `575`=`RemovePartyMember` and `509`/`733` all matched. **VERIFIED**, same method,
same confidence:

| id | routine | handler | how found |
| --- | --- | --- | --- |
| 13 | `SetPartyLeader` | `0x00545A70` | `[eax+0x34]` store, confirmed in the loop's `mov eax,[esi+0xC]` / store / reload pattern |
| 196 **and** 385 | `ActionJumpToObject` | `0x0052CCE0` | two separate stores wire to the identical handler — a real duplicate/alias in this table, not a scan error (both offsets are inside the same contiguous store sequence). **INFERRED**: 385 may be a deprecated numeric alias kept for old compiled content; not resolved further |
| 204 | `ActionStartConversation` | `0x0052D5B0` | `[ebx+0x330]` store |
| 505 | `SetLockOrientationInDialog` | `0x005470E0` | `[edx+0x7E4]` store |
| 575 | `RemovePartyMember` | `0x00541C00` | `[edx+0x8FC]` store — matches `minigames.md`'s independent `575`/`577` claim exactly |

A throwaway console tool (`ncsdump2`, referencing `KotorCoop.Memory` directly, not committed —
scratchpad only) walks every `.ncs` in a module's `.rim`/`_s.rim` pair through
`NcsReader.Disassemble` + `FindActionCalls`, against this expanded table plus the pre-existing
one. Every script name, offset, routine and preceding string constant quoted below came out of
that tool reading the real GOG install; nothing here was typed from memory of the game.

Two more routines matter throughout and are **not** independently confirmed: `#200` and `#229`,
both always paired with a string constant that reads as an object/waypoint tag
(`"sta_bastila"`, `"k45_door_malak"`, `"STA_JUMP0_WP"`...). **INFERRED** from the call shape
(a tag-lookup idiom used identically everywhere) to be `GetObjectByTag`/`GetWaypointByTag`-family
calls; not resolved to a specific id, and not needed to be — what matters below is which tag
each script touches and in what order, not the numeric routine.

---

## 2. The module map

### 2.1 Lehon

```
unk_m41aa ──beach/temple approach──▶ unk_m41ac ──▶ unk_m42aa ──▶ unk_m43aa ──▶ unk_m44aa/ab/ac
    │ (Rakata gate fight)                                          (the temple — the trials)
    │
    └─(when the party leaves)──▶ STUNT_19 (vision) ──▶ ebo_m41aa (Ebon Hawk on Lehon)
                                                              │
                                                              └─▶ sta_m45aa (Star Forge arrival)
```

Every arrow is a `StartNewModule` call read directly out of the scripts (routine 509,
`CSWVirtualMachineCommands::ExecuteCommandStartNewModule 0x00544390`), e.g.
`k_ptar_entertemp.ncs`: `StartNewModule("","","","","","","unk44_sw44aa01","unk_m44aa")`;
`k_punk_blrak01.ncs`: `StartNewModule(...,"unk43_sw43aa08","unk_m43aa")`;
`k_punk_goebon.ncs`: `StartNewModule(...,"K_EBN_RAMP_ENTRANCE","ebo_m41aa")`. **VERIFIED.**

`STUNT_19`'s own `k_scene_start.ncs` opens with `SetPartyLeader` then
`ActionStartConversation` against dialogue `"19"`, and its lone gameplay script
(`k_punk_jumptpl.ncs`) is nothing but a `StartNewModule` back into `unk_m44aa` — a pure cutscene
room, structurally identical to `STUNT_00` in `cinematics.md` §4.2. **VERIFIED.**

`ebo_m41aa`'s own exit script (found while walking the Ebon Hawk archive) fires
`StartNewModule(...,"", "sta_45aa")` — note the missing `m`. **INFERRED**: either a genuine
retail typo in the constant pool (this game has several — `cinematics.md` §4.4 catalogues six
dead movie names the shipped scripts reference and no file backs) or the resref resolver
silently normalises it; either way `AddMovieToExoArrayList`'s and `StartNewModule`'s own
"refuse a name/module it cannot use" behaviour (`cinematics.md` §1.5) means this is not a
co-op-relevant risk on its own — noted so nobody chases the string as a real second module.

### 2.2 Star Forge

```
sta_m45aa ─────────────────────────────────────────────────────────────▶ sta_m45ad ("sta45a_sta45d")
(docking bay; Bastila/Carth        sta_m45ab                                    ▲
 companion content)             (droid-horde floor,                            │
      │                          16 turrets)                                   │ "sta45d_sta45c"
      │                                                                        │
      └──────────────────────────▶ sta_m45ac ──────k45_door_malak────────────▶┘
                                (shield generators;
                                 Bastila's chamber,
                                 k45_door_bast1/2 —
                                 party stripped HERE)
```

Door tags (`sta45a_sta45d`, `sta45c_sta45d`, `sta45d_sta45c`, `k45_door_malak`,
`k45_door_bast1`/`2`) read directly out of the scripts that reference them; the exact walkable
topology (which are elevators vs. corridor doors) was not traced GIT-by-GIT — **INFERRED** from
the tag names and script co-location, not walked in-engine. Good enough to place every forced
moment correctly; not guaranteed to be the literal level layout.

---

## 3. Every forced solo/party-split moment

### 3.1 The temple summit (Lehon, `unk_m43aa`/`unk_m44aa`) — full-party GATHER, not a split

`k_punk_rakspk_en.ncs` (the Rakatan spirit's speech): `SetGlobalFadeOut` → `SetPartyLeader`
(routine 13) → **four** `ActionJumpToObject` calls (routine 385, the alias) → `SetGlobalFadeIn`
→ `ActionStartConversation` (routine 204). **VERIFIED.** The same five-call skeleton
(`SetPartyLeader` + exactly four `ActionJumpToObject`s) repeats at nine separate points across
`unk_m41aa`/`ac`/`ad` and `sta_m45aa`/`ac`/`ad` — it is this game's standard idiom for "snap the
whole active party into a fixed formation before a scripted conversation," not a split. All four
jump targets are party-slot lookups (`GetPartyMemberByIndex`, routine 577, called immediately
before each jump in every instance read) — **the whole party is gathered, never thinned.**

**Co-op reading.** The co-op partner is not a party member (per the memory note: a phantom, not
an NPC slot), so `SetPartyLeader`/`ActionJumpToObject` never touches them directly. If they are
physically present in the module when this fires, they simply watch their partner's PC and the
NPCs snap into formation — no different from any other in-engine cutscene `cinematics.md` §3.3
already covers via `DialogueSpectateController` (same DLG, same entry node, both engines evaluate
it locally). **INFERRED, NEEDS-LIVE**: never exercised specifically at this trigger.

### 3.2 The Rakatan trials (Lehon, `unk_m41aa`/`unk_m43aa`) — ordinary combat, not solo-gated

The `k_punk_fight*`/`k_punk_rakatk*`/`k_punk_blrak*` scripts (the black/red Rakata and rancor
fights) show `CutsceneAttack` (routine 503) for the scripted opening beats and ordinary
`#200`-tag spawns for the rest, but **no `RemovePartyMember` call anywhere in this content** —
searched exhaustively across all nine Lehon modules' compiled scripts. **VERIFIED absence** (not
just "not found" — every `.ncs` in these archives was walked, not sampled).

**Co-op reading.** This is just combat in a normal module. `local_combat`, host-authoritative
enemy mirroring and `enemy-presence-sync.md` cover it exactly as they cover any other fight; a
phantom partner can be fully present and can fight. No gap specific to this content.

### 3.3 The Bastila duel (Star Forge, `sta_m45ac`) — the first true forced-solo point

`k_psta_door.ncs`, at the door tagged `k45_door_bast1`: **nine** consecutive `RemovePartyMember`
calls (routine 575), by tag lookup against `"Bastila"`, `"Carth"`, `"Jolee"`, `"Cand"`, `"HK47"`,
`"Juhani"`, `"T3M4"`, `"Zaalbar"`, `"Mission"` — every possible companion, unconditionally.
**VERIFIED**, and it is a **same-module door**, not a `StartNewModule` — no travel/follow
mechanism fires at all; the game's own party roster is simply emptied while the player stays in
`sta_m45ac`.

What follows: `k_psta_cutscene1.ncs` branches on `Sta_BastConversion`/`G_FinalChoice` into
`"sta45c_darkscene"` or `"sta45c_lghtscene"` dialogue (routine 204,
`ActionStartConversation`, with `SetLockOrientationInDialog` × 3 pinning facing first — routine
505). If it goes to a fight, `k_psta_bastatk.ncs` locks orientation again and issues
`CutsceneAttack` (routine 503) against `"sta_bastila"`. The outcome sets `STA_BASTILA_DEAD`
(boolean) or leaves Bastila alive and re-added later — **no `AddPartyMember` call was found in
this content**, consistent with (but not proof of) vanilla KOTOR1's well-known behaviour that the
party stays empty from here through Malak, restored only after the Ebon Hawk escapes.
**INFERRED** on the restoration point specifically — general game knowledge, not a disassembled
call.

**Co-op reading — the open question.** `RemovePartyMember` cannot touch the co-op partner's
phantom (it is not a party member), and this is a same-module event with **no travel command at
all** — so if the partner is physically standing in `sta_m45ac` when the door opens, nothing in
this mod's machinery moves, hides, or parks them. They would simply watch — or, since nothing
gates `local_combat` here either, potentially **join the scripted 1-on-1 Bastila fight as a
second real combatant**, which the game's own design never contemplated. This is not something
any existing mechanism decides one way or the other; see §6 gap #1.

### 3.4 The Star Forge production-floor waves (`sta_m45ab`) — full party, not solo

`k_sta_spawnstart.ncs`/`k_sta_spawnreset.ncs`/`k_psta_spawnhb.ncs` drive an endless
droid-horde spawner keyed on `K_STA_SPAWNER` (bool) and `K_STA_HORDE` (a running count),
gated behind `k_psta_turretoff.ncs` disabling **sixteen** turrets (`sta_turret1`..`16`, all by
tag, routine `#200`) at a hacking terminal (`"K_COMPUTER_SPIKE"`). `k_psta_destall.ncs` adds a
`"SithMaster"` mini-boss whose death triggers four more spawners (`sta_spawner_1`..`4`).
**VERIFIED. No `RemovePartyMember` anywhere in this module** — the whole party (and, if present,
the co-op partner) stays together through this fight.

**Co-op reading.** Ordinary sustained combat against waves — the same mechanisms as §3.2 cover
it. The one thing worth flagging: this is the single most sustained, highest-enemy-count fight
in the game, and the Tier-2 structural limit already logged in `roadmap-seamless.md`
(`local_combat` is a **single slot** — `_locallyFightingId` — so a client engaged with a second
threat only mirrors it) will bite hardest here, where a horde spawner can trivially produce more
simultaneous threats than one client slot can locally fight. Not a new gap; the existing,
documented limit, just worth calling out as the place a player will most notice it.

### 3.5 The final duel with Malak — solo, script-driven, and the drain mechanic

Once `sta_m45ac`'s shield generators are destroyed (`STA_GENERATORS` counted down in
`k_psta_genassdth.ncs`/`genmk2dth.ncs`, opening `k45_door_malak` from `k_psta_gendeath.ncs`),
the player crosses into `sta_m45ad` **still solo from §3.3's `RemovePartyMember`** — this is a
continuation of the same forced-solo state, not a second one.

**The eight captive Jedi and the drain, read straight from the bytecode:**

* `k_psta_areaenter.ncs` (`sta_m45ad`'s own `OnEnter`): `SetGlobalNumber("K_END_JEDI_LEFT", ...)`
  on arrival — initialising the counter. **VERIFIED**; the numeric value pushed was not captured
  by this tool (it only surfaces preceding *string* constants), but eight captive objects exist
  (`sta_plc_captive1`..`8`, each with matching `sta_way_captive1`..`8` waypoints and
  `k_psta_cutjump2`..`8`/`cutwalk2`..`8`/`cutdrain2`..`8` per-captive cutscene scripts), so
  **INFERRED** the initial value is 8.
* `k_psta_jedieff.ncs`: `#229`-tag lookups against `"bp_malak_target"` and `"bp_jedi_target"` —
  the two ends of a beam effect between Malak and whichever captive is being drained.
  **VERIFIED** the tags exist and are read together in one script; the beam itself is a VFX
  detail this pass did not trace further.
* The Malak AI scripts (`k_psta_castat.ncs`, `k_psta_malcast.ncs`/`malcast2.ncs`,
  `k_psta_malakfght.ncs`, `k_psta_jediend.ncs`/`jediend2.ncs`, `k_psta_malakhost.ncs` — several
  of these are ~74 KB, the largest compiled scripts in the game) all read
  `K_END_JEDI_LEFT`/`K_END_MALAK_JEDI_USED` and several (`k_psta_castat.ncs`, `k_psta_malcast.ncs`)
  **write** `K_END_JEDI_LEFT` down and `K_END_MALAK_JEDI_USED` up during the fight, alongside a
  `STA_MALAK_DRAIN` boolean toggled around the drain beat and a `STA_MALAK_JEDI` running number
  (`k_psta_malakdrn.ncs`: `SetGlobalNumber("STA_MALAK_JEDI", ...)`) that tracks how much power
  Malak has absorbed. **VERIFIED**: this is a live, heartbeat-driven mechanic — Malak's own AI
  script decides, every combat round while `K_END_JEDI_LEFT > 0`, whether to drain another
  captive, and the drain is what feeds his self-heal. The actual `ApplyEffectToObject`/heal call
  itself was not isolated to one instruction (buried inside the ~74 KB scripts, not worth fully
  unrolling for this pass) — **INFERRED** that a heal effect follows the drain, from the
  mechanic's well-known in-game behaviour (Malak's health bar visibly jumps) plus the beam-target
  tags found above; not read as a single verified `ApplyEffectToObject` call.
* On death: `STA_MALAK_DEAD` set, `k_psta_malakend.ncs` opens `"k45_door_end"` and fades out.
  `k_psta_endjump.ncs` handles the two terminal states by tag: `"sta_pc_dead"` /
  `"sta_malak_dead"`.

**Confirming the mechanism the task asked about — does the host-authoritative HP mirror carry a
script-driven self-heal?** Yes, generically: `peer-damage-ledger.md` states and live-tests this
exact case in general form — *"an owner-side heal of 5 heals our copy by 5 instead of
overwriting"* and *"Owner-side healing propagates. Force-heal or stim an enemy on the owner and
confirm..."* (§9, run and passing). Malak is an ordinary spawned creature; whichever engine owns
him for combat purposes runs his real AI script (including the drain-heal), and the generic
health-mirror rule — lower NEVER overwrites a higher value the ledger has already paid for,
higher always propagates — is exactly the rule that would carry a mid-fight self-heal to the
other screen. **VERIFIED as a mechanism** (this is precisely what `peer-damage-ledger.md` §9
tested and shipped); **NEEDS-LIVE** specifically for this script, because nobody has run the
actual Malak fight through a live two-instance session — the generic claim has never been
exercised against this particular ~74 KB AI package, which is far larger and more branchy than
anything `peer-damage-ledger.md`'s own test used.

**Can the second player be physically present?** `sta_m45ad` is a completely ordinary module (not
`STUNT_*`), reached through a normal door (`k45_door_malak`), not a `StartNewModule` funnel — so
none of the stunt-module refusal machinery (`cinematics.md` §4.3) applies to keep the partner
out. If `follow_lead`/door `transition_announce` are doing their job (as `transition-smoothness.md`
describes and live-tested for ordinary doors), **the partner's phantom would follow through this
door exactly like any other**, ending up standing in the room for what the base game designed as
a strict 1-on-1 duel. Nothing in this mod currently treats "story says this is solo" as a signal
distinct from "story says this is a stunt cutscene." **This is the headline gap — see §6 #1.**

### 3.6 The ending cutscenes and movies

`STUNT_42`/`STUNT_44` fire mid-Star-Forge (`k_psta_carthend.ncs`/`carthlove.ncs`/`bastfade.ncs`
each call `StartNewModule` into one of them, movies `"50"`/`"54"`/`"56"`) as one-off vignette
rooms — Carth's or Bastila's romance/loyalty payoff scene, each a single `k_scene_start.ncs`
(`SetPartyLeader` → `ActionStartConversation`), structurally identical to `STUNT_00`.

The credits chain, read tag-by-tag: `STUNT_50a` (movie `50`; can loop back into `sta_m45ac` via
its own `k_pstu_startmod.ncs`) → `STUNT_51a` (Bastila-walks-with-PC scene, movies `51`/`54`,
gated on `STA_BASTILA_STUNT`) → `STUNT_54a` (movie `54`, chains to `STUNT_56a` or `STUNT_55a`)
→ `STUNT_56a` (movie `56`, dialogue `m01aa_c05`) → **`STUNT_57`**, the true finale: one
`k_scene_start.ncs` spawns every surviving companion by tag at `WP01`..`WP09` —
`Bastila` (unless `STA_BASTILA_DEAD`), `HK47`, `Juhani` (unless `KAS_FREYYRDEAD` — read here,
oddly, alongside `KAS_CHUUNDARDEAD`, both Kashyyyk-planet globals reused for a Lehon-beach
reunion check), `Zaalbar`, `Carth`, `Canderous`, `Jolee`, `T3M4`, `Mission` — then runs dialogue
`m41ad_c01` (the beach reunion) and sets `CREDITPLAY`. **VERIFIED**, all of it read directly.

`STUNT_55a`'s `k_stunt_end.ncs` is the one `cinematics.md` §4.4 already flagged as **the only
place in the whole game that contemplates players skipping a movie independently** —
`QueueMovie("55")` + `PlayMovieQueue` with `bAllowSeparateSkips = 1`. That earlier finding and
this pass's finding are the same script; `cinematics.md`'s skip design (§3.2 point 3 — never
mirror a skip, never wait, let arrival be the rendezvous) already covers it by design, and this
is the one movie in the credits chain where the base game itself already expects two people
could watch it for different lengths.

**Co-op reading.** Every `STUNT_*` room here is refused by `CoopAgent`'s existing stunt-module
follow gate, exactly like `STUNT_00` — **INFERRED by structural identity**, **NEEDS-LIVE**: never
run end to end, and this chain is far longer (up to six consecutive stunt hops depending on path)
than anything previously tested (two hops, `end_m01ab`→`STUNT_00`→`tar_m02af`). The accumulate-
and-carry-the-movie-queue design in `cinematics.md` §3.2 point 2 should, in principle, carry all
of `50`/`51`/`54`/`56`/`56b` through to wherever the follower's one eventual warp lands — but
that design has only ever been exercised for a two-movie accumulation, not five or six.

---

## 4. The light/dark ending branch — can two players' choices conflict?

`G_FinalChoice` is written from exactly two places: `k_psta_lightside.ncs`/`k_psta_darkside.ncs`
on Star Forge, and `k_punk_end.ncs`/`k_punk_end2.ncs`/`k_punk_evilpc.ncs`/`k_punk_goodpc.ncs` on
Lehon (read-back checks in many more places). All of the *write* sites are single-reply dialogue
scripts — the payoff of one specific conversation node, not a raw player action.

**Why no genuine write-write conflict is architecturally possible, by the mod's own existing
rules:**

1. **Dialogue reply authority is single-owner by design** — `roadmap-seamless.md` Tier 2 #15:
   "Only the token owner picks dialogue replies... by design." Two players cannot independently
   pick different replies in the same conversation instance; only whoever holds the token can
   advance it.
2. **The moment this fires is already solo** (§3.3/§3.5) — the party is emptied and, per §3.5,
   it is an open question whether the partner is even in the room. If they are, `dialogue-sync.md`
   §3 design (already implemented per `cinematics.md` §3.3: same DLG, same entry node, both
   engines evaluate locally) means a spectating partner sees the *same* node the owner picked —
   they do not independently execute a different one.
3. **`quest-sync.md`'s Lamport-clock merge is generic and per-key**, with no evidence of a
   `G_FinalChoice`-specific carve-out — a normal `SetGlobalNumber` write from one engine
   propagates to the other exactly like `K_END_JEDI_LEFT` or any quest flag. **VERIFIED**
   (the mechanism, from `quest-sync.md`'s own "Implemented, live-verified, ON by default"
   status) generically; **NEEDS-LIVE** for `G_FinalChoice` specifically — never exercised.

So the honest answer is: **no conflict is structurally reachable given the existing token-owner
and stunt-module-refusal rules, provided §3.5's open question resolves toward "the partner
cannot independently act in this content."** If it resolves the other way — if the partner CAN
walk into `sta_m45ac`/`sta_m45ad` as a fully independent actor with no dialogue token and no
combat restriction — the risk is not a global-merge conflict (still structurally prevented by
rule 1) but a narrative one: two players watching the same forced ending dialogue from two
different physical vantage points, one of them a bystander in a scene the base game wrote for
exactly one witness. **INFERRED**, not measured.

---

## 5. Coverage summary

| Moment | Solo-gated? | Forcing script(s)/routine | Partner physically present? | Existing mechanism | Status |
| --- | --- | --- | --- | --- | --- |
| Temple summit (Lehon) | No — gathers party | `k_punk_rakspk_en.ncs`: `SetPartyLeader`(13) + 4×`ActionJumpToObject`(385) + `ActionStartConversation`(204) | Yes, ordinary module | Dialogue spectate (`cinematics.md` §3.3) | Covered, NEEDS-LIVE |
| Rakatan trials (Lehon) | No | `k_punk_fight*.ncs`: `CutsceneAttack`(503) + ordinary spawns | Yes | `local_combat`, enemy mirroring | Covered, no new work |
| Bastila duel (`sta_m45ac`) | **Yes** | `k_psta_door.ncs`: 9×`RemovePartyMember`(575); `k_psta_bastatk.ncs`: `SetLockOrientationInDialog`(505)+`CutsceneAttack`(503) | **Undecided** — no travel event fires, nothing parks them | None specific to "story-solo, not a stunt module" | **Gap — see §6 #1** |
| Star Forge production floor (`sta_m45ab`) | No | `k_sta_spawnstart.ncs`/`k_psta_spawnhb.ncs`: horde spawner; `k_psta_turretoff.ncs`: 16 turrets | Yes | `local_combat` one-slot limit (pre-existing, documented) | Covered, will be felt hardest here |
| Malak duel, drain mechanic (`sta_m45ad`) | **Yes** (continuation of the Bastila-duel solo state) | `k_psta_castat.ncs`/`malcast.ncs` etc.: `K_END_JEDI_LEFT`/`K_END_MALAK_JEDI_USED`/`STA_MALAK_DRAIN`/`STA_MALAK_JEDI` read+write | **Undecided**, same as above; door not stunt-gated | Host-authoritative HP mirror (generic, VERIFIED mechanism) | **Gap — see §6 #1/#2** |
| Ending vignettes (`STUNT_42`/`44`) | Yes (one-off room) | `k_scene_start.ncs`: `SetPartyLeader`+`ActionStartConversation` | No — stunt-module refusal | `cinematics.md` §4.3 stunt gate + movie-queue accumulation | Covered, NEEDS-LIVE |
| Credits chain (`STUNT_50a`..`57`) | Yes | `StartNewModule` chain, movies `50`/`51`/`54`/`56`/`56b`; `STUNT_55a` uses `bAllowSeparateSkips` | No — stunt-module refusal | Same as above, but a 5-6 hop accumulation never tested | **Gap — see §6 #3** |
| `G_FinalChoice` light/dark write | N/A | `k_psta_lightside.ncs`/`darkside.ncs` etc. | Depends on the above | Token-owner dialogue rule + `quest-sync.md` Lamport merge | Structurally safe, NEEDS-LIVE |

---

## 6. Gaps, ranked

1. **[LARGE, needs the rig, and a product decision first]** Whether a second real player can
   walk into `sta_m45ac`'s Bastila chamber or `sta_m45ad`'s Malak chamber is currently
   **undecided by this mod's own rules**, not merely untested. Neither of those doors is a
   `StartNewModule` funnel (so the stunt-module refusal never engages) and neither is a
   registered minigame (so `MinigameController`'s park/hide never engages either) — the only two
   existing "keep the partner out of story-solo content" mechanisms both structurally do not
   apply here. Before any live test is useful, someone has to decide: should this mod treat
   "the whole party was just forcibly emptied by script" as its own signal to park the partner
   (a third mechanism, not yet built), or is a spectating/participating second player in the
   Malak duel an acceptable co-op experience? Blocks gap #2 from being meaningfully tested.
2. **[MEDIUM, needs the rig]** `peer-damage-ledger.md`'s owner-side-heal-propagates rule is a
   VERIFIED, live-tested *mechanism*, but has never been exercised against
   `k_psta_castat.ncs`/`k_psta_malcast.ncs` specifically — the largest, most heavily-branched
   scripts in the game. A ~74 KB heartbeat AI package with a self-heal buried in it is a
   plausible place for an edge case (a heal applied via a code path this project's disassembler
   pass did not fully unroll) that a smaller synthetic test would never surface.
3. **[MEDIUM, needs the rig]** The credits chain is 5-6 consecutive `STUNT_*` hops
   (`STUNT_50a`→`51a`→`54a`→`56a`→`57`, with `STUNT_50a` able to loop back once). The
   accumulate-the-movie-queue design (`cinematics.md` §3.2 point 2) has only ever been tested
   for a two-hop chain (`STUNT_00`, the Taris vision). Whether the accumulation, the eventual
   convergence warp, and `follow_lead` all still hold at 5-6 hops is unverified.
4. **[SMALL]** `G_FinalChoice`, `STA_BASTILA_DEAD`, `K_END_JEDI_LEFT`,
   `K_END_MALAK_JEDI_USED`, `STA_MALAK_DRAIN`, `STA_MALAK_JEDI`, `STA_MALAK_TALK` are all
   ordinary NWScript globals and should already ride `quest-sync.md`'s generic merge with no
   special-casing needed — low risk, but literally never exercised for these specific keys.
   Cheap to check once the rig is already up for gap #2/#3.
5. **[SMALL]** The double-firing question `cinematics.md` §3.3 left open generally (a
   script-started conversation firing independently on both engines) has concrete new instances
   here — `k_psta_cutscene1.ncs`'s `sta45c_darkscene`/`sta45c_lghtscene` branch and the Lehon
   `k_punk_rakspk_en.ncs` speech are both `OnEnter`/heartbeat-triggered, not click-triggered.
   Same open question, no new mechanism proposed; noted so a future pass at §3.3 tests these
   triggers specifically.
6. **[SMALL, no rig needed]** The `"sta_45aa"` vs `"sta_m45aa"` string mismatch in `ebo_m41aa`'s
   exit script (§2.2) is worth a five-minute look at whether it is a genuine dead reference (like
   `cinematics.md` §4.4's six dead movie names) or resolves correctly — purely a documentation
   completeness item, not believed to affect co-op behaviour either way.

---

## 7. What this pass did not do

Did not walk the Bastila/Malak fight's `.dlg` files node-by-node (only script-level `.ncs`
evidence is cited above). Did not trace the exact `ApplyEffectToObject` instruction inside the
~74 KB Malak AI scripts that performs the self-heal — only the surrounding
read/write-global evidence that the heal is conditioned on the drain state. Did not resolve
routines `#200`/`#229`/`#548`/`#582` to names (their call *shape* was enough for this pass's
questions). Did not open a live game or the rig at any point.

---

## 8. Design shipped (2026-08-27): `solo_segment_spectate`

Gap #1 (§6) forced the product decision this doc's task named up front: **the OWNER's fight;
the other human SPECTATES, they do not enter the 1-on-1.** Built as `solo_segment_spectate`
(agent- and plugin-side coop.ini key, default OFF, NEEDS-LIVE — no rig access for this pass
either, same as the research above).

### 8.1 Detection

A story-forced solo segment begins when the local companion roster (`CSWPartyTable`'s own
count, via `Odyssey.TryReadPartyRoster` — see `party-reconciliation.md`) drops from one or two
companions to zero, IN one of `SoloSegmentCatalogue`'s modules, via a real drop edge rather than
a level read — which is what makes it distinguishable from three false-positive shapes an
earlier draft of this brief flagged by name:

* **The player never having recruited anyone yet** (the Endar Spire, companion count 0
  throughout) — excluded because the very FIRST observation a fresh `SoloSegmentMachine` makes
  can never itself read as a drop; only a transition FROM &gt;=1 companions DOWN TO 0 counts.
* **A death** — excluded by requiring every companion who just left the roster to have been
  alive (current hit points &gt; 0) the last time `SoloSegmentController` actually observed them
  present. In practice this is close to vacuous for KOTOR1 (party members go temp-dead, not
  dead, and `RemovePartyMember` is not how a down is represented), but the check costs nothing
  and the brief asked for it explicitly.
* **The party-select screen** — excluded by reading `CGuiInGame+0x78`
  (`CSWGuiPartySelection*`, the same offset `galaxy-map-and-party-select.md` §2 documents, and
  the SAME one `Odyssey.Dialogue.Follow.cs`'s own class comment records having once been
  confused with the dialogue panel at `+0x3C` — this feature's own detection reuses the offset
  correctly, not the mistake). A drop observed while that panel is open never begins a segment.
  In practice this defence is mostly belt-and-braces here too: `galaxy-map-and-party-select.md`
  §2's own finding is that the party-select screen's only confirmed shipped trigger is the Taris
  swoop-taxi system, nowhere near the Star Forge, so the module gate (only `sta_m45ac`/
  `sta_m45ad` qualify) already excludes ordinary party-select usage almost entirely on its own.

The catalogue is exactly the two modules §3.3/§3.5 verified by reading the real compiled
scripts — `sta_m45ac` (the shield-generator floor and Bastila's chamber; `k_psta_door.ncs`'s
nine `RemovePartyMember` calls fire at the door tagged `k45_door_bast1`) and `sta_m45ad`
(Malak's chamber — not a second solo-gating event, a *continuation* of the same forced-solo
state carried across the ordinary `k45_door_malak` door crossing, per §3.5). **Not** `sta_m45ab`
(the droid-horde production floor, §3.4 — full party, no `RemovePartyMember` anywhere in it): an
earlier draft of this feature's own brief named that module by mistake, and the shipped
`SoloSegmentCatalogue` uses this doc's own verified modules instead. Extendable at runtime via
coop.ini `solo_segment_modules` for a future mod's own forced-solo content.

A segment ends when the roster refills (a companion count of 1+ again — the vanilla behaviour
after Malak dies, per §3.3's own INFERRED note that the party stays empty from the Bastila door
through Malak, restored only after the escape) or the module changes.

### 8.2 The wire and the two reactions

The owner's instance publishes `PlayerState.FlagSoloSegment` (bit 128 — a new wire bit on the
existing `Flags` dword, not a `ProtocolConstants.Version` bump by itself) for the whole duration.
Two DELIBERATELY ASYMMETRIC reactions follow from it — asymmetric because, unlike
`FlagInMinigame`/`FlagAway`, the flag-bearer must stay VISIBLE (the spectator's camera needs a
live body to follow):

* **The owner** (driven by its OWN local flag, needing nothing from any peer): hides every
  remote peer from what it writes locally — `SoloSegmentRule.ShouldHideAllPeers`, applied in
  `CoopAgent.PumpOnce`'s peer-visibility filter, the same "simply absent from what gets written"
  idiom the minigame/away filters already use — so the duel script never sees a second body.
* **The spectator** (driven by a REMOTE peer's flag, gated on being in the same module):
  `SoloSegmentSpectateController` parks input, points the camera at the owner's phantom (reusing
  `Odyssey.SetCameraFollowServerObject`/`RestoreCameraToControlledCreature`, the exact engine
  funnel `SpectateController`'s down-spectate camera already uses and verified live for —
  `spectate-camera.md`), and stops crediting this player's own damage against the owner's enemy
  mirror (a `PeerDamageController` gate, on top of the input lock, not instead of it). A HUD
  line ("`<name>` is fighting alone - spectating", plugin-side `PostTextLine` row 8/10) and a
  chat toast (`SoloSegmentAnnouncer`, agent-side, the same diff-per-tick shape
  `MinigameAnnouncer`/`AwayAnnouncer` already use) both fire on the entering edge.

⚠ **The input lock is the one genuinely unverified lever, and the live pass should treat
confirming it as the headline question.** Two levers, stacked: `creature+0xE8`
(`Odyssey.ObjCommandable`) set to 0, and `Odyssey.HoldCreatureStill` reasserted every pass (the
identical mechanism `InteractionLockController` already uses to hold an NPC someone is talking
to still). Neither has been watched stop a real WASD press or a real mouse-click attack — see
`SoloSegmentSpectateController`'s own doc comment for the full reasoning, including
`party-reconciliation.md`'s own "trap 5" finding that `commandable` alone does NOT stop a queued
AI follow action (a different code path from the local player's own real-time input, but the
closest existing measurement this project has).

### 8.3 Live test plan (NOT yet run — no rig access for this pass)

Two `tools/demo.ps1` (or manual) instances, both on a save that has reached the Star Forge, OR —
much faster — a save staged directly at the door with `warp`, the retail console cheat this
mod's own co-op layer already shadows to go through the session properly (`console-cheats.md`
§2):

1. **Reach the module.** `warp sta_m45ac` on the HOST (the owner of the fight — pick whichever
   player will hold the token). Both instances should land together the normal way
   `TravelController`'s warp already keeps two players in sync.
2. **Trigger the strip for real, not synthetically.** `opendoor k45_door_bast1` (the existing
   `CheatKindOpenDoor` staging verb — "open an ORDINARY door... the engine's own door-use
   action", `console-cheats.md` §3) on the HOST, so the real `k_psta_door.ncs` OnOpen script
   runs and fires its nine genuine `RemovePartyMember` calls, rather than reproducing the effect
   by hand. If `opendoor`'s OnOpen path does not reach this door's script for some reason not
   yet understood, the fallback is `firescript <id> fire` (`console-cheats.md` §3) against the
   door's own resolved object id — untested for a DOOR specifically (every existing `firescript`
   use in this codebase's own history was against a CREATURE's `+0x260`/`+0x270` slot, per
   `Odyssey.cs`'s own `FireCreatureScriptSlot0x260` naming; whether a door's OnOpen lives at the
   same offset is itself unconfirmed).
3. **Watch the owner's own log** for `solo segment: began in sta_m45ac — this fight is yours...`
   and confirm `block.LocalInSoloSegment`/the published `PlayerState.FlagSoloSegment` bit both
   flip (a `blockpoke`-style read, or just trusting the log line — the wire round-trip itself is
   already codec-pinned by `PacketCodecTests.SoloSegmentFlagRoundTripsIndependentlyOfTheOther
   PresenceFlags`).
4. **On the SPECTATOR's screen**, confirm in order: the chat toast ("`<owner>` is fighting alone
   - spectating"), the HUD notice line, the camera snapping to the owner's phantom, and — the
   headline unverified item — that WASD/mouse input genuinely does nothing to the spectator's
   own PC for the duration. Try an attack too, and confirm (via the owner's own HP bar, or the
   plugin's damage-dealt log) that nothing the spectator does registers against Malak/the
   Bastila-duel target even if some input DOES leak through.
5. **Peer-visibility check.** On the OWNER's screen, confirm the spectator's phantom is genuinely
   gone (not merely invisible) — no `coop_phantom`-tagged body standing in `sta_m45ac`, the same
   `creatures.ps1`-style check `client-control-lock.md`'s own teardown story used.
6. **End the segment two ways**: let the fight resolve normally (Bastila dies or is spared,
   `STA_BASTILA_DEAD`/`STA_MALAK_DEAD` per §3.3/§3.5) and confirm the flag clears and control/
   camera hand back cleanly; separately, cross into `sta_m45ad` via `k45_door_malak` while still
   roster-empty and confirm the segment stays ACTIVE across the door (§3.5's "continuation", not
   a fresh `Began` — the log should show no second `began in sta_m45ad` line, only the
   already-active state persisting).
7. **Regression**: with `solo_segment_spectate = 0` (the shipped default), repeat step 2 and
   confirm nothing in this whole feature engages — the spectator's phantom stays mirrored
   exactly as before, matching §3.3's own "the open question" description of the UNMODIFIED
   behaviour this feature replaces.

Watch for in the plugin log: `solo segment: began in ...`/`solo segment: ended ...` (owner side),
`solo segment: spectating 0x... — input parked, camera follows` / `solo segment: ... — control is
yours again` (spectator side) — and, on the owner's agent log, nothing new naming the peer at
all, which is itself the evidence the hide filter worked (an absent peer produces no line, the
same "removed" ambiguity `client-control-lock.md`'s own spawn-drain story flags as worth a
positive "nothing left standing" report rather than silence — here the positive signal is the
`RemoteCount`/peer-slot readout, not a dedicated log line).

## Live pass (2026-08-28): the door opened for real, the roster never wiped — and §3.3's own
## citation for the trigger is corrected, one level short of the actual answer

Two-instance rig, `solo_segment_spectate=1` on both `coop.ini`s, both `warp sta_m45ac` (landed
together, confirmed via `where`). `k45_door_bast1` sits ~112m from the module's warp-in point —
too far for `opendoor`'s own 40m range gate — so this pass read the door's real coordinates
directly out of the module's own GIT data (`tools/kotorfiles.py gff
"...\modules\sta_m45ac.rim:m45ac.git" --path "Door List"`, no live game touched for this part):
`k45_door_bast1` at `(-15.97, -82.24, 12.16)`, a different Z than the warp-in point — a vertical
section this pass did not otherwise map. Both players `tp`'d there directly (bypassing the
unmapped walk); `opendoor k45_door_bast1` on the host succeeded through the real engine call
(`door 0x3E: opened (state 0 -> 2)`, `Odyssey.OpenDoorFully`/`CSWSDoor::OpenDoor 0x00589C70` —
per that function's own doc comment in `Odyssey.Doors.cs`, this is NOT a state poke; it resolves
the opener, picks a swing side, and **runs the door's real OnOpen script** through
`CVirtualMachine::RunScript`, exactly the "genuine engine call" §8.3 step 2 asks for).

**No `solo segment: began` line ever appeared, on either side, and the party roster never
changed** — the host's own party-follow diagnostics (`party follow: 0x105 ... inParty=1
...leaderPos=(-14.0,-83.0,12.1)`) show Trask still an active, in-party companion both before and
after the door opened. `sTA_BASTILA_DEAD`/`G_FinalChoice` were not read this pass, but the
absence of any roster-count drop is unambiguous from the follow-diagnostics alone.

### Root cause, read directly from the module's own template data (not re-derived from bytecode)

`k45_door_bast1.utd`'s own `OnOpen` field (`tools/kotorfiles.py gff
"...\sta_m45ac_s.rim:k45_door_bast1.utd"`) is **`k_psta_bast_wor`**, not `k_psta_door` — a
**correction to §3.3's own citation**, which named `k_psta_door.ncs` as "at the door tagged
`k45_door_bast1`" from a blind bytecode sweep for `RemovePartyMember` calls, associated to the
door by NAME similarity, not by tracing the actual GFF wiring (§3.3's own method section is
explicit that the `.dlg` files were never walked node-by-node — this pass closes exactly that
gap, one level further than §3.3 reached). `k_psta_bast_wor.ncs`'s own string constants
(`sta_bastila`, `sta_invis_bast_conv`, `k45_door_bast2`, `Bastila`) read as scene setup — moving
an invisible conversation trigger into place — not a party-roster operation; no companion tag
appears in it.

**A full sweep of every script and template resource in `sta_m45ac_s.rim`** (193 `.ncs`, 104
`On*`-bearing templates) for a literal reference to `k_psta_door` found exactly two hits, both
false leads (`k45_door_malak`/`sta_door_freeze`'s `OnFailToOpen = k_psta_doorbark`, a
similarly-named but different, far smaller script) — **`k_psta_door.ncs` is not wired as an
`On*` field on any door, placeable, creature or trigger template in this module.** It IS
referenced, though: a sweep of the module's 18 `.dlg` files for the literal string found it in
**`k_sta_bastila.dlg`** (the Bastila conversation itself) — i.e. `k_psta_door.ncs` fires as a
dialogue NODE's own action script somewhere inside the Bastila conversation, not from the door
opening at all. This matches the vanilla design better than the door-OnOpen reading did: the
duel is gated behind actually TALKING to Bastila after the door opens, not the door swing itself
— `k45_door_bast1`'s `OnOpen` merely stages the scene (moving `k45_door_bast2` and the invisible
conversation trigger into position) for a conversation the player has not yet started.

**Not resolved this pass**: which node inside `k_sta_bastila.dlg` calls `k_psta_door.ncs`, or
whether it fires unconditionally the moment the conversation starts (matching the task's own
"unconditionally, every possible companion" reading of the 9x `RemovePartyMember` calls) or only
on a specific branch. This project has no `.dlg`-node-to-script GFF walker built yet (the byte
offset of each node's own `Script`/`Script2` fields inside `DialogList`/`EntryList`/`ReplyList`
was not traced this pass) — the string-literal sweep above proves the REFERENCE exists, not
which node holds it or under what condition. Actually STARTING that conversation live needs
either a real click on Bastila's NPC body (blocked by the same mouse-injection gap
`docs/research/session-save.md` already documents) or a console primitive this project does not
yet have for forcing a specific NPC's specific `.dlg` to start (`pazaak`/`openstore` are the
precedent for "call the engine's real entry point directly instead of clicking," but no
`.dlg`-start equivalent exists yet).

### A second finding: `firescript` on a DOOR object is unsafe — do not use it there

Before finding the `.dlg` lead, this pass tried the task briefing's own documented fallback
(`console-cheats.md`'s "if `opendoor` doesn't reach the script, try `firescript <id> fire`") on
the door object id (`firescript 0x3E fire`). **This produced a tight, repeating burst of
first-chance access violations** (`0xC0000005` at a fixed address outside the game's own image,
`~12` per burst, lasting ~200ms) before self-resolving — the process survived (confirmed
`Responding=True` and normal logging resumed 7 seconds later), but this is now a **live-confirmed
negative result**, not the "untested for a DOOR specifically" open question `docs/CONSOLE.md`'s
own `firescript` entry names. Root cause, read from `Odyssey.Doors.cs`'s own doc comment on
`OpenDoorFn`: a `CSWSDoor`'s OnOpen script is invoked internally by `CSWSDoor::OpenDoor` itself,
through `CVirtualMachine::RunScript` directly — **there is no `+0x260`/`+0x270`-style script-slot
field on a door the way a creature has one**, so `firescript`'s generic offset read against a
door object dereferences whatever unrelated field actually lives there. **Do not use `firescript`
against a door id** — `opendoor`'s own real engine call already runs the OnOpen script correctly
(confirmed working, above); there is nothing for `firescript` to add for a door, and this pass's
own evidence is that trying costs a crash-loop for no benefit.

### Verdict: `solo_segment_spectate` stays OFF — the detection precondition itself was never
### reached, so none of the spectate/input-lock machinery this feature is actually about was
### exercised

The task's headline question (does the input lock actually stop a real WASD press or attack)
remains **exactly as unverified as §8.2's own design doc left it** — this pass never got as far
as the party roster actually dropping to zero, which is the trigger `SoloSegmentController`
watches for. What this pass adds is a corrected, one-level-deeper map of the real trigger chain
(door OnOpen stages the scene; the roster wipe fires from within the Bastila conversation, not
the door) and a live-confirmed hazard (`firescript` on a door) for the next round to avoid.
`solo_segment_spectate` stays at its default OFF; `docs/research/star-forge-finale.md`'s own §8
design is unchanged (the detection mechanism itself — a roster count dropping from ≥1 to 0 in
`sta_m45ac`/`sta_m45ad` — is still believed correct; it simply was never fed a real drop this
pass, and the code path that would drive it end to end was never reached).

**Concrete next step**: either build a `.dlg`-node script-walker (a natural extension of
`NcsReader`'s own resolved-action-table approach, this time reading `DialogList`/`ReplyList`
struct fields instead of bytecode) to find and confirm `k_psta_door.ncs`'s exact firing
condition inside `k_sta_bastila.dlg`, or find another way to start that conversation live (a
console `.dlg`-start primitive, or a save with genuine story progress that reaches this point
through ordinary play) so the actual `RemovePartyMember` sweep can be observed directly.

`dotnet build KotorCoop.slnx -warnaserror` and `src/KotorCoop.Plugin/build.ps1 -WarnAsError`:
both clean (no code changed this pass — static RE plus a live rig session only).

## Live pass (2026-08-28): a real trigger lead found and tried — `k45_init_bastila` — still no
## roster wipe; the search space is narrower but the conversation still was not reached

Two-instance rig, `solo_segment_spectate=1` on both `coop.ini`s, both `warp sta_m45ac` together
(confirmed via `where`). Before touching the game, this pass read the module's own data further
than the prior round did: `tools/kotorfiles.py gff "...\sta_m45ac.rim:m45ac.git" --path "Trigger
List"` lists all 12 triggers by template/position, and one of them is a genuinely new, specific
lead the prior round's blind `.ncs`/`.dlg` string sweep did not surface — **`k45_init_bastila`**,
a trigger at `(-8.08, -54.49, 12.53)` (`~28m` from the door, matching the "different Z, a vertical
section" gap the prior round flagged) whose own template (`k45_init_bastila.utt`)
names `ScriptOnEnter = k_psta_init_bast`. Extracted and string-scanned that script
(`tools/kotorfiles.py extract ...`): it references `sta_invis_bast_conv`, `Bastila`,
`k45_door_bast1` and `k45_door_bast2` by name — the same conversation-trigger tag the door's own
`k_psta_bast_wor.ncs` references, plus Bastila herself — a much more direct candidate for
"the thing that actually starts her conversation" than the door was ever likely to be. (The
`sta_invis_bast_conv`-tagged object itself is NOT a static GIT entry — none of the 12 placed
triggers or 94 placeables carries that tag — so it is created or repositioned by script at
runtime, consistent with the "moving an invisible conversation trigger into place" reading
`galaxy-map-and-party-select.md`'s prior round already gave `k_psta_bast_wor.ncs`.)

**Tried live, twice, both negative.** (1) `opendoor k45_door_bast1` first (`door 0x3E: opened
(state 0 -> 2)`, the same real engine call confirmed safe before), then `tp`'d just outside
`k45_init_bastila`'s own coordinates and `walkto -8.08 -54.49 12.53` — synthetic WALKED input,
not a teleport landing, specifically so the engine's own boundary-crossing OnEnter check (not
just "am I inside") gets a real chance to fire, the same reasoning `usedoor`/`useplaceable`
already document for why this project drives input rather than poking state. `where` confirmed
arrival exactly on the trigger's own coordinates. No `dialogue: engine reached StartDialog` line,
no `solo segment: began`, and `party roster`/`party follow` kept reporting Trask (`0x107`)
unchanged (`inParty=1`, `soloMode=0`) for the whole observation window. (2) Walked on to
`sta_way_bastila`, the GIT's own waypoint for Bastila's actual spawn point (`-32.77, -39.33,
12.39`, a separate room past the trigger) — same negative result, and `census` reported the same
`7` creatures both before and after (Bastila, if spawned at all here, does not read as hostile so
would not show up in this specific count either way — inconclusive on its own, not strong
evidence she never appeared). Both `swkotor.exe` processes and the client's own `party follow`
diagnostics stayed healthy throughout; no crash, no state corruption.

**What this narrows, and what it does not.** `k45_init_bastila`'s OnEnter is a strong, code-
verified (string-reference) candidate for the thing that stages `sta_invis_bast_conv` into its
final position, but entering it — even via real synthetic walking input, not a teleport — was not
enough on its own to start a conversation or move the roster within this pass's observation
window. Two readings, neither confirmed: (a) `k_psta_init_bast.ncs` only relocates/prepares
`sta_invis_bast_conv` without itself starting anything, and the ACTUAL start trigger is
`sta_invis_bast_conv` itself, now sitting at some position this pass never located (its own
post-relocation coordinates are not static GIT data — nothing to read without either tracing
`k_psta_init_bast.ncs`'s own bytecode for the `CreateObject`/`ActionMoveToLocation`-style call
that places it, or walking the room more exhaustively than this pass's two waypoint-guided
attempts did); or (b) the conversation genuinely needs a real click on Bastila's own NPC body
(which may not even be present/visible yet, matching hypothesis (a)) and no amount of walking
alone reaches it, matching this doc's own long-standing "needs a human for one click" class of
gap. **Not settled this pass.**

### Verdict: `solo_segment_spectate` stays OFF — same as every prior round, narrower evidence

The detection precondition (a roster drop from >=1 to 0) was still never fed a real edge, so the
input-lock/spectate machinery this feature is actually about remains exactly as unverified as
before. This pass adds one genuinely new, code-verified lead (`k45_init_bastila` /
`k_psta_init_bast.ncs`, distinct from and more targeted than the door itself) and two more
negative live data points (walked-input entry into that trigger, and into Bastila's own spawn
waypoint) without a positive result. `solo_segment_spectate` stays at its default OFF.
**Concrete next step, more specific than the prior round's**: disassemble
`k_psta_init_bast.ncs`'s own bytecode (not just its string table) to find exactly what it does
with `sta_invis_bast_conv` — where it places it, and whether that placement is itself the
trigger a player needs to walk through, or whether it does something else (e.g. spawning Bastila
directly) that this pass's two waypoint guesses did not intersect.

`pwsh src\KotorCoop.Plugin\build.ps1 -Install -WarnAsError` on both game directories (hash-
confirmed identical to the fresh build) and `dotnet build KotorCoop.slnx -warnaserror`: both
clean — no source changed this pass, static RE (`tools/kotorfiles.py`) plus a live rig session
only.
`dotnet test tests/KotorCoop.Tests --no-build`: 2339/2339 passing (unchanged).

## Live pass (2026-08-28): `startdialog` reaches `sta_invis_bast_conv` for the first time —

## `sta_invis_bast_conv` is a PLACEABLE, not a creature; the roster wipe still did not fire

Task: `solo_segment_spectate=1` on both, `warp sta_m45ac` both,
`startdialog sta_invis_bast_conv k_sta_bastila` on the host — now buildable, since
`dialogue-sync.md`'s own 2026-08-28 addendum shipped the `startdialog` verb after this doc's
prior rounds were written. Confirm the roster wipe fires, the client's phantom hides, input
locks, camera follows, toast fires; flip ON if clean.

**Not flipped ON — the detection precondition (a roster drop) still never fired**, but this pass
adds one genuinely new, load-bearing fact and reaches further into the conversation than any
prior round.

### `sta_invis_bast_conv` found and resolved directly — it is a PLACEABLE (object id `0xAA`),

### which is WHY `startdialog <tag>` could never find it

`startdialog sta_invis_bast_conv k_sta_bastila` refused every time with `no npc tagged
'sta_invis_bast_conv' found here` — expected, since `startdialog`'s own NPC resolution
(`CreatureFinder.FindByTag`, per `dialogue-sync.md`'s own addendum) only sweeps for the
`CSWSCreature` vtable. A one-line diagnostic settled what three prior rounds of this doc left
as an open question ("does `k_psta_init_bast.ncs` create/reposition this object at all"): after
`opendoor k45_door_bast1` + `walkto` (not `tp`, matching this doc's own prior finding that a
teleport skips the boundary-crossing OnEnter check) into `k45_init_bastila`'s own trigger
coordinates, `useplaceable sta_invis_bast_conv` answered `refused: placeable 0xAA
('sta_invis_bast_conv') is 32.6m away, too far to use` — **the object exists, is a placeable,
and its own object id is `0xAA`.** This closes the prior round's own hypothesis (a) vs (b)
question in favour of (a): `k_psta_init_bast.ncs` does place/reposition
`sta_invis_bast_conv` as promised, it is simply not a creature type, and no console primitive
in this codebase resolves a placeable by tag for `startdialog`'s own purpose (only
`useplaceable`/`opendoor`-shaped verbs, via `FindPlaceableByTag`, do).

### `startdialog 0xAA k_sta_bastila` (by object id) DOES reach `StartDialog`, twice, live

Since `startdialog` also accepts a numeric id (`ConsoleCommand.Cheats.cs`'s own
numeric-vs-tag split), `startdialog 0xAA k_sta_bastila` was tried instead of the tag form —
**and succeeded**, twice in the same session: `startdialog: ActionStartConversation queued on
the player, targeting 0xAA (dlg 'k_sta_bastila')` immediately followed by
`dialogue: engine reached StartDialog (1 so far, forced=False)` and a
`dialogue-node-pub: #1 node -1 -> 61` line — the queued action genuinely drove the player
(`party follow` diagnostics show it walking toward the placeable, ~20m over several seconds)
and started the real conversation, landing on entry node 61/83. A second `startdialog 0xAA
k_sta_bastila` call later the same session (after the first conversation appeared to end with no
observed `RemovePartyMember`-shaped effect) again reached `StartDialog`, this time visibly
responsive to `skipline`/`selectreply 0` (`skip requested at node 52/83`, `reply slot 0
committed`) for several more exchanges.

### The roster wipe: NOT observed, across 15+ reply advances this round

`tools/partystate.ps1` was polled after every `selectreply 0` across both attempts —
`companions 1  soloMode 0` on the HOST, unchanged, every single sample, including immediately
after the conversation reached its highest observed node (61) and after 15 further advance
attempts on the second run (most of which read `refused: reply slot 0 not available (no
conversation, or out of range)`, meaning the conversation had already gone quiet again between
polls — consistent with either a short auto-advancing narration stretch, or the conversation
naturally ending and (per `dialogue-sync.md`'s own "adopt" mechanic) not being re-entered by
`startdialog`'s own second call in quite the way a fresh `StartDialog` reach would suggest).
**Whether `k_sta_bastila.dlg`'s own node-action reference to `k_psta_door.ncs` (found by string
sweep in the prior round, never resolved to a specific node) sits on a branch this pass's blind
`selectreply 0` spam never selected, or requires plot-flag state this staged scenario does not
have (`STA_BASTILA_DEAD`/other globals never set here), remains genuinely unresolved** — this
pass could drive the conversation but had no visibility into what each reply option's TEXT was,
so "always pick slot 0" is a blind guess at the affirmative/continue path, not a targeted choice.

### One incidental, unexplained asymmetry: `soloMode` differed between host and client

Read on both instances via `partystate.ps1` at the same point in the session (after the second
`startdialog` attempt): HOST `soloMode 0`, CLIENT `soloMode 1` (`companions 1` unchanged on
both — no roster drop on either side). Not chased further this pass — could be a pre-existing
per-save/per-module difference in how the two players' own solo-mode flags were seeded on
arrival at `sta_m45ac` (unrelated to `solo_segment_spectate`, which the host log never showed
engaging — no `solo segment: began` line on either side), or a genuine and previously-unnoticed
asymmetry worth a future round's attention if `solo_segment_spectate` work resumes.

### Verdict: `solo_segment_spectate` stays OFF — the precondition is now closer than ever but

### still not reached; the concrete next step is narrower than any prior round's

The good news this pass leaves behind: `startdialog` by object id is a working, repeatable way
to reach `k_sta_bastila.dlg`'s `StartDialog` without a real mouse click, closing the "no way to
force this conversation" gap every prior round of this doc named. The bad news: reaching
`StartDialog` is not the same as reaching the specific node that calls `k_psta_door.ncs`, and
this pass's blind reply-advancing did not find it. **Concrete next step, sharper than any prior
round's**: either build the `.dlg`-node script-walker `docs/research/star-forge-finale.md`'s own
prior round already named (reading `DialogList`/`ReplyList`'s `Script`/`Script2` GFF fields
directly, now that the conversation is reachable and drivable by object id, so a script-walker's
findings could be checked against a REAL running conversation instead of only static data), or
capture each node's reply TEXT live (via the `CSWGuiDialog` panel fields `dialogue-sync.md`'s
own §2 already documents, `panel+0x1970[]`/`+0x6C`) so a future round can choose replies by
their actual content instead of always picking slot 0.

Both processes stayed healthy throughout (`Get-Process swkotor` — `Responding=True` on both,
confirmed after the session). No crash. `dotnet build KotorCoop.slnx -warnaserror` and
`src/KotorCoop.Plugin/build.ps1 -WarnAsError`: both clean — no source changed this pass, live
rig session only.

## 9. The wipe chain, resolved end to end (2026-08-28): trigger → dlg node graph → the exact
## script and node, and why every prior `startdialog`/console attempt could never reach it

RESEARCH ONLY, static analysis only — no rig touched this pass (deliberately: the brief for
this round was docs-only). This closes the concrete-next-step every prior round of this doc
left open: **build a `.dlg`-node script-walker** to find exactly which node inside
`k_sta_bastila.dlg` calls `k_psta_door.ncs` and under what condition.

### 9.1 Method: `tools/kotorfiles.py`'s existing GFF walker already IS the `.dlg`-node
### script-walker — it just needed to be pointed at `EntryList`/`ReplyList`/`StartingList`

No new C# code was needed. `tools/kotorfiles.py`'s `GFF`/`GFFStruct` classes (`gff.root`,
`struct["ListLabel"]` returning a `List[GFFStruct]`, `struct.get("Field")`) already decode
KOTOR's `.dlg` GFF structs generically — `DLG ` is just GFF V3.2 with three top-level lists
(`EntryList`, `ReplyList`, `StartingList`) whose own struct members happen to be named
`Script`/`Text`/`Speaker`/`RepliesList`/`EntriesList`/`Active`. A ~40-line Python script
(scratchpad only, not committed — same precedent as `minigames.md`'s `ncsdump2`) imported
`kotorfiles` as a library, loaded `k_sta_bastila.dlg` out of `sta_m45ac_s.rim`, and ran a
plain BFS over the two mutually-referencing lists (an Entry's `RepliesList` gives reply
indices, a Reply's `EntriesList` gives entry indices — the walk alternates) from each of
`StartingList`'s 6 candidate starting entries, stopping at the first entry whose own `Script`
field equals `k_psta_door` (the wipe script §3.3/the 2026-08-28 `startdialog` round already
named). **VERIFIED**, read directly out of the real GOG `k_sta_bastila.dlg` (472 structs, 83
`EntryList` entries, 103 `ReplyList` entries, 6 `StartingList` candidates) — every claim below
is that walker's own output, not re-derived from memory.

A second scratchpad tool (a throwaway `net10.0-windows` console app referencing
`KotorCoop.Memory` directly — same non-committed precedent) disassembled the specific `.ncs`
files this walk implicated, using `NcsReader.Disassemble` + `FindActionCalls` against §1's own
expanded action table plus four more routines resolved by the same call-shape reasoning this
pass needed: none of `#42`/`#548`/`#213`/`#92` were resolved to names (consistent with §7's
"did not resolve routines #200/#229/#548/#582" — this pass adds #42/#213/#92 to that same
INFERRED-shape-only, not-independently-confirmed list); their *call shape* (a single-arg
boolean gate immediately before a `JumpIfZero`, or a tag-lookup immediately followed by an
action) was legible enough for this pass's purpose without a name.

### 9.2 The verified chain: trigger OnEnter → dialogue StartingList → a 7-node linear
### cutscene chain with exactly one real player choice (which doesn't matter) → the wipe

**Step 1 — the trigger really does start the conversation itself, no click needed.**
`k45_init_bastila.utt`'s `ScriptOnEnter` = `k_psta_init_bast` (already found by the prior
2026-08-28 round). This pass disassembled it: after a gate at offset 645-752 —
`GetPartyMemberByIndex(0) == GetEnteringObject()` (**VERIFIED** call shape: routine 577 then
routine 25 then an object-typed `Equal`, `type=22`) **AND** a second, unresolved routine-548
based condition, combined with `LogicalAnd` — the script looks up both `"sta_invis_bast_conv"`
and `"Bastila"` by tag (routine `#229`, offset 795/839), runs an unresolved single-arg boolean
check (`#42`, offset 892) on the result, and if that passes, calls **`ActionStartConversation`
(routine 204, argcount 11, offset 989)** directly — the exact same engine entry point
`dialogue-sync.md`'s own `startdialog` verb uses, but issued by the GAME's own script against
whatever object the trigger resolved, not synthetically. **VERIFIED**: this is a real,
unconditional-once-gated `ActionStartConversation` call sitting in the trigger's own `OnEnter`,
not a hypothesis.

**Step 2 — `StartingList` picks the branch, and only one of six branches leads anywhere near
the wipe.** `k_sta_bastila.dlg`'s `StartingList` has 6 candidates, evaluated in authored order,
first whose `Active` condition script returns true wins (**VERIFIED** GFF data; standard
BioWare `.dlg` semantics, matching this doc's own live-tested fact that a fresh `startdialog`
call landed on entry 61 — index 2 of this list):

| # | entry | condition script | what it checks (disassembled) | leads to the wipe? |
| --- | --- | --- | --- | --- |
| 0 | 77 | `sta_bast_lasttst` | `GetGlobalBoolean("K_PSTA_BASTSAVE") == 1` | BFS: no path to entry 70 |
| 1 | 72 | `k_sta45_bastevil` | `GetGlobalNumber("G_FinalChoice") == 1` | BFS: no path to entry 70 |
| **2** | **61** | **`k_sta_basttalk0`** | **`GetGlobalNumber("Sta_BastTalk") == 0`** | **YES — the only branch that reaches it** |
| 3 | 51 | `k_sta_basttalk1` | `GetGlobalNumber("Sta_BastTalk") == 1` | BFS: no path (confirmed reachable subtree = entries 51-60 only, dead-ends) |
| 4 | 46 | `k_sta_basttalk2` | `GetGlobalNumber("Sta_BastTalk") == 2` | BFS: no path to entry 70 |
| 5 | 0 | `k_sta_basttalk3` | `GetGlobalNumber("Sta_BastTalk") == 3` | BFS: no path to entry 70 |

All six condition scripts are ~110-byte one-liners; the table above is their entire logic,
**VERIFIED** by full disassembly of each. `Sta_BastTalk` is a plain NWScript global number,
defaulting to 0 on a fresh save (no explicit initialiser found or needed).

**Step 3 — the branch itself, entry 61 to entry 70, is a straight line with exactly one real
choice that doesn't matter.** Walking `EntryList`/`ReplyList` from entry 61 (**VERIFIED**, the
BFS's own path, cross-checked node-by-node against the raw GFF dump):

```
entry 61  Script=k_sta_basttalk_1  (no text — the scene-staging beat, see 9.3)
  -> reply 79 (silent, 1 option)              -> entry 62  Script=k_sta_bastmove (moves the
                                                            real "sta_bastila" NPC creature
                                                            into position — see 9.4's open risk)
  -> reply 80 (silent, 1 option)              -> entry 63  "Revan - I knew you'd come for me."
                                                            (strref 21176, Bastila speaking)
  -> THREE real reply choices here, ALL converge back to entry 67:
       reply 85 "I'm <FullName> now, Bastila. Revan is no more." (strref 21228) -> entry 66 (direct)
       reply 83 (strref 21227, k_sta_bastcon+20)                                -> entry 64 -> reply 82 (IsChild) -> entry 67
       reply 81 (strref 21226)                                                  -> entry 65 -> reply 84 (IsChild) -> entry 67
  -> entry 66  "Sadly, I know this is true... you rejected the dark side." (strref 21179)
  -> reply 86 (silent)                        -> entry 67  "Now you must pay the price. Here on
                                                            the Star Forge the power of the dark
                                                            side is at its strongest. This time
                                                            you will not defeat me!" (strref 21180)
  -> reply 87 (silent, VO only)                -> entry 68  Script=k_psta_stasis   (no text)
  -> reply 88 (silent)                         -> entry 69  Script=k_psta_stasis2  (no text)
  -> reply 89 (silent)                         -> entry 70  Script=k_psta_door     <-- THE WIPE
  -> reply 90 (silent)                         -> entry 71  Script=k_psta_pcwalk   (no text)
  -> reply 91 Script=k_sta_bastatt (the 74 KB combat-AI-sized script — likely starts the fight;
                                     not traced further this pass) -> EntriesList empty (dialogue ends)
```

**VERIFIED**: every entry/reply struct, its `Script`/`Text`/`RepliesList`/`EntriesList` field,
and the reply-choice convergence (81/83/85 all reach entry 67, two of them via `IsChild=1`
back-references) were read directly out of the GFF, not inferred from names. The player's
choice at entry 63 is real (three different strref'd lines) but narratively cosmetic here — it
does not gate the wipe. **Every other link on this path has exactly one option with an empty
`Active` condition** — a pure forced cutscene chain, seven "commit the reply" actions
(`selectreply`-shaped) from entry 61 to entry 70, none of them player-skippable, none of them
conditionally gated at the link level.

**Step 4 — the wipe script itself, corrected.** `k_psta_door.ncs` (2480 bytes) does call
`RemovePartyMember` nine times — **but not by tag**, correcting §3.3's own citation:
`RemovePartyMember(0)`, `RemovePartyMember(1)`, … `RemovePartyMember(8)` — nine **plain integer
NPC-slot constants**, back to back (`PushConstant int N` immediately before each `CallEngine
routine=575 argcount=1`, offsets 999-1146, **VERIFIED**, no string constant precedes any of
them). This matches NWScript's real `RemovePartyMember(int nNPC)` signature (an `NPC_*`
enumerated constant, not a tag) far better than a tag-lookup reading would. **Immediately
after**, a *separate* block (offsets 1154-1476) does nine `#200`-style tag lookups by name —
`"Bastila"`, `"Carth"`, `"Jolee"`, `"Cand"`, `"HK47"`, `"Juhani"`, `"T3M4"`, `"Zaalbar"`,
`"Mission"` — each followed by a validity check (`#42`) and (not traced further this pass,
each companion's continuation branch was not individually unrolled) presumably a
hide/reposition/effect call on that companion's own physical body, now that they are off the
roster but still physically standing in the room. **§3.3's "nine RemovePartyMember calls, by
tag lookup against Bastila/Carth/…" conflated these two adjacent-but-distinct call blocks** —
the removal is unconditional and index-based; the tag lookups are a second, subsequent pass
over the same nine companions for a different purpose.

### 9.3 The precondition every `startdialog`/console attempt this doc's own prior rounds tried
### actually skipped — found, not just guessed

**`Sta_BastTalk` is a one-shot global, and it is bumped the INSTANT entry 61 is merely
*reached* — before the wipe, before the player can do anything, unconditionally.**
`k_sta_basttalk_1.ncs` (entry 61's own `Script`, 2167 bytes) is entry 61's action: among other
things (staging the party at `sta_way_party0`/`1`/`2`, `SetPartyLeader`, jumping the
NPC-tagged `sta_bastila` object into view) it contains, at offset 870-939:

```
870: push "Sta_BastTalk"; CallEngine 580 (GetGlobalNumber)   -- read Sta_BastTalk
893: push 1; Add                                              -- +1
923: push "Sta_BastTalk"; CallEngine 581 (SetGlobalNumber)    -- write it straight back
```

**VERIFIED**, full disassembly. This runs the moment the entry-61 node fires — it does not
wait for the player to advance past it, and it does not check whether the conversation
subsequently reaches the wipe. The practical consequence, matching this doc's own
2026-08-28 `startdialog` round's live log **exactly**: the FIRST `startdialog 0xAA
k_sta_bastila` call that session reached `StartDialog` and landed on **entry node 61**
(`Sta_BastTalk` was 0, `k_sta_basttalk0` correctly won `StartingList`'s evaluation) — but by
the time that node had merely been reached, `Sta_BastTalk` was already 1, whether or not the
chain continued any further. The SECOND `startdialog 0xAA k_sta_bastila` call that same
session landed on **"node 52/83"** — this pass's own BFS from entry 52 (**VERIFIED**, a
fresh walk, not the same one used for the wipe path) confirms entry 52 is `StartingList[3]`'s
own branch: `entry 51` (`k_sta_basttalk1`, `Sta_BastTalk == 1`) → `reply 65` → **entry 52**,
exactly the node the live log reported, and that branch's entire reachable subtree (entries
52-60) never reconnects to entry 70 (**VERIFIED**, second BFS). **This is not a coincidence of
node numbers — it is the fingerprint of the exact mechanism**: the first call's mere arrival at
entry 61 permanently retired the `Sta_BastTalk == 0` branch for the rest of that save, and
every subsequent `startdialog` call (this doc records exactly two, both in the same session)
was mathematically guaranteed to land somewhere in the `Sta_BastTalk == 1` branch instead,
which this pass's BFS proves has no path to `k_psta_door` at all. **The console approach did
not fail to find the right node — it found it once, correctly, on the very first call, and the
act of finding it consumed the one-shot precondition needed to reach it again.**

A second, independent gate likely also mattered and remains **NEEDS-LIVE**: `k_psta_init_bast.ncs`'s
own trigger-OnEnter gate (9.2 step 1) requires `GetEnteringObject() == GetPartyMemberByIndex(0)`
— i.e. the object that crossed the trigger boundary must be the actual party leader/PC. The
prior 2026-08-28 round's `walkto` attempt into this trigger's own coordinates produced no
`dialogue: engine reached StartDialog` line at all, which is consistent with either this gate
or the unresolved `#42` validity check on the `"Bastila"`-tagged object failing — **not
independently distinguished this pass**, since `startdialog`'s object-id override (used
successfully afterward) sidesteps this whole gate by calling `ActionStartConversation` from a
different caller context entirely (queued on the local player, per `dialogue-sync.md`'s own
addendum), which is exactly why it could succeed where the trigger-walk attempt did not.

### 9.4 The console recipe that WILL fire the wipe — a testable, specific hypothesis, NEEDS-LIVE

Not yet run — this doc's own convention (RESEARCH ONLY, no rig this pass) applies here too.
Concretely, on a save that has never yet reached this dialogue branch (or after resetting the
one-shot precondition):

1. `setglobal Sta_BastTalk 0` on the HOST (idempotent safety — guarantees `StartingList`
   candidate index 2 wins even if a prior attempt already bumped it; harmless on a genuinely
   fresh save where it is already 0). Optionally also `setglobal K_PSTA_BASTSAVE 0` and
   `setglobal G_FinalChoice 0` if either might already be set, so candidates 0/1 don't
   pre-empt candidate 2.
2. `startdialog 0xAA k_sta_bastila` **once** — per 9.3, this should land on entry 61.
3. **Exactly seven** `selectreply 0` calls, back to back, with no other `startdialog` call in
   between and as little delay as possible: 61→62 (may need `k_sta_bastmove`'s `ActionJumpToObject`
   on the real `sta_bastila` NPC creature to actually resolve — see the open risk below) →
   63→66 (slot 0 = reply 85, the shortest of the three converging choices, skipping the
   flavor-only entries 64/65) →67→68→69→**70 (the wipe fires here)**.
4. Confirm via `tools/partystate.ps1` (`companions` count drops from 1-2 to 0) or the plugin
   log around `RemovePartyMember` if such a diagnostic exists; if not, the roster-count read
   `SoloSegmentController` itself already watches is the ground truth per §8.1.
5. **Do not retry with a second `startdialog` call if a step fails partway** — per 9.3,
   `Sta_BastTalk` is already 1 the moment step 2 lands, so a bare retry is guaranteed to reach
   the dead `Sta_BastTalk == 1` branch instead. Re-run `setglobal Sta_BastTalk 0` before every
   single attempt.

**One open risk this pass could not resolve without the rig**: entry 62's own script
(`k_sta_bastmove.ncs`) explicitly jumps/moves the object tagged `"sta_bastila"` (the real NPC
creature, distinct from the `sta_invis_bast_conv` placeable `startdialog` targets) to a
waypoint tagged `STA_BAST_JUMP2`. If that NPC creature is not actually present/spawned when
`startdialog` is used to force the conversation synthetically (unconfirmed — the 2026-08-28
`startdialog` round only ever confirmed the placeable `0xAA`, never the creature `sta_bastila`
itself, e.g. via `census` or a direct tag probe), this step could silently stall the action
queue exactly the way the prior round's "conversation appeared to end" symptom reads. **A
sensible pre-flight check for the live pass**: confirm `sta_bastila` (the creature tag)
resolves to a valid, spawned object *before* attempting step 2 above — if it does not, that is
likely the true blocker, independent of the `Sta_BastTalk` finding, and would need its own
fix (find whatever spawns/positions the real Bastila NPC, likely earlier module `OnEnter`
content this pass did not trace).

### 9.5 What the solo-segment detector should key on if the wipe stays unreachable: hooking
### `RemovePartyMember` directly — assessed, not built

The task asked for a cost assessment of this alternative, given `solo_segment_spectate`'s own
detection (§8.1) has never once observed a real drop edge across five live-rig rounds. Hooking
`ExecuteCommandRemovePartyMember 0x00541C00` (routine 575, the VM's own action handler,
**VERIFIED** address per §1's table) directly, rather than inferring a "solo segment" from a
roster-count diff `Odyssey.TryReadPartyRoster` observes indirectly:

**Pros.** Fires at the exact moment of ground truth, not a polled diff — no dependency on the
pump cadence noticing the roster count change before something else does. Immune to false
negatives from the polling gap this pass's own live rounds kept hitting (the conversation
"went quiet" between polls). Immune to false positives §8.1 already worried about (party
never-recruited, death) since it fires only on the actual engine call, not a state read.
Generalises to ANY future forced-solo content this mod hasn't researched yet (a different
`RemovePartyMember`-driven scene in a mod, or DLC-shaped content), not just the two catalogued
modules — the module gate (`SoloSegmentCatalogue`) could still be applied as a *filter* on top
of the hook rather than as the sole detection mechanism.

**Cons / cost.** This is a different SHAPE of hook than anything `docs/CONTRIBUTING.md`'s
engine-write rules or this project's existing detours (`DialogueGateHook.OnStartDialog`, a
`CSWSObject::StartDialog` prologue tap) have done: those hook a **method on an object
instance** reached via a vtable slot/adjustor thunk (per the "vtable slots, not addresses"
project rule); `ExecuteCommandRemovePartyMember` is a **VM action-routine handler**, called
from the single dispatch table `CSWVirtualMachineCommands::InitializeCommands` fills (§1) —
every `RemovePartyMember(...)` call in the WHOLE GAME, from any script, in any module, funnels
through this one function. A hook here needs its own `FINDING-OFFSETS.md`-style live
verification pass (confirm the prologue bytes at `0x00541C00` match what static disassembly
shows, the same category of check `DialogueGateHook`'s own address already had) before
shipping, and needs to read the routine's own argument (the `int nNPC` on the VM stack, not an
object handle) to know WHICH companion, which this project's stack-reading conventions
(`NcsReader`'s own stack-offset fields) can express but nothing here has read a live VM stack
argument out of running memory yet — that specific technique is unproven in this codebase.
It also fires **nine times per wipe** (one per `RemovePartyMember` call, 9.2 step 4), so the
consuming code needs the same kind of debounce/coalesce-into-one-event logic
`MinigameAnnouncer`/`AwayAnnouncer`'s diff-per-tick shape already uses elsewhere, or nine
redundant "solo segment began" events fire back to back.

**Assessment**: comparable effort to the existing `DialogueGateHook.OnStartDialog` detour (a
single well-understood function-prologue hook plus one new address to live-verify) — MEDIUM,
not a new category of risk, and it is a genuinely more direct signal than the roster-count
poll `SoloSegmentController` currently uses. **Recommended as the next concrete step** if
9.4's console recipe also fails to reach the wipe live, since it would let a future round
detect the wipe (and thus test the actual spectate/input-lock machinery §8.2 exists for)
without needing to solve the `.dlg`-node-reachability puzzle at all — the hook fires on the
REAL trigger's own script, from ordinary play, with no console driving needed.

### 9.6 Provenance

Read: `k_sta_bastila.dlg` (GFF, full `EntryList`/`ReplyList`/`StartingList` walk),
`k45_init_bastila.utt`, `k_psta_init_bast.ncs`, `k_sta_basttalk0/1/2/3.ncs`,
`sta_bast_lasttst.ncs`, `k_sta45_bastevil.ncs`, `k_sta_basttalk_1.ncs`, `k_sta_bastmove.ncs`,
`k_psta_stasis.ncs`, `k_psta_stasis2.ncs`, `k_psta_door.ncs`, `k_psta_pcwalk.ncs` — all
extracted from the real GOG install's `modules/sta_m45ac_s.rim` via `tools/kotorfiles.py`,
disassembled via `src/KotorCoop.Memory/GameFiles/NcsReader.cs`/`NwscriptActions.cs` through a
throwaway console app (not committed, same precedent as `minigames.md`'s `ncsdump2`) and a
throwaway Python GFF-walker script (not committed) built directly on `tools/kotorfiles.py`'s
own `GFF`/`GFFStruct` classes — no new project code, no rig, no game launched.
`dotnet build KotorCoop.slnx -warnaserror`: clean (unaffected — nothing in `src/` changed this
pass).

---

## 10. Live pass (2026-08-28, rig round): §9.4's console recipe run for real — entry 61 reached
## reliably, twice, but the chain stalls immediately after; the roster never wipes, so every
## spectate lever (hide/lock/camera/toast/restore) stays unverified this round too

Two-instance rig (`tools/demo.ps1`), `solo_segment_spectate=1` written into both `coop.ini`s
before launch. `warp sta_m45ac` on the HOST landed BOTH instances in the module together (the
co-op `warp` verb's own "the peer follows" behaviour — confirmed via `blockpoke.ps1 ... state`
reading `module 'sta_m45ac'` on both sides, no separate client-side warp needed). Party roster
baseline: `partystate.ps1` read `companions 1` on both instances throughout setup.

### The recipe: reaches entry 61 twice, cleanly, exactly as §9.4 predicted

`setglobal Sta_BastTalk 0` on the HOST (confirmed already 0 on a fresh save) → `startdialog 0xAA
k_sta_bastila` **once**. The plugin log showed `dialogue: engine reached StartDialog` and
`dialogue-node-pub: #1 node -1 -> 61` immediately — **exactly the entry §9.3 predicted for the
`Sta_BastTalk == 0` branch.** Confirmed a second, independent time later in the same session
(after resetting `Sta_BastTalk` back to 0 — the reset itself reported `'Sta_BastTalk' (number) 1
-> 0`, proving the global really had been bumped to 1 by the first attempt's mere arrival at
entry 61, matching §9.3's "one-shot" finding exactly): a fresh `startdialog` call produced
`dialogue-node-pub: #2 node -1 -> 61` again. **Entry 61 is reliably, repeatably reachable
through this console recipe — that part of §9.4's plan is now live-confirmed, not just
reasoned.**

### The chain does not advance past entry 61, despite seven clean `selectreply` calls

Issued **exactly seven** `selectreply 0` calls on the first attempt (spread over roughly two
minutes, well clear of the "hammering re-commits against a stale panel" pacing warning
`console-cheats.md` documents for this verb) and one more on the second, cleaner attempt. Every
single call logged `dialogue: selectreply committed slot 0` / `cheat: reply slot 0 committed
(status 1)` — the API-level call always reports success. **But `dialogue-node-pub` never logged
a second transition in either attempt** — the node counter stayed at 61 for the rest of each
session, and `blockpoke.ps1 ... dialogue` read empty `speaker`/`partner`/`line` fields within
seconds of the first `selectreply`, in both attempts. `partystate.ps1`'s `companions` count
stayed at **1** on both instances for the whole round — **the wipe did not fire.**

This is the exact symptom the earlier "conversation appeared to end" live rounds reported, and
it lands precisely where §9.4's own open risk said it might: **entry 62's script,
`k_sta_bastmove.ncs`, tries to move the real `sta_bastila`-tagged NPC creature into position**
(distinct from the `sta_invis_bast_conv` placeable `startdialog` targets, which only exists to
give the console verb an object to aim at). This round had no tag-resolution console primitive
available to directly confirm whether that creature is spawned at this point in a save staged
straight at the door via `warp` (the same gap §9.4 itself flagged as unconfirmed) — `resolve`
only takes a hex object id, and no `firescript`/`census`-style verb in this codebase resolves a
bare tag to an id. **Not independently distinguished this round**: whether entry 62's script is
silently failing to resolve `sta_bastila` and aborting the conversation, or blocking forever
waiting on an action that can never complete, or something else entirely — only that the
symptom (repeatable arrival at 61, then nothing) matches the predicted failure mode exactly.

A black screen was observed on BOTH instances throughout this test (no geometry, HUD, or dialogue
text rendered, just a bare frame-rate counter in the corner on the client). **Not conclusively
tied to the dialogue stall** — no screenshot was taken between the `warp` landing and the first
`startdialog` call, so whether the black screen predates the recipe entirely (a dark default
`warp` landing spot in this module, unrelated to any of this) or was caused by entry 61's own
staging script (`k_sta_basttalk_1`, which repositions the party and jumps the camera) was not
distinguished. Recorded as an open observation, not a finding.

### The spectate levers: not exercised this round — the wipe never happened

Because the roster never dropped, `SoloSegmentController` never had a drop edge to detect:
neither plugin log (`kotorcoop-plugin.log`) on either instance shows a single `solo segment:
...` line anywhere in the session. **Phantom hide, input lock, camera follow, the toast, and the
restore-on-refill/module-change behaviour are all still unverified live** — none of §8.3's test
plan past step 1 could be exercised, since step 1 itself (reach a genuine roster-empty state)
did not succeed. No crash on either instance across the whole round (`Responding=True`
throughout, confirmed after the final `selectreply` attempt).

### Verdict: `solo_segment_spectate` stays OFF; the wipe chain itself is confirmed reachable,
### the block is narrowed to "entry 62 onward", not the `StartingList`/`Sta_BastTalk` mechanism

§9's static analysis of the `StartingList`/`Sta_BastTalk` gate is now live-corroborated — the
recipe reaches entry 61 exactly as predicted, twice, reproducibly. The remaining gap is
downstream of that, inside the 61→70 linear chain itself, and the leading suspect is still
§9.4's own flagged risk (the real `sta_bastila` creature's spawn state). **Concrete next step**:
before the next rig round retries this, add a tag-resolution console primitive (or extend
`resolve`/`census` to accept a bare tag) so `sta_bastila`'s live spawn state can be checked
directly rather than inferred from the failure's shape — or pursue §9.5's `RemovePartyMember`
hook instead, which sidesteps the `.dlg`-reachability question entirely by detecting the wipe
from real, un-forced play. `solo_segment_spectate` stays at its default (`0`, OFF) in
`coop.ini`/`AgentSettings`/`CoopConfig`/`profiles/coop.ini.example` — no default was flipped
this round. `dotnet build KotorCoop.slnx -warnaserror` and `src/KotorCoop.Plugin/
build.ps1 -WarnAsError`: both clean (no source changed this pass — a live-verification round
only).

## 11. Built (2026-08-28): the script-agnostic second trigger — a detour on
## `ExecuteCommandRemovePartyMember`, a burst-trigger input on `SoloSegmentMachine`, and a
## `removeparty` dev verb that reaches it without the Bastila dialogue

§9.5 assessed this ("Recommended as the next concrete step") but did not build it. This pass
builds it: a plugin-side detour that observes every real `RemovePartyMember` call directly,
independent of and in addition to `SoloSegmentController`'s existing roster-count poll, plus a
console primitive to exercise it without needing to solve the `.dlg`-node-reachability puzzle
§9.3/§9.4 left open (the one-shot `Sta_BastTalk` precondition that made a second `startdialog`
attempt land on a dead branch).

### 11.1 Addresses

**VERIFIED**, re-confirmed fresh this pass against the real GOG v1.0.3 binary (not re-derived
from memory of §1's own table):

* `CSWVirtualMachineCommands::ExecuteCommandRemovePartyMember` = **0x00541C00** — `tools/q.py
  func RemovePartyMember` names this address itself `[__thiscall]`; `tools/disasm.py 0x54C960
  60000` shows `mov dword ptr [edx+0x8fc], 0x541c00` at 0x0054E3C6 inside
  `InitializeCommands` (0x8FC / 4 = **575**, matching §1's own table and `minigames.md`'s
  independent claim).
* The hook's own splice point is **not** the function's entry — `tools/disasm.py 0x541C00 60`,
  read in full:

  ```
  0x00541C00  51                   push ecx
  0x00541C01  8b0d003a7a00         mov ecx, dword ptr [0x7a3a00]      ; ecx = VM
  0x00541C07  8d442400             lea eax, [esp]                     ; eax = &local
  0x00541C0B  50                   push eax
  0x00541C0C  e8eff30800           call 0x5d1000                      ; StackPopInteger(vm, &local)
  0x00541C11  85c0                 test eax, eax
  0x00541C13  7509                 jne 0x541c1e                       ; success -> HookSite
  0x00541C15  b82ff8ffff           mov eax, 0xfffff82f                ; (failure path)
  0x00541C1A  59                   pop ecx
  0x00541C1B  c20800               ret 8
  0x00541C1E  8b0dfc397a00         mov ecx, dword ptr [0x7a39fc]      <- HookSite (overwritten)
  0x00541C24  53                   push ebx                           <- ResumeAt
  ```

  `nNPC` is a NWScript-side argument on the VM's own parameter stack, not the x86 call stack —
  it is popped by `CVirtualMachine::StackPopInteger 0x005D1000` a few instructions into the
  function, so the function's true entry (0x00541C00) does not yet know it. **HookSite =
  0x00541C1E**, the first instruction past `StackPopInteger`'s own success branch — the
  earliest point `nNPC` is known. `StackPopInteger` is `__thiscall(vm, int* out)`, one stack
  arg, callee-cleaned (`ret 4`), so by the time it returns `esp` is back to exactly where it
  was right after `push ecx` — the same local slot `lea eax,[esp]` pointed at — meaning
  **`[esp] == nNPC` at HookSite itself**, read directly by the stub before `pushad` can disturb
  it. **Anchor** (4 bytes immediately before HookSite, never touched): `59 C2 08 00` (the
  failure path's own `pop ecx; ret 8` tail). **Prologue** (6 bytes overwritten):
  `8B 0D FC 39 7A 00` (`mov ecx, [0x7a39fc]`, the same `AppManagerPtr` global
  `Odyssey.Core.cs` already names). **ResumeAt = 0x00541C24**.
* `Odyssey.RemovePartyMemberBySlot(int slot, out int failure)` (`src/KotorCoop.Plugin/
  Odyssey.Party.cs`) — the `removeparty` dev verb's own dispatch — already existed before this
  pass (built for a different purpose, party-roster reconciliation) and needed no changes: it
  already dispatches `RemovePartyMember(int nNPC)` through the exact same parameter-stack
  pattern `startdialog` established (routine resolved by scanning the live table for the
  handler's address, never a hardcoded index; the VM must be idle; the call is stack-neutral).

### 11.2 What was built

* **`src/KotorCoop.Plugin/RemovePartyMemberHook.cs`** (new) — the detour itself. A two-anchor
  mid-function splice (this plugin's SECOND, after `ItemTemplateSpawnHook`'s first) that reads
  `nNPC` off `[esp]`, records `(slot, Environment.TickCount)` into a small fixed-capacity ring
  (18 entries — a real wipe fires exactly nine calls back to back, per §9.2 step 4; doubled for
  headroom the same way `ItemTemplateSpawnHook.Capacity` doubles `BarkHook`'s own reasoning),
  and returns through the relocated original instruction. **Deliberately plugin-only** — no
  `SharedBlock` field, no wire format, no agent-side consumer: `SoloSegmentController` (already
  plugin-side) is the only reader. Installed unconditionally in `PluginHost.Startup.cs` (no
  opt-out key), matching `BarkHook`/`PhantomHitHook`'s own precedent — its debug log and the
  `removeparty` verb work whether or not `solo_segment_spectate` itself is on; that key still
  gates the FEATURE (whether a captured call can actually begin a segment), unchanged.
* **`src/KotorCoop.Protocol/SoloSegmentMachine.cs`** — a new public method,
  `NoteRemovePartyMemberCall(catalogue, module, anyModuleAllowed, nowTicks)`: records every call
  into a 9-entry sliding window and, on a call that brings the trailing-~1s-window count to 2 or
  more (while not already `Active`, and while the module qualifies — the catalogue, or ANY
  module when `anyModuleAllowed`), sets `Active = true` and returns `Began` — the identical
  product decision `Step`'s roster-drop edge already makes, reached by a different, more direct
  signal. A single call is always a no-op (the whole point of the debounce): one
  `RemovePartyMember` is an ordinary, unremarkable script action; nine in a burst is a
  story-forced strip. Every call after the first `Began` is a no-op too (`Active` already true)
  — `Step` alone still owns the end conditions, so this closes §9.5's own "nine redundant solo
  segment began events" cost concern for the burst path without any extra coalescing logic.

  **A genuine race this pass had to close, not just describe**: the burst trigger can set
  `Active = true` a frame or more BEFORE the roster-count poll `Step` reads has caught up to the
  wipe (the hook fires at the exact VM call; `Step`'s own `CompanionCount` read happens on
  whatever the next frame's poll timing is). Without a guard, the very next `Step` pass would
  see `CompanionCount >= 1` (still stale) and misread it as "the roster refilled", ending the
  segment one frame after it began. Fixed with a new `_sawEmptyRosterSinceActive` field:
  `Step`'s own `rosterRefilled` check now additionally requires having actually OBSERVED
  `CompanionCount == 0` at least once since `Active` went true (set immediately for the
  drop-edge path, since `after == 0` there by construction; set the first time `Step` genuinely
  sees zero for the burst path). A real refill after Malak dies still ends the segment exactly
  as before, once the emptied state has genuinely been observed — see
  `SoloSegmentMachineTests.ABurstBeginDoesNotImmediatelyEndOnAStillStaleRosterCount`.

* **`src/KotorCoop.Plugin/SoloSegmentController.cs`** — drains `RemovePartyMemberHook`'s ring
  every frame (`DrainRemovePartyMemberHook`, the same dedup-by-serial shape
  `ItemTemplateSpawnController` already uses for its own hook ring, with an insertion sort by
  serial first since ring INDEX order is not necessarily capture order once the write cursor
  wraps and the debounce cares about relative timing), logs one **debug line per call**
  (`solo segment debug: RemovePartyMember slot {npcSlot} in {module}`) UNCONDITIONALLY —
  regardless of what `NoteRemovePartyMemberCall` decides — so a live pass can see the wipe fire
  even if the machine never begins a segment from it, and feeds each capture into the machine.
  New coop.ini key `solo_segment_any_module` (default 0/OFF) is read here and passed through as
  the burst trigger's own module gate; the roster-drop-edge trigger is untouched and stays
  catalogue-gated regardless — it has no equivalent "this is definitely a script, not a
  bench/death" signal of its own. `Invalidate()` now also resets the hook's ring and the
  controller's own drain-serial bookmark on a module change/session teardown.
* **`src/KotorCoop.Plugin/SharedBlock.cs`** — `CheatKindRemoveParty = 43` (next free number
  after `CheatKindStartDialog = 42`).
* **`src/KotorCoop.Plugin/CheatController.Party.cs`** — `RemoveParty()`: validates the slot
  (0-8), calls `Odyssey.RemovePartyMemberBySlot`, answers applied/refused. Added alongside
  `Recruit`/`Leader` (the file's existing party-affecting verbs), not `CheatController.
  Dialogue.cs` — this is not a dialogue verb, and building a real `RemovePartyMember` call needs
  nothing from the dialogue system.
* **`src/KotorCoop.Memory/IGameBridge.cs`** — `CheatKinds.RemoveParty = 43` (agent-side mirror).
* **`src/KotorCoop.Session/ConsoleCommand.cs`** / **`ConsoleCommand.Cheats.cs`** —
  `CoopCommandKind.RemoveParty`, `removeparty <slot>` verb-table row, `ParseRemoveParty`
  (0-8 range check, same shape as `ParseSelectReply`), and a `CheatsText` help line marked
  **dev-only**.
* **`src/KotorCoop.Agent/CoopCommandDispatcher.cs`** — dispatch entry, same one-liner shape as
  `SelectReply`'s own (`SendCheat(new CheatRequest(CheatKinds.RemoveParty, Amount: c.Amount))`).
* **`src/KotorCoop.Plugin/ConsoleVerbCatalog.cs`** and **`FuncInterpHook.cs`** — `removeparty`
  registered in both (Tab completion, and reachable by typing it directly at the in-game
  console) — unlike most dev-only primitives (`recruit`/`skipline`/etc, deliberately
  blockpoke-only per `FuncInterpHook.cs`'s own note), because this feature's own live plan
  (§11.4) needs it typed at the console on the host, matching the `startdialog` precedent.
* **`docs/CONSOLE.md`** — new row, marked **DEV-ONLY**. **`docs/CONFIG.md`** and
  **`profiles/coop.ini.example`** — new `solo_segment_any_module` key.

### 11.3 Tests

`tests/KotorCoop.Tests/SoloSegmentMachineTests.cs` — nine new cases for the burst trigger,
alongside the thirteen pre-existing roster-drop-edge cases (all still passing, unchanged):

* `ASingleRemovePartyMemberCallNeverBegins` — the debounce, one direction.
* `TwoRemovePartyMemberCallsWithinTheWindowBeginInACataloguedModule` — the basic positive case.
* `NineCallsInOneScriptLikeTheRealWipeBeginExactlyOnce` — simulates the real wipe's own nine
  calls; asserts `Began` fires exactly once, not nine times.
* `TwoCallsMoreThanTheWindowApartNeverBegin` — the debounce, the other direction.
* `ABurstInAnUncataloguedModuleNeverBeginsWithoutTheAnyModuleGate` /
  `ABurstInAnUncataloguedModuleBeginsWhenAnyModuleIsAllowed` — the `solo_segment_any_module`
  gate, both settings.
* `ABurstBeginIsANoOpWhileAlreadyActive` — no double-`Began`.
* `ABurstBeginDoesNotImmediatelyEndOnAStillStaleRosterCount` — the race fix (§11.2), full
  sequence: burst begins -> `Step` sees a still-stale count (stays active) -> `Step` finally
  sees the real zero -> a REAL refill afterward still ends it normally.
* `InvalidateClearsTheRecentCallHistoryToo` — the sliding window is reset on `Invalidate()`, so
  stale pre-module-change history cannot combine with a fresh call into a false burst.

`ConsoleCommandVerbTableCoverageTests` — one new sample-line row (`removeparty 0`), keeping the
existing exhaustiveness check (every non-sentinel `CoopCommandKind` has a parseable sample)
green.

Hook stub anchors: pinned the same way `ItemTemplateSpawnHook`'s own two-anchor splice is —
`Anchor`/`Prologue` are `ReadOnlySpan<byte>` literals checked byte-for-byte before any patch, so
a future engine-binary mismatch refuses cleanly (`RemovePartyMember hook NOT installed: ...`)
rather than corrupting memory. No dedicated unit test exists for the raw byte patch itself (none
exists for `ItemTemplateSpawnHook`'s either — this project's convention is a live install-time
guard, not a byte-level unit test, for x86 machine code it cannot execute off the win-x86
NativeAOT target).

### 11.4 Full result

`dotnet build KotorCoop.slnx -warnaserror`: clean, 0 warnings, 0 errors.
`pwsh src/KotorCoop.Plugin/build.ps1 -WarnAsError`: clean — `dinput8.dll` (x86, 1992 KB).
`dotnet test tests/KotorCoop.Tests --no-build`: **2369/2369 passing** (2360 pre-existing + 9 new
burst-trigger cases; the one coverage-test gap the new `CoopCommandKind.RemoveParty` value
opened — `EveryNonSentinelKindHasASampleLine` — was closed with a sample-line row, not skipped).

No rig this pass (docs-and-code round, matching the task brief). The live plan, not yet run:

1. `warp sta_m45ac` on the HOST (same staging §8.3/the 2026-08-28 live rounds already used).
   `solo_segment_spectate=1` on both `coop.ini`s (the feature's own kill switch — unlike the
   hook itself, the MACHINE deciding to begin a segment is still gated on this).
2. `removeparty 0` on the HOST, **three times** (three companions is more than the debounce's
   own minimum of two, and matches "roughly what a real wipe's opening calls look like" better
   than exactly two) — no dialogue, no `setglobal`, no `Sta_BastTalk` precondition maze.
3. Watch the HOST's own plugin log for `solo segment debug: RemovePartyMember slot 0 in
   sta_m45ac` (once per call, all three) and then `solo segment: began in sta_m45ac — script
   trigger (RemovePartyMember burst), roster read not required to have caught up yet` (once,
   after the second call).
4. Confirm `tools/partystate.ps1` shows the roster genuinely emptied (a real engine call, not a
   simulation — the companions actually left the active party) and that `block.
   LocalInSoloSegment` flipped (a `blockpoke`-style read, or trusting the log line).
5. **On the CLIENT**, confirm the spectate levers §8.2 already describes fire off the SAME wire
   flag this new trigger sets, unchanged from the roster-poll path: the chat toast, the HUD
   line, the camera snapping to the host's phantom, and — the still-unverified headline item
   every prior round of this doc left open — whether WASD/mouse input genuinely does nothing.
6. Regression: with `solo_segment_any_module=0` (the shipped default), repeat step 2 in an
   UNCATALOGUED module (e.g. the Endar Spire) and confirm no `began` line appears — only the
   per-call debug lines. Then set `solo_segment_any_module=1` and repeat in the same
   uncatalogued module, confirming it now DOES begin.
7. End-condition regression: let the roster genuinely refill (`recruit <slot>` back, or a real
   `AddPartyMember`) and confirm a real `Ended` fires — not suppressed by the race-guard fix
   (§11.2), since the guard only ever delays a FALSE end, never a true one.

### 11.5 NEEDS-LIVE

Everything in this section is static analysis against the verified GOG v1.0.3 binary plus new
unit tests — no rig this pass:

* The splice site itself (`HookSite`/`Anchor`/`Prologue`/`ResumeAt`), the `[esp] == nNPC` claim,
  and that the stub's `pushad`/`popad` genuinely leaves every register (including the relocated
  `mov ecx, [0x7a39fc]`'s needs) exactly as the original code would have.
* Whether `RemovePartyMemberHook.Install` actually succeeds against the real running process (a
  byte mismatch here fails closed — a log line, no crash — but has not been observed either
  way).
* Whether the burst trigger's own debounce window (~1s) is well-tuned against the REAL wipe's
  own timing — the nine-call burst is assumed to land within a few milliseconds of a single
  script's own execution (§9.2 step 4's own reading), never measured live.
* The full live plan (§11.4) end to end, including whether `removeparty`'s three calls actually
  produce the same downstream spectate/input-lock behaviour (§8.2) a real dialogue-driven wipe
  would — this hook observes the SAME engine call a real wipe makes, but has never been watched
  firing from either source on a live process.

## 12. Live pass (2026-08-28, rig round): §11's live plan run end to end — every lever confirmed,
## `solo_segment_spectate` flipped ON by default

Task: run §11.4's live plan on the two-instance rig (`tools/demo.ps1`), then flip the default if
clean, per this session's own queue (item 1 of a broader live-verification round covering both
this feature and `party_follow_phase2_kick`).

### Method

`src/KotorCoop.Plugin/build.ps1 -Install` on both game directories (fresh x86 build, strings
grep confirmed `RemovePartyMemberBySlot`/`NoteRemovePartyMemberCall` present in both deployed
`dinput8.dll`s). Both `coop.ini` reset to `profiles/coop.ini.example`, then
`solo_segment_spectate=1` set on both. `tools/demo.ps1` up (loads `END_M01AA` by default),
`warp sta_m45ac` on the HOST via the console channel (`tools/blockpoke.ps1 ... console '...'`).

### VERIFIED: the splice installs on both instances

Both plugin logs, at plugin startup, before any warp: `solo segment: RemovePartyMember calls at
0x541C1E (stub 0x...)` — the exact `HookSite` §11.1 derived, confirmed live for the first time
(§11.5's own first open item).

### VERIFIED: a single `removeparty 0` logs the debug line, no crash

Host, in `sta_m45ac`: one `removeparty 0` → engine reply `party slot 0 removed`; plugin log
gains exactly one `solo segment debug: RemovePartyMember slot 0 in sta_m45ac` line. Both
`swkotor.exe` processes stayed alive and responsive throughout.

### VERIFIED: a tight burst (3 calls within ~330ms) begins a segment

The console channel's own per-invocation process-spawn overhead (a fresh `pwsh` per call) made
three separate `blockpoke.ps1 console '...'` invocations land roughly 1.05s apart — outside the
machine's own ~1s debounce window, so the burst never began a segment this way. A small scratch
script (embedded `Add-Type`, one process, three back-to-back shared-block writes) sent three
`removeparty 0` commands 156-163ms apart instead (host already had slot 0 removed from the first
test, so the engine refused all three with `failure 9` — irrelevant, since `HookSite` sits before
any validity check and fires on every real VM call regardless of outcome, exactly as §11.1
predicted). Plugin log:

```
[09:35:28.792] solo segment debug: RemovePartyMember slot 0 in sta_m45ac
[09:35:28.933] solo segment debug: RemovePartyMember slot 0 in sta_m45ac
[09:35:28.933] solo segment: began in sta_m45ac — script trigger (RemovePartyMember burst),
                roster read not required to have caught up yet
[09:35:29.091] solo segment debug: RemovePartyMember slot 0 in sta_m45ac
```

`began` fired on the SECOND call, matching `NoteRemovePartyMemberCall`'s own "count reaches 2"
threshold exactly.

### VERIFIED: the owner's roster genuinely empties; the peer's phantom hides on the owner's side

`tools/partystate.ps1` on the host immediately after: `companions 0`. The client (mirroring):
`companions 0  soloMode 1`. `tools/blockpoke.ps1 ... state` on the host: `phantoms 0  remotes 0`
— the client's own phantom is not tracked at all on the owner's instance while the segment is
active, confirming the agent-side peer-hide filter (`CoopAgent.PumpOnce`,
`SoloSegmentRule.ShouldHideAllPeers`) engaged.

### VERIFIED: the spectator's input parks, camera follows, and a toast fires

Client plugin log: `solo segment: spectating 0x10D — input parked, camera follows` — the
`SoloSegmentSpectateController` log line, firing on cue. The client's own agent log
(`agent2.log`): `* Lanik is fighting alone - spectating` (Lanik = the host's player name) — the
status-toast/notice text confirming the spectator-side reaction surfaced through the normal
agent-event path, not just an internal flag.

**Still not verified**: whether the input lock stops a real PHYSICAL WASD press. Injected
keystrokes never reach this game (docs/live-verification.md), so this remains provable only by
confirming the mechanism engaged (the log line, the flags it sets) — the same limit §11.5 already
flagged, unchanged by this pass.

### VERIFIED: `solo_segment_any_module`'s own gate, both directions

With `solo_segment_any_module=0` (the shipped default), the identical burst in an UNCATALOGUED
module (`END_M01AA`, via `warp end_m01aa`) produced three `solo segment debug:` lines but no
`began` — the gate correctly holds the burst trigger to the catalogue when the extra key is off.
Torn the rig down, set `solo_segment_any_module=1` on both `coop.ini`s (read once at plugin
construction — a restart is required for the flag to take effect, unlike the burst's own
per-call reads), relaunched (fresh session loads directly into `END_M01AA`), repeated the
identical burst: `solo segment: began in END_M01AA — script trigger (RemovePartyMember burst),
roster read not required to have caught up yet` fired, and the client's own spectate log line
fired in the same uncatalogued module too. No crash either direction.

### VERIFIED: the end condition — a real roster refill ends the segment cleanly

`recruit 0` on the host (still in the `any_module=1` / `END_M01AA` session): engine reply `NPC
slot 0 recruited and added to the party (0x127)`. ~28 seconds later, plugin log: `solo segment:
ended (roster refilled or the module changed)` — §11.2's own race-guard fix
(`_sawEmptyRosterSinceActive`) did not block the real end condition, matching its own design
intent (the guard only ever delays a FALSE end).

### Verdict: every lever holds; `solo_segment_spectate` flipped ON by default

No crash on either instance across the whole round (install → catalogued burst → uncatalogued
regression, both directions → end condition). Per this project's own bar ("flip ON only when all
levers hold"), `solo_segment_spectate` is now ON by default:
`SoloSegmentController`/`SoloSegmentSpectateController`'s own `PluginConfig.ReadBool` fallback,
`CoopConfig.SoloSegmentSpectateEnabled`'s own fallback (also fixed to the
`!ContainsKey(...) || IsTruthy(...)` pattern `CompanionXpSync`/`CompanionGearSync` already use,
closing the same "key absent falls back to OFF regardless of the shipped default" gap footnote³
of `docs/CONFIG.md` describes for other features), `profiles/coop.ini.example`, `docs/CONFIG.md`,
`README.md`, `docs/roadmap-seamless.md`, and this doc's own README.md index row all updated to
match. `solo_segment_any_module` stays at its own default OFF — it is a testing convenience for
future forced-solo content, not a feature toggle of its own.

### Files

`src/KotorCoop.Plugin/SoloSegmentController.cs`, `SoloSegmentSpectateController.cs`,
`src/KotorCoop.Session/CoopConfig.cs`, `CoopAgent.cs`, `src/KotorCoop.Protocol/PlayerState.cs`,
`profiles/coop.ini.example`, `docs/CONFIG.md`, `README.md`, `docs/roadmap-seamless.md`,
`docs/research/README.md`, `docs/live-verification.md`.

## 7. The endings in co-op, live (2026-10-10)

**Test fixture.** The rig's campaign saves were built from the Endar Spire save with console
recruits and warps, so Trask never left party slot 0 and Bastila never took it; STUNT_55a's
`k_scene_start` then saw slot 0 "available", spawned Trask, and its ceremony waited forever for
Bastila. `tools/make_ending_fixture.py` copies a save and writes Trask out of the party table the
way `k_pend_trask_d` would (slot 0 unavailable, no members): `000921 - ending_fixture` from
`000919 - campaign_sta_m45ac`. The one ending save that started inside `STUNT_57` cannot load at
all (a save written inside a cutscene module comes up with no player).

**What happens on Malak's death.** `k_psta_ud_malak`, user event 1007: `STA_MALAK_DEAD`, both doors
unlocked; `G_FinalChoice == 1` and a male player jumps to `STUNT_54a` (movie 54); otherwise
nothing more, and the player walks out through `k45_door_end` (its conversation `sta45d_doors`),
`sta_walkaway` -> `sta_endgame` -> `StartNewModule("sta_m45aa", "STA45A_WAY_45D")`, the
Carth/Bastila confrontation. Malak's own conversation reaches the ending script `k_psta_mlkend`
only through an easter-egg branch. `slay` skips event 1007's scripted path.

**Verified end to end, both games together** (fixture, `setglobal STA_MALAK_DEAD true`,
`warp sta_m45aa STA45A_WAY_45D`, first replies): the confrontation (dark choice) -> STUNT_54a's
bridge scene -> STUNT_55a's dark-side ceremony -> movie 55 (42 s, both at once) -> credits ->
main menu, no crash. The joiner now builds the scene's friendly actors (sta_carth, sta_bastila)
itself and follows the host's lines; the stand-in stands beside the player in ordinary scenes and
shows in STUNT_54a.

**Open: the partner in stunt-driven shots.** STUNT_55a's `end_55a` and STUNT_57's `m41ad_c01`
carry a StuntList (`PLAYER` -> `m44ac_c01_char02`, ...): the engine loads the stunt model onto the
participant's client body as an add-in animation source (`SendDialogModels` 0x5A0080 ->
`HandleDialogModels` 0x62E020 -> `LoadStuntModelsForDialog` 0x62C620, Gob vtable +0x20; undone in
`UnloadStuntModelsForDialog` 0x62C6F0) and plays the dlg's own animation numbers on it (1200..1205
for the player, live at client body +0x48) through `SetDialogAnimations` 0x6313A0. The player's
object never moves (79.3, 69.5 the whole ceremony) while its body is drawn on the balcony beside
Bastila, so a stand-in staged beside the object is off-camera. Branch `wip/scene-stunts` gives the
stand-in the same add-in model (loads, Gob+0x64 set) and replays the player's ids with the
engine's own sequence (animbase cutscene flag, idle, id), but the stand-in has not appeared on
screen yet; the load also tended to land after the first ids (the stand-in is only staged once
both players stand on the mark). Next step: load the track at conversation start, and check
whether the body plays the add-in animation at all (read the Gob's current animation, not +0x48).
