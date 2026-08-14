# Cinematics in a co-op session — what a movie actually does to this mod (2026-08-14)

The canonical case: leaving the Endar Spire and arriving on Taris. What happens today when a
cinematic plays in a live pair, what the host-authoritative design should be, and the exact
live test that settles it.

Verified against `swkotor.exe` (GOG retail 1.0.3; disk == memory, no ASLR, image base
`0x400000`) with `tools/disasm.py`, `tools/xrefs.py` and the sha256-matched symbol database
`tools/kotor1_0_3.db`. Nothing in the game install was modified and the game was not launched
for this write-up.

Tag key: **VERIFIED** = read from the binary here · **INFERRED** = deduced, not observed ·
**NEEDS-LIVE** = must be confirmed on the running pair.

Read alongside [menu-frame-hook.md](menu-frame-hook.md) (which first named the movie gate),
[transition-window-races.md](transition-window-races.md) (the four rules any transition-path
change must obey), [ingame-gui-transition.md](ingame-gui-transition.md) and
[dialogue-sync.md](dialogue-sync.md).

---

## Headline

**A KOTOR movie does not play alongside the game. It replaces it, and it takes this plugin
with it.**

`CClientExoAppInternal::WaitForMovie 0x005F6F20` spins the **main thread** in a loop
(`0x005F7063`–`0x005F70B5`) that runs nothing but `Global::messagepump 0x00401510`, once every
500 iterations, until the Bink player says the movie is done. Until it returns:

* **the frame loop does not turn over.** `Global::WinMain`'s iteration is stuck wherever the
  movie was entered — inside the client `MainLoop` at `0x004046BD` for a transition, or inside
  the server `MainLoop` at `0x00404757` for a script's `PlayMovie`. Either way, the *other* one
  is not reached and the iteration does not complete;
* `GDI32!SwapBuffers` is never called — so the plugin's `FrameHook` (the IAT detour at
  `0x0073D00C`) does not fire, which is where **every** world controller runs;
* `MainLoopHook`'s call-site stub at `0x004046BD` runs *before* the `MainLoop` that then
  blocks, so it does not fire again either.

So during a cinematic the plugin is not idle — it is **unreachable**. The only thing of ours
still moving is the worker thread (`PluginHost.WorkerLoop`, 16 ms) bumping the shared-block
heartbeat, and the external agent, which is a different process entirely and keeps reading
memory and pumping UDP the whole time.

The second headline is the lever that makes the fix cheap: **the engine already has a
per-transition movie queue, and this mod's travel path already drains it.**
`CClientExoAppInternal+0x2D8` is a `CExoArrayList<CExoString>` named `move_to_module_movies` in
the symbol database; NWScript `StartNewModule` fills it and
`CServerExoAppInternal::StartNewModule 0x004BA920` plays it — and `Odyssey.TravelToModule`, the
warp path `TravelController` uses to pull a peer along, goes through **that same
`StartNewModule`**. Adding one `AddMoveToModuleMovie 0x005EDB50` call per movie before the warp
makes the peer's transition play exactly the movies the owner's did, through the engine's own
code, with no new blocking call and no new hook.

The third headline is what the game files say about the case that was actually asked about, and
it is worse than "the second player misses a movie". **Endar Spire → Taris is not one
transition, it is two, and it routes through the stunt module `STUNT_00` — which the session's
follow logic deliberately refuses to enter.** So the partner misses `01c.bik`, misses the whole
vision cutscene, misses `01f.bik`, spends all of it alone in a module the story has already
left, and is then teleported into an apartment on another planet. Every part of that is a
consequence of machinery that is individually correct. **§4.3.**

---

## 1. The engine facts

### 1.1 The two kinds of cinematic

| Kind | Entry point | Blocks the main thread? |
| --- | --- | --- |
| **Pre-rendered movie** (`Movies\*.bik`) | `CClientExoApp::PlayMovie 0x005EDB30`, or the module-transition queue | **Yes**, inside `WaitForMovie` |
| **In-engine cutscene** (DLG camera/animation nodes, `ActionStartConversation`, module OnEnter) | `CSWSObject::StartDialog 0x004CF490` and the action queue | No — it is ordinary simulation, ticked frame by frame |

They need completely different treatment, and only the first one is a problem. §4 covers the
second.

### 1.2 The movie player and its "playing" flag — VERIFIED

`CClientExoApp::IsMoviePlaying 0x005EDB40` is a three-hop thunk chain that bottoms out in one
instruction:

```
0x005EDB40  mov ecx,[ecx+4]        ; CClientExoApp -> CClientExoAppInternal
0x005EDB43  jmp 0x005F2BD0

0x005F2BD0  mov ecx,[ecx+0x12C]    ; movie_player  (CExoMoviePlayer*)
0x005F2BD6  test ecx,ecx
0x005F2BD8  jne 0x005F2BDD
0x005F2BDA  xor eax,eax ; ret      ; no player -> not playing
0x005F2BDD  jmp 0x0070C1F0

0x0070C1F0  mov ecx,[ecx]          ; CExoMoviePlayer -> CExoMoviePlayerInternal
0x0070C1F2  jmp 0x00404DD0

0x00404DD0  mov eax,[ecx+0x30]     ; the playing flag
0x00404DD3  ret                    ; <- bare ret: argument size is ZERO
```

That bare `ret` is the argument-size proof `engine-api.md` demands: `__thiscall`, no arguments,
`param_size_bytes = 0` in the DB, and `tools/callaudit.py` agrees (`args=1 needs=0 ret(0)`).
Every hop is a load with a null guard, so the call is safe on any frame, world or menu.
`CExoMoviePlayerInternal::StopMovie 0x00404DE0` clears the same `+0x30`.

### 1.3 `WaitForMovie 0x005F6F20` — the blocking loop — VERIFIED

```
loop 0x005F7063:
  movie = [clientInternal+0x12C]
  if (!movie)                              break
  if (CExoMoviePlayer::IsMoviePlaying()!=1) break
  if (spin == 500 && !alreadyCancelled) {
      Global::messagepump 0x00401510                  ; the ONLY thing that runs
      if ([clientInternal+0x254] == 1) {              ; a skip was seen
          CExoMoviePlayer::CancelMovie 0x0070C1E0(1,0)
          alreadyCancelled = 1
      }
      spin = 0
  } else spin++
  goto loop
```

On the way out it calls `CClientExoApp::RemoveMoveToModuleMovies 0x005EE380` and, if
`[clientInternal+0x200]` is set, `CGuiInGame::SetGlobalUnpauseableState 0x62B410(gui, 0)` —
the release of the pause lock `PlayMoviesInExoArrayList` took on the way in (`0x00602B09`,
`SetGlobalUnpauseableState(gui, 1)`).

The Bink decode itself is on a **dedicated worker thread**, `Global::MoviePlaybackWorker
0x00405B30` (`__stdcall`, one arg), which parks on `WaitForSingleObject([internal+4], INFINITE)`
and, per movie, runs `InitializeMovie 0x004053E0` → `PlayMovieLoop 0x00404C80` →
`StopMovie 0x00404DE0`. `PlayMovieLoop` is its own spin loop —
`BinkDoFrame`/`BinkBufferLock`/`BinkCopyToBuffer`/`BinkBufferBlit`/`BinkNextFrame`/`BinkWait`
with `Sleep(1)`, plus its own `PeekMessageA`/`DispatchMessageA` pump. The main thread is
started on it by `CExoMoviePlayer::PlayMoviesAsync 0x0070C200` (which sets the playing flag at
`0x00405EBF` and `SetEvent`s the worker) and then goes straight into `WaitForMovie`. So
"asynchronous playback plus a synchronous wait" is the shape; the wait is what makes it look
blocking, and it is total.

⚠ `CExoMoviePlayerInternal::PlayMovieLoop 0x00404C80` is recorded in the DB with
`param_size_bytes = 0` and the binary ends it with `ret 4` at `0x00404DC0`, with one argument
pushed at both call sites. Trust the binary. (This is a DB row for `tools/dbaudit.py`, not
something the plugin calls.)

**Skipping is entirely local, and there are two independent routes into it — neither of which
is observable by us.**

* `Global::MovieWinMessageHandler 0x00405190` is the **movie window's** WndProc, pumped by the
  worker thread. `WM_KEYDOWN` with `VK_ESCAPE` (`0x00405221`), and left- or right-click
  depending on `ClientOptions+0x54` (`0x004052E8` / `0x00405342`), each reach
  `CExoMoviePlayerInternal::CancelMovie 0x00404C40(0, 0)`, which sets the abort request
  `[internal+0x14] = 1`; `PlayMovieLoop` tests it at `0x00404D3F` and bails out of the frame
  loop. `WM_ACTIVATEAPP` with `wParam == 0` cancels everything (`CancelMovie(1, 1)`).
* `WaitForMovie`'s own loop additionally polls `[clientInternal+0x254]` and calls
  `CExoMoviePlayer::CancelMovie 0x0070C1E0(1, 0)` — cancel-all — when it sees 1.

There is no completion callback anywhere. The worker thread simply `PostMessageA`s the main
window (`0x007A39D8`) with the private messages `0x409` (fullscreen restore) and `0x40A`
(`Global::RestoreResolutionFromMovie 0x00403DE0`), handled in `Global::WinMessageHandler
0x00402800` at `0x00403326` / `0x00403316`. So there is no callback, no event, and no moment at
which our code could observe a skip while it was happening. **That single fact decides the skip
design in §3.2.3.**

Three callers, all of which mean "stop the world here": `RestartMoviesIfNecessary 0x005F9780`
at `0x005F97B3`, `PlayMoviesInExoArrayList 0x00602AF0` at `0x00602BA6`, and
`CClientExoAppInternal::MainLoop 0x00602EB0` at `0x0060300F`.

The MainLoop site is worth reading in full, because it is the one that catches an
*asynchronously* started movie:

```
0x00602FF9  mov ecx,[esi+0x12C]        ; movie player
0x00602FFF  cmp ecx,ebx                ; ebx = 0
0x00603001  je  0x00603016
0x00603003  call 0x0070C1F0            ; IsMoviePlaying
0x00603008  cmp eax,ebp                ; ebp = 1
0x0060300A  jne 0x00603016
0x0060300C  push ebp                   ; 1
0x0060300F  call 0x005F6F20            ; ** WaitForMovie(1) **
```

It sits **inside** MainLoop's in-world block (`0x00602F0F`–`0x00603254`), which needs a client
module and a client player creature. At the **main menu** that block never runs, which is why
the menu's looping movie plays without blocking anything and `MainLoopHook` keeps firing there
— the observation `menu-frame-hook.md` recorded and could not explain.

### 1.4 NWScript `PlayMovie` — action **733** — VERIFIED

The dispatcher is `CSWVirtualMachineCommands::ExecuteCommand 0x0052C0D0`: a bounds check against
`0x304` (772 commands) and an indirect call through the table at `[this+0x0C]`, allocated in
`CSWVirtualMachineCommands::InitializeCommands 0x0054C960` (`malloc(0xC10)` = 772 slots) and
filled with `mov [reg + id*4], handler` stores. Walking those stores gives the ids directly —
the store for `PlayMovie` is the literal `C7 82 74 0B 00 00 D0 0E 54 00` = `mov [edx+0xB74],
0x00540ED0`, and `0xB74/4 = 733`. Calibrated against known ids (0 `Random`, 1 `PrintString`,
8 `ExecuteScript`, 25 `GetEnteringObject`).

| id | handler | symbol |
| --- | --- | --- |
| 203 | `0x00542410` | `SetAreaTransitionBMP` |
| 503 / 507 | `0x0052E890` / `0x0052E950` | `CutsceneAttack` / `CutsceneMove` |
| **509** | `0x00544390` | **`StartNewModule`** |
| 719 / 720 | `0x00546010` / `0x005460D0` | `SetGlobalFadeIn` / `SetGlobalFadeOut` (five floats each: wait, length, R, G, B) |
| **733** | `0x00540ED0` | **`PlayMovie`** |
| 760 | `0x00547E30` | `HoldWorldFadeInForDialog` |
| 768 / 769 / 770 | `0x005481E0` / `0x00548210` / `0x005482D0` | `IsMoviePlaying` / `QueueMovie` / `PlayMovieQueue` |

`CSWVirtualMachineCommands::ExecuteCommandPlayMovie 0x00540ED0` (`__thiscall`, `ret 8`):

```
0x00540EEC  CExoString ctor
0x00540F04  CVirtualMachine::StackPopString 0x005D1080     ; the movie name
0x00540F0B  on failure -> return 0xFFFFF82F
0x00540F48  CClientExoApp::ResetDriveAcceleration 0x005EDBA0
0x00540F6B  CClientExoApp::HideLoadScreen 0x005EDA90(0, 1)
0x00540F87  CClientExoApp::PlayMovie 0x005EDB30(&name, &?, 0, 0)   ; four args, 16 bytes
```

and `CClientExoApp::PlayMovie 0x005EDB30` → `CClientExoAppInternal::PlayMovie 0x00602CF0`,
which appends the name to the SAME `+0x2D8` list and calls `PlayMoviesInExoArrayList 0x00602AF0`
— which calls `WaitForMovie`. **So a script's `PlayMovie` blocks the VM, the server tick and
therefore the whole process, right where it stands.**

There are three sibling script commands, all present in this build:
`ExecuteCommandQueueMovie 0x00548210`, `ExecuteCommandPlayMovieQueue 0x005482D0` and
`ExecuteCommandIsMoviePlaying 0x005481E0` (the last one is the script-visible half of §1.2;
`movie_queue` is a second list at `CClientExoAppInternal+0x2E4`).

### 1.5 The module-transition movie queue — VERIFIED, and the design lever

| Fact | Evidence |
| --- | --- |
| `CClientExoAppInternal+0x2D8` is `move_to_module_movies : CExoArrayList<CExoString>` | the DB's own `offsets` row; `CClientExoApp::GetMoveToModuleMovies 0x005EDB60` is literally `mov eax,[ecx+4]; add eax,0x2D8; ret` |
| `CExoArrayList` layout is `+0x00 data`, `+0x04 size`, `+0x08 capacity` | DB `offsets`, consistent across all 130 instantiations |
| `CExoString` layout is `+0x00 char*`, `+0x04 length` (length **includes** the null) | DB `offsets` + the codebase's own CExoString length trap |
| `CClientExoApp::AddMoveToModuleMovie 0x005EDB50` appends one name to that list | `0x006027A0`: `lea eax,[ecx+0x2D8]; call AddMovieToExoArrayList 0x005FBBF0; ret 4` |
| NWScript `StartNewModule(module, waypoint, movie1..movie6)` queues **six** movies | `CSWVirtualMachineCommands::ExecuteCommandStartNewModule 0x00544390` calls `0x005EDB50` six times, at `0x0054459E`/`AA`/`B6`/`C2`/`CE`/`DA` |
| The queue is drained by the module load | `CClientExoApp::PlayLoadModuleMovies 0x005EDB20` has exactly **one** caller: `CServerExoAppInternal::StartNewModule 0x004BA920` at `0x004BA9CA` |
| …and playback has exactly two entry points in the whole binary | `xrefs 0x0070C200` (`CExoMoviePlayer::PlayMoviesAsync`) = `0x00602735` (the transition queue) and `0x00602B92` (`PlayMoviesInExoArrayList`, i.e. `PlayMovie` / `PlayMovieQueue` / the script path). Nothing else in the game plays a movie. |
| Two guards make a movie silently not happen | `PlayLoadModuleMovies 0x00602650` early-outs on a null movie player (`+0x12C`) **and on `[clientInternal+0x1F8]`**; `PlayMovie 0x00602CF0` early-outs on `+0x1F8` too. `+0x1F8` is a global movie suppression latch — a movie that "did not play" on one instance and did on the other will show up here first. |
| …which is driven by the server's own frame loop | `xrefs 0x004BA920` = one site, `CServerExoAppInternal::MainLoop 0x004BABB0` at `0x004BAD93` |
| A save carries its pending transition movies | `CSWGuiSaveLoad::LoadPCAutoSave 0x006CA250` calls `AddMoveToModuleMovie` six times (`0x006CA8B8`–`0x006CA91E`) |
| An area event can queue one too | `CSWSArea::EventHandler 0x0050D6C0` at `0x0050DA9F` |
| `AddMovieToExoArrayList 0x005FBBF0` refuses a name it cannot use (returns 0) | early-out at `0x005FBC2C` after a string check; `PlayMovie` tests the result at `0x00602D17`. That it validates the *file* is **INFERRED** — the path-building constants were not fully traced. |

Ordering worth knowing, because it decides what state the movie plays *over*.
`CServerExoAppInternal::StartNewModule 0x004BA920` runs a prologue before it ever gets to the
movies: the load-screen hint (`GetNextLoadScreenHintSTRREF 0x005EE090`,
`SetLoadScreenHint 0x005EDF00`, `SetLoadScreenHintUpdateTimer 0x005EE040`),
`CClientExoApp::SetCanSendDialog 0x005EDC80(0)`, `CGuiInGame::SetGlobalDialogState 0x0062EC60`
and `CloseDialog 0x006332B0` (any conversation is shut down), `CExoSound::StopAllOneShots
0x005D5E70` — **and then** `PlayLoadModuleMovies` at `0x004BA9CA`.

`PlayLoadModuleMovies 0x00602650` itself pauses the world timer
(`CServerExoApp::GetWorldTimer 0x004AEDE0` → `CWorldTimer::PauseWorldTimer 0x004ADFF0`), takes
the unpauseable lock, clears input events, shows the load screen, changes resolution
(`Global::ChangeResolutionForMovie 0x00403CF0`), and only then starts playback.

⚠ Note what that prologue is **not**: it is not a server teardown. `CServerExoApp` and
therefore `APP_MANAGER+0x08` survive the whole movie, so the agent's
`GameLifecycle.GetPhase()` — which separates "main menu" from everything else purely on the
server object existing — does **not** flip to `MainMenu` during a cinematic. That was the
obvious way for this to have gone badly wrong and it does not. **VERIFIED** by resolving each
call in the prologue against the symbol database.

The other half of the prologue is a real co-op fact: a transition **closes the conversation**
(`SetGlobalDialogState`, `CloseDialog`). If either player is mid-dialogue when the other
triggers a transition that pulls them along, that dialogue ends on the moved instance and not
on the other. `DialogueSpectateController`'s own epoch invalidation covers the follower;
whether the OWNER of a conversation being yanked mid-line leaves the spectator's copy open is
**NEEDS-LIVE**.

### 1.6 Fades, cutscene chrome, and one correction

`CGuiInGame::StartGlobalFade 0x0062ABF0` is `__thiscall`, **`ret 0x10`** — four arguments,
forwarded straight to `CSWGuiFade::SetFade 0x006244E0` (direction, wait, length, and a pointer
to three floats = the fade colour). Two readable predicates come with it:

* `CSWGuiFade::IsFadeDone 0x006244B0` — `fade+0x6C <= 0` or `fade+0x64 + fade+0x68 <= fade+0x6C`;
* **`CGuiInGame::IsGlobalFadeObscuring 0x0062DED0`** — `this->[0x6C]->[0x4C] > 0.001f`, i.e.
  "the screen is currently blacked out". That is the closest thing the engine has to "a
  cinematic fade is on screen", and it is a pure read.

There is **no cutscene manager class** in this build. KOTOR expresses a cutscene as
dialogue-with-camera plus the letterbox (`CSWGuiDialogLetterbox`, `IsFadeDone 0x006A6DA0`,
`SetFade 0x006A7620`) plus per-object flags (`Gob::IsInCutscene 0x0043E8A0`,
`CSWCAnimBase::SetInCutscene 0x0069D710`), driven by the `CutsceneAttack`/`CutsceneMove` script
actions (ids 503/507 → `CSWSCreature::AddCutsceneAttackActions 0x004F9FB0` /
`AddCutsceneMoveActions 0x004FA050`). **INFERRED**, but consistent with everything found.

⚠ **Correction to [ingame-gui-transition.md](ingame-gui-transition.md)**, which reads
`0x00641980(module)` in `MainLoop` as "the dialog/cutscene flag". It is not.
`0x00641980` is **`CSWCModule::GetMiniGame`**: `[module+0x48]` is `area` (`CSWCArea*`) and
`[area+0x264]` is `mini_game` (`CSWMiniGame*`), both named in the DB's own offsets table. The
MainLoop branch it gates (`SetInputClass(1,1)` instead of the HUD bring-up) is **minigame mode**
— the swoop race and turret sequences — not conversation mode. It happens not to change that
document's recipe, but anyone reasoning about cutscenes from that line would be reasoning about
the wrong thing.

**The consequence that matters.** `Odyssey.TravelToModule` — the path `TravelController` uses
to pull a peer into the host's module — is `SetMoveToModuleString 0x004AECD0` +
`SetMoveToModuleStartWaypoint` + `SetMoveToModulePending 0x004AECC0(1)`, and the engine
consumes that pending flag in `CServerExoAppInternal::MainLoop` by calling **the very
`StartNewModule` that drains the movie queue**. So the co-op warp is not a different kind of
transition that skips movies; it is the same transition with an empty queue. Filling the queue
first is all that is needed. **VERIFIED statically; NEEDS-LIVE.**

---

## 2. What happens today, in a live pair

### 2.1 The plugin

Traced in the code, not guessed:

* `PluginHost.OnFrame` — the epoch gate, the pre-store flush, `TravelController.Execute`, the
  phantom / enemy / party / encounter / door / item / journal passes, the dialogue spectate
  publish — **all** run on `FrameHook`, the `SwapBuffers` IAT detour on the slot at
  `0x0073D00C`. `docs/re/frame-hook.md` records that both swap sites are gated behind
  `!IsMoviePlaying`, and §1.3 shows the thread never gets back to them anyway.
* `PluginHost.MenuFrame` — the Co-op button, the native browser, `MenuLoadController`, the
  password prompt, the console assist — runs on `MainLoopHook`, whose stub is the rewritten
  `call` at `0x004046BD` and therefore runs immediately **before** the MainLoop that blocks.
* **Nothing in the plugin looked at a movie at all** before this pass. `grep -rin movie
  src/` returned only comments about the *menu* movie explaining why the swap hook is dormant
  there.

So, for the duration of a movie: no epoch gate, no pre-store flush, no travel execution, no
phantom drive, no enemy mirror, no damage forwarding, no dialogue publish. Before this pass the
only trace a cinematic left anywhere was one line from `PluginHost.WorkerLoop`, which fires on
any movie longer than two seconds and reads like a defect report:

```
WARNING: frame hook installed but has not fired for ~2s
```

It is not a defect; it is the correct reading for the whole duration. The line now says so
(`... (normal while a movie or a module load holds the main thread)`), because a reader who
does not know that will spend the length of a movie chasing a hook that is fine. The heartbeat
in the same loop keeps ticking, so the agent still sees the plugin as alive — correctly.

### 2.2 The session

The agent is a separate x64 process. It keeps its UDP pump, its retransmit queues and its
memory reads running throughout. What it reads while the host is inside a movie is exactly the
poison `transition-window-races.md` catalogues: the module label, the player pointer and the
census all belong to a world that is being torn down or built. The four safe patterns already
in force are what stop that from being a crash — in particular pattern 3 (`TryReadCurrentModule`
read in the same breath as the census) and pattern 4 (identity from the engine).

But pattern 1 and pattern 2 are both enforced **by the plugin, on the frame hook**:
`SharedBlockView.ClearAgentWorldInputs()` runs from `OnFrame`'s epoch gate. During a movie that
gate cannot run. It runs on the first frame *after* the movie, before any other controller —
so the invariant holds; it is just delayed by the length of the movie. **INFERRED, NEEDS-LIVE:**
the window is longer than any previously tested, and it is the single most likely place for a
new fault.

### 2.3 The other player

Their world is untouched. They keep playing, at full speed, in the old module, while the host's
half of the session goes silent for the length of the movie. Their copy of the host — the
phantom — freezes at the last position the host's agent published, because the host's agent has
nothing new to read. Enemies the host owns stop being republished, so the client's mirror holds
its last state (levels, not events: correct by construction).

### 2.4 The shape of any transition cinematic

**INFERRED end to end; every step below is a NEEDS-LIVE claim.** For the *canonical* case
specifically — which is worse than this, because of the stunt module in the middle — see §4.3.

1. Whichever player reaches the trigger runs the script; their engine queues the transition
   movie and blocks in `WaitForMovie`.
2. Their partner sees the world carry on and their partner's body stand still.
3. The mover's engine loads the new module. Their plugin's first frame back invalidates the
   epoch, clears the agent inputs and starts again in the new world.
4. Their agent republishes with the new module label. The session issues a travel command to
   the partner.
5. The partner's `TravelController` warps them to the same module — **with no movie, no fade
   and no queued cinematic**, because nothing put one in their queue.

Net: **one player sees the cinematic, the other sees a loading screen; they arrive seconds
apart.** That is the defect. Whether it is *only* that — whether the session survives the
window at all — is exactly what the test in §5 is for.

⚠ There is an accidental-success case that will make this look fixed when it is not: if both
players are standing in the same trigger, both engines fire the script independently, both
queue the movie, and both play it at roughly the same time — with no session involvement at
all. That is the *adopt* pattern from `click-to-join-and-vo.md` repeating itself: the path that
needs no coordination always works, and the path that needs it never does. Any test that only
ever walks both players in together proves nothing. §5 step 4 deliberately moves one player.

---

## 3. The correct co-op behaviour

### 3.1 The principle

**The cinematic is not the synchronisation point. The module is.**

Both players should see the same cinematic, and this mod already has the machinery to make
them arrive together — `WorldTransfer` for the world itself, `TravelController` +
`Odyssey.TravelToModule` for the move, and the epoch gate for everything that must be forgotten
in between. What is missing is not a rendezvous; it is the movie name.

### 3.2 The design

**Host-authoritative, event-shaped, replayed locally.**

1. **The owner announces, the host relays.** Whichever instance's engine actually reached the
   trigger publishes "these movies are about to play" — the names, not the frames. That is the
   same shape as every other world-truth channel here: a monotonic serial, retransmitted until
   acked, relayed by the host. It is *not* a new authority model; a cinematic is world truth
   like a door state.

   The pre-warning exists and is readable: `move_to_module_movies` fills on the frame the
   script calls `StartNewModule`, and the plugin is still running on that frame. That is the
   window in which the announcement must be made, because after it there is no window at all.

   **Read the queue, do not predict the movie.** §4.4 shows why this is not a stylistic
   preference: the planet takeoff and landing movies are chosen at runtime from a `string`
   variable, so their names never appear as literals anywhere a static scan could find them.
   `move_to_module_movies` is the one place the answer is always correct — for the 34 literal
   cases, for the runtime-selected ones, and for a mod's own cinematic.

2. **The peer plays the movie through its own transition, not through a new call.** Extend the
   travel command with the movie list. `TravelController.Execute` calls
   `AddMoveToModuleMovie 0x005EDB50` once per name and then `TravelToModule` exactly as today;
   `CServerExoAppInternal::StartNewModule` plays them as part of the peer's own load. No new
   blocking call, no new hook, no reimplementation of the movie player — the engine's own
   idiom, on the engine's own path (§1.5). Note the queue is a **list**, not a single name:
   `06a`+`05_1C` and `17a`+`11a` are real two-movie transitions in the base game, so the wire
   field has to be a list too.

   This also degrades correctly for mismatched installs: `AddMovieToExoArrayList` refuses a
   name it cannot use and the transition simply proceeds without the movie — and §4.4 shows
   the retail game already relies on that path for its own dead names. Same per-channel
   graceful-degradation rule `mod-compatibility.md` applies elsewhere.

   **Stunt modules are the case that makes this design earn its keep.** `CoopAgent` already
   refuses to follow a peer into a `stunt*` module, correctly (§4.3), and it should keep
   refusing. What it must additionally do is **accumulate** the movie lists of every transition
   the owner makes while it is declining to follow, and hand the whole accumulation to the
   travel command it finally does issue. On the canonical chain that means the follower's one
   warp into `tar_m02af` carries `["01c", "01f"]` — both movies, in order, played by the
   follower's own `StartNewModule` — and the follower never enters `STUNT_00` at all. The
   private vision stays private; the cinematics that bracket it do not.

3. **Skipping is local. Do not mirror it, and do not wait for everyone.** Argued rather than
   asserted:

   * *Waiting for everyone* is not implementable on this engine without new risk. To hold a
     movie open past its end you must write into the Bink player's state from outside the loop
     that owns it — and the thread that would do the writing is the thread the loop is on. The
     only alternative is a second thread poking `CExoMoviePlayerInternal`, which violates the
     main-thread rule this whole plugin is built around.
   * *Mirroring the skip* cannot be delivered in time. The skip is consumed inside
     `WaitForMovie` (§1.3); the skipper's plugin is blocked and cannot send, and the peer's
     plugin is blocked and cannot receive. Any "I skipped" message is delivered after both
     movies have already ended, where it means nothing.
   * *Neither* is correct, and it is not a compromise. Because both sides start their own copy
     from the same session event and each transition is independent, a player who skips simply
     arrives in the new module first — a state this mod already handles every day (a peer in
     a different module: no phantom, module-gated channels off, converge on arrival). The
     player who watches the whole movie arrives late and the pair re-converges with no special
     code. **The right rendezvous is arrival, and it already exists.**

4. **Never express "a cinematic is playing" as a live shared-block flag.** The plugin cannot
   write one while a movie plays, because it is not running; a flag would read 0 for the entire
   window in which it was supposed to mean something, and then flip after the fact. Cinematic
   state has to be expressed as a *pre*-announcement (the queue, above) or a *post-hoc*
   reconciliation (the epoch gate). This is why this pass deliberately ships no block field —
   see §6.

5. **Not in scope, and say so.** Mid-module `PlayMovie` calls with no transition (the Leviathan
   capture, the Star Forge arrival) have no travel command to ride on and would need their own
   event. The design above extends to them — announce, then have the peer call
   `CClientExoApp::PlayMovie 0x005EDB30` — but that *is* a new blocking engine call on the
   peer's main thread, and it should not be added on the same pass as the transition case.

### 3.3 In-engine cutscenes are a different problem, and mostly already solved

`DialogueSpectateController` does not send a transcript. It opens **the same DLG** against the
peer's own copy of the NPC (resolved by tag, because object ids are per-module-load) and drives
it to the same entry node through the engine's own entry producer. So the peer's own engine
evaluates the node — which means the node's camera angles, its animations, its voice-over and
its lipsync all run locally, from the same file. A dialogue-driven cutscene is therefore
*already* the same cutscene on both screens, not a description of one. The enemy/party presence
channels carry the actors (`ProtocolConstants` v13 added the plot flag precisely so cutscene
actors replicate presence while staying out of the combat channel).

Two open questions, both **NEEDS-LIVE**, both out of scope for this pass:

* **Double-firing.** A cutscene started by a script rather than a click (a module OnEnter, an
  `ActionStartConversation`) can fire independently on both machines. The "one conversation at
  a time" gate then refuses whichever one is not the owner — which is the right outcome only if
  the owner is the instance whose engine genuinely reached the trigger. The encounter channel
  had exactly this shape and solved it by having the *owner* fire for the peer's phantom
  (`client-encounters.md`); the same answer probably applies.
* **The camera.** A spectating player's camera follows their own character, not the
  conversation's. `SpectateController` already knows how to hand the camera to another creature
  through `CSWCModule::SetCameraFollow 0x0063F9D0`, so the mechanism exists; whether the DLG's
  own camera nodes take over on the follower has not been measured.

---

## 4. The base-game inventory

### 4.1 What is actually on this machine

`C:\GOG Games\Star Wars KOTOR (GOG)\Movies\` — **61 `.bik` files**, 592 MB:

```
01a 01c 01f 01g 02 03 05 05_1c 05_2a 05_2c 05_3a 05_3c 05_4a 05_4c 05_5a 05_5c
05_7a 05_7c 05_8a 05_8c 05_8e 05_9 05r 06a 07_1 07_2 07_3 07_4 08 09 0a 0b 0c 0d
11a 11b 17 17a 22a 22b 23a 23b 26a 26b 31a 33 43 50 50b 51 51b 54 54b 55 56 56b
LIVE_1a Live_1c biologo leclogo legal
```

`legal`, `biologo` and `leclogo` are the startup logos — their names are string literals at
`0x0074ECE8`/`0x0074ECEE`/`0x0074ECF6` in the binary, adjacent to `.\movies\55.bik` at
`0x0074ECD8` (the one hard-coded path in the engine, used by `PlayMovieQueue 0x00602D40`).
The numbered files are the plot cinematics; the engine indexes them through **`movies.2da`**
(`CTwoDimArrays::Load2DArrays_Movies 0x005C4450`, table name string at `0x0074BD24`), and
`CClientOptions::Set/GetMovieShown 0x0061D230`/`0x0061D3D0` tracks which have been seen under
the `"Movies Shown"` option key (string at `0x0074F900`) — that is what feeds the main menu's
`CSWGuiTitleMovies` gallery.

**The relevant consequence for co-op**: the seen/unseen bits are **per-install options, not
per-save**, so two players will disagree about them, and any future design must not let that
decide whether a movie plays. The movie name in the transition queue is the authority.

### 4.2 The Endar Spire → Taris chain

Read out of the real game files with `tools/kotorfiles.py` (KEY/BIF/RIM/GFF) plus an NCS
disassembler; module scripts ship **compiled only** (`end_m01aa_s.rim` holds `.ncs` and no
`.nss`), so the literals below come from each script's constant pool. Sources for the
`scripts.bif` scripts (`k_ren_visionland`, `k_inc_stunt`) are shipped and were read directly.

**It is five modules, not three, and the apartment is `tar_m02af`.**

```
end_m01aa ──door end_door10──▶ end_m01ab ──k_pend_pod01──▶ STUNT_00 ──k_ren_visionland──▶ tar_m02af ──door tar02_aptdoor──▶ tar_m02aa
   PlayMovie "01A"               (no movie)     StartNewModule       (vision)   StartNewModule            (no movie)
   on area OnEnter                              +movie "01c"                    +movie "01f"
```

| # | Movie | Shape | Fired by | Detail |
| --- | --- | --- | --- | --- |
| 1 | **`01a.bik`** (42 MB, the opening crawl) | **in-place `PlayMovie`** — module already resident | `k_pend_area01`, the `OnEnter` of `m01aa.are` | PC-only, first-entry guarded; also `SetGlobalFadeOut`/`FadeIn`, sets `K_CURRENT_PLANET = 5`, then `ActionStartConversation("m01aa_c01")` — the opening Trask scene |
| — | none | plain engine area transition | door `end_door10` in `m01aa.git` → `LinkedToModule = end_m01ab`, `LinkedTo = from_01a` | no `OnOpen`/`OnClick` script at all |
| 2 | **`01c.bik`** (6.7 MB, the escape pod) | **queued transition movie** | `end_pod.utp` `OnUsed = k_pend_pod02` (fade, gather the party, `JumpToObject` the pod) → conversation `end_pod.dlg`, whose `ReplyList[1].Script` is **`k_pend_pod01`** | `k_pend_pod01` is 0x46 bytes and does exactly one thing: `StartNewModule("STUNT_00", "", "01c", "", "", "", "", "")` |
| 3 | — | the vision | `STUNT_00`, area `stunt_eboqrts`, `OnEnter = k_scene_start` → `cut00_convers.dlg`, whose `StartingList` picks one of six entries by body type | **all six reply nodes run `k_ren_visionland`** |
| 4 | **`01f.bik`** (3.7 MB, waking on Taris) | **queued transition movie** | `k_ren_visionland` (source in `scripts.bif`; it is `ST_PlayVisionLanding()` from `k_inc_stunt` inlined) | the `K_CURRENT_PLANET == 5` branch: `StartNewModule("tar_m02af", "", "01f")`. The other branch is `StartNewModule("Danm13", "", "09")` |
| — | none | area OnEnter + plain door | `k_ptar_02af_en` closes `tar02_aptdoor` and reveals the map; the door links to `tar_m02aa` | |

Two traps for anyone re-deriving this:

* `to_tar_m02aa.utt` (`Type = 1`, area transition) exists as a **blueprint in both Endar Spire
  module archives and is instanced in neither GIT.** It is a leftover from a design where the
  Spire linked straight to Taris. Grepping for it finds a transition that does not exist.
* `tar_m02aa`/`tar_m02ab` — the modules the name suggests — fire **no movie at all**. The
  apartment is `tar_m02af`.

### 4.3 ⚠ What this chain does in co-op today — and it is worse than "the client misses a movie"

`CoopAgent`'s follow logic **deliberately refuses to follow the host into a stunt module**:

> *Stunt modules stage cutscenes. The host will be dumped back into a real module when its
> scene ends; following it in would trap this player in someone else's cinematic.*

That gate is correct, and `STUNT_00` is exactly what it is guarding against. But put it next
to the chain above and the actual co-op behaviour of the canonical case falls out:

1. One player uses the escape pod. Their engine queues `01c`, tears down `end_m01ab`, plays the
   movie with the main thread blocked, and loads `STUNT_00`.
2. The other player **does not follow** — the destination starts with `stunt`. They are left
   alone in `end_m01ab`, still playing, with a frozen phantom of a partner who is not there.
3. The mover's vision runs — a whole in-engine cutscene — then `k_ren_visionland` fires
   `StartNewModule("tar_m02af", "", "01f")`: a second queued movie, a second full stall.
4. Only *now* does the host's module read as something the follow logic will act on, and the
   partner is warped straight to `tar_m02af` — **with no `01c`, no vision, no `01f`, and no
   loading fade that means anything.**

So the honest statement of today's behaviour is not "the second player misses a cinematic". It
is: **the second player misses two movies and an entire scripted scene, spends that whole time
alone in a module the story has already left, and then teleports into an apartment on another
planet.** Every one of those is a consequence of machinery that is individually correct.

**INFERRED end to end** from the game files and the session code; this is precisely what §5
exists to confirm.

### 4.4 The rest of the base game, for scale

The full sweep found **34 distinct movies fired by `StartNewModule`** (the queued shape) and
**11 fired in place by `PlayMovie`**, plus exactly one `QueueMovie` (`"55"`, in
`k_stunt_end`, with `bAllowSeparateSkips = 1` — the only place the game itself contemplates
players skipping independently). Highlights of the queued set, all module transitions:

`02` (Bastila joins, → `tar_m02af`) · `03` (→ `stunt_03a`) · `05` (Taris bombardment,
→ `STUNT_06`) · `06a`+`05_1C` (→ `STUNT_07`, a **two-movie queue**) · `11a` (→ the turret
minigame `m12ab`) · `17a`+`11a` (Leviathan escape) · `17` (Leviathan capture) · `05_2A`
(Dantooine landing) · `05_8A` (Unknown World crash) · `31a` (Leviathan bridge) · `33`/`43`
(Star Forge) · `22A`/`22B` (Kashyyyk baskets) · `23a`/`23b` (Manaan submersible) ·
`50`/`50b`/`51`/`51b`/`54`/`54b`/`56`/`56b` (the endings).

The in-place set is smaller and more scattered: `01A` (above), `01g` (a swoop-track
heartbeat), `03`, `05_8E`, `26a`/`26b` (Manaan Hrakert Rift), and `0A`/`0B`/`0C`/`0D` — the
four Star Map hologram reveals (`k_hbas_playtat`/`playman`/`playkash`/`playkorr`). A
debug-only `k_qa` script plays all 45 and should be ignored.

**One category cannot be enumerated statically at all, and it matters.** The planet
takeoff/landing movies are chosen at runtime: `k_inc_stunt.nss`'s `ST_GetTakeOffRender()` /
`ST_GetLandingRender()` return a **string variable** selected from the `K_CURRENT_PLANET` /
`K_FUTURE_PLANET` globals (15 Dantooine `05_2c`/`05_2a`, 20 Kashyyyk `05_4c`/`05_4a`,
25 Manaan `05_5c`/`05_5a`, 30 Korriban `05_7C`/`05_7a`, 35 Tatooine `05_3c`/`05_3a`,
40 Leviathan `"NULL"`, 45 Unknown World `05_8c`). The literal never reaches the
`StartNewModule` call site, so no constant-pool scan will ever find it.

That is the strongest possible argument for the design in §3.2: **do not try to predict which
movie will play.** Read the queue the engine has actually filled (`move_to_module_movies`) and
forward *that*. It is correct for the 34 literal cases, the runtime-selected ones, and any mod's
cinematic equally, because it is the same list the engine is about to play.

Finally, the dead names, so nobody chases them: `5_9`, `5_1C`, `5_8e`, `023aComp`, `023bComp`
and `LIVE_2a`..`LIVE_6c` are referenced by shipped scripts with **no matching `.bik` on disk** —
the engine's own "refuse a name it cannot use" path (`AddMovieToExoArrayList`, §1.5) is load-
bearing in the retail game, not just in mod-compat theory.

---

## 5. The live test plan

The rig is `tools/demo.ps1` (two instances, unattended). The point of this test is not "does a
movie play" — of course it does — it is **does the session survive the longest main-thread
stall it has ever been asked to survive, and do both players end up in the same module.**

Everything below reads `kotorcoop-plugin.log` in each game directory, plus the new
`kotorcoop-plugin.cinematic.log` split file this pass adds.

### 5.0 Before anything

```powershell
pwsh src\KotorCoop.Plugin\build.ps1 -Install -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'
pwsh src\KotorCoop.Plugin\build.ps1 -Install -GameDir 'D:\Games\swkotor-gog-p2'
```

`demo.ps1` does **not** deploy the plugin. Without `-Install` this test measures a stale
`dinput8.dll` and every signal below is absent for the wrong reason. Confirm the new build
really shipped:

```powershell
Select-String -Path 'C:\GOG Games\Star Wars KOTOR (GOG)\dinput8.dll' -Pattern 'cinematic:' -Encoding Byte -List
```

### 5.1 The steps

1. **Bring the pair up.** `pwsh tools\demo.ps1` (save slot 1 — remember `-Save 2` silently
   forces the flaky menu-load path). Wait for both logs to show a connected session and each
   side to report the other's phantom.

2. **Baseline the instrumentation at the menu.** Before the world is up, each log should
   already contain, exactly once:

   ```
   cinematic: a movie is playing with no world up (the main menu's own loop)
   ```

   *This is a control.* It proves the watch is installed and reading the engine, and it settles
   the last INFERRED claim in `menu-frame-hook.md` — that the settled main menu really is
   holding a movie open, which is why the SwapBuffers hook is dormant there. **If this line is
   missing, stop**: either the plugin is stale (see 5.0) or `IsMoviePlaying` is not reading what
   we think it is, and nothing after this point is interpretable.

3. **Record the "no cinematic" floor.** With both players standing still in a world for ~30 s,
   `kotorcoop-plugin.cinematic.log` should gain **nothing**. Any `main thread was gone` line
   here is a pre-existing stall unrelated to movies and must be understood before continuing.

4. **Fire a transition, host side. Do it twice — synthetic first, then scripted.**

   **4a — the synthetic control.** A warp carries no movie, so this establishes the shape of a
   transition *without* a cinematic in exactly this rig:

   ```powershell
   pwsh tools\blockpoke.ps1 -ProcId <host pid> travel tar_m03aa
   ```

   Expect a stall line on the host (a module load is seconds too) with `0 transition movie(s)
   were queued going in`, and no queue line at all. That is the discriminator: the queue count
   in the stall line is what separates a load from a cinematic.

   **4b — the real one.** Get the pair to `end_m01ab` (the starboard section), stand the host
   on the escape pod, and let the game do it: `end_pod.utp`'s `OnUsed` runs `k_pend_pod02`,
   which opens `end_pod.dlg`; **taking reply 1 runs `k_pend_pod01`, which is
   `StartNewModule("STUNT_00", "", "01c")`** (§4.2). The movie must arrive the way the game
   produces it, not the way we would.

   ⚠ **Move only ONE player.** If both are standing in the trigger, both engines fire the
   script independently and both play the movie with no session involvement — the
   accidental-success case (§2.4). That result proves nothing.

   ⚠ **Do not use a save built by warping off the command deck** — it carries the
   min-one-hitpoint pin and, more importantly for this test, it will not have the pod's
   prerequisites. If reaching `end_m01ab` legitimately is impractical, `warp end_m01ab` on the
   host is acceptable for 4b as long as the pod conversation still runs; say so in the result.

5. **Watch the host log for the shape, in order.** The pass signal is a sequence, not one line:

   ```
   cinematic: the module transition has queued 1 movie(s): 01c
   cinematic: the main thread was gone for NNNNN ms (in a world; 1 transition movie(s) were queued going in) ...
   epoch: module changed; clearing phantom, item, combat and door state
   ```

   `01c` by name is the single strongest signal this whole pass can produce: it proves the
   plugin read the engine's own transition queue, on a frame before the world went away, and
   agreed with the game files (§4.2). Expect the sequence **twice** — once for
   `end_m01ab → STUNT_00` with `01c`, then again for `STUNT_00 → tar_m02af` with `01f`.

6. **Watch the client log — for what is NOT there.** Today it should contain no queue line and
   no stall until the very end, when it gets a bare

   ```
   travel: moving to module 'tar_m02af' on the session's authority
   ```

   and nothing else. That absence, next to the host's two cinematics, is the measured statement
   of the defect and the before-picture for §3.2. **Also record how long the client spent alone
   in `end_m01ab`** (the gap between the host's first stall and the client's `travel:` line) —
   that number is the real cost of §4.3 and nobody has ever measured it.

7. **Confirm the stunt gate behaved.** The client must **never** report `STUNT_00`. If it does,
   the stunt gate in `CoopAgent` failed and that is a separate bug with a bigger blast radius
   than anything about movies.

8. **Confirm both arrived.** On both instances:

   ```powershell
   pwsh tools\blockpoke.ps1 -ProcId <pid> state
   ```

   and, in-game, `where` on each console. Both must name `tar_m02af`.

9. **Confirm the session survived.** Both logs must show phantoms re-spawned in the new module,
   and neither may contain a `FIRST-CHANCE access violation`. A silent disappearance of one
   instance is the NativeAOT fail-fast and needs `tools/crashlog.ps1` +
   `llvm-symbolizer` (`plugin-crash-symbolication`).

10. **Repeat it.** The first Taris warp killed the client *twice* before anyone believed it.
    Three consecutive clean runs, or it is not a pass.

### 5.2 Pass / fail, per claim

| Claim | Pass signal | Fail signal |
| --- | --- | --- |
| **Both players see the movie** | a `main thread was gone for N ms` line on **both** sides, with comparable N, and a queued-movie line naming the same movie on both | today: the line appears on the mover only. That is the expected FAIL and the reason for §3.2 |
| **The instrumentation is trustworthy** | the queue line names `01c` and then `01f`, matching §4.2 exactly | a queue line with a different name, or `(names unreadable)` — the `CExoArrayList`/`CExoString` walk is wrong and §6's accessors need re-deriving before anything else here is believed |
| **Neither game deadlocks** | the stall line is *followed* by more log on the same side; the frame counter (`blockpoke state`) advances again afterwards | no further lines from that instance; heartbeat still ticking but frame counter frozen — the plugin is alive and the engine is not |
| **Both arrive in the same module** | `where` agrees on both consoles; both logs show `epoch: module changed` and then phantoms | one side stuck in the old module, or the client's `travel:` line reports a non-zero failure code |
| **The session survives** | phantoms both ways, enemy census non-empty and equal, no access violation, three clean runs | any AV; a census labelled with the wrong module (the §3 defect in `transition-window-races.md`); phantoms that never respawn |

### 5.3 The specific things to distrust

* **The stall swallows the pre-store flush window.** `OnFrame`'s
  `Odyssey.IsAreaTransitionPending()` branch is what destroys plugin-spawned bodies before
  `StoreCurrentModule` bakes them into the saved area, and it runs on the hook that a movie
  turns off. If the transition's pending window falls entirely inside the movie, phantoms and
  replicated items become permanent residents of the stored module and the object-id counter
  shifts for the rest of the playthrough. **Check for it explicitly**: after the run, warp back
  to the departed module and look for a stray `coop_phantom`-tagged body. This is the highest-
  value thing this test can find and it has never been looked for.
* **`ClearAgentWorldInputs` is delayed by the length of the movie** (§2.2). The window in which
  the agent's stale world inputs are live is now seconds, not milliseconds.
* **A transition closes any live conversation** before the movie plays (§1.5). If a player is
  mid-dialogue when their partner triggers a transition, that is the collision. Worth one
  deliberate attempt.
* **The agent's phase read should NOT flip to `MainMenu`** — the prologue pauses the world
  timer, it does not destroy the server (§1.5, VERIFIED statically). Confirm it anyway: it is
  one glance at the agent console during step 5, and if it *does* flip, every phase-gated
  decision the agent makes for the length of the cinematic is made about the wrong state.
* **The min-one-hitpoint pin** travels with a character warped off the command deck, so a save
  built that way silently blocks every death-path test in the destination
  (`transition-window-races.md`). Irrelevant to the cinematic itself, relevant to anything you
  test immediately after it.

---

## 6. What this pass shipped, and what it deliberately did not

**Shipped** — `src/KotorCoop.Plugin/CinematicWatch.cs`, driven from `MainLoopHook`:

* the **move-to-module movie queue** on its rising edge, with the movie names — the one
  pre-warning the engine gives, logged from a frame on which we are still running;
* the **`IsMoviePlaying` edges** we can actually see (the menu case, and any in-world movie
  that turns out *not* to block — which would be a new fact worth the line);
* the **main-thread stall**: the wall-clock gap between consecutive callbacks, which for a
  blocking movie *is* the cinematic, measured. This is the only direct evidence of one that
  this process can produce.

Plus the read-only engine accessors in `Odyssey` (`IsMoviePlaying`,
`QueuedTransitionMovieCount`, `ReadQueuedTransitionMovie`) and a `cinematic` log channel, so
`kotorcoop-plugin.cinematic.log` is the whole story in one file.

**Deliberately not shipped:**

* **No shared-block field.** A live "cinematic" flag is unwritable during the exact window it
  would describe (§3.2.4), and a block version bump carries the half-bump trap (the version is
  part of the agent's `PluginSignature`; bumping one without the other makes every agent
  silently fail to find the plugin). Nothing here is worth that.
* **No `AddMoveToModuleMovie` call.** The lever in §1.5 is verified statically and is the right
  design, but writing into a transition the plugin cannot observe is precisely the shape of
  every bug in `transition-window-races.md`. It wants its own pass, its own protocol version,
  and the live evidence this test produces.
* **No skip handling.** §3.2.3 argues it should never be added.
* **No change to the stunt gate.** §4.3 is a finding, not a fix. Accumulating movie lists
  across a declined follow (§3.2.2) is the right answer and it belongs with the protocol
  change, not ahead of it.

Also corrected on the way through, because both were load-bearing for someone reading about
cutscenes: `ingame-gui-transition.md`'s "dialog/cutscene flag" at `0x00641980` is
`CSWCModule::GetMiniGame` (§1.6), and `docs/re/frame-hook.md`'s "switch to the call-site detour
if a callback is genuinely needed during movie playback" does not work — that detour does not
fire during a movie either.

---

## Key addresses

| Address | Symbol / meaning |
| --- | --- |
| `0x005EDB40` | `CClientExoApp::IsMoviePlaying` — `__thiscall`, **no args** (chain bottoms at `0x00404DD0`, a bare `ret`) |
| `0x005F2BD0` / `0x0070C1F0` / `0x00404DD0` | the rest of that chain; `movie_player` at `CClientExoAppInternal+0x12C`, playing flag at `CExoMoviePlayerInternal+0x30` |
| **`0x005F6F20`** | **`CClientExoAppInternal::WaitForMovie`** — the blocking spin loop `0x005F7063`–`0x005F70B5`; runs only `Global::messagepump 0x00401510`; skip flag `[clientInternal+0x254]`; cleanup `RemoveMoveToModuleMovies` + `SetGlobalUnpauseableState(0)` |
| `0x00603003` / `0x0060300F` | MainLoop's own `IsMoviePlaying` test and `WaitForMovie(1)` — inside the in-world block, which is why the MENU movie never blocks |
| `0x00602AF0` | `CClientExoAppInternal::PlayMoviesInExoArrayList` — takes the pause lock (`SetGlobalUnpauseableState(1)` at `0x00602B09`), calls `WaitForMovie` at `0x00602BA6` |
| `0x005EDB30` / `0x00602CF0` | `CClientExoApp::PlayMovie` (`ret 0x10`, four args) → internal, which appends to `+0x2D8` and plays it |
| `0x00540ED0` | `CSWVirtualMachineCommands::ExecuteCommandPlayMovie` — NWScript `PlayMovie`; pops the name, `HideLoadScreen(0,1)`, then `PlayMovie` |
| `0x005481E0` / `0x00548210` / `0x005482D0` | script `IsMoviePlaying` / `QueueMovie` / `PlayMovieQueue`; `movie_queue` at `CClientExoAppInternal+0x2E4` |
| **`CClientExoAppInternal+0x2D8`** | **`move_to_module_movies : CExoArrayList<CExoString>`** — the transition queue |
| `0x005EDB50` → `0x006027A0` | `CClientExoApp::AddMoveToModuleMovie(CExoString*)` — appends to it; **the design lever** |
| `0x005EDB60` | `CClientExoApp::GetMoveToModuleMovies` — `mov eax,[ecx+4]; add eax,0x2D8; ret` |
| `0x005EDB20` → `0x00602650` | `CClientExoApp::PlayLoadModuleMovies` — one caller only |
| **`0x004BA920`** | **`CServerExoAppInternal::StartNewModule`** — calls `PlayLoadModuleMovies` at `0x004BA9CA`; one caller, `CServerExoAppInternal::MainLoop 0x004BABB0` at `0x004BAD93`. **The co-op warp path lands here too.** |
| `0x00544390` | `ExecuteCommandStartNewModule` — NWScript `StartNewModule(module, waypoint, movie1..6)`; six `AddMoveToModuleMovie` calls at `0x0054459E`–`0x005445DA` |
| `0x006CA250` | `CSWGuiSaveLoad::LoadPCAutoSave` — restores six queued transition movies (`0x006CA8B8`–`0x006CA91E`) |
| `0x0050D6C0` | `CSWSArea::EventHandler` — queues a transition movie at `0x0050DA9F` |
| `0x005EE380` / `0x005EE160` / `0x0070C1E0` | `RemoveMoveToModuleMovies` / `CClientExoApp::CancelMovie` / `CExoMoviePlayer::CancelMovie` |
| `0x00405B30` / `0x00405E50` | `Global::MoviePlaybackWorker` (a thread proc; parks the current player in the global `0x007A3CF4`) / `CExoMoviePlayerInternal::PlayMoviesAsync` — sets the playing flag at `0x00405EBF`; playback is on its own thread and `WaitForMovie` is what makes it look synchronous |
| `0x00404C80` | `CExoMoviePlayerInternal::PlayMovieLoop` — the Bink frame loop with its own message pump. ⚠ **`ret 4`**, not the DB's 0 |
| `0x00405190` | `Global::MovieWinMessageHandler` — the movie window's WndProc; ESC at `0x00405221`, mouse at `0x004052E8`/`0x00405342`, `WM_ACTIVATEAPP` at `0x00405255`, all → `CExoMoviePlayerInternal::CancelMovie 0x00404C40` |
| `0x00404C40` | `CExoMoviePlayerInternal::CancelMovie(int all, int force)` — `ret 8`; sets `[internal+0x14]=1`, tested by `PlayMovieLoop` at `0x00404D3F` |
| `0x00402800` @ `0x00403316` / `0x00403326` | `Global::WinMessageHandler` — the private `0x40A` / `0x409` the worker posts to `0x007A39D8` when a movie ends. **There is no completion callback.** |
| `0x0062ABF0` / `0x006244E0` | `CGuiInGame::StartGlobalFade` — `ret 0x10`, four args → `CSWGuiFade::SetFade` |
| `0x0062DED0` / `0x006244B0` | `CGuiInGame::IsGlobalFadeObscuring` (`fade[0x4C] > 0.001f`) / `CSWGuiFade::IsFadeDone` — the readable fade predicates |
| `0x00641980` | `CSWCModule::GetMiniGame` — **not** a cutscene flag; corrects `ingame-gui-transition.md` |
| `0x0056CBF0` / `0x0056CC50` | `CSWSMessage::SendServerToPlayerModule_Start/EndStartNewModule` — the network messages that bracket a transition; the obvious hook points if the design in §3.2 ever needs an engine-side edge instead of a polled queue |
| `CClientExoAppInternal+0x1F8` | movie suppression latch: non-zero and neither `PlayMovie` nor `PlayLoadModuleMovies` does anything |
| `0x007A3CF4` / `0x007A39D8` / `0x007A39D4` | current `CExoMoviePlayerInternal*` / main HWND / movie HWND |
| `0x0061D230` / `0x0061D3D0` | `CClientOptions::Set/GetMovieShown` — the per-save "already seen" bits |
| `0x004AECC0` / `0x004AECD0` | `SetMoveToModulePending` / `SetMoveToModuleString` — what `Odyssey.TravelToModule` writes |
| `0x0073D00C` / `0x004046BD` | the SwapBuffers IAT slot (movie-gated, so `FrameHook` is dead during a movie) / the `MainLoop` call site `MainLoopHook` rewrites |
