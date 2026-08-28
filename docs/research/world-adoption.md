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
| `0x006DBE9B` | **`SaveCharGenCreature([classSel+0x68])`** — `0x6123E0` | writes `Temp\pifo.ifo` |
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
| `join_mode` | `save` | `save` = carry this install's own save's character (today's behaviour). `newcharacter` = go through retail chargen and carry that. |
| `auto_adopt_world` | ON | a joiner whose world does not match the host's adopts instead of playing degraded. Also what makes a HOST serve its world by default — a joiner with no save has no other way to obtain one. |

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
| `0x6123E0` | `CSWCCreature::SaveCharGenCreature` — writes `TEMP:pifo`; **one** call site, `0x006DBE9B` |
| `0x6DBDF0` | `CSWGuiClassSelection::BeginPlayGame` — the whole new-game start; CreateServer `0x6DBE93`, pifo `0x6DBE9B`, connect `0x6DBEDE`, RequestStatus `0x6DBF07`, module name `0x6DBF0C`+ |
| `0x6EB320` | `CSWGuiMainCharGen::BeginPlayGame` — `RemoveSubPanels` then tail-jmp to `0x6DBDF0` with `this = [chargen+0x68]`; **no stack args** |
| `0x67AFB0` | `CSWGuiMainMenu::OnNewGamePicked` — ⚠ dereferences its firing-control argument at `0x67AFF0`; do not pass 0 |
| `0x6DC3C0` | `CSWGuiClassSelection::Constructor(guiManager, CExoString*)` |
| `0x7592A8` | `CSWGuiMainCharGen` vtable (size `0x22DC`) |
| `0x758020` | `CSWGuiClassSelection` vtable (size `0x1560`) |

## Still open

1. **`FireMidSessionPhase1` remains the weak link** — see decision 3. It had never run against
   the game before this path used it.
2. **The party.** Unchanged from §8 item 1, and now sharper: a brand-new character adopting a
   mid-game host's world inherits the host's `PARTYTABLE.res`/`AVAILNPC0.utc`, i.e. companions a
   level-1 character has never met. The roster is host-authoritative anyway
   (`party_select_host_only`), so this should be cosmetic rather than broken, but it is
   untested.
3. **Arrival position.** Same as §7 item 3: `[player+0x40] == 0` on the TEMP path, so the joiner
   lands at `ModEntryArea`, not next to the host. The presence/teleport machinery closes the gap
   afterwards; judging health on POSITION will still call every healthy adoption a failure.
4. **Pressing New Game** — see the section above.
