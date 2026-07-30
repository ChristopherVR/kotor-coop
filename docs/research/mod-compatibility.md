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
