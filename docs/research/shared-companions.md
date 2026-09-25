# Shared companions — exploration, not a commitment (research 2026-09-25)

Question on the table: could companions be "shared" — e.g. one companion each, instead of the
host owning both — without committing to building it? This is a survey of the current model and
three option shapes, grounded in `party-reconciliation.md`, `companion-state-sync.md`,
`companion-combat-credit-plan.md`, `control-return.md`, `galaxy-map-and-party-select.md`, and the
live controllers in `src/KotorCoop.Plugin`. No code changes accompany this document.

Tag legend, matching every other doc here: **VERIFIED** = read out of the binary/source, or
asserted by a passing test/live pass already on record. **INFERRED** = a reasonable deduction
from verified facts, not itself measured. **NEEDS-LIVE** = would need a rig pass before shipping.

---

## 1. How the current model works, end to end

### 1.1 One party table per engine instance; the host's is the only one anyone plays by

Every running `swkotor.exe` — the host's and each joiner's — has its own, completely independent
`CSWPartyTable` (`CServerExoAppInternal+0x1B770`) and `CSWParty` (the separate client-side
control/leader struct). **VERIFIED** (`party-reconciliation.md` §"Engine findings",
`galaxy-map-and-party-select.md` §2.3/§2.5). The engine itself enforces, per instance:

- `PartyMemberCount` (`+0x00`) caps at **2 active companions**, full stop — not a project choice,
  `CSWPartyTable::AddMember` (`0x00565620`) refuses a third add outright. **VERIFIED**
  (disassembly, `party-reconciliation.md` line 118).
- 9 total roster slots (`+0x30` availability, `+0x0C` per-slot spawned object id). **VERIFIED**.
- `+0xF0` (`pt_controlled_npc`) freezes `AddMember`/`RemoveMember`/`CreateParty` while non-`-1` —
  a script driving one companion directly locks the whole roster. **VERIFIED**.

Only the **host's** table is ever driven by the game's real AI/scripts for real decisions —
combat targeting, follow pathing, dialogue triggers, XP grants. Every other instance's copy of
each companion is a **puppet**: `PartyPresenceController` (client role, `DriveOne`) claims each
companion body every frame and forces its position/facing/animation/health to whatever the host
last published, resolved by **NPC roster slot**, never by object id, because ids drift across
instances but slot indices (0..8) are identical for the same save. **VERIFIED**
(`src/KotorCoop.Plugin/PartyPresenceController.cs` header comment; `party-reconciliation.md`
§"Recommended design", implemented and live-verified across many rounds).

### 1.2 The roster channel: one table, host effectively wins, joiner reconciles

`PartyRosterController` runs on every instance: it **publishes** this instance's own roster
(which of 9 slots are available, which 2 are seated) every frame, and **reconciles** its own
table to match whatever roster it is being told, by calling the real engine routines
(`AddPartyMember`/`RemovePartyMember`/`SpawnAvailableNPC`, later `AddAvailableNpcByTemplate` for
availability). **VERIFIED** (`src/KotorCoop.Plugin/PartyRosterController.cs` header;
`party-reconciliation.md` §"Roster sync"). This is a **level, not an edge** — the host restates
its complete roster every tick specifically so a dropped packet self-heals rather than leaving a
permanent split (`party-reconciliation.md` line 21-25).

Recruitment itself (which blueprint gets attached to a slot) is **not** replicated by this
channel — the party table's own availability field is a bare `1`, it never records which NPC
template was recruited (`+0x30`, `CSWPartyTable::AddNPC` writes the literal `1` at `0x00564350`).
**VERIFIED**. Availability sync (`dialogue_recruit_sync`, ON by default since 2026-09-18) works
around this by using the fixed, game-defined per-slot template mapping
(`PartyRosterTemplateTable`) rather than any wire payload for the blueprint itself.

**Never raw-poke the availability flag.** `PartyRosterController`'s own history records an
earlier build that wrote `+0x30` directly (`Odyssey.TrySetSlotAvailable`); `party-reconciliation.md`
Round 75 live-confirmed this is insufficient on its own — the subsequent spawn has nothing to
build from (`SpawnAvailableNPC` refuses, "failure 0", forever) because the real engine call the
game's own recruitment conversations use (`AddAvailableNPCByTemplate`) does more than flip a bit.
**VERIFIED, live-confirmed failure.** This is the same class of hazard the project's own
engine-write rule already names for the party-availability flag elsewhere (never raw-poke it;
recruit through the real call or the next warp/spawn breaks).

### 1.3 Party-select is host-only by design; the client's copy is closed, not driven

`CSWGuiPartySelection` (the retail party-composition screen) writes through the exact same
`SpawnNPC`/`AddMember` pair the roster channel already mirrors. **VERIFIED**
(`galaxy-map-and-party-select.md` §2.3). Because of that, the project's shipped design does not
give a joiner a second, independent picker at all: `PartySelectSuppressionController` force-closes
a client's own copy of the screen the moment it opens (the same panel-teardown idiom the native
server-browser panel uses), and lets `PartyRosterController`'s ordinary ~30-frame poll converge
the joiner's roster to whatever the host picked. **VERIFIED**
(`src/KotorCoop.Plugin/PartySelectSuppressionController.cs` header). The doc's own words: *"The
client never gets a real choice by design — nothing in `CSWPartyTable`/`CSWGuiPartySelection` is
per-peer, there is exactly one roster, and the host is already the authority for every other
roster change in this codebase."*

### 1.4 Equipment, XP, and damage: host-authoritative, joiner-facing channels bolted on

- **Equipment**: `CompanionEquipSuppressionController` blocks a **client** from even opening a
  companion's equipment page (closes it, posts a chat line "the host equips companions"); the
  host's own equip choices are published (`companion_gear_sync`, default read from source as
  `true` in the current `PartyPresenceController.cs`) and applied to the mirrored body through
  `Odyssey.EquipItemFromTemplateDirect`. **VERIFIED** (source read this pass;
  `companion-state-sync.md` §2 records the multi-round history: an early live FAILURE — the item
  landed on the player, not the companion — root-caused to a queued, never-armed action, fixed by
  calling `CSWSCreature::EquipItem` directly, then live-confirmed; a mid-session propagation gap
  and a 16-character-resref truncation bug were also found and (per the source-code default now
  reading ON) subsequently fixed).
- **XP**: a companion's `stats+0x068` is merged **upward by max**, never a delta, exactly like the
  PC's own progression channel, and for the identical reason (kill XP already agrees on both
  engines independently; only script-granted XP needs a wire channel). **VERIFIED**, live-tested
  (`companion-state-sync.md` §1).
- **Damage/heal while released**: `CompanionDamageController` is the piece most relevant to
  "sharing" — while `companion_local_combat` has released a companion to a joiner's own engine,
  that joiner's instance forwards cumulative damage the companion *received* locally, and heals
  it received, to the host, keyed by roster slot; the host turns the increase into a real,
  authoritative `EffectDamage` on its own copy. **VERIFIED** (source header read this pass,
  `companion-state-sync.md` §13/§14). This is the one existing precedent for "a joiner's engine
  temporarily has real, credited custody of a companion's combat state," and it already works.

### 1.5 The 2-player math, precisely (where the 2-companion cap actually bites)

With one host and one joiner:

- **Host's engine**: `PC(host) + companion + companion` — the table's normal 3-person cap, full.
  The joiner's own character occupies **zero** `CSWPartyTable`/`CSWParty` slots on the host's
  instance; it is a raw phantom spawn with no party-table interaction at all. **VERIFIED**
  (`galaxy-map-and-party-select.md` §2.5, confirmed by a repo-wide grep finding zero
  `CSWPartyTable` references in `PhantomController*.cs`).
- **Joiner's engine**: `phantom(host) + companion(mirrored) + companion(mirrored)` — the joiner
  sees exactly the same two companions, driven as puppets, plus the host rendered as a phantom.
  **VERIFIED**.

So today, with 2 players, there is really only **one** party of 2 companions in the whole
session — not "2 companions per player." The cap (`+0x00 == 2`) is party-wide on whichever
engine is asked, not per player, and every instance enforces it independently on its own local
copy. A design that wants "one companion each" for exactly 2 players fits inside this cap
numerically (2 companions total); a design that wants it for 3+ players does not — the engine has
no concept of more than 2 active companions on any single instance, and nothing about a
network layer can raise that ceiling without either giving up the "one canonical companion body
per slot" model entirely or accepting that some players get no companion.

### 1.6 Control-switching and dialogue: the fragile edges this design already lives next to

- **Control return** (`control-return.md`): a client who is directly *controlling* (Tab'd onto) a
  companion at connect time hit a real regression (locked onto the companion permanently) that
  took several rounds to root-cause and fix. The fix itself still carries an open item: whether
  `CSWParty::SetControlledCreature` (`0x00635480`, the engine's own control-switch primitive) can
  be called successfully from this process **at all** — every live attempt this project has made
  left the array unchanged, including a bounds-check-free sentinel call. **NEEDS-LIVE, still
  open.** The shipped fix (`Odyssey.RotateControlToFront`) works around this by reproducing the
  same end state through direct, id-only writes rather than calling the engine's function — a
  workaround, not a resolution of why the real function doesn't visibly do anything when called
  from here.
- **Dialogue that references party members**: a conversation runs its scripts on **one** instance
  at a time; every other instance spectates a token, it does not execute the dialogue
  (`dialogue-sync.md`, referenced throughout `party-reconciliation.md`/`companion-state-sync.md`).
  A companion who needs to speak or react inside a scene must be driven by whichever instance owns
  that conversation, which is exactly why `PartyFreezeRule` exists: `PartyPresenceController`
  backs off mirroring a companion the instant `pt_controlled_npc` shows a script has taken it
  under direct control (`party-reconciliation.md` §"presence mirror gets the same roster-freeze
  guard"). Any scheme that hands a companion to a joiner would still have to yield it back for
  every such scene, on top of `companion_local_combat`'s existing release/reclaim edge.
- **Story/galaxy-map scripts assume one party**: `galaxy-map-and-party-select.md` §3 documents
  several forced-solo/away segments (Leviathan capture, Taris swoop, turret sequences, Star Forge
  finale) that are scripted against a single PC's party with no established rule for what a peer
  mirroring a companion that vanishes mid-scene should see — flagged there as an open gap, not
  something this document resolves.
- **K1 has no TSL-style influence system** — nothing in this codebase's grep of `GetFirstPC`
  touches a companion-influence mechanic (checked this pass; K1 simply doesn't have one). What
  does exist and does matter: ordinary story/dialogue nodes and plot scripts that check party
  composition (`IsObjectPartyMember`-style checks, companion-specific banters and personal-quest
  triggers) run against **whichever engine's own `CSWPartyTable` the running script asks** — i.e.
  per instance. Any design where a companion is physically present in one instance's table but
  not another's risks those checks reading differently across instances. **INFERRED** — no
  specific banter/personal-quest desync has been live-observed in this codebase yet, but the
  mechanism (per-instance table, script reads it directly) is verified and the conclusion follows
  directly from it.

### 1.7 Save/adoption

A joiner adopts the host's staged autosave world wholesale — the memory note "Staged autosave is
the host's world" and this project's own `StagedSave` marker/pifo-presence rule mean the joiner's
party table, on load, **is** the host's party table, not a merge of two. **VERIFIED** (project
convention, cross-referenced in `party-reconciliation.md`'s adopted-world entries). Any per-player
"this companion is yours" concept is therefore **not** something the retail save format carries at
all — it would have to be new coop-only state, re-derived or re-sent at every reconnect and every
module transition, the same way the roster channel already is a wire-level construct rather than
a save field.

---

## 2. Options

### (a) "One each" — each player owns and drives one companion, mirrored to the others

**What it would mean**: player A's companion is authoritatively simulated on player A's own
engine (their AI decisions, their equipment, their HP), not the host's; player B's companion
likewise on player B's engine; each is mirrored outward to everyone else the way a companion is
mirrored today, just with the roles reversed for the piece that used to always be the host's.

**Engine feasibility — the honest split**:

- **Numerically**, 2 players + 1 companion each = 2 companions total, which fits inside the
  engine's own party-wide cap of 2 (§1.5). It does **not** extend to 3+ players; the cap is a
  hard engine ceiling per instance, not a project setting.
- **Architecturally**, this conflicts with the model every other channel in this codebase is
  built on: **one canonical world simulation** (the host's), everyone else a puppet renderer
  (client/server-halves memory rule; `PeerDamageController`'s own header: "the engine, not a
  health poke"). Making player B's engine the *authority* for a companion's position, AI
  decisions and death means:
  - **Kill XP** would stop agreeing for free. `DistributeExperience` (`0x005653A0`) loops
    whichever instance's own active-slot array is asked and grants XP to every member **in that
    instance's own table** — companion-state-sync.md's whole argument that kill XP already
    agrees without a wire channel depends on the companion having a live body **in the same
    engine that ran the kill**. A companion authoritative on a different engine than the one that
    landed the kill would need every kill-XP grant re-routed through a wire channel, not just the
    script-granted case the existing `companion_xp_sync` channel handles.
  - **Save authority** breaks. Only the host's world is ever staged/autosaved (§1.7); a companion
    whose "real" HP/inventory/position lives on a joiner's engine has no path into that save
    unless the joiner's state is snapshotted back to the host constantly — which is exactly the
    bidirectional-op-log shape this project's own shared-inventory lesson already rejected
    ("shared inventory must be host-snapshot + intents, never a bidirectional op-log").
  - **Dialogue and story scripts** (§1.6) check party composition against whichever instance is
    running the script — almost always the host's, since most story content triggers there. A
    companion not actually seated in the host's own `CSWPartyTable` would fail every such check
    on the host, silently skipping banters, plot gates, and scene participation that the
    unmodified game expects to always be able to see.
  - **`AddMember`/`RemoveMember`'s own precondition** (`+0xF0 != -1` freezes the roster) is
    per-instance; two engines independently trying to keep "their own" companion seated while the
    other engine's script-driven freeze windows come and go is a new, unstudied source of the
    exact "party table divergence never heals on its own" failure mode `PartyRosterController`'s
    own header names as the reason it publishes a level every frame, not an edge.

**What must change**: essentially a second full party-simulation authority path — a new
peer-owns-this-companion wire model (not the existing publish/reconcile level), a kill-XP
re-route, a save-merge story for a companion that was never in the host's own table, and new
rules for every dialogue/story script that currently assumes one party table. This is not an
extension of `PartyPresenceController`/`PartyRosterController`, it is a parallel authority model
next to them.

**Risks**: party table divergence (the two engines could each believe they own a companion the
other has also silently claimed, or lose track after a `+0xF0` freeze window); every risk in
§1.6 (control-switching, dialogue collisions) doubled, since now TWO instances can be a real
authority instead of one; save/adoption has no existing mechanism for a companion that isn't the
host's; transitions/`CreateParty` (the engine's own per-module rebuild of spawned bodies from the
availability table) run independently per instance and have already been caught producing
"available but unrebuildable" slots on an adopted world (`party-reconciliation.md` line 5570) —
a second authority makes this harder to reason about, not easier.

**Effort**: **VERY HIGH**. This is architecturally the biggest of the three options and cuts
against the project's own established host-authoritative-world pattern in every subsystem this
document read. Not recommended as a target to build toward (see §3).

### (b) Host owns both, a joiner can be "assigned" one to control/equip

**What it would mean**: the host's `CSWPartyTable` stays the single authority (nothing about §1.1
changes) — but the game grants a specific joiner elevated, semi-persistent rights over one seated
companion: its equipment, and its combat-targeting/behavior, without ever making the joiner's own
engine authoritative for the companion's canonical state.

**Engine feasibility**: this is a **direct extension of primitives that already exist and are
already live-verified**, not a new authority model:

- `companion_local_combat` already proves the core mechanic: when a companion, a joiner's player,
  and the one locally-fought enemy are all mutually close, the companion is released from
  mirroring and its attack is driven directly through `Odyssey.AttackTarget` — a call the project
  went out of its way to verify makes **no write to solo mode**, so it cannot destabilize a second,
  still-mirrored companion (`companion-combat-credit-plan.md` Round 83, live-confirmed Round 85:
  two companions released simultaneously, no cross-contamination). **VERIFIED, live.** Today this
  window is proximity-gated and transient (seconds); "assignment" would mean making a similar
  release window **deliberate and longer-lived** (a joiner opts in / the host grants it) rather
  than purely geometry-triggered.
  - Note precisely what "assigned" does **not** need to mean here: it does not need to hand the
    joiner real engine *control* (Tab-equivalent) of the companion at all. `companion_local_combat`
    never touches `pt_controlled_npc` or calls `SetControlledCreature` — it drives one action
    (`AttackTarget`) directly. An "assignment" built the same way sidesteps `control-return.md`'s
    entire fragile, partially-unresolved control-switch mechanism (§1.6) rather than depending on
    it.
- `CompanionDamageController` already proves the credit path: damage a companion takes and heals
  it receives while released flow back to the host, keyed by roster slot, and the host applies
  them as real, authoritative `EffectDamage`. **VERIFIED, live.** This is the exact shape
  "assigned custody of a companion's combat outcomes" already needs.
- Equipment is the one piece that would need new plumbing, since today equipment flows strictly
  one way (host → mirror; a client is actively blocked from its own companion equip screen,
  §1.4). A joiner "assigned" a companion asking to re-equip it would need an **intent** the host
  applies (matching the shared-inventory precedent: "host-snapshot + intents, never a
  bidirectional op-log") — e.g. the joiner's client posts "equip X in slot Y on companion at
  roster slot N," the host validates and calls the real equip primitive
  (`EquipItemFromTemplateDirect`) itself, then republishes gear the existing channel already
  syncs. This is new code, but it is new code **in the same shape** as machinery the project
  already ships (a request/apply pair, not a new authority).

**What must change**: a new "assignment" concept (which joiner, if any, currently has elevated
rights over which of the 2 seated companions) that is session-scoped state, not save state
(§1.7) — most naturally piggybacked on the existing roster-publish level so it self-heals the
same way; an extended, deliberate version of the `companion_local_combat` release window (opt-in
rather than pure-geometry) with its own throttle/reclaim rules; a new equip-intent channel
(host-applied) if joiner-driven re-equipping is wanted, otherwise equipment can stay exactly as
it is (host equips, everyone sees it) and "assignment" is scoped to combat behavior only.

**Risks**: extending `companion_local_combat`'s release window from "seconds, tightly
proximity-gated" to "minutes, player-toggled" changes the exposure profile of a mechanism whose
own live-test history includes a real warp-transition crash discovered along the way
(`companion-combat-credit-plan.md`, round 85) and multiple rounds chasing follow-AI staleness —
none of that is disqualifying (both were root-caused and the crash was fixed), but a
longer-lived release window means more real time spent in the state that produced them, which is
new exposure, not a repeat of already-covered ground. Party table divergence risk is low relative
to (a) because the table itself never gains a second writer — only *who is allowed to issue one
kind of action* on an already-host-owned body changes. `+0xF0`'s roster-freeze precondition still
applies exactly as today (an assigned joiner's control attempt during a script-owned freeze window
must be refused, same as any other write attempt).

**Effort**: **MEDIUM**. Reuses `CompanionLocalFightRule`/`CompanionDamageController`'s proven
shape; the new work is turning a geometry-triggered burst into an opt-in, session-scoped state,
plus (optionally) an equip-intent channel.

### (c) Party-select screen per player, picking from a shared roster, with conflict resolution

**What it would mean**: instead of `PartySelectSuppressionController` closing a joiner's copy of
`CSWGuiPartySelection` outright (§1.3), each player gets some UI to express a preference for who
fills the 2 active seats, with a rule for what happens when preferences conflict or exceed the
cap.

**Engine feasibility**: the engine has **no per-peer concept here at all** —
`CSWGuiPartySelection::AcceptParty` writes through the single `CSWPartyTable` every player's
engine independently maintains its own copy of; there is exactly one "the party" per instance,
and `PartyRosterController` is already built to converge every instance to whatever the **host's**
copy says (§1.2/§1.3). This means a "party-select per player" feature is necessarily a
**negotiation layer sitting in front of the existing, unmodified roster-reconciliation
machinery**, not a new engine-facing capability:

- The **retail screen itself cannot be the per-player UI** — `PartySelectSuppressionController`'s
  own reasoning (§1.3) still holds: giving a joiner a real, independent `AcceptParty` would just
  produce two different tables that `PartyRosterController` then has to reconcile by converging
  one onto the other, i.e. exactly the "party seat gets it wrong" failure the roster channel
  already exists to correct, but self-inflicted and continuous instead of caused by an aborted
  conversation.
  - Note the fallback the existing doc already flags but did not build,
    §2.4 item 4 ("auto-confirm with the host's picks" by replaying `SetSelected`/`AcceptParty`
    synthetically) — that is a **replication** mechanism (making the client's screen show what
    the host chose), not a **negotiation** one; it does not by itself give a joiner an independent
    vote.
- A real per-player "pick" therefore has to be a **new, synthetic UI/chat-style channel** (proposals
  travel as data, not through the retail panel), with the host's own roster-publish staying the
  single source of truth the way it already is. This is closer in shape to a vote/request system
  than to a screen: each player proposes a companion for one of the 2 seats; the host (or a fixed
  tie-break rule, e.g. first proposal wins, or the host's own pick always wins) decides; the
  decision is realized through the **existing** `AddPartyMember`/`RemovePartyMember`/
  `SpawnAvailableNPC` calls `PartyRosterController` already knows how to drive — no new engine
  call is needed for the resolution step itself.

**What must change**: a new proposal/vote wire message (small — "player X proposes companion at
slot N for seat 0/1"); a resolution rule (host-wins is simplest and matches every other
host-authoritative decision in this codebase; a fairer scheme needs explicit tie-breaking); a UI
surface for the proposal — most naturally the existing chat overlay/console rather than a new
native panel, given `PartySelectSuppressionController`'s own conclusion that a synthetic native
panel is extra moving parts for no proven gain (§2.4 of `galaxy-map-and-party-select.md`). No
change to `PartyRosterController`'s reconciliation logic, `CSWPartyTable`, or any engine call.

**Risks**: the 2-companion cap makes conflicts common and visible by design (two players who each
want a *different* third companion when both seats are already full will always produce a loser);
`+0xF0`'s roster-freeze precondition still has to gate proposal resolution exactly like it gates
every other roster write today (a proposal that lands while a script has one companion under
direct control must queue or be refused, not silently drop); this option touches **no** engine
write path that is not already shipped, so it carries materially less risk than (a) or (b) — its
risk is almost entirely UX (players disagreeing) rather than engine-state divergence.

**Effort**: **LOW-MEDIUM**. No new engine primitive; the work is a small protocol message, a
resolution rule, and a chat/console-level UI. This is the cheapest of the three options to
prototype and the safest to abandon if playtesting doesn't like it.

---

## 3. Recommendation and staged plan

**Recommendation: do not build (a).** It requires a second real simulation authority next to the
host's, which conflicts with the host-authoritative-world pattern every other subsystem in this
project depends on (client/server-halves rule, the shared-inventory host-snapshot-not-op-log
lesson, `DistributeExperience`'s per-instance kill-XP argument, save/adoption being wholesale from
the host). Every other option in this document reuses that pattern; (a) is the only one that asks
to break it, and nothing in the current codebase gives a concrete, lower-risk path to doing so.

**Start with (c), then layer (b) on top if the group wants companions to feel more "theirs" in
combat specifically.** In order:

1. **Smallest safe step — a chat-level "dibs" convention, no code.** Before writing anything,
   this can be tried today with zero engineering: players say in the existing coop chat which
   companion they consider "theirs" and the host picks accordingly through the unmodified retail
   screen. This costs nothing, proves nothing technically, but tells you in one session whether
   "one each" is something players actually want the ritual of, before any of the below is built.
2. **(c), the proposal/resolution layer.** A new small wire message plus a resolution rule
   riding entirely on the roster channel that already exists and already self-heals. No new
   engine write. If shipped, this is the first real feature step — it makes "who gets which
   companion" a first-class, negotiated decision instead of a silent host-only pick, without
   touching a single line of the mirroring/reconciliation machinery that everything else here
   depends on.
3. **(b)'s combat-assignment half**, built directly on `CompanionLocalFightRule` and
   `CompanionDamageController` — the two pieces of this whole area that are already live-verified
   and already prove the "a joiner's engine can meaningfully act for a companion, credited, host
   as final authority" shape. Turn the existing proximity-triggered release into an opt-in,
   session-scoped "assignment" a player can request and the host can grant/revoke, still gated by
   the same `+0xF0` roster-freeze and the same "no write to solo mode" discipline the shipped
   feature already follows.
4. **(b)'s equipment half, only if step 3 lands clean.** An equip-intent channel (joiner requests,
   host validates and applies) in the same host-snapshot-plus-intents shape the shared-inventory
   design already uses. Lower priority than combat assignment because today's host-only equip
   model (§1.4) already works and is live-verified; this only matters if players specifically want
   to gear up "their" companion without asking the host to do it.

**Do not attempt** a true control handoff (real Tab-equivalent `pt_controlled_npc`/
`SetControlledCreature` custody) as part of any of the above. `control-return.md`'s own open item
— whether the engine's own control-switch primitive can even be called successfully from this
process — is unresolved today independent of this feature, and making persistent player-facing
control ride on it would multiply exposure to a code path this project has not yet gotten to work
reliably.

### What would need live proof before any of this ships

1. **For step 2 (proposal/resolution layer)**: whether a proposal that arrives while `+0xF0`
   (roster freeze) is set is correctly deferred/refused rather than silently lost — the existing
   roster channel already handles this for ordinary recruitment, but a player-initiated proposal
   racing a script-owned freeze window has never been specifically exercised.
2. **For step 3 (combat assignment)**: whether extending `companion_local_combat`'s release
   window from seconds to minutes reproduces, at a longer duration, any of the party-presence
   staleness or follow-AI stall failure modes `party-reconciliation.md`'s many combat-credit
   rounds already root-caused at the shorter, proximity-triggered duration — none of those are
   known to be duration-sensitive, but none were tested at a longer duration either.
3. **For step 3, specifically the reclaim path**: whether an assignment being explicitly revoked
   (player toggles off, or walks out of any residual proximity requirement kept for safety)
   reclaims the companion as cleanly as `companion_local_combat`'s own proven proximity-loss
   reclaim does, or whether a deliberate, player-initiated release needs its own distinct
   log line and test — the existing reclaim has only ever been exercised by geometry changing,
   not by an explicit request.
4. **For step 4 (equip intent), if built**: whether a request/apply round trip for equipment adds
   meaningfully more latency or failure surface than the existing host-push-only channel already
   has (§1.4's own live history already found and fixed a mid-session propagation gap and a
   16-character-resref truncation bug in the push-only direction — an intent channel adds a second
   direction that has never been tested at all).
5. **Independent of all of the above**: whether `CSWParty::SetControlledCreature`
   (`0x00635480`) can be called successfully from this process — `control-return.md`'s own open
   item. This blocks nothing in this document's recommended path (none of it needs real engine
   control custody), but it should stay resolved-or-avoided rather than accidentally depended on
   later, since it is the one primitive every option above deliberately routes around.
