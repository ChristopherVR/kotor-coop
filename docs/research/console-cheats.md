# Console assist and the staging cheats (2026-08-14)

What the in-game `~` console does for you while you type, and the co-op-aware cheats added
for staging a live test. Read [console-overlay.md](console-overlay.md) for how the console is
opened and read at all, and [console-register.md](console-register.md) for the command table
this leans on.

Tag legend: **VERIFIED** = read out of this binary (GOG 1.0.3, disk == memory, base
`0x400000`) or asserted by a unit test. **NEEDS-LIVE** = reasoned but not yet observed in a
running game; every one of these fails safe and says what it saw.

---

## 1. What the console assist actually does

All of it runs in `ConsoleAssistController`, on the **MainLoop** hook rather than the
SwapBuffers frame hook, because the console opens at the main menu as well as in-world and
the swap hook is dormant behind the menu movie.

| Behaviour | Where | Reaches the screen how |
| --- | --- | --- |
| Up/Down recall submitted lines | `ConsoleHistoryRing`, driven by the controller | `Odyssey.SetConsoleLine` rewrites the engine's own edit buffer; the engine keeps owning it, so "recall then keep typing" needs no extra work |
| Tab completes the first token | `ConsoleCompletionEngine.Complete` | same buffer rewrite |
| Repeated Tab cycles candidates | the engine's `_cycleCandidates` / `_lastProduced` pair | — |
| Escape dismisses the hint | controller (`_dismissed`, cleared by the next keystroke) | stops posting the line |
| Hint line under the input | `ConsoleCompletionEngine.Hint` | `Odyssey.PostTextLine` → `AurPostString 0x0044D490`, grid cell (3, 3), re-posted every frame at alpha 0.75 |
| Argument hints | `Hint` marks the argument being typed: `connect <ip\|#n> [password]  <- [password]` | same hint line |
| Completion over the ENGINE's verbs | `ConsoleCommandTable` reads the retail command table | same candidate list; co-op verbs sort first |
| History survives a restart | `ConsoleHistoryRing.Snapshot`/`Restore` + `kotorcoop-console-history.txt` beside the plugin log | written when the console closes and only when the ring grew |

Notes worth keeping:

- **The engine's own tab-complete is dead code.** `0x0044C0C0` is the retail console's
  completion routine and it has **zero callers** in the binary (`tools/xrefs.py 0x0044C0C0`),
  so our Tab handling is the only one and nothing fights it. **VERIFIED**
- **Which argument you are on** is derived from the usage string itself, not a second table,
  so a verb cannot grow an argument in one place and not the other. A trailing parenthetical
  in a usage line is prose and stops the scan.
- **Passwords are never in the history.** `ConsoleHistoryRing.Record` drops any line whose
  verb is `password`/`pw` (including `/pw` and `coop password`), so the persisted file cannot
  contain one either — and `Restore` re-applies the same rule on the way back in, so a
  hand-edited file cannot smuggle one back.

### Reading the engine's verb list

`ConsoleCommandTable` walks the retail table described in `console-register.md`:

| What | Address | Note |
| --- | --- | --- |
| Command table (flat `void*[]`, sorted by name) | `0x007A8AB8` | **VERIFIED** in console-register.md |
| Entry count | `0x007B8FB8` | **VERIFIED** |
| Verb name, inline at entry + 0 | — | **VERIFIED** (the register helper at `0x0044C560` copies the string to `this+0`; every lookup compares bytes at `[entry]`) |

Strictly read-only, once, on the first frame the console is open. Every pointer is
range-checked and every name is copied through the non-faulting `CreatureFinder.TryReadSelf`
(an unreadable address returns false where a direct dereference would raise an access
violation NativeAOT cannot catch). A count outside 1..512 is refused with a logged line
naming the number it read. Mods that register verbs are picked up for free, because the
source is the live table rather than a list we wrote down.

---

## 2. The retail console cheats — and why we did not rewrite them

KOTOR registers these itself. The names were read out of the binary's string table, the
contiguous block at file offset `0x34F058`..`0x34F210`. **VERIFIED**

```
heal              setcomputeruse   setdemolitions   setstealth       setawareness
setpersuade       setrepair        setsecurity      settreatinjury   setstrength
setdexterity      setconstitution  setintelligence  setwisdom        setcharisma
addexp            turbo            invulnerability  restartminigame  bright
addlightside      adddarkside      revealmap        givecredits      giveitem
givemed           giverepair       givecomspikes    givesecspikes    givesitharmour
warp              infiniteuses     whereami         addlevel         dancedancemalak
```

Every one of them touches **only the typist's own single-player state** — their inventory,
their credits, their XP, their map, their stats. That is exactly what makes them already
correct in co-op: there is nothing to desync, because nothing in that list is shared world
state. So the task's "give credits / give item / unlock the map / set XP and level" is
answered by the game itself:

| Want | Type | Co-op rule |
| --- | --- | --- |
| credits | `givecredits <n>` | local purse; the peer's purse is their own |
| an item in your pack | `giveitem <resref>` | local inventory; if you equip it, the equipment channel shows it on your phantom |
| XP | `addexp <n>` | local; each player levels their own character |
| a level | `addlevel` | local; the level-up itself is the character screen, not scriptable |
| the area map | `revealmap` | local map fog only; co-op already shares fog reveal separately |
| full health | `heal` | local party heal. A temp-dead body is a *separate* state that only the Resurrection effect clears (the path `DeathController`'s peer rescue uses), so it is not what stands a downed co-op player up — which is why `standup` exists. **NEEDS-LIVE**: check 7 confirms `standup` works; nobody has measured what retail `heal` does to a downed body. |

**The one deliberate shadow is `warp`.** The co-op layer intercepts it because travel has to
go through the session (the plugin flushes every spawned body before the module is stored, and
the peer follows). Everything else is left to the engine, and
`ConsoleCompletionEngineTests.NoCoopVerbShadowsARetailConsoleVerb` fails the build if a new
co-op verb ever takes a retail name.

Two consequences worth knowing:

- A retail cheat is **not** intercepted, so a typo in one gets the engine's own
  "function `<x>` not found" — that is the engine talking, not us.
- Because completion now reads the live table, all of the above Tab-complete in-game even
  though the mod knows nothing about them.

---

## 3. The co-op staging cheats

Added only where the answer **depends on who typed it**, or where co-op needs a scenario
staged that nothing else reaches. Parsed in `ConsoleCommand`, dispatched by
`CoopCommandDispatcher`, executed by `CheatController` on the game thread.

| Verb | What it pokes | Co-op authority rule |
| --- | --- | --- |
| `god [on\|off]` | `CSWSObject::min_one_hitpoint` on the local PC (`Odyssey.SetMinOneHitPoint`) | **Yours only.** Your health is yours and is published to the peers from your own creature, so a damage floor keeps both screens agreeing on a real number. Implemented as the engine's own pin rather than a per-frame health write precisely so it does not fight the forwarded-damage pipeline. A bare `god` reports the current state; a module's own pin (Endar Spire command deck, the Taris duel ring) is still a pin, and `god off` says so. |
| `topup [n]` | `SetCurrentHitPoints` on the local PC, clamped to its maximum | **Yours only.** Healing a peer would mean writing a number their own instance republishes several times a second. |
| `standup [n]` (alias `getup`) | `SetRaiseable` + the engine's Resurrection effect, then a deferred hit-point top-up | **Yours only.** Additive rather than a duplicate of retail `heal`: KOTOR's heal restores hit points but leaves a temp-dead body prone, and the co-op down state *is* that body. The top-up is deferred a tick because a heal landing while the target still reads dead is dropped by the engine's own rule. |
| `slay [tag\|id]` | see below | **The whole point.** Decided by `CheatAuthority.ForSlay`. |
| `tp <x> <y> [z]` | `Odyssey.SnapTo` on the local PC | **Yours only.** Your position replicates every tick, so the peer's phantom of you follows with nothing being told about the teleport. |
| `goto <player\|#n>` | the same teleport, aimed at a peer's last known position | **Same module only** — peer positions are area coordinates, so the same numbers name a different place in a different module. Refuses across modules and names both. |
| `dropitem <resref>` | `CreateItemOnFloor` at your feet | **Shared by construction.** A ground item is already replicated state: the peer's instance materialises a real clickable object and it vanishes for both when either picks it up. This is "give the party an item" obeying the replication rule the mod already has — unlike retail `giveitem`, which puts it in your pack where no peer will ever see it. |
| `cheats` | prints both halves of this document's verb list | — |

### The `slay` authority rule

`CheatAuthority.ForSlay(targetResolved, targetIsLocalPlayer, targetIsPhantom,
targetIsCompanion, targetIsPlot, mirrorsEnemies)`, in that order:

1. **unresolved** → refuse, naming the id or tag it could not find;
2. **your own character** → refuse (`slay me` is caught in the parser too, for a faster answer);
3. **a peer's phantom** → refuse; that body is rendered here on their behalf and killing it
   would produce a corpse only this instance can see;
4. **a party companion** → refuse; the host drives them and every client mirrors them, so a
   kill on the wrong side is a desync, not a death;
5. **plot-flagged** → refuse; the module decides who is un-killable, the same rule the combat
   mirror applies to a cutscene actor;
6. otherwise **`mirrorsEnemies`** decides:
   - **false** (this instance owns its enemies) → `Odyssey.SetMinOneHitPoint(false)` then
     `KillCreature` — the engine's own lethal damage effect, so corpse, loot and OnDeath all
     run. The pin comes off first or the effect just floors health at 1 (the same order
     `CombatController` uses for an owner's death).
   - **true** (this instance mirrors somebody else's enemies) → **do not touch the local
     copy.** `PhantomHitHook.Dealt.Add(ownerId, 100000)` instead: the owner spends the sum as
     real damage, its copy dies its own real death, and the death comes back down the ordinary
     channel to everyone. This is the existing peer-damage path
     ([peer-damage-ledger.md](peer-damage-ledger.md)), not a new one. Poking the local copy
     would give one game a corpse and the other a living enemy, and the owner's next mirror
     pass would stand our corpse back up.

`mirrorsEnemies` is **not guessed**: it is `SharedBlock.OffsetDamageDealtEnabled`, which the
agent sets on exactly the instances whose enemies belong to somebody else — the same flag the
damage ledger and the phantom-neutrality hold already key off.

The ledger id is translated through `MaterialisedEnemies.ToOwner` first, because an enemy this
instance built for itself has a local spawn id that means nothing to the owner.

### The transport

One serial-edged slot in the shared block, `0x3328..0x33B0` (version 39), mirrored in
`KotorGameBridge` the way every other block region is:

```
0x3328 serial   (agent -> plugin, written LAST)
0x332C kind     0x3330 target id   0x3334 amount
0x3338 x  0x333C y  0x3340 z       0x3344 text[32]  (tag or resref)
0x3364 applied  (plugin -> agent)  0x3368 status    0x336C detail[64]
```

The **detail string is not decoration**. A cheat that cannot be applied has to say *what it
saw* — "0x2A is a peer's phantom, not an enemy", "no creature tagged 'foo' here", "'x' did not
spawn (failure 7; wrong resref?)" — or a guard that fails safe is indistinguishable from a
guard that silently does nothing, and the whole value of a staging command is that an
unattended run can trust the answer. The plugin writes the same sentence to its log; the agent
prints it on screen once, when the applied serial matches the one it filed.

Deliberately one request in flight: these are typed by a human a few times a minute, and a ring
would add ordering questions to a channel whose value is that its answer arrives before the
next line is typed. The agent gives up after **3 seconds** and says so — the executor runs on
the SwapBuffers frame hook, which is dormant behind the main-menu movie, so a cheat typed at
the menu would otherwise wait forever and wedge the single slot behind it.

### No new engine calls

Every cheat routes through calls the plugin already made and `tools/callaudit.py` already
checks: `SetMinOneHitPoint`, `GetMinOneHitPoint`, `SetCurrentHitPoints`, `GetMaxHitPoints`,
`IsPlot`, `KillCreature` (→ `ApplyDamageEffect`), `ReviveCreature`, `SetRaiseable`, `SnapTo`,
`SpawnItemOnFloor`, `ResolveServerObject`, `CreatureFinder.FindByTag`, `TryReadPartyRoster`.
`callaudit.py` reports **122 declarations, 122 agreeing with the binary, 0 disagreeing** after
this change — because the count did not move.

For the record, the calls that *would* have been needed had we rewritten the retail cheats
were looked up and their argument sizes checked against the binary's own `ret` immediates
(the oracle described in [engine-api.md](engine-api.md)), and then not used:

| Candidate | Address | Callee pops | Would have been |
| --- | --- | --- | --- |
| `CSWSCreature::GetGold` | `0x004EDD60` | `ret` (0) | credits |
| `CSWSCreature::SetGold` | `0x004EDD90` | `ret 4` | credits |
| `CSWSCreature::ReceiveExperience` | `0x004EF930` | `ret 4` | XP |
| `ExecuteCommandGiveGoldToCreature` | `0x0053E690` | `ret 8` | credits, via the script VM |
| `ExecuteCommandGiveXPToCreature` | `0x0053E750` | `ret 8` | XP, via the script VM |
| `ExecuteCommandRevealMap` | `0x00547240` | `ret 8` | map, via the script VM |
| `CSWSAreaMap::SetEntireMapExplored` | `0x00578FA0` | `ret` (0) | map, direct — `this` is `*(CSWSModule + 0x218)`, read straight out of `ExecuteCommandRevealMap`'s own instruction stream |
| `CVirtualMachine::StackPushVector` | `0x005D1050` | `ret 12` | pushes the vector by value (three floats) |

They are recorded here so the next person does not repeat the lookup, not because anything
calls them.

---

## 4. NEEDS-LIVE

Everything below fails safe: no effect, or a logged line naming what it saw. None can crash.

⚠ **Typing in the console needs a human.** Injected keyboard input does not reach this game
at all (docs/live-verification.md), so an unattended run cannot press `~`. For the cheats
themselves that is solved: `pwsh tools\blockpoke.ps1 -ProcId <pid> cheat god 1` (and
`topup`, `standup`, `slay <decimal id>`) writes the same channel the console does, and the
bare `cheat` prints the plugin's status **and its detail sentence** — which is the answer
worth reading, because a refusal names the rule and the object id. Only the checks about the
console UI itself (1–5) genuinely need somebody at the keyboard.

| # | Item | Exact live check | If it fails |
| --- | --- | --- | --- |
| 1 | The engine command table read (`0x007A8AB8` / count `0x007B8FB8`) | Press `~` once, then `Select-String 'console: completion has' kotorcoop-plugin.log`. Expect `35 engine console verbs` or thereabouts. | A line reading `console table count reads N - not walking it` means the count dword is wrong; completion falls back to co-op verbs only. |
| 2 | Engine verbs really do complete | In the console type `giv` then Tab, repeatedly. Expect `givecredits → giveitem → givemed → giverepair → givecomspikes → givesecspikes → givesitharmour`. | If Tab does nothing, item 1 failed. |
| 3 | The argument marker renders | Type `connect 1.2.3.4 ` (trailing space). The hint row should read `connect <ip\|#n> [password]  <- [password]`. | If it runs off the right edge, lower `MaxHintLength` in `ConsoleCompletionEngine`. |
| 4 | Escape dismisses | With a hint showing, press Escape. The hint should vanish; typing a character brings it back. | If Escape also closes the console, that is the engine's behaviour and acceptable — note which. |
| 5 | History survives a restart | Type `status`, close the console, quit, relaunch, press `~`, press Up. Expect `status`. Then `Get-Content kotorcoop-console-history.txt` and confirm **no** `password` line is in it. | A `could not write the command history` line in the plugin log names the exception. |
| 6 | `god` | `god` → "god is OFF". `god on` → "god ON - damage floors at 1 hp". Take a hit from an enemy and confirm health stops at 1. Check the peer's screen shows you at 1 too. | The pin address is already used by the combat mirror, so a failure here means the player pointer, not the offset. |
| 7 | `topup` / `standup` | Get hurt, `topup` → "hit points 7 -> 40 (max 40)". Get downed (see death-test staging), `standup` → "standing up with 10 hp" and the body stands. | `revive refused (failure N)` names the VM step. |
| 8 | `slay` on the **host** | Stand near an enemy, `slay`. Expect `slaying the nearest enemy 0x.. (N.Nm away)` then `killed 0x..`, a real corpse with loot, and the client's plugin logging `combat: killed 0x.. on the owner's authority`. | Falls back to `kill refused (failure N)`. |
| 9 | `slay` on the **client** | Same, on the joining instance. Expect `0x.. is the host's; sent the kill to them` and then the enemy dying on **both** screens through the normal mirror. This is the single most important check here — it is the authority split. | If the enemy dies only on the client, the ledger id translation is wrong. |
| 10 | `slay` refusals | `slay <the peer's player name>` → "their character is theirs, not yours to kill". Aim it at a companion's tag (e.g. `slay Trask`) → `0x.. is a party companion`. | Both are logged lines; a silent no-op is a bug. |
| 11 | `tp` / `goto` | `where` to read your coordinates, `tp <x+5> <y>`, confirm you moved and the peer's phantom of you followed. Then on the client `goto <host name>`; you should land on the host. **Walkmesh is not consulted** — if `tp` drops you through the floor, that is the known risk and `goto` is the safe form. | `refusing to teleport to the area origin` guards the one degenerate case. |
| 12 | `dropitem` | `dropitem g_w_blstrpstl001`. Expect `dropped 'g_w_blstrpstl001' as 0x..` and the item appearing as a real clickable object on the **other** player's screen; either player picking it up removes it for both. | `did not spawn (failure N; wrong resref?)` names the resref. |
| 13 | The cheat block region itself (`0x3328..0x33B0`) | `pwsh tools\blockpoke.ps1 -ProcId <pid> cheat god 1`, then the bare `cheat` — expect `applied` to catch up to `serial` and a detail of `god ON - damage floors at 1 hp`. | Silence from every cheat while the console still routes `status` means the region, not the verbs. |
