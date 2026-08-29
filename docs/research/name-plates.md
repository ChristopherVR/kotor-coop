# Phantom name plates — solved (research 2026-07-31)

The earlier "AddString ran but no text found" mystery is closed. Two separate facts:

1. **The server-side write was correct all along**; the text was five indirections deep:
   `stats+0x34 CExoLocString {internal*, strref@+4}` → internal `{list*, count@+4, fromTlk@+8}`
   → list `{head, tail, count}` → node `{prev, next, data@+8}` → entry
   `{id@0 (=2*lang+gender), char* text@+4, capacity@+8}`. Trask's "empty" locstring was
   never evidence against substrings: his name lives in the STRREF at `stats+0x38`
   (first) / `stats+0x40` (last); `ClearLocString` never touches the strref.
2. **The selection band never reads any of that after the twin exists.** It renders
   client-side: `CSWGuiTargetActionMenu::UpdateNameLabel 0x00685CB0` → twin vtable `+0x70`
   = `CSWCCreature::GetName 0x00617090` → plain `CExoString`s at
   `[[creature+0x224]+0x2F8]+0x14` (first) / `+0x1C` (last). Those are populated exactly
   once — the object's FIRST GameObjUpdate (CreateNewLastUpdateObject 0x00577000 sets all
   mask bits) — because the per-frame diff `ComputeAppearanceUpdateRequired 0x00567100`
   NEVER sets name bit `0x400` (decoded end to end). Names are transmitted once, ever.

## The fix (implement in Odyssey.SetCreatureFirstName + PhantomController)

Keep the server half (ClearLocString + AddString(0, &s, 0) — feeds saves/feedback/
GetFullName), and ADD the client half, which is load-bearing:

```
twin = [creature + 0x224]
if twin == 0: fine — the first GameObjUpdate will carry the name
guard: [twin] == 0x0074F5F8 (CSWCCreature vtable)
CSWCCreature::SetName 0x0060D600  __thiscall(this, CExoString* first, CExoString* last) ret 8
  — pass non-null empty ("") last name, engine copies with its own allocator
```

Re-assert periodically (once/second) and after module transitions — the twin is
recreated and only gets the name if the server locstring is already set at that moment.
Fallback if SetName misbehaves: assign `clientStats+0x14`/`+0x1C` directly via
`CExoString::operator=_2 0x005E5C50`.

**TLK is NOT needed and must not be used**: substrings beat strrefs in GetString_3
(0x005EA130), WriteCExoLocStringServer transmits substrings as literal text, and TLK
text is read from the FILE on every fetch (no resident table to patch). Mod-safe by
construction.

## Verification (cheap, memory-only)

- Server half landed: walk the chain, expect count 1, id 0, text = name.
- Band will show it: read `[[creature+0x224]+0x2F8]+0x14` char* — that IS the band text.
- Trask control: `[TraskStats+0x38]` is a valid strref (resolvable via CTlkTable::Fetch
  0x0041E550, this=[0x007A3A08]).

## Handoff corrections to fold in

- 4.11's "set before the twin exists so it's created with the name" is unreliable —
  the twin write is required.
- CExoString `+0x04` is CAPACITY (strlen+1), not length; operator=_2 strlen()s the
  source and ignores source capacity.
- New offsets: CSWSCreatureStats first_name +0x34/strref +0x38/last_name +0x3C/strref
  +0x40; CSWCCreatureStats first +0x14/last +0x1C via [[creature+0x224]+0x2F8];
  CSWCCreature vtable +0x70 = GetName.

---

## The peer's LOOK has the same two halves as its name (2026-08-29)

Reported from play: **"if we warp to the host, the client(s) show as a Trask icon"** — a peer
rendering as something that is not that peer, specifically *after a warp*, not at first
connect. Investigating it found a defect with exactly the shape this document already
describes for the NAME, one field over, and it had gone unnoticed for the same reason.

### The defect (VERIFIED by reading the code against X2's live result; unit-tested)

`PhantomController.ApplyAppearance` decided "already applied" from the **server** field alone:

```
if (*(ushort*)(phantom + 0xA60) != row) { write; RebuildAppearance; RebuildClientAppearance; }
```

That is the wrong question, for the same reason `4.11`'s "set it before the twin exists" was
wrong for the name. `phantom-locomotion.md` §11.2 (X2) established that the CLIENT twin's model
is NOT rebuilt from the server row by itself — that is the whole reason
`Odyssey.RebuildClientAppearance` exists and why adding it fixed the model live. And
`RebuildClientAppearance` returns without doing anything when there is no twin yet.

At **spawn** there is no twin yet. So a phantom spawned at a moment when the peer's row is
already known:

1. gets the row written to the server object,
2. gets `RebuildClientAppearance` called into a null twin — a silent no-op,
3. has its twin built a frame or more later, from the **blueprint's** row
   (`k_coop_phantom` = appearance 114, `P_FEM_B_LRG_03`; the `n_commm` fallback = 17), and
4. is never re-dressed, because the server field now matches and the guard above says
   "already applied" for the rest of the module.

**Why "after a warp"**: at first connect the peer's row usually arrives a tick *after* the
body, so the drive loop's own `ApplyAppearance` did the work with a twin present and the bug
hid. A module change destroys every phantom (`PhantomController.Invalidate`) and the RE-spawn
happens with the peer's row already in the shared block — the one ordering where the only
apply that ever runs is the one with no twin.

### The fix

Same shape as `_twinNamed`, one field over: a per-BODY `_twinDressed` latch, and
`Odyssey.RebuildClientAppearance` now returns whether it found a twin. `ApplyAppearance`
re-asserts every step until it latches — two reads and a return while the twin is missing, its
engine call exactly once per body once it exists, which is the per-object latch discipline the
project's own per-frame-write rule asks for, not a cadence.

`Odyssey.TryGetTwinAppearanceRow` reads the row the twin is actually built for, so a log line
can carry BOTH halves; the phantom spawn line now does, along with the portrait id and what
the peer published.

### The identity-source rule (`PeerIdentityRule`, unit-tested)

Stated so it cannot drift: **peer packet > that peer's last published row > leave the body
alone.** Nothing local is ever a source. Two consequences that are not obvious:

- **0 is not an appearance, it is "not published yet."** A peer publishes 0 for about a second
  either side of a module change while its own agent re-resolves its player object
  (`KotorGameBridge.TryReadLocalState` returns false through a transition) — which is exactly
  the window a phantom is re-spawned in. `PhantomController` therefore keeps the last usable
  row per PEER, across module changes, and drops it only when that peer leaves the slot (a
  slot is reused by later joiners, so this must not outlive its owner).
- **There is no local fallback and there must not be one.** Every wrong-identity report this
  project has had is the same shape — a value sourced from something other than that peer.

### Live pass, 2026-08-29 (rig, both instances, demo save 1)

**The twin gap is real and it is UNIVERSAL, not occasional.** Every phantom spawn observed —
host and client, before and after a warp, seven spawns across two rig sessions — logged:

```
spawned phantom in slot 0: id 0x12E ... appearance 114 (peer published 114, renders no twin yet), portrait 0
identity: phantom slot 0 (0x12E) now renders appearance 114 (server row was 114, peer published 114)
```

`renders no twin yet` is the spawn-time `RebuildClientAppearance` finding nothing to dress —
**it happened on 100% of spawns**, which is what the ordering argument above predicts and what
makes the old "only on an actual row change" guard fatal: `peer published 114` means the row
was already known at spawn, so the server field was written there and matched forever
afterwards, and the spawn-time no-op was the only client-side apply that would ever run. The
second line is the new latch closing 30-230 ms later.

Post-warp state, read out of both processes with `identityprobe.ps1` (server row `+0xA60`, twin
row `+0x224 -> +0x38C`):

| | id | tag | is_pc | follow `+0x4C0` | server row | twin row | portrait |
| --- | --- | --- | --- | --- | --- | --- | --- |
| host: PC | `0x7FFFFFFF` | | 1 | non-null | 114 | 114 | 15 |
| host: peer's phantom | `0x72` | `coop_phantom` | 0 | null | 114 | **114** | 0 |
| client: PC | `0x7FFFFFFF` | | 1 | non-null | 114 | 114 | 15 |
| client: peer's phantom | `0x7B` | `coop_phantom` | 0 | null | 114 | **114** | 0 |

Screenshots after the warp confirm it visually on both screens: the peer stands there under its
own player name band (`MALAK`) wearing the peer's own model, not a companion's and not a
commoner's.

**Two measured facts that kill the "the peer publishes Trask's row" theory** (the alternative
the section above flagged as NEEDS-LIVE):

- **Trask reads `is_pc == 0`** (`stats+0x6C`, live, `end_m01aa`). So
  `ScanCreatures`/`StillLooksLikeThePlayer` cannot latch him as the player, and the agent
  cannot publish his appearance as a peer's. That whole path is ruled out.
- **The PC's own `+0x4C0` party-follow pointer is NON-NULL** (`0x191216E0`), i.e. the PC reads
  `IsPartyMember == true` in `ScanCreatures`. Anyone tempted to "fix" a player-resolution bug
  by adding `&& !c.IsPartyMember` would break player resolution outright. Recorded so the next
  round does not try it.

⚠ **A limitation of this rig, stated plainly**: demo save 1's PC is appearance **114**, which is
also `k_coop_phantom`'s own blueprint row. A phantom left wearing the blueprint therefore looks
*correct* on this save, so the rig can prove the twin gap exists and that the latch closes it
(the log lines and the twin-row readout do that) but it cannot show the wrong-model symptom
itself. A save whose PC is not row 114 would; the observable proxy is the twin row, not the
screen.

⚠ **The reported symptom was "a Trask icon" specifically, and that was NOT reproduced here.**
Trask does not follow a `warp` in this save (he is absent from `end_m01ab` on both instances
after the transition), so the reporter's scenario — companions present after a relocation —
was not reachable this round. What is fixed is the mechanism that makes a peer render as
something that is not that peer after a module change; whether the reporter's Trask is this
bug wearing a companion-shaped blueprint on their save, or a second mechanism, is open. The
next round should reproduce with a save where the PC is NOT appearance 114 and the party
actually crosses the transition.

### What is still open after the live pass

The blueprint rows are 114 / 17. **Neither is Trask** (`end_trask.utc`: `Appearance_Type` 378
`Unique_Trask`, `PortraitId` 40 = `po_ptrask`, the LAST row of `portraits.2da` — worth knowing,
because a portrait id that is clamped or read out of range lands on exactly that row). So a
peer rendering as *Trask* specifically needs a mechanism that puts 378 on that body, and only
two things in this codebase ever write an appearance row: `PhantomController.ApplyAppearance`
(from the peer's own packet) and `MaterialisedEnemies` (from the enemy census).

Ruled out this round, measured on the rig, not reasoned: **the agent cannot latch a companion
as the local player.** `KotorGameBridge` requires `is_pc` (`stats+0x6C`) for both the scan
(`creatures.FirstOrDefault(c => c.IsPlayer)`) and the cached-pointer revalidation
(`StillLooksLikeThePlayer`), and Trask reads `is_pc == 0` live. A peer publishing Trask's row
as its own is therefore not a reachable state through player resolution.

Left for the next round, in order:

1. **Reproduce on a save the rig cannot represent.** Demo save 1's PC is appearance 114, the
   same row as `k_coop_phantom`, so a phantom wearing the blueprint is invisible here (see the
   limitation above), and `warp` leaves Trask behind so the reporter's "companions present
   after a relocation" scenario never occurred. A save with a non-114 PC that crosses a real
   door with a companion in the party is the test that would settle whether the report is this
   bug or a second one.
2. **The census path.** `KotorGameBridge.ReadEnemies` excludes a creature only when
   `IsPlayer || IsPartyMember`, and `IsPartyMember` is the `+0x4C0` follow pointer — which is
   exactly the field `PartyFollowController`/`FollowPhaseReleaseRule` exist because it is not
   always attached when you expect. A host companion published into the census while that
   pointer is null would have a stand-in materialised for it on the client, at appearance 378,
   standing where the companion is. That is a second, independent way for a Trask-shaped body
   to appear near the party after a transition, and it has not been measured.
3. **Sibling defect, found while reading, NOT fixed here** (it is the enemy channel, not the
   peer channel): `MaterialisedEnemies` writes a stand-in's appearance row at spawn — before
   the twin exists, exactly as above — and never calls `RebuildClientAppearance` at all, at any
   point. Given the live finding that the spawn-time twin is missing on 100% of spawns, that
   means every materialised stand-in renders as the `k_coop_phantom` blueprint rather than as
   the creature it stands for. The fix is the same latch this round added one channel over.

⚠ Do NOT "fix" a player-resolution bug with `&& !c.IsPartyMember`: the PC's own `+0x4C0` reads
non-null (measured, above), so that filter rejects the player.

---

## The census leak — items 2 and 3 above, settled (2026-08-29, MEASURED LIVE)

Item 2 was right, and it is the mechanism behind the report. The full write-up, with the
numbers, lives in [enemy-materialisation.md](enemy-materialisation.md) §7 — this section records
only what it means for THIS document, because the two are one lesson wearing two hats.

**The Trask body is not a peer at all.** It arrives through the ENEMY channel: the host's census
excluded a creature on `IsPlayer || IsPartyMember`, `IsPartyMember` being the `+0x4C0` follow
pointer, so a companion whose pointer had not been (re)attached read as a hostile stranger and
was published. Measured on the rig, `end_m01ab`: Trask, appearance **378**, party-table roster
slot 0, `+0x4C0 == 0` for **5.5 seconds** — fifteen consecutive samples — and 4.9 s on a second
run. The peer cannot resolve that id (party bodies are spawned by the party table at load, not
placed in the GIT: `0x76` on the host against `0x74` on the client for the same companion, both
measured), so it materialises a stand-in and repaints it to the published row. Appearance 378.
Standing where the host's Trask stands. The reporter's "Trask icon".

**Every "icon" surface was checked explicitly and cleared, so the next round does not re-check
them:**

- **Minimap peer arrow** (`MinimapPeerHook`, `Odyssey.Map`) — reuses the engine's OWN embedded
  player-arrow control and touches only angle, alpha and colour. `CSWGuiImageParams.resref` is
  never written anywhere in this repo except `NativeBrowserController`. It is structurally
  incapable of rendering a portrait.
- **M-screen pin** (`MapMarkerController`) — a bare `CSWSWaypoint` with `HasMapNote = 1`, no
  blueprint, no MapNote text. The stock blip.
- **Party bar** — `PortraitController` only HIDES widgets 1 and 2 on a mirroring client (an
  off-screen `SetExtent`). Nothing in the repo ever writes a portrait id to a creature: the only
  `+0x30` sites are reads (`PhantomController`'s log line, `ReadPartyPortraits`), and
  `CSWCCreatureStats+0x11C` is referenced in a comment and read by nothing. A peer has no roster
  slot to occupy.
- **`ReadPartyPortraits`** — reads the LOCAL roster's portraits (so, legitimately, `po_ptrask`),
  but only ever to advertise this host's party on someone else's lobby screen. Never an identity
  source for a peer.
- **Name plate** — sourced from `block.GetRemoteName(slot)` (the peer packet) and nothing else;
  `ApplyName` returns early on an empty name rather than substituting. A stand-in gets no name
  at all.
- **One real hazard found and closed**: `PartyRosterBodyReuseRule.ShouldReuse` (behind
  `dialogue_recruit_sync`, default off) accepted any id it could resolve locally, including a
  body THIS plugin spawned. Phantoms and stand-ins are allocated from a runtime counter the two
  instances advance independently, so a peer's roster id can collide with one by arithmetic
  alone — and that is the one code path in the project that could have seated a remote player's
  avatar in a party portrait slot. It now refuses them.

Item 3 (stand-ins never dressed on the client twin) is fixed with the same per-body latch this
document's own section added for phantoms; see enemy-materialisation.md §7.2. Item 1 — a save
whose PC is not appearance 114 — is still not represented on this rig, but it is no longer the
blocker it was: the Trask symptom is explained and reproduced as a mechanism without it.
