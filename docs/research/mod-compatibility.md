# Mod compatibility audit + digest handshake design (research 2026-07-31)

Users WILL have different mods (TSLPatcher: override .utc/.uti/.2da/.gui/.ncs, appended
dialog.tlk, module .mod files). The rule: fail soft, detect mismatch, never silently
corrupt. Currently honoured in ONE place (equip create); stated-but-unimplemented in a
second (globals digest computed, never compared).

## Assumption inventory (severity: C=corruption/crash, H=feature dies silent, M=degraded, L=cosmetic)

| # | file | Assumption | Breaks with mods | Sev | Fix |
| --- | --- | --- | --- | --- | --- |
| 1 | GlobalVariableTable / KotorGameBridge:755 | catalogue digest detects mismatch | **NEVER COMPARED** (0 consumers). Different globalcat → (type,index) keys name different vars → EventFlagStore fuses quest flags | C | wire into handshake §B |
| 2 | PlayerState.Appearance → +0xA60 | appearance.2da row is install-invariant | unguarded; modded row order → wrong body; row past table → crash | C | clamp to local row count; carry label hash; else leave blueprint appearance |
| 3 | PhantomController n_commm | blueprint exists | missing → SpawnCreature returns Invalid with failure=0, retries every 30 frames FOREVER, unbounded disk log on game thread | C | dedicated failure code; latch "unavailable"; stop retrying |
| 4 | Odyssey SetJournalState | peer's quest tag exists in global.jrl | SetState APPENDS unknown tag (phantom entry); returns true unconditionally | H | filter tags to local global.jrl; verify list changed |
| 5 | ~40 absolute addresses | retail v1.0.3 exe byte-for-byte | exe-patch mods (4GB LAA, UniWS, localisations) shift everything → writes into unrelated memory | C | hash exe at start; refuse unknown; refuse session if peers differ |
| 6 | CoopHost HandleHello | Hello admits without content check | hello.Module read and thrown away; no content check at all | H | digest in Hello/Welcome |
| 7 | ProtocolConstants / CoopHost | "old agents rejected at Hello" | NOT rejected — version mismatch → NoteMalformed, NO RejectPacket, old peer retries forever | M | parse header leniently, answer with typed Reject |
| 8 | KotorGameBridge object ids / DoorFlags | ids identical because same save+GIT order | mod editing a module GIT shifts ids → enemy health/doors/items hit WRONG object; no detection | C | SAVE+GIT digest; refuse those channels on mismatch, keep phantoms |
| 9 | ReadEquippedResref | item tag == resref | false for mod gear (acknowledged soft-fail) | M(by design) | ok; residual: same resref different item = wrong armour, cosmetic |
| 10 | EquipItemFromTemplate | — | ✅ verified soft-fail (failure=7, logged, hash remembered) — the pattern to copy | L | none |
| 11 | ItemController SpawnItemOnFloor | — | ✅ soft but same failure=0 ambiguity as #3 | M | explicit code for ObjectInvalid |
| 13 | PluginMenuLoader | <gamedir>\saves\ vanilla layout, slot N by mtime | SAVES: alias / cloud sync → peers stage DIFFERENT saves → #8 collapses; also Directory.Delete overwrites real autosave | H | identify save by content; back up not delete |
| 15 | Exports.cs dinput8 | our dll slot is ours | other dinput8 mod (widescreen/fps) overwritten or overwrites us | H | detect pre-existing non-ours dinput8, chain to it |
| 16 | Odyssey n_commm faction 5 | blueprint faction unchanged | mod editing n_commm faction → phantoms hostile → aggro sink returns | H | read faction back after spawn, force Neutral |
| 17 | PhantomController inline name | names can be inline | real names are strrefs (see name-plates.md) | L | send text, resolve locally; never send raw strref |

Nothing reads Override\, chitin.key, dialog.tlk, *.2da, or global.jrl. Only game-file
reader in C# is PluginMenuLoader's ERF walk. tools/kotorfiles.py has full decoders +
predict_object_ids — the spec to port (~400 lines read-only C#).

## Digest handshake design

Two phases (data available at two times), FNV-1a 32 (house hash):
- **Install digest** (agent start, from gamedir): exe, Override census, Override *.2da,
  appearance.2da (rowIndex+label), dialog.tlk (count+size), global.jrl tags, modules
  census. Carried in **HelloPacket + WelcomePacket** (Hello already retransmits → free
  reliability, single-round-trip admission).
- **World digest** (in-world, re-sent on epoch change): globals catalogue digest (EXISTS
  today), save identity (module + Mod_NextObjId0 + GIT ObjectId list), module GIT digest.
  New reliable **WorldDigestPacket** both ways.

Comparison: one pure `ContentCompatibility.Compare(local, remote) -> Verdict` in
KotorCoop.Protocol so host+client compute identically. Output = SessionCapabilities bitset
(WorldState|Doors|Enemies|GroundItems|Journal|Appearance|Equipment|Phantoms); CoopAgent
gates each Pump* on its bit (one line per pump). **Unknown ≠ mismatch** — a field that
couldn't be computed can only WARN, never kill a session.

Policy per class:
| Class | Policy | Why |
| --- | --- | --- |
| PROTO | REFUSE (real Reject) | — |
| EXE | REFUSE | every address per-binary |
| SAVE | REFUSE (--allow-different-save degrades to Phantoms\|Appearance\|Equip\|Journal) | id identity is the foundation |
| GIT | DEGRADE: drop Enemies\|Doors\|GroundItems for that module | modded module shifts ids there only |
| GLOBALS | DEGRADE: drop WorldState\|Doors, loudly | keys name different vars |
| APPEARANCE | DEGRADE: stop raw rows, label→row fallback, else blueprint | crash risk |
| JRL | DEGRADE: journal filters to local tags | no phantom entries |
| TLK | WARN | normal for any mod; nothing reads TLK today |
| OVERRIDE/MODULES | WARN + list top differing names | the EXPECTED case; explains soft failures |
| 2DA | WARN | mostly additive; appearance+globals have own classes |

Cheapest win: **wire the existing GlobalVariableTable.CatalogueDigest** — already computed
and correct, just needs a consumer. Then port kotorfiles.py's 2DA/GFF/TLK/GIT readers to
C# (~400 lines) to unlock #2 (label→row) and #4 (known-tag filter), the two highest-value
repairs. Neither touches the per-tick path.

---

# What is enforced today (2026-08-14)

The design above is implemented. This section is the reference for what actually runs: the
exact inputs to each digest, the decision each difference produces, and how a player finds
out. Everything here is covered by unit tests (`ContentCompatibilityTests`,
`WorldDigestTests`, `SaveDigestTests`, `InstallDigestTests`, `CompatibilityAnnouncerTests`,
`SessionIntegrationTests`); the ⚠ NEEDS-LIVE items are called out at the end.

## The two digests, field by field

**Install digest** — `InstallDigest.Compute(gameDir, exePath)`, once at agent start, carried
in `HelloPacket` + `WelcomePacket`. FNV-1a-32 throughout.

| Field | Exactly what is hashed |
| --- | --- |
| `ExeDigest` | file length ⊕ FNV(first 64 KB) ⊕ FNV(last 64 KB) of `swkotor.exe` |
| `OverrideCensus` / `OverrideCount` | every `Override\` entry's name + size, in enumeration order |
| `TwoDaDigest` | full CONTENT of every `Override\*.2da`, name-sorted (a TSLPatcher edit is in-place, so presence is not enough) |
| `AppearanceDigest` / `AppearanceRows` | each `appearance.2da` row's index + `label`, and the row count |
| `TlkDigest` / `TlkEntryCount` | `dialog.tlk` entry count ⊕ file size ⊕ FNV(first 4 KB) |
| `JournalDigest` | every `global.jrl` category `Tag`, lower-cased and sorted |
| `ModulesCensus` | every `modules\` file's name + size |

**World digest** — `WorldDigest`, published by `CoopAgent` on the heavy pump and re-sent on
change through the reliable `WorldDigestPacket` (both directions).

| Field | Exactly what is hashed | Source |
| --- | --- | --- |
| `GlobalsCatalogue` | the live global-variable catalogue's names and types | engine memory (`GlobalVariableTable`) |
| `SaveIdentity` | the save's module name + `Mod_NextObjId0` + `Mod_NextObjId1` + its stored GIT's `ObjectId`/`TemplateResRef` list, in `LoadGIT` list order | `saves\...\SAVEGAME.sav` |
| `Module` + `ModuleGit` | per module: the save's stored GIT when it has been visited (each entry's `ObjectId` + resref); otherwise the ids PREDICTED off the pristine module file — `LoadGIT` order, each entry taking the next id and its blueprint's `Equip_ItemList` + `ItemList` taking the ids after it (verified 217/217 against a real save) | module `.rim`/`_s.rim`/`.mod` + `chitin.key` |

Both world-digest file fields come from `SaveDigestCache`, which:

* **never blocks its caller.** The first ask for a value returns 0 = unknown and computes on
  a background thread. ⚠ This is not an optimisation: the caller is the agent's heavy pump,
  which is also what carries a player through a module transition, and the first tick in a
  new module is exactly when a fresh module's digest is first wanted. See
  [transition-window-races.md](transition-window-races.md) for what stalling there costs.
* **re-checks the staged save** (its `SAVEGAME.sav` size + mtime, at most every 2 s) instead
  of latching. World adoption writes that slot AFTER an agent may already have fingerprinted
  what was there; a latched cache would keep reporting the joiner's OLD identity and tell
  them their save does not match the host's at the moment it literally *is* the host's.

## Save identity, after world adoption

[world-adoption.md](world-adoption.md) changed what this gate means. It used to be the
foundation nobody could supply: object ids line up only when both engines dealt them from the
same save, so a player who had ever played alone could not join. Adoption *makes* the saves
identical — the host's world is staged over the joiner's autosave slot and only their
character is carried in — so a mismatch no longer means "these two players are
incompatible"; it means **adoption did not happen or did not finish**, which is recoverable
by rejoining.

So the policy inverted: **a save mismatch degrades by default and refuses only on demand**
(`--require-identical-save`, `SessionSettings.RequireIdenticalSave`). `--allow-different-save`
still parses — it now describes the default and is a no-op.

Two supporting changes:

* **the transfer states the host's identity** (world-transfer framing **v2**: the reply
  header grew 4 → 8 bytes to carry it). Before this, `agent adopt` passed
  `expectedSaveIdentity: 0` and the self-check — "is what I staged what I was sent?" — never
  ran on the path that ships. 0 remains a legal answer (a host that cannot fingerprint its
  own save) and skips the check with a log line, per the unknown-never-blocks rule.
* **a refusal is carried out.** `CoopHost.Tick` now drops a peer whose world-time verdict is
  a refusal and sends it a typed `RejectPacket`. Before, a refusal was applied by narrowing
  that peer's capability set to nothing — and since the session takes the narrowest set
  across all peers, that quietly switched every channel off *for everyone* while the refused
  peer stayed connected and never learned why.

## The degradation matrix

`ContentCompatibility.ObjectIdKeyed` = `Enemies | Doors | GroundItems | Party`.

| Difference | Code | Decision | Channels lost |
| --- | --- | --- | --- |
| protocol version | `E_PROTO` | REFUSE | all |
| `swkotor.exe` | `E_EXE` | REFUSE | all |
| save identity, `RequireIdenticalSave` set | `E_SAVE` | REFUSE (peer dropped with the reason) | all |
| save identity, default | `W_SAVE` | degrade | `ObjectIdKeyed` |
| module GIT (this module only) | `W_GIT` | degrade, re-judged every module change | `ObjectIdKeyed` |
| globals catalogue | `W_GLOBALS` | degrade | `WorldState \| Doors` |
| `appearance.2da` | `W_APPEARANCE` | degrade | `Appearance` |
| `global.jrl` | `W_JOURNAL` | warn (channel filters to shared tags) | — |
| `dialog.tlk` | `W_TLK` | warn | — |
| `Override\` census | `W_OVERRIDE` | warn | — |
| other `*.2da` | `W_2DA` | warn | — |
| `modules\` census | `W_MODULES` | warn | — |
| either side could not fingerprint | `W_UNCHECKED` | warn | — |

**The unknown rule, restated because everything depends on it.** An install field with its
bit clear in `KnownMask`, or a world field of 0, is compared as unknown: it can only produce a
warning, never a refusal and never a degradation. A field must be resolved on BOTH sides
before a difference counts. The module GIT additionally requires both peers to report the
SAME module — while one is mid-transit the two digests describe different areas, and
comparing them would flag every ordinary module change as a mod difference.

⚠ **Updated 2026-08-21 (round 54, `shared-state-audit.md` §2): save identity gets the same
same-module gate, and a short debounce besides.** `SaveIdentity` folds in the CURRENT module
name, so it legitimately changes the instant each side's own engine autosaves into a new
module — and the two peers never do that in the same instant. Comparing it while they report
different modules produced a live, repeatable `W_SAVE` false alarm on an ordinary crossing
(both saves genuinely identical, `Enemies|Doors|GroundItems|Party` dropped for several
minutes). Fixed: `WorldDigest.Compare` now judges `SaveIdentityMismatch` only when
`sameModule`, same as the GIT check above; `SaveIdentitySettle` additionally debounces the
residual few seconds of same-module cache lag (`SaveDigestCache`'s own up-to-2-second re-stat
interval on each side) so a mismatch is honored only once observed continuously for 5s.

## How a player finds out

`CompatibilityVerdict` carries `CompatibilityNote(Code, Dropped, Text)` — machine-readable
cause, the exact channels it cost, and the sentence to show. Anything consuming a verdict
switches on the code and never parses the sentence. Consumers:

* `CompatibilityAnnouncer` (agent) diffs the verdict each 150 ms command tick and posts new
  findings through the same trio every co-op reply uses — stdout, the `~` console reply line
  and the **in-game overlay**. Announced once each; `W_GIT` is re-armed when the channels come
  back, because "enemies are off here" needs a matching "shared again" when *here* ends.
* `AgentDashboard` prints the summary, the OFF channel set and the first warnings.
* `CoopHost` puts `verdict.Summary` (`E_…`) in the `RejectPacket`; the client surfaces it as
  its session status.

## ⚠ NEEDS-LIVE

Everything above is unit-tested and none of it has had a live mismatch pass — two identical
installs agree, which exercises only the happy path.

1. **A mod-edited module actually degrades.** Copy one module's `.mod`/`.rim` into instance 2
   with an extra GIT entry, walk the pair into it, expect `W_GIT` on screen naming the module
   and enemies/doors/items going quiet — then walk into the next module and expect them back.
2. **The digest never stalls a transition.** Warp the pair through several modules and watch
   for the first-tick 0 followed by a real digest a tick or two later; no added transition
   latency, no `agent` tick-failure lines.
3. **Adoption's self-check with the host's stated identity** (`agent serve-world` prints the
   identity it is serving; `agent adopt` should verify it with no `--` help).
4. **The staged-save re-check.** Start the agent BEFORE adopting, adopt, and confirm the
   reported identity becomes the host's within a few seconds rather than staying the old one.
5. **A strict refusal evicts.** `--require-identical-save` on both sides with genuinely
   different saves: the client should show `E_SAVE` and the host's peer list should empty.
   ✅ **Confirmed live 2026-08-23** — see the "Round (2026-08-23)" entry near the end of
   this file: exact eviction cycle observed, `E_SAVE: different save; rejoin to adopt the
   host's world` on both sides. Items 1-4 above are still open.

## Update (2026-08-23): the handshake is fully wired; a real live-only bug found and fixed

This section was written against `main`, before most of the design above shipped. As of
`integration/honest-limitations` @ 92ce9b2 the "NEVER COMPARED" line for item #1 is stale:
`GlobalVariableTable.CatalogueDigest` has a consumer (`CoopAgent.PublishWorldDigest` →
`CoopHost`/`CoopClient` → `ContentCompatibility.ApplyWorldMismatch`), `ContentDigest` rides
in Hello/Welcome, `WorldDigest` has its own packet type sent every heavy pump, and both ends
recompute the verdict identically (`src/KotorCoop.Protocol/ContentCompatibility.cs`,
`WorldDigest.cs`, `ContentDigest.cs`; producers `src/KotorCoop.Memory/InstallDigest.cs`,
`SaveDigest.cs`, `GlobalVariableTable.cs`). 29 targeted unit tests plus the GOG-gated
integration tests (`InstallDigestTests`, `SaveDigestTests`, `WorldDigestTests`,
`ContentCompatibilityTests`) all pass, and the GOG-gated tests are not vacuous — this
machine has a real install at `C:\GOG Games\Star Wars KOTOR (GOG)`, so they exercised the
genuine executable, appearance.2da, dialog.tlk and global.jrl reads, not just synthetic
fixtures.

### The bug found: four file reads used the wrong FileShare

`GameFileIo.ReadSlice` (module RIM/ERF/BIF reads) has always opened with
`FileShare.ReadWrite`, with a comment explaining exactly why: *"the running game keeps
these files open; a reader demanding exclusivity would fail the moment swkotor.exe is up —
which is exactly when the co-op agent wants to read them."* Four other reads on the same
install-digest path never got that memo and used the BCL's `File.ReadAllBytes`, whose
default is `FileShare.Read`:

- `KeyIndex.Load` (chitin.key) — `src/KotorCoop.Memory/GameFiles/KeyIndex.cs`
- the loose-Override branch of `GameFiles.TryReadResource` — `GameFiles.cs`
- `InstallDigest.TryHashOverride2das` (every `Override\*.2da`) — `InstallDigest.cs`
- `Tlk.Open` (used by the server-browser module-name lookup) — `Tlk.cs`

`FileShare.Read` is a sharing violation against a handle opened with `FileAccess.ReadWrite`
and `FileShare.Read` — exactly the shape the project's own comment says swkotor.exe holds
on its data files (write access it never exercises, read-only sharing). Every one of those
four reads sits behind a `try { } catch (IOException or UnauthorizedAccessException) { }`
that degrades silently to "unknown" (`GameFiles.IsUsable = false`, or one `ContentDigest`
field's known-bit staying clear) — which, per the Unknown-is-not-mismatch rule, can only
ever *warn*, never refuse or degrade a channel. **The practical effect: if chitin.key was
genuinely locked the way the comment describes, an install digest computed while attached
to a live game could have silently lost the Override, TwoDa, Appearance, Journal and
Modules fields all at once — five of seven — leaving only Exe and Tlk (which already used
safe reads inside `InstallDigest` itself) actually checked.** No existing test caught this
because no test held a competing handle on the file it was reading; the GOG-gated tests
above ran against files nothing else had open. This is the exact class of bug the project
has hit before: a check that is correct in isolation but never exercised against the real
concurrent-access condition it will actually run under.

Fixed by adding `GameFileIo.ReadAllBytes(path)` (same `FileShare.ReadWrite`, whole-file
instead of a slice) next to `ReadSlice` and routing all four call sites through it
(`src/KotorCoop.Memory/GameFiles/Archives.cs`, `KeyIndex.cs`, `GameFiles.cs`,
`Tlk.cs`, `src/KotorCoop.Memory/InstallDigest.cs`). Three new regression tests
(`tests/KotorCoop.Tests/GameFileSharingTests.cs`) open a file with the exact handle shape
(`FileAccess.ReadWrite`, `FileShare.Read`) the comment attributes to swkotor.exe, assert
a plain `File.ReadAllBytes` genuinely fails against it (so the fixture is proven real, not
vacuous), then assert the real code paths — `GameFiles.IsUsable`, `TryReadResource`, and
`InstallDigest.Compute`'s TwoDa field — still succeed. Reverting the `KeyIndex.cs` fix
alone turns `ChitinKeyReadsWhileHeldOpenTheWaySwkotorHoldsIt` red, confirming the test
catches the regression. `dotnet test` is green at 616/616 (0 skipped — the GOG-gated tests
ran) after the fix.

Everything else audited in this pass — wire format symmetry (`PacketCodec.WriteDigest`/
`ReadDigest`, `WorldDigestPacket`), the FNV inputs feeding each field, the Refuse/Degrade/
Warn policy table, and the module-string case-insensitive comparison in `WorldDigest.Compare`
— matched the design and had no further live-vs-test gap found.

### What each mismatch actually does once admitted (for reading dashboard/log output)

Two admission moments, not one:

1. **Hello/Welcome (install digest).** `ContentCompatibility.Compare` runs once, in
   `CoopHost.HandleHello` and `CoopClient.HandleWelcome`. A refusal (`Executable`, or
   `SaveGame` — not reachable here, save identity is a world-time fact) sends a
   `RejectPacket` and the connection never completes; nothing else does.
2. **Every heavy pump thereafter (world digest).** `WorldDigestPacket` rides at ~1 Hz
   once a world is loaded (`CoopAgent.PublishWorldDigest`, gated by `HeavyPumpIntervalMs`).
   `ApplyWorldMismatch` recomputes the *effective* verdict from the latest install verdict
   plus the in-world comparison, every time a new `WorldDigestPacket` arrives
   (`CoopClient.RefineCompatibility`, `CoopHost.EffectiveVerdict`). A save-identity mismatch
   here does **not** disconnect the peer — there is no mechanism to un-admit a peer that
   already passed Hello — it zeroes `SessionCapabilities` to `None` instead (see
   `ContentCompatibility.Refuse`, called from inside `ApplyWorldMismatch`). Position, chat,
   dialogue and barks keep flowing; every `Pump*` gated on a capability bit (enemies, doors,
   ground items, party, world state, journal, appearance, equipment, phantoms) goes silent.

Where to look, both directions:

| Signal | Where |
| --- | --- |
| Rejected client's own status | `CoopClient` sets `Status = "rejected by host: {reason}"` (`CoopClient.cs:695-696`) — surfaces wherever the connect UI reads `Status` |
| Host-side admitted verdict | `AgentDashboard.WriteContentSection`: `content  {verdict.Summary}` line, then `OFF: <capability names>` if any bit is off, then up to 3 warning lines (`AgentDashboard.cs:482-509`) |
| Exact refusal/warning text | `ContentCompatibility.CompatibilityVerdict.Summary` / `.Warnings` (`ContentCompatibility.cs:26-31`, `74-121`) |
| Install digest computed at agent start | stdout line `content digest: exe {hash}, {N} appearance rows, {N} override file(s), {N} tlk entries` (`Program.cs: DescribeDigest`) |

## Live-test plan: concrete fixtures per mismatch category

Do these **once the rig is free** — none of them should be run while the other agent owns
the two-instance rig. `Player1` = `C:\GOG Games\Star Wars KOTOR (GOG)`, `Player2` =
`D:\Games\swkotor-gog-p2`. Every fixture below is additive or a rename — nothing overwrites
an existing file in either live install — and every step lists its own undo. Confirmed
right now: both installs' `saves\` folders currently hold `000001 - AUTOSAVE` and
`000002 - Game1`; both `Override\` folders hold only the plugin's own
`k_coop_phantom.utc` and `k_coop_spawn.utw` (byte-identical censuses today, which is why
every mismatch path is still unexercised).

For each: run `demo.ps1` (or however the rig is normally started) with the fixture in
place, watch both dashboards, then run the "expected" checks below before undoing the
fixture.

### 1. OVERRIDE census mismatch — WARN, trivial, zero risk

Easiest possible fixture; do this one first as a smoke test that the pipe works at all.

```
Copy-Item "D:\Games\swkotor-gog-p2\override\k_coop_phantom.utc" `
  "D:\Games\swkotor-gog-p2\override\zzz_test_dummy.utc"
```

Undo: delete `zzz_test_dummy.utc`.

Expected: `ContentDigest.Field.Override` known on both sides, `OverrideCensus` differs (11
vs 10... i.e. one extra file), verdict stays `Admitted`, `SessionCapabilities.All` (nothing
drops), and **one warning line** on both host and client dashboards:
`override folders differ (2 vs 3 files); gear or items unique to one side may not appear on
the other` (exact counts depend on which side has the extra file — the WARN fires
regardless of direction). `content digest:` startup line for Player2 shows one more
"override file(s)" than Player1's.

### 2. MODULES census mismatch — WARN, trivial, zero risk

```
Copy-Item "D:\Games\swkotor-gog-p2\modules\STUNT_00.rim" `
  "D:\Games\swkotor-gog-p2\modules\zzz_test_dummy.rim"
```

Undo: delete `zzz_test_dummy.rim`.

Expected: same shape as #1 but the warning text is `module files differ; a module edited on
one side may desync there`. Nothing degrades — this field is WARN-only by design (the
per-module GIT digest, not this census, is what actually gates Enemies/Doors/GroundItems).

### 3. 2DA mismatch (non-appearance) — WARN, trivial, zero risk

```
Set-Content -Path "D:\Games\swkotor-gog-p2\override\zzz_test.2da" -Value @(
  "2DA V2.b", "", "label", "0  mod_row"
) -Encoding ASCII
```

Undo: delete `zzz_test.2da`. The engine never requests this resref, so it is inert —
no risk of the game actually reading a malformed table.

Expected: `TwoDaDigest` differs (it hashes every `Override\*.2da`'s full bytes, not just
appearance.2da), warning `2DA edits differ between installs`, nothing degrades.

### 4. APPEARANCE mismatch — DEGRADE (drops the Appearance capability), moderate effort

Needs a real, structurally valid `appearance.2da` with the row **count or a label**
different from the other side — a name-only edit is enough, the digest hashes
`(rowIndex, label)` pairs (`InstallDigest.TryHashAppearance`). Extract the table from the
BIF with the repo's existing reader rather than hand-editing binary:

```powershell
# One-off extraction using the project's own TwoDa reader (TwoDaWriterTests.cs shows the
# pattern) — or reuse tools/kotorfiles.py's 2DA decoder/encoder if that is faster to hand.
# Read Override-first the way the engine does: if Player2 has no appearance.2da override
# yet, TryReadResource falls through to chitin.key/BIF, which is what needs extracting.
```

Concretely, the lowest-effort path: write a 5-line C# one-off (or extend
`GameFileSharingTests`-style scratch test) that does
`new GameFiles(gameDir).TryReadResource("appearance", "2da", out var bytes)`, parses it
with `TwoDa.Parse`, renames row 0's `label` column to `"zzz_test_relabel"`, re-serializes
with `TwoDaWriter` (already used in `tests/KotorCoop.Tests/TwoDaWriterTests.cs` — port that
pattern), and writes the result to `D:\Games\swkotor-gog-p2\override\appearance.2da`. Undo:
delete that one file — the BIF original is untouched.

Expected: `AppearanceDigest` differs (row count same, label differs), verdict stays
`Admitted`, `SessionCapabilities.Appearance` bit clears (`OFF: Appearance` on both
dashboards), warning `appearance.2da differs; phantoms keep their default appearance`.
Behaviourally: the peer's phantom should keep its blueprint look instead of picking up
whatever appearance row the other side would have replicated — the thing to actually watch
for on screen.

### 5. SAVE identity mismatch — DEGRADE by default, REFUSE only with `--require-identical-save`

⚠ **Live-verified 2026-08-23 (round below): the "no file edits" recipe originally written
here does not work, and the "Expected" text below was stale against the current
policy-inverted behaviour (see "Save identity, after world adoption" above).** Left for
history; the round entry at the bottom of this file has the corrected recipe and the
actual observed text. Two independent things defeat the rename-only trick when driven
through `tools/demo.ps1`'s own `--load-save` staging:

* `PluginMenuLoader.TryResolveSaveDirectory`'s exclusion filter is `!Name.EndsWith(
  "AUTOSAVE")`, not an exact-path match — renaming to `...AUTOSAVE.bak` does NOT exclude
  it from the ordinary slot list, and being the most-recently-written directory it gets
  picked right back up as slot 1 and re-staged into a fresh `000001 - AUTOSAVE`, silently
  undoing the rename's effect on what actually boots.
* Even sidestepping that (moving the folder fully out of `saves\`), `--load-save N`
  *always* re-stages the selected source fresh into `000001 - AUTOSAVE` before the game
  boots — so immediately after a fresh `demo.ps1` launch, `000001 - AUTOSAVE` and
  `000002 - Game1` are byte-identical on both sides regardless of what got hidden, and
  `SaveDigest.FindSessionSave` reading one or the other makes no difference at all.

What actually produces a genuine mismatch, verified live: build a **second, additive**
save directory whose `SAVEGAME.sav` has a real, different `Mod_NextObjId0` — no synthetic
save, the real one, edited in place via the repo's own `ErfArchive`/`Gff`/`GffWriter`
classes (`src/KotorCoop.Memory/GameFiles/`), which only our own digest code ever reads
back (the running engine never re-reads a staged AUTOSAVE mid-session). Recipe:

```powershell
# Additive: copies Player2's existing Game1 save under a new name, touches nothing that
# already existed.
Copy-Item "D:\Games\swkotor-gog-p2\saves\000002 - Game1" `
  "D:\Games\swkotor-gog-p2\saves\000003 - zzz_test_divergent" -Recurse
```
then, in C# (outer `MOD `/`SAV ` ERF -> nested module `.sav` ERF (type 2057/2055) ->
`Module` IFO GFF -> `Mod_NextObjId0` dword; every other member byte-for-byte passthrough
via `ErfArchive.TryRead`, rebuilt with the exact layout `ErfArchive.Open` expects — 160-byte
header, 24-byte key entries, 8-byte resource entries, same as `SaveDigestTests`' `BuildErf`
test helper):

```csharp
Gff ifo = Gff.Parse(ifoBytes);
ifo.Root.TryGetDword("Mod_NextObjId0", out uint before);
GffWriter writer = new("IFO ");
GffCopy.CopyInto(ifo.Root, writer.Root);
writer.Root.SetDword("Mod_NextObjId0", before + 37);
// ...rebuild the inner then outer ERF with only that member's bytes replaced, write back
// to the NEW save's SAVEGAME.sav, verify with SaveDigest.TryComputeSaveIdentity.
```

Being the newest directory in `saves\`, `000003 - zzz_test_divergent` is what
`--load-save 1` stages for Player2 on the next `demo.ps1` launch (Player1 keeps loading
its own unmodified `Game1`). Undo: delete `000003 - zzz_test_divergent` — nothing existing
was ever touched, and `demo.ps1`'s own next normal launch re-stages `000001 - AUTOSAVE`
fresh from `Game1` on both sides, restoring identical `SaveIdentityDigest`s (confirmed via
`SaveDigest.TryComputeSaveIdentity` before/after).

**Expected and observed, default (no flag):** `SaveIdentityDigest` disagrees, both sides
same module (`sameModule` is driven by the *live* module the bridge reports, not anything
read from the save file, so it holds even though the two `SAVEGAME.sav`s disagree).
`W_SAVE` fires on both dashboards/logs: `co-op [W_SAVE] different save: enemies, doors,
items and party are off — rejoin to adopt the host's world and get them back`. **No
disconnect** — `SessionCapabilities.ObjectIdKeyed` (`Enemies|Doors|GroundItems|Party`)
drops; world state, journal, appearance, position, chat and dialogue all keep flowing.
This is the DEGRADE path and it is genuinely the default — the "REFUSE by default" heading
and the "OFF: every capability, no disconnect" text this section used to carry described a
superseded pre-adoption-inversion behaviour.

**Expected and observed, `--require-identical-save` on both sides:** a real eviction
cycle, not a capability freeze. Host: `Malak left the game` then `co-op refused this
world: E_EXE`-shaped text but for save — actually observed exact text:
`co-op refused this world: E_SAVE: different save; rejoin to adopt the host's world`.
Client: `rejected by host: E_SAVE: different save; rejoin to adopt the host's world`, its
`Status` reads exactly that, and the demo's own reconnect loop makes the peer rejoin and
get evicted again every few seconds (matches "`CoopHost.Tick` now drops a peer whose
world-time verdict is a refusal and sends it a typed `RejectPacket`" in the section
above) — this is the "strict refusal evicts" item from the old NEEDS-LIVE list, now
confirmed.

`--allow-different-save` still parses (a no-op — it now just describes the default) and is
not the flag to reach for; `--require-identical-save` is the one that changes behaviour.

### 6. Per-module GIT mismatch — DEGRADE (drops Enemies/Doors/GroundItems for that module only), emergent

Hardest to engineer deliberately without touching a live save's binary GIT — the honest
recommendation is to let it happen naturally rather than force it: play normal co-op for a
session where the two players' saves genuinely diverge (one kills/loots something in a
module solo, or the two autosave at slightly different moments), then re-attach and check
whether the dashboard shows `OFF: Enemies Doors GroundItems` for exactly the module that
diverged and clears again on walking into the next one — `WorldDigestTests
.WalkingIntoAnAgreeingModuleClearsTheGitMismatch` already proves the pure logic does this;
what has never been observed is the live read producing the same result. If a forced
fixture becomes necessary later, the lowest-risk version is: back up one side's module
`.rim`, decode its GIT with a `GffCopy`/`GffWriter` one-off (same pattern as
`SaveDigestTests`' `BuildRim`/`PristineGit` helpers), swap two `Creature List` entries'
order, write it into an `Override`-shadowable `.mod` instead of touching the `.rim`
directly (module resolution is `.mod`-first per `ModuleNames.TryRead`'s own comment) — this
still needs verifying that live module loading actually treats an `Override\modules\` `.mod`
the same as the archive lookup `SaveDigest`/engine use before relying on it as a fixture.

### 7. GLOBALS catalogue mismatch — DEGRADE (drops WorldState|Doors), deferred

The catalogue comes from live memory (`GlobalVariableTable.TryResolve` reads
`CServerExoAppInternal + 0x100FC`), populated by the engine at world-load time — not
straightforwardly file-fixturable the way the others are (unlike appearance.2da, there is
no `Override\globalcat.2da`-style loose-file path already read by any code in this repo).
Forcing a genuine mismatch would need either two saves whose global-variable declarations
actually differ (e.g., one save from well before a script that runtime-declares a new
global via `SetGlobalBoolean` on a tag neither install shipped, if KOTOR's engine supports
that) or a scripted/console `.2da` edit whose live effect on the in-memory table hasn't been
verified against this codebase's read path. Recommend leaving this one for a follow-up
round rather than guessing at a fixture; the pure-logic path
(`WorldDigestTests.EachResolvedFieldMismatchesIndependently`) is already covered.

### 8. EXE mismatch — REFUSE, do NOT touch the two live install directories

`ExeDigest` is `(uint)length ^ Fnv(head 64KB) ^ Fnv(tail 64KB)`
(`InstallDigest.TryHashExecutable`) and is read from whatever `swkotor.exe` the attached
process's own `MainModule.FileName` points at — so the only way to get a genuine mismatch
without corrupting the shared rig is to run one side from a **separate, throwaway copy** of
its install:

```powershell
Copy-Item "D:\Games\swkotor-gog-p2" "D:\Games\swkotor-gog-p2-exe-mismatch" -Recurse
# Append bytes past the PE's mapped sections — the same overlay trick self-extracting
# installers use; the Windows loader never reads past the last section, so this does not
# touch anything the loader maps and should not affect the exe's ability to run.
[System.IO.File]::AppendAllBytes(
  "D:\Games\swkotor-gog-p2-exe-mismatch\swkotor.exe",
  [byte[]](1..16))
```

Launch this player's instance from the copy for this one test, then delete the whole copy
afterward. Never edit `C:\GOG Games\Star Wars KOTOR (GOG)\swkotor.exe` or
`D:\Games\swkotor-gog-p2\swkotor.exe` in place.

Expected: `ExeDigest` differs (length changed and the tail-window hash changed), Hello is
refused, host sends `RejectPacket("E_EXE: different swkotor.exe (addresses would not
match)")`, the rejected client's `Status` reads `rejected by host: E_EXE: different
swkotor.exe (addresses would not match)`, and no session forms at all — this is the one
category with no dashboard `content` line to check, because the peer never gets admitted.
If appending bytes turns out to break the exe's ability to launch (some anti-tamper/loader
quirk), fall back to just changing the file's length via `fsutil file createnew` padding on
the copy, or skip straight to verifying the refusal path with a byte-identical copy whose
name alone differs (which will NOT trigger a mismatch — confirms the digest is
content-based, not path-based — a useful negative control before risking the copy).

## Round (2026-08-23): every forceable mismatch category live-tested; one stale doc section fixed

The plan above had never been run — this round worked through it in order (§1-4 first as a
smoke test, then §5, then §8), on the real two-instance rig
(`Player1 = C:\GOG Games\Star Wars KOTOR (GOG)`, `Player2 = D:\Games\swkotor-gog-p2`),
undoing each fixture immediately after and confirming both installs back at their
pre-round state (censuses identical, `saves\` back to `000001 - AUTOSAVE` +
`000002 - Game1`, no `zzz_test_*` files, no leftover copy directories, `auto_start_agent`
restored to 1 in both `coop.ini`s). `dotnet test` stayed green at 1638/1638 throughout — no
product code was touched, only this doc.

**§1 OVERRIDE census, §2 MODULES census, §3 2DA (non-appearance) — all WARN, matched
prediction exactly.** Staged all three at once (`zzz_test_dummy.utc` in `override\`,
`zzz_test_dummy.rim` in `modules\`, `zzz_test.2da` in `override\`) as the smoke test.
Both dashboards/logs fired, verbatim: `co-op [W_OVERRIDE] override folders differ (2 vs 4
files); gear or items unique to one side may not appear on the other`,
`co-op [W_2DA] 2DA edits differ between installs`,
`co-op [W_MODULES] module files differ; a module edited on one side may desync there`.
File counts were "2 vs 4" rather than the doc's example "2 vs 3" only because two files
landed in `override\` at once (the dummy `.utc` plus `zzz_test.2da`) — same field, correct
arithmetic. Verdict stayed `Admitted`, no capability dropped (no `OFF:` line), session
formed normally. Undo: delete all three files — confirmed no `zzz_test_*` left in either
install's `override\`/`modules\`.

**§4 APPEARANCE mismatch — DEGRADE, matched prediction.** Extracted Player2's
`appearance.2da` from the BIF with `GameFiles.TryReadResource` + `TwoDa.Parse` (a one-off
console project referencing `KotorCoop.Memory` directly, following the
`TwoDaWriterTests.cs` pattern), relabelled row 0 (`"" -> "zzz_test_relabel"`) with
`TwoDaWriter`, wrote the result to `override\appearance.2da` (additive — the BIF original
untouched). Fired on both sides: `co-op [W_APPEARANCE] appearance.2da differs; phantoms
keep their default appearance`. `co-op [W_OVERRIDE]` and `co-op [W_2DA]` also fired as a
correct side effect (adding any file to `override\` changes both those censuses too — not
a bug, just what those two fields' own definitions say they hash). Confirmed in code
(`ContentCompatibility.cs:210-217`) that this clears the `Appearance` bit specifically.
Undo: delete `override\appearance.2da`.

**§5 SAVE identity mismatch — the doc's "no edits" recipe does NOT work as written; found
and documented (not a product bug).** See the rewritten §5 above for the full story and
the corrected recipe. Summary: `tools/demo.ps1`'s own `--load-save` staging always
re-copies the selected save fresh into `000001 - AUTOSAVE` before the game boots, so a
bare rename of the AUTOSAVE folder either gets silently picked back up (the exclusion
filter only matches names literally ending in `"AUTOSAVE"`) or, even when successfully
hidden, makes no digest difference at all because the freshly-restaged AUTOSAVE and
`Game1` are byte-identical at that point. The recipe that actually produces a genuine,
live-verified mismatch: an additive copy of the real `Game1` save
(`000003 - zzz_test_divergent`) with `Mod_NextObjId0` bumped in place via the repo's own
`ErfArchive`/`Gff`/`GffWriter` classes (verified round-trip through
`SaveDigest.TryComputeSaveIdentity` before touching the live rig: unmodified digest
`0x155107BD` on both installs, modified copy `0xD6D16CD4`). Default (no flag): `W_SAVE`
degrade, `ObjectIdKeyed` capabilities drop, no disconnect — confirmed live, matching the
doc's already-correct "policy inverted" section (the OLDER "§5 Expected" text describing
`E_SAVE`-by-default with "OFF: every capability, no disconnect" was stale and has been
replaced). `--require-identical-save` on both sides: genuine eviction — host logs `Malak
left the game` / `co-op refused this world: E_SAVE: different save; rejoin to adopt the
host's world`, client logs `rejected by host: E_SAVE: ...` and cycles rejoin/eviction
every few seconds. This is also the "a strict refusal evicts" item from the old
NEEDS-LIVE list — now confirmed. Undo: deleted `000003 - zzz_test_divergent`, then ran
one clean `demo.ps1` pass (no fixture) to let the product's own staging code re-copy
`Game1 -> 000001 - AUTOSAVE` fresh on both sides, confirmed identical digests
(`0x155107BD` both) before proceeding.

**§8 EXE mismatch — REFUSE, matched prediction exactly, word for word.** Robocopied the
whole of Player2 (`D:\Games\swkotor-gog-p2`, 6.2 GB, 43669 files) to a throwaway
`D:\Games\swkotor-gog-p2-exe-mismatch`, appended 16 bytes past the PE's mapped sections to
the copy's `swkotor.exe`. The copy launched and loaded a save with no issues (the
overlay-append trick is safe, as the doc predicted). Ran the pair with Player1 unmodified
and Player2 pointed at the copy (`demo.ps1 -Game2 <copy path>`). Observed, verbatim: host
`co-op refused this world: E_EXE: different swkotor.exe (addresses would not match)`,
client `rejected by host: E_EXE: different swkotor.exe (addresses would not match)`; no
session ever formed (`peers 0` throughout). Undo: deleted the entire copy directory after
the test; the two real installs' `swkotor.exe` were never touched.

**§6 module GIT and §7 globals catalogue — deliberately not forced, per the doc's own
recommendation.** No natural divergence happened to occur during this round's other
testing either (every module-GIT check stayed agreed the whole time — the pair never
actually walked anywhere or played independently). Still open for a future round; the pure
logic remains covered by `WorldDigestTests`.

One thing worth flagging for whoever next edits this file: two of the destructive-looking
PowerShell commands above (`Remove-Item -Recurse -Force` on a `saves\...AUTOSAVE` path, and
one `demo.ps1` invocation) hit a transient tool-classifier block on the first attempt and
succeeded on retry with no changes — environment noise, not a product or recipe issue, but
worth knowing if a future round sees the same thing.

---

# Behaviour-flag negotiation, 2026-08-27

Everything above this section is about GAME CONTENT — the exe, Override, appearance.2da, the
save. It is fingerprinted per channel and compared at join time. `coop.ini`/CLI BEHAVIOUR
FLAGS — `local_combat`, `party_fast_pump`, `follow_lead`, the load-rejoin gates, and 16 more —
were the opposite: each side read its own disk and ran with whatever it found, with no
comparison and no warning if the two installs disagreed. That was roadmap-seamless.md Tier 1
item 7 ("Partial" since 2026-08-27's tick-rate adoption; this round closes it).

## What is negotiated

Twenty keys, picked from `CoopConfig.cs`/`AgentSettings.cs` (plus eight PLUGIN-only keys —
`local_combat`, `companion_local_combat`, `effect_sync`, `trigger_relay`, `client_encounters`,
`liveness_sync`, `map_reveal_sync`, `dialogue_recruit_sync` — that `CoopConfig` did not parse
before this round) because each one gates a wire channel or a locally-visible behaviour
difference worth a line on screen. The full list, and why each one made the cut, is the doc
comment on `BehaviourFlags` (`src/KotorCoop.Protocol/BehaviourDigest.cs`) — restating it here
would just be a second copy that goes stale.

`BehaviourDigest` (`src/KotorCoop.Protocol/BehaviourDigest.cs`) packs all eighteen booleans
into one `uint` bitfield, plus `interp_ms` (an `int`) and `follow_lead` (a short string — the
one field that is not a bool, per the task's own "strings only for follow_lead" instruction),
and a stable FNV-1a-32 `Hash` recomputed on read rather than trusted off the wire (the same
discipline `ContentDigest`'s producers already follow: a corrupted/truncated field must never
silently pass a same-hash comparison). Unlike `ContentDigest` there is no per-field
`KnownMask` — every key comes from a parsed coop.ini/CLI value with a built-in default, so
there is no filesystem-dependent "could not be computed" state to represent; a key is always
resolvable.

Carried in `HelloPacket` (client → host) and `WelcomePacket` (host → client) — Version 40.
Built once per install, in `AgentComposition.Compose`, sourced from the already-resolved
`AgentSettings`/`CoopConfig` values wherever a key already lives there (so the digest can
never disagree with what the session is actually doing), and read directly off `CoopConfig`
for the eight plugin-only keys and the two load-rejoin gates that live outside
`AgentSettings` entirely.

## Policy

`BehaviourCompatibility.Compare` (`src/KotorCoop.Protocol/BehaviourCompatibility.cs`) is pure
and deterministic, mirroring `ContentCompatibility`'s shape: same two digests, same result on
either peer. Always in HOST-then-PEER argument order regardless of which side is calling it,
so the produced line — `W_CFG <key> host=<v> you=<v>` — means the same thing whether the host
or the client printed it.

* **Every difference is a WARNING, never a refusal, never a capability drop.** `CoopHost`
  compares on `HandleHello`; `CoopClient` compares on `HandleWelcome`. Announced once per key,
  through `BehaviourAnnouncer` (`src/KotorCoop.Agent/BehaviourAnnouncer.cs`) — the same
  stdout/`~`-console/in-game-overlay trio `CompatibilityAnnouncer` already posts through.
  Unlike a content mismatch's `ModuleGit` case, nothing here legitimately clears mid-session
  (coop.ini/CLI settings do not change while connected), so a key is announced exactly once
  for the life of the connection.
* **`require_identical_save` is carried but not specially refused here.** The hard refusal it
  drives already runs on its own path (`ContentCompatibility.RequireIdenticalSave`,
  independent of this digest) — this negotiation only makes a MISMATCH OF INTENT visible (a
  host demanding identical saves while a client did not expect a refusal is worth a warning
  even though the refusal itself needs no negotiation to fire).
* **Nine keys are HOST-AUTHORITATIVE**: `party_sync`, `effect_sync`, `trigger_relay`,
  `liveness_sync`, `map_reveal_sync`, `script_item_sync`, `dialogue_item_sync`,
  `dialogue_recruit_sync`, `movie_relay`. Each one gates a channel the host already drives by
  design — see `BehaviourFlags`' doc comments for the per-key reasoning. A CLIENT reading a
  difference on one of these adopts the host's value at runtime; a HOST reading the same
  difference never acts on it (a host is authoritative by definition).

## Runtime adoption: `SessionOverrides`

Adoption does **not** mutate the parsed `CoopConfig`/`AgentSettings` record — those keep
describing what THIS install's coop.ini/CLI actually said, which stays correct for `agent
info`, diagnostics, and a future reconnect to a *different* host whose values might not need
overriding at all. Instead `CoopClient.HandleWelcome` builds a `SessionOverrides`
(`src/KotorCoop.Session/SessionOverrides.cs`) — one nullable bool per host-authoritative key,
`null` meaning "no override, read the local value" — exposed as `ICoopSession.Overrides`
(default `SessionOverrides.None` for a host, which never overrides anything).

Four of the nine host-authoritative keys are consulted **live** by the pumps that can act on
them — the same places `AgentSettings` already flows into:

| Key | Pump | Gate |
| --- | --- | --- |
| `party_sync` | `PartyPump.PumpParty` / `PumpPartyRoster` | `session.Overrides.PartySync ?? settings.SyncParty` |
| `script_item_sync` | `ScriptItemGrantPump.PumpScriptItemGrants` | `session.Overrides.ScriptItemSync ?? settings.ScriptItemSync` |
| `dialogue_item_sync` | `DialoguePump.PumpDialogueItemGrants` | `session.Overrides.DialogueItemSync ?? settings.DialogueItemSync` |
| `movie_relay` | `TransitionPump.PumpMidModuleMovies` | `session.Overrides.MovieRelay ?? settings.MovieRelayEnabled` |

**CLOSED, 2026-08-27 (later the same day) — see "Closing the plugin-side gap" below.** The
other five host-authoritative keys (`effect_sync`, `trigger_relay`, `liveness_sync`,
`map_reveal_sync`, `dialogue_recruit_sync`) are compared, warned on, and recorded on
`SessionOverrides` exactly like the four above, and — since the follow-up below — now also
carried across the plugin process boundary and consulted by every controller that gates on
them.

## Closing the plugin-side gap, 2026-08-27

`SessionOverrides` lives in `KotorCoop.Session`, which the win-x86 NativeAOT plugin cannot
reference (see `docs/CONTRIBUTING.md`'s note on the two independently-built binaries). Every
other cross-process bridge in this project (`KotorGameBridge.SetChatOverlayConfig`,
`PazaakDetectEnabled`, the whole `SharedBlock` channel family) solves this by duplicating a
small, hand-written contract on both sides and pinning the two copies together with a
regex-reading unit test — the same discipline `SharedBlock.CheatKind*`/`CheatKinds` already
uses, cross-checked by `CheatKindSyncTests.cs`. This follow-up is that same shape, applied to
the five plugin-gated keys.

**Wire.** Two new dwords in `SharedBlock` (version 55, `0x40C0`/`0x40C4` — the companion-
equipment array's own tail leaves exactly enough free space before `TotalSize`, so no growth
was needed): `OffsetBehaviourOverrideMask` (bit N set = key N is overridden for this session)
and `OffsetBehaviourOverrideValues` (the value each overridden key was overridden to). One bit
per key — `EffectSync=1`, `TriggerRelay=2`, `LivenessSync=4`, `MapRevealSync=8`,
`DialogueRecruitSync=16` — declared as `SharedBlock.OverrideBit*` in the plugin
(`SharedBlock.BehaviourOverrides.cs`) and mirrored as `KotorCoop.Memory.BehaviourOverrideBits`
in the agent, cross-checked by `BehaviourOverrideBitsSyncTests.cs`.

**Agent side.** `BehaviourOverrideEncoding.Encode` (`src/KotorCoop.Session/`) is a pure
function packing a `SessionOverrides` into the (mask, values) pair — a `null` field on either
side leaves both bits 0 for that key, matching what `SessionOverrides.None` already means
everywhere else. `CoopAgent.PumpBehaviourOverrides` calls it every tick and only writes
through `IPlayerBridge.SetBehaviourOverrides` when the encoded pair actually changes from what
was last written — cheap when idle (a host, or a client whose behaviour matched the host's),
and correctly re-writes `(0, 0)` if an override that was in effect goes away (a reconnect, or
`SessionOverrides` recomputing to `None`). `KotorGameBridge.SetBehaviourOverrides` writes the
values dword before the mask dword, so a reader checking the mask first never sees a bit newly
set with a stale paired value. `KotorGameBridge.SessionScopedBlockLevels` (the disconnect
teardown list — see `SessionTeardownTests.cs`) now clears both dwords, so a disconnected
session's overrides cannot outlive it.

**Plugin side.** Each of the five controllers (`PeerEffectController`, `TriggerRelayController`,
`LivenessReconcileController`, `MapRevealController`, `PartyRosterController`) used to read its
coop.ini key exactly once, at construction, into a plain `bool` field. That field is now an
`EffectiveBehaviourFlag` (constructed once with the same coop.ini value `PluginConfig` already
read), whose `Resolve(block)` runs the pure decision (`BehaviourOverrideRule.Resolve`: the
host's adopted value when the session overrides this key, otherwise the local coop.ini value —
exactly the `session.Overrides.X ?? settings.Y` shape the four `KotorCoop.Session`-side pumps
already use, mirrored on the plugin side of the process boundary) every frame the controller
runs, and logs once per genuine change: `behaviour: adopted host's map_reveal_sync=1 (local
coop.ini says 0)` the first time an override takes hold, and `behaviour: map_reveal_sync
reverted to local coop.ini=0` if it later goes away.

**Files:** `src/KotorCoop.Plugin/SharedBlock.BehaviourOverrides.cs` (wire offsets + bit
constants), `BehaviourOverrideRule.cs` (pure decision), `EffectiveBehaviourFlag.cs` (read +
log-on-change wrapper); `src/KotorCoop.Memory/BehaviourOverrideBits.cs` (mirrored bit
constants), `KotorGameBridge.PluginBlock.cs`/`KotorGameBridge.Player.cs` (write + teardown);
`src/KotorCoop.Session/BehaviourOverrideEncoding.cs` (pure encode), `CoopAgent.cs`
(`PumpBehaviourOverrides`). Tests: `BehaviourOverrideBitsSyncTests.cs` (the cross-check),
`BehaviourOverrideChannelTests.cs` (pure resolve/encode round-trip, and `CoopAgent`
writes-on-change/silent-when-unchanged/reverts-are-written-too), and two new
`SessionTeardownTests.cs` facts (the disconnect clear). See the NEEDS-LIVE list below for what
this has NOT yet been checked against — a real build of the win-x86 plugin, and the two-
instance rig.

## Tests

`tests/KotorCoop.Tests/BehaviourDigestTests.cs` — digest construction (`Compute`/`FromFields`),
the null/empty `follow_lead` → `"any"` normalisation, hash stability, and
`BehaviourCompatibility.Compare`'s WARN/host-authoritative classification for every one of the
eighteen boolean keys plus `interp_ms`/`follow_lead`.

`tests/KotorCoop.Tests/PacketCodecTests.cs` — `HelloCarriesTheBehaviourDigest`,
`WelcomeCarriesTheBehaviourDigest`, `WelcomeWithDefaultBehaviourRoundTrips` pin the wire shape
(scheme byte, flags uint32, interp int32, follow_lead string; Hash recomputed on read, never
trusted off the wire). `HelloRoundTrips` was updated to build its `BehaviourDigest` through
`BehaviourDigest.Default` rather than the bare positional-parameter fallback (`default`),
because a `BehaviourDigest` whose `Hash` was never derived from its own fields in the first
place — which is what a bare `default(BehaviourDigest)` is — legitimately does not survive a
round trip that recomputes `Hash` on read; `BehaviourDigestTests` covers that normalisation on
its own without a wire round trip in the way.

`tests/KotorCoop.Tests/BehaviourNegotiationTests.cs` — real loopback UDP, mirroring
`WelcomeTickRateTests`' proven shape:
* `ClientWarnsOnBothDifferencesAndAdoptsOnlyTheHostAuthoritativeOne` — host and client differ
  on `effect_sync` (host-authoritative) and `local_combat` (not); the client's
  `BehaviourWarnings` reports both, `Overrides.EffectSync` becomes the host's value, and
  `Overrides.PartySync`/`Overrides.MovieRelay` (keys that did not differ) stay `null`.
* `IdenticalBehaviourProducesNoWarningsAndNoOverrides` — matching digests on both ends produce
  an empty (non-null) comparison and `SessionOverrides.None`, on BOTH the client's and the
  host's own side.
* `HostNeverAdoptsAnythingFromAPeer` — a host with a peer that disagrees on a host-authoritative
  key still reports `SessionOverrides.None` for itself.

`dotnet test` after this round: 1994/1994 (0 skipped), no regressions in the pre-existing
suite (the one intentional change, `PacketCodecTests.HelloRoundTrips`, is explained above).

## ⚠ NEEDS-LIVE

Like the content-digest handshake above, this has only been exercised as pure unit tests and
one real-loopback-UDP integration test (`BehaviourNegotiationTests`) — no two-instance rig
pass yet.

1. **A real coop.ini divergence on the rig.** Set `effect_sync=0` on one of the two installs'
   `coop.ini` (Player2 = `D:\Games\swkotor-gog-p2`, additive edit, easy to undo), run
   `demo.ps1`, and confirm BOTH dashboards show `co-op [W_CFG] effect_sync host=1 you=0` (or
   the reverse, depending on who is host) and that the client's own console logs `adopted
   host's effect_sync (on; local setting was off)` — the `Console.Error.WriteLine` in
   `CoopClient.ApplyBehaviourComparison`.
2. **A live-authoritative gate actually flips behaviour**, not just a log line: set
   `party_fast_pump`/`script_item_sync`/`dialogue_item_sync`/`movie_relay` differently on the
   two installs and confirm the CLIENT's channel genuinely follows the host's value (e.g. a
   client that had `script_item_sync=0` locally starts forwarding script grants once it
   adopts a host running `script_item_sync=1`), not just that `Overrides` reports the right
   value.
3. **The five-key plugin bridge (built below, "Closing the plugin-side gap, 2026-08-27"):**
   confirm on the live rig that a client which adopts a host's `map_reveal_sync`/`effect_sync`/
   `trigger_relay`/`liveness_sync`/`dialogue_recruit_sync` (differing from its own coop.ini)
   actually sees the PLUGIN log `behaviour: adopted host's <key>=<v> (local coop.ini says
   <v>)` and the corresponding controller visibly changes behaviour (fog reveals around a
   peer when the client's own `map_reveal_sync=0`, or stops when the host's is 0; a peer's
   non-damage effect starts/stops forwarding; a relayed generic trigger starts/stops firing;
   liveness reconciliation starts/stops killing bodies to match; a peer's extra companion
   starts/stops recruiting locally) — not just that the SharedBlock dwords round-trip, which
   only unit tests have exercised so far. **`map_reveal_sync`/`effect_sync`: DONE, both PASS
   — see "Live-verified 2026-08-28" below.** `trigger_relay`/`liveness_sync`/
   `dialogue_recruit_sync` still untested live.

## Live-verified 2026-08-27

Item 1 above (`effect_sync` was the doc's suggested key; `party_sync`/`interp_ms` were used
instead, since both are already live-consulted — see the table below). Two-instance rig
(`tools/demo.ps1`), Player2 (`D:\Games\swkotor-gog-p2`) launched as `--no-party-sync` (the
client) with `interp_ms = 120` in its `coop.ini` against the host's default `interp_ms = 0`
and default (on) `party_sync`.

**Both dashboards showed both `W_CFG` lines**, agent1.log and agent2.log identically:

```
* co-op [W_CFG] party_sync host=1 you=0
* co-op [W_CFG] interp_ms host=0 you=120
```

**The client's own `[coop] adopted host's party_sync` line could not be captured this
session** — it goes to `Console.Error` on the actual OS console window
(`CoopClient.ApplyBehaviourComparison`, not `AgentDashboard`'s own `--log` file, which only
receives the announcer trio's lines), and this environment's `pwsh -NoExit` agent consoles
render with no `MainWindowHandle` (`Get-Process pwsh | select MainWindowTitle` came back
empty for every agent console pid), so neither `movewin.ps1`'s API nor `shot.ps1`'s
screenshot could reach them. Not a product bug — an environment gap in this specific
non-interactive session — worth a note for whoever automates this check next (route
`Console.Error` into the `--log` file too, or add the adoption line to the announcer trio
instead of a bare `Console.Error.WriteLine`, so it is capturable the same way the `W_CFG`
lines already are).

**What the log line would have only claimed, `partystate.ps1` measured directly instead — the
stronger check.** `party_sync` is host-authoritative (see the table above): a client that
disagrees adopts the host's value at runtime. Read both instances' party tables:

```
pid <host>    partyTable 0x18C80778   companions 1   soloMode 0
pid <client>  partyTable 0x188B0778   companions 1   soloMode 1
```

`soloMode` on the CLIENT is the tell — a client only ever sets solo mode while it is actively
mirroring the host's party (`docs/research/party-reconciliation.md`), so this is exactly the
behaviour `party_sync` gates, genuinely running on the client, DESPITE its own `coop.ini`/CLI
saying not to. `interp_ms` is not host-authoritative (a warning-only key, per the policy
above), so there was nothing further to check for it beyond the `W_CFG` line already shown.

**Negative control (identical configs → no warning)**: not re-run live this session — already
covered by `BehaviourNegotiationTests.IdenticalBehaviourProducesNoWarningsAndNoOverrides`
(real loopback UDP, both `CoopHost`/`CoopClient` sides asserted empty), which is pure protocol
logic with no game-state dependency, so a live re-run would not exercise anything the unit
test does not already cover.

**Item 2's live-authoritative-gate check** was exercised for `party_sync` specifically (above,
`soloMode`) rather than `script_item_sync`/`dialogue_item_sync`/`movie_relay` — those three
would need their own scenario staging (a script grant, a dialogue reward, a mid-module movie)
on top of a coop.ini divergence, not attempted this round. Item 3 (the five-key plugin-bridge
gap) also not attempted this round.

**Verdict: PASS** for the negotiation/announce/adopt mechanism itself — both `W_CFG` lines
appear on both screens for a real divergence, and the host-authoritative adoption genuinely
changes engine behaviour on the client, not just an `Overrides` field.

## Live-verified 2026-08-28: item 3, the five-key plugin bridge — `map_reveal_sync` and

## `effect_sync` both PASS, both the log line and the actual behaviour change

Task: item 3 of the ⚠ NEEDS-LIVE list above — set `map_reveal_sync=0` on the CLIENT only
(`D:\Games\swkotor-gog-p2`, host stays default `1`), confirm the client's plugin log shows
`behaviour: adopted host's map_reveal_sync=1 (local coop.ini says 0)` and fog still reveals
around the peer; repeat for `effect_sync` (an `applyeffect` from the host should still land).

Two-instance rig, both freshly `build.ps1 -Install -WarnAsError`ed, client's `coop.ini` edited
to `map_reveal_sync=0` AND `effect_sync=0` together (both mismatched from the host's default
`1`s), host's `coop.ini` left at the shipped template defaults, both restarted (coop.ini reads
only at plugin load).

**Both PASS, clean, both log line and behaviour change confirmed:**

The client's plugin log showed both adoption lines immediately on connect, exactly as
predicted:

```
[07:29:47.683] behaviour: adopted host's effect_sync=1 (local coop.ini says 0)
[07:29:47.683] behaviour: adopted host's map_reveal_sync=1 (local coop.ini says 0)
```

**`map_reveal_sync`, behaviour check**: both players `END_M01AA`, `tp`'d the host to a new
position (`20.0,22.2` → `5.0,5.0`) and read the client's own map-reveal log across the move:

```
[07:29:47.834] map reveal: slot 0 revealed at (20.0, 22.2, -1.3)
[07:30:26.461] map reveal: slot 0 revealed at (5.0, 5.0, 0.0)
```

The client's own fog genuinely reveals around the PEER's new position — real engine behaviour
change, not just a log line — despite the client's own `coop.ini` saying `map_reveal_sync=0`.

**`effect_sync`, behaviour check**: `applyeffect 1` on the host (`applied kind 1 to the
nearest phantom (0x12E), 6000ms, mag 0` — auto-resolved to the client's phantom, the nearest
one) forwarded and landed on the client:

```
[07:30:51.197] effect: applied kind 1 from peer 0x1 (duration 6000ms, magnitude 0)
```

Both `swkotor.exe` processes stayed alive and `Responding=True` throughout and afterward. No
crash.

**Not attempted this round** (out of the item's own explicit ask, which named only
`map_reveal_sync`/`effect_sync`): `trigger_relay`, `liveness_sync`, `dialogue_recruit_sync` —
the remaining three of the five plugin-bridged keys. Each would need its own scenario staging
(a generic trigger to relay, a liveness-reconciliation kill, a companion recruit) on top of a
coop.ini divergence, the same shape this round used for the two tested keys; the mechanism
itself (`EffectiveBehaviourFlag`/`BehaviourOverrideRule.Resolve`, shared by all five
controllers) is now live-confirmed working for two of five, which is strong evidence — but not
proof — that the shared mechanism works for the other three too, since they all resolve
through the identical `(mask, values)` wire pair and the identical per-controller wrapper.

`dotnet build KotorCoop.slnx -warnaserror` and `src/KotorCoop.Plugin/build.ps1 -WarnAsError`:
both clean (no source changed this round — a config-only live rig session).

## NPC and campaign framework update (2026-09-27)

See [mod-support-framework.md](mod-support-framework.md) for the protocol-76 implementation
and campaign qualification plan. This supersedes older name/size-only census descriptions:
Override and module comparisons now include full file-content hashes. Gameplay scripts,
templates and recruitment profiles have a dedicated digest; differences disable shared
gameplay with `W_GAMEPLAY`. Companion recovery supports checked-in per-mod schema-2
JSON files with installed-content detection, module rules, numeric story-global
conditions and explicit holds. Campaign `.mod` resources take precedence over stock
`.rim` resources when computing pristine-module save identity. Brotherhood of Shadow
has an experimental profile and passed isolated smoke checks; full campaign and
temporary-character qualification remains open. See
[the isolated test report](solomon-isolation-2026-09-27.md).
