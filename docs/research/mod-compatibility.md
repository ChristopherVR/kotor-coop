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

### 5. SAVE identity mismatch — REFUSE by default, moderate effort, no file edits

No hex-editing needed: the two installs' saves already diverge enough for this, because
`SaveDigest.FindSessionSave` prefers the staged `000001 - AUTOSAVE` and only a rename is
needed to make it fall back to the older `000002 - Game1` on one side.

```powershell
Rename-Item "D:\Games\swkotor-gog-p2\saves\000001 - AUTOSAVE" `
  "D:\Games\swkotor-gog-p2\saves\000001 - AUTOSAVE.bak"
```

⚠ This is not a digest-only trick — `PluginMenuLoader` uses the same staged-autosave
convention to decide what the *actual running game* loads, so Player2 will genuinely boot
into the older `000002 - Game1` save instead of the staged one for this session. That is
what makes the mismatch "genuine" rather than synthetic, but it also means Player2's
in-game state for this test run is whatever `Game1` was saved at, not the usual coop-staged
position — expect the two players to start in different modules. Undo immediately after
the test: `Rename-Item "...\000001 - AUTOSAVE.bak" "...\000001 - AUTOSAVE"`.

Expected, with `--allow-different-save` NOT passed (the default): once both worlds are
loaded and the first `WorldDigestPacket` round-trips, `SaveIdentityDigest` disagrees (very
likely also a different `Module` string, so `ModuleGitMismatch` never even gets judged —
see `WorldDigest.Compare`'s same-module gate). `ApplyWorldMismatch` returns
`Refusal = RefusalReason.SaveGame`, `Allowed = SessionCapabilities.None`. **No
disconnect** (see "what each mismatch actually does" above) — both dashboards show
`content  E_SAVE: different save; object ids would not line up` followed by
`OFF: <every capability>`. Position sync, chat, dialogue and barks should still work;
everything else (party roster, enemies, doors, ground items, journal, appearance) should go
silent on both sides.

Optional second pass: rerun with `--allow-different-save` (check `AgentOptions`/coop.ini
for the exact flag name) to see the degrade path instead: `Admitted = true`, warning
`different save (allowed): enemies, doors, items and party are not shared`, and
`SessionCapabilities.Enemies|Doors|GroundItems|Party` cleared but everything else (world
state, journal, appearance) still on.

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
