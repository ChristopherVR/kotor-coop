# World adoption — a joiner plays the host's world with their own character (2026-08-13)

How a joining player stops needing an identical save and starts being *given* one. Built on
[character-login-chain.md](character-login-chain.md), which reverse-engineered the login fork
this feature turns around and uses deliberately.

Tag key: **VERIFIED** = read from the binary, measured on disk, or covered by a passing test
· **INFERRED** = deduced, not yet run · **NEEDS-LIVE** = must be confirmed on the running
game.

## Headline

Every object-id-keyed channel in this mod — enemies, doors, placeables, container loot,
encounters — rests on both engines having dealt the same object ids, which they do only when
both loaded the same save. `ContentCompatibility.ApplyWorldMismatch` could *detect* that
failing and refuse the join (`RefusalReason.SaveGame`); nothing could *make it true*. A
player who had ever played on their own could not join at all.

Adoption makes it true. The host's save directory is sent to the joiner and staged over the
autosave slot; the joiner's own save contributes exactly one thing — their character, lifted
out as `Temp\pifo.ifo` — and the engine's own `LoadCharacterFromIFO(-1)` path logs them into
a world they did not come from. Afterwards both installs' save-identity digests are equal
**by construction**, so the gate that used to refuse the join passes because the thing it
measures was made true rather than merely checked.

**The trade, stated plainly:** the joiner gives up the world they were playing, not their
character. Party, inventory, companions, globals and journal come from the host's save,
because in co-op those are shared state. Their own save on disk is never written — only the
autosave slot and `Temp\` — so leaving the session and loading their own game costs nothing.
This is the trade Seamless Co-op makes: you bring your character, the host brings the world.

## 1. Why the login fork is the whole mechanism (VERIFIED)

`RunModule` forks the character login on `DoingStartNewModule`
(`serverInternal+0x1007C`):

| flag | path | reads the PC from |
| --- | --- | --- |
| cleared | `LoadPrimaryPlayer` (0x4B5F50) | the loaded module's own `module.ifo` → `Mod_PlayerList` |
| set | `LoadCharacterFromIFO(-1)` (0x561E30) | **`TEMP:pifo`** — a standalone IFO with no tie to the world |

`MenuLoadController` has cleared the flag on every frame of the load window since the
login-chain research, because a manual save carries no pifo and the set path then fails
silently — the `0xF` login branch ignores `LoadCharacterFromIFO`'s return value, login is
confirmed anyway, and `InitiateModuleForPlayer` (0x4B3A90) manufactures a blank
1/1-hit-point creature at the module start point.

For an adopted world that reroute is exactly wrong: the staged world is the *host's*, so its
`module.ifo` holds the *host's* character, and `LoadPrimaryPlayer` would bring the joiner up
in the host's body. Leaving the flag **set** is correct there — and safe, because we supply
the pifo the retail path lacks.

So the feature is not a new mechanism. It is the engine's own autosave-flavour login, given
the file it expects.

## 2. Measured facts that decided the design

### `ObjectId` on a saved PC is `0x7FFFFFFF` (VERIFIED, two real saves)

Read out of `saves\...\SAVEGAME.sav → END_M01AA.sav → module.ifo → Mod_PlayerList[0]`. The
engine does **not** preserve a PC's runtime object id across a save; it re-mints one on load.

This removes the whole id-collision question. A character carried into a foreign world cannot
collide with an object already in it, and the copy is safe precisely *because* it leaves the
field alone — which is what `TheSavedObjectIdIsCarriedUntouched` guards.

### A player entry uses GFF type 17 (VECTOR), which the writer could not emit (VERIFIED)

117 fields, 113 nodes, nesting depth 2, and `FollowInfo` carries `FollowLocation`,
`LastLeaderPos`, `LastFollowerPos` as VECTORs. `GffWriter` threw `NotSupportedException` on
those, so a faithful copy was impossible before this. `SetVector`/`SetOrientation` were added
with the two field-data cases.

### `Mod_IsPrimaryPlr` is the on-disk form of the is-PC mark (VERIFIED)

`GetPrimaryPlayerIndex` (0x4C5D90) walks the packed in-memory list for `[e+0x18] == 1`. On
disk the entry carries both `Mod_IsPrimaryPlr` and `IsPC`; `PlayerIfo.SelectPrimary` prefers
the former, falls back to the latter, then to index 0. Every single-player save measured has
exactly one entry, so this only matters if one ever does not.

### A save is 654 KB without the screenshot (VERIFIED)

`SAVEGAME.sav` 584 KB, `GLOBALVARS.res` 58 KB, `PARTYTABLE.res` 11 KB, `savenfo.res` 673 B,
`Screen.tga` 192 KB. The screenshot is a third of the payload and is GUI decoration for a
slot the mod loads programmatically, so it does not travel.

## 3. ⚠ The autosave pifo trap (VERIFIED mechanism, INFERRED consequence)

`LoadPCAutoSave` exports the staged save to `GAMEINPROGRESS:` and then copies
`<export>\pifo` over `TEMP:pifo` (`CExoResMan::FileCopy` at 0x6CA634).

A **manual** save has no pifo, which is why nothing in the existing load path was ever
affected. A genuine **autosave** does, and it holds the character of whoever made it — so
adopting a host's autosave verbatim would overwrite the joiner's carried character with the
host's, and *both players would come up as the same person*.

`PlayerIfo.TryNeutralisePifo` defuses it with a 16-byte in-place rename of the member's ERF
key cell (`pifo` → `kcoopoldpifo`). Offsets, sizes and every other member are untouched, so
the archive stays valid and the payload is still there under a name nothing looks for.
Rebuilding the ERF to drop the member would be far more work for no gain. Staging calls it
automatically on any `SAVEGAME.sav` it writes.

## 4. Layout

```
joiner                                            host
------                                            ----
PlayerIfo.TryExtractPlayer(own save)   <-- FIRST, before anything is destroyed
        |  SAVEGAME.sav -> <module>.sav -> module.ifo -> Mod_PlayerList
        v
WorldTransfer.FetchAsync  ------- TCP, same port as the session ----->  ServeAsync
        |                                                              (save directory,
        v                                                               minus Screen.tga)
WorldTransfer.WriteSaveDirectory(saves\000001 - AUTOSAVE)
        |  + TryNeutralisePifo on SAVEGAME.sav
        v
PlayerIfo.TryWritePifo(Temp\pifo.ifo)
        |
        v
SaveDigest.TryComputeSaveIdentity(staged) == the host's digest?   <-- the self-check
        |
        v
PluginMenuLoader.TryRequestLoad(adoptedWorld: true)
        |  block+0x3320 = 1
        v
MenuLoadController: leaves DoingStartNewModule SET; block+0x3324 = 2 (carried character)
```

Shared block **version 35**, region `0x3320..0x3330`. `OffsetAdoptWorldLogin` (agent →
plugin) selects the flavour; `OffsetAdoptWorldApplied` (plugin → agent) reports which one the
load actually took, which is the only cheap way to notice a missing pifo before the player
notices they are a level-1 stranger.

## 5. Why TCP, beside the UDP session

A save is three orders of magnitude past a datagram, and it is the one payload in this
protocol that is neither a level nor an event but a bulk object that must arrive exactly
once, complete, before anything else can happen. Reusing the retransmit-until-acked machinery
would mean reinventing ordering, windowing and flow control for a transfer that happens once
per session. The listener binds the **same port number** as the session, so anyone who has
forwarded a port for co-op has already forwarded this.

No compression (a save is mostly incompressible ERF payload and the link is usually a LAN),
no resume (a failed transfer is retried whole, and 654 KB is under a second), no delta (there
is no prior state to delta against — that is the point).

⚠ The receiver is being handed bytes by a remote machine. Two fields could hurt it: the
declared SIZE, which becomes an allocation (bounded at 16 MB per file, 48 MB total, 16 files),
and the file NAME, which becomes a PATH. Names are **refused** rather than sanitised if they
are not already bare — reducing `..\..\swkotor.exe` to its last component would also be safe,
but a name that needed reducing did not come from a save directory, and a sender offering one
has nothing else worth trusting.

## 6. What is verified, and what is not

VERIFIED (19 tests, plus a check against the two real saves on this machine):

* the deep copy is lossless on the real 117-field character — every field, type, nested
  struct id, 113 nodes, and byte-identical across two runs;
* ExoString and ResRef stay apart (both decode to `string`, so a copy driven by CLR type
  would silently rewrite one as the other);
* the pifo is an `IFO ` holding exactly one entry, so `[player+0x80] == 0` indexes it right;
* a characterless save fails **before** the autosave slot is touched;
* the joiner's own save is byte-identical and untouched after an adoption;
* a wrong password, an absent world, a foreign server on the port, a traversing file name, an
  absurd declared size and a truncated stream are all refusals, not crashes or partial worlds;
* **the gate**: two saves that fingerprint differently, and `ApplyWorldMismatch` refusing
  them, become one fingerprint and a full `SessionCapabilities.All` admission after adoption.

## 7. Live pass (2026-08-13) — VERIFIED, and the bug it found

Run against two real GOG installs: `agent serve-world` on install 1, `agent adopt` on
install 2 sitting at the main menu. Both installs' saves were byte-identical, so a marker
file was added to the served save to prove the world genuinely crossed the wire.

Measured, on the successful run:

| | |
|---|---|
| transferred | 654,025 bytes, 5 files (screenshot correctly excluded) |
| staged identity | `0x155107BD`, equal to the host's |
| marker file | arrived in the joiner's autosave slot |
| joiner's own save | byte-identical afterwards (untouched) |
| the character that came up | **hp 12, xp 150, `is_pc` 1, faction 0** — the saved character |
| position | (15.42, 20.12) — the module entry point |
| crash log | clean |

### ⚠ THE BUG: `LoadPCAutoSave` TRUNCATES THE CARRIED PIFO (item 2, answered the hard way)

The first live adoption came up as a **blank 1-hit-point, 0-experience character standing at
the module start point** — the exact failure §1 describes, with the faction-repair canary
firing to match. `Temp\pifo.ifo` was 0 bytes, timestamped inside the load.

The cause: `LoadPCAutoSave` copies `<export>\pifo` over `TEMP:pifo`
(`CExoResMan::FileCopy`, 0x6CA634) and **makes that call even when the staged save contains
no pifo** — which every manual save and every adopted world is. A copy with no source
truncates the destination to zero. So the file the agent wrote before the load was empty by
the time the login read it.

An agent-side re-write loop does NOT fix this, and the attempt is recorded because it looks
like it should: the truncation and the login's read fall inside the same load, and a 100 ms
poll from another process cannot fit between them. Measured — the loop fired twice, the file
was restored to its full 11,918 bytes, and the character was *still* blank.

The fix is in the plugin, at the only place where the gap is addressable: `FirePhase2` copies
a stash (`Temp\kcoop_carried.ifo`, a name the loader's FileCopy does not target) back over
`Temp\pifo.ifo` on the game's own thread, immediately after `LoadPCAutoSave` returns and
before the login that reads it. With that in place the canary went silent and the character
came up whole.

### Item 3, answered: the joiner arrives at the module entry point

`(15.42, 20.12)`, not the saved `(21.01, 21.15)` — because the TEMP path leaves
`[player+0x40] == 0` and `InitiateModuleForPlayer` places them at `ModEntryArea`. This is
correct for someone arriving in another person's world, and it is now observed rather than
assumed. ⚠ Anything judging adoption health on POSITION will call every healthy adoption a
failure; judge on hit points and experience.

## 8. Still open

1. **The party.** The adopted world's `PARTYTABLE.res` and `AVAILNPC0.utc` are the host's, so
   the joiner should come up with the host's companions. Untested, and it interacts with
   `PartyRosterController`, which is the channel that keys on exactly this.
2. **A host mid-module.** Everything here assumes the host's save is a save. Serving a world
   while the host is standing in it means serving a *stale* one — the host would need to save
   first, or the joiner adopts the last save and then converges through the ordinary channels.
   This is the largest open design question, not just an untested path.
3. **Adoption has not been run INTO a live session.** The pass above adopts and loads; it does
   not then connect the two agents and play. The digests agree by construction, so the
   compatibility gate should now admit — but "should" is what §7 was about.
4. **The flavour field reports the branch, not the outcome.** `block+0x3324` says the login
   took the TEMP:pifo path; it said so on the run that produced a blank character too. The
   honest oracle is the PC's own hit points and experience.

## Key addresses

| Address | Symbol / meaning |
| --- | --- |
| `0x1007C` | `CServerExoAppInternal` DoingStartNewModule — selects the login flavour |
| `0x4AF090` | `SetDoingStartNewModule(int)` — the reroute the adopted path SKIPS |
| `0x4B6270` | `RunModule` — reads the flag at the mode 1→2 transition |
| `0x561E30` | `LoadCharacterFromIFO(index, ...)` — `-1` reads `TEMP:pifo`; `TEMP:` registered at 0x561EAE |
| `0x4B5F50` | `LoadPrimaryPlayer` — the saved-character path, wrong for an adopted world |
| `0x4B3A90` | `InitiateModuleForPlayer` — start-point placement when `[player+0x40] == 0` |
| `0x6CA250` | `LoadPCAutoSave` — export 0x6CA50C, **pifo FileCopy 0x6CA634**, Temp create 0x6CA542 |
| `0x4C5D90` | `GetPrimaryPlayerIndex` — the is-PC mark, `Mod_IsPrimaryPlr` on disk |
| `0x7DE` | restype 2014 "ifo" — `TEMP:pifo` ⇒ `Temp\pifo.ifo` |

---

# The joiner who has never played: `join_mode = newcharacter` (2026-08-29)

## The hole this fills

Everything above assumes the joiner HAS a save. `PlayerIfo.TryExtractPlayer` opens their
`SAVEGAME.sav`, walks to `module.ifo` → `Mod_PlayerList`, and carries that entry. A player who
has just installed the game and wants to join a friend has none of that, so
`WorldAdoption.TryAdoptAsync` returns null at its first step with "the save's `Mod_PlayerList`
is empty (no character to carry)" — correct, and deliberately before anything is written, but
it left exactly one piece of advice: *go play single-player for a few minutes first*. That is a
strange thing to require of someone whose whole intent is co-op.

## The observation

> ⚠ **THIS SECTION'S PREMISE IS FALSE, AND THE ROUND BELOW ("The capture point, answered")
> replaces it.** Retail chargen does NOT write `TEMP:pifo`. `SaveCharGenCreature 0x6123E0`
> does not reference the string at all and its own tail writes `Temp	emp.bic`. It is kept
> here, unedited, because the whole live round that followed was spent measuring the
> consequence, and a reader who only sees the fix would not know which of these paragraphs
> to distrust elsewhere.

**Retail chargen already writes the file adoption carries.** §2 of
[character-login-chain.md](character-login-chain.md) enumerates the writers of `TEMP:pifo`
exhaustively, by string xref, and the first of the three is
`CSWCCreature::SaveCharGenCreature 0x6123E0` — new game. Its single call site is `0x006DBE9B`
(`tools/xrefs.py 0x6123E0`: one direct call, zero address literals), inside
`CSWGuiClassSelection::BeginPlayGame 0x006DBDF0` — the same function
[network-topology.md](network-topology.md) already names for its
`StartConnectToSession 0x005D5040` call at `0x006DBEDE`.

So the file a first-time joiner needs is one the engine writes for them, in the ordinary course
of starting a game, in exactly the shape `PlayerIfo.BuildPifo` hand-builds: an `IFO ` GFF whose
root carries a single-element `Mod_PlayerList`. Nothing had to be created; something had to be
*noticed*.

`CSWGuiClassSelection::BeginPlayGame`, disassembled (`tools/disasm.py 0x6DBDF0 0x180`), in
order:

| Address | Call | Note |
| --- | --- | --- |
| `0x006DBE93` | `CreateServer(appManager, 0)` — `0x00401380` | the same call `MenuLoadController.FirePhase1` makes |
| `0x006DBE9B` | **`SaveCharGenCreature([classSel+0x68])`** — `0x6123E0` | ⚠ writes `Temp	emp.bic`, NOT `Temp\pifo.ifo` — see the correction below |
| `0x006DBEDE` | `StartConnectToSession` — `0x005D5040` | the loopback connect, again as FirePhase1 |
| `0x006DBF07` | `RequestStatus` — `0x006775E0` | |
| `0x006DBF0C`+ | module name built from `[classSel+0x1558]`/`+0x155C` | the Endar Spire start |

## The shape

```
client                                              host
------                                              ----
agent: --join, coop.ini join_mode = newcharacter
   |  connect (admitted, degraded - no world yet)   CoopHost + WorldTransfer.ServeAsync
   v                                                (auto_adopt_world, ON by default)
bridge.TryArmNewCharacterCapture  ->  block 0x4780
   |
   v
PLAYER clicks New Game and creates a character (retail, untouched)
   |   SaveCharGenCreature -> Temp\pifo.ifo
   v
NewCharacterCaptureController (MenuFrame, 250 ms poll)
   |   fresh? >= 256 bytes? stable across two polls?
   v
   File.Copy -> Temp\kcoop_carried.ifo ; block 0x4788 = 2, 0x478C = bytes
   |
   v
agent: PlayerIfo.TryExtractPlayerFromPifoBytes(stash)
   |
   v
WorldAdoption.TryAdoptWithCharacterAsync  ----- the SAME fetch/stage/verify as above ----->
   |
   v
bridge.TryRequestAdoptedWorldLoad(inSession: TRUE)
   |
   v
manager.Reconnect()   -> the digests now agree BY CONSTRUCTION; SessionCapabilities.All
```

Shared block region `0x4780..0x47A0`: `OffsetJoinChargenRequest` (agent → plugin serial),
`OffsetJoinChargenApplied`, `OffsetJoinChargenState` (0 idle / 1 armed / 2 captured / 3 failed),
`OffsetJoinChargenBytes`.

## Three decisions worth defending

**1. A file poll, not a hook on `0x6123E0`.** The function has exactly one call site, so a
detour there would fire exactly once per chargen — a clean capture point, and tempting. It was
not taken: a poll cannot crash the game, and the only thing a hook buys is a quarter of a
second on a step the player has just spent several minutes on. The plugin does the copy (rather
than the agent, which could poll the same file from outside) for one reason — the DESTINATION.
`Temp\kcoop_carried.ifo` is the name §7's `LoadPCAutoSave` `FileCopy` does not target, and
`MenuLoadController.RestoreCarriedCharacter` already owns putting it back on the game's own
thread. One owner for the whole carried-character lifetime beats two racing ones.

**2. The three capture conditions, each a silent failure.** The engine's `0xF` login branch
ignores `LoadCharacterFromIFO`'s return value (character-login-chain.md §1), so a bad pifo does
not error — it produces a blank level-1 stranger at the module entry point, which is what §7
spent a live round diagnosing. Hence: *newer than the arm* (a stale `Temp\pifo.ifo` survives
across runs on any install that has ever loaded an autosave — capturing it would silently carry
a previous session's character); *at least 256 bytes* (a real one measured 11,918; zero is the
wreckage a source-less `FileCopy` leaves); *stable across two polls* (the engine writes from its
own thread; a length read mid-write is a truncated character). All three are pinned by
`NewCharacterCaptureRuleTests`.

**3. `inSession: true`.** Retail chargen finishes by LOADING A MODULE — the player is standing
in the Endar Spire by the time their character exists — so the adoption that follows is
mid-session by construction. A main-menu-only request would be acked and mooted the instant
`MenuLoadController` saw a server (its `OnFrame`'s "server already exists" branch), and the
player would sit in single-player believing they had joined. This is the first thing to
exercise `FireMidSessionPhase1`, which was written from static analysis only (see
[session-save.md](session-save.md)); it is therefore also the riskiest part of this path, and
the first suspect if a `newcharacter` join lands in the wrong world.

Aborting the Endar Spire load instead — so there is only one load, not two — was considered and
rejected for this pass. The module name is built and consumed inside `BeginPlayGame` after the
pifo is written, with no reachable seam between them, and forcing a different module name there
would load a *pristine* module from the game data rather than the host's stored world, which is
the one thing adoption exists to avoid.

## ⚠ Deliberately NOT done: pressing "New Game" for the player

The player clicks it themselves. `CSWGuiMainMenu::OnNewGamePicked 0x0067AFB0` is a thiscall
with ONE stack argument, and — unlike `CSWGuiPartySelection::ConfirmPartySelection 0x006BEC90`,
the precedent for calling a panel's own handler with a null firing control — it DEREFERENCES
that argument: `mov ecx, [esp+0x3C]` / `mov eax, [ecx+0x4C]` / `test eax,eax` at
`0x0067AFEC..0x0067AFF5`. Passing 0 faults. The offset of `BTN_NEWGAME` inside
`CSWGuiMainMenu` is not researched (only `BTN_WARP`'s `menu+0x1248` is).

Replaying the handler's BODY is entirely feasible and is the natural follow-up: it is only
`operator new(0x1560)` (`0x0067B0F7`) → `CSWGuiClassSelection::Constructor 0x006DC3C0`
(`0x0067B11C`) → `AddPanel 0x0040BC70` (`0x0067B130`) → the same `[menu+0x44] = (&~0x300)|0x400`
teardown mark `FirePhase1` already writes (`0x0067B147..0x0067B158`). What stopped it this pass
is the constructor's second argument: a `CExoString` built through a resource-directory dance at
`0x0067B01B..0x0067B0F2` (`AddResourceDirectory 0x408800`, two `ResourceExists 0x408BC0` probes
against restype `0x7DB`, literals `0x752F58` and `0x73D90C`) whose meaning is not pinned.
Guessing it is the exact class of mistake the engine-write rules exist to prevent. One click by
the player is a cheap price for not guessing.

Related, and useful if that follow-up is picked up: `CSWGuiMainCharGen::BeginPlayGame 0x6EB320`
IS argument-free (`RemoveSubPanels 0x6EA830`, then `mov ecx,[this+0x68]` and a tail
`jmp 0x6DBDF0`), so "finish chargen with whatever is on screen" is one guarded, vtable-checked
call (`CSWGuiMainCharGen` vtable `0x7592A8`, size `0x22DC`) away — the automation primitive a
fully unattended live test of this path would need.

## Config

| key | default | meaning |
| --- | --- | --- |
| `join_mode` | `auto` (since 2026-09-03; was `save`) | `auto` = the mod owns the co-op character: the first join goes through retail chargen and the result is written to the `saves\kotorcoop-character.*` sidecar at once; every later join adopts the host's world carrying the sidecar, unconditionally. `newcharacter` = retail chargen on this join even when a sidecar exists (replaces it). `save` = the pre-2026-09-03 behaviour: carry this install's own most recent save's character. |
| `auto_adopt_world` | ON | a joiner whose world does not match the host's adopts instead of playing degraded. Also what makes a HOST serve its world by default — a joiner with no save has no other way to obtain one. With it OFF, `join_mode = auto` degrades to the `save` decision (adoption is the only carrier). |

### `join_mode = auto` — the mod-owned co-op character (2026-09-03)

The user's requirement: "even if we fetch the host world we always need to go through character
creation mode, and once we have a character it's used going forward". Two things were wrong
with the previous default: `save` carried the PC of whatever single-player save was newest on
the install (a co-op session could silently start with someone's Dark Side solo run), and the
sidecar was only ever written on an own in-game save or a clean disconnect, so a joiner who
crashed out before their first save had no co-op character at all.

What changed (all in `JoinFlow.cs`, `JoinModeRule.cs`, `SessionManager.RejoinAfterLoadAsync`):

- `JoinModeRule.Decide` is now the production decision (`JoinFlow.Decide`), not just a tested
  one. It takes `hasCoopCharacter` (the sidecar reads) and, in `auto`, ignores `hasOwnCharacter`
  and `saveIdentityMatchesHost` entirely: sidecar → `AdoptWithCoopCharacter`, none →
  `CreateCharacterThenAdopt`.
- The first-time flow presses New Game itself through `CharGenDriveController`'s `newgame`
  (the console verb's replay of `OnNewGamePicked`'s body; the old "the player clicks it" note in
  the flow's header was stale since that verb shipped). A refused press falls back to telling the
  player to click.
- The sidecar is written immediately after the chargen quicksave is captured (host identity 0)
  and rewritten with the real identity once adoption succeeds.
- The every-later-join path reuses `SessionManager.AdoptWithNewCharacterAsync` with the sidecar's
  player handed in. It runs from the MAIN MENU although the request carries `inSession: true`:
  `MenuLoadController` takes the main-menu route whenever `Odyssey.HasServer` is false, whatever
  the flag says (the flag only matters when a server exists).
- Mid-session rejoins (`TriggerRejoin` → `RejoinAfterLoadAsync`) pass `carriedIsAuthoritative`
  in `auto`, which adopts through `TryAdoptWithCharacterAsync` and never resolves an own save —
  a fresh install that joined through chargen may not have one, and a newer single-player save
  must not outrank the co-op character on age (`ShouldPreferSidecar` still governs `save`).
- The CLI `--join` starts the flow only for the two adoption actions and only if the game is
  still at the main menu once the handshake lands (`!bridge.TryReadLocalState`), so
  demo3.ps1/session.ps1, which load a save before the agent joins, are untouched. demo.ps1
  writes `join_mode = save` into the client install explicitly; `-ClientAutoCharacter` writes
  `auto` and leaves instance 2 at the main menu (delete the client's sidecar first for the
  first-join leg, keep it for the later-join leg), `-ClientNewCharacter` writes `newcharacter`.

**VERIFIED 2026-09-03** (`demo.ps1 -ClientAutoCharacter`, chargen driven with `blockpoke
chargen quick 0` / `accept`), both legs:

- *First join, no sidecar.* `newchar: armed` 20:46:38 → `chargen: newgame opened character
  creation` 20:46:39 (the flow's own New Game press) → chargen accepted 20:47:1x → the plugin's
  ~2 min settle → `newchar: the engine saved the new character into saves\000000 - QUICKSAVE
  (570103 bytes)` 20:49:19.760 → sidecar written (10054 bytes, host identity rewritten to
  0x155107BD after adoption) → `world adoption: adopted 'END_M01AA' (654,006 bytes, identity
  0x155107BD), carrying the character from the character you just created` → reconnected
  20:49:24, `memory ok module END_M01AA hp 6/6`, no `W_SAVE`. Chargen accept → in-world: 2 min 8 s.
  The client's PC model was its own (`pmbas01`), the host's its own (Yasuko Kaar).
- *Every later join, sidecar present.* Three runs (20:51, 21:14, 21:18): `join: carrying this
  install's co-op character (saves\kotorcoop-character.pifo) into the host's world` → adopted
  `carrying the character from your co-op character (captured 2026-09-03 10:49:20Z)` → `co-op
  character: you are in 'END_M01AA' with your co-op character ...; reconnecting to the host.`
  No chargen. Connect → in-world: 5 s.

The first pass also caught a real bug on the way out — the disconnect-time character capture
lifting the HOST's PC out of the staged autosave into the sidecar. Fixed the same day
(`StagedSave`, the `saves\kotorcoop-staged.json` marker written right after
`WorldTransfer.WriteSaveDirectory`, plus the autosave slot's pifo content as the marker-less
fallback); the account and the three live checks are in `docs/status-engineering.md` ("The
v0.9.0 sidecar bug") and `session-save.md`'s live-trigger paragraph. The chargen leg itself is
the 2026-08-29 round below, unchanged.

`auto_adopt_world` took `RejoinTrigger.SaveMismatch` over from `client_load_rejoin`. They were
answering different questions that happened to share an answer: `client_load_rejoin` is "when I
load a save mid-session, put me back in the host's world"; this is "when my world simply is not
the host's, fix it". A joiner who had turned the first off — because they never load
mid-session — was silently also refusing the second, the only mechanism that could ever have
admitted them to the object-id-keyed channels at all. Both default ON, so an install that has
opted out of neither sees no change.

## Key addresses (this section)

| Address | Symbol / meaning |
| --- | --- |
| `0x6123E0` | `CSWCCreature::SaveCharGenCreature` — ⚠ writes **`TEMP:temp` restype `0x7DF` ("bic")** through `CResGFF::WriteGFFFile` at `0x613434`, NOT `TEMP:pifo`; **one** call site, `0x006DBE9B` |
| `0x6DBDF0` | `CSWGuiClassSelection::BeginPlayGame` — the whole new-game start; CreateServer `0x6DBE93`, pifo `0x6DBE9B`, connect `0x6DBEDE`, RequestStatus `0x6DBF07`, module name `0x6DBF0C`+ |
| `0x6EB320` | `CSWGuiMainCharGen::BeginPlayGame` — `RemoveSubPanels` then tail-jmp to `0x6DBDF0` with `this = [chargen+0x68]`; **no stack args** |
| `0x67AFB0` | `CSWGuiMainMenu::OnNewGamePicked` — ⚠ dereferences its firing-control argument at `0x67AFF0`; do not pass 0 |
| `0x6DC3C0` | `CSWGuiClassSelection::Constructor(guiManager, CExoString*)` |
| `0x7592A8` | `CSWGuiMainCharGen` vtable (size `0x22DC`) |
| `0x758020` | `CSWGuiClassSelection` vtable (size `0x1560`) |

## Live pass (2026-08-29) — the drive verbs work; the CAPTURE POINT is wrong

Run on the rig: host on demo save 1 in END_M01AA, client launched with
`demo.ps1 -ClientNewCharacter` (no save loaded, `join_mode = newcharacter`), everything driven
through `blockpoke chargen …` — no mouse input at all.

**What is VERIFIED working:**

| | |
|---|---|
| `agent run --join` with `join_mode = newcharacter` at the main menu | runs (it used to exit 2) |
| the arm handshake, agent → block → plugin | `newchar: armed (request 1)` |
| `CharGenSaveHook` byte check + install | `chargen-save watch at 0x6DBE9B`, no crash |
| `DialogueSkipHook` byte check + install | `dialogue skip authority at 0x6A7278`, no crash |
| `newgame` | class-selection screen up and rendering — **screenshot-confirmed** |
| `chargen quick 2` | class picked, optimised template applied |
| `chargen accept` | `BeginPlayGame` runs, the new game starts |
| the hook firing at the right instant | `newchar: chargen saved a character (hook)` immediately after `BeginPlayGame` |

**Two things the live run corrected, both of which had looked right on paper:**

* **`AddPanel`'s layer argument is 2, not 0.** Retail's call at `0x67B130` is
  `push 1; push ebx; push eax` with EBX still holding the 2 set at `0x67B02E` — the same slot
  `OnSelectButton` passes **3** in for the chargen panel (`0x6DBA4D`). Added at 0 the panel is
  constructed and registered and then never driven: the screen stayed black and all six class
  controls read zero. It looked exactly like a bad constructor argument.
* **`OnSelectButton`'s argument is an EVENT, not a control.** It reads `[arg+0x48]` as an event
  id and `[arg+0x4C]` as that event's DATA, bailing silently on a zero — and only then derives
  the class index from the pointer's own address. Those fields are filled by the GUI manager
  while it dispatches a real click, so a call without them is a guaranteed no-op. The driver
  now writes `[choice+0x4C] = 1`, calls, and puts the old value back.

### ⚠ STILL OPEN, and this is the one thing that blocks the feature

**`SaveCharGenCreature` has not flushed the file when it returns.** Measured, twice:
`Temp\pifo.ifo` EXISTS and is **0 bytes** at the hook, and is still 0 bytes 400 ms later with
the hook spinning on it; by the time the Endar Spire is up the whole `Temp\` directory is
empty. The write goes into the resource manager's `TEMP:` cache and materialises — if at all —
somewhere inside the module load, in a window where the main loop is gone for over a second
(`cinematic: the main thread was gone for 1265 ms`), and is swept before any frame callback
sees it. So neither a poll nor a hook at the write site can have the bytes.

The hook is still the right instrument and stays: it is a precise, verified "chargen finished"
signal, and it is what turned this from a guess into a measurement. What has to change is
WHERE the character is taken from.

**Recommended next step, and it needs no new RE:** let the new game run into the Endar Spire —
it already does — and take the character the way `join_mode = save` already does, from a save.
Once the client is in the world it has a real PC, so the flow becomes: chargen → the game's own
first module → the agent saves once → `PlayerIfo.TryExtractPlayer` on that save → the existing
adoption, `inSession: true`. Every step of that is machinery this document has already
verified; it costs the player the first few seconds of the Endar Spire and removes the pifo
timing problem completely. The alternatives (finding the engine's flush point, or hand-building
the pifo from the live `CSWCCreature` at `[mainCharGen+0x64]`) are both more work and one of
them is explicitly rejected by character-login-chain.md §5.

## Still open (as of that round — see "Still open after this round" at the foot for the current list)

1. ~~**The capture point** — see the live pass above. This is the blocking item.~~ **CLOSED**:
   there was nothing to capture; see "The capture point, answered" below.
2. ~~**`FireMidSessionPhase1` remains the weak link** — see decision 3. It had never run against
   the game before this path used it.~~ **CLOSED**: it has now run repeatedly, and it was indeed
   where both of the round-2 bugs lived (the message-box gate and the `Temp\` wipe).
2. **The party.** Unchanged from §8 item 1, and now sharper: a brand-new character adopting a
   mid-game host's world inherits the host's `PARTYTABLE.res`/`AVAILNPC0.utc`, i.e. companions a
   level-1 character has never met. The roster is host-authoritative anyway
   (`party_select_host_only`), so this should be cosmetic rather than broken, but it is
   untested.
3. **Arrival position.** Same as §7 item 3: `[player+0x40] == 0` on the TEMP path, so the joiner
   lands at `ModEntryArea`, not next to the host. The presence/teleport machinery closes the gap
   afterwards; judging health on POSITION will still call every healthy adoption a failure.
4. **Pressing New Game** — see the section above.

---

# The capture point, answered: chargen writes a BIC, not a pifo (2026-08-29, round 2)

## The correction

The previous round's blocking finding — `SaveCharGenCreature` "has not flushed the file when it
returns", `Temp\pifo.ifo` 0 bytes at the hook and still 0 after a 400 ms spin — was measured
correctly and diagnosed wrongly. There was no flush to wait for. **`SaveCharGenCreature` never
writes a pifo at all.**

Two independent reads say so, and they agree:

* **String xref.** `tools/xrefs.py 0x745318` (the `"pifo"` literal) lists five address-literal
  occurrences: `0x004B2D61` (`StorePlayerCharacters`), `0x004B908D` (`DoPCAutosave`),
  `0x00561EC7`/`0x00561F1F` (`LoadCharacterFromIFO`), `0x006CA558` (`LoadPCAutoSave`).
  `"TEMP:pifo"` (`0x74554C`) adds `0x004B90CA` and `0x006CA605`, in the same two functions.
  **Not one of them is inside `0x6123E0..0x6134B9`.** So character-login-chain.md §2's writer
  (a) is wrong, and it was wrong in the one way a string-xref list cannot self-correct: the
  function was attributed to the *feature* ("chargen") rather than to a hit on the literal.
* **The function's own tail.** `SaveCharGenCreature` builds a GFF whose header is `"BIC "` /
  `"V2.0"` (`0x745384` / `0x745338`, pushed at `0x0061245D` / `0x00612445`), fills it with
  `FirstName`, `LastName`, `Description`, `Gold`, appearance, class list and so on, and ends at
  `0x00613434` with `CResGFF::WriteGFFFile(<name>, 0x7DF)` where the name is
  `"TEMP:" + "temp"` (`0x745310` + `0x74F4F4`) and `0x7DF` = 2015 = **"bic"**. It writes
  `Temp\temp.bic` — a character file — not a module IFO with a `Mod_PlayerList`.

The file the previous round watched, `Temp\pifo.ifo`, reads as 0 bytes because
`LoadCharacterFromIFO(-1)`'s own missing-resource branch (`0x561EF8`) fabricates an empty one,
and empty is exactly what an unrelated leftover looks like. Every symptom that round recorded is
consistent with a file nobody was writing.

⚠ **Generalisation worth carrying**: a writer list that is "exhaustive by string xref" is only
exhaustive for the *string*. Attributing an entry to a feature is a second, unchecked step, and
this is the second time in this document that step has cost a live round (§7's `LoadPCAutoSave`
truncation was the first). Re-derive the attribution from the disassembly before building on it.

## What replaces it

Exactly the recommendation the previous round's own "next step" named, and it needed no new RE
beyond one call:

```
client                                                  host
------                                                  ----
agent: --join, coop.ini join_mode = newcharacter
   |  connect (admitted, degraded - no world yet)       CoopHost + WorldTransfer.ServeAsync
   v                                                    (auto_adopt_world, ON by default)
bridge.TryArmNewCharacterCapture  ->  block 0x4780      state = 1 (armed)
   |
   v
PLAYER creates a character (retail screens, untouched); the new game loads the Endar Spire
   |
   v
NewCharacterCaptureController (MenuFrame, 250 ms)
   |   Odyssey.HasPlayer, then a 4 s settle
   v
   Odyssey.RequestQuickSave()  ->  DoQuickSave 0x005F4B50      state = 4 (saving)
   |   retried up to 5x, 8 s apart: GetIsAllowedToSave is a real gate
   v
   watch saves\ for a SAVEGAME.sav newer than world-up, >= 256 bytes, stable across two polls
   |
   v
   block 0x47C0 = the directory NAME ; 0x478C = its bytes ; state = 2 (captured)
   |
   v
agent: PlayerIfo.TryExtractPlayer(saves\<name>)   <- the SAME extraction join_mode = save uses
   |
   v
WorldAdoption.TryAdoptWithCharacterAsync  ----- unchanged fetch/stage/verify ----->
   |
   v
bridge.TryRequestAdoptedWorldLoad(inSession: TRUE) ; ForgetWorldShadows() ; manager.Reconnect()
```

The player still does nothing but character creation. The Endar Spire intro is cut short by the
adoption load, which is a full `DestroyServer` / `UnloadModule` / `CreateServer` teardown
(`FireMidSessionPhase1`) — the same teardown a real mid-session Load Game performs, so no
dialogue state can survive it.

### The save primitive

`CClientExoAppInternal::DoQuickSave 0x005F4B50` is the whole retail quicksave and the smallest
save entry point in the binary. Disassembled: it takes **no arguments and uses no `this`** (the
first thing it does with `ecx` is overwrite it, `lea ecx,[esp+0xc]` at `0x5F4B6D`; it ends in a
bare `ret`), so it binds as a plain Cdecl no-arg call. Its body is two
`CExoString("QUICKSAVE")` (literal `0x74E9E0`) and
`CClientExoApp::SendSaveGameRequest(client, 0, &name, &description)` at `0x5ED670` — the
ordinary client→server save message, handled in `HandlePlayerToServerModuleMessage` behind
`CSWSPlayer::GetIsAllowedToSave` (`[player+0x3C] != 0`, `0x560CC0`) and landing in
`CServerExoAppInternal::SaveGame 0x4B58A0`, whose directory is
`sprintf("SAVES:%06d - %s", number, name)` (format `0x7454D4`). Slot number 0, name
`QUICKSAVE` — and it does not collide with `PluginMenuLoader.AutosaveSlot`
(`000001 - AUTOSAVE`), the slot the adoption then stages over.

Also exposed as the `save` console verb (docs/CONSOLE.md), which several previous rounds have
wanted for its own sake.

### Why the plugin names the directory rather than the agent guessing

Both processes can see `saves\`, and "the newest directory" would usually be right. The plugin
is the side that knows *when* the save was asked for and *when this world came up*, so it is the
side that can distinguish a directory this session produced from one that was already on disk —
and two processes each independently picking "the newest" is two chances to disagree at the one
moment it matters. Block field `OffsetJoinChargenSaveDir 0x47C0`, 32 bytes of NUL-padded ASCII,
written BEFORE the state goes to `captured` (payload first, serial last, as everywhere else
here).

### Three guards, each against a silent failure

1. **The freshness cutoff is WORLD-UP, not arm.** An install has old saves; carrying a character
   out of one would join as somebody the player did not create, with no symptom. Keying on
   world-up also excludes a save made during a chargen that was cancelled and restarted.
2. **The save is retried, and then given up on.** `GetIsAllowedToSave` is a real gate and a
   refused save answers with a status message nothing in this plugin listens to, so "no
   directory appeared" is the only signal there is. Five requests, 8 s apart, then a failure the
   player can read — rather than an arm that waits forever.
3. **Bytes, not just state.** `NewCharacterCaptureRule`'s three conditions (fresh / at least 256
   bytes / stable across two polls) carried over unchanged, because they were never about the
   pifo specifically — they are about reading a file another thread is still writing, and about
   a file that was already there before this join started.

`CharGenSaveHook` is kept and demoted: five bytes on a call that happens exactly once per
character creation, now only a counter, so a stalled join's log can say whether the player has
finished chargen or the module has not come up.

### New key addresses (round 2)

| Address | Symbol / meaning |
| --- | --- |
| `0x5F4B50` | `CClientExoAppInternal::DoQuickSave` — **no args, no `this`**, bare `ret`; `"QUICKSAVE"` literal `0x74E9E0`; `SendSaveGameRequest` at `0x5F4BA5` |
| `0x5ED670` | `CClientExoApp::SendSaveGameRequest(int number, CExoString* name, CExoString* description)` |
| `0x4B58A0` | `CServerExoAppInternal::SaveGame` — directory `sprintf("SAVES:%06d - %s", …)`, format literal `0x7454D4` |
| `0x560CC0` | `CSWSPlayer::GetIsAllowedToSave` — literally `[player+0x3C] != 0`; checked at `0x524992`/`0x524A7A` in the save message handler |
| `0x613434` | `CResGFF::WriteGFFFile("TEMP:temp", 0x7DF)` — the END of `SaveCharGenCreature`; `0x7DF` = "bic" |
| `0x6A7230` | `CSWGuiDialog::HandleInputEvent` — **vtable slot 0x3C in BOTH dialogue panel vtables** (`0x7559E0`, `0x755800`); `ret 8`, arg1 = event code, arg2 = a nonzero payload |

## LIVE PASS (2026-08-29, round 2): `join_mode = newcharacter` works END TO END

Rig: host `C:\GOG Games\Star Wars KOTOR (GOG)` on demo save 1 in `END_M01AA` serving its world,
client `D:\Games\swkotor-gog-p2` launched with `demo.ps1 -ClientNewCharacter` — no save loaded,
sitting at the main menu. Character creation driven entirely through
`blockpoke chargen newgame / quick 2 / accept`; no mouse input, no keyboard, nothing typed in
the game.

### The whole run, with timings

| t | what | evidence |
|---|---|---|
| 13:55:25.890 | agent arms the capture | `newchar: armed (request 1) … chargen-save watch at 0x6DBE9B` |
| 13:55:49.293 | `chargen accept` | drive status 0 |
| 13:55:51.648 | the new game's first module is up | `newchar: the new game's first module is up with a player creature; saving in 4000 ms` |
| 13:57:53.578 | the engine is asked to save | `newchar: asked the engine to save (request 1/5)` — **first request, no retry needed** |
| 13:57:54.070 | the save is on disk | `saves\000000 - QUICKSAVE (566770 bytes of SAVEGAME.sav)` — **492 ms after the request** |
| 13:57:55 | the host's world is adopted | `world adoption: adopted 'END_M01AA' (654,006 bytes, identity 0x155107BD), carrying the character from the character you just created` |
| 13:57:56.623 | mid-session load fires | `mid-session load precondition [msgbox+0x64] = 0x06 … observed, not obeyed`, then the teardown |
| 13:57:57.671 | the carried character is restored | `restored the carried character over TEMP:pifo (10110 bytes, from swkotor-gog-p2\kcoop_carried.ifo)` |
| 13:57:59.580 | the world is up and on screen | no faction-repair line, i.e. a real PC, not the blank one |
| 13:57:59 | the session reopens | `* reconnecting...`, and **no `W_SAVE` degraded warning** afterwards |

**Wall clock from `chargen accept` to standing in the host's world: 2 minutes 10 seconds**, of
which ~2 minutes is the settle before the save (see "the settle is not 4 seconds" below).

### The character is the player's own — measured three ways

`chargen quick 2` produced **Appearance_Type 171, Gender 0, PortraitId 24, MaxHitPoints 12**
(read out of the client's own `000000 - QUICKSAVE` → `END_M01AA.sav` → `module.ifo` →
`Mod_PlayerList`). The host's world carries **"Yasuko Kaar", Appearance_Type 114, Gender 1,
PortraitId 15**.

| | |
|---|---|
| client's live PC (`creature+0xA60`) | **171**, hp 12 — its own |
| host's live PC | **114**, hp 12 |
| host's phantom of the client | `appearanceRow=171` (from the host's own gait log) |

So the joiner is neither the host nor a blank stranger, which is the failure §7 spent a round
diagnosing. Position `(15.4, 20.1, -1.3)` — the module entry point, exactly as §7 item 3 says an
adopted TEMP-path login lands, with the host at `(20.0, 22.2)`.

### Convergence, on that same joined session

`tools/verify.ps1`: **41 passed, 8 failed**, squarely inside this repository's documented
baseline (~40-42 / 7-9) and with every failure in the long-known flaky set (the enemy-hit pair,
the ground-item vanish pair, the death pair, the signature check, per-frame spam, the container
walk). What matters here is what PASSED, on a client that had never played this game:

* `enemy presence mirrored on the client` — presence 25, census 25
* `census: MIRROR 25 id(s) 'END_M01AA': 25 resolve here, 0 do not` — **every one of the host's
  object ids resolves on the joiner**, which is the whole point of adoption
* `a door opened on the client opens on the host` — door `0x46`, host reads open-state 2
* `the host still controls its companions` — `partystate`: both sides Trask `0x127` in slot 0,
  client `soloMode 1` (host-authoritative, as designed)
* party gold, PC experience, journal add/decrease/remove all reach the other instance
* `no swkotor crash in the event log — clean`

### Then: quit the client and rejoin with `join_mode = save`

The client's `000000 - QUICKSAVE` is a real save of its own, so the next session needs nothing
special. Relaunched with a plain `demo.ps1`; the client loaded its own new save (slot 1 is that
quicksave), and at 14:14:32 the ordinary path took over by itself:

```
* co-op [W_SAVE] different save: enemies, doors, items and party are off - rejoin …
* your world still differs from the host's - rejoining automatically
* session-save: starting rejoin (SaveMismatch)
* world adoption: carried the character from '000000 - QUICKSAVE' (117 fields, module 'END_M01AA')
* world adoption: adopted 'END_M01AA' (654,006 bytes, identity 0x155107BD) …
* rejoined 'END_M01AA'
```

Live PCs afterwards: client **171**, host **114**. One second, no intervention. §8's advice
("save once and switch back to `save`") is now something the mod does for the player rather than
something they have to be told.

### Two things this round had to fix on the rig, both of them the mid-session path

**1. The message-box precondition refused every mid-session load.** `FireMidSessionPhase1`
faithfully replicated retail's `[gui+0x98]+0x64` bit-0 gate and returned failure 9 when it was
clear, which it always is: measured directly, on BOTH instances at once, `[msgbox+0x64]` reads
`0x06` — bit 0 clear — on a client mid-conversation AND on a host twenty minutes into ordinary
play with nothing modal on screen. It marks the pause-menu ROUTE into `LoadGame`, not "safe to
load", and our load never comes through that route. Four earlier live rounds
(docs/research/session-save.md) had to poke the bit by hand to get a load at all. The bit is now
LOGGED and not obeyed; a NULL sub-object is still a refusal. This also removes that documented
rig trap from `loadgame`.

**2. ⚠ A MID-SESSION ADOPTION EMPTIES `Temp\`, so the carried-character stash cannot live
there.** First fixed run: `restored … TEMP:pifo` never fired,
`adopting a world but kcoop_carried.ifo is missing; the login will produce a blank character`
did, and the faction canary fired to match. On disk afterwards, `Temp\` held exactly one file —
`pifo.ifo`, **0 bytes** — although the agent had written both it and an 11,918-byte stash two
seconds earlier. So the directory was emptied and then `LoadPCAutoSave`'s source-less `FileCopy`
recreated the one name it targets. The main-menu adoption never saw this because the wipe rides
on the teardown only a mid-session load performs (`DestroyServer` / `CClientExoApp::UnloadModule`
at `0x6CB310..0x6CB324`). `PlayerIfo.TryWritePifo` now writes the stash to the GAME DIRECTORY
ROOT; `MenuLoadController.RestoreCarriedCharacter` prefers it there and keeps `Temp\` as a
fallback so a newer plugin still works with an older agent. That is the difference between the
13:49 run (blank character) and the 13:57 run (10,110 bytes restored, correct character).

### Still open after this round

1. **The settle is not 4 seconds in practice.** `WorldSettleMs` is 4000, but the save request
   landed **122 seconds** after world-up (13:55:51.648 → 13:57:53.578). `MenuFrame` is the
   game's own MainLoop and the Endar Spire's opening cinematic holds it (the same log records
   `the main thread was gone for 12703 ms` across one stretch), so the poll simply does not run.
   It is not a correctness problem — the flow completes and the player waits through an intro
   they were going to watch anyway — but "a few seconds later" in the README is optimistic, and
   a round that wants it faster should look at cutting the intro short rather than at the timer.
2. **The party.** Unchanged: a level-1 character adopts the host's `PARTYTABLE.res`, so it comes
   up with companions it has never met. `partystate` confirms the roster mirrors correctly and
   `the host still controls its companions` passes, so this is cosmetic as predicted — but it is
   still a first-time joiner standing next to a stranger the game thinks they recruited.
3. **Arrival position.** The joiner lands at `ModEntryArea`, ~5 units from the host in
   `END_M01AA`. Fine here; in a large module it would be a walk. No snap-to-host exists and none
   was added this round.

# A fresh `join_mode = newcharacter`'s starting gear sits unequipped (2026-09-10)

**Symptom, from a real cross-machine test.** After a first-time joiner (`join_mode =
newcharacter`) finished chargen and was adopted into the host's world, the HOST's screen showed
the joiner's phantom with NO CLOTHING — a bare/default body model instead of the light
robes/starting weapon retail chargen grants. Filed as a reproduction lead in
docs/research/network-topology.md's 2026-09-10 "New findings" section, alongside three
unrelated bugs (a wrong portrait, a movie replay, a dialogue-click issue) that this round did
not touch.

## What it is not

The wire and the receiving side both behave correctly and were RULED OUT first:

- `PlayerState.EquippedBody`/`EquippedRightWeapon`/`EquippedLeftWeapon` publish exactly what
  `KotorGameBridge.ReadEquippedResref` (KotorGameBridge.Items.cs) reads off
  `creature+0xA2C`'s slot dwords — verified by reading the SAME dwords directly out of the live
  process with a throwaway script and comparing.
- `EquipApplyDecision`/`PartyPresenceController.ApplyEquipment`'s "empty length = leave it
  alone, never invent an unequip" convention is exactly right and was not the bug — an empty
  value here means "the source has nothing to report", not "actively strip this slot".
- The item-tag/address cache (`KotorGameBridge.Items.cs`'s `ServiceItemTagCache` /
  `ItemSweepProvenanceRule`) is not stale or slow: the `items` console verb, run on the live
  joining install, reported "57 item bodies, 57 with a readable tag" — including the exact
  starting items — while the equip-slot dwords still read zero.
- It is **not** a "read too early, wait for non-empty" timing race in the sense the original
  task hypothesis suggested. Waiting does not fix it: read directly with a throwaway
  memory-poke script (below), the joining character's own `CSWInventory` slot dwords
  (`inventory+0x08` body, `+0x14` right weapon, `+0x18` left weapon) still read **zero 90+
  seconds after the character had already joined and stabilised** in the host's session. A
  longer wait never turns that into a real resref; there is nothing to eventually read.

```powershell
# creature address from the plugin block (BlockEnginePlayerPointer, +0x2260), then:
$inv = Read-UInt32 $creature+0xA2C          # CSWInventory*
Read-UInt32 $inv+0x08   # body    -> 0
Read-UInt32 $inv+0x14   # right   -> 0
Read-UInt32 $inv+0x18   # left    -> 0
```

## What it actually is

The starting gear chargen grants is created (`CreateItemOnObject`, synchronous, confirmed —
the items exist, are tagged, and their `Possessor` field names the right creature) but never
gets **linked into the creature's own equip-slot array**. `Odyssey.Equipment.cs`'s own existing
doc comment on `EquipItemFn`/`ActionEquipItem` already documents the general mechanism this
matches: `ActionEquipItem`'s VM dispatch does not equip synchronously at all — it appends a
node onto the creature's own `CSWSCombatRound` pending list, and nothing arms that round to
drain it unless the caller is an actively-simulated party member being driven through the
engine's ordinary per-frame processing. Whatever chargen's own "Quick Character" template
application uses internally to grant + equip the starting package apparently hits the same
class of gap for a character created through this path.

This is compounded by (and made much harder to reproduce reliably by) a fact this document
already recorded in the section above: `MenuFrame`/`OnFrame` does not run at all while the
Endar Spire's opening cinematic holds the main thread — the same round measured a 122-second
gap between world-up and the save request landing. Whether the starting items are even
findable at all, and where, is genuinely NOT a constant across runs — see the live-measurement
table below.

## The fix — `Odyssey.EquipUnequippedStartingGear`

Added to `Odyssey.Equipment.cs`, called once per poll from `NewCharacterCaptureController`
(NewCharacterCaptureController.cs) for a bounded window (`EquipRepairExtraMs`, 6000 ms) right
before the character is quicksaved for carrying — i.e. it repairs the character's own equip
slots BEFORE the save that `PlayerIfo`'s "verbatim GFF copy" faithfully carries into the host's
world, rather than trying to fix anything after the fact.

It looks for a starting item in three places, because live testing found the items do not
consistently sit in the same one:

1. The creature's own personal backpack (`CreatureItemRepo`, `creature+0xA30`) — LIVE-MEASURED
   **empty in every single run**; the starting items are never linked here at repair time.
2. The repository `Odyssey.PartyPool.cs`'s `ResolveCreatureItemRepo` returns via the engine's
   own `GetItemRepository(1)` accessor — per `GiveItemToInventory`'s own doc comment this is the
   SHARED PARTY POOL for a live party member, which the local player always is. LIVE-MEASURED:
   this is where the starting WEAPON reliably turns out to be.
3. A bounded id-window scan (±32) around the creature's own object id, for a straggler linked
   into neither repository — chargen mints a character's starting items in the same creation
   burst as the creature itself (ids handed out sequentially), so a live item whose own
   `Possessor` field names this exact creature is a safe, vetted candidate
   (`ResolveItemBase`'s existing vtable+id check, plus this method's own Possessor re-check,
   gate every candidate before anything is touched — a wider window costs wasted reads, never a
   wrong write).

Classification (`StartingGearSlotRule.cs`, unit-tested) uses the same resref-prefix convention
every equip decision in this project already assumes for a stock blueprint (`g_a_` = body/
armour/clothing, `g_w_` = weapon) rather than a live baseitem-type read — this project has no
verified offset for `CSWSItem`'s BaseItem field, and inventing one blind under this round's time
budget was rejected as exactly the kind of unverified engine write docs/ARCHITECTURE.md's
engine-write rules warn against. The actual equip uses the SAME direct, readback-verified
primitive `EquipItemFromTemplateDirect` already uses (`SetPossessorFn` + `RepositoryRemoveItemFn`
+ the synchronous `CSWSCreature::EquipItem` call + a slot-dword readback) — never the queued VM
action, which is the mechanism suspected of causing the gap in the first place. A slot that
already holds an item is never displaced.

## ⚠ Partial fix — LIVE-CONFIRMED 2026-09-10, six joins, one class package

| Run | Repo/window fix present | Retry window | Weapon slot | Clothes slot |
| --- | --- | --- | --- | --- |
| 1 (baseline, no fix) | — | — | empty | empty |
| 2 (`ResolveCreatureItemRepo` only, wrong repo for the personal-backpack case) | partial | none | not found (0 repaired) | not found |
| 3 (raw `CreatureItemRepo` field only) | partial | none | not found (0 repaired — repo real but empty) | not found |
| 4 (repo fallback: personal, then pool accessor) | yes | none | **found & equipped** (pool accessor) | not found |
| 5 (+ id-window scan, single attempt at settle) | yes | none | not found this run | not found |
| 6 (+ 6s retry window) | yes | 6000 ms | **found & equipped** | not found |
| 7 (+ 15s retry window) | yes | 15000 ms | **found & equipped** | not found |

The starting **WEAPON is now reliably equipped** — confirmed via the readback check inside
`EquipUnequippedStartingGear` (it will not log/count a "success" unless the raw
`CSWInventory` slot dword afterwards actually equals the item's own id) and independently via
`readslot.ps1`-style direct memory reads showing the right-weapon slot populated after
adoption.

The starting **CLOTHES item was never found or equipped, in any run**, despite:

- Being confirmed genuinely possessed by the same creature (`items` console verb, tag
  `g_a_clothes01`, `held by 0x<creature id>`) moments after the repair ran and failed to find
  it.
- The id-window scan covering the exact id gap observed between the creature and its clothes
  item in every run where both were visible via `items` (+1, sometimes +7/+9 for a stale
  duplicate copy — well inside the ±32 window).
- Extending the retry window from 0 → 6000 → 15000 ms changing nothing: the outcome was
  identical at all three, which rules out "just needs more time" as the explanation for
  clothing specifically (it did not rule out timing for the weapon, which needed the retry
  window to be reliable at all).

**NEEDS-LIVE, unresolved:** what actually creates or links a fresh character's starting
CLOTHES item, and why its path differs from the starting weapon's. Candidate theories not yet
tested: the clothes item is genuinely created later than the weapon (by a different, as-yet
unidentified script or system) and 15 extra seconds still is not enough; the `items` console
verb's own broad committed-memory scan is finding a **duplicate/stale copy** of the item body
that only exists after the observed 0x129/0x131-style id duplication (this project's own
scanner code already documents that class of artifact — freed memory reused, an old and new
copy both briefly visible) rather than the same object my repair could have reached; or the
clothes item is attached to the creature through a mechanism (a body-model/appearance
attachment rather than a true `CSWInventory` equip slot) this repair does not model at all. A
host still sees a first-time joiner's phantom carrying its correct starting weapon while
wearing default/bare clothing until this is closed.

## What was verified

- `dotnet build -warnaserror` (full solution) and `src/KotorCoop.Plugin/build.ps1
  -WarnAsError` (NativeAOT plugin) both build clean.
- `cd tests/KotorCoop.Tests && dotnet test`: 3481/3481 passing (includes 8 new
  `StartingGearSlotRuleTests` covering the prefix classification both ways).
- Live, six times, against the two-GOG-copy rig (`demo.ps1 -ClientNewCharacter`, chargen driven
  with the `chargen newgame`/`quick 2`/`accept` blockpoke verbs — no mouse/keyboard input): the
  weapon fix reproduces reliably at a 6-second retry window; the clothes gap reproduces (fails
  to close) with total consistency.

## Round 2026-09-10b — diagnostic instrumentation, and a SEPARATE bug it caught (open)

Added `DiagnoseCandidates`/`DiagnosePollSummary` to `Odyssey.Equipment.cs` (diagnostic-only,
never equips/touches anything, bounded logging, `newchar-diag:` prefix) to answer the addendum's
open question directly: does the clothes item's id ever appear among
`EquipUnequippedStartingGear`'s own three candidate sources at all?

One live joiner-chargen capture (two-GOG-copy rig, `-ClientNewCharacter`) landed on something
upstream of that question instead. `EquipUnequippedStartingGear` is called as
`Odyssey.EquipUnequippedStartingGear(Odyssey.GetPlayerObjectId())`
(`NewCharacterCaptureController.cs:271`). In this capture, `GetPlayerObjectId()` returned
**`0x7FFFFFFF`** for the entire 6-second/25-poll repair window — not the joiner's real new
creature id. `ResolveServerObject(0x7FFFFFFF)` still resolved to *something* (a valid,
non-null object with a real inventory), so the repair function never early-outs; it just runs
its whole candidate search — both repositories, the ±32 id-window scan, everything — against
the WRONG object the entire time. Its pool repo held exactly one item that whole run
(`g_i_adrnaline003`, an adrenaline stim, `possessor=0x7FFFFFFF`); neither the weapon nor the
clothes item was ever a candidate, because neither is possessed by `0x7FFFFFFF` — they belong
to the real creature. `newchar-diag: candidate 0x122 tag='g_i_adrnaline003' possessor=0x7FFFFFFF
(creature is 0x7FFFFFFF) classify=-1` plus 25 polls of `personal repo ... count=0, pool repo
... count=1` are the log lines (`kotorcoop-plugin.log`, `D:\Games\swkotor-gog-p2`,
16:23:10–16:23:16 on 2026-09-10).

`0x7FFFFFFF` is not random garbage — it is a value this project's own `Odyssey.ControlSwitch.cs`
has logged before as the engine's OWN placeholder for "no real player creature registered yet"
(`kotorcoop-general.log`, same install, 14:18:41: `control: server player re-pointed
0x7FFFFFFF -> 0x128`). `GetPlayerObjectId()` (`Odyssey.Phantom.Gait.cs:236`) reads
`CServerExoApp::GetPlayerCreature()` and then that returned object's own id field
(`+0x04`/`+0x28`, same field `SyncServerPlayerToLeader` calls `registered`) — i.e. it reports
whatever the engine's server-side "player" slot is CURRENTLY POINTING AT, not necessarily the
locally-controlled PC. For an ordinary game this slot is set once, early, and stays right. For a
first-time joiner going through mandatory `join_mode = newcharacter` chargen, nothing in this
capture ever re-pointed that slot to the freshly minted creature before the repair window ran
out — it sat on the placeholder the whole time.

**This does not by itself explain the weapon-vs-clothes asymmetry** the previous round
established across six clean joins (weapon reliably equips, clothes never does) — if
`creatureObjectId` were wrong in those six runs too, the weapon could not have been found either
(its candidacy also requires `possessor == creatureObjectId`). So this capture's
`GetPlayerObjectId()` failure looks like a SEPARATE, intermittent race — the server's "player"
slot not yet re-pointed to the new creature by the time `NewCharacterCaptureController` calls in
— layered on top of, not a replacement for, the still-open clothes question. It is a genuine
bug in its own right: when it fires, the WHOLE repair silently no-ops (0 repaired, no log line),
so a joiner could end up with neither weapon nor clothes, not just bare clothing.

**NEEDS-LIVE, unresolved (both, now):**
1. The original question — does the clothes item's id ever appear among the three candidate
   sources when `creatureObjectId` IS correct? This capture could not answer it; the
   diagnostic logging is in place (`Odyssey.Equipment.cs`, `DiagnoseCandidates`/
   `DiagnosePollSummary`, uncommitted-behavior-safe) and needs a run where `GetPlayerObjectId()`
   is confirmed correct at call time (e.g. log its value next to `registered` from
   `SyncServerPlayerToLeader`, or cross-check against the `items` verb's creature id) to get a
   clean read.
2. What actually re-points the engine's server "player" slot to a first-time joiner's freshly
   created creature, and why that did not happen (or happened too late) in this capture —
   candidate: whatever wiring a genuine "New Game" flow does that this project's own join path
   does not reproduce for `join_mode = newcharacter`.

No fix landed this round — instrumentation and this finding only. The diagnostic code is
inert (logs only, vetted the same way every other read in this file is) and safe to leave in
for the next live pass.

## Round 2026-09-10c — a live crash found and fixed (unrelated to the clothes gap itself)

Not a clothes finding, but landed in the same investigation: a first-time joiner's own
freshly-created chargen module crashed the game outright (`EXITPROCESS 0xC0000005`) during this
round's rig time. Root cause was a race between two flows that both react to the joiner's
module not matching the host's world:

- `NewCharacterCaptureController`/`JoinFlow.CreateCharacterThenAdoptAsync` — the capture+adopt
  path this document is about, already actively resolving the mismatch by design.
- `PersistentSaveMismatchRule` (`docs/research/session-save.md` §"Rule") — the general
  established-player world-drift detector, which cannot tell "nothing is handling this
  mismatch yet" from "the chargen capture flow already owns it and is mid-flight."

Both raced to tear down/rebuild the same session, and the two independent `DestroyServer`/
`CreateServer` calls hit an access violation. **Fixed** in
`src/KotorCoop.Agent/CoopCommandDispatcher.SaveLoad.cs`: the mismatch rule's `Observe()` call is
now skipped entirely (not called with a forced-false result — that would still touch
`_mismatchRejoinState`) for the duration of an in-flight chargen capture, detected via
`bridge.TryGetNewCharacterCapture` reporting `ChargenStateArmed` or `ChargenStateSaving` (local
mirrors of `SharedBlock.JoinChargenStateArmed`/`JoinChargenStateSaving`, duplicated across the
plugin/agent assembly boundary the same way `JoinFlow.cs` already does for the same field). This
leaves the very next tick after the capture window closes free to read as a fresh edge if a
mismatch still genuinely persists (i.e. the capture itself failed) — the rule's ordinary
fallback still applies once neither flow owns the mismatch.

Same commit also widened `EquipRepairMaxAttempts` from 100 to 400 polls, based on further live
observation (this round's own capture) that the smaller poll-count budget introduced in the
prior round wasn't consistently enough — a single main-thread stall from the Endar Spire intro
cinematic can run past 120 seconds and consume most of a smaller budget's wall-clock window
before the poll count itself is exhausted. This widening is unrelated to the clothes-specific
gap (still open — see the round above) but reduces the chance the repair window closes before
enough real polls land.

Both changes are unit-covered and build-clean; **NEEDS-LIVE** for whether the crash fix holds
across further chargen joins — this round's rig time was spent on the crash itself once it was
found, not on re-running the clothes capture.

## Round 2026-09-10d — the 0x7FFFFFFF bug confirmed structural, and the weapon "fix" retracted

Short version in `docs/research/world-adoption-clothes-notes.md`; this is the full derivation.
Three live captures, two-GOG-copy rig (`demo.ps1 -ClientNewCharacter`, `chargen newgame`/
`quick 2`/`accept` blockpoke verbs, `rig.lock` held as `clothing-fix-round-3`).

### 1. The requested cross-check log, and what it immediately showed

Added at the `EquipUnequippedStartingGear` call site (`NewCharacterCaptureController.cs`):
`GetPlayerObjectId()`'s raw value next to `SyncServerPlayerToLeader`'s last-known `registered`
(exposed as `Odyssey.ControlSwitch.cs`'s new `LastKnownServerPlayerRegistered`). First live
capture fired it immediately:

```
newchar-diag: GetPlayerObjectId()=0x7FFFFFFF vs SyncServerPlayerToLeader last registered=never logged
```

...on EVERY poll, for the entire repair window (measured: 116+ consecutive polls). Round 2b had
seen this once and could not tell if it was a fluke. Round 3 ran it three times, isolated any
way it could think of, and it fired identically every time: **the 0x7FFFFFFF placeholder is not
intermittent — it is the value `GetPlayerObjectId()` reports for the WHOLE equip-repair window
on every first-time `join_mode = newcharacter` join.** `SyncServerPlayerToLeader` never ran even
once in any of the three captures (no `registered` value to compare against) — consistent with
its own doc comment: it only fires on an actual leader SWITCH, and a first-time joiner has no
prior leader to switch FROM.

### 2. Two hypothesised fixes, tried live, both falsified

**Fix attempt A — client-side fallback.** Added `Odyssey.GetControlledCreatureObjectId()`
(`Odyssey.Menu.cs`): reads `CSWParty` (client-side) member[0]'s own id directly, the same field
`GetControlledCreaturePointer()` already resolves through `ResolveServerObject`, but returned as
the bare id. Reasoning: this field is driven by client-side input authority, not the
server-side `CSWSPlayer::SetGameObject` wiring `GetPlayerObjectId()` depends on, so it seemed
like a plausible independent source of truth. Wired it in as the PRIMARY repair id (falling back
to `GetPlayerObjectId()` only if it read 0), rebuilt, redeployed, ran a second live capture.

**Live result: falsified.** `GetControlledCreatureObjectId()` read `0x7FFFFFFF` too (or
`0x7F000000` — `OBJECT_INVALID` — in the very first poll or two, before any client-side party
exists yet) for the entire second capture's repair window. Reverted the substitution: the actual
repair still calls `EquipUnequippedStartingGear(Odyssey.GetPlayerObjectId())` unchanged from
round 2's baseline; the new accessor is kept only as a logged cross-check.

**Fix attempt B (diagnostic probe) — read the id even earlier.** Before concluding no accessor
works, added one more diagnostic: `CharGenDriveController.Accept()` now logs
`*(uint*)(creature + 0x04)` — the chargen-time creature pointer's OWN id field, read directly,
before `BeginPlayGame` even runs. This is the earliest possible point in the whole flow to ask
the object what its own id is.

**Live result: also `0x7F000000`.** Third capture. So from class selection through at least 90
seconds into the new module, THREE independent reads (server player slot, client party
member[0], the object's own id field at chargen time) all agree: this creature has no real,
assigned numeric id yet. This is not a read-side bug in any one accessor — the object itself
appears not to be registered into the engine's normal id space during this whole window.

### 3. The weapon "fix" from round 1 is retracted — it was `party_pool_sync` colliding with the same bug

This rig's shipped `coop.ini` default is `party_pool_sync=2` (host-authoritative). Re-reading the
first capture's own log with fresh eyes: right before the "successful" weapon equip, the log
shows

```
party pool: add diag - repo 0x34A9B20, item_count 1->2, 0x12B links 1->1, fresh body (linked by the engine, stack 2)
party pool: added 'g_i_medeqpmnt01' (0x12B) x2, charges 0/0, upgrades 0x0
cheat: added 'g_i_medeqpmnt01' x2 to the party pool (status 1)
...
cheat: added 'g_w_blstrrfl001' x1 to the party pool (status 1)
newchar-diag: candidate 0x12C tag='g_w_blstrrfl001' possessor=0x7FFFFFFF (creature is 0x7FFFFFFF) classify=4
newchar: equipped starting item 0x12C into nwscript slot 4 (was sitting unequipped in the fresh character's own inventory)
```

`cheat: added '...' to the party pool` is `CheatController.PartyPool.cs`'s `PartyPoolAdd` —
"the automatic apply half of the [party-pool] sync channel" per its own doc comment: this is the
HOST syncing an item from ITS OWN inventory into the joiner's shared pool, nothing to do with
chargen. The synced item's `Possessor` gets stamped with whatever `GetPlayerCreature()` reports
at link time — which round 3 has now shown is ALSO `0x7FFFFFFF` — so
`EquipUnequippedStartingGear`'s `possessor == creatureObjectId` check matches by coincidence
(wrong equals wrong) and a weapon synced from the HOST's own inventory gets equipped onto the
joiner. This looks exactly like "the joiner's own starting weapon worked" and was reported as
such in round 1's six-join table, but it demonstrably is not that.

**Isolated live test, round 3:** set `party_pool_sync=0` on both installs (temporary, restored to
`2` afterward — this was never meant to be a shipped-default change), cleared the plugin log, ran
a clean third capture. Result: **zero candidates found for the entire repair window** —
`personal repo ... count=0` and `pool repo ... count=0` on every one of 80 polls, and all 32
distinct ids the ±32 window scan produced (built around the placeholder `0x7FFFFFFF`/`0x7F000000`,
not any real id) failed to resolve to anything (`did NOT resolve` on every one). Neither the
weapon nor the clothes item was ever a genuine candidate in this clean run. This means: **the
equip-repair mechanism has not been confirmed to have ever equipped a joiner's own genuine
starting gear, in any round's testing to date** — every previously reported "success" was the
sync collision above. `Odyssey.EquipUnequippedStartingGear`'s own doc comment (the
"⚠ PARTIAL FIX, LIVE-CONFIRMED" paragraph above) is now known to overstate what was actually
demonstrated and should be read with this correction in mind pending a genuinely isolated,
id-correct re-verification.

### 4. A timing clue for the real root cause

Across all three captures, the real numeric id (`CSWPartyTable`'s "party table probe" diagnostic
log first showing e.g. `id 0x128 creature` instead of `id 0x7F000000 none`) appeared **2-3
seconds AFTER** the equip-repair's own forced save fired — i.e. right around when the AGENT picks
up the saved character and starts adopting it into the HOST's world (a fresh module load, which
assigns object ids from scratch, starting low). This is a real, repeated pattern (3/3 captures)
but not yet proof of causation: it has not been established whether the joiner's creature ever
receives a real id inside the ORIGINAL Endar Spire chargen module at all (in which case the fix
is to wait for it, however long that takes), or whether it only ever gets one as a side effect of
being loaded fresh into a different module during adoption (in which case the equip-repair is
structurally in the wrong place — pre-save in the original module — and would need to move to
run post-adopt, in the host's own session, against the real post-load id).

### What was verified this round

- `dotnet build -warnaserror` (full solution) and `src/KotorCoop.Plugin/build.ps1
  -WarnAsError` (native plugin) both build clean after every change this round.
- Three live captures on the two-GOG-copy rig, each ending in a successful save/adopt
  (no crash) — the round c fixes held.
- `party_pool_sync` isolation and restoration confirmed via direct `coop.ini` reads before and
  after (not just assumed from the diff).

### Still open (round 3's own list)

1. Does the joiner's creature ever get a real numeric id inside the ORIGINAL chargen module, or
   only after the save/adopt round-trip? Needs a diagnostic-only run with
   `EquipRepairMaxWallClockMs` pushed well past 90s (150000+, NOT for shipping) to see whether
   `GetPlayerObjectId()`/`GetControlledCreatureObjectId()` ever stop reading a placeholder before
   the module itself ends.
2. The original round 1/2 question — does the clothes item ever appear as a candidate, given a
   CORRECT id — is still completely unanswered; every capture to date, in every round, has run
   the search against a placeholder id.
3. Any future capture must isolate `party_pool_sync` (temporarily 0, restored after) to avoid
   repeating the round 1/2 misattribution.

## Round 2026-09-10e — the timing question answered (and it was the wrong question), the repair
## relocated post-adopt, and the weapon fix shipped and live-verified 4/4

Rig: two-GOG-copy (`demo.ps1 -ClientNewCharacter`, `chargen newgame`/`quick <n>`/`accept`
blockpoke verbs), `rig.lock` held as `clothing-fix-round-4`. `party_pool_sync` isolated to `0`
on both installs for every capture below, restored to `2` (the shipped default) at the end.

### 1. Settling round 3's own open question — and finding the question itself was malformed

Per round 3's plan, `NewCharacterCaptureController.EquipRepairMaxWallClockMs` was pushed from
90000 to **150000 ms** (diagnostic only, `EquipRepairMaxAttempts` widened to 650 to match) and a
clean live capture run end to end. Result: `Odyssey.GetPlayerObjectId()` and
`GetControlledCreatureObjectId()` read `0x7FFFFFFF` on **every single one of ~600 polls**, for
the entire 150-second window, right up to the wall-clock ceiling firing
(`newchar: equip-repair wall-clock ceiling reached (150000 ms) with 49 poll(s) still budgeted`).
So: no, the real id never appears inside the original chargen module, confirming round 3's
suspicion — but the SECOND half of the diagnostic is the actual finding this round turned on.

**The same accessor was queried on the HOST's own character** — a long-established, fully-played,
fully-equipped save, nothing to do with chargen (`blockpoke console 'resolve party'` on the host
instance): `server player: 0x19891D08 id 0x7FFFFFFF is_pc=True hp 12`. **Identical placeholder.**
Rounds 1-3's whole framing — "`0x7FFFFFFF` means no real player is registered yet; wait for a
real one" — was wrong. `0x7FFFFFFF` is the engine's PERMANENT, reserved self-id for whichever
creature a client's `CSWSPlayer` currently drives, on every client, in every session, forever.
It is not a sentinel that eventually resolves to something like the low three-digit hex ids
(`0x120`s) a companion or an item gets; it never will, for anyone. `Odyssey.ResolveServerObject`
resolves it through the engine's own `GetGameObject` exactly like any other id — confirmed this
round: the resolve succeeds, and the resolved creature's own `+0x04` field reads back the same
`0x7FFFFFFF`, so the object table is entirely self-consistent about it. There is no real numeric
id to wait for, and there never was — three rounds spent hunting for one were chasing a mirage.

### 2. So why did the repair never find anything? The actual gap

Re-reading round 3's own capture with this correction in hand: for the WHOLE original
chargen-module window, `EquipUnequippedStartingGear`'s two candidate repositories (the creature's
personal backpack and the party-pool repository) read `count=0`, every single poll
(`newchar-diag: poll N ... personal repo ... count=0, pool repo ... count=0`). The items chargen
grants are simply **never linked into either repository at all** while still inside the isolated,
hand-rolled in-process chargen replay (`CharGenDriveController`) — regardless of what id is used
to address them, and regardless of how long the wait. The bounded id-window scan
(`StartingGearIdWindow`, ±32 around `creatureObjectId`) was ALSO never going to help for this
specific id: `0x7FFFFFFF` is nowhere near the normal dynamic-pool id space every other object
(Trask, the items themselves) actually gets handed out of — measured this round, low three-digit
hex, e.g. `0x121`/`0x123`/`0x125`/`0x12A`-`0x12C`. Scanning ±32 around a reserved sentinel can
only ever find more of the same sentinel-adjacent nothing (confirmed: all 32 window-scan
candidates, every capture, logged `did NOT resolve`).

### 3. The fix: relocate the repair, not the id

`EquipUnequippedStartingGear`'s own logic was never the bug and is unchanged. What moved is WHEN
it runs: a new `PostAdoptEquipRepairController` calls it exactly once, after a first-time
joiner's adopted-world load finishes — a genuine, ordinary engine module load
(`CreateServer`, real character login, normal inventory linking), the SAME kind of load every
other join type already goes through — instead of `NewCharacterCaptureController` calling it
repeatedly, for up to 150 seconds, from inside the isolated chargen module. `PostAdoptEquipRepairController.Arm()`
is called by `NewCharacterCaptureController` the instant it captures a fresh chargen character
(before the quicksave even completes), recording the shared block's current `MenuLoadApplied` as
a baseline; a small state machine (extracted as `PostAdoptEquipRepairRule.Decide`, unit-tested)
then watches for that field to move to a new serial with `MenuLoadStatus == 0` —
`MenuLoadController`'s own "the world is up and on screen" ack — and polls the repair (bounded,
20 attempts at 250 ms) from there.

`NewCharacterCaptureController` no longer runs any repair before the save: since the repair
structurally cannot succeed inside the isolated chargen module (per §2 above), the pre-save wait
was pure dead weight — up to two and a half minutes of a first-time joiner's very first join,
spent on a repair that could never find anything. Removed entirely; the controller now saves
`WorldSettleMs` (4 s) after world-up, exactly as it always did before the repair loop was added.

### 4. Live verification — four independent joins, four different chargen classes, 4/4

Each capture: `demo.ps1 -ClientNewCharacter`, drive `chargen newgame` (auto-pressed by the join
flow) / `chargen quick <n>` / `chargen accept` by hand, watch `kotorcoop-plugin.log` for
`post-adopt equip repair:` and the `newchar-diag: candidate` lines underneath it. The first three
captures ran against the functional fix; the fourth ran against the final committed build, AFTER
the pure code-organisation refactor that split `PostAdoptEquipRepairRule` out of the controller
for unit testing — confirming the refactor changed nothing observable.

| Class index | Candidates found (personal+pool, fully enumerated) | Weapon equipped | Clothes candidate seen |
| --- | --- | --- | --- |
| 0 | `g_i_adrnaline003` (stim), `g_i_medeqpmnt01` (medpac), `g_w_blstrrfl001` (blaster rifle) | Yes — 1 poll after world-up | No |
| 2 | same three | Yes — 1 poll after world-up | No |
| 4 | same three | Yes — 1 poll after world-up | No |
| 1 (final, against the committed refactor) | same three | Yes — 1 poll after world-up | No |

Every run: `post-adopt equip repair: equipped 1 starting item(s) the joiner's chargen left
unequipped (player id 0x7FFFFFFF, 1 poll(s) after the adopted world came up)` — i.e. the
repository was ALREADY populated by the time the world-up ack landed; the fix never needed more
than its first poll in any of the four captures. The weapon's own candidate line every time:
`newchar-diag: candidate 0x12C tag='g_w_blstrrfl001' possessor=0x7FFFFFFF (creature is
0x7FFFFFFF) classify=4` — a genuine match (not the round-1 `party_pool_sync` collision: isolated
to `0` for every capture here), correctly classified, correctly equipped, readback-verified by
`EquipUnequippedStartingGear`'s own inline slot-content check (it reads the raw `CSWInventory`
slot back after the write and only counts the item as equipped if it lands). Process stayed
responsive and the session stayed live (`resolve party` answered normally) after every capture —
no crash, no regression.

Save-to-adopt timing also improved as a direct consequence of removing the dead pre-save wait:
`newchar: asked the engine to save` now fires promptly once `MenuFrame` is actually ticking (the
Endar Spire's own opening-cinematic stall — unrelated, pre-existing, main-thread-blocking,
independently measured this round at 88-122 seconds depending on the run — is the only remaining
source of delay between world-up and the save, not this controller's own logic).

### 5. The clothes half: very likely never a real bug

**Not one of the three classes tested (indices 0, 2, 4) ever produced a `g_a_`-prefixed candidate
from any of the three search sources**, with the search now provably working (it finds the
weapon every time). All three classes granted the exact same three items — a stim, a medpac, and
a weapon — nothing else. The working conclusion: KOTOR's premade "Quick Character" chargen
packages do not grant a separate wearable clothes item at all; the class's own default appearance
already renders as dressed without one. If true, the "clothes" half of the original player-facing
report was never a missing-equip bug — there was never an item to equip. This is NOT exhaustively
confirmed (only 3 of the 6 quick-chargen portraits tested, and a full/custom chargen — as opposed
to the Quick Character button this rig automates — was not tested at all), but three consistent,
independent, byte-identical results across what should be three different underlying classes is
strong evidence, not a guess.

### 6. What was verified this round

- `dotnet build -warnaserror` (full solution, including the new `PostAdoptEquipRepairRuleTests`)
  and `src/KotorCoop.Plugin/build.ps1 -WarnAsError` (native plugin) both build clean after every
  change.
- `dotnet test` — 8 new tests for `PostAdoptEquipRepairRule`, all passing.
- Four live captures end to end on the two-GOG-copy rig (classes 0, 2, 4, then 1 again against
  the final committed build after the pure code-organisation refactor that split
  `PostAdoptEquipRepairRule` out of the controller) — see the table above and its addendum below.
- `party_pool_sync` isolated to `0` on both installs for every capture, restored to `2` and
  confirmed via direct `coop.ini` reads afterward.

### Still open for a future round

1. Confirm (or refute) §5's clothes theory exhaustively: the remaining 3 quick-chargen portraits,
   and a full/custom (non-Quick) character creation, which this round's automation does not
   drive. If any package IS found to grant a `g_a_`-prefixed item, the fix here should find and
   equip it with no further code changes (`StartingGearSlotRule.Classify` already handles the
   prefix generically, and it is unit-tested) — this is a verification gap, not a known code gap.
2. §4's process-stability checks were "still responding, session still live" — not a full
   multi-hour soak. Nothing this round found suggests instability, but a longer play session
   after a `newcharacter` join has not been specifically exercised.
