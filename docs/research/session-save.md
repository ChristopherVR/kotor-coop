# Mid-session save/load (design, 2026-08-21)

Roadmap item 14. `world-adoption.md` makes a joiner's FIRST entry into a session work — the
host's save is served, the joiner's own character is carried into it. Nothing addresses what
happens the SECOND time: resuming tomorrow, or either player saving/loading mid-session today.
This document is that design. **No code was written and no game was run for this document.**

Tag key as elsewhere in this tree: **VERIFIED** · **INFERRED** · **NEEDS-LIVE**.

## 1. What the client loses today, precisely

`world-adoption.md`'s own "trade, stated plainly" is the starting fact, quoted directly:

> the joiner gives up the world they were playing, not their character. Party, inventory,
> companions, globals and journal come from the host's save, because in co-op those are shared
> state. **Their own save on disk is never written** — only the autosave slot and `Temp\` — so
> leaving the session and loading their own game costs nothing.

That trade is correct for the FIRST join, and it is exactly what breaks on the second one, for a
reason `disconnect-reconnect.md` already states as settled fact: **"World transfer / adoption is
a startup path, not a session one. A mid-session reconnect never re-stages a save, which is
correct."** Correct for reconnect (the world is still running; nothing needs re-staging). Wrong
for "resume tomorrow": nothing ever writes the joiner's own save file, so the character that
walked around this session — gained items, leveled up, spent credits, shifted alignment, changed
equipment — has **no representation anywhere on the joiner's disk.** The joiner's own save
directory is exactly what it was before session 1 started.

Concretely, session-scoped progression on the client's own character is either:

* **Shared state, already merged, and safe** — XP (max-merge, `shared-state-audit.md` §3),
  journal (`JournalStore`), globals (`EventFlagStore`), gold (ledger). These live on the HOST's
  save, which the host does write when they save. A client re-adopting the host's save next
  session gets these correctly, same as the first join.
* **Per-player state that lives ONLY in the client's own live `CSWSPlayer`/`CSWSCreatureStats`
  and is never written anywhere** — inventory, equipped gear, alignment (per `PlayerIfo.cs`'s
  own remark: `AdjustAlignment` is per-character stat storage, read directly off the character
  the join flow extracted as `Temp\pifo.ifo`). **This is what is lost.** The client's next
  session re-adopts the host's world from scratch — a FRESH extraction of whatever character is
  sitting in the client's own `saves\` directory right now, which is still last session's
  starting point, because nothing wrote a new one.

`PlayerIfo.TryExtractPlayer`/`BuildPifo` (`src/KotorCoop.Memory/PlayerIfo.cs`) already prove the
mechanism that fixes this exists and works: it is the SAME code that lifts a character out of a
save directory and re-emits it as a standalone pifo at join time. What is missing is a moment,
during or at the end of a session, where the CLIENT's own `saves\` directory is updated FROM
their live in-session character, so the next join's `TryExtractPlayer` has fresh material to
work from.

## 2. Design (a): "session save" — write the client's progressed character to their own slot

### 2.1 What already exists to build on

* `PlayerIfo.TryExtractPlayer(saveDirectory, out player, ...)` reads a `Mod_PlayerList[0]`-shaped
  `GffStruct` out of ANY save directory's `SAVEGAME.sav → <module>.sav → module.ifo`. It does not
  care whether the save directory is the host's staged world or the client's own — same shape.
* The reverse direction — reading the LIVE character straight out of the running engine, not
  from a save file — is what the join flow's OTHER half needs anyway (the host has to produce a
  `module.ifo`-shaped player entry from its OWN live save to serve to a joiner; `WorldTransfer`
  already does this for the whole save directory, not per-character). The gap is specifically:
  nothing today captures the CLIENT's own live character (not the host's) into a
  `GffStruct`/save-shaped form while a session is running.
* `SaveDigest.TryComputeSaveIdentity` already computes a stable content digest for a save
  directory, used today by the compat gate; the same digest is the natural "did anything change"
  check to avoid rewriting the client's save when nothing did.

### 2.2 The mechanism

The clean way to get the client's live character into save-file shape is **the engine's own
save code**, not a hand-rolled memory-to-GFF serializer:

1. **Let the client SAVE NORMALLY**, into their own local save slot (autosave or a numbered
   manual slot — `kotor-coop-save-slot-off-by-one.md`'s finding about the engine's list skipping
   the autosave slot is a UI/selection concern, not a blocker to writing one directly the way
   `MenuLoadController` already drives load without a human clicking three menus). This produces
   a real `SAVEGAME.sav` in the CLIENT's own `saves\` directory, containing the client's
   character exactly as the engine would write it for a single-player save — full fidelity, no
   copy risk, because it is the retail save path, not a reimplementation of it.
2. **The catch, and it is the whole reason this needs design rather than "just let them save":**
   a session client is standing in the HOST's world (adopted, per `world-adoption.md` §1). A
   plain save from there would save the HOST's module/world state under the CLIENT's own save
   slot — which is nearly right for `PartyRosterController`/inventory purposes (the client DOES
   want their own subsequent adoption to carry forward from wherever this session ended) but
   would also serialize host-owned world objects (doors, containers, other NPCs) that the next
   session's `world-adoption.md` flow is going to overwrite with a FRESH copy of the host's save
   anyway. This is not actually harmful — the next join re-stages the whole world over this
   save's autosave slot regardless, per the existing flow — but it means a session-save's WORLD
   half is throwaway and only its CHARACTER half matters.
3. **So "session save" = normal engine save into the client's own slot, and `PlayerIfo` reads
   back out of it exactly as it already reads out of ANY save directory.** No new extraction
   code. The save the engine just wrote becomes the input to the SAME `TryExtractPlayer` path
   that already runs at every join — the only change is WHEN it runs: once at the end of (or
   periodically during) a session, not only at join time.
4. **Where the save fires from.** Two options:
   - **Player-driven**: the ordinary in-game Save menu, unmodified. Simplest, but relies on the
     player remembering to save before quitting — the exact human-reliability problem "resume
     tomorrow" framing implies should not exist.
   - **Session-driven**: the agent triggers a save automatically at session teardown (client
     disconnect, `Bye`, or process exit) via the same "replay the engine's own code path, no
     input injection" technique `MenuLoadController` already uses for loading — a
     `SaveController` built the same way, calling the SAVE half of `CSWGuiSaveLoad` instead of
     the load half. **NEEDS-LIVE / new RE**: `MenuLoadController`'s own header already names
     that its replayed branch is specifically `CSWGuiSaveLoad::LoadGame`'s main-menu autosave
     branch; the SAVE branch (`CSWGuiSaveLoad::SaveGame` or equivalent) has not been traced by
     this or any doc in this tree and would need the same call-order archaeology
     `MenuLoadController`'s comments record for load (panel construction, the loopback-session
     dance, the load-screen presentation hand-off) before it could be replayed safely.
   - **Recommended**: ship player-driven first (zero new RE, uses the existing Save menu exactly
     as retail does — the only new code is WHEN to prompt/remind, which is a UI nicety, not an
     engine write) and treat session-driven auto-save as a follow-up once a `SaveController` is
     independently justified (it likely will be, for reasons unrelated to this document — an
     unattended host crash losing session progress is a much bigger problem that argues for the
     same mechanism on its own).
5. **What actually needs new code**: nothing engine-side. The new code is entirely in
   `PlayerIfo`-adjacent orchestration: after a save happens (detected however §2.2 item 4
   resolves), extract the character with the existing `TryExtractPlayer`, and — this is the one
   genuinely new piece — persist it somewhere the NEXT join can find deterministically. The
   simplest answer: **do nothing extra at all.** The client's own `saves\` directory, written by
   the engine's own save code, already IS "somewhere the next join can find it" — `TryExtractPlayer`
   already takes a `saveDirectory` argument and the next session's join flow already has to pick
   which of the client's saves to adopt-FROM (today it presumably uses their most recent /
   selected save the same way it always has). No sidecar file is needed; the existing save slot
   already does the entire job once something writes to it during the session.

### 2.3 What this design explicitly does NOT do

It does not attempt to make the CLIENT's own save directory a faithful copy of the shared WORLD
(doors, containers, other players' state) — that would duplicate `WorldTransfer`'s entire job for
no benefit, since the next session's `world-adoption.md` flow discards it anyway in favor of a
fresh host-served world. Session save is character-only by design, exactly mirroring what
`world-adoption.md` §"the trade, stated plainly" already established as the shape of this whole
feature: the client brings the character, the host brings the world, in both directions.

## 3. Design (b): the HOST loads a save mid-session

### 3.1 The engine fact that decides this

`world-adoption.md` §8 item 2 already named this as "the largest open design question, not just
an untested path," and gave the load-bearing engine fact: **serving a world while the host is
standing in it serves a STALE world** — `WorldTransfer.ServeAsync` reads the host's save
DIRECTORY off disk, which only reflects reality up to the host's last save. A host who has been
playing for an hour since their last save and then triggers a load (of ANY save, including their
own most recent) is, from the ENGINE's perspective, doing exactly what a single-player load
always does: tearing down the current world and module state and replacing it wholesale.

`transition-window-races.md`'s four defects are the second load-bearing fact, and they all share
one shape stated in that document's own words: **"state read in one world, acted on in another."**
Every one of them was triggered by an ordinary MODULE TRANSITION — the mildest kind of "the world
under us just changed" event the engine has — and still found four real crash/desync bugs across
`EncounterController`, `EnemyPresenceController`, `PartyPresenceController`, `PartyRosterController`
and the agent's own player-scan. A save LOAD is a strictly bigger discontinuity than a module
transition (object ids reset the same way, but ADDITIONALLY every piece of world state reverts
to whatever the save says, not just the module identity) — so every failure mode
`transition-window-races.md` catalogued applies to a mid-session host load at least as strongly,
and the fixes it shipped (`Odyssey.GetPlayerCreaturePointer()` instead of cached pointers,
`SharedBlockView.ClearAgentWorldInputs()` on epoch invalidation, tag-based census exclusion,
live module-label reads) are exactly the machinery that would need to hold up against it.

### 3.2 Recommendation: treat it as a module transition + full world re-adoption, not a special case

**Reuse the join path, do not build a new one.** The reasoning:

1. **The failure shape is identical to what module transitions already produce**, and that path
   is the one with a live-verified track record (`transition-window-races.md`'s "verified live,
   repeatedly" for the fixes; `world-adoption.md` §7's live pass for the join flow itself).
   Building a SEPARATE "mid-session load" code path multiplies the surface that needs its own
   live verification for no benefit, when the existing path already handles "the world just
   changed out from under every client" as its core job.
2. **The compat/digest machinery already exists to detect it and drive it.** `SaveDigest`
   computes a save-identity digest; `SaveDigestCache` (in `SaveDigest.cs`) already recomputes it
   periodically per the pump architecture already in place, not just at join. A host-side load
   changes the save identity digest (a fresh load reads a materially different save state) —
   this is the SAME signal `ContentCompatibility.ApplyWorldMismatch` already uses to REFUSE a
   join whose saves disagree; running the identical comparison mid-session, when the host's own
   digest changes out from under an established session, is a natural re-use rather than a new
   mechanism.
3. **The concrete flow**: host triggers a load (menu, in the field, however). The host's own
   `SaveDigestCache` notices its digest changed (this already happens passively — it is the same
   check the join gate runs). The host's session broadcasts "world epoch changed, re-adopt" —
   this can literally be the same admission/adoption RPC `world-adoption.md` §4's diagram already
   shows (`WorldTransfer.FetchAsync`/`ServeAsync`, `PlayerIfo.TryStagePlayer`,
   `PluginMenuLoader.TryRequestLoad(adoptedWorld: true)`), run again against every currently
   connected client rather than only a newly joining one. Each client:
   - Extracts ITS OWN current in-session character first (§2's session-save mechanism — this is
     exactly why session save should exist independently of "resume tomorrow": a host-triggered
     mid-session reload is the other consumer of the exact same "capture my live character
     before the world under me changes" operation), THEN
   - re-adopts the host's freshly-served (post-load) world, carrying that just-captured character
     back in via the same `TEMP:pifo` mechanism `PlayerIfo`/`MenuLoadController` already drive.
4. **What is new, versus what is reused**: nothing about the TRANSPORT or the PLAYER-CARRYING
   mechanism is new — both are `world-adoption.md`'s existing machinery, run a second time. What
   is new is (a) the TRIGGER — detecting a host-side load mid-session and broadcasting the
   re-adopt request, and (b) surviving the transition-window race class on every controller that
   caches world-scoped state, which `transition-window-races.md` already named as a concrete,
   bounded list of consumers to check (`EncounterController`, `EnemyPresenceController`,
   `PartyPresenceController`, `PartyRosterController`, the agent's own player-scan) — these were
   fixed for MODULE transitions; a host-load transition needs the SAME consumers re-verified
   against the SAME failure shape, not a new audit from scratch.
5. **The alternative — refuse the load outright with an on-screen line** — is the safe fallback
   if re-adoption's live pass finds the transition-window race class does not fully cover a save
   load (a save load can, unlike a module transition, change which MODULE is even loaded, which
   is a strictly larger jump than anything `transition-window-races.md` measured). Recommend
   shipping the refusal first (`RefusalReason`-shaped, cheap, and it is exactly the trade
   `ApplyWorldMismatch` already makes when saves disagree at JOIN time — refusing a divergent
   world is already this codebase's existing default posture, not a new one to invent) and
   upgrading to live re-adoption once the transition-window class is confirmed to hold for a full
   save load, not merely a module change within the SAME save.

### 3.3 Why NOT "the host's client peers just keep playing in the stale world"

Not viable even as a stopgap: `docs/research/enemy-presence-sync.md`,
`docs/research/party-reconciliation.md` and the container-loot/journal/flag channels all key
their cross-instance identity on **object ids assigned in GIT load order for the CURRENTLY
loaded save** (per `world-adoption.md` §2's own settled fact: "the engine mints a fresh id on
load"). A host load changes the host's ids; a client that has not also reloaded is comparing
against ids that no longer mean anything on the host's side. This is not a graceful-degradation
case — it is exactly `ContentCompatibility.RefusalReason.SaveGame`'s failure mode, mid-session
instead of at join, and every world channel silently sends garbage across mismatched ids until
something notices. Refusing or re-adopting are the only two coherent options; "ignore it" is not.

## 4. Design (c): a CLIENT loads a save mid-session

### 4.1 The detection signal

Two candidates, and they are not mutually exclusive:

1. **The save/load hook itself**, if/when a `SaveController`/load-detection mechanism exists
   (§2.2 item 4's follow-up, or `MenuLoadController`'s existing in-session load path if a client
   ever drives a load the same way a joiner's adoption does). Direct and immediate, but depends
   on new plumbing existing first.
2. **The module+object-id fingerprint the compat check already computes** — `SaveDigest`'s
   per-module GIT digest (`TryComputeCurrentModuleGit`/`TryComputePristineModuleGit`,
   `SaveDigest.cs`) plus `Odyssey.TryGetWorldEpoch` (`src/KotorCoop.Plugin/Odyssey.cs`), which
   **already exists precisely for this shape of problem**: it "combines the server object array
   pointer, which `UnloadModule` replaces, with a hash of the module name string," explicitly
   built to answer "did the world under me just change" for module transitions. A client-side
   load — even a same-module one — is exactly the kind of discontinuity this epoch is built to
   catch: `UnloadModule` runs, the server object array is replaced, and the epoch changes whether
   the reload landed on the same module name or a different one. **This needs no new engine
   read**: `TryGetWorldEpoch` is already polled continuously by every controller
   `transition-window-races.md` fixed, for exactly this "did everything I'm caching just become
   invalid" question.

Recommend (2) as the primary signal — it requires no new hook, reuses machinery already proven
live across four real bugs, and naturally fires for the mid-session-load case because a load IS
a bigger version of the same discontinuity the epoch already tracks. Layer (1) on top only if/when
save-hook plumbing exists for other reasons (§2.2 item 4's `SaveController`).

### 4.2 The response: re-join automatically

Once a client-side epoch change is detected mid-session (as opposed to at startup, where it is
just "the world finished loading" and not a re-join trigger — the two cases are distinguished by
whether a session was already established, exactly the same distinction
`disconnect-reconnect.md` draws between "the easy failure" and a real mid-session event):

1. Treat it structurally like the disconnect-recovery path `disconnect-reconnect.md` already
   built and live-verified, NOT like a fresh join: the client's peer identity, nonce and
   cumulative channel state (damage ledger, progression ledger) should all be **retained**, the
   same way a network blackout retains them — a save load is not a new player showing up, it is
   the SAME player's world going stale for a few seconds. `disconnect-reconnect.md` §2.1's whole
   "an identity must live as long as the counters keyed on it, and those are scoped to the
   module, not to the connection" rule applies directly: a client-side load does not reset any of
   the module-scoped counters on the HOST side (they are the host's own tables, untouched by a
   CLIENT reloading), so there is no reason to mint a new identity for it.
2. The client re-runs `world-adoption.md`'s adoption flow against the CURRENT session (host's
   live save, re-served fresh — not the stale copy from before, since the host has kept playing).
   This is the same machinery §3.2 recommends for the host-load case, run from the other
   direction: the client is the one whose world just became wrong, so the client re-adopts.
3. **What the client's own load actually loaded is thrown away**, exactly the same way
   `world-adoption.md` §"the trade" already establishes for the very first join: whatever save
   the client picked from their own menu is a character-and-world snapshot from SOME earlier
   point; only the CHARACTER half survives (via the same `PlayerIfo.TryExtractPlayer` path,
   reading the client's own just-loaded save the moment it lands, before the re-adopt overwrites
   their world with the host's), and the world half is discarded in favor of re-adopting the
   host's live world. This is consistent, not a special case: it is the exact same trade the
   player already implicitly accepted by choosing to co-op in the first place.

## 5. Engine facts this design rests on, versus assumptions

**VERIFIED / already established elsewhere in this tree, cited rather than re-derived:**

* A saved PC's `ObjectId` is `0x7FFFFFFF`; the engine mints a fresh id on every load
  (`world-adoption.md` §2, two real saves).
* `LoadCharacterFromIFO(-1)` reads `TEMP:pifo`, and `PlayerIfo` already builds that file from
  ANY save directory's `Mod_PlayerList[0]` (`PlayerIfo.cs`, `world-adoption.md` §1/§4).
* `LoadPCAutoSave` truncates a pifo-less staged save's `TEMP:pifo` to zero bytes, requiring the
  carried-character stash-and-restore `PlayerIfo`/`MenuLoadController` already implement and
  live-verified (`world-adoption.md` §7's "THE BUG" section).
* Module transitions produce a documented, fixed class of "state read in one world, acted on in
  another" bugs across five named consumers, all live-verified fixed
  (`transition-window-races.md`).
* Disconnect/reconnect already solves "an identity must live as long as the counters keyed on
  it," live-verified across ten real defects (`disconnect-reconnect.md` §2, §3's measured
  recovery sequence).
* `Odyssey.TryGetWorldEpoch` already exists, already combines the server object array pointer
  with a module-name hash, and is already polled continuously by the controllers
  `transition-window-races.md` fixed.
* `SaveDigest.TryComputeSaveIdentity`/`SaveDigestCache` already compute and cache a save-identity
  digest used by the compat gate.

**INFERRED (reasoned in this document, not directly observed):**

* That a host-triggered load produces the SAME shape of discontinuity as a module transition,
  only larger. Strongly supported by `world-adoption.md` §2's id-reminting fact (a load always
  reassigns ids the same way a transition does) but the "only larger" claim — specifically,
  whether every consumer `transition-window-races.md` fixed is sufficient for a save load that
  ALSO changes which module is loaded, versus only a module-transition's module-to-module jump —
  is not directly measured.
* That `TryGetWorldEpoch` changes on a client-side same-module reload, not only a module-name
  change. The epoch's own doc comment says it combines the object-array pointer (which
  `UnloadModule` always replaces) with the module-name hash — the object-array-pointer half
  should change on ANY reload including same-module, but this specific case (same module name,
  reloaded) has not been measured.
* That session-save's character-only scope (§2.3) is sufficient — i.e., that nothing else per-player
  and session-scoped exists beyond inventory/equipment/alignment. Feats, skills, force powers and
  other `CSWSCreatureStats` fields should fall under the same "lives in the character, carried by
  the same pifo mechanism" umbrella, but this document did not enumerate `CSWSCreatureStats`
  field-by-field to confirm nothing else needs separate handling.

## 6. Risks

1. **§3.2's biggest open risk**: whether re-adoption survives a host load that changes the
   MODULE, not just the save state within one module — `transition-window-races.md`'s fixes were
   proven against module-to-module travel, which is a similar-shaped but not identical event to
   "the host reloaded a save that happens to be in a different module." Needs a live pass before
   trusting the reuse claim in §3.2 fully.
2. **§2.2's session-save relies on the player-driven Save menu by default**, which means a host
   crash or ungraceful client exit still loses session progress — exactly the gap that motivates
   eventually building the session-driven auto-save follow-up. Flagged, not solved, in this
   document.
3. **§4.2's "throw away what the client loaded" UX** may surprise a player who intentionally
   loaded an OLDER save hoping to diverge from the shared session — this design assumes that is
   never the intent inside an active co-op session (consistent with `world-adoption.md`'s
   existing framing that co-op state is inherently shared), but it has not been validated against
   real player expectations, only against engine facts.
4. **The compat-digest re-use in §3.2/§4.1 assumes `SaveDigestCache` recomputes fast enough** to
   be a useful mid-session trigger, not just a join-time check. `disconnect-reconnect.md` §4.1
   already flags the counter-vs-fingerprint distinction as an open weakness for a related channel
   (container loot); the digest here is a different mechanism (content hash, not a counter) but
   its recompute LATENCY specifically for a mid-session trigger has not been measured.

## Recommended build order and rough effort

1. **Session save, player-driven (§2, item 4's first option).** Smallest: zero new engine RE
   (the Save menu and `PlayerIfo.TryExtractPlayer` both already exist and already do everything
   needed), the only new work is wiring "which save directory does the next join's adopt-FROM
   step read" to pick up whatever the player last saved during a session — likely already true
   with no code change if that selection was already "the player's most recent save." Effort:
   small (verification + maybe a UI reminder), most likely under a day of focused work plus a
   live pass.
2. **Client-side mid-session reload detection + auto-rejoin (§4).** Reuses
   `TryGetWorldEpoch` (already polled) and the disconnect-recovery identity-retention pattern
   (already live-verified for a different trigger). Medium: the detection is nearly free: but
   distinguishing "world just finished loading at startup" from "world changed mid-session" needs
   care, and the re-adopt-while-retaining-identity path is new integration between two proven
   pieces (`world-adoption.md`'s adopt flow + `disconnect-reconnect.md`'s identity retention),
   not either alone.
3. **Host-side mid-session load: refusal first (§3.2 item 5).** Small: a `RefusalReason`-shaped
   message reusing the existing compat-refusal pattern. Ship this before attempting the harder
   re-adoption path, since it converts an undefined "stale world, silent divergence" state into a
   defined, safe one immediately.
4. **Host-side mid-session load: full re-adoption (§3.2 items 1-4).** Largest: depends on item 3
   shipping first as the safety net, needs the transition-window consumer list re-verified
   against a save-load discontinuity (not just module transitions), and needs the broadcast
   trigger built. Should follow a dedicated live-test pass confirming the transition-window class
   actually covers this case (§6 item 1) before it replaces the refusal from step 3.
5. **Session-driven (unattended) auto-save.** Deferred, per §2.2 item 4 — likely justified on its
   own merits (crash recovery) independent of this document, and should reuse whatever
   `SaveController` that need produces rather than being built bespoke here.

## What could not be determined from the repo

- Whether `CSWGuiSaveLoad`'s SAVE branch (as opposed to `MenuLoadController`'s already-traced
  LOAD branch) has been reverse-engineered anywhere in this tree. A grep found no such document;
  §2.2 item 4 flags this as new RE work if session-driven auto-save is ever built.
- Whether `TryGetWorldEpoch` empirically changes across a same-module client reload (only
  reasoned from its doc comment, not measured — §5's INFERRED list).
- Whether anything beyond inventory/equipment/alignment is per-player, session-scoped, and
  currently lost — this document checked `CSWSCreatureStats`-adjacent state only at the level
  `PlayerIfo`'s existing comments already describe, not field-by-field.
- Live timing for `SaveDigestCache`'s recompute latency, needed to judge whether §3.2/§4.1's
  digest-based mid-session trigger is fast enough to feel responsive versus needing a dedicated
  save/load hook instead.

## Phase 1 shipped (2026-08-21)

Roadmap item 14 phase 1: item 1 (client character persistence) and item 2 (host mid-session
load refusal) in full; item 3 (client mid-session load auto-rejoin) deliberately NOT
implemented — see "What phase 1 does not do" below. **No engine RE was done for this pass**
— everything below is orchestration over what §1-§6 above already established exists:
`PlayerIfo.TryExtractPlayer`/`BuildPifo`, `WorldAdoption.TryAdoptAsync`'s existing fetch/stage
order, and — the one piece this pass leaned on hardest — `IGameBridge.SaveIdentityDigest`
(backed by `SaveDigestCache`, already polled every heavy pump and already carried on the wire
in `WorldDigest` for the join-time compatibility gate). Tag key as elsewhere in this tree:
**VERIFIED** · **INFERRED** · **NEEDS-LIVE**.

### What shipped

**Item 1, session save (design §2).** `src/KotorCoop.Session/ClientCharacterPersistence.cs`:
a sidecar pair, `saves\kotorcoop-character.pifo` (exactly `PlayerIfo.BuildPifo`'s bytes —
no new serialization format) plus `saves\kotorcoop-character.json` (host save identity +
UTC capture time). Both writes are temp-file-then-rename (a single filesystem rename is
atomic on both Windows and POSIX); every read tolerates the sidecar being absent, half-written
or corrupt — a bad sidecar is "nothing to prefer," never an exception. The preference rule
(`ShouldPreferSidecar`) is pure: a valid sidecar strictly newer than the client's own save
wins; a missing/invalid one, one no newer than the own save, or one whose recorded host
identity is KNOWN to differ from the current one all fall back to the own save (0 = unknown
never blocks, the same convention every other compatibility check in this tree already
follows). `WorldAdoption.TryAdoptAsync` gained an overload taking an optional
`(GffStruct Player, SidecarMetadata Metadata)?` — the own-save character is still extracted
first (the existing fail-fast order is unchanged), the sidecar decision runs AFTER the host's
world is fetched (that is the earliest point the real host identity is known), and
`AdoptedWorld` now carries `CarriedPlayer`/`CharacterSource` so a later pifo re-assert (the
`LoadPCAutoSave` truncation `world-adoption.md` §7 found) re-writes THE CHARACTER THIS
ADOPTION ACTUALLY CHOSE rather than silently re-deriving from the own save and undoing a
sidecar preference on every truncation recovery. `agent adopt` prints which character was
used, on screen.

**Live trigger.** `src/KotorCoop.Agent/SessionSaveWatch.cs` watches
`bridge.SaveIdentityDigest` once per dispatcher tick (150 ms) with `SaveIdentityWatch` — a
pure edge detector with NO peer comparison: the first real (nonzero) reading each connection
becomes the baseline, not a change; a later DIFFERENT nonzero reading is the one edge. On a
CLIENT, that edge captures the character out of whatever `SaveDigest.FindSessionSave` reports
right now into the sidecar (item 1's "client saves normally in-game" trigger). The "OR on
clean disconnect" trigger is `SessionManager.OnDisconnecting`, a new hook invoked immediately
before ANY session teardown (a clean disconnect, a reconnect's implicit teardown, or a fresh
`Connect` replacing one already running) while `Current` is still readable — wired to
`dispatcher.CaptureCharacterOnDisconnect()`.

**Item 2, host mid-session load refusal (design §3.2 item 5).** Reuses the SAME
`SaveIdentityWatch` primitive on the HOST side of `SessionSaveWatch`: the digest changing
AFTER it had already resolved once this connection is treated as "the host loaded a different
save" and `ObserveHost` returns true exactly once for that edge. The dispatcher reacts by
printing `"co-op: loading a save mid-session is not supported yet - clients must rejoin"` on
the host's own screen and calling `SessionManager.Disconnect()` — which tears the host's
session down, so every client's socket goes silent and each one's own existing
disconnect-then-reconnect machinery (`docs/research/disconnect-reconnect.md`, ten real defects
already fixed and live-verified there) is what "clients must rejoin" actually runs on. No new
wire packet, no new shared-block field, no block version bump: this is the design doc's
recommended safe default (refuse first; full re-adoption is phase 2) built entirely from
signals that already cross the wire.

**What phase 1 does NOT do — item 3, client mid-session load auto-rejoin.** The task
authorizing this pass allowed skipping item 3 "ONLY if the detection falls out of the same
fingerprint poll for free; otherwise document it as phase 2." Detection does fall out for
free — a CLIENT's own `SaveIdentityWatch` edge fires for a load exactly the same way it fires
for a save, since both are "my own save-identity digest changed." That is precisely the
problem: **a save and a load are not distinguishable from a content digest alone.** Wiring
auto-rejoin to this signal would fire on every ordinary in-game save a client makes while
playing (item 1's own trigger), forcing an unwanted reconnect on completely normal play. Doing
this correctly needs a way to tell the two apart — a dedicated save/load hook (design §2.2
item 4's `SaveController`, itself flagged as new RE with no existing trace in this tree) or
some other live-verified signal — which is exactly the kind of engine work this phase's scope
excluded. Deferred to phase 2, not shipped as a half-working guess.

### NEEDS-LIVE (nothing in this section has run against the game)

1. **Whether an in-game "Load Game" menu action changes `SaveIdentityDigest` at all**, as
   opposed to only a SUBSEQUENT save. `SaveDigestCache`/`SaveDigest.FindSessionSave` fingerprint
   the save DIRECTORY ON DISK (the autosave slot if staged, else the most-recently-WRITTEN
   directory) by `SAVEGAME.sav`'s length and mtime — a genuine LOAD only READS a chosen save,
   so nothing on disk changes at the moment of loading unless the engine's own load path
   happens to write something too (an autosave-on-load behaviour, if any, is unmeasured here).
   If loading alone does not touch the watched file, item 2's host-refusal detection will miss
   a load until the host's NEXT save — an honest gap, not silently assumed away. §5's INFERRED
   list in the design doc above already flagged the doc's own assumption on this point as
   unmeasured; this pass did not resolve it, by design (no engine RE).
2. **Whether the host-refusal path is disruptive in a way that surprises a real host** — tearing
   down the WHOLE session (not just notifying) means the host has to re-host manually afterward.
   Never run against two real GOG installs.
3. **The end-to-end "resume tomorrow" loop**: play a session as a client, save or disconnect,
   confirm the sidecar lands (`saves\kotorcoop-character.pifo`/`.json`), quit, `agent adopt`
   again against a NEW instance of the same (or a continued) host session, and confirm the
   on-screen line names the session-saved character and the character that actually loads
   matches it (hit points/experience/inventory, not position — `world-adoption.md` §7's own
   warning that a healthy adoption always lands at the module entry point applies here too).
4. **The atomic sidecar write under real disk conditions** (antivirus lock contention, a
   crash between the two files' writes) — unit-tested with synthetic corruption/truncation,
   never with a real interrupted process.
5. **`SessionManager.OnDisconnecting` firing at the right moment relative to the game's own
   state** — unit-tested against the manager alone; never confirmed the client's save directory
   is still valid/complete to read from at the instant a real `disconnect` console command or
   window close reaches it.

### Tests

`tests/KotorCoop.Tests/ClientCharacterPersistenceTests.cs` (the preference rule — newer
sidecar wins, missing/corrupt sidecar falls back, host-identity mismatch falls back, unknown
identity never blocks — plus atomic write/tolerant read, including synthetic half-written and
corrupt-json cases), `tests/KotorCoop.Tests/SessionSaveWatchTests.cs` (`SaveIdentityWatch`'s
edge detection in isolation, then `SessionSaveWatch`'s host-refusal-as-a-pure-rule and
client-capture wiring), and two new cases in `tests/KotorCoop.Tests/WorldAdoptionTests.cs`
(a newer sidecar riding into a real adoption; a host-identity-mismatched one falling back) —
all against real temp save directories and the real `PlayerIfo`/`WorldAdoption` code, no
mocking of the GFF layer. 1106 total in the merged suite (was ~1080 before this pass), 0
failing.

## Phase 2 shipped (2026-08-22)

Roadmap item 14 phase 2: item (a) (distinguish a LOAD from a SAVE on the client, so a client
loading a save mid-session auto-rejoins) and item (b) (host mid-session load → full
re-adoption instead of refusal), both landed behind new gates, both default OFF. **No engine
RE was done for this pass either** — the detection primitive reuses two facts PluginHost
already tracked for other reasons (the world epoch, and the pre-transition announcement flag),
and the rejoin action reuses the exact two calls `agent adopt` already makes for the first
join. Tag key as elsewhere in this tree: **VERIFIED** · **INFERRED** · **NEEDS-LIVE**.

### 1. The detection primitive

Phase 1's own "What phase 1 does NOT do" section named the blocker precisely: "a save and a
load are not distinguishable from a content digest alone." `Odyssey.TryGetWorldEpoch` is the
fix's other half — its own doc comment already states the mechanism: the epoch combines the
server's object-array pointer (which `UnloadModule` replaces) with a module-name hash. A plain
SAVE never calls `UnloadModule` — nothing about writing the current world to disk tears the
object array down — so **the epoch does not change on a save (INFERRED** from the disassembly
that doc comment already cites; not measured against a real in-game save).

The remaining problem: an ORDINARY module transition changes the epoch too (`UnloadModule`
runs on every module load, load or transition alike), and — live-measured, shared-state-audit.md
round 53 — KOTOR's own engine autosaves through every transition, so the save-identity DIGEST
changes on an ordinary transition as well. Digest-change-alone and epoch-change-alone therefore
BOTH fire on both a load and an ordinary transition; neither distinguishes them by itself.

The primitive that closes the gap is `Odyssey.IsAreaTransitionPending` (`trans_pending`),
already tracked by `PluginHost` as `_transitionPending` for the pre-store flush window
(destroying spawned phantoms before `StoreCurrentModule` serialises the area). Its own doc
comment: a byte the engine sets **roughly half a second before** a door/trigger transition
stores the area. An ordinary transition is therefore always ANNOUNCED by this flag going true
first; a Load Game action — main menu or the in-game pause menu — goes straight from
`CSWGuiSaveLoad`'s LOAD branch into `ExecuteLoadModule` with no such precursor, because
`trans_pending` belongs to `CSWSArea`'s own scripted-transition bookkeeping, unrelated to the
save/load UI. **INFERRED**, not measured live — this is the one assumption the whole feature
rests on, and it is named, not hidden, in `WorldLoadRule.IsUnannouncedTeardown`'s own doc
comment (`src/KotorCoop.Plugin/WorldLoadRule.cs`), a deliberately one-line pure function so the
assumption is exactly what a test pins.

**The wire primitive.** `PluginHost.OnFrame`, at the exact point a world teardown is noticed
(`TryGetWorldEpoch` starts returning false), now judges `WorldLoadRule.IsUnannouncedTeardown(_transitionPending)`
BEFORE `_transitionPending` is reset for the next epoch (resetting it first would erase the one
fact the decision needs). An unannounced teardown bumps a new block field,
`SharedBlock.OffsetWorldLoadSerial` (a plain monotonic counter, no payload) — block **version
49**, in the free tail the mid-module-movie region's own comment had already measured
(0x396C..0x3970 of the 0x94 free bytes at 0x396C..0x3A00; no `TotalSize` growth). Half-bump
guarded the usual way: `_block->Version = 49` (`PluginHost.cs`) and the version byte in
`KotorGameBridge.PluginSignature` bumped together (0x30 → 0x31), pinned by
`TransitionMovieTests.TheBlockVersionMovedInBothHalves`.

**The agent-side classification.** `KotorCoop.Session.SerialEdgeWatch` is `SaveIdentityWatch`'s
same edge-detector shape, minus its "0 = unresolved" special case (a load serial legitimately
starts at 0). `KotorCoop.Session.SaveOrLoadClassifier` correlates a digest-change edge against
a load edge within a 5-second window (`SaveDigestCache`'s own documented up-to-2-second re-stat
interval, the same order of magnitude `SaveIdentitySettle.DefaultSettleMs` already uses for a
structurally similar "let a related signal catch up" wait): a digest change WITH a load edge in
the window is `SaveOrLoadVerdict.Load`; a digest change with none is `Save`. A load edge that
never gets a matching digest change simply expires unconsumed and produces no verdict — this
deliberately does NOT fix phase 1's own NEEDS-LIVE item 1 (whether a genuine Load Game action
touches the watched save file at all); it only fixes CLASSIFYING the digest-changes that do
fire, which is everything this phase needed.

`SessionSaveWatch` (`src/KotorCoop.Agent/SessionSaveWatch.cs`) gained `ObserveOwnVerdict`
(either role, its own digest+load-serial) and `ObserveRemoteVerdict` (client only, the HOST's
reported values). Both watches share `Reset()`'s lifecycle with phase 1's `_watch`, so a fresh
connection's first reading of everything becomes a baseline, never a change — this is what
makes "the load that IS the adoption" (the world adoption that just carried a joiner INTO the
session) invisible to the classifier with no special-casing at all, the same way phase 1's own
baseline rule already worked.

**A free side-effect worth naming**: `CoopCommandDispatcher.MaintainSessionSave` now gates the
LEGACY `session_save_host_refusal` action on the classifier's verdict instead of the raw digest
edge — closing shared-state-audit.md round 53's exact false positive (an ordinary transition
tripping the refusal) as a consequence of building the real primitive, not a separate fix. That
rewiring itself has no live pass yet, so the flag stays default OFF regardless.

### 2. Client load → auto-rejoin (`client_load_rejoin`, default OFF)

On a CLIENT's own `SaveOrLoadVerdict.Load`: prints "you loaded a save - rejoining the host's
world" and calls a new `SessionManager.RejoinAfterLoadAsync`, gated through
`KotorCoop.Session.ClientLoadRejoinRule` — a pure two-state machine (`Idle`/`InFlight`) that
swallows a second trigger while a rejoin is already running rather than queueing or restarting
one (the in-flight attempt fetches the host's CURRENT world at the moment it actually runs, so
a second signal has nothing to add).

`RejoinAfterLoadAsync` reuses, rather than re-derives, the exact two pieces `agent adopt`
(`Program.cs`'s `AdoptAsync`, the first-join path) already composes: `WorldAdoption.TryAdoptAsync`
for the network half (fetch the host's world, stage it, write the carried character — the
character is whatever the phase-1 "resume tomorrow" sidecar holds, which `SessionSaveWatch.ObserveClient`
already wrote fresh THIS SAME TICK for an own-load trigger, since it captures on every digest
change unconditionally, save or load alike — design doc §4.2 item 3's own intent), and the new
`KotorGameBridge.TryRequestAdoptedWorldLoad` (wrapping `PluginMenuLoader.TryRequestLoad(adoptedWorld: true)`,
the same call the first join makes) for the local engine half. `CoopCommandDispatcher.TriggerRejoin`
then polls `bridge.TryGetAdoptedWorldLoadStatus` (up to 60 s) and calls `SessionManager.Reconnect()`
once it settles, to reopen the coop socket against the same host.

**⚠ The one gap this honestly does not close.** `MenuLoadController`'s replay is a MAIN-MENU-ONLY
mechanism — it acks and no-ops the instant `Odyssey.HasServer` is true (its own header). A
mid-session client Load Game action (the in-game pause menu, not the main menu) tears down the
old server and stands up a NEW one entirely inside the ENGINE's own unmodified load path, with
no main-menu visit in between — whether the game has genuinely returned to "no server" by the
time `TryRequestAdoptedWorldLoad` fires, or whether it silently acks-and-moots against the
player's own freshly-loaded server, has never been measured. **This is new RE this pass did not
attempt** (driving a load from an ALREADY-ACTIVE session is a different code path than the
main-menu autosave branch `MenuLoadController`'s own header already names as everything it
replays), and it is why `client_load_rejoin` ships default OFF rather than claimed working.

### 3. Host load → re-adoption (`host_load_readopt`, default OFF)

Design doc §3.2's recommendation, taken literally: "reuse the join path, do not build a new
one," and — for the trigger — "have every client run the same auto-rejoin." No new broadcast
packet: `WorldDigest` (the in-world fingerprint already exchanged every 1 Hz on the existing
`WorldDigestPacket`) gained a fifth field, `LoadSerial` (protocol **version 35**), carrying the
sender's own `WorldLoadSerial` counter unmodified. A CLIENT already receives the host's raw
`SaveIdentity` and now its `LoadSerial` too (`CoopClient.RemoteWorldDigest`, new); running the
SAME `SaveOrLoadClassifier` against those two fields (`SessionSaveWatch.ObserveRemoteVerdict`)
classifies the HOST's edge exactly as it classifies its own, entirely client-side, with no
signal the host has to explicitly send beyond what was already on the wire.

On the HOST's own `SaveOrLoadVerdict.Load`, with `host_load_readopt` on: prints "host loaded a
save - everyone re-joins" on the host's own screen and does nothing else — re-adoption is a
CLIENT action by construction, and every connected client already sees the edge for itself the
moment its own `WorldDigestPacket` poll runs `ObserveRemoteVerdict`. When BOTH `host_load_readopt`
and the legacy `session_save_host_refusal` are on, re-adoption takes priority (a working
re-adoption is strictly better than a refusal); the refusal remains the safe fallback if
re-adoption is ever turned back off. Each client's own reaction is `TriggerRejoin(RejoinTrigger.HostReload)`
— the IDENTICAL machinery §2 built, reused rather than duplicated, per the design doc's own
instruction.

**What "settling" means here, and where round 54's debounce comes in.** §3's design explicitly
warned that re-adoption must copy the host's world AFTER the host's own load settles, not the
stale mid-write state. `WorldAdoption.TryAdoptAsync` already fetches the host's world fresh at
the moment it runs (a TCP pull of the CURRENT save directory, not a cached copy), and
`ContentCompatibility`'s existing `SaveIdentitySettle` (round 54, `DefaultSettleMs` 5000)
already exists for exactly this class of "a related signal has not caught up yet" problem —
`SaveOrLoadClassifier`'s own correlation window is deliberately the same order of magnitude for
the same reason, though it is a separate instance rather than a shared one (they debounce
different things: one a peer-comparison mismatch, one an edge correlation).

**⚠ Gaps this pass did not close, named rather than hidden:**

1. Everything §2's gap names applies here too, once per client — `TryRequestAdoptedWorldLoad`'s
   main-menu-only replay is unverified against a live "already in a session" state.
2. **Whether re-adoption survives a host load that changes which MODULE is loaded**, not just
   the save state within one. Design doc §6 item 1 flagged this as the biggest open risk
   already: `transition-window-races.md`'s fixes were proven against ordinary module-to-module
   travel, a similar-shaped but unmeasured-for-THIS-case discontinuity (a save load reverts
   EVERY piece of world state to the save, not just the module identity).
3. **Wire latency**: whether `WorldDigest.LoadSerial`, riding the existing 1 Hz channel, reaches
   a client fast enough that the correlation window (5 s) reliably still contains it once the
   client's own digest classification runs. Not measured.

### 4. Tests

All pure, no game, no filesystem beyond what phase 1's existing helpers already used:

- `tests/KotorCoop.Tests/WorldLoadRuleTests.cs` — the plugin-side primitive
  (`WorldLoadRule.IsUnannouncedTeardown`), both cases.
- `tests/KotorCoop.Tests/WorldLoadWatchTests.cs` — `SerialEdgeWatch` (baseline-not-a-change
  including a zero baseline, repeats, an advancing edge, a skipped range still one edge) and
  `SaveOrLoadClassifier` (a bare digest change is a save; a coinciding load edge is a load; a
  load edge that arrives BEFORE the digest catches up still correlates; one outside the window
  does not; a load edge is consumed by the first digest change and cannot match a second;
  `Reset` forgets a pending edge).
- `tests/KotorCoop.Tests/ClientLoadRejoinRuleTests.cs` — the rejoin decision state machine: no
  trigger or a gated-off trigger does nothing, a genuine trigger (either `RejoinTrigger`) starts
  a rejoin and moves to `InFlight`, a second trigger while `InFlight` is swallowed not queued or
  restarted, `Complete` returns to `Idle` and frees the next trigger.
- `tests/KotorCoop.Tests/SessionSaveWatchTests.cs` — new cases for `ObserveOwnVerdict`/
  `ObserveRemoteVerdict`: establishing both baselines together is never a verdict (the
  composition that makes "the load that IS the adoption" invisible), a bare digest change
  classifies as Save, a coinciding load edge classifies as Load, the own and remote watches are
  independent, and `Reset` forgets both.
- `tests/KotorCoop.Tests/CoopConfigTests.cs` — `client_load_rejoin`/`host_load_readopt` default
  OFF and can be turned on, mirroring `session_save_host_refusal`'s existing cases.
- `tests/KotorCoop.Tests/PacketCodecTests.cs` — `WorldDigestLoadSerialRoundTrips`, alongside the
  existing `WorldDigestRoundTrips`/`AnEmptyWorldDigestRoundTripsWithoutLosingItsEmptiness` (both
  of which still pass unmodified — `LoadSerial` defaults to 0, so a caller that never mentions
  it round-trips exactly as before).
- **Not attempted**: a `SessionIntegrationTests`-style real-socket loopback exercising a
  simulated load edge end-to-end. That harness drives `CoopHost`/`CoopClient` directly; the
  detection and rejoin logic this pass added lives one layer up, in `CoopCommandDispatcher`
  (which needs a real or stubbed `IGameBridge` with a settable `WorldLoadSerial`/`SaveIdentityDigest`
  threaded through `SessionManager`) — building that harness is a larger, separate undertaking
  than this pass's scope, and the pure state-machine tests above already pin every property a
  loopback test would additionally exercise except real wire timing, which is precisely what
  the live script below is for.

### NEEDS-LIVE — nothing in this section has run against the game

1. **Whether `TryGetWorldEpoch` changes on a plain SAVE.** The load-bearing INFERRED claim
   §1 rests on — reasoned from the `UnloadModule`/object-array doc comment `TryGetWorldEpoch`
   already cited before this pass, never measured against a real in-game save.
2. **Whether `trans_pending` (`IsAreaTransitionPending`) is NEVER set ahead of a Load Game
   action**, mid-session (pause menu) as well as at the main menu. If it turns out to be set —
   even coincidentally, even rarely — `WorldLoadRule.IsUnannouncedTeardown` would silently
   misclassify that load as an ordinary transition and neither `client_load_rejoin` nor
   `host_load_readopt` would fire for it.
3. **Whether `TryRequestAdoptedWorldLoad` (`MenuLoadController`'s replay) does anything at all
   once a co-op session was already running** — as opposed to acking-and-no-opping against
   `Odyssey.HasServer` being true from the player's own just-completed load. §2/§3's single
   biggest open question.
4. **End-to-end rejoin timing**: from the moment a client's Load Game action lands to the
   moment `manager.Reconnect()` fires — whether the 60 s poll deadline in
   `CoopCommandDispatcher.TriggerRejoin` is anywhere near the right order of magnitude.
5. **Whether re-adoption survives a host load that changes the MODULE**, not just the save
   state within one module (§3, gap 2).
6. **Wire latency for `WorldDigest.LoadSerial`** reaching a client inside the 5 s correlation
   window (§3, gap 3).

### Live-test script, once the above are checked off

Two real GOG installs, rebuilt plugin and agent on both (`build.ps1 -Install`), a live co-op
session already running (host + one client, both in the same module).

1. **A plain save does NOT trigger anything.** On EITHER side, save normally (in-game Save
   menu, any slot). Expect: on a client, the existing phase-1 "session-saved character"
   capture line only — no "rejoining" line, no disconnect, no reconnect. On the host with
   `host_load_readopt=1`, no "everyone re-joins" line at all.
2. **Client loads a different save mid-session.** With `client_load_rejoin=1` on the client:
   from the in-game pause menu (NOT the main menu), Load a different save. Expect within a few
   seconds: "you loaded a save - rejoining the host's world" on the client's screen, the co-op
   session tears down and re-establishes, and the client lands back in the HOST's current
   module — not the save it just picked. Watch specifically for whether the local reload
   (`TryRequestAdoptedWorldLoad`) does anything (NEEDS-LIVE item 3) — a log line
   `menu: autosave load refused (failure ...)` or no plugin log activity at all around the
   request means it silently no-op'd.
3. **Host loads a save mid-session.** With `host_load_readopt=1` on the host and
   `client_load_rejoin=1` on every client: from the host's in-game pause menu, Load a save
   (same module first, then repeat into a DIFFERENT module). Expect: "host loaded a save -
   everyone re-joins" on the host's screen, and — within the 5 s correlation window plus the 1
   Hz digest cadence — every client independently prints "host loaded a save - rejoining their
   world" and goes through the same rejoin sequence as item 2, landing in the host's NEW
   module.
4. **Confirm the character survived.** After either rejoin, check hit points, experience,
   inventory and alignment match what the loaded/reloaded character actually had — not the
   module entry point's fresh spawn (`world-adoption.md` §7's own warning: judge health on
   stats, never on position, for the identical reason here).
5. **Confirm the flags are still off by default.** With both gates left at 0 (coop.ini
   default), repeat steps 2 and 3: expect ONLY the log line ("session-save: ... verdict... NOT
   acting (gate off)") and no visible effect on either player's game — the phase 1 behaviour
   (client capture, host's legacy refusal if THAT flag is separately on) must be unchanged.
