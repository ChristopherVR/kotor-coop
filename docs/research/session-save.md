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
