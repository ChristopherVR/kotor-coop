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
