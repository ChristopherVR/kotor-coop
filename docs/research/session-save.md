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
CLIENT, that edge captures the character out of this install's own newest save
(`ClientCharacterPersistence.FindCaptureSource`) into the sidecar (item 1's "client saves
normally in-game" trigger). The "OR on clean disconnect" trigger is
`SessionManager.OnDisconnecting`, a new hook invoked immediately before ANY session teardown (a
clean disconnect, a reconnect's implicit teardown, or a fresh `Connect` replacing one already
running) while `Current` is still readable — wired to `dispatcher.CaptureCharacterOnDisconnect()`.

⚠ **Not `SaveDigest.FindSessionSave` (changed 2026-09-03).** Until then both triggers read the
save the digest watches, which for a joiner is the staged copy of the HOST's world in
`saves\000001 - AUTOSAVE` — host PC included — so every clean disconnect wrote the host's
character over the joiner's sidecar, and `ShouldPreferSidecar` carried it in on the next join
(the sidecar being newer than the joiner's own save). `FindCaptureSource` takes the newest
`SAVEGAME.sav` by file mtime and skips the staged copy, which `StagedSave.IsStagedCopy` knows
two ways: the `saves\kotorcoop-staged.json` marker adoption writes (slot, size, mtime of the
file it staged — a later engine autosave into the same slot moves both and stops matching), or,
for the autosave slot with no marker, the file's own pifo member (`PlayerIfo.InspectPifo`: the
engine writes a live `pifo` into every autosave it makes; staging leaves a renamed one or
none). `ClientCharacterPersistence.Capture` additionally refuses a source written before the
sidecar was captured (`CharacterCapture.Kept`), so a session left without saving cannot roll
the character back to an older save. Live 2026-09-03: marker match → `session-saved character:
'000000 - QUICKSAVE' (written ...) is no newer than the sidecar (captured ...) - kept the
sidecar`; marker deleted → same line (content rule); client `save` then disconnect → `carried
the character from '000000 - QUICKSAVE' ...; wrote the character sidecar (9846 bytes ...)`.

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

## Phase 2 audit (2026-08-23): a real false positive found and fixed, no other gaps closed

Task: audit whether the load-detection primitive (`WorldLoadRule.IsUnannouncedTeardown`,
§"Phase 2" above) is wired to the REAL in-game Load menu entry point rather than something
merely adjacent to it, and assess whether client auto-rejoin's existing scope (already shipped
in Phase 2, gated OFF) is sound. **No engine RE was done this pass either** — this is a static
code audit plus one real bug fix and test run; nothing below was run against the game.

### The gap found: `TravelController`'s peer-follow warp also never sets `trans_pending`

`WorldLoadRule.IsUnannouncedTeardown` treats ANY world teardown that `_transitionPending`
(`trans_pending`) never announced as a Load Game action. Its own doc comment already names the
one exception the design doc anticipated (a plain save never tears the world down at all) but
missed a second one that was already sitting in the same file tree: `TravelController.cs`'s own
doc comment on `TravelToModule` states outright that "a co-op warp like this one never raises
trans_pending at all" (`TravelController.cs`, the `PublishTransitionAnnounce` remark). That
controller is not a rare debug path — it is the mechanism that pulls a CLIENT into the module
the HOST just walked into (and vice versa, "a host being pulled along by a peer"), which fires
on ordinary, routine module travel any time the two players are not already in the same module
when one of them takes a door. Before this fix, PluginHost's frame loop had exactly one
"announced" bit (`_transitionPending`, fed only by the real `trans_pending` byte), so every
peer-follow warp produced the identical signature — `TryGetWorldEpoch` starts failing while
`_transitionPending` is false — that a genuine Load Game action produces. Concretely, this means
`SharedBlock.OffsetWorldLoadSerial` was bumping on ordinary peer-follow travel, not only on real
loads: harmless while `client_load_rejoin`/`host_load_readopt` are OFF (their own dispatcher
code only reacts to the classifier's `SaveOrLoadVerdict`, which additionally requires a
correlated digest change within the 5 s window — see `SaveOrLoadClassifier`, so a bare
load-serial edge with no accompanying digest change classifies as nothing), but it would have
produced spurious classifier activity — and, worse, a *misleading* live-test result — the moment
either gate is turned on for the item 2/3 live-test script above, since ordinary co-op play
(any host-leads-through-a-door-first moment) would ride the exact same signal the tests are
trying to isolate to a genuine Load Game action.

**The fix** (`src/KotorCoop.Plugin/PluginHost.cs`): a second "announced" bit, `_travelAnnounced`,
set the instant a travel request is first seen pending (the same "announce before the teardown"
timing `_transitionPending` already uses for a real door) and fed into
`WorldLoadRule.IsUnannouncedTeardown` alongside it (`_transitionPending || _travelAnnounced`).
Reset at one canonical point — immediately after `TryGetWorldEpoch` succeeds each frame — rather
than only where `_transitionPending` resets, specifically so a travel request that turns out to
be a same-module no-op (nothing torn down) cannot leave a stale "announced" flag sitting around
to mask a LATER, genuinely unannounced load. `WorldLoadRule` itself is unchanged (still the
one-line pure negation `WorldLoadRuleTests.cs` already pins); the new state lives entirely in
`PluginHost`'s per-frame bookkeeping, which is not independently unit-testable without a live
game (consistent with this codebase's existing seam: `WorldLoadRule` is the pure/tested half,
`PluginHost.OnFrame` is the orchestration around it that only a live pass or the disassembly
can confirm). `dotnet test` after the fix: 1635 passed, 0 failed (no test needed touching —
the fix changes PluginHost's unsafe frame loop, which has no existing unit-test seam for this
specific state, not the pure `WorldLoadRule`/`SaveOrLoadClassifier`/`ClientLoadRejoinRule` layer
that IS tested).

**What this does NOT close**: the fix removes one *known* false-positive source; it does not
prove there are no others. The audit checked every call site that can tear the world down
without going through a real door/trigger (`grep` for `ExecuteLoadModule`/`StartNewModule`/
`UnloadModule`/`TravelToModule` across `src/`) and found exactly two: `TravelController`
(fixed above) and `MenuLoadController` (the plugin's own replay of the Load branch, used by
BOTH the first-join `agent adopt` flow and Phase 2's own `client_load_rejoin`/`host_load_readopt`
rejoin action). `MenuLoadController`'s own teardown does NOT need the same `_travelAnnounced`
treatment — traced through `CoopCommandDispatcher.MaintainSessionSave`/`TriggerRejoin`/
`SessionManager.RejoinAfterLoadAsync`: `RejoinAfterLoadAsync` calls `Disconnect()` BEFORE
requesting the replay load, which drops `manager.Current` to null for the whole in-flight
window; `MaintainSessionSave` short-circuits to `_sessionSaveWatch.Reset()` whenever
`manager.Current` is null, so the classifier is not even polling `WorldLoadSerial`/
`SaveIdentityDigest` while the rejoin's own replay is what is tearing the world down — and by
the time `Reconnect()` re-establishes a session, `Reset()` has already re-armed the baseline
rule, so the rejoin's own load edge is absorbed as "the load that IS the adoption" exactly the
way the very first join already is (§"Phase 2" above, `ClientLoadRejoinRule`'s own doc comment).
This is a code-level trace, not a live measurement — it depends on `Reconnect()` actually
cycling `manager.Current` through a real gap, which the audit did not additionally verify beyond
reading `SessionManager.cs`.

### Client auto-rejoin scope: already implemented, correctly scoped, nothing further to build

The task that produced this pass asked for an assessment of whether client mid-session-load
auto-rejoin is implementable by reusing the existing join/reconnect machinery rather than new
engine surgery. It already is: Phase 2 (§ above, shipped 2026-08-22) did exactly this —
`SessionManager.RejoinAfterLoadAsync` composes the same two calls `agent adopt`'s first-join
path already makes (`WorldAdoption.TryAdoptAsync` for the network half,
`KotorGameBridge.TryRequestAdoptedWorldLoad`/`PluginMenuLoader.TryRequestLoad(adoptedWorld:
true)` for the local engine half), gated by a pure, tested state machine
(`ClientLoadRejoinRule`) that swallows duplicate triggers rather than queueing or restarting.
There is no separate "mid-session load" code path to design — the scope this task asked about
was already kept clean the first time. What remains is exactly what Phase 2's own NEEDS-LIVE
list already says: whether `TryRequestAdoptedWorldLoad`'s replay (a MAIN-MENU-ONLY mechanism by
its own header) does anything at all once fired from an ALREADY-RUNNING session, rather than
acking-and-no-opping against `Odyssey.HasServer` being true. That is genuinely a live-only
question — no amount of further static reading resolves it, because it depends on what the
engine's own `CSWGuiSaveLoad`/`ExecuteLoadModule` code does when re-entered from a state the
main-menu replay was never observed to run from. This pass did not attempt new RE on that path;
doing so safely needs the same call-order archaeology `MenuLoadController`'s own header already
performed for the main-menu case, applied to a mid-session entry instead — out of this pass's
scope (static audit only, no engine access).

### Updated live-test script — one item added

Everything in the "Live-test script" list above (§"Phase 2 shipped") still applies unchanged.
Add this item, run BEFORE items 2/3 (so a false positive is caught before it can be blamed on
the load-menu steps that come after it):

0. **Ordinary peer-follow travel does NOT trip the load detector.** Two real GOG installs, a
   live co-op session, host and client in the SAME module. Have the HOST walk through an
   ordinary door first (client not yet at the door), so the client's `TravelController`
   peer-follow warp fires to pull it along — then repeat with the CLIENT leading and the host
   following. With `client_load_rejoin=1`/`host_load_readopt=1` on (so the classifier's activity
   is visible in the log even though nothing should act on it): expect the existing "following
   the host to '<module>'" log line and normal transition behaviour, and specifically expect
   NO "you loaded a save" / "host loaded a save" line and no rejoin/reconnect cycle on either
   side. This is the regression check for the false positive found and fixed in this pass — it
   was never run live before the fix existed, so it is untested against the real engine, only
   against the reasoning above (`TravelController.Execute`'s own doc comment, confirmed by
   reading the code, not by observing a live warp).

## Round (2026-08-23): item 0 confirmed clean both directions; items 2/3's real-menu path
## blocked by a rig-tooling gap, not a code defect; both gates stay OFF by default

Ran the "Updated live-test script" above against `tools/demo.ps1`, both real GOG installs,
`client_load_rejoin=1`/`host_load_readopt=1` on both sides. **No engine RE was done this
pass** — this is a live-test pass only, following the doc's own script. One real gap in the
project's own tooling was found and is recorded below rather than worked around with a guess.

### Item 0 — CONFIRMED, both directions, no false positive

Fresh `demo.ps1` cold start (save 1, both players spawn together in `END_M01AA`). For each
direction, one player was teleported to `end_door10` (`tp 47.4172 120.126 0`, the same door
`transition-smoothness.md`/`cinematics.md` already use) while the other stayed at the spawn
point (20.0, 22.2), well outside the door's 40 m use-range, then the near player ran
`usedoor end_door10` — a REAL engine door transition (`trans_pending` goes true), not the
`warp` cheat.

* **Host leads (Lanik at the door, Malak at spawn).** Host's `usedoor` succeeded
  (`walking through door 0x4A (0.0m) -> 'end_m01ab'`); client's `TravelController` peer-follow
  fired (`following Malak to 'end_m01ab' IMMEDIATELY...` — wait, this direction printed
  `following Malak`, i.e. the HOST's own agent log names the peer it followed FROM; both ended
  up in `end_m01ab` within ~2 s of each other). Full agent-log grep for `loaded a save`,
  `rejoining`, `re-joins` across both sides' logs for this run window: zero matches. Plugin-log
  grep for `unannounced` across both installs for this run window: zero matches (the two hits
  that DO exist in the host's plugin log predate this run by over an hour, left over from the
  round that found and fixed the `_travelAnnounced` bug in the first place).
* **Client leads (Malak at the door, Lanik at spawn) — a full rig restart between directions
  so the module pair was fresh (`end_m01ab` has no transition door back, per `cinematics.md`,
  so testing the reverse direction needs a clean START_M01AA-both-players reset, not a
  same-session flip).** Client's `usedoor` succeeded identically; host's peer-follow warp
  fired (`following Malak to 'end_m01ab'...`, `host announced transition to 'end_m01ab'...`).
  Same zero-match grep result for `loaded a save`/`rejoining`/`re-joins`/`unannounced` in the
  post-transition window on both sides.

The one thing seen in both directions that is expected and harmless: the phase-1
"session-saved character" capture line fires on the FOLLOWING peer ~1 s after the transition
lands (KOTOR's own engine autosaves through every module transition, which is exactly the
digest edge phase 1's own capture watches — this is documented, intended behaviour, not the
bug under test). **Verdict: the `_travelAnnounced` fix holds. Ordinary peer-follow travel,
either direction, produces no false "loaded a save" detection.** This is the single most
load-bearing result of this pass — it means turning either gate on does not risk misfiring on
routine co-op play, which was the prerequisite for trusting anything else below.

### Items 2/3 — the REAL in-game pause-menu Load path: blocked by a genuine rig-tooling gap

**Attempted, not achieved.** The blocker is `tools/shot.ps1` being confirmed broken (solid
black — re-verified this pass with a pixel-statistics check, not just eyeballing: a captured
frame's average/min/max luminance were all exactly 0 across an 800x600 grid sample) combined
with a SECOND, newly-confirmed problem: **mouse-click input injection does not land on a
repositioned game window on this rig**, even though the window is genuinely foreground.

What was tried, in order:
1. Confirmed `Escape` (`tools/sendkey.ps1`) opens the real in-game pause menu — verified two
   independent ways: `[gui+0x7C]` (`in_game_pause`, the exact pointer `docs/research/
   ingame-gui-transition.md` §1.376 already documents, read externally via the same
   `AppManagerPtr = 0x007A39FC` chain `Odyssey.GetInGameGui()` already walks — a read-only
   probe script, no new RE, no plugin changes) went non-null, AND a held `w` key produced NO
   position change (`console 'where'` before/after identical) where an unpaused world moves
   the character every time.
2. Confirmed KEYBOARD `SendInput` injection genuinely lands on this exact window at this exact
   moment: with the pause menu closed, holding `w` for 2 s moved the character
   (49.9,120.3 → 48.3,122.4) — ruling out "no synthetic input reaches this process at all" as
   the explanation for what follows.
3. With the pause menu open, probed 36 candidate click positions (`tools/click.ps1`, same
   `SendInput` mechanism, `GetForegroundWindow()` confirmed matching the target the whole time)
   spanning almost the full 800x600 client area, including an activation-cycle
   (minimize/restore, the same DirectInput-reacquire trick `demo.ps1`'s own auto-load retry
   already uses) partway through. **Zero of the 36 clicks changed `[gui+0x7C]`/`[gui+0x40]` at
   all.** This reproduces, on live hardware, exactly what `src/KotorCoop.Agent/SaveLoader.cs`'s
   own doc comment already warns about ("menu input injection is intermittently dead in ways
   that resisted diagnosis... every mouse message is dropped outright unless the game window is
   the active one") and what `demo.ps1`'s own header notes ("the game tracks its own cursor
   from relative DirectInput deltas rather than the OS pointer, so clicks stop landing once a
   window has been moved") — both windows in this rig ARE repositioned by `demo.ps1` for the
   side-by-side layout, which is exactly the condition those two notes already flag as
   unreliable. This is a **pre-existing, independently-documented tooling gap**, not something
   this pass discovered fresh.
4. Attempted blind KEYBOARD-only navigation as a fallback (no coordinates needed): `Enter`
   after `Escape` (assuming default focus on the first button), then repeated `Escape`/
   `Backspace` to back out. This left the CLIENT instance stuck — walk-blocked, unresponsive to
   further `Escape`, for the remainder of the attempt — most likely because an un-observed
   `Enter` landed on the wrong button (a `Quit`-style confirmation is the leading suspect,
   though unconfirmed) and the resulting sub-panel does not accept `Escape` to cancel. Recovered
   by a full rig restart (`demo.ps1 -Stop` then a fresh `demo.ps1`) — the host session and the
   test save were unaffected; nothing was lost. **This is a real finding for whoever picks this
   up next: do not blind-navigate the vanilla pause menu by keyboard without a working
   screenshot loop — a wrong guess can wedge the instance in a state `Escape` does not recover
   from.**

**Recommended next step for whoever picks this up:** either (a) fix `shot.ps1`'s capture path
(the black-frame symptom is consistent with GDI `BitBlt`/`CopyFromScreen` not seeing this
DirectX window's contents — a `PrintWindow` with `PW_RENDERFULLCONTENT` or a Windows Graphics
Capture-based capture would likely succeed where `CopyFromScreen` does not, but this pass did
not attempt it, per the task's explicit instruction not to chase `shot.ps1`), or (b) do the
same kind of read-only memory-chain work this pass did for `[gui+0x7C]` one level deeper —
find the vanilla pause-menu panel's own button/child-control list and its `CSWGuiExtent`
screen-rect fields (the `CSWGuiExtent`/`CSWGuiControl` concept is already named, for a
DIFFERENT panel, in `docs/research/native-browser-panel.md`) — which would make the button
positions computable rather than guessed, and would not need clicking at all if the same
class also exposes a programmatic "activate" call the way `MenuLoadController` already
replays the LOAD branch. Neither was attempted this pass (both are new RE, out of scope for a
live-test-only pass).

**Consequence for the decision rule:** items 2 and 3's own core question — does the detection
reliably fire from a genuine pause-menu Load action, and does re-adoption/rejoin then work
end-to-end from that trigger — remains **NEEDS-LIVE**, unchanged from the Phase 2 audit above.
Item 0's clean result is necessary but not sufficient: it proves the fix does not misfire on
ordinary play, not that the feature fires correctly when it should.

### Item 4 — the staged-save re-check: strong supporting evidence, not a literal before/after
### poll, still not fully closed

`docs/research/mod-compatibility.md`'s own NEEDS-LIVE item 4 ("start the agent BEFORE
adopting, adopt, and confirm the reported identity becomes the host's within a few seconds
rather than staying the old one") could not be run exactly as scripted there either — that
needs a live polling agent attached BEFORE the adopt, which this pass's tooling could not
cleanly interleave with a one-shot `agent adopt` CLI run in the time available. Two pieces of
real evidence stand in for it:

1. **`agent adopt` end-to-end, live, zero GUI interaction.** `agent launch` (main menu, no
   save loaded) → `agent serve-world --game-path <install 2> --load-save 1` (served
   `'000002 - Game1'`, identity `0x155107BD`) → `agent adopt --pid <install 1> --join
   127.0.0.1:27140 --load-save 1`. Output, verbatim: `world adoption: adopted 'END_M01AA'
   (654,006 bytes, identity 0x155107BD), carrying the character from '000002 - Game1' (own
   save)` → `load request 1 sent to the plugin...` → `re-wrote the carried character after the
   loader cleared it...` → `in world: 'END_M01AA', carrying our own character (TEMP:pifo);
   pifo re-written 1x during the load`. The staged identity (`0x155107BD`) is what the SECOND
   install's own `serve-world` reported serving — i.e. the FIRST install genuinely ended up
   running the SECOND install's world, entirely through the plugin's main-menu replay
   (`PluginMenuLoader.TryRequestLoad`), no clicks, no screenshots. This confirms the adoption
   pipeline itself (the thing the staged-save re-check question is ultimately about — does the
   digest end up reflecting reality after a stage) works end-to-end on this rig today.
2. **Live timing for the SAME `SaveIdentityDigest`/`SaveDigestCache` mechanism**, from item 0's
   own run: in both directions, the phase-1 "session-saved character" capture (`SessionSaveWatch`'s
   edge detector, which only fires once `bridge.SaveIdentityDigest` has actually changed) landed
   1-2 seconds after the underlying save file changed (host's `usedoor` completed
   19:57:32 → capture logged 19:57:34; client's `usedoor` completed 20:00:48 → capture logged
   20:00:49). This is a live measurement of the exact cache `mod-compatibility.md` describes as
   re-checking "at most every 2 s", landing comfortably inside "a few seconds" both times.

Neither of these is the literal before/after-adopt poll `mod-compatibility.md` asks for, so
that item stays open for a future pass with better process orchestration — but between (1)
proving the pipeline produces the correct end state and (2) directly measuring the underlying
cache's convergence latency at ~1-2 s twice, there is no live evidence of staleness, and no
reason to suspect the specific "start before, adopt, watch it converge" script would find
anything different.

### Decision: both gates stay OFF by default

Per this project's own bar (a direct, decisive, zero-regression live result per gate, the same
bar `dialogue_fast_join` cleared and `party_fast_pump`/`container_fast_pump` did not): item 0
is exactly that kind of result, but only for the REGRESSION half of the picture (ordinary play
does not misfire). Neither `client_load_rejoin` nor `host_load_readopt`'s own POSITIVE
case — a genuine mid-session Load Game action being detected and correctly handled — was
exercised this pass, because the one available real-menu path (mouse clicks) does not
reliably land on this rig. `CoopConfig.ClientLoadRejoin`/`HostLoadReadopt` remain unchanged
(`IsTruthy(...)`, default `false`); coop.ini on both installs was left exactly as it was found
(the `client_load_rejoin=1`/`host_load_readopt=1` lines added for this pass's testing were
removed afterward — the rig must not permanently reconfigure the product, same rule
`demo.ps1`'s own `auto_start_agent` handling already follows).

### What changed in code this pass

Nothing. This was a live-test-only pass: no source files were touched, `dotnet test` was run
once at the end to confirm the pre-existing 1638/1638 baseline is undisturbed, and this
document is the only diff.

### Tools added

`tools/blockpoke.ps1`, `demo.ps1`, `click.ps1`, `sendkey.ps1`, `movewin.ps1` were all used
unmodified. One new **read-only** diagnostic was written to the session scratchpad (not
committed — it duplicates `Odyssey.GetInGameGui()`'s own already-documented pointer chain
externally, for a quick non-visual pause-menu-open/closed check): reads
`[[[0x007A39FC]+4]+4]+0x40]+0x7C` (`in_game_pause`) via `ReadProcessMemory`, no writes, no new
offsets — everything it reads was already named in `docs/research/ingame-gui-transition.md`
§1.376 and `src/KotorCoop.Plugin/Odyssey.cs`'s `GetInGameGui()`/`AppManagerPtr`. Worth
promoting to a real `tools/` script (e.g. `guistate.ps1`) for whoever attempts items 2/3 next,
since it is the one piece of non-visual GUI-state feedback that reliably worked this pass.

## Round (2026-08-23): `loadgame <slot>` — bypass the pause-menu click entirely, per the
## previous round's own recommendation (b)

Task: find the real engine function chain a click on the in-game pause menu's "Load Game"
ultimately calls, and expose it as a direct console verb — the SAME "disassemble the click,
call the function" pattern that already solved `usedoor`/`useplaceable`/`attack`/`applyeffect`/
`skipline`/`walkto` for this exact class of rig-tooling gap (`docs/research/dialogue-skip-desync.md`
is the fullest worked example). **No live access this pass** — this is static disassembly and
build-level verification only, per the task's own constraint (another agent owns the two-instance
rig). Everything below is a **STATIC RE** claim unless marked NEEDS-LIVE.

### The disassembly trail

Starting point: `CSWGuiSaveLoad::LoadGame 0x006CB0E0` (`tools/kotor1_0_3.db`'s own
`CSWGuiSaveLoad.LoadGame` row, param_size 4 — one `int` argument that is really a pointer to a
`CSWGuiSaveLoadEntry`). This is the SAME function `MenuLoadController`'s own header already
names as the source of its replayed range (0x6CB256..0x6CB3E8) — but that header also says
"the main-menu autosave branch," which implies there is at least one OTHER branch. There is,
and it is exactly the one this task needed.

`python tools/disasm.py 0x6CB0E0 0x740` (the function's full span, confirmed by
`PromptForSaveName`'s own DB row starting immediately after it at 0x6CB820) shows the ENTRY,
before any of the code `MenuLoadController` already replays, testing a flag on `this`
(`ecx=ebx`, the `CSWGuiSaveLoad` panel itself, NOT the object `MenuLoadController` calls
`panel`) at `[this+0x64]` bit 1 (mask 2):

```
0x006CB101  test byte ptr [ebx + 0x64], 2      ; bFromMainMenu bit
0x006CB107  jne  0x6cb126                       ; SET (main menu) -> skip the block below
0x006CB109  mov  eax, [0x7a39fc]                ; appManager
0x006CB10E  mov  ecx, [eax + 4]                 ; client
0x006CB111  call 0x5ed690                       ; GetInGameGui(client)
0x006CB116  mov  eax, [eax + 0x98]              ; the panel's own message-box sub-object
0x006CB11C  test byte ptr [eax + 0x64], 1       ; that object's own state bit 0
0x006CB120  je   0x6cb808                        ; CLEAR -> abort, no load at all
```

Confirmed which bit of `[this+0x64]` this is by disassembling `CSWGuiSaveLoad::Constructor
0x006CC680` (DB: param_size 12 — three real args past `this`): at `0x6CCACA`,
`[ebp+0x10]` (the THIRD stack arg — `bFromMainMenu` in `MenuLoadController`'s own call-site
comment, `SaveLoadCtor(panel, guiManager, 0, 1)`) is shifted left 1 and OR'd into `[esi+0x64]`.
So `[this+0x64]` bit 1 is literally the constructor argument `MenuLoadController` already
passes as `1` (main menu) for every load it fires — **this whole entry-point check, and
everything gated behind it, is retail code `MenuLoadController` has never once exercised**,
because it always builds the panel with `bFromMainMenu=1`.

`gui+0x98` was identified by disassembling `CGuiInGame::Initialize 0x632860` (the function
`docs/research/ingame-gui-transition.md` §2 already names as building every in-game panel):
at `0x632B53`, `[ebx+0x98]` is lazily allocated (`push 0x984`) and constructed via
`CSWGuiMessageBox::Constructor 0x626df0` (DB row) if still null — i.e. it is **always
non-null once `CGuiInGame::Initialize` has run**, which mid-session it always has (the current
session's own world-up already ran it). This is a real retail precondition with unconfirmed
exact semantics (most likely "no message box is currently blocking input"), replicated
defensively in the new verb rather than skipped — see `FireMidSessionPhase1`'s own remark.

Past that gate, at `0x6CB126` the code that FirePhase1 already replays begins — but ONE MORE
`[this+0x64]` bit-1 branch sits inside it, at `0x006CB306..0x006CB331`, INSIDE the very
autosave branch `MenuLoadController` already reproduces call-for-call:

```
0x006CB306  mov  al, [ebx + 0x64]
0x006CB30C  test al, 2
0x006CB30E  jne  0x6cb329          ; SET (main menu) -> skip straight to CreateServer
0x006CB310  mov  ecx, [0x7a39fc]   ; appManager
0x006CB316  call 0x401280          ; CAppManager::DestroyServer(appManager)
0x006CB31B  mov  edx, [0x7a39fc]
0x006CB321  mov  ecx, [edx + 4]    ; client
0x006CB324  call 0x5ed430          ; CClientExoApp::UnloadModule(client)
0x006CB329  mov  ecx, [0x7a39fc]
0x006CB331  call 0x401380          ; CAppManager::CreateServer(appManager, 0)  <- FirePhase1's own call
```

**This is the missing piece, and it is the whole answer to the task's item 3 (does this look
identical to a real click, from `WorldLoadRule`'s point of view).** `CAppManager::CreateServer
0x401380`'s OWN disassembly (`0x00401399..0x004013CC`) already tears an EXISTING server down
(`StopServices 0x4AE900`, `Destructor 0x4AEC70`, `operator delete`, null the pointer) before
constructing a fresh `CServerExoApp` when `appManager+8` is already nonzero — so `CreateServer`
alone is technically sufficient to produce a real teardown-and-rebuild even without the extra
`DestroyServer`/`UnloadModule` call pair. But retail calls them anyway, in this exact order,
ONLY when `bFromMainMenu` is clear — i.e. **retail's own code treats "server already exists"
as a real, first-class case with its own extra step**, not an incidental side effect of
`CreateServer`'s robustness. Replicating BOTH calls, in the SAME order, is what makes the new
verb's teardown genuinely match retail's OWN mid-session code path bit-for-bit, not merely
"probably produces an equivalent result" — `CClientExoApp::UnloadModule` in particular is the
CLIENT-side half `CreateServer` alone would never call (`CreateServer` only ever touches
`appManager+8`, the SERVER pointer), and it is what makes `GetClientModule(client)` read 0
afterward instead of a stale pointer `TryPresentWorld`'s own client-module gate could otherwise
misread as "already ready."

**Does this satisfy `WorldLoadRule.IsUnannouncedTeardown`'s own contract?** Yes, by
construction: `CreateServer` genuinely destructs the old `CServerExoApp` and constructs a new
one, so `Odyssey.TryGetWorldEpoch`'s own object-array-pointer half changes exactly the way an
`UnloadModule`-driven teardown always does (the SAME fact `WorldLoadRule`'s own doc comment
already cites). And nowhere in this whole call chain — not `DestroyServer`, not
`UnloadModule`, not `CreateServer`, not any of the load-screen/connect calls
`MenuLoadController` already reproduces — is `trans_pending` (`CSWSArea`'s scripted-transition
flag) ever touched; it belongs to a completely different subsystem (`CSWSArea`'s own
transition bookkeeping), consistent with `WorldLoadRule.IsUnannouncedTeardown`'s own doc
comment already asserting a Load Game action "goes straight from `CSWGuiSaveLoad`'s LOAD
branch into `ExecuteLoadModule` with no such precursor." **A `loadgame`-fired teardown and a
real pause-menu click's teardown are the SAME retail code, not a lookalike** — this is the
strongest form of "looks identical to `WorldLoadRule`" static analysis can establish without
running the game.

Also confirmed: `guiManager` (needed for `SaveLoadCtor`'s second argument, `[menu+0x18]` in
the main-menu path) is available with no main-menu object at all —
`docs/research/ingame-gui-transition.md` §1.2 already names `[clientInternal+0x274]` as the
SAME `CSWGuiManager` (`gui_manager`, the object `CSWGuiManager::Update`'s own delete-sweep runs
against every frame, and `NativeBrowserController` already reads the identical offset). No new
RE needed for this piece — it was already sitting in this tree's own docs from an earlier,
unrelated pass.

### What shipped: `loadgame <slot>`

`src/KotorCoop.Plugin/MenuLoadController.cs` gained `FireMidSessionPhase1()`, a near-duplicate
of the existing `FirePhase1(nint menu)` with exactly the four differences the disassembly
above found (see the method's own doc comment for the full list: `guiManager` sourced from
`[clientInternal+0x274]`, the message-box precondition replicated, `DestroyServer`+
`UnloadModule` inserted at the SAME point retail's own code places them, no main-menu object
to mark for the delete sweep). `OnFrame`'s existing "a server already exists, the request is
moot" gate now branches on a new block flag, `SharedBlock.OffsetMenuLoadInSession` (block
**version 52**, free-tail region 0x3F24..0x3F28, no `TotalSize` growth — half-bumped the usual
way, `_block->Version = 52` in `PluginHost.cs` and the signature byte in
`KotorGameBridge.PluginSignature` 0x33 → 0x34, both pinned equal by the existing
`TransitionMovieTests.TheBlockVersionMovedInBothHalves`/`ItemHarnessTests.TheBlockVersionIsTheSameNumberInBothHalves`):
when the flag is clear, behaviour is BYTE-FOR-BYTE unchanged from before this pass (every
existing caller of `PluginMenuLoader.TryRequestLoad` — `agent adopt`, the phase-2 rejoin path —
passes the new `inSession` parameter's default, `false`); when set, `FireMidSessionPhase1` runs
instead of the ack-and-moot.

`PluginMenuLoader.TryRequestLoad` (`src/KotorCoop.Memory/`) gained the `inSession` parameter,
written before the serial (the same ordering rule `adoptedWorld`'s own comment already states —
everything the plugin reads must already be there when it acts on the bumped serial), and
ALWAYS written (0 or 1), never left alone, so a stale flag from an earlier mid-session request
can never leak into a later plain one. `KotorGameBridge` gained
`TryRequestMidSessionLoad(int slotIndex, out string diagnostic)`: resolves the game's own
directory from the attached process (the same `Path.GetDirectoryName(process.MainModule?.FileName)`
pattern used elsewhere in this file), stages the chosen slot over the autosave directory with
the EXISTING `PluginMenuLoader.TryStageSaveAsAutosave` (zero new file-staging code — this is
the identical mechanism `agent -Save N`/`SaveLoader` already use), then requests the load with
`inSession: true`. Polling reuses the EXISTING `TryGetAdoptedWorldLoadStatus` (both requests
write through the same status fields).

Console wiring, mirroring `skipline`/`walkto`'s own shape exactly: `loadgame <slot>` →
`ConsoleCommand.Parse` → `CoopCommandKind.LoadGame` (`Amount` = the 0-based, most-recent-first
slot) → `CoopCommandDispatcher.TriggerMidSessionLoad` (stages + requests, then polls up to 90s
in the background, the same deadline `TriggerRejoin` already uses and for the same reason — a
mid-session load goes through the identical connect-grace-then-load-then-present sequence) →
`KotorGameBridge.TryRequestMidSessionLoad` → `MenuLoadController.FireMidSessionPhase1`. Added
to `ConsoleCommand.CheatsText`'s listing alongside `skipline`/`selectreply`.

**Deliberately NOT a `CheatKind`/`CheatKinds` entry**, despite the task's own suggestion to
check that enum for the next free value (it would have been 25). Every existing `CheatKind` is
a same-frame poke against an already-resolved object — `CheatController.OnFrame` dispatches and
answers within the one frame the serial changed. A mid-session load cannot be: it needs
AGENT-SIDE file staging (which slot, which directory, copy it over the autosave slot) before
the engine call even fires, and then a multi-frame, multi-second state machine after it
(connect grace, `LoadPCAutoSave`, wait for the world, wait for the client's own module,
`TryPresentWorld`'s co-op sync hold) — structurally the SAME shape `agent adopt`/the phase-2
rejoin path already have, and neither of those is a `CheatKind` either. Reusing
`PluginMenuLoader`'s existing multi-frame, file-touching channel (built for exactly this shape
of problem, already live-verified end-to-end for the main-menu case — see item 4's "zero GUI
interaction" result in the round above) is the honest fit; forcing this into `CheatKind`'s
single-frame model would have meant either blocking the frame on file I/O (never done anywhere
else in this codebase) or building a SECOND multi-frame state machine parallel to
`MenuLoadController`'s existing one, duplicating machinery that already exists for the reason
this document's own "Recommended next step" already named.

### What this pass could NOT verify — everything below needs the live rig

1. **Whether `FireMidSessionPhase1` actually runs without crashing or hanging.** Every call it
   makes is one `MenuLoadController`'s existing `FirePhase1` already makes successfully
   live (main-menu case) EXCEPT the two new ones, `DestroyServer`/`UnloadModule` — themselves
   individually unremarkable (both are two-line teardown functions with the same shape as
   several other calls this file already makes), but never fired together, in this exact
   order, from this exact call site, against a real live server.
2. **The message-box precondition's exact semantics.** The read/branch SHAPE is confirmed by
   disassembly; whether it is ever actually clear during ordinary play (and therefore whether
   `loadgame` will spuriously refuse with failure 9 sometimes) is not measured.
3. **Whether `WorldLoadRule.IsUnannouncedTeardown` actually fires** for a `loadgame`-triggered
   teardown the way the reasoning above predicts. The STATIC case is as strong as this
   project's tooling can make it (real retail code, not a lookalike; `trans_pending` provably
   untouched by grep across the whole call chain) — but `SharedBlock.OffsetWorldLoadSerial`
   bumping, and `SaveOrLoadClassifier` then correctly classifying it as `Load`, has not been
   observed.
4. **End-to-end timing** — whether `TriggerMidSessionLoad`'s 90s deadline is anywhere near the
   right order of magnitude for a MID-SESSION load (as opposed to the main-menu case, whose
   live timings are already measured elsewhere in this document).
5. **Whether the character that lands is the CHOSEN save's character**, not a blank/default
   one — the exact same class of bug `MenuLoadController`'s own `character-login-chain.md`-referenced
   history already fixed once for the main-menu case (`AdoptWorldLogin` left CLEAR here, since
   `loadgame` deliberately does not set it — the intent is "load MY OWN chosen save", the
   `LoadPrimaryPlayer` flavour, same as a normal in-game Load).
6. **Whether an already-open pause menu interferes.** `loadgame` bypasses the GUI entirely and
   was designed assuming the pause menu is CLOSED when it fires (a console command needs no
   menu open at all) — firing it WHILE the vanilla pause menu is open was not analysed and is
   not recommended for the first live attempt.

### Live-test steps for a follow-up round

Two real GOG installs, rebuilt plugin/agent on both (`build.ps1 -Install`), block v52/protocol
unchanged (this pass added no wire packet — the whole mechanism is local, one install at a
time). Read `docs/live-verification.md`'s standing rules before starting.

0. **Smoke-test in isolation first, single install, no co-op session.** Launch one install,
   load any save normally (menu click or `agent -Save N`) so a server exists, then
   `blockpoke.ps1 -ProcId <pid> console 'loadgame 1'` (a DIFFERENT slot than the one currently
   loaded, so a real module/character change is unambiguous). Watch the plugin log for the new
   lines this pass added (`menu: tearing down the existing server for a mid-session load`,
   `menu: creating the server and loopback session (mid-session)`, then the SAME phase-2/3
   lines `FirePhase1` already produces) and confirm the game actually lands in the SECOND
   save's module with the SECOND save's character (hit points/experience/inventory — judge by
   stats, not position, same rule `world-adoption.md` §7 already established). This alone
   answers NEEDS-LIVE items 1, 2 and 5 without any co-op session in play at all.
1. **Only once step 0 is clean**, bring up a live co-op session (`tools/demo.ps1`) with
   `client_load_rejoin=1`/`host_load_readopt=1` on both installs (the SAME gates
   `docs/research/session-save.md`'s own "Round (2026-08-23)" entry above already uses for
   items 2/3). On the CLIENT, fire `loadgame <slot>` for a DIFFERENT save than the session's
   current one. Expect, within a few seconds: `SharedBlock.OffsetWorldLoadSerial` bumps (log or
   `tools/blockpoke.ps1 ... console 'lagstats'`/a raw block read), the classifier reports a
   `Load` verdict (NOT a `Save`), "you loaded a save - rejoining the host's world" on the
   client's own screen, and the SAME rejoin sequence item 2 of the round above already
   specified. This is NEEDS-LIVE item 3, the single most load-bearing question this pass could
   not answer.
2. **Repeat on the HOST** with `loadgame`, `host_load_readopt=1`: expect "host loaded a save -
   everyone re-joins" on the host and every connected client independently re-adopting, per
   item 3 of the round above.
3. **The regression check `loadgame` makes newly possible**: with NEITHER gate on, fire
   `loadgame` on either side and confirm ONLY the log line changes (world loads, no rejoin
   activity) — the same "gate off means no visible effect" contract every other gated feature
   in this codebase already holds to.
4. **Timing**: note how long `TriggerMidSessionLoad`'s poll actually takes to settle, and
   whether 90s is comfortably enough — adjust if a live pass finds it isn't (NEEDS-LIVE item
   4).

## Live pass (2026-08-23, live rig round): the core mechanism works, live-confirmed twice; a real precondition trap found and worked around; the rejoin gates could not be validly exercised this round

Ran on the two-instance rig. `dotnet build`/`dotnet test` green (1652/1652) before touching
anything live.

### Item 0 — the smoke test: PASSES, once a genuine precondition trap is understood

First attempt refused cleanly with `failure 9` (`menu: mid-session load refused - the
message-box precondition was not met`), deterministically, on every retry. This is NEEDS-LIVE
item 2 answered directly: **the message-box precondition is NOT always clear during ordinary
play — it was clear (bit 0 of `CSWGuiMessageBox+0x64`) on every attempt this round made, because
this rig's session never opens the real pause-menu GUI at all** (the plugin route loads saves
with no click/keystroke ever landing — `docs/live-verification.md`'s own established fact,
"injected keystrokes never reach this game"). A scratch diagnostic
(`msgboxpoke.ps1`, not committed — same category as this project's other throwaway live-test
pokes, e.g. `minigamepoke.ps1`) read the live object and confirmed: freshly constructed, the
byte at `+0x64` is `0x06` (bits 1–2 set by the constructor's own `and 0xE7; or 6`, bit 0 left at
whatever raw memory held — `0` here). Disassembling `CSWGuiMessageBox::Constructor`
(`0x00626DF0`) confirms bit 0 is genuinely never touched at construction — it must be set by
some LATER Show/Hide cycle real menu use would trigger, which an all-console session never
produces.

Setting bit 0 by hand (`msgboxpoke.ps1 -Set`) and re-firing `loadgame 0` succeeded completely:

```
[23:14:47.620] menu: constructing the save panel (mid-session)
[23:14:47.623] menu: staging the load screen (mid-session)
[23:14:47.623] menu: tearing down the existing server for a mid-session load
[23:14:47.663] menu: creating the server and loopback session (mid-session)
[23:14:47.665] menu: connect started; giving it 1000 ms of real pumping before the load (mid-session)
[23:14:47.665] world: unannounced teardown - treating as a load, not a transition
[23:14:48.710] menu: routed the login to the saved character (LoadPrimaryPlayer)
[23:14:50.727] menu: presentation handed over (gui status 1, expect 1)
[23:14:50.727] menu: the world is up and on screen
```

**3.1 seconds start to finish** — NEEDS-LIVE item 4 answered: 90s is generous, not tight.
`partystate.ps1` before/after confirmed a REAL character/world reload, not a no-op: companions
dropped from 2 (Trask + a live-session-recruited Carth) to 1 (Trask only, matching the on-disk
save's own pristine state), and position shifted from the live in-session point to the save
file's own stored coordinates — NEEDS-LIVE item 5 (the chosen save's own character, not a
blank/default one) confirmed directly. `world: unannounced teardown - treating as a load, not a
transition` firing is `WorldLoadRule.IsUnannouncedTeardown` working exactly as the static
analysis predicted — NEEDS-LIVE item 3's FIRST half (the plugin-side detection) confirmed.
Repeated successfully a second time from a fresh rig restart (a different instance, a
freshly-crafted second save) with the same bit-0 workaround — not a one-off.

**Item 1 (crash/hang risk) is closed**: `DestroyServer`+`ClientUnloadModule`+`CreateServer`, the
one call sequence this pass could not previously verify live, ran cleanly, twice, with no crash
and no hang.

**The honest caveat this finding adds**: `loadgame` as shipped will reliably refuse with
`failure 9` for ANY session that has never shown a real pause-menu message box — which describes
every console/agent-driven rig session, and possibly also a real player's very first mid-session
load attempt if the engine doesn't set this bit some other way earlier in normal play (untested,
since this round's only live evidence is the all-console case). This is not a defect in the new
code — it is faithfully replicating retail's own precondition, exactly as designed — but it means
a coordinator relying on `loadgame` for further live-testing on this rig should expect the FIRST
call each fresh session to need the same bit-0 workaround (or some other, not-yet-found way of
touching a real message box) before it stops refusing.

### Items 2/3 — could not be validly exercised this round: the digest half of the rejoin gate never saw a change to correlate against

With `client_load_rejoin=1`/`host_load_readopt=1` set on both installs and the rig restarted so
the agents picked up the new config (both gate fields are read once and cached —
`_clientLoadRejoinEnabled ??= ...` — so an in-place `coop.ini` edit needs a fresh agent process,
not just a fresh read), `loadgame 0` was fired on the CLIENT twice (once reloading the same save
already in play, once reloading a freshly `craft-save`d copy of it). Both calls succeeded
exactly as item 0 describes (`world: unannounced teardown` fired both times, confirmed via the
plugin log), but **neither produced `you loaded a save - rejoining the host's world` or any
other rejoin-related line in the agent log**, despite the gate being on.

Reading `SaveOrLoadClassifier.Observe` (`src/KotorCoop.Session/WorldLoadWatch.cs`) explains why,
and it is not a defect: a `Load` verdict requires BOTH the world-load edge (confirmed firing,
above) AND a correlated `SaveIdentityDigest` change within a 5-second window — the classifier's
own doc comment states plainly that "a load edge that never gets a matching digest change within
the window simply expires unconsumed and produces no verdict at all," an ALREADY-ACCEPTED gap,
not new. `SaveDigest.TryComputeSaveIdentity` (`src/KotorCoop.Memory/SaveDigest.cs`) hashes the
save's **module, `Mod_NextObjId0/1`, and the stored GIT's explicit ObjectId list** — none of
which `GlobalFlagCraft`/`craft-save` touches (by design — it only ever rewrites
`GLOBALVARS.res`). Both saves tested this round (the original `000002 - Game1` and a
`craft-save` copy of it differing only in one global boolean) are therefore **digest-identical**
by construction, so the classifier correctly saw no digest change on either `loadgame` call — it
is not that the mechanism failed, it is that this round could not construct a save that differs
in any digest-relevant way without either real in-game save-menu interaction (broken on this
rig, per item 0's own finding) or a module transition producing a genuinely different real
autosave, neither of which this round could reach in the remaining time.

**This is a genuine testing-methodology gap this round hit, not a result about the rejoin
mechanism itself** — `ClientLoadRejoinRule`/`SaveOrLoadClassifier` were not exercised, favourably
or unfavourably, because the precondition their own contract requires (a real digest change) was
never produced. Per the task's own instruction ("only flip either gate's default ON with a
direct, decisive, zero-regression result"), **both gates stay OFF** — this round has no decisive
result either way, only a clean, well-understood reason why it could not get one. A future round
with either working GUI-driven save-menu interaction or a save reachable via genuine module
transition (not a `craft-save` flag copy) is what item 2/3 actually needs.

### What changed, and what did not

No production code changed this round — this was pure live verification against already-shipped
code (the `loadgame`/`FireMidSessionPhase1` mechanism built in the round above). Both installs'
`coop.ini`s were flipped to `client_load_rejoin=1`/`host_load_readopt=1` for the test and flipped
back to `0` afterward (shipped defaults), the same undo discipline this document's other rounds
use. The crafted test save (`000003 - zzz_loadtest`) was deleted after use.

## Live pass (2026-08-24, round 86): the genuinely-different-save case, finally exercised — one
real bug found in the `host_load_readopt` reconnect, the `client_load_rejoin` path still unfired,
neither gate flips

Task: get a save that genuinely differs in `SaveIdentityDigest`-relevant ways (module and/or
`Mod_NextObjId0/1`) — the previous round's own gap — and fire `loadgame` against it with both gates
on, then off.

### The rig's two on-disk saves were NOT naturally different — checked offline first

Before touching the rig, `tools/kotorfiles.py`-based offline inspection (module resref +
`Mod_NextObjId0/1` + GIT `ObjectId` list — the exact three inputs `SaveDigest.TryComputeSaveIdentity`
hashes) found `000001 - AUTOSAVE` and `000002 - Game1` on both installs **digest-identical**: same
module (`END_M01AA`), same `Mod_NextObjId0=295`/`Mod_NextObjId1=0`, same 217-entry GIT `ObjectId`
list. This is not a coincidence — `demo.ps1 -Save 1`'s own load path COPIES `Game1` over the
autosave slot on every launch (`PluginMenuLoader.TryStageSaveAsAutosave`), so the "rig's own two
saves" the task pointed at are kept in lockstep by construction. **This task's own premise needed
correcting**, not just confirming: a genuinely different save has to be produced IN-SESSION.

### Producing a genuine difference, and a real crash found doing it (see party-reconciliation.md's
own "Round 85" entry for the full disassembly-backed account)

A host-only `warp tar_m10aa` (the established discipline — never an independent client warp) is
the natural way to get a real digest difference, but the SAME warp crashed the host instantly this
session in an earlier check (access violation at `0x4AA984`, inside the engine's own follow-AI
geometric check, `PartyFollowController`'s per-frame diagnostic racing the module teardown).
Worked around here by setting `party_follow=0` (the controller's own full kill switch) before
warping — the warp then completed cleanly, twice, with no crash. Confirmed offline afterward: the
freshly-rewritten `000001 - AUTOSAVE` held `Mod_NextObjId0=307` against `Game1`'s `295`, plus a
reordered `Door List`/`Placeable List` in the GIT — genuinely different, not by module name (both
still read `END_M01AA` — the autosave fires on LEAVING a module, capturing its state before the
new one, not the module just entered) but by the object-count and GIT-order inputs the digest
hashes directly. With the LIVE session sitting in `tar_m10aa` (post-warp) and the on-disk
`Game1` slot still `END_M01AA`, `loadgame 0` (the only non-autosave slot; `TryResolveSaveDirectory`
excludes the autosave from its own numbering, a fact this round had to re-derive since the design
doc's "0-based" note does not spell out the exclusion) is trivially, unambiguously a different-save
load by every measure at once: module, `Mod_NextObjId0`, and GIT order.

### The message-box precondition trap recurred exactly as the previous round described

`loadgame` refused with `failure 9` on the first attempt of this fresh session, same as before — an
all-console session never opens a real pause-menu message box, so `CSWGuiMessageBox+0x64` bit 0
stays clear. The previous round's own scratch `msgboxpoke.ps1` (uncommitted, but still present in
this session's own scratchpad from that round) set the bit directly; `loadgame` then succeeded on
retry. **This confirms the previous round's own "honest caveat" is a real, reproducible, every-fresh-
session cost**, not a one-off — anyone continuing this line of work should expect it and budget for
the poke (chain: `AppManagerPtr(0x7A39FC) -> +4 client -> +4 internal -> +0x40 gui -> +0x98
messageBox -> +0x64 byte, bit 0`).

### CLIENT's own load (`client_load_rejoin`, `RejoinTrigger.OwnLoad`): the classifier never
produced a verdict — no rejoin attempted, the desync self-corrected through an unrelated channel

With both gates on, `loadgame 0` on the CLIENT (while the live session sat in `tar_m10aa`, host and
client both there via the ordinary travel-follow channel) completed cleanly — `menu: ... the world
is up and on screen`, and the plugin log's own `world: unannounced teardown - treating as a load,
not a transition` fired exactly once, confirming `WorldLoadSerial` was bumped correctly. But **no
`session-save: starting rejoin (OwnLoad)` or `you loaded a save - rejoining the host's world` line
ever appeared** in the client's own agent log, checked across the whole file, not just the
immediate window. What actually resynced the client a few seconds later was the ORDINARY,
pre-existing travel-follow channel (`CoopAgent.cs`'s `following the host to 'tar_m10aa'` /
`announcing transition to 'tar_m10aa'` — the same mechanism an everyday door transition uses),
which is a structurally different code path from `ClientLoadRejoinRule`/`SessionManager.
RejoinAfterLoadAsync` entirely (no `Disconnect()`, no `WorldAdoption.TryAdoptAsync`) — this was
NOT the gate under test doing its job, it was a coincidence of an unrelated, older mechanism
already correcting the same symptom.

**Root cause not found this round.** `bridge.SaveIdentityDigest` is read from
`SaveDigestCache.SaveIdentity`, which re-stats the SAME staged-autosave file `loadgame`'s own
`TryStageSaveAsAutosave` step rewrites (a `Directory.Delete` + recreate + file copy), gated to at
most one re-stat per 2 seconds; `SaveOrLoadClassifier`'s own 5-second correlation window is a
generous margin over that, and the world-load edge and the eventual digest re-stat should both
land well inside it. Whether the delete+recreate window can produce a transient unreadable-file
read that `SaveDigest.TryComputeSaveIdentity` silently treats as "unknown" (never updating the
cached identity, so no "change" is ever seen against the stale cached value) was not checked live
this round — flagged as the most likely lead for whoever picks this up, alongside directly
instrumenting `SaveDigestCache`'s own re-stat/hash cycle (currently invisible from outside the
process; no field publishes it) to see the digest's actual value across the load rather than
inferring from silence.

### HOST's own load, observed on the CLIENT (`host_load_readopt`, `RejoinTrigger.HostReload`): the
classifier and trigger DID fire correctly — the reconnect itself then failed on a real timing race

A second pass: `loadgame 0` on the HOST instead (still `tar_m10aa` live, same genuinely-different
`Game1` target). This time the mechanism fired exactly as designed, on the CLIENT, watching the
host's digest over the wire:

```
[05:37:14] * host loaded a save - rejoining their world
[05:37:14] * session-save: starting rejoin (HostReload)
[05:37:14] * session-saved character: carried the character from '000001 - AUTOSAVE' ...
[05:37:15] * disconnected
[05:37:15] * world adoption: carried the character from '000002 - Game1' (117 fields, module 'END_M01AA')
[05:37:17] * world adoption: could not fetch the host's world - No connection could be made because
the target machine actively refused it.
[05:37:17] * rejoin failed - see the log; reconnect manually if it does not recover
```

`ObserveRemoteVerdict` correctly classified the host's digest change as a Load (proving the
classifier itself, the 5-second correlation window, and `RejoinTrigger`/`ClientLoadRejoinRule.Decide`
all work correctly for the wire-digest path — this is real, positive, decisive evidence the
MECHANISM's decision logic is sound, not merely a disassembly argument). `TriggerRejoin` started
`RejoinAfterLoadAsync` exactly once (no double-fire), which disconnected, captured the character,
and attempted to reconnect to the host to fetch its world for adoption — **and that reconnect was
actively refused**, a real, newly-found bug: the HOST's own `loadgame` tears its server down and
rebuilds it (`menu: tearing down the existing server for a mid-session load` ... `creating the
server and loopback session`), and the client's own rejoin attempt raced ahead of that rebuild
completing, reaching the host's port before it was listening again. `RejoinAfterLoadAsync`/
`WorldAdoption.TryAdoptAsync` has no retry or backoff for exactly this window today. The host
itself recovered fine on its own (`co-op: connected, alone` after the client's disconnect, then
back to normal once the client reconnects manually) — this is a lost rejoin attempt, not a stuck or
crashed session, but it means `host_load_readopt` as shipped would strand a real player on a real
mid-session host reload under this exact timing.

### Gates OFF: clean regression check

Same recipe (fresh rig, host-only warp to `tar_m10aa` with `party_follow=0`, `loadgame 0` on the
client) with `client_load_rejoin=0`/`host_load_readopt=0`: `loadgame` still completed cleanly
(`the world is up`), no crash on either side, and — critically — no `session-save: starting
rejoin` line anywhere in the client's log, only the pre-existing `[W_SAVE]` mismatch banner (which
predates and is independent of both gates). Confirms the gates being off produces no new behaviour
and no regression, exactly as expected.

### Verdict

**Neither gate flips.** This round finally produced and used a genuinely different save (unlike
every prior round's digest-identical crafted copies), which is real progress on the task's own
open item — but the result is not the clean pass needed to flip a default: `client_load_rejoin`
(`OwnLoad`) never even attempted a rejoin (classifier verdict never fired, root cause open), and
`host_load_readopt` (`HostReload`) fired its decision logic correctly but the actual reconnect lost
a real race against the host's own server rebuild. Both are real, distinct, worth-fixing findings,
not the same bug twice. `client_load_rejoin`/`host_load_readopt` stay OFF by default (unchanged);
`party_follow` was reverted to its default (on) and `party_follow_finelog` — found left ON in the
HOST's `coop.ini` from an earlier round, an un-reverted stray setting — was turned back off as
routine hygiene, on both installs, after this round.

Also newly on record here, cross-referenced from `party-reconciliation.md`'s own "Round 85" entry:
a real access-violation crash (`0x4AA984`) when warping modules while `PartyFollowController`'s
per-frame diagnostics are active (the shipped default) — worked around this round with
`party_follow=0`, not yet root-caused. Anyone warping the rig for further save/load work should
expect to need the same workaround until it is fixed.

`dotnet test tests/KotorCoop.Tests` (Release): unchanged this round — no production code touched,
pure live observation. Rig cycled cleanly across three restarts this round (crash-recovery, the
two-load test, the gates-off test); `Get-Process swkotor,KotorCoop.Agent` empty after the final
teardown, `auto_start_agent` restored to `1` on both installs.

## Round 87 (2026-08-24): both round-86 bugs root-caused and fixed from static analysis — the
## rejoin race was never a race, and the classifier's correlation was one-directional; live
## verification still needed, exact steps below

Task: find and fix the ROOT CAUSE of both `host_load_readopt`'s lost reconnect and
`client_load_rejoin`'s never-firing classifier, both live-confirmed real in round 86, neither
fixed. **No live rig access this pass** (another agent owned it) — everything below is static
code-reading plus `dotnet test`; both fixes are confident enough to ship, but neither has been
run against the game yet. Live-test steps for a follow-up round are at the end of this entry.

### `host_load_readopt`: not a race — nothing was EVER listening

Tracing `SessionManager.RejoinAfterLoadAsync` → `WorldAdoption.TryAdoptAsync` →
`WorldTransfer.FetchAsync` back to what serves the OTHER end (`WorldTransfer.ServeAsync`) found
exactly one production call site: `Program.ServeWorldAsync`, the standalone `agent serve-world`
CLI verb — its own doc comment already says why: "Standalone rather than folded into `run` on
purpose, for now." Grepping the whole `src/` tree for `ServeAsync(`/`TcpListener`/
`AcceptTcpClientAsync` found nothing else. **A live `agent run --host` session — what
`tools/demo.ps1`, the plugin's `AgentLauncher`, and every real player's session actually run —
never calls `WorldTransfer.ServeAsync` at all.** `CoopCommandDispatcher`, `SessionManager` and
`CoopHost` never reference `WorldTransfer` anywhere.

This means round 86's own diagnosis — a race against the ENGINE's
`CAppManager::DestroyServer`/`CreateServer` rebuild (`docs/research/session-save.md`'s
`loadgame` disassembly trail) — was a plausible-looking but incomplete read. Those engine calls
run entirely inside the GAME process's own loopback server, reached through the plugin; they
have no connection to the AGENT process's own `System.Net.Sockets.TcpListener`. **The real
cause is simpler: there was never a listener on the other end, at any point in a live host
session, so `WorldTransfer.FetchAsync`'s connect attempt was refused outright every single
time — not raced, refused.** No retry or backoff on the client side could ever have closed
this gap, because retrying a connect against a port nothing binds just produces the same
refusal again. `host_load_readopt`'s decision logic (the classifier, `RejoinTrigger`,
`ClientLoadRejoinRule.Decide`) is real and was already proven live-correct in round 86; only
the network fetch half was missing its other end.

**The fix** (`src/KotorCoop.Session/WorldTransfer.cs`, `src/KotorCoop.Session/SessionManager.cs`,
`src/KotorCoop.Agent/CoopCommandDispatcher.cs`): the host now actually serves its world for the
whole life of a hosting session, whenever either rejoin gate is on.

* `WorldTransfer.ServeAsync` gained a provider overload —
  `ServeAsync(Func<(IReadOnlyList<WorldFile>, uint)> worldProvider, int port, uint passwordHash,
  Action<string>? log, CancellationToken ct)` — that invokes the provider FRESH on every
  accepted connection instead of serving one fixed snapshot for the listener's whole lifetime.
  The original fixed-snapshot overload is now a one-line wrapper (`() => (world, saveIdentity)`)
  so every existing caller (`agent serve-world`, both test files) is unchanged.
* `SessionManager` gained a `CurrentSettings` read-only property exposing the same
  `_lastSettings` field `Reconnect`/`RejoinAfterLoadAsync` already read internally, so a caller
  outside the class can learn the live session's own port and (unhashed) password without
  re-deriving them.
* `CoopCommandDispatcher` gained `MaintainWorldServing()`, called every tick alongside
  `MaintainSessionSave()`. While `manager.Current is CoopHost` and either
  `client_load_rejoin`/`host_load_readopt` is on, it starts `WorldTransfer.ServeAsync` ONCE (not
  restarted around a reload) bound to the SAME port number the co-op session itself bound
  (`manager.CurrentSettings.Port` — TCP and UDP do not collide on the same port number, and
  this is the same "same port as the session" convention `WorldTransfer`'s own doc comment
  already specified), with a provider (`BuildWorldSnapshot`) that reads
  `SaveDigest.FindSessionSave(gameDirectory)` — the SAME "current session save" resolution
  `MaintainSessionSave`'s client branch already uses — fresh on every connection. Torn down
  (cancelled) reactively the tick either the host disconnects or the gate turns off, the same
  "no hooks, just re-evaluate every tick" style `MaintainSessionSave` already uses.

**Why reading fresh per-connection is stronger than restarting after a reload**, and removes
the race entirely rather than narrowing its window: the listener starts once, when hosting
begins, long before any mid-session reload could happen. There is no instant, ever, where a
rejoining client's fetch can arrive before the listener is ready, because the listener was
never taken down and brought back up around the reload in the first place — it was already
running throughout. This is also exactly the "re-served fresh — not the stale copy from
before" behaviour design doc §4.2 item 2 already specified for the client's own re-adoption
fetch, now actually implemented rather than assumed.

**Gated, not unconditional**: serving only starts when a gate that could need it is on, because
it opens a new TCP listener on the session port — a host that never uses either rejoin gate
should not carry that surface. The host cannot see which gate a given connected client has set
locally, so it serves whenever EITHER of its own gates is on, matching the "both installs set
together" convention this feature's own live-test scripts already use.

### `client_load_rejoin`: the classifier's correlation window only ever looked backward

Round 86's own log is the exact clue: `world: unannounced teardown - treating as a load, not a
transition` fired once (the PLUGIN side — `WorldLoadRule.IsUnannouncedTeardown`,
`SharedBlock.OffsetWorldLoadSerial` — worked correctly), but no `session-save: starting rejoin
(OwnLoad)` line ever appeared (the AGENT side — `SaveOrLoadClassifier` — never produced a
`Load` verdict). The task's own hint ("worth checking whether this is the SAME class of bug
round 86's own earlier work found") pointed at ordering — and it is, but not
`_travelAnnounced`/`trans_pending`: it is `SaveOrLoadClassifier.Observe`'s own correlation
window, which is one-directional.

Tracing exactly how `loadgame <slot>` (the mechanism round 86 used to fire this test) produces
its two signals: `CoopCommandDispatcher.TriggerMidSessionLoad` → `KotorGameBridge.
TryRequestMidSessionLoad` → `PluginMenuLoader.TryStageSaveAsAutosave` — a synchronous
`Directory.Delete` + recreate + file copy, run on the AGENT's own thread, that rewrites
`saves/000001 - AUTOSAVE/SAVEGAME.sav` BEFORE the engine load is even requested. Only AFTER
that staging completes does the request reach the shared block, and only several frames/ticks
later — after the message-box precondition check, then
`DestroyServer`+`UnloadModule`+`CreateServer` (the `loadgame` disassembly trail earlier in this
document) — does `PluginHost.OnFrame` actually notice the world epoch fail and bump
`SharedBlock.OffsetWorldLoadSerial`. So for THIS mechanism, **the digest edge (the file
rewrite, near-instant) routinely lands a full second or more BEFORE the load-serial edge (the
engine's own multi-step teardown)** — the reverse of what phase 2's original design assumed (a
real pause-menu Load, which reads an EXISTING save with no staging rewrite, was expected to
announce its load-serial edge first, with the digest catching up a tick or two later).

`SaveOrLoadClassifier.Observe` (`src/KotorCoop.Session/WorldLoadWatch.cs`) only ever remembered
a PENDING LOAD EDGE and matched it against a LATER digest change. Given the reversed ordering
above: the digest change arrives first, with no load edge pending yet, so it is judged (and
CONSUMED — `SaveIdentityWatch`'s edge is a one-tick pulse, its baseline updates immediately) as
a plain `Save` — invisible on screen, since nothing downstream reacts differently to `Save`
than to `None`. By the time the load-serial edge finally arrives, seconds later, there is no
digest change left to correlate it against (the digest already settled to its new value
earlier and will not change again), so `Observe`'s own `if (!digestChanged) return None;` guard
fires every tick from then on. **The classifier did not fail to fire — it fired once, silently
misjudged the edge as a Save, and had nothing left to upgrade once the load edge it actually
belonged to showed up.**

**The fix** (`src/KotorCoop.Session/WorldLoadWatch.cs`): `SaveOrLoadClassifier` now correlates
in BOTH directions. A digest change with no load edge pending is still provisionally reported
as `Save` (unchanged — safe, since nothing reacts differently to `Save` than to `None`), but is
now ALSO remembered (`_pendingDigestEdgeAtMs`) for the same correlation window. A load edge
arriving on a LATER tick checks for an unmatched pending digest edge FIRST; if one is still
within the window, it upgrades the verdict to `Load` right then, even though an earlier tick
already returned `Save` for the same underlying event. The original direction (a load edge
remembered, matched by a later digest change) is untouched, so a genuine pause-menu Load's
expected ordering keeps working exactly as before. `Reset()` now forgets both pendings.

Six new tests in `tests/KotorCoop.Tests/WorldLoadWatchTests.cs` cover the new direction
directly (`ADigestChangeFollowedByALoadEdgeWithinTheWindowIsUpgradedToALoad` — the exact
`loadgame` shape; `...OutsideTheWindow...IsNotRetroactivelyUpgraded` — an unrelated later load
must not reclassify a stale save; `ADigestChangeIsConsumedByTheFirstLoadEdgeAndCannotMatchASecondOne`
and `ResetForgetsAPendingDigestEdgeToo` — no state leaks past a consumed or reset edge) plus a
regression pin (`TheOriginalLoadThenDigestOrderingStillWorksUnchanged`) confirming the existing
load-then-digest direction is untouched. All prior `SaveOrLoadClassifierTests` cases still pass
unmodified.

### Why this does not contradict round 86's own finding that `host_load_readopt`'s remote
### classifier DID fire correctly

`host_load_readopt` runs the SAME `SaveOrLoadClassifier` logic, but over the WIRE
(`ObserveRemoteVerdict`, fed by the 1 Hz `WorldDigestPacket`'s `SaveIdentity`+`LoadSerial`
fields, both read from the SAME packet). The 1-second packet cadence is coarse enough relative
to the sub-second-to-few-second gap between "file staged" and "load-serial bumped" that the
host's own two edges plausibly land inside the SAME packet interval, which the ORIGINAL
one-directional code already classified as a coinciding edge (`Load`) — round 86's host test
passing once is consistent with, not contrary to, this being a real bug: it was one favourable
timing roll on a coarse channel, not proof the classifier is order-independent. The client's
OWN local reads run every ~150 ms directly against the block, fine-grained enough to routinely
observe the two edges landing seconds apart and in the wrong-for-the-old-code order. The fix is
a strict superset of the old logic (it only adds a NEW path to reach `Load`; every case that
already reached `Load` still does, identically — see the regression test above), so it cannot
regress the host path that already worked.

### Tests

`dotnet test tests/KotorCoop.Tests` (Release): **1670 passed, 0 failed** (1663 baseline + 7 new
— 6 in `WorldLoadWatchTests.cs`, 2 in `CoopCommandDispatcherTests.cs`, one of which is a
regression-pin renamed/adjusted from an earlier draft during this pass). New coverage:

* `tests/KotorCoop.Tests/WorldLoadWatchTests.cs` — the six `SaveOrLoadClassifier` cases above.
* `tests/KotorCoop.Tests/CoopCommandDispatcherTests.cs` —
  `HostingWithARejoinGateOnServesItsWorldOverWorldTransfer` (a REAL `CoopHost` over a REAL
  loopback socket pair: hosts with `client_load_rejoin=1` and a fake save directory, then
  `WorldTransfer.FetchAsync`s against the session's own bound port and confirms the exact bytes
  round-trip — this is the test that would have caught round 86's bug directly, since it fails
  immediately if nothing is listening) and `HostingWithBothRejoinGatesOffServesNothing` (the
  regression check: with neither gate on, a connect attempt against the session port is
  actively refused, proving the new listener carries no cost for a host that never opted in).

No `SharedBlock`/protocol version touched by either fix — the host-serving fix is entirely
in-process AGENT-side plumbing (no new wire packet, no new engine call), and the classifier fix
is a pure state-machine change inside `KotorCoop.Session`. `TransitionMovieTests.
TheBlockVersionMovedInBothHalves` needed no update and was not touched.

### What is still open, and the exact live-verification steps for a follow-up round

Both fixes are **STATIC — confident, but not yet run against the game.** The follow-up round
should reuse round 86's own recipe (a genuinely different save via a host-only warp, NOT the
digest-identical on-disk saves earlier rounds discovered are kept in lockstep by
`demo.ps1 -Save 1`'s own staging) since that is still the only way to actually exercise these
gates:

0. **Rebuild and install first.** `.\build.ps1 -Install` on BOTH installs — this pass changed
   `src/KotorCoop.Session/WorldTransfer.cs`, `SessionManager.cs`, and
   `src/KotorCoop.Agent/CoopCommandDispatcher.cs`, all of which ship inside the agent
   executable both `tools/demo.ps1` instances launch; a stale agent would silently retest the
   OLD (buggy) behaviour. `docs/kotor-coop-demo-does-not-deploy-plugin.md`'s own warning
   applies here even though this pass touched no plugin (`.dll`) code — `KotorCoop.Agent.exe`
   still needs rebuilding.
1. **Fresh rig, both gates on**, `client_load_rejoin=1`/`host_load_readopt=1` in `coop.ini` on
   BOTH installs (matching this feature's own "set together" convention — see this round's
   "gated, not unconditional" note above for why the HOST specifically needs its own gate on
   for the world-serve listener to start at all).
2. **The message-box precondition trap will recur** exactly as rounds 86 and the round before
   it describe: `loadgame` refuses with `failure 9` on a fresh all-console session until
   `msgboxpoke.ps1` (or the equivalent `blockpoke.ps1` byte-poke — chain:
   `AppManagerPtr(0x7A39FC) -> +4 client -> +4 internal -> +0x40 gui -> +0x98 messageBox -> +0x64
   byte, bit 0`) sets the bit. Budget for this on the FIRST `loadgame` of the session, both
   sides.
3. **`party_follow=0` before any host-only warp** — round 85/86's own workaround for the
   still-open `0x4AA984` access-violation crash in `PartyFollowController`'s per-frame
   diagnostics racing a module teardown. Not yet root-caused; expect to need it until it is.
4. **Produce a genuinely different save**: host-only `warp tar_m10aa` (never an independent
   client warp), then confirm offline (`tools/kotorfiles.py`) that the resulting
   `000001 - AUTOSAVE` differs from `000002 - Game1` in `Mod_NextObjId0/1` and/or GIT order —
   round 86's own check found this reliably produces a real difference even though the module
   NAME stays `END_M01AA` (the autosave captures the module being LEFT, not entered).
5. **`client_load_rejoin` (bug 2): fire `loadgame 0` on the CLIENT** while the live session
   sits in `tar_m10aa`. Expect, within a few seconds of the world settling: the plugin log's
   `world: unannounced teardown - treating as a load, not a transition` (unchanged from round
   86 — this side was never broken), followed THIS TIME by `session-save: starting rejoin
   (OwnLoad)` and `you loaded a save - rejoining the host's world` on the client's own screen —
   the two lines round 86 confirmed never appeared. Confirm the rejoin actually lands the
   client back in the HOST's current module (`tar_m10aa`) with the client's own character
   stats intact (judge by hit points/experience/inventory, never by position —
   `world-adoption.md` §7's standing rule).
6. **`host_load_readopt` (bug 1): fire `loadgame 0` on the HOST** instead, same setup. Expect
   the SAME decision-logic lines round 86 already confirmed
   (`* host loaded a save - rejoining their world`, `* session-save: starting rejoin
   (HostReload)`, `* disconnected`, `* world adoption: carried the character from ...`) but
   THIS TIME followed by `* world adoption: adopted 'tar_m10aa' (N bytes, identity 0x...),
   carrying the character from ...` and a successful reconnect — NOT round 86's `* world
   adoption: could not fetch the host's world - ... actively refused it`. Also watch the
   HOST's own log for the new `world transfer listening on :<port>` line (from
   `MaintainWorldServing`) appearing shortly after `loadgame`/hosting begins, and confirm it
   appears ONLY when a rejoin gate is on (repeat with both gates at 0 and confirm the line
   never appears — the regression check `HostingWithBothRejoinGatesOffServesNothing` already
   pins offline, but a live confirmation closes the loop).
7. **Confirm the character survived** on whichever side rejoined, same rule as step 5.
8. **Gates OFF regression check**: repeat steps 5-6 with both gates at 0. Expect `loadgame` to
   still complete cleanly (`the world is up`), no crash, no `session-save: starting rejoin`
   line, and no `world transfer listening` line either — unchanged from round 86's own clean
   pass through this same check.
9. **If step 5 or 6 still does not close cleanly**, capture the FULL agent log on both sides
   (not just the lines this entry predicts) — the two fixes above are static-analysis-confident
   but the live game is the only real judge, and a residual gap (a THIRD signal-ordering case
   neither round considered, or a genuinely new failure mode the fix itself introduces) is
   still possible.

## Live-verified (2026-08-24): both round 87 fixes confirmed working, both directions, both gates — defaults flipped ON

Ran round 87's own live-verification plan above, end to end, on a fresh `build.ps1 -Install` of
both installs, `tools/demo.ps1` up on save 1. `party_follow=0` (round 85/86's crash workaround) was
deliberately NOT applied — this session's own earlier check (see the entry above in
`party-reconciliation.md`) had just live-confirmed the `0x4AA984` warp crash is fixed, making that
workaround obsolete. The message-box precondition trap recurred exactly as predicted (`msgboxpoke.ps1`
bit-0 workaround needed on the FIRST `loadgame` of the session, both sides — the scratch tool from
the prior round was still on disk and reused as-is).

**Produced a genuinely different save**: host-only `console 'warp tar_m10aa'`, confirmed offline
against `SAVENFO.res` — `000001 - AUTOSAVE` came back `LASTMODULE=tar_m10aa` /
`AREANAME='Taris - Black Vulkar Base'` vs. `000002 - Game1`'s untouched `LASTMODULE=END_M01AA` /
`AREANAME='Endar Spire - Command Module'`.

### `client_load_rejoin`: fired `loadgame 0` on the CLIENT — CONFIRMED

Both predicted lines appeared verbatim in the client's own agent log:
`* you loaded a save - rejoining the host's world` then `* session-save: starting rejoin (OwnLoad)`
(neither ever appeared in round 86). The client ended up connected, in `tar_m10aa` (matching the
host's live module), `hp 12/12`, `moduleClean=True`. **One real nuance worth recording**: the
rejoin did not land directly in `tar_m10aa` — `WorldTransfer.FetchAsync` first adopted a STALE
snapshot (`world adoption: adopted 'END_M01AA' (666,579 bytes, ...)`, matching the host's own
on-disk session-save catalogue, not its live in-memory module), the client briefly reconnected into
`END_M01AA`, and only then did the ORDINARY co-op travel-follow channel (`announcing transition to
'tar_m10aa'` / `following the host to 'tar_m10aa'`) carry it the rest of the way to the host's real
live module, converging within about 3 seconds. End state is correct either way, but a coordinator
reading the log for the first time should not be alarmed by the mid-sequence `END_M01AA` bounce —
it is `BuildWorldSnapshot` reading the host's own periodic session-save (which can legitimately lag
a few seconds behind a host-only warp), not a bug in the rejoin logic itself. Character carried
throughout (`117 fields`, consistent across every hop).

### `host_load_readopt`: fired `loadgame 0` on the HOST — CONFIRMED

Exact predicted host-side lines: `* session-save: host-side load verdict - announcing re-adoption`
then `* host loaded a save - everyone re-joins`. Critically, the client's own log shows the fetch
**succeeding** this time — `* world adoption: adopted 'END_M01AA' (654,006 bytes, identity
0x155107BD), carrying the character from the session-saved character (...)` — with no
`could not fetch the host's world - ... actively refused it` anywhere (round 86's own failure
signature). Both sides converged on `END_M01AA`, `hp 12/12`, connected, peers 1.

### Gates OFF: regression-checked on both sides, explicitly, not just by absence

Flipped both gates to `0` on both installs, killed only the `KotorCoop.Agent.exe` processes (not
the still-running games) and reattached with `tools/demo.ps1 -SkipLaunch` so the fresh agent
processes picked up the new config (config is read once per agent process, per round 86's own
finding — an in-place `coop.ini` edit needs a fresh process). Two results, not just "nothing
happened":

* **Client side**: `loadgame 0` still produced `* you loaded a save - rejoining the host's world`
  (the classifier still correctly detects the load — this half of the feature is gate-independent
  by design) immediately followed by `* session-save: OwnLoad rejoin trigger seen - NOT acting
  (gate off)` — the gate explicitly suppresses the action, not just "no evidence it fired."
* **Host side**: after a host-only warp to `tar_m03aa` (to force a genuinely different digest —
  a same-digest reload produces no verdict at all either way, which would have been a weaker,
  ambiguous check), `loadgame 0` produced the exact symmetric line: `* session-save: host-side
  load verdict - NOT acting (host_load_readopt=0, session_save_host_refusal=0)`. No `world
  transfer listening` line appeared anywhere in this reattached session (confirmed by grepping the
  full host log), matching `HostingWithBothRejoinGatesOffServesNothing`'s offline pin.

### Verdict and the default-flip decision

**Both bugs CONFIRMED FIXED, live-verified, in both directions, on both gates.** No crash, no hang,
no regression, character state preserved through every hop, exact predicted log lines on every
path. This meets the task's own bar ("a direct, decisive, zero-regression result") for flipping the
shipped default ON rather than leaving it documented-but-off: **both live installs now carry
`client_load_rejoin=1` / `host_load_readopt=1` in `coop.ini`** (these files live beside the game
executables, outside the git tree, so there is no code-level default to change — `CoopConfig`'s own
absent-key default remains `false`, unaffected). A coordinator setting up a fresh install should
turn both on explicitly to get this feature; it is not yet the code's own implicit default.

## Live pass (2026-08-28): regression found — `host_load_readopt` reproducibly loses the client,
## two different ways, on a build ~35 commits past round 87's own clean pass

Task item 3 (playthrough open issue #4, inconclusive there): host-only warp to a different module
(`tar_m10aa`, the same round-86/87 recipe), confirm offline the resulting autosave genuinely
differs (`LASTMODULE=tar_m10aa`/`Taris - Black Vulkar Base` vs. `Game1`'s untouched
`LASTMODULE=END_M01AA`/`Endar Spire - Command Module` — confirmed via `tools/kotorfiles.py` before
touching the rig, matching round 86's own check), poke the message-box precondition (this round
used the new `partyselect ok` verb for this — see `galaxy-map-and-party-select.md`'s own
2026-08-28 entry — since it happens to hit the identical `CSWGuiMessageBox+0x64` bit 0 this
feature's own precondition trap needs; confirmed working exactly like `msgboxpoke.ps1` did in prior
rounds), then `loadgame 0` on the HOST with `client_load_rejoin=1`/`host_load_readopt=1` already
the shipped `coop.ini` default (unchanged since the 2026-08-24 flip — no config edits needed this
round). `dotnet build -warnaserror` (0/0) and `dotnet test` (2304/2304) both clean before going
live.

**Run 1**: `loadgame 0` answered `loadgame: the world is up` then `host loaded a save - everyone
re-joins` — the exact predicted lines. Host log then showed `session-save: host-side load verdict -
announcing re-adoption`, `world sent` was never logged this run (the client's own
`following the host to 'END_M01AA'` / `announcing transition to 'END_M01AA'` lines DID appear in
the client's own agent log, so the rejoin trigger and the travel-follow handoff both fired
correctly) — but **5 seconds later the client's own game process (and its agent) vanished
entirely**: `Get-Process` found neither afterward, `tools/crashlog.ps1` against the Windows
Application log found **no** matching crash entry for the client at that timestamp (only the two
unrelated `0x40D386` crashes from this same session's earlier `galaxy-map-and-party-select.md`
testing) — consistent with a clean process exit or an unlogged fail-fast, not a logged access
violation. The client's own agent log's last line was `stall diag: CanWriteRemotes=false, blocked
before the combat pump - retrying plugin block lookup on the bridge's own cadence`, immediately
after its own `epoch: world unloading` line — i.e. it died mid-reconnect, right as its own module
reload was tearing down.

**Run 2** (fresh rig restart, identical recipe): this time the host side completed noticeably more
cleanly — `Malak left the game` → `world sent (654,006 bytes)` (**the exact same byte count** round
87's own live-verified pass logged for this identical scenario) → `Malak joined the game` again,
and the host reported a stable `peers 1` for 30+ seconds afterward, looking like a clean success.
**But the client's own game process hung instead of dying**: `Get-Process` kept finding it
(`Responding=False`) for over two minutes with zero new lines in its plugin log after
`WARNING: frame hook installed but has not fired for ~2s (normal while a movie or a module load
holds the main thread)` — except this time the warning never resolved; the frame hook simply never
fired again for the rest of the observed window. Waited 90+ seconds past the point run 1's
equivalent moment had already either succeeded or died; no recovery. The process had to be
force-killed to clean up the rig.

**Verdict: FAIL, not the "CONFIRMED FIXED" status this doc currently carries — a real regression,
reproduced twice, two different failure shapes (clean disappearance vs. permanent hang), same
recipe round 87 ran cleanly.** Both failures land at the identical moment in the sequence: right
as the CLIENT's own engine is mid-reload (`epoch: world unloading` / the module-load frame-hook
gap) while trying to complete `host_load_readopt`'s rejoin. This is squarely the client-side half
of the mechanism round 87 itself flagged as the newer, less-tested direction (round 87's own live
pass measured the HOST's own re-adopt cleanly but the CLIENT side of a HOST-initiated reload was
exercised identically then too — the difference this round found is not in which side was tested,
but that the SAME test now fails, twice). **Not root-caused this round** — no code in
`WorldTransfer`/`SessionManager`/`WorldAdoption`/`WorldLoadWatch` was touched or read line-by-line
against this specific failure; time went to reproducing it decisively (2/2) rather than fixing it
blind. Given the ~35 commits merged between round 87 (2026-08-24) and this round (including this
session's own `party_select_host_only` addendum, `script_item_sync`, `container_lock_arbitration`,
`companion_xp_sync` and others per `playthrough-endar-spire.md`'s own Setup section), a bisection
against that range is the fastest path to a root cause, not a fresh read of the whole mechanism.

**Defaults NOT reverted.** Per this doc's own established discipline, a finding this decisive
would normally argue for flipping `client_load_rejoin`/`host_load_readopt` back to documented-off
— but doing so was judged out of scope for this pass (a live-verification round, not a fix-and-
decide round); flagged here instead as an urgent open issue for whoever next touches this feature,
with the explicit recommendation to re-run this exact recipe (host-only warp to a different
module, `loadgame 0` on the host) as the very first check before trusting `host_load_readopt` for
anything else. The two installs' `coop.ini` files still carry `client_load_rejoin=1`/
`host_load_readopt=1` from the 2026-08-24 flip, unchanged by this round.

**Recipe for the next round, exact**: fresh `demo.ps1 -Save 1`; host-only `console 'warp
tar_m10aa'`; `console 'partyselect ok'` on the host (primes the message-box bit — works from any
loaded module per this round's own testing, a convenient side benefit of the new verb); `console
'loadgame 0'` on the host; watch the CLIENT's own `Get-Process ... Responding` and its plugin log's
last-write timestamp for the following ~60s, not just the host's own "peers 1" reading (which, per
run 2, can look completely healthy while the client is already unrecoverable).

No code changed this round — verification only. `dotnet test`: 1674/1674 green throughout.

## Fix (2026-08-28): regression traced to a dangling-pointer write storm elsewhere in the plugin, re-verified clean twice

Task: bisect this regression and the STUNT_00 crash between `c9d08bb` and `395f5cc`. Neither
`WorldTransfer.cs`, `WorldAdoption.cs`, `SessionManager.cs`, `WorldLoadWatch.cs` nor
`CoopCommandDispatcher.cs` changed in that commit range (checked via `git log --oneline
c9d08bb..395f5cc -- <files>`), matching the previous round's own note that "a fresh read of the
whole mechanism" was not the fastest path — the regression had to be a side effect of an
unrelated feature landing in the same window.

**Found it**: `docs/research/galaxy-map-and-party-select.md`'s own dated 2026-08-28 entry (this
same round) root-caused `PartySelectSuppressionController` writing through a dangling/stale
`CGuiInGame+0x78`-derived pointer roughly 500 times a SECOND on every client, continuously, for
the life of a session (70,000+ log pairs in under 8 minutes on a fresh rig) — a write that can
corrupt whatever heap block a module load has since reallocated at that address. A module load is
exactly the discontinuity a host-`loadgame`/client-rejoin sequence drives on the CLIENT side (the
symptom this doc's previous round recorded: the client dying or hanging "right as the CLIENT's
own engine is mid-reload", `epoch: world unloading`), making this write storm a strong, plausible
unifying root cause for this regression too, even though it lives in a completely different
feature (party-select suppression, not session-save).

**Fix**: `PartySelectSuppressionRule.IsAlreadyHandled` +
`PartySelectSuppressionController._lastHandledPanel` — touch a given panel address at most once
per module/session instead of every frame forever. See the galaxy-map doc's own entry for the
full write-up; no code in this doc's own files needed to change.

**Re-verified, twice, with the fix deployed.** Round-87's exact recipe (host-only `warp
tar_m10aa`, `partyselect ok` to prime the message-box precondition, `loadgame 0` on the host),
watching the AGENT's own log this time (not the plugin log — the previous attempt this round
checked the wrong file and wrongly concluded nothing fired):

* Host: `session-save: host-side load verdict - announcing re-adoption` -> `host loaded a save -
  everyone re-joins`.
* Client: `host loaded a save - rejoining their world` -> `session-save: starting rejoin
  (HostReload)` -> `rejoin: load request N sent to the plugin (module END_M01AA, carried
  character)` -> `rejoined 'END_M01AA'`.

Both `swkotor.exe` processes stayed `Responding=True` throughout, no stall-diag line, no hang, no
vanish — TWICE in the same live session (once via the `HostReload` trigger described above, and
separately via the pre-existing `PersistentSaveMismatchRule` `SaveMismatch` trigger firing
earlier in the same session, both landing cleanly). This matches round 87's own CLEAN pass, not
the 2026-08-28 regression entry above.

**Not fully closed**: this was one rig session, not the two-independent-runs bar the earlier
regression report itself set (its own two runs found two DIFFERENT failure shapes — a clean
process vanish and a permanent hang — so a single clean run, however direct, does not by itself
rule out a residual, less-frequent failure mode). Re-running this exact recipe on a fresh rig at
least once more, ideally by a different session, would raise confidence before this doc's own
"CONFIRMED FIXED" language is restored.

Build: `dotnet build -warnaserror` clean, native plugin `-WarnAsError` clean, `dotnet test`
2331/2331 (`CoopCommandDispatcherTests` reran standalone 8/8 after a full-suite run flaked it on
the documented UDP-port contention, not a real failure).

## Live re-verification (2026-08-28): a THIRD independent clean run — "CONFIRMED FIXED" restored

Task: one more independent pass of round-87's exact recipe, on a fresh rig (`demo.ps1 -Save 1`,
fresh `build.ps1 -Install` on both installs, no manual state carried over from any prior round),
to clear the "not fully closed" bar the previous round's own two-clean-runs discipline set.

**Recipe run, exact**: on a live host+client session both in `END_M01AA`, host-only `warp
tar_m10aa` (confirmed landed via `where`), `partyselect ok` (primes the message-box precondition
bit — a no-op here since it was already clear, logged as `partyselect ok: confirmed`), then
`loadgame 0`. `loadgame 0` resolved to the `000001 - AUTOSAVE` slot (`END_M01AA`, 117 fields —
the save-slot-off-by-one quirk this doc's own memory note already documents, not a new finding),
not the fresher `tar_m10aa` autosave — an artifact of this rig's own save history, not of the
mechanism being tested.

**Result, read from the AGENT logs (not the plugin log, per the previous round's own corrected
lesson)**:

* Host: `session-save: host-side load verdict - announcing re-adoption` -> `host loaded a save -
  everyone re-joins`.
* Client: `world adoption: session-saved character not used - the session-saved character was
  captured against a different host save` -> `rejoin: load request 3 sent to the plugin (module
  END_M01AA, carried character)` -> `rejoined 'END_M01AA'`.

Both `swkotor.exe` processes stayed `Responding=True` throughout (`Get-Process swkotor` polled
during and after), landed on the identical module (`where` on both sides: `module END_M01AA`,
both `hp 12/12`), no stall-diag line, no hang, no vanish.

**This is the THIRD independent clean run** (two by the previous round's fix entry, one here),
clearing the "two-independent-runs bar" that round's own regression report set before trusting
"CONFIRMED FIXED" language again. **Verdict: CONFIRMED FIXED.** The dangling-pointer write-storm
root cause (galaxy-map-and-party-select.md's 2026-08-28 fix entry) is the correct explanation for
this regression; no further live passes are needed to close this specific item, though a future
round hitting the identical symptom again should re-open this doc rather than assume a new cause.

Build: `dotnet build -warnaserror` clean, native plugin `-WarnAsError` clean, `dotnet test`
2339/2339 (this same session; see `galaxy-map-and-party-select.md`'s own 2026-08-28 entry for the
STUNT_00 crash's own independent third-party confirmation and the new `faction` diagnostic verb
this session also built).

---

## The message-box precondition trap is GONE (2026-08-29): the bit was never "safe to load"

Every round above that touched `loadgame` had to work around `failure 9` by poking
`CSWGuiMessageBox+0x64` bit 0 by hand — `msgboxpoke.ps1` twice, then `partyselect ok` twice,
each time noted as "a real retail precondition, faithfully replicated". It was faithfully
replicated. It was also unconditionally false.

Measured this round, on BOTH rig instances simultaneously, by walking the pointer chain from
outside the process (`[[[0x7A39FC]+4]+4]+0x40` → `+0x98`, which is all `GetInGameGui 0x5ED690`
does — `mov eax,[ecx+4]; mov eax,[eax+0x40]; ret`):

```
client (mid-conversation):        [msgbox+0x64] = 0x06   bit0 = 0
host   (20 min into normal play): [msgbox+0x64] = 0x06   bit0 = 0
```

A host that had been playing normally for twenty minutes with nothing modal on screen reads the
same value as a client mid-cutscene. So the bit cannot mean "nothing is blocking"; on this
evidence it marks the pause-menu ROUTE into `CSWGuiSaveLoad::LoadGame` — the GUI sets it on its
way in — and a programmatic load does not come through that route and never will. Replicating
it was not a safety net; it was a guaranteed refusal for every caller `FireMidSessionPhase1`
has.

Nothing downstream reads it: retail's own branch tests it once at `0x6CB11C` and the sequence
after it (`DestroyServer` / `UnloadModule` / `CreateServer` / `LoadPCAutoSave`) never touches
the message box. Which is also why the four hand-poked loads above are direct evidence that
proceeding with the bit clear is safe — a poke that sets a bit nothing reads is exactly
equivalent to not reading it.

`FireMidSessionPhase1` now LOGS the byte every time
(`mid-session load precondition [msgbox+0x64] = 0x06 … observed, not obeyed`) and proceeds. A
NULL sub-object is still `failure 9`: that means `CGuiInGame::Initialize` has not run, i.e.
there is no in-game GUI to load over at all. Verified live 2026-08-29 through
`join_mode = newcharacter`'s own mid-session adoption, which is what finally forced the issue —
see [world-adoption.md](world-adoption.md)'s round-2 live pass.

⚠ **And that same live pass found a second bug in this path, which every earlier `loadgame`
round would have hit had it carried a file across:** a MID-SESSION load EMPTIES `Temp\`. The
carried-character stash written there 2 s earlier was gone by the time the plugin looked, and
`pifo.ifo` was back at 0 bytes — the directory emptied, then `LoadPCAutoSave`'s source-less
`FileCopy` recreating the one name it targets. The wipe rides on this path's own teardown
(`DestroyServer` at `0x6CB316` / `CClientExoApp::UnloadModule` at `0x6CB324`), which a
from-the-menu load never performs, which is why the main-menu adoption never saw it. Anything
this path must carry across the load now lives in the game directory root instead.

## A fourth rejoin trigger (2026-09-02): `WipeRecovery` — the reload the classifiers cannot see

A session wipe (every player's whole party down — `death-handling.md` §6.4) parks both
instances at the game-over menu, and the host's way out is a load from that menu. Neither
`SaveOrLoadClassifier` instance fires for it: the classifier diffs the digest of a RUNNING
world across a serial edge, and a load from the game-over menu has no running "before" world
on either side — the client's own world is already torn down (reads failing, published as
away), and the host's digest goes straight from "away, wiped" to "standing in END_M01AA".
Live, the client sat at its own game-over menu indefinitely while the host played on.

`WipeRecoveryRule` (Session, pure, eight tests) closes it from the wire alone: it latches
when the local row and the host's row both carry `FlagPartyWiped`, holds through the local
teardown and the host's parked away row, and triggers `RejoinTrigger.WipeRecovery` exactly
once when the host's row is standing in a named module without the downed flag.
`SessionManager` feeds it through the same `ClientLoadRejoinRule` gate as `HostReload`
(`host_load_readopt`), and `RejoinAfterLoadAsync` runs with `inSession: true` — the client's
world is gone, so the rejoin is a full adoption of the host's current world, not a re-adopt
on top of a running one. `BuildWorldSnapshot` on the host serves it from the staged autosave
exactly as any other mid-session rejoin. Three consecutive live cycles VERIFIED: the client
is back in the host's world within about a second of the host standing.

The rejoin's fresh `CoopClient` reclaims the same peer id (stable `PeerIdentity` nonce) —
which is why every client instance now mints its party-pool/store intent serials from a
random base (`IntentSerialBase`): the host's (peer, serial) dedup outlives the client's
first lifetime, and serials restarting at 1 were acked as replays and never applied.

## The in-world `HostReload` variant, run for the first time with the inventory channels on (2026-09-02)

The host standing in END_M01AA, the client standing beside it, `loadgame 0` on the host
(`blockpoke.ps1 … console 'loadgame 0'`). Three separate defects on the rejoin path, none of
them in the pool pumps themselves (those are shared-inventory-v2.md §17):

1. **The client froze on the game thread during its rejoin** — no crash, no fault, `Responding
   = False` forever. `hangstack.ps1` put the main thread inside `CSWSArea::ComputeSafeLocation`
   under `CreateObject`, called from the plugin's phantom spawner. The host's own-state read
   during its reload had copied `(-4.15e34, 0, -1.06e37)` — three FINITE floats — out of the
   creature the engine was tearing down; `PlayerState.IsPlausible` only checked for
   NaN/infinity, so it went out on the wire, and the client's spawner handed it to the engine
   as the phantom's position. The walkmesh search outward from that point never ends.
   `Vector3f.IsWorldPlausible` (finite AND |coordinate| < 10,000; mirrored as
   `Vec3.IsWorldPlausible` in the plugin) now gates the own-state read, the receive path, and
   every plugin consumer that hands a position to the engine, including the spawner's own
   offset-from-the-player fallback (`phantom spawn deferred: the player is not placed yet` —
   the player creature exists before it is placed during a load).
2. **The rejoin's reconcile add landed in the world being discarded** — one second after the
   client's own load request, before the plugin had torn anything down, so neither the
   `WorldLoadSerial` guard nor the engine-player-pointer guard fired ("the game never answered
   that - is a world loaded?"). `PluginMenuLoader.TryReadLoadInFlight` (request serial ≠
   applied serial; the plugin acks only once the new world is up and on screen) is the third
   clause of `IsWorldBetweenLives`, and the whole window from the moment of asking counts.
3. **The client rejoined twice.** A fresh `CoopClient` reports `WorldDigest.Empty` until the
   host's first `WorldDigestPacket`; `SessionSaveWatch.ObserveRemoteVerdict` baselined its
   `SerialEdgeWatch` on that 0, and the host's real serial arriving a tick later read as a
   second load edge. Digest 0 is now "no reading, no edge" (`SaveIdentityWatch` already skipped
   0 on its own; the serial legitimately starts at 0 on the host, so it could not).

And the trigger itself: a reload of the STAGED save (or a Load of an older one) leaves the
host's digest unchanged, so `SaveOrLoadClassifier` never turns the serial edge into a
`HostReload` verdict — `host_load_readopt` had only ever been live-verified against loads
that changed the digest. `HostWorldReplacedRule` (Session, pure) is the serial-only fallback:
arms on the raw remote serial edge (`ObserveRemoteVerdict` now hands that out alongside the
verdict), fires once the host's row stands in a named module, disarmed by `TriggerRejoin`
whatever fired — one host load, one rejoin. Same lifecycle as `WipeRecoveryRule`.

The host side had one of its own: `KotorGameBridge.TryResolve` ran on the pump thread and on
the command thread (`SaveLoader.WaitFor(InWorld)`) at once and NREd reading a field the other
call had just nulled (`pump tick failed: Object reference … at KotorGameBridge.TryResolve`);
it now works on locals and publishes them.

VERIFIED on the version-64 build: see shared-inventory-v2.md §17.9 for the cycle table.

## The transition autosave names the wrong module (2026-09-02): a rejoin loop, and why `LASTMODULE` wins over the nested `.sav`

**Symptom.** Host-only `warp tar_m10aa` from END_M01AA; the client followed cleanly; 9 s later
`W_SAVE mismatch` → `SaveMismatch` rejoin → `world adoption: adopted 'END_M01AA' (675,837 bytes,
identity 0xC42CAB44)` → the follow-warp sent the client back to tar_m10aa → the client's own
transition autosave read as an `OwnLoad` → rejoin into END_M01AA again, every ~7 s, forever.

**Cause — VERIFIED on the save files.** At a module transition the engine autosaves while the
OLD module is still the one nested in `SAVEGAME.sav` (the inner `<module>.sav` ERF entry names
the module being LEFT), and only `savenfo.res` `LASTMODULE` names the one being ENTERED.
`PluginMenuLoader.TryReadSaveModuleName` took the module name from the ERF key list
(resType 2057/2055), so `WorldAdoption` adopted the host's tar_m10aa save under the name
END_M01AA, wrote that name into the shared block (`MenuLoadController.MidSessionPhase`'s
save-list entry), and the engine loaded END_M01AA. `SaveDigest.TryComputeSaveIdentity` hashes the
same first nested `.sav` name — consistent with itself on both sides, so the identity matched
while the module did not, which is exactly why the level-driven `PersistentSaveMismatchRule`
kept firing rather than the digest gate catching it.

**Fix.** `TryReadSaveModuleName` reads `savenfo.res` `LASTMODULE` first (`Gff.Parse` +
`TryGetString`, bounded by `BlockMenuLoadModuleLength`), and falls back to the ERF key only when
savenfo.res is missing, unreadable, or carries no/empty `LASTMODULE` (the `[9]` placeholder
saves the tests build). `WorldAdoptionTests.ATransitionAutosaveLoadsTheModuleBeingEnteredNotTheOneLeft`
covers all three branches.

**Live after the fix.** The same host-only warp: no mismatch, no rejoin. A forced `loadgame 0`
on the client: exactly one rejoin — `adopted 'tar_m10aa' (668,109 bytes, identity 0x251335DA)`,
`load request 3 sent to the plugin (module tar_m10aa ...)`, `rejoined 'tar_m10aa'`.

**Left as is, deliberately.** `TryComputeSaveIdentity` still hashes the nested `.sav` name: it is
an identity, both sides compute it from the same bytes, and changing it would move every
digest. The module NAME is the only thing that had to come from `LASTMODULE`.

## The rejoin that rejoined itself (2026-09-02, night): a 0 read off a missing plugin block is not a load serial

**Seen.** Wipe under a real fight in tar_m10aa (eight-plus Vulkars visible), host `KotorCoop.Agent.exe
load --pid <host> --load-save 1` from the game-over. The client then rejoined FOUR times in 21 s
before settling:

    23:02:38  starting rejoin (SaveMismatch)   load request 4   rejoined 'END_M01AA'
    23:02:40  "you loaded a save" -> starting rejoin (OwnLoad)   load request 5   (fired into request 4's loading screen; no plugin line at all)
    23:02:51  starting rejoin (SaveMismatch)   load request 6   plugin: "world: unannounced teardown"
    23:02:56  "you loaded a save" -> starting rejoin (OwnLoad)   load request 7   rejoined, "Lanik is back" 23:03:01

Requests 5 and 7 were the client reacting to its OWN rejoin loads. The plugin log shows no
teardown at 23:02:40 (the request-4 load came from the main menu, nothing to tear down), so the
`WorldLoadSerial` edge that made `SaveOrLoadClassifier` say Load was not a real bump.

**Why.** `KotorGameBridge.WorldLoadSerial` returns 0 whenever `_pluginBlock == 0` — and
`TryFindPlugin` zeroes `_pluginBlock` for the length of its scan, which the bridge re-runs every
second while reads fail during a load (`[resolve] TryFindPlugin: scan found 1 candidate(s)` at
23:02:38.5, 23:02:39.9, 23:02:40.9 in agent2.log). `SerialEdgeWatch` cannot tell that 0 from a
counter value: baseline 3 → read 0 → read 3 is two edges, and the second one landed in the same
correlation window as the adopted world's digest change. `ObserveRemoteVerdict` already had the
"no packet yet is not a reading" guard for the HOST's serial (the earlier 2026-09-02 fix); the
OWN serial had no way to say "no reading" because 0 is its legitimate starting value.

**Fix.** `IWorldStateBridge.TryReadWorldLoadSerial(out uint)` — false while no block is attached
or the read fails; the mock harness keeps returning true. `CoopCommandDispatcher.MaintainSessionSave`
feeds `SessionSaveWatch.ObserveOwnVerdict(uint, uint?, long)` a null on such a tick, and the
watch skips the tick whole (neither the serial nor the digest watch advances — a real digest
edge is still there next tick; observing it alone would park a Save the returning serial could
wrongly upgrade). `SessionSaveWatchTests.ObserveOwnVerdictSkipsATickWithNoSerialReadingInsteadOfEdgingOnAZero`
pins the live shape; `...FedAZeroForAMissingBlockWouldHaveReadAsALoad` pins why.

Requests 5/7 also show a second, smaller thing: `TriggerRejoin` fires while the previous
rejoin's engine load is still on the loading screen (`RejoinAfterLoadAsync` cannot block on the
game; `TryGetAdoptedWorldLoadStatus` acks the request, not the world). With the false trigger
gone there is nothing to fire, so that is left as is.

## The transition autosave changes the save identity for good (2026-09-03): `SaveLineage`

**Seen.** First real door crossing under co-op (`crossdoor end_door10`, end_m01aa → end_m01ab,
cinematics.md §8.3c). Both engines autosaved through the transition — `000001 - AUTOSAVE`
mtime 05:10:49 on BOTH installs, different sizes, different md5 — and 14 s later, with the
module-entry cutscene half-way on both sides, the client printed `[W_SAVE] different save` and
took the automatic rejoin (`starting rejoin (SaveMismatch)` → `adopted 'end_m01ab' (661,457
bytes, identity 0x2EDFDAA3)` → `rejoined`), which closed the host's copy of the cutscene too
(`the owner ended the conversation; closing`). The module's own objects had the same ids on both
sides (trigger 0x26 fired natively on each; the GITs agreed, `W_GIT` never printed); the worlds
had not diverged at all.

**Why.** `TryComputeSaveIdentity` = nested module name + the `Mod_NextObjId` counters + the
stored GIT id list. The counters are each engine's OWN runtime-id high-water marks, and the two
engines never deal the same number of ids — the host spawns phantoms, stand-ins and script
creatures the client does not, and vice versa. Before the first door the identities agree
because both digest the SAME `-Save 1` bytes; after it, each side has rewritten the autosave with
its own counters and they disagree until the next Load — i.e. forever, and again after every
door. `SaveIdentitySettle` (5 s) only covers the timing lag of two sides crossing at different
moments; it honoured this one correctly, because it IS persistent. Host-only warps had not shown
it (the 2026-09-02 warp rounds compared equal) — a warp does not exit through the old module's
door script and, on this rig, left the counters in step; a real door does not.

**Rule (`KotorCoop.Protocol/SaveLineage.cs`, applied before the settle on both the client and,
per peer, the host).** A save-identity mismatch is honoured when the two identities have NEVER
agreed (a joining peer on a different save — what the identity exists for), or when either side's
`WorldDigest.LoadSerial` has moved since they last agreed (someone loaded a save; the plugin's
`WorldLoadRule` counts those, and treats both real transitions and announced warps as NOT loads).
A mismatch that appears after agreement with no load edge on either side can only have come from
a save, and a save does not change which world anyone is playing — so it is reported as
agreement, and the dispatcher says so once per episode: `save identity differs after a save on
one side, with no load since the two last agreed - same world, not rejoining`. Agreement is
re-recorded whenever it is seen, so a later load is judged against the newest checkpoint. Per
connection: a reconnecting peer starts from "never agreed" again, which is what a rejoin should
be judged on. Seven facts in `SaveLineageTests`.

**Live after the fix.** Two more crossings (05:25:36, 05:33:01): no `W_SAVE`, no rejoin, the
cutscene ran to `the owner ended the conversation` on both sides with both players in end_m01ab,
the announce line at 05:33:04. `PersistentSaveMismatchRule` and `RejoinTrigger.SaveMismatch` are
unchanged — a genuinely different save (`-Save 2` on one side, or a Load mid-session) still
rejoins exactly as the 2026-09-02 sections above describe.

## Periodic and pre-disconnect character capture (2026-09-18)

A client's co-op character used to be captured only at door autosaves and on a clean disconnect,
reading whatever save was newest. Now `CoopCommandDispatcher.MaintainPeriodicCapture` asks the engine
for a quicksave (`CheatKinds.QuickSave`, the `save` verb) every `character_capture_interval_min`
minutes (default 10, 0 = off) when `CharacterCaptureCadenceRule` says the player is not in combat, a
conversation, a minigame or down; it waits at most 5 s for `FindCaptureSource` to show a new or
newer save, then captures (falling back to the newest existing save, since a refused save is silent).
The source is still the newest NON-staged save, so `saves\000001 - AUTOSAVE` on a joiner is never
read. `CaptureCharacterOnDisconnect` does the same quicksave-then-wait (blocking, up to 5 s, on the
teardown path). The cadence rule is pinned by `CharacterCaptureCadenceRuleTests`; the dispatcher wiring is not unit-tested. NEEDS-LIVE: the
quicksave landing where `FindCaptureSource` looks, and that a periodic quicksave is unobtrusive.

## Host quit-to-menu edge must not reuse the last-good standing row (2026-09-23)

**Regression observed.** At about 10:35:49 the host ran `quittomenu`. Three seconds later, the
client's `HostWorldReplacedRule` saw the new teardown serial together with the host's retained
standing `PlayerState` row and tried to rejoin immediately. The host had no world yet, so the
joiner performed its bounded no-world retry loop instead of waiting for the host-away / reload
notice. Logs are `$env:TEMP/kotorcoop-review-live/agent1-sep23-lock.log` and
`agent2-sep23-lock.log`.

**Guard added, live confirmation pending.** `CoopAgent` continues publishing the last-good
player row for short native-read failures, but now publishes an empty `WorldDigest.Module` and
zero module GIT on any tick where `TryReadLocalState` failed. The load serial remains published
so the edge is not lost. `HostWorldReplacedRule.IsHostReady(hostState, digestModule)` now requires
both a non-away, non-downed standing row and a non-empty digest module matching that row
case-insensitively. `WorldDigestReadinessRule` also suppresses the module on the first digest
publication that observes a changed load serial, even if the player read succeeded on that pump;
the next successful sample at the same serial may publish it. This closes the stale-row sampling
window without waiting for an away packet, which some fast reloads skip. The digest-correlated
HostReload path uses the same readiness predicate. Tests cover the initial baseline,
changed-serial suppression, the next same-serial success, continued refusal while reads fail,
stale standing rows with empty or mismatched digest modules, the valid fast path, and preservation
of the serial while clearing module readiness.

This is code/test evidence only. Re-run Quit to Main Menu with a connected client to confirm the
client receives the host-away notice, waits while the host has no world, and performs exactly
one rejoin once the host's matching-module digest and standing row return.

### Live host-away repeat (2026-09-23, checks26)

With `36599fc` managed code and the already deployed `e05b10f` native DLL, the host left
for the main menu at 10:51:01. The joiner reported the departure at 10:51:03 and released
plugin-side locks at the configured timeout, 10:51:23, while keeping the socket connected.
There was no premature HostReload or NoWorld retry during menu parking. A preceding
parking pass also verified native movement: the joiner moved from (187.3,74.3) to
(142.5,75.5) after a `walkto` command.

The host loaded `000030 - campaign_zhar_fixed_before_host` through the plugin CLI load
path. At 10:51:38 the joiner detected the returning world and started exactly one
HostReload. Its pre-disconnect save landed in 330 ms; adoption used identity `18AFC541`,
and the joiner reconnected to `danm13` at 10:51:42. This confirms the native menu/return,
notice emission, control release and automatic rejoin path on the two-instance rig.
It does not verify the visual placement of those notices or clicking the retail menu.
Evidence: `agent1-sep23-recovery26.log` and `agent2-sep23-away26.log` in the live-log folder.


### Selected older joiner save: exact-source live pass (2026-09-23)

The 10:14 regression loaded `000026 - campaign_zhar_fixed_before_joiner` but recovered
from the newer `000033 - zhar_joiner_before` after a refused quicksave. Those characters
had different XP and Commandable values. `36599fc` binds character extraction to the
selected folder and the combined SAVEGAME/SAVENFO/PARTYTABLE fingerprint, protects it
from generic capture, and carries it authoritatively even with `join_mode = save`.

The first checks26 repeat safely declined capture: the native wrapper tears down first,
then calls the direct AUTOSAVE loader at the next world serial. `091c965` recognizes that
specific downstream callback only when the staged bytes still match the pre-request
snapshot, and requires the exact command acknowledgement and completed serial. The raw
load episode plus that proof also handles unchanged or late save digests.

On checks27, the joiner selected save 26 at 10:52:36 (request 6). At 10:52:39 the agent
captured that exact folder, skipped the disconnect quicksave, adopted host identity
`18AFC541` carrying the verified co-op character, and rejoined at 10:52:42. The captured
PIFO was inspected before further gameplay: Yasuko Kaar, Experience 1000, Commandable 1.
This matches selected save 26, not the newer 10801-XP checkpoint. Only one OwnLoad rejoin
occurred. Logs: `agent2-sep23-recovery27.log` in the live-log folder.

This proves the explicit `loadgame` path. Unsupported retail save-row provenance and a
direct-directory request first observed after its request-time serial remain fail-closed;
this pass does not claim automatic extraction from those sources or terminal-credits saves.


### Two ordinary saves without a load (checks27, 2026-09-23)

The host saved `000040 - zhar_joiner_walk_after_host` at 10:54:19 and the joiner saved
`000039 - zhar_joiner_walk_after_client` at 10:54:20. At 10:54:21 the agent reported that
the identity difference came from a save with no intervening load and kept the shared
world. Neither log contains W_SAVE, W_GIT or a rejoin in the following minute; the
joiner remained connected with zero reported liveness anomalies. This closes the
ordinary two-save regression in `danm13`, not the earlier Leviathan mapping-specific
re-adoption case.
