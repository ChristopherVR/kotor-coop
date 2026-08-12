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

NEEDS-LIVE — none of this has been run against the game:

1. **Does the adopted world come up at all**, and with the joiner's character rather than a
   blank level-1 stranger? `block+0x3324` reports the flavour taken; the character's own
   `FirstName` and hit points settle the rest.
2. **Does `LoadPCAutoSave` disturb `Temp\`?** It creates the directory (`CreateDirectory2` at
   0x6CA542) and no delete call was found near it, but this was not chased to the bottom.
   `WorldAdoption.TryReassertCharacter` exists to be called again immediately before the load
   fires; if the answer is "no", it is redundant rather than wrong.
3. **Where does the joiner physically arrive?** The TEMP path leaves `[player+0x40] == 0`, so
   `InitiateModuleForPlayer` places them at the module start point rather than a saved
   position. That is believed to be the *right* behaviour for someone arriving in another
   person's world, but it has not been seen.
4. **The party.** The adopted world's `PARTYTABLE.res` and `AVAILNPC0.utc` are the host's, so
   the joiner should come up with the host's companions. Untested, and it interacts with
   `PartyRosterController`, which is the channel that keys on exactly this.
5. **A host mid-module.** Everything here assumes the host's save is a save. Serving a world
   while the host is standing in it means serving a *stale* one — the host would need to save
   first, or the joiner adopts the last save and then converges through the ordinary channels.
   This is the largest open design question, not just an untested path.

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
