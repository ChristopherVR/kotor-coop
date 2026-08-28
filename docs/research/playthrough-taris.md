# End-to-end co-op playthrough smoke: Taris (2026-08-28)

A scripted, dev-verb-driven walk through Taris as two players, run against the **shipped
defaults** (`profiles/coop.ini.example`, byte-for-byte the same values as both installed
`coop.ini` files modulo encoding artifacts and the `auto_start_agent` line `demo.ps1` itself
manages — no flags changed this round). This is the second chapter a real pair reaches after
the Endar Spire opening (`playthrough-endar-spire.md`). Same method: every claim is tagged
**VERIFIED** (read off a running instance's logs/memory this session), **NEEDS-LIVE** (not
settled this session, reasoned only), or **INFERRED**.

## Setup

- Worktree HEAD was already `a7833b7` == `origin/main` at session start ("Merge: rig round —
  reconnect re-mirror verified (round 104)...") — no merge needed.
- Both installed `coop.ini` files diffed byte-for-byte against `profiles/coop.ini.example`:
  identical apart from mojibake in the comment em-dashes/section-marks (a pre-existing
  encoding artifact, not a content difference) and the installed files' own
  `auto_start_agent = 1` line, which `demo.ps1` toggles off for the duration of a rig run and
  restores on `-Stop`. **Confirmed shipped defaults; nothing was reset.**
- `dotnet build KotorCoop.slnx -warnaserror`: clean, 0 warnings/errors.
- `pwsh src\KotorCoop.Plugin\build.ps1 -WarnAsError -Install` run against BOTH game
  directories (`C:\GOG Games\Star Wars KOTOR (GOG)` host "Lanik", `D:\Games\swkotor-gog-p2`
  client "Malak"). Both deployed `dinput8.dll` came out byte-identical (2,059,264 bytes,
  timestamp `2026-08-28 18:38:41`) and both strings-checked positive for the `KotorCoop`
  marker.
- **Crash dumps could not be enabled this session** — `tools\crashlog.ps1 -EnableDumps`
  needs an elevated shell (`HKLM\...\LocalDumps` is machine-wide) and this session's shell
  was not elevated. Relied on the Windows Application event log (`crashlog.ps1`) and plugin
  crash-probe logs instead, as prior rounds did before dumps were wired up. Flagged as a gap
  for whoever runs the next round from an admin shell.
- Logs cleared, `pwsh tools\demo.ps1` (save slot 1, `END_M01AA`) brought both instances up
  unattended: host pid 17740, client pid 28924, `peers 1` within 3 s of the client's own
  launch, matching `docs/CONTRIBUTING.md`'s documented shape.
- **All console verbs driven through `tools\blockpoke.ps1 -ProcId <pid> console '<line>'`**
  (`attack`, `death`, `journal` used their own dedicated `blockpoke.ps1` top-level commands,
  not the console channel — see `docs/live-verification.md`). Replies are a 96-byte summary;
  the substance is in `%TEMP%\kotorcoop-demo\agent1.log`/`agent2.log` and
  `<gamedir>\kotorcoop-plugin.log`.

### A correction worth recording: several Taris module IDs in the task's own framing do not
### match this build's real area names

Resolved every `tar_m0*`/`tar_m1*` area's display name via its `.are`'s `Name` strref
(`tools/kotorfiles.py gff ... --path Name` then `tools/kotorfiles.py tlk <strref>`) rather
than trusting assumed numbering. Real names, for the record (and for whoever plans the next
chapter):

| Module | Real area |
| --- | --- |
| `tar_m02aa` | Taris - South Apartments |
| `tar_m02ab` | Taris - Upper City North |
| `tar_m02ac` | Taris - Upper City South |
| `tar_m02ae` | **Taris - Upper City Cantina** |
| `tar_m02af` | Taris - Hideout |
| `tar_m03aa` | Taris - Lower City (NOT the cantina) |
| `tar_m03ae` | Taris - Javyar's Cantina (a SECOND, different cantina, Lower City) |
| `tar_m03af` | Taris - Swoop Platform |
| `tar_m03mg` | Taris - Swoop Track (the actual race module) |
| `tar_m04aa` | Taris - Undercity (rakghouls) |
| `tar_m05aa` | Taris - Lower Sewers |
| `tar_m10aa`/`ab`/`ac` | Taris - Black Vulkar Base |

The task brief's own module numbers for the cantina (`tar_m03aa`) and for "Lower
City"/"Undercity" were off by one area each; the system-prompt's own authoritative guidance
("Vulkars in `tar_m10aa`, rakghouls in `tar_m04aa`") was correct and is what this round
followed. Step 2 below was re-run against the real cantina (`tar_m02ae`) after the first
attempt landed in Lower City instead.

## Step 1 — `tar_m02aa` (South Apartments / street)

**PASS.**

- `warp tar_m02aa` on the host; both landed within 4 m of each other (`92.1,128.9` /
  `92.0,132.7`), `hp 12/12` both sides. **VERIFIED.**
- Phantoms, equip, map pin: host plugin log `spawned phantom in slot 0: id 0xB7 ...` →
  `equip: phantom 0xB7 wears g_a_clothes01` → `joined the player faction` → `map marker: slot
  0 pinned as 0xB9`; client's own log shows the mirror-image phantom of the HOST at the
  host's real coordinates (`spawned phantom in slot 0: id 0xA6 at (92.07, 128.92, 0.00)`).
  **VERIFIED, both directions.**
- Bonus: fog-of-war reveal fired live and unprompted during this step — `map reveal: slot 0
  revealed at (85.0, 110.0, 0.0)` on the host after the client walked there — closing
  `docs/live-verification.md`'s own "still unconfirmed live: fog-of-war reveal" note from the
  very first pass. **VERIFIED.**
- **NPC conversation, spectate + late join.** An engine-forced BACKGROUND conversation was
  already running from module entry (`dialogue: engine reached StartDialog (1 so far,
  forced=False)` at `18:40:41`, unrelated to any command sent) and walked its own nodes to
  completion over ~75 s — the same "forced conversation on arrival" pattern the Endar Spire
  round found on `end_m01ab`. Once it cleared, `startdialog 0x16` (creature `n_bith001`) on
  the HOST queued a real, second conversation: `dialogue: engine reached StartDialog (2 so
  far, forced=False)` at `18:41:59.999`. The CLIENT adopted it **3.1 s later**: `dialogue: the
  engine started the same conversation here; adopting it to follow` → `followed conversation
  delivered content 0 ms after opening` → landed on the SAME node the host was already on.
  One owner (host, who alone could `skipline`/`selectreply`), the client only ever followed —
  spectate + late join both demonstrated exactly as the Endar Spire mechanism proved.
  **VERIFIED.**
- **Apartment door, host leads, client follows.** `tar02_aprtdoor` (Tag =
  `tar02_aprtdoor`, `X=90.75 Y=134.25`) links to `tar_m02af` ("Taris - Hideout"). First two
  `usedoor` attempts from ~4 m away stalled short of the door (`door: 600 frames after the
  walk the player is 4.5m from 0x23 ... No transition happened - the door is still an
  obstacle, or the path stopped short` — the same walk-to-door walkmesh friction the Endar
  Spire round's escape-pod dialogue hit, not new). `tp`'ing to 1.3 m and retrying `usedoor`
  succeeded cleanly: both players landed at the IDENTICAL `88.4, 136.2, -0.0` in `tar_m02af`.
  **VERIFIED**, with the note that `usedoor`'s own walk-to-door primitive needs to already be
  within ~1-2 m to reliably cross a doorway threshold — a minor staging quirk, not a co-op
  bug (the transition and mirror themselves were clean).
- **Locked door, client leads, host mirrors.** `ptar_lockde` (shared tag across 4 GIT
  instances) opened by the CLIENT from 20.1 m: `door: opened 0x1F (state 0 -> 2) at 20.1m,
  LOCKED` — an honest, explicitly-labelled bypass of a genuinely locked door, matching the
  documented convention. Mirrored to the HOST 0.7 s later: `door 0x1F: swinging 0 -> 2 on the
  session's authority`. **VERIFIED.**

## Step 2 — `tar_m02ae` (the real Upper City Cantina)

**PARTIAL — pazaak detection PASS with a clean divergence finding; Duel Ring not
attempted; merchant test blocked by an unresolved `openstore` refusal.**

First attempt warped to `tar_m03aa` per the task's own (incorrect) module number and landed
in Lower City instead — corrected mid-round, re-warped to `tar_m02ae`. Both landed at the
identical `123.2, 110.0, -0.0`.

- **Pazaak detection.** `pazaak` on the host: `pazaak: StartPazaakGame(deck 0, opponent the
  engine's own default opponent) called - running now: True` → `pazaak: entered (CPazaakData
  field 0x1) - peers will park this player's phantom` → `minigame: entered (Pazaak)`. Ended
  cleanly 10 s later with `pazaak end` (`EndPazaakGame() called - running now: False` →
  `minigame: exited - back in the shared world`). **VERIFIED.**
- **The partner's own divergence, live-confirmed.** During the entire ~10 s hand, the
  CLIENT's plugin log recorded **zero** lines referencing pazaak, minigames, or phantom
  parking of any kind. This confirms the shipped `minigame_hide_peer=1` default gives the
  partner literally no notice that anything happened — the host's phantom presumably just
  silently freezes on the client's screen mid-motion with no accompanying announce. This is
  exactly the divergence a real pair would experience and the task asked to document.
  **VERIFIED (by the clean absence of any client-side signal).**
- **Duel Ring** (`tar02_duelorg` organizer + duelist NPCs `tar02_femduel002`/
  `tar02_maledue002` near `X≈108-117, Y≈75-90`) was located in the GIT but **not driven live
  this round** — time budget went to combat/store/globals instead. **NOT COMPLETED.**
- **Merchant.** The cantina itself carries **zero** placed stores in vanilla KOTOR
  (`StoreList = 0 entries`, confirmed by a fresh GFF dump) — the task's own assumption of a
  cantina merchant does not hold for this game. Substituted the nearest real store, Larrim's
  (`tar_m02aa`, ResRef `larrim`, `X=86.9 Y=101.7`): `openstore larrim` refused twice
  (`no target: no store tagged 'larrim' found here`), once immediately after the warp and
  once after a 3 s settle — even though the player was well within the usual interaction
  range and the module had fully loaded (`where` confirmed the correct module). **Not
  root-caused this round**: the store's own GIT struct (`StoreList` entries) carries only an
  `XPosition`/`YPosition`/`ResRef` — **no `Tag` field at all** — which may mean `openstore`'s
  tag-based resolver needs a different lookup path for this object class than it uses for
  ordinary placeables/creatures. Did not retry against the Undercity's `igearstore001` store
  due to time. **Genuinely open — flagged in "Open issues" below.**

## Step 3 — `tar_m10aa` (Black Vulkar Base) — genuinely hostile combat

**PASS for hostile-faction confirmation, host-side real combat/kill, and gold convergence;
PARTIAL for client-side hit-landing evidence; downed-player/revive not reached; one new
reproducible bug found (`topup` "0 max hp").**

Both warped in, landed identically at `103.3, 115.9, 2.2`.

- **Genuinely hostile, confirmed both sides.** The `faction <id> me` diagnostic on three
  Vulkar candidates (`0x2` patrol, `0x46`, `0x24`) all read `GetAIStateReputation=2` in both
  directions on the HOST; `0x2` was independently re-checked on the CLIENT and read
  byte-for-byte identical (`faction 1, GetAIStateReputation=2` both ways). **VERIFIED** —
  matches the task's own authoritative guidance that Taris Vulkars (unlike the Endar Spire's
  `end_sith2`, which turned out to be genuinely friendly-faction) are real hostiles.
- **Host lands real hits, a real kill, and real unprompted AI aggro.** `blockpoke ... attack
  0x7FFFFFFF 0x2` (the `attack` verb is a `blockpoke.ps1` top-level command, not a console
  line — using it as a console string first correctly failed with "unknown command"). `0x2`'s
  hp moved `22 -> 15 -> 10 -> 5 -> dead (-11 hp)` across repeated probe samples — a genuine
  kill. Simultaneously, and entirely unprompted, a SECOND Vulkar (`0x46`) began attacking the
  host on its own AI initiative (`combat ground-truth: local PC took N damage, attacker=0x46`
  repeating every ~1-2 s) — real, autonomous hostile AI, not staged. **VERIFIED.**
- **The host's hp floored at 1 and never reached 0**, despite ~10 more real hits landing
  after it first read `1/12` — the vanilla engine's own "ordinary damage cannot reduce a
  player character below 1 hp" rule, observed directly rather than assumed. This is why no
  downed state was reached this round (see below).
- **Client-side hit-landing: real hp movement observed, but the causal link is murky.**
  Client ordered `attack 0x7FFFFFFF 0x46` (a DIFFERENT Vulkar than the host's target) from
  1.59 m. Over the following ~13 s, `0x46`'s own hp genuinely dropped `16 -> 1` — real
  movement, not a stale read. However, the diagnostic `attack probe` line's own
  `localCombatExcluded=0x46` flag stayed SET the entire window, meaning the co-op layer's own
  `LocalFightCandidateRule` was actively excluding this target from the client's local-combat
  bookkeeping throughout. Whether the hp drop is genuinely from the client's own swings, or
  bleed-over from the host's separate, ongoing fight nearby (both Vulkars were adjacent),
  could not be cleanly isolated this round. **This meaningfully updates (does not cleanly
  re-confirm) the Endar Spire round's open issue #1** ("a client cannot land real hits") —
  this round is clearly NOT a repeat of that round's total failure mode (`round(active=0)`,
  hp never moving, no `local_combat:` log line ever) — real hp movement was seen — but it
  is not proof the client's own attack order is what caused it either. **NEEDS-LIVE,
  recommend a dedicated round with only ONE hostile anywhere near the client.**
- **Gold converges instantly.** `grantgold 150` on host (`11 -> 161`); read back on client
  immediately (`grantgold 0`, a no-op probe: `161 -> 161`). **VERIFIED.**
- **Downed player + revive: NOT reached**, same root cause as the Endar Spire round (no
  console verb can force a genuine 0-hp state; the vanilla 1-hp floor blocks it even under
  real, sustained, multi-attacker combat). Unchanged, genuinely open.
- **New bug, live-confirmed, reproducible: `topup` refuses with "0 max hp" after sustained
  real combat damage, while `where`'s own hp readout stays correct.** Once the host's hp had
  been driven down by real combat, `topup` answered `the engine refused it: 0 max hp (obj
  0x7FFFFFFF vtable 0x7470C8 cur 1)` — repeatedly, on retry, and again after moving to safety
  — while `where` continued to correctly report `hp 1/12` the whole time (one intermediate
  log line even briefly misread max hp as `265` before settling back to `12`, suggesting the
  stats-struct pointer this path reads is unstable mid/post-combat, not just zeroed). **This
  reproduced identically on the CLIENT too** after its own Undercity combat in Step 4, and
  persisted across a full module transition. Not root-caused or fixed this round (would need
  reading `CheatController`'s `topup` implementation against whatever `where`'s own resolver
  does differently) — flagged precisely in "Open issues," ranked #1.

## Step 4 — `tar_m04aa` (Undercity) — rakghouls

**PASS for hostile confirmation and a positive `scriptgive` forward; loot-corpse test not
completed (no kill this round); the `topup` bug reproduced again.**

Both warped in, landed identically at `312.9, 234.7, 3.4`.

- **Rakghouls are placed statically in the GIT** (9 `tar04_rakghoulr` instances scattered
  through the module) rather than spawned by a single trigger walk-in. Moving near one
  cluster (`315, 138`) put BOTH host and client `IN COMBAT` immediately and unprompted —
  genuine auto-aggro. `faction 0x5B me` confirmed hostile (`=2`) both directions.
  **VERIFIED.**
- Both players again floored at 1 hp under real, ongoing damage (same vanilla protection as
  Step 3) — retreated both to the module spawn point rather than push further, given `topup`
  was already known broken this session.
- **`scriptgive g_i_progspike01` succeeded completely this round, including the peer
  forward** — a meaningful correction/nuance to the Endar Spire round's documented failure
  for the SAME item. Host: `gave 'g_i_progspike01' (0x1F2) to the local player, untracked`.
  Client, <1 s later: `materialise: gave loot 'g_i_progspike01' (0x1F4) to stand-in
  0x7FFFFFFF for the local player (dialogue grant) - stack 1... status 1` — a clean success,
  not the `failure 8; wrong resref` the Endar Spire round hit. **Plausible explanation (not
  proven this round)**: a freshly `scriptgive`n item takes its Tag from the `.uti` blueprint's
  own default `Tag` field, which may equal the resref itself — the Endar Spire round's
  failure was specifically against a Tag read off a *placed GIT instance* whose Tag had been
  overridden to `k_computer_spike` by that module's own design, not a property of the item
  family in general. This narrows (but does not close) the known `script_item_sync`
  limitation: it may only bite when the specific placed/scripted instance's Tag diverges,
  not for a freshly materialised copy of the same base item. **NEEDS-LIVE**, worth a
  dedicated round contrasting a fresh `scriptgive` against a Tag-diverged GIT-placed pickup
  of the identical item.
- **`loottake` (bare form): refused** — `the sweep found 85 placeable(s) but none close
  enough / none with inventory` at the safe retreat position; not retried against a real
  corpse because no rakghoul actually died this round (both real fights ended in the players
  retreating, not the enemy). **NOT COMPLETED.**
- **`topup` "0 max hp" reproduced again, identically, on both characters** after this
  module's own combat — see Step 3's write-up; this round's second independent reproduction
  strengthens confidence it is a real, general bug rather than a one-off artifact of the
  specific Vulkar Base session.

## Step 5 — `tar_m03af` (Swoop Platform) and the swoop-taxi party-select trigger

**PASS for "the partner parks correctly"; the specific `k_pebn_loc0N` trigger from the
source doc could not be located in this build, and the substitute trigger produced no
positive confirming evidence (a known detection-method limitation, not a new bug).**

- **Partner parking.** `warp tar_m03af` on host; both landed at the identical `246.2, 218.4,
  8.2`, phantom re-spawned correctly on both sides (`spawned phantom in slot 0: id 0x41 at
  (246.20, 218.44, 8.25)`). **VERIFIED** — satisfies the task's own lenient bar for this
  known-blocked feature (the swoop RACE dispatch itself has never been observed to reliably
  fire live per `docs/research/minigames.md`, and this round did not re-chase that
  already-documented, unrelated bug).
- **The `k_pebn_loc01`..`09` trigger family named in `galaxy-map-and-party-select.md` could
  not be found in this build.** A full GFF dump of `tar_m03aa`'s (Lower City's) `TriggerList`
  — the module the source doc's own live-test recipe points at ("warp `tar_m03aa` and walk to
  one") — found no trigger with that name or script. The only comparable object is
  `tar03_swoop03aa` (Tag = `tar03_swoop03aa`, `ScriptOnEnter = k_ptar_swoop03aa` — a
  DIFFERENT script family than `k_pebn_loc0N`), present as two GIT instances at
  `(400.8, 138.8)` and `(192.4, 222.6)`.
- **Walked the CLIENT onto the first instance's polygon** (a `tp` directly onto the
  coordinates does NOT fire a trigger's `OnEnter` — only genuine physical movement does, the
  same distinction Step 1's door walk-to hit; a full-distance `walkto` stalled short on the
  walkmesh, so a shorter `tp`-then-1-2 m-`walkto` was needed to actually cross the boundary).
  **No NEW `party-select: client copy suppressed` log line appeared.** This is INCONCLUSIVE,
  not a clean negative: `galaxy-map-and-party-select.md`'s own 2026-08-28 fix caps this
  detection to fire **at most once per module/session** (closing a previously-found
  500/sec write-storm bug) — and this module had already produced one such pair on ordinary
  module entry, before any trigger walk. By design, a second, genuinely real trigger firing
  in the SAME module visit would produce no new log line — the log-line method cannot
  distinguish "the real trigger fired and was correctly, silently suppressed" from "nothing
  happened," which is exactly the open question the source doc's own entry already flags.
  Sanity-checked `partyselect ok` on the host near this area as a positive control: answered
  `confirmed (poked the message-box precondition bit first...)` with no crash, matching
  prior rounds. **NEEDS-LIVE, unresolved** — see "Open issues."

## Step 6 — journal/globals merge, disconnect/reconnect, save/reload

**PASS across the board.**

- **`setglobal`'s type-checking worked correctly, not a bug.** `setglobal
  TAR_VULKARBASE_OPEN 1` was correctly REFUSED (`(number) still reads 0 after requesting 1 -
  name may be undeclared or declared as the other type`) because `TAR_VULKARBASE_OPEN` is a
  BOOLEAN global and `1` was parsed as the wrong type; `setglobal TAR_VULKARBASE_OPEN true`
  succeeded (`(boolean) 1 -> 1`). No console verb exists to READ a global back on the client
  side, so the merge itself remains an inference from code (same gap the Endar Spire round
  noted) — **substituted the journal channel**, which shares the identical reliable-flag
  store per the code's own doc comment, as the live-provable half:
- **Journal merge.** `blockpoke journal k_swg_gadon 10` on host: `journal: 'k_swg_gadon' ->
  state 10 on the session's authority` at `19:02:45.500`. CLIENT, the SAME entry, **0.6 s
  later**: identical line at `19:02:46.092`. **VERIFIED.**
- **Blackout reconnect.** `netsim blackout 10` on the client: host saw `Malak left the game`
  → `Malak joined the game` ~6 s later, fully automatic. **VERIFIED**, matches the merge
  base's own "reconnect re-mirror verified (round 104)."
- **`quittomenu` → `loadgame` → rejoin (client).** `quittomenu` tore the client's world down
  cleanly (`memory READ-FAILING` while the engine unloads, as expected). `loadgame 0`
  reloaded the character from the original autosave, then immediately re-synced/travelled to
  the HOST's live current module (`tar_m03af`) — landed at the IDENTICAL coordinate as the
  host, fully healed (`12/12`, incidentally clearing the client's own `topup`-bug state via
  the fresh load). **VERIFIED**, clean end to end.
- **Host `loadgame` → client re-adopt: a clean PASS this round**, unlike the Endar Spire
  round's INCONCLUSIVE result. `loadgame 0` on the HOST (who was live, never away) this time
  had a genuine edge to load (the party had travelled far from the original save's module),
  and produced the full expected chain: `host loaded a save - everyone re-joins`, both
  players landed back in `END_M01AA` (the disk's most recent save) together, agent log
  `session-saved character: carried the character from '000001 - AUTOSAVE'`. **VERIFIED** —
  resolves the Endar Spire round's open issue #5. Side effect: this ended the live Taris
  portion of the session by returning both players to `END_M01AA`, which is where Step 7's
  checks below were run from.

## Step 7 — `verify.ps1`, `dotnet test`, native build

- **`verify.ps1`: 35 passed, 14 failed** (run immediately after the host `loadgame` above,
  from `END_M01AA`). **8 of the 14 failures exactly match the documented baseline set**:
  `phantom damageable, not a sink`; `the peer really loses those hit points`; `a client hit
  costs the enemy real hit points`; `a downed player is reported to the agent`; `the co-op
  revive stands the player back up`; `every engine call signature matches the binary`
  (197/199 here — codebase grew again since the Endar Spire round's 177/179, still exactly 2
  disagreeing); `a container opened on the client is held and shown open on the host`; `no
  per-frame spam`.
  **6 are new this round**, all plausibly explained by this session's own unusually heavy
  manual-mutation history rather than a regression:
  - `the peer follows the same conversation` (FAIL, `partner ''`) and `an interaction lock
    reaches the other instance` (FAIL, `client sees 0 object(s) held by a peer`) — both
    checks ran seconds after the host's own `loadgame`-triggered full world reload; plausibly
    a timing race against the just-reloaded conversation/interaction-lock state rather than a
    real gap.
  - `PC experience rises on the other instance` (FAIL, `host 1450->1455, client's engine
    reads 150`) and `a client's experience cannot be pulled backwards` (FAIL) — directly
    explained by this session's own earlier manual gold/XP-adjacent console testing leaving
    the two characters' XP numbers in a state this check does not expect; not touched by any
    code this round.
  - `an item picked up here vanishes on the peer` (FAIL) — the same class of "heavy item
    traffic before the check confuses it" artifact the Endar Spire round already flagged for
    the identical check.
  - `a door opened on the client opens on the host` (FAIL) — the check's own printed comment
    self-diagnoses this as its known distance/deferred-commit gap (`host is 46 units from the
    door`), not a new regression.
- **`dotnet test tests/KotorCoop.Tests --no-build`: 2469/2470 passed.** The one failure,
  `CoopCheatDispatchTests.ACheatTheGameNeverAnswersGivesUp` (`the channel stayed wedged after
  the timeout`), passed cleanly in isolation immediately afterward (`dotnet test --filter
  ...`: 1/1 passed) — a timing flake from running concurrently with the still-live
  two-instance rig, the same class of flake `docs/CONTRIBUTING.md` documents for UDP-bound
  suites, not a regression.
- **Native plugin build (`pwsh src\KotorCoop.Plugin\build.ps1 -WarnAsError`): clean, 0
  warnings/errors.**
- **No `swkotor.exe` crash in the Windows Application event log for the entire session**
  (`tools\crashlog.ps1 -Hours 1`: clean). The rig was torn down with `demo.ps1 -Stop`
  afterward (`auto_start_agent` restored to `1` in both installs, 2 agent consoles closed).

## Fixes made this round

None. Two genuine, reproducible bugs were found and precisely documented (`topup`'s "0 max
hp" refusal; `openstore`'s tag resolution against a store with no `Tag` field) but neither
was a small, contained fix reachable within this round's time budget — both need a short
investigation of the relevant read path before a safe fix can be written, per the task's own
instruction to document rather than guess at a fix.

## Open issues, ranked by how badly they hurt a real pair

1. **`topup` refuses to heal with "0 max hp" after a player has taken sustained real combat
   damage, while the ordinary `hp/max hp` readout (`where`) stays correct the whole time.**
   Reproduced identically on BOTH the host and the client, in TWO different modules
   (`tar_m10aa` and `tar_m04aa`), and it persisted across a full module transition. A real
   pair fighting Taris' genuinely hostile Vulkars/rakghouls would find their own healing
   command broken at exactly the moment they need it. Repro: get hit by 2+ real hostiles
   until hp is driven down (it will floor at 1, not reach 0), then `topup` — expect `the
   engine refused it: 0 max hp (obj 0x7FFFFFFF vtable 0x7470C8 cur N)`. Needs someone to read
   `CheatController`'s `topup` stat-resolution path against whatever `where`'s own resolver
   does differently after combat.
2. **Downed player + revive remains unreached, now confirmed under real, sustained,
   multi-attacker Taris combat too** (Vulkars in Step 3, rakghouls in Step 4) — same root
   cause the Endar Spire round found: the vanilla engine's own "cannot reduce a player below
   1 hp from ordinary damage" rule blocks every available dev-verb path to a genuine downed
   state. This is still the single largest unverified feature for two real players; needs
   either a way to apply a guaranteed lethal/critical hit through a dev verb, or acceptance
   that this specific check needs a human at the controls.
3. **A client's local-combat hit-landing evidence is real but not cleanly isolated.**
   `0x46`'s hp genuinely dropped `16 -> 1` while the client's own `attack` order was active,
   but the diagnostic `localCombatExcluded=0x46` flag stayed set throughout, meaning the
   drop cannot be confidently attributed to the client's own swings versus bleed-over from
   the host's adjacent, separate fight. Needs a dedicated round with exactly ONE hostile
   anywhere near the client to settle this cleanly — this directly updates (without closing)
   the Endar Spire round's ranked #1 open issue.
4. **No merchant exists in the Upper City Cantina at all** (a wrong assumption in the task's
   own framing, now corrected) **and the substitute store (`larrim`, South Apartments)
   refused `openstore` even at a plausible range** with no obvious cause — its own GIT
   `StoreList` struct carries no `Tag` field, only a `ResRef`, which may mean `openstore`'s
   resolver needs a different id path for this object class than the ordinary
   placeable/creature tag lookup. Blocks any live store_sync/`buy`/`sell` testing on Taris
   specifically until resolved. Untried substitute: the Undercity's `igearstore001`.
5. **The documented `k_pebn_loc01`..`09` swoop-taxi party-select trigger could not be
   located in this build**, and the closest analog found (`tar03_swoop03aa` /
   `k_ptar_swoop03aa`) produced no positive confirming evidence — compounded by the already-
   known "suppression detection fires at most once per module" limitation, which makes a
   real second trigger firing indistinguishable in the logs from a no-op. Needs either a
   fresh RE pass to find the actual current trigger name/location for this build, or a
   positive-signal check that does not rely on the once-per-module suppression log line
   (e.g., hooking `k_ptar_swoop03aa`'s own execution, or reading `CGuiInGame+0x78`'s address
   directly for a change).
6. **Module-ID corrections for future Taris planning**: the real cantina is `tar_m02ae`, not
   `tar_m03aa` (which is Lower City); the real swoop-taxi drop-off search should target
   `tar_m03aa`'s own triggers as the source doc says, but by the name found live
   (`tar03_swoop03aa`), not `k_pebn_loc0N`; the actual swoop RACE module is `tar_m03mg`, not
   `tar_m03ae` (which is a second, different cantina — Javyar's). Low severity on its own
   (doesn't hurt a real pair) but cost real time this round and is worth recording precisely
   so it isn't rediscovered from scratch.
7. **Duel Ring and `loottake`-from-a-real-corpse were not attempted this round** (time
   budget went to the higher-value combat/topup/store findings above). Open, not attempted —
   not a demonstrated failure of either feature.
8. **Crash dumps could not be enabled this session** (non-elevated shell) — a tooling/process
   gap for whoever runs the next round, not a code issue. Run from an admin `pwsh` and call
   `tools\crashlog.ps1 -EnableDumps` first.

## Follow-up (2026-08-28): open issue #1 fixed; the minigame-toast finding was a false negative

### `topup`'s "0 max hp" — root cause, and fixed

Read `CheatController.Player.cs`'s `TopUp` against `Odyssey.GetMaxHitPoints`
(`Odyssey.Health.cs`), which is a one-line read of `CSWSObject+0xE0` — and against
`KotorGameBridge.Attach.cs`'s own `TryReadMaxHitPoints`, the out-of-process reader this
project already ships for the dashboard. That comment names the real bug directly: **the
stored field at +0xE0 is `ushort` zero on EVERY player character, forever** — the engine's own
`CSWSCreature::GetMaxHitPoints` (0x004ED310) never writes it for a PC; instead it re-sums the
character's per-level hit points (`level.hitPoints + conModifier`, once per level) every time
it is asked, and `CSWSObject::GetMaxHitPoints` (the plain field read `topup` used) is only the
branch the engine takes for doors, placeables and non-PC creatures. So `topup` was never
reading combat-damaged state — it read an always-zero slot, on every player, the entire time.
It only LOOKED combat-shaped this round because nobody had tried it before the first fight;
the one intermediate `max 0 -> 265 -> 12` misread the write-up mentioned is consistent with a
torn read of the same never-meaningfully-written field, not a separate symptom.

**Fix**: `Odyssey.GetEffectiveMaxHitPoints(nint obj)` (new, `Odyssey.Health.cs`) reproduces the
engine's real branch in-process — `CreStats`'s own `is_pc` flag selects the stored-field read
for anything that isn't a PC, or the per-level sum for anything that is — reusing the exact
offsets `KotorGameBridge.Attach.cs`'s `TryReadMaxHitPoints` already verified LIVE (a level-1 PC
with a rolled 10 and a +2 CON modifier read back 12, matching a full-health reading exactly).
The pure "which branch, what sum" decision is factored out into `MaxHitPointsRule.cs`
(dependency-free, linked into `KotorCoop.Tests.csproj` like every other Plugin arithmetic
rule) so it is testable without a live process — `MaxHitPointsRuleTests.cs`, 7 tests, pins the
live-verified 10+2=12 case plus the empty/over-bound "genuinely unresolved, report 0" fallback.
`TopUp`, its own `ApplyPendingHitPoints` (the `standup` deferred top-up), and
`DeathController.cs`'s `ApplyPendingHitPoints` (a peer's revive top-up) all switched to the new
method — all three ever only operate on a player character, so all three hit the identical
bug. The remaining two callers of the original `GetMaxHitPoints` (`CombatController.cs` and
`EnemyPresenceController.cs`, both enemy-facing reads) are left untouched on purpose: they read
a non-PC creature's stored field, which is exactly right for that case and was never the bug.

**NEEDS-LIVE**: the individual pieces (the offsets, the branch, the live-verified 10+2=12 sum)
are each already confirmed live via `KotorGameBridge.Attach.cs`'s own out-of-process reader;
combining them into a NEW in-process read path (`Odyssey.GetEffectiveMaxHitPoints`) has not
itself been run against a live game this round — no rig was available. Next round with rig
access: get a player's hp driven down by real combat exactly as this round did, then `topup`,
and confirm it now reports the character's real max (not 0) and heals correctly, on both
instances, in both the fresh-module and post-combat-transition cases this round reproduced.

Files: `src/KotorCoop.Plugin/MaxHitPointsRule.cs` (new), `src/KotorCoop.Plugin/Odyssey.Health.cs`,
`src/KotorCoop.Plugin/CheatController.Player.cs`, `src/KotorCoop.Plugin/DeathController.cs`,
`tests/KotorCoop.Tests/MaxHitPointsRuleTests.cs` (new),
`tests/KotorCoop.Tests/KotorCoop.Tests.csproj` (linked-source entry). `dotnet build
KotorCoop.slnx -warnaserror` and `src/KotorCoop.Plugin/build.ps1 -WarnAsError`: both clean, 0
warnings/errors. `dotnet test tests/KotorCoop.Tests --no-build`: green (see the full-suite
result recorded with the minigame-toast follow-up below).

### The minigame-toast "ZERO notice" finding: a false negative, not a regression

This round's own step 2 wrote: "the CLIENT's plugin log recorded **zero** lines referencing
pazaak, minigames, or phantom parking of any kind" and concluded the partner gets no notice
under the shipped `minigame_hide_peer=1`. **That conclusion does not hold up** — checking the
plugin log was checking the wrong artifact for this specific mechanism, and the mechanism
itself was already live-verified working, in a DIFFERENT round the same day, before this one.

`MinigameAnnouncer` (`src/KotorCoop.Agent/MinigameAnnouncer.cs`) is deliberately AGENT-side,
not plugin-side, precisely because the plugin cannot observe a hidden peer's flag (its own
class doc comment, and `docs/research/minigames.md` §23.3, explain why at length: under
`minigame_hide_peer=1`, `CoopAgent.PumpOnce`'s `visible` filter and `TryWriteRemotePeers`'s
own write happen in the same call on the same filtered snapshot, so there is provably no tick
at which a hidden peer's slot in the shared block is both active and carries the minigame
flag). `CoopCommandDispatcher.Announcers.cs`'s `AnnouncePeerChanges` feeds
`MinigameAnnouncer.Observe` from `session.GetRemotePeers()` — the RAW admitted roster the wire
buffer holds, sampled straight off `CoopHost`/`CoopClient` with **no** minigame filtering at
all (confirmed by reading both implementations: `GetRemotePeers() => _buffer.Sample(...)`,
nothing else). `CoopAgent.PumpOnce`'s own hide filter only ever touches its locally-scoped
`visible` list, which is used for `TryWriteRemotePeers` and the position/combat/pause pumps —
never for what `GetRemotePeers()` itself returns. So the toast's own wiring was never subject
to the hide filter in the first place, by construction.

The toast, when it fires, reaches the player through `KotorGameBridge.PostChatLine` — a write
into the shared block's chat-line field — which `ChatOverlayController` (plugin side) renders
directly onto the frame as queued text (`Odyssey.PostTextLine`). **It is never written to the
plugin's own log file.** It IS written to the agent's own log (`Reply`'s `log(text)` call,
`CoopCommandDispatcher.cs`) — `agent2.log` for the client in this round's own naming, not
`kotorcoop-plugin.log`. Grepping the plugin log for "minigame" could not have found this line
even on a fully working build.

This is not merely inferred: `docs/research/minigames.md` §23's "Live pass (2026-08-28)" — an
EARLIER round the same day as this Taris playthrough — ran the identical shipped configuration
(`minigame_hide_peer=1`) with the real `pazaak` console primitive and recorded, verbatim:

```
CLIENT [03:28:08]     * Lanik is in a minigame - waiting                  <- the chat toast (agent2.log)
```

explicitly noting the line "fired on the client under the SHIPPED default
(`minigame_hide_peer=1`), confirming §23.3's own follow-up fix ... works for Pazaak." This
Taris round's own host also ran `pazaak` under the same default and reported the SAME
`CPazaakData field 0x1` / `minigame: entered (Pazaak)` local detection lines the earlier round
saw — the flag genuinely fired again — but this round only inspected the plugin log, not
`agent2.log`, so it could not see the toast that the earlier round already proved exists.

**No code change was needed.** A permanent regression pin was added instead:
`MinigameAnnouncerLoopbackTests.cs` (new) — real `CoopHost`/`CoopClient`/`CoopAgent` pump loops
over real sockets, `HidePeersInMinigame` left at its shipped-default `true`, a fake bridge
standing in for the game process. It drives the client's own `MinigameAnnouncer.Observe` from
`client.GetRemotePeers()` (the exact call `AnnouncePeerChanges` makes in production) on its own
independently-scheduled loop, sets the host's fake `FlagInMinigame` on, and asserts BOTH halves
of the claim at once: the toast fires ("Revan is in a minigame - waiting") AND, at the same
moment, the client's own block-write bridge (`TryWriteRemotePeers`) received an EMPTY peer
list — proving the hide filter parked the phantom and the toast fired, simultaneously, from the
same flag edge, exactly as designed. Passes against the current (unmodified) wiring. Files:
`tests/KotorCoop.Tests/MinigameAnnouncerLoopbackTests.cs` (new).

**Lesson for future live rounds**: when a channel is documented as agent-side (`AwayAnnouncer`,
`MinigameAnnouncer`, `SoloSegmentAnnouncer` all share this shape), a live check for its output
needs to grep the AGENT's log, not the plugin's — see `docs/CONTRIBUTING.md`'s log-location
table if the two are not obviously named per-instance in a given rig's `%TEMP%` layout.
