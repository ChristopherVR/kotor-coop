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

### What this does NOT explain, and what to check next (NEEDS-LIVE)

The blueprint rows are 114 / 17. **Neither is Trask** (`end_trask.utc`: `Appearance_Type` 378
`Unique_Trask`, `PortraitId` 40 = `po_ptrask`, the LAST row of `portraits.2da`). So if a peer
literally renders as *Trask* rather than as the blueprint, a second mechanism is publishing
Trask's row as that peer's, and the candidate is the agent's own player resolution:

- `KotorGameBridge.TryReadLocalState` publishes `_player.Address + 0xA60` verbatim, and
  `_player` falls back to `creatures.FirstOrDefault(c => c.IsPlayer)` — `is_pc`
  (`stats+0x6C`) with no tie-break — whenever the plugin block is momentarily unreadable,
  which a module transition's main-thread stall can cause (that exact stall is already on
  record in `TryReadLocalState`'s own comments).
- **The open question is whether a party companion reads `is_pc != 0` in this engine.** If it
  does, the scan can latch Trask, `StillLooksLikeThePlayer` keeps passing (it checks vtable +
  `is_pc`, nothing else), and the peer publishes Trask's position AND appearance as its own
  until the plugin block is found again and the adoption branch overrides it.
- Measure it with `identityprobe.ps1` (scratchpad) or `tools/creatures.ps1`'s own `pc` column:
  read Trask's `stats+0x6C` live. If it is non-zero, the fix is a tie-break in
  `ScanCreatures`/`TryResolve` (prefer a creature that is `is_pc` and NOT a party member) —
  which needs its own live check that the PC does not itself carry a party-follow pointer at
  `+0x4C0`.

Sibling defect found while reading, NOT fixed here (it is the enemy channel, not the peer
channel): `MaterialisedEnemies` writes a stand-in's appearance row at spawn — before the twin
exists, exactly as above — and never calls `RebuildClientAppearance` at all, at any point. By
X2's own live result that means every materialised stand-in renders as the `k_coop_phantom`
blueprint rather than as the creature it stands for. The fix is the same latch.
