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
`dotnet test tests/KotorCoop.Tests --no-build`: 2339/2339 passing (unchanged).
