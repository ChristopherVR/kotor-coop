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
| `AddMovieToExoArrayList 0x005FBBF0` refuses a name it cannot use (returns 0) | **VERIFIED, and it really does check the file.** `0x005FBC1A` compares the name against `""` (`CExoString::operator!= 0x005E5390`) and returns 0 at `0x005FBF39` if it is empty. Then `0x005FBC37` searches it for `':'` (`0x0073E2A0`); a name WITHOUT one falls into `0x005FBC99`, which formats `MOVIES:%s` (`0x0073D7D8`), opens it `"rb"` (`0x0073D5B0`) as resource type `0x80F`, and — if that fails — tries the `LIVE%d` (`0x007453C0`) variants before returning 0 and adding **nothing**. The add itself is `CExoArrayList<CExoString>::AddUnique 0x005F7C10`, so it also **silently drops a duplicate name**. |

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
  value thing this test can find and it has never been looked for. **§7 makes it stageable
  without the escape-pod conversation — and §7.4 says which of the two forms actually tests
  this, because the obvious one does not.**
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
  and the live evidence this test produces. ⚠ **Superseded twice**: by §7, which added the call
  behind a debug verb and a guard, and by **§9**, which gave it the automatic producer and the
  protocol version this bullet asked for — after both halves had been measured live.
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

## 7. Staging a movie-carrying transition on purpose (2026-08-15)

§5 needs a movie playing across a module transition, and the only way the base game produces
one is the escape-pod conversation — a human, playing. §6 deliberately shipped no
`AddMoveToModuleMovie` call, so the first live pass could only test a transition with **no
movie**, which it did: the pre-store flush ran correctly and **that result does not touch the
hypothesis at all** (`docs/live-verification.md`, 2026-08-15).

This section is the instrument that closes that gap: a console verb that puts movies into
`move_to_module_movies` and then triggers a transition, so the whole thing runs unattended.

### 7.1 The verb

```
moviewarp <module> <movie> [movie...]     stage the cinematic, then warp
moviewarp queue <movie> [movie...]        stage only; the NEXT transition of any kind takes it
moviewarp clear                           empty the queue and disarm
moviewarp show                            report what is queued, change nothing
```

Driven on the rig exactly like every other verb:

```powershell
pwsh tools\blockpoke.ps1 -ProcId <pid> console 'moviewarp tar_m03aa 01c'
```

⚠ `blockpoke console` waits for **one** reply-serial change, so it prints only the first
answer — `armed 01c for the next warp (was: nothing)`. The second half (`staged 01c; now
warping to ...`) lands about a dispatcher tick later and is in the agent console and the
plugin log; do not read the first reply as the whole outcome.

Movie names are comma- or space-separated; the wire form is commas. `01c` (6.4 MB, the escape
pod) and `01f` (3.5 MB, waking on Taris) are present in **both** rig installs — 61 identical
`.bik` files each, checked — and `01c` is the canonical one for this test because it is the
movie the real chain fires (§4.2).

### 7.2 Where the engine write happens, and why there

| Form | Written by | When |
| --- | --- | --- |
| `moviewarp <module> <movie>` | `TravelController.QueueStagedMovies` | on the frame the warp fires, **after** every plugin-spawned body has been flushed and immediately before `Odyssey.TravelToModule` |
| `moviewarp queue <movie>` | `CheatController.StageMovie` | at once, and left there |

The warp form is **two round trips on purpose**. The plugin arms the names and answers; only
then does the agent file the travel. Filing both in one tick cannot work — `PluginHost.OnFrame`
takes the travel branch and `return`s **before** it ever reaches `_cheats.OnFrame()`, so the
warp would go out with no movie and the cinematic would sit armed, waiting to ambush whatever
transition happened next. `CheatFollowUp` is that rule, and it also guarantees the other half:
**a refused staging does not warp.** A movie-less transition has already been tested and proves
nothing here, so silently running one again would waste the pass.

The write sits immediately before `TravelToModule` because that is where the real feature will
put it (§3.2.2): the pending flag this controller raises is consumed by
`CServerExoAppInternal::StartNewModule 0x004BA920`, which is the same function that drains the
queue. The debug verb is only the first producer of `TravelController.StagedMovies`; a peer
replaying an owner's transition movies writes to exactly the same arm.

### 7.3 The guard

`MovieStaging.Decide` (pure, unit-tested) refuses, **naming what it saw**, when: there is no
client application; no names; more than the engine's own six; **a transition is already
pending** (writing into one in flight is the shape of every defect in
`transition-window-races.md`); a movie is already playing; or **the queue is not empty** —
because a cinematic the GAME staged is not ours to add to, and doing so would make the
resulting stall un-attributable to either.

Around that:

* the verb is spelled in full with no alias, in both `FuncInterpHook.Verbs` and the completion
  catalog — the same rule `netsim` follows, for the stronger reason;
* the parser refuses a malformed line rather than arming nothing: a duplicate name (the engine's
  `AddUnique` would drop it **silently**), a non-resref name, a list too long for the 32-byte
  cheat text field;
* the engine's own verdict is reported per name. `AddMovieToExoArrayList` opens `MOVIES:<name>`
  and adds nothing if it cannot (§1.5), so `the engine REFUSED '01x'` is a real and expected
  outcome, and it is the difference between "the design is wrong" and "that file is not here";
* the arm **expires after 15 s** and is dropped at any epoch change, both out loud. An arm that
  outlived its run would attach a multi-second main-thread stall to an unrelated transition with
  nothing in the log connecting the two.

### 7.4 ⚠ What each form does and does NOT test

This is the part to read before booking rig time.

**`moviewarp <module> <movie>` tests**: that the queue write reaches the engine on the co-op
warp path at all (§1.5, the whole design lever, VERIFIED statically and never once live); that
a session survives the longest main-thread stall it has ever been asked to survive; that both
instances re-converge afterwards.

**It does NOT, by construction, test the pre-store flush hypothesis.** On this path the plugin
initiates the transition, so `PluginHost.OnFrame` has already destroyed every spawned body
*before* raising the pending flag — the `IsAreaTransitionPending` flush branch is not even
needed. If phantoms are baked in anyway on this path, that is a **new and worse** finding
(the flush is insufficient, not merely skippable), but a clean result here means nothing about
the risk case.

**`moviewarp queue <movie>` is the one that tests it.** It leaves the queue armed and then a
transition the plugin did NOT initiate — a door, an area trigger, a script — drains it. That is
exactly the shape of the escape pod: no advance warning, the flush branch is the only thing
standing between a phantom and the stored module, and the movie turns off the hook that branch
runs on.

⚠ **A KOTOR transition door is USED, not walked into**, and `end_m01aa` has no area-transition
triggers at all — so there is no walk-in route and this needed a second verb. **`usedoor` is
it, and §8 is the whole of run B.**

### 7.5 The live procedure

Preconditions as §5.0 (**`build.ps1 -Install` on both installs** — `demo.ps1` does not deploy
the plugin), then:

1. Bring the pair up and confirm the control line
   `cinematic: a movie is playing with no world up (the main menu's own loop)` once per instance.
2. `moviewarp show` on the host. Expect `queued: nothing; armed: nothing` — this proves the
   channel is live and the readers agree before anything is written.
3. **Run A, the lever.** On the host:
   `pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'moviewarp tar_m03aa 01c'`
4. **Run B, the hypothesis.** Back in a module with both players together and a phantom
   standing: `moviewarp queue 01c` on the host, confirm with `moviewarp show`, then move the
   host through an ordinary door/area transition (or `warp` — a plain `warp` is also
   plugin-initiated, so a DOOR is what makes run B different from run A).
5. Whatever the outcome, `moviewarp clear` afterwards. An armed queue outlives the test.

### 7.6 What PASS and FAIL look like, line by line

All lines are in `kotorcoop-plugin.log`; the `cinematic:` ones are also in
`kotorcoop-plugin.cinematic.log`, the `travel:` ones in `kotorcoop-plugin.travel.log`, and the
flush lines only in the combined file (they are prefixed `flush...`, which is not a channel).

**Run A, the expected shape on the host, in this order:**

```
cinematic: armed 01c for the next co-op warp (previously armed: nothing)
cheat: armed 01c for the next warp (was: nothing) (status 1)
flush, before the module is stored: destroyed phantom 0x136 (slot 0)
flush: destroyed item body 0x131 (item 0x...) before the module is stored
cinematic: staged 1 of 1 movie(s) onto this transition: 01c
travel: moving to module 'tar_m03aa' on the session's authority
cinematic: the main thread was gone for NNNNN ms (in a world; 1 transition movie(s) were queued going in) ...
epoch: module changed; clearing phantom, item, combat and door state
```

* **The movie actually played** ⟺ `staged 1 of 1` is followed by a stall of **thousands** of
  milliseconds. `01c` is 6.4 MB and runs about 25 s unskipped; a module load alone measured
  5.1–5.3 s on this rig (2026-08-15), so **a stall under ~6 s did not include a movie** and the
  run is void — check `moviewarp show` and the per-name engine verdict before believing
  anything else.
* ⚠ `1 transition movie(s) were queued going in` in the stall line comes from
  `CinematicWatch.NoteStaged`, i.e. from **us**, not from an observation. It cannot be
  observed: the queue rises and is drained between two `MainLoopHook` callbacks (the write is
  on the SwapBuffers hook inside the client MainLoop; the drain is in the server MainLoop that
  follows). The independent confirmation is the per-name engine verdict, and — in run B only —
  `cinematic: the module transition has queued 1 movie(s): 01c`, which IS an observation,
  produced by walking `CClientExoAppInternal+0x2D8` with a completely different code path.
* **FAIL, and stop**: `the engine REFUSED '01c'` (the file is missing or the name is wrong —
  nothing was staged and the transition that follows is an ordinary movie-less one);
  `NOT staging ... the game has already queued` (the module itself was about to play something);
  `the staged movie(s) ... expired unused` (the travel half never arrived — look at the agent
  console for `the cinematic was not staged, so the warp to X was NOT attempted`).

**The pre-store-flush hypothesis — the three outcomes and what each means.** Compare the flush
lines against the stall line:

| Outcome | Reading |
| --- | --- |
| flush lines **before** the stall | The window was not swallowed. In **run A** this is the expected result and proves only that the plugin-initiated path flushes ahead of itself, which was already known. In **run B** it is the real answer: the engine raised the pending flag, the plugin got at least one frame in it, and the flush completed before the movie took the thread. |
| flush lines **after** the stall, or interleaved with `epoch:` | The flush started but did not finish before the movie. Anything still standing at that moment is already in the stored module. Expect a partial: some `destroyed phantom` lines, then the stall, then the epoch. **This is a real defect** and the fix is to flush on the pending EDGE rather than one body per frame. |
| **no flush lines at all** between the stage and the stall | The hypothesis is confirmed: the pending window fell entirely inside the movie. Every phantom and every replicated item standing at that moment is now a permanent resident of the stored module. Confirm with 7.7 before believing it — "there was nothing to flush" produces the same silence, so the run is only interpretable if a phantom and an item body were verifiably standing (grep the run for `spawned phantom` / `spawned item` with no matching destroy). |

**The session survived** ⟺ both logs continue after the stall, both show `epoch: module
changed` and then phantoms re-spawned, the frame counter in `blockpoke state` advances again,
and neither log contains `FIRST-CHANCE access violation`.

### 7.7 The check for a baked-in phantom

⚠ **SUPERSEDED BY §8.6.** The tag-and-count recipe below reads `tools/creatures.ps1`, which is
a raw vtable sweep with no liveness gate and lists destroyed bodies as live ones. Use §8.6,
which asks the engine's own object array through `resolve <id>`. The paragraph is kept because
the reasoning about the TAG is still right; only the verdict step was wrong.

The thing nobody has ever looked for. After the run, go back to the module that was stored and
count the bodies the co-op blueprint leaves behind:

```powershell
pwsh tools\blockpoke.ps1 -ProcId <pid> console 'warp end_m01aa'     # the departed module
pwsh tools\creatures.ps1 -ProcId <pid>                              # every creature in it
```

A phantom baked into the save is a creature whose **tag is `coop_phantom`** and which no peer
is currently driving. The tag is the identifier that matters — the census already excludes
creatures by that tag precisely because the id table clears before the body finishes dying
(`transition-window-races.md` §2), so tag is the one thing that survives to the last readable
moment.

* **CLEAN**: exactly as many `coop_phantom` creatures as there are peers standing in that
  module right now (normally one), and they disappear on `disconnect`.
* **BAKED**: a `coop_phantom` creature that is still there with the session down — or, more
  visibly, **two** of them after re-entering the module a second time. The count growing by one
  per visit is the signature, and it is unmistakable.
* Same check for items: a ground item body that persists across a re-entry with no session.

⚠ **"They disappear on `disconnect`" was NOT true until 2026-08-15**, so a BAKED reading taken
before that date is measuring a teardown leak rather than the transition window. Two causes,
both fixed and written up in `client-control-lock.md`: `PhantomController`'s idle despawn was
gated on a derived count it never recomputed, and — the one that matters for every spawner —
a destroy REFUSED by the engine (`failure 4`, the script VM mid-call) cleared the slot anyway,
so one declined call orphaned the body permanently with nothing left to retry it. Refusals are
retried now, and `PluginHost.DrainSessionSpawns` sweeps all four spawners on the session's
falling edge and says either how many it removed or that none were standing. If a
`coop_phantom` survives a `disconnect` again, the log will name it and its failure code.

⚠ Do the check with a save that is **discarded afterwards**. A baked phantom is written into
the save, and it also shifts the object-id counter for the rest of that playthrough.

### 7.8 If it crashes in the window

The transition window is where this codebase historically dies, and a movie makes it seconds
longer. A crash here presents as **one instance disappearing with nothing after the stall line**
— the NativeAOT fail-fast bypasses the log entirely.

```powershell
pwsh tools\crashlog.ps1                      # the Windows Error Reporting record: eip + module
```

If `eip` is **outside** the `swkotor.exe` image it is our `dinput8.dll`. Take the module's load
base from the same record, subtract it, and hand the RVA to `llvm-symbolizer` (ships with VS;
the PDB sits next to the installed dll):

```powershell
llvm-symbolizer --obj='C:\GOG Games\Star Wars KOTOR (GOG)\dinput8.dll' --relative-address 0x<rva>
```

That is how `EncounterController.HandleWatch` was found. The first suspects for THIS window,
in order: a controller dereferencing `block.PlayerCreature` rather than resolving fresh
(pattern 1 — the block's pointer is a liveness signal only); `ClearAgentWorldInputs` running
seconds late because the epoch gate could not run during the movie (§2.2), so the agent's stale
world inputs were live for the whole film; and a `0xC0000409` **anywhere unrelated**, which
means a stack-skewed engine call — re-run `python tools/callaudit.py --bad` before looking
anywhere else.

---

## 8. Run A happened; run B needed a door (2026-08-15, later)

### 8.1 Run A, measured

`moviewarp tar_m03aa 01c` on the host of a live pair:

```
cinematic: armed 01c for the next co-op warp (previously armed: nothing)
flush, before the module is stored: destroyed phantom 0x12D (slot 0)
cinematic: staged 1 of 1 movie(s) onto this transition: 01c
travel: moving to module 'tar_m03aa' on the session's authority
epoch: module changed; clearing phantom, item, combat and door state
cinematic: the main thread was gone for 15531 ms
```

**15,531 ms**, against 5.1–5.3 s for a bare module load on the same rig. The movie genuinely
played. `AddMoveToModuleMovie` is correctly wired and **the co-op warp path carries a queued
movie through `CServerExoAppInternal::StartNewModule` exactly as §1.5 said it would** — the
design lever is now VERIFIED live, not just statically. The baked-phantom check afterwards was
clean — but read §8.6 before believing it. It was `two coop_phantom rows, one distinct id
(0x139), for one peer`, and both halves of that criterion have since been shown unsound: a raw
vtable sweep lists freed blocks as live creatures, so neither the row count nor the distinct-id
count is evidence either way. Run A is **not a CLEAN result**; it is an unmeasured one. Nothing
about run A depended on it.

`moviewarp queue 01c` also works, and produced the independent confirmation §7.6 promised —
the array walk, on a different code path from the write:

```
cinematic: the engine accepted '01c' onto the transition movie queue
cinematic: the module transition has queued 1 movie(s): .\movies\01c.bik
```

⚠ Note what the engine stored: **`.\movies\01c.bik`, not `01c`**. `AddMovieToExoArrayList`
normalises the name into a path before adding it (§1.5), so `moviewarp show` echoes the path
and not the name you typed. That is the engine agreeing with you, not disagreeing.

And exactly as §7.4 predicted, none of it tests the hypothesis. The flush ran **before** the
stall because on that path it always does.

### 8.2 Why a door, and why nothing else would do

The blocker was blunt: with the movie queued, there was no way to make the engine start a
transition. `end_m01aa` has one transition door (`end_door10` → `end_m01ab`) and **no area
transition triggers at all** — all fourteen of its triggers are script triggers.

⚠ This paragraph originally continued "and a KOTOR transition door is not walked into; it is
USED". **That was wrong, and §8.3 has the proof.** Walking into it is exactly what fires it;
what does not work is *teleporting* onto it, because a `SetPosition` snap runs no movement
update and the transition is posted by the movement update. The rig had measured a `tp`, not a
walk, and the wrong conclusion was drawn from it — by me.

Reading the engine settles why the distinction matters, and it is sharper than expected.
**A door transition and a co-op warp end in the SAME three calls.** `CSWSDoor::EventHandler`
at `0x0058C185`:

```
0x0058C18E  push 1     ; ecx = CServerExoApp
0x0058C192  call 0x004AECC0            ; SetMoveToModulePending(1)
0x0058C197  lea eax,[esi+0x390]        ; door->linked_to_module
0x0058C1A0  call 0x004AECD0            ; SetMoveToModuleString(...)
0x0058C1A5  add esi,0x388              ; door->linked_to
0x0058C1AE  call 0x004AED30            ; SetMoveToModuleStartWaypoint(...)
0x0058C1B9  call 0x00506B30            ; CSWSArea::SetTransitionPending(0, 0)
```

That is `Odyssey.TravelToModule`, verbatim, plus one line. **What differs is everything
before it**, and it is what this whole investigation is about. The door path has two phases:

| Phase | What runs | Where |
| --- | --- | --- |
| **1 — on use** | `CSWSArea::GetNextTransitionPendingID 0x00506AC0`, `CGuiInGame::StartGlobalFade 0x0062ABF0` (0.5 s), **`CSWSArea::SetTransitionPending(1, id) 0x00506B30`**, `CServerExoApp::SetPauseState(2, 0)` | `0x0058C230`–`0x0058C315` |
| **2 — when the fade completes** | the four calls above; the transition actually happens | `0x0058C185`–`0x0058C1BE` |

`SetTransitionPending` writes `CSWSArea::trans_pending` at **`+0x2C4`** — the exact byte
`Odyssey.IsAreaTransitionPending()` reads, and therefore the exact flag that opens
`PluginHost.OnFrame`'s pre-store flush branch. So:

* **a door transition gives the plugin a ~0.5 s window** (the fade) in which to destroy every
  body it spawned, and only then stores the area;
* **a co-op warp gives it none, and needs none**, because the plugin flushed before it raised
  anything.

That one asymmetry is the whole reason run A cannot answer the question and run B can. It also
confirms, from the binary, the premise the flush branch was written on.

### 8.3 The verb: `usedoor` — and the correction that matters

```
usedoor                  the nearest module transition door
usedoor <tag>            that door by tag, e.g. usedoor end_door10
usedoor <id>             that door by object id (0x1F, #31 or 31)
```

⚠ **CORRECTION (2026-08-15, third pass). A KOTOR module transition door is entered by WALKING
INTO IT, not by using it — the exact opposite of what the first two passes of this section
said, mine included.** The first live run failed on this and nothing about the failure was
visible: the click was accepted, the PC walked from 4.1 m to 1.5 m, and then nothing at all
happened — no transition, no conversation, no error.

The binary settles it completely. `CSWSDoor::EventHandler` switches on the event's type word
through the jump table at `0x0058C594` (indexed by `0x0058C5C8`), and **only event `0x1E`**
reaches the transition arm at `0x0058C0BE` — `GetIsLinked`, then the `linked_to_module` length,
then the `trans_pending` test that forks into phase 1 or phase 2. Using an object posts
**`0x1A`**: both `AIActionUseObject` (`0x0057ECC0`) and `AIActionOpenDoor` (`0x0057D661`) build
that type, and it lands at `0x0058BD62` — the arm that runs the door's OnUsed script and
nothing else.

Event `0x1E` is built in exactly five functions, and **every one of them is movement**:

| Builder | Address |
| --- | --- |
| `CSWSCreature::HandleSubAreaStateChanges` (3 sites) | `0x00516020` |
| `CSWSCreature::WalkUpdateLocation` (2 sites) | `0x0051BB10` |
| `CSWSCreature::DriveUpdateLocation` | `0x0051CD20` |
| `CSWSCreature::WalkUpdateLocation_QuickWalk` | `0x0051D9C0` |
| `CSWSCreature::AIActionMoveToPoint` (2 sites) | `0x0051F4F0` |

So a module transition is **a creature crossing into the door's sub-area, noticed by its own
movement update**. `HandleSubAreaStateChanges` additionally builds the event only when
`CSWParty::GetIndex` returns 0 (`0x00516263`) — the party leader — which is exactly what the
door's own `GetIsLeader` check at `0x0058C206` agrees with. `DriveUpdateLocation` in that list
is the human case: a player walking their PC into the door.

**Three consequences, and they explain every dead end of the last two passes:**

* **`tp` onto or beside the door can never transition.** It is a `SetPosition` snap; no
  movement update runs, so nothing notices a crossing. That is why teleporting the PC to the
  doorway did nothing.
* **Sending a use/open input can never transition.** It posts `0x1A`. This is what `usedoor`
  did on its first version, and the 4.1 m → 1.5 m walk was `AIActionUseObject` pathing to the
  door's *action point* — real movement, but it stops short of the threshold on purpose.
* **The action queue on the player character works.** That same walk proves it. The two
  no-ops were not one bug wearing two hats; they were two different mistakes that happened to
  look alike.

**So `usedoor` walks the player THROUGH the door.** It queues `ActionMoveToLocation` (the
routine that becomes `AIActionMoveToPoint`) to a point `DoorUse.WalkThroughOvershoot` = 2.5 m
*past* the door, and lets the movement update post the event as the player crosses. Aiming at
the door instead would let the walk stop short — which is precisely what the use-action did.
The verb keeps its name because live runs are staged against it; the name is now a historical
artefact and the log says what it really does.

### 8.3b ⚠ A closed door is a wall, and that is the second thing that stopped it

The first walk-through run got further than anything before it and still did not transition:
the walk was queued, the PC *moved* — from (47.4, 116.0) to (46.6, 118.4) — and stopped
**1.8 m short** of the door at (47.4172, 120.126), nowhere near the 2.5 m-past target. Moving
and stopping short is a different failure from not moving at all, and it has an obvious
physical reading: **`end_door10` was shut, and a shut door is a pathing obstacle, so the path
ended at its face.**

The engine agrees. `CSWSDoor::MoveToNextOpenState 0x00589680` commits pending → current
(+0x2CD → +0x2CC) and then calls **`CSWSArea::BudgeCreatures 0x00508180`** with the door's
position and its new open/closed bit — the update that makes the area stop treating the door
as solid. That is exactly why a raw write to +0x2CC is forbidden: the byte moves the collision
without telling the area.

So the real sequence is **both events, in order** — open it, then cross it — and `usedoor` now
does both:

1. **Open**, if `GetDoorOpenState` reads 0, through the engine's own
   **`CSWSDoor::OpenDoor 0x00589C70`** (`__thiscall`, `ret 4`, argument = the OPENER's object
   id). It is worth the call rather than a state poke for five reasons, all of them in the
   disassembly: it takes `dot(opener.position − door.position, door.orientation@+0x9C)` and
   picks swing state **1 or 2 from which side the opener is standing** (0x00589C97–0x00589CE1);
   it records the opener at +0x31C; it calls `SetOpenState(state, bDeferred=1)`, the engine's
   own idiom; it opens the door's LINKED twin the same way (`GetLinkedObject 0x00589580`, so
   double doors move together); and it runs the door's OnOpen script at +0x228. None of those
   is reproducible by writing a byte.
2. **Commit** immediately with `MoveToNextOpenState`, so the walkmesh is open on this frame
   rather than at the end of a swing animation.
3. **Then** queue the walk.

⚠ `OpenDoor` does **not** consult the lock — it sets the state regardless. `end_door011.utd` is
`Locked = 0` so it does not arise here, but the entry line reports the lock state anyway
because a staging verb that quietly ignores a lock is lying about what it did.

**The walk direction now comes from the door, not the approach.** `DoorUse.WalkThroughPoint`
takes the door's own facing (`CSWSObject::Orientation`, +0x9C) and picks the far side with the
sign of `dot(player − door, facing)` — the identical test `OpenDoor` uses for its swing. A
doorway has one axis you can cross it on, and an approach-vector overshoot aims at the wall
beside the opening whenever you arrive at an angle. The two agree only when you walk in dead
square, which is how the approach-only version survived its first test: the teleport to
(47.4, 116) happened to be due south of a door bearing −1.57079. It falls back to the approach
when the facing is degenerate, and to the door itself when the player is standing on it.

**And the outcome is now self-reporting.** Ten seconds after the walk is queued, and only if no
transition happened, the verb logs where the player actually ended up:

```
door: 600 frames after the walk the player is N.Nm from 0x4A (was M.Mm), at X,Y;
      aimed at X,Y. No transition happened - the door is still an obstacle, or the path
      stopped short.
```

That line exists because its absence cost a round: "walked into a shut door" and "mis-aimed"
left identical evidence, and the difference had to be recovered by hand from a `where` and a
coordinate subtraction. The watch is cancelled by the epoch reset, so a successful run prints
nothing.

`Odyssey.MoveToLocation` declines with **failure 4** while the script VM is mid-call — the same
transient refusal that orphaned a phantom on teardown until destroys were retried. The walk is
therefore retried for up to 120 frames and **says so when it gives up**
(`gave up queueing the walk through 0x<id> after 120 frames`), rather than reporting a walk
that never started.

**The guard** (`DoorUse.Decide`, pure and unit-tested) refuses, naming what it saw: no world;
nothing answering to that tag or id; something that answers but is **not a door** (vtable, not
the object-type byte); **a door with no `LinkedToModule`** — `CSWSDoor::GetIsLinked 0x005890D0`
reads `linked_to_flags` (+0x384) and accepts only 1 or 2, and the log additionally prints the
raw flag byte; a destroyed door (open state 3); a transition already pending; a movie already
playing; or a door further than **40 m**, so a mistyped tag cannot send the PC on a walk whose
cause nobody is still watching for when it lands. A bare `usedoor` searches **linked doors
only**, so it is incapable of doing anything except starting a transition — an ordinary door
standing closer would merely open, which looks exactly like the command working.

Costs worth knowing: `usedoor` with no argument does one full address-space sweep for the door
vtable (`0x007498B0`) — the same machinery `FindByTag` has used on dialogue edges for months,
tens of milliseconds, but it is on the game thread. **Prefer the tag form for a measured run**,
so the sweep early-outs and cannot contribute to the number you are about to read.

⚠ Kept but no longer called for doors: `CSWCMessage::SendPlayerToServerInput_UseObject
0x00677D10` is the **placeable** message — its one caller in the binary is
`CSWCPlaceable::UsePlaceable 0x00682660` — and the only door input sender is
`CSWCDoor::ToggleDoorState 0x00683D90`, which sends `ChangeDoorState 0x00677D70` with `0x2725`
to open. Neither transitions. The binding stays, declared honestly, because the address is
right for a placeable and the comment is what stops the next person repeating the mistake.


### 8.3a The party gate, and why `usedoor` gathers

⚠ **One gate is handled rather than refused, and it is the one that ate the first attempt.**
`CSWSDoor::EventHandler` tests **`CSWParty::IsPartyTogether 0x00635350`** at `0x0058C228` and,
when it is false, jumps to `0x0058C31A` — which runs the script `k_trg_transfail` (string at
`0x007499C4`) **instead of transitioning**. That failure has no trace at all: the door is used,
no fade starts, no module is stored. So `usedoor` reads the same predicate and, when it is
false, logs

```
door: WARNING - the engine says the party is NOT together, and a door transition is gated on
that (0x0058C228), so this use will run k_trg_transfail and move nobody
```

That happened on the first attempt, exactly as predicted: `tp 47.4 116 -0.04` put the PC 4.1 m
from `end_door10`, `usedoor end_door10` reported the door and its destination correctly — and
Trask stayed a hundred metres back at the module start, so the gate cancelled the transition.
No `area transition pending:` line, no flush lines, nothing.

**So `usedoor` now gathers the party before it clicks**, through the engine's own
**`CSWPartyTable::PositionParty 0x00565B00`** — a bare `ret`, no arguments, whose ONE caller in
the whole binary is `CServerExoAppInternal::MovePlayerToArea 0x004B3D10` at `0x004B3E9E`. That
is to say: it is precisely the party collection a module transition already performs. Per
member it computes a formation offset around the player, snaps it with
`CServerExoApp::ComputeSafeLocation 0x004AEB60` (10 m search radius) and writes it with
`CSWSObject::SetPosition`/`SetOrientation`; on the way in it resets the follow data
(`CSWParty::ResetAllFollowData 0x00637890`) and on the way out it clears `pt_solomode`
(+0x190). Walkmesh, formation and follow state are all the engine's problem, which is exactly
what a hand-rolled position write would get wrong.

Two details that would otherwise be silent:

* **`CSWPartyTable+0x10C` is a one-shot "skip the positioning" flag.** `PositionParty` tests it
  at `0x00565B6E` and, when set, jumps straight to the exit — clearing it and moving nobody. So
  `GatherParty` reads it first and, if it was set, calls `PositionParty` **twice**: the first
  call clears the flag, the second does the work. Deterministic, read out of the branch, not a
  retry.
* **The gather is a repair, not a ritual.** A party the engine already calls together is not
  moved at all, so the verb has no side effect in the normal case.

The outcome is classified into four states (`DoorUse.ClassifyGate`, unit-tested) and only two
of them count as passing: already together, or gathered and now together. "Gathered and still
apart" and "could not gather" are reported as failures **before** the click, because a door
used with the gate shut is used for real and produces nothing at all — the one answer this verb
must never give is a confident one.

⚠ Host-side staging only. Companions are host-authoritative, so on a mirroring instance this
moves bodies whose owner republishes them. Acceptable for a debug verb; it would not be for a
feature.

Costs worth knowing: `usedoor` with no argument does one full address-space sweep for the door
vtable (`0x007498B0`) — the same machinery `FindByTag` has used on dialogue edges for months,
tens of milliseconds, but it is on the game thread. **Prefer the tag form for a measured run**,
so the sweep early-outs and cannot contribute to the number you are about to read.

### 8.4 Run B, verbatim

Both players in the same module, with a phantom and ideally a replicated item standing — the
flush needs something to flush, or its silence means nothing (§7.6).

```powershell
# 1. Confirm the queue is empty and the channel is live.
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'moviewarp show'
#    expect: queued: nothing; armed: nothing

# 2. Queue the movie WITHOUT warping. Nothing transitions yet.
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'moviewarp queue 01c'
#    expect: the engine queued 1: 01c - the NEXT transition plays them
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'moviewarp show'
#    expect: queued: .\movies\01c.bik; armed: nothing

# 3. Prove there is something to flush.
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'dropitem g_w_blstrrfl001'
#    and check the host log has a `spawned phantom` with no matching destroy.

# 4. Put the PC beside the door. `tp` moves the PC only; `usedoor` brings the party.
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'tp 47.4 116 -0.04'
#    end_door10 is at (47.4172, 120.126), so this lands ~4 m short of it.

# 5. Make the ENGINE start the transition.
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'usedoor end_door10'
#    log:    door: gathered N party member(s) to you
#            door: using door 0x<id> at N.Nm - it links to module 'end_m01ab' at ...
#    answer: walking through door 0x<id> (N.Nm) -> 'end_m01ab'
#    the PC then WALKS through the doorway; the transition fires as it crosses.

# 6. Afterwards, always.
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'moviewarp clear'
```

If step 5 answers `no door 'end_door10' in this module`, try bare `usedoor` — it reports
`the sweep found N door(s), M of them module transitions`, which distinguishes the wrong module
from a broken sweep.

⚠ **`blockpoke walk` cannot do step 4, and never could.** It queues NWScript
`ActionMoveToLocation` — the AI action queue, which is what it was built and verified for, on
PHANTOMS. The player character does not move that way in this build, and the proof is negative
and complete: all 17 call sites of `CSWCMessage::SendPlayerToServerMessage 0x00677410` were
enumerated and the message type read off each, and on the player-input channel (6) this game
only ever sends **2** Attack, **3** ChangeDoorState, **7** UseSkill, **8** Dialog, **9** UseItem,
**0xB** UseObject, **0xC** UnlockObject and **0x12** CastSpell. Type **1**, `WalkToWaypoint` —
whose server handler `CSWSMessage::HandlePlayerToServerInputWalkToWaypoint 0x005235B0` is a
complete implementation — is **never sent by anything**. It is dead code inherited from the
networked lineage this engine came from, and there is no "walk the player here" message to
borrow. That is precisely why `usedoor` can go through a message and a walk cannot; the PC is
moved by drive control from local input instead. The channel now logs every path it takes,
including the two that used to `return` in silence, so "the walk did nothing" can be told from
"the walk was never tried". **INFERRED, not proven:** whether the queued action is cancelled by
the drive controller or never run at all for a player-controlled creature.

⚠ **A same-module `warp <module> <waypoint>` ignores the waypoint** and puts the party back at
the module start — measured: `warp end_m01aa end_wp_traskdie` landed at (20.2, 23.0). Use `tp`.

⚠ **The venue is not negotiable.** `end_door10` is the ONLY engine-initiated module transition
in this chain: `end_m01aa`'s other fourteen triggers are all script triggers with no area
transition among them, `end_m01ab` has no transition doors at all, and `tar_m03aa` has neither
transition doors nor transition triggers. Geometry for the record — `end_door10` at
(47.4172, 120.126), `end_wp_traskdie` (43.26, 118.27) 4.6 m away, `SW_WAYPOINT001`
(45.01, 120.19) 2.4 m away.


### 8.5 Run B: PASS and FAIL, line by line

The host log should read, in this order:

```
cinematic: the engine accepted '01c' onto the transition movie queue
cinematic: the module transition has queued 1 movie(s): .\movies\01c.bik
door: 0x<id> at N.Nm links to module 'end_m01ab' at waypoint 'from_01a'; open state 0
door: opened 0x<id> (state 0 -> 1) so the walk has somewhere to go
door: gathered 1 party member(s) to you
cheat: walking through door 0x<id> (N.Nm) -> 'end_m01ab' (status 1)
area transition pending: flushing spawned objects before the module is stored
flush, before the module is stored: destroyed phantom 0x<id> (slot 0)
flush: destroyed item body 0x<id> (item 0x<id>) before the module is stored
cinematic: the main thread was gone for NNNNN ms (in a world; ...)
epoch: module changed; clearing phantom, item, combat and door state
```

`area transition pending:` is the line that makes run B run B — it is `PluginHost` seeing
`CSWSArea::trans_pending`, and it **cannot appear on a co-op warp at all**. If it is missing,
the door did not start a transition and everything after it is about something else.

**The three outcomes, and what each means.** Compare the two `flush` lines against the stall:

| Outcome | Reading |
| --- | --- |
| **flush lines BEFORE the stall** — PASS | The fade window survived the movie. The engine raised `trans_pending`, the plugin got frames inside the 0.5 s fade, and every spawned body was destroyed before `StoreCurrentModule` ran. The hypothesis is **refuted**, and the flush branch is correct as written. |
| **flush lines AFTER the stall, or interleaved with `epoch:`** — PARTIAL FAIL | The flush started and did not finish before the movie took the thread. Expect some `destroyed phantom` lines, then the stall, then the epoch — and whatever was still standing at that moment is already in the stored module. **Real defect**: the fix is to flush every body on the pending EDGE rather than one per frame, since one-per-frame assumes ~150 frames of window and a movie leaves zero. |
| **`area transition pending:` with NO flush lines before the stall** — FAIL, hypothesis confirmed | The pending window fell entirely inside the movie. Every phantom and replicated item standing at that moment is now a permanent resident of the stored module, and the object-id counter has shifted for the rest of that playthrough. Only interpretable if step 3 verifiably left something to flush. |

Two more things to record either way:

* the stall must be **thousands** of ms — `01c` runs ~25 s unskipped and a bare module load
  measured 5.1–5.3 s here, so under ~6 s means no movie played and the run is void;
* the client's log, for the absence: it has no queue line and no stall, and gets a bare
  `travel:` line at the end. That gap is the measured cost of §4.3.

### 8.6 The baked-phantom check for the DEPARTED module

The departed module is `end_m01aa` (run B leaves it for `end_m01ab`). After the run, go back and
ask whether any body the co-op blueprint spawned is still a real object there.

⚠ **`tools/creatures.ps1` cannot answer that question, and an earlier version of this section
was wrong to ask it.** It is a raw sweep for the `CSWSCreature` vtable with no liveness gate,
and `operator delete` clears neither the vtable nor the object id — so a destroyed creature
keeps reading as one until its memory is reused. Measured on 2026-08-15, after a teardown that
provably destroyed the body: the plugin logged `destroyed phantom 0x12D`, `state` reported
`phantoms 0`, and `creatures.ps1` **still listed `0x12D coop_phantom`**. It is the same artifact
that cost the enemy-census hunt, and counting distinct ids does not save the criterion either —
one destroyed body still leaves exactly one row. As written it would have reported a freed
block as a baked body and sent everyone hunting a transition-window bug that was not there.

**Ask the engine's object array instead.** The sweep is only a way to produce CANDIDATES; the
verdict belongs to `resolve`:

```powershell
pwsh tools\blockpoke.ps1 -ProcId <pid> console 'warp end_m01aa'
pwsh tools\creatures.ps1  -ProcId <pid>                       # candidates only
pwsh tools\blockpoke.ps1 -ProcId <pid> console 'resolve 0x12D'  # the verdict, per candidate id
```

`resolve` asks the game thread, in one frame, whether the engine still holds that id:

```
resolve: 0x12D: ... array(+0x1005C) 0x345E138  array(engine) 0x345E138
              | engine 0x0  walk 0x0  resolve 0x0        <- nothing holds it: the row is a ghost
```

* **CLEAN**: every `coop_phantom` candidate that is not a peer standing there right now
  **resolves to 0**. A row with no object behind it is a freed block, not a leak.
* **BAKED**: a `coop_phantom` id that **still resolves** with the session down — or, most
  visibly, the count of *resolving* ones growing by one on each re-entry.
* Same rule for items: a ground item body that still resolves after a re-entry with no session.

⚠ **Any BAKED reading taken before 2026-08-15 was measuring something else.**
`Odyssey.DestroyObjectById` declines with failure 4 while the script VM is mid-call, and all
four spawners used to clear their bookkeeping regardless of the result — so one refused call
orphaned a body permanently and "they vanish on `disconnect`" was simply not true. Destroys are
retried now (bounded at 3, and a write-off names the id and the failure code). A pre-fix BAKED
result is a teardown leak, not a transition-window one.

⚠ Do it on a save you then **discard**. A genuinely baked phantom is written into the save and
shifts the object-id counter for good.


### 8.7 If it crashes

Unchanged from §7.8 — `tools\crashlog.ps1`, an `eip` outside the exe image means our
`dinput8.dll`, and `llvm-symbolizer --obj=<installed dll> --relative-address 0x<rva>` names the
line. The suspect list gains one entry specific to run B: the flush branch now runs with a
movie about to start, so a body destroyed on the last frame before `WaitForMovie` is the newest
place a freed pointer can be read from.

---

## 9. Cinematic parity: the wire between the two proven halves (2026-08-15)

§3.2.2 designed this and §6 deliberately did not ship it. Both halves have since been measured
live, so what was left was the wire between them.

### 9.1 What was measured

The follower's half of run B, verbatim:

```
11:39:50.646  flush, before the module is stored: destroyed phantom 0x12D (slot 0)
11:39:50.648  flush, before the module is stored: destroyed item body 0x131 (item 0x10001)
11:39:50.652  travel: moving to module 'end_m01ab' on the session's authority
11:39:50.663  epoch: world unloading; clearing phantom, item, combat and door state
```

It flushed correctly, followed correctly, and logged **no `cinematic:` line at all** while the
owner's main thread was gone for **7,407 ms**. The follower is not gated and needs no new
engine call: its `move_to_module_movies` queue is simply empty, and the engine plays what is
in the queue. Run A had already proved the other half — a movie queued by us on the co-op warp
path played for real, 15,531 ms. So the only missing thing was the names.

### 9.2 The channel

| Direction | Carrier | When |
| --- | --- | --- |
| plugin → agent | shared block `0x3800` (serial, count, 6 × 16 resrefs) | the frame `CinematicWatch` sees the engine's queue rise |
| agent → wire | **`PacketType.TransitionMovies = 40`**, protocol **25** | the tick the block's serial changes, repeated 5 ticks |
| host | relays verbatim to everyone but the sender | its own tick, beside the bark relay |
| wire → agent | accumulated into the follower's pending set | before the follow decision |
| agent → plugin | the travel request's own movie list (`0x3880`) | written before the travel serial, like the module |
| plugin → engine | `AddMoveToModuleMovie` in `TravelController` | immediately before `Odyssey.TravelToModule` |

The last step is unchanged code — it is the path run A measured.

### 9.3 ⚠ The names must be stripped, or the whole thing fails silently

**The engine does not store the name a script gave it.** `AddMovieToExoArrayList 0x005FBBF0`
resolves the name and stores the resolved form, so reading the queue back yields
`.\movies\01c.bik` — which is exactly what `moviewarp show` reported and what made this
visible. Feed that stored form back into `AddMoveToModuleMovie` and the add takes the
**no-colon** branch, builds `MOVIES:.\movies\01c.bik`, fails to open it, and refuses the movie:
returns 0, adds nothing.

Had this shipped unstripped, every follower would have transitioned with an empty queue —
identical to having no channel at all — and the only trace would have been one
`the engine REFUSED` line per movie. So `MovieStaging.TryStripToResRef` reduces the stored form
to `01c` at the point of publication, and refuses anything that cannot become a resref rather
than forwarding a name the far end will certainly reject.

### 9.4 The accumulation rule, and what bounds it

A follower that correctly declines to follow (the `stunt*` gate refusing `STUNT_00`) keeps that
transition's movies and hands the whole set to the warp it finally does issue. On the canonical
chain that means the one warp into `tar_m02af` carries **`["01c", "01f"]`** — both movies, in
order, played by the follower's own `StartNewModule`, while it never enters `STUNT_00` at all.

Three bounds, and deliberately **no timeout**:

* **spent by the follow that carries it** — the set is cleared the moment a travel request is
  filed, so the same names can never ride a second, unrelated transition;
* **dropped when already with the host** — arriving where the host is means no transition of
  ours is pending, so holding names would attach somebody else's film to whatever we did next;
* **capped at six**, the engine's own ceiling (`StartNewModule` takes exactly six movie
  arguments), with the overflow named in the log rather than dropped quietly.

A timeout was considered and rejected, and the reason is the case the accumulation exists for:
the stunt vision between `01c` and `01f` runs for a minute or more, so any timeout short enough
to bound a hoard is also short enough to drop `01c` before `01f` arrives. The three bounds
above are all event-shaped and all tied to something that provably ends the wait.

### 9.5 Reliability, and the two things deliberately not built

**Unreliable, like barks.** A lost announcement costs a cinematic, never consistency — the
rendezvous is arrival in the module, which the travel channel already guarantees on its own.
The sender repeats each announcement over 5 ticks so one drop cannot lose it, and receivers
de-duplicate on `(peer, serial)`. There is no ack: adding one would put a handshake in front of
something whose failure mode is cosmetic.

**No skip mirroring**, for the reason §3.2.3 established and nothing has changed: a skip is
consumed inside `WaitForMovie`, where the skipper cannot send and the peer cannot receive. A
player who skips simply arrives first, which this mod handles every day.

**No block field for "a movie is playing"** — §3.2.4. The plugin cannot write one during the
window it would describe.

### 9.6 Versions moved — check these before blaming anything else

| Number | From | To | Guard |
| --- | --- | --- | --- |
| `ProtocolConstants.Version` | 24 | **25** | `PacketReader` rejects a mismatch; a stale peer gets a typed `Reject` |
| `PluginHost._block->Version` | 41 | **42** | `TransitionMovieTests.TheBlockVersionMovedInBothHalves` |
| `KotorGameBridge.PluginSignature` byte 8 | `0x29` | **`0x2A`** | same test — it reads both files |
| `SharedBlock.TotalSize` | `0x3800` | **`0x3A00`** | the region would not fit either existing gap |

⚠ **Packet type 40, not 39.** `network-topology.md` reserves 39 on paper for `PrimaryPlayer`,
and three agents in this tree have already collided on a number only one of them could see.
`TransitionMovieTests.NoTwoPacketTypesShareANumber` now fails the build on any duplicate, and a
second test pins 39 as free.

A half-bump of the block version presents as "plugin shared block not found" — a broken install,
not a version skew. That warning existed for three bumps and nothing enforced it; it does now.

### 9.7 The live acceptance run

Everything is as §8.4 except that the pair must be **connected**, and the follower needs
somewhere to be. Note the staging point: `usedoor` crosses on the door's own facing axis, so
put the PC on it.

```powershell
# Both instances up and connected, both in END_M01AA, phantom + item standing.
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'moviewarp show'
#    expect: queued: nothing; armed: nothing

pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'moviewarp queue 01c'
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'dropitem g_w_blstrrfl001'
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'tp 44.5 120.1 -0.04'
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'usedoor end_door10'
pwsh tools\blockpoke.ps1 -ProcId <host pid> console 'moviewarp clear'
```

**PASS is symmetric evidence in BOTH plugin logs.** On the host, as before, plus its agent:

```
cinematic: published 1 movie resref(s) for the session: 01c
[agent] announcing 1 transition movie(s): 01c
```

On the FOLLOWER — every one of these is new, and the first two are what this pass is for:

```
[agent] transition movies pending for the next follow: 01c
[agent] following the host to 'end_m01ab' with 1 transition movie(s): 01c
cinematic: the module transition has queued 1 movie(s): .\movies\01c.bik
cinematic: staged 1 of 1 movie(s) from the session onto this transition: 01c
travel: moving to module 'end_m01ab' on the session's authority
cinematic: the main thread was gone for NNNNN ms
```

* the follower's queue line must appear **before** its `travel:` line;
* **both** stalls must be comfortably above the 5.1–5.3 s bare-load baseline (`01c` runs ~25 s
  unskipped, and the owner's was 7,407 ms);
* both flush their spawned bodies before travelling, both arrive in `end_m01ab`, neither log
  contains a `FIRST-CHANCE` access violation.

**The negative control matters as much.** Repeat with no `moviewarp queue`: neither side may
stall beyond baseline, the follower must log `following the host to '...'` with no movie clause,
and `staged N of M` must not appear anywhere. A channel that attaches a cinematic to a
transition that had none is worse than one that attaches nothing.

**FAIL signatures worth naming.** `the engine REFUSED '<name>'` on the follower means its
install lacks the file — the transition still completes, by design, and that is degradation
rather than breakage. `NOT staging ... the game has already queued` means the follower's own
engine had staged something and ours correctly stood aside. No follower queue line at all,
with the host's announcement present, puts the fault on the wire or the accumulation — check
the follower agent for `transition movies pending`.

---

## 10. Mid-module movies: the detection half, and the receiver (2026-08-22)

docs/research/trigger-relay.md §6 shipped `MovieRelayQueue` — the pure PlayNow /
DeferToNextTransition / Ignore decision for a mid-module `PlayMovie` call (no transition
involved: the Leviathan capture, the Star Forge arrival, the Taris bombardment, the Dantooine
vision) — and `coop.ini movie_relay=1`, which changed nothing, because nothing captured WHICH
movie such a call played. §9 above is the transition-queue channel; this is its mid-module
sibling, closing the gap that section's own §6 named and left open.

### 10.1 Finding the hook — VERIFIED, disassembled this pass

The task was to trace NWScript `PlayMovie` (action 733) down to the function that takes the
resref and blocks. §1.4 already has `ExecuteCommandPlayMovie 0x00540ED0` calling
`CClientExoApp::PlayMovie 0x005EDB30(&name, &local, 0, 0)` — a thunk (`mov ecx,[ecx+4]; jmp`)
onto `CClientExoAppInternal::PlayMovie 0x00602CF0`. Disassembled fresh this pass
(`tools/disasm.py 0x602CF0`, `tools/xrefs.py 0x602CF0` and `0x5EDB30`, against the same
sha256-matched `tools/kotor1_0_3.db`):

```
0x00602CF0  56                   push esi
0x00602CF1  8bf1                 mov esi, ecx           ; esi = CClientExoAppInternal*
0x00602CF3  8b86f8010000         mov eax, [esi+0x1F8]    ; the movie-suppression latch
0x00602CF9  85c0                 test eax, eax
0x00602CFB  7406                 je 0x602d03
0x00602CFD  33c0                 xor eax, eax
0x00602CFF  5e                   pop esi
0x00602D00  c21000               ret 0x10                ; FOUR stack arguments
0x00602D03  8b442408             mov eax, [esp+8]        ; arg1 = the CExoString* name
...
0x00602D2E  e8bdfdffff           call 0x602af0           ; PlayMoviesInExoArrayList — blocks
```

`xrefs 0x00602CF0` finds exactly **one** caller: the `jmp` thunk at `0x005EDB30`. `xrefs
0x005EDB30` finds exactly **two** callers in the whole binary: `0x00540F87` inside
`ExecuteCommandPlayMovie` (the NWScript command), and `0x006DCF5E` inside a function the DB's
own `functions` table resolves to `CSWGuiTitleMovies::OnMoviePicked 0x006DCF50` — the main-menu
"movies shown" gallery's replay-on-click handler. **Neither is the transition path.** A
transition's movies are played by `CServerExoAppInternal::StartNewModule 0x004BA920` →
`PlayLoadModuleMovies 0x005EDB20` → `PlayMoviesInExoArrayList 0x00602AF0` **directly** — §1.5's
own table already lists this as one of exactly two entry points into that function, the other
being `0x00602B92` inside `0x00602CF0` itself. So `0x00602CF0` is hookable at its very first
instruction and, by construction of the call graph (not an assumption about behaviour), fires
**only** for a mid-module `PlayMovie` or a menu-gallery replay — never for a transition. That
is what makes hooking it safe: no risk of double-announcing a movie the transition-queue channel
(§9) already covers, and no need to distinguish the two cases by watching a flag.

**The argument.** `ExecuteCommandPlayMovie`'s own call site pushes `(&name, &local, 0, 0)` before
the thunk. Because the thunk is a `jmp` (not a `call`), the stack is untouched when
`0x00602CF0` starts running: `arg1` — a pointer to the `CExoString` `StackPopString 0x005D1080`
just filled two instructions earlier — sits at `[esp+4]` at function entry. That is the SAME
two-field `CExoString` layout (`+0x00 char*`, `+0x04 length INCLUDING the null`) every other
hook in this codebase reads, and it is what `MoviePlayHook` captures.

**Prologue chosen for the detour:** `push esi; mov esi,ecx; mov eax,[esi+0x1F8]` — 9 bytes,
none position-dependent, verify-bytes-or-refuse exactly like `PhantomHitHook`/`PhantomEffectHook`.

`arg2` (a fresh local the caller allocates and never reads back) is **INFERRED** to be a pure
out-parameter of `PlayMoviesInExoArrayList` — not traced past `0x00602CF0` itself, and
irrelevant to the READ side (`MoviePlayHook` never touches it) though relevant to the WRITE side
(`OdysseyMovies.PlayMovieNow`, §10.3, passes a fresh scratch buffer rather than null, matching
both real callers).

### 10.2 What ships: `MoviePlayHook.cs`

`src/KotorCoop.Plugin/MoviePlayHook.cs` — the SAME jmp-detour/pushad/relocate-and-resume shape
as `PhantomHitHook`/`PhantomEffectHook`. On every call to `0x00602CF0` it copies the resref
(cutting at the first null, the CExoString length trap) into a 4-slot ring and bumps a serial.
Deliberately read-only: it never touches `arg2`, never suppresses the call, never delays it.

`src/KotorCoop.Plugin/MidModuleMovieController.cs` is the controller both halves live behind
(coop.ini `movie_relay`, default OFF until live-verified — same posture `PeerEffect` and
`CreatureLiveness` shipped with):

* **Detection.** Polls `MoviePlayHook.Sequence`; on a new capture, excludes a resref that is
  ALSO sitting in the engine's own transition queue right now
  (`MovieRelayQueue.IsAlreadyQueued`, pure, tested — defence in depth against a coincidence,
  not a case the hook can actually produce given §10.1's call-graph argument) and publishes
  what is left into the new block region.
* **Receiver.** Reads a peer's announcement the agent has already filtered to the local module,
  asks `MovieRelayQueue.Decide` using engine state only the plugin can read (`Odyssey.HasServer`
  / `IsAreaTransitionPending` / `IsMoviePlaying` for idle, `Odyssey.IsConversationActive` for
  dialogue, `Odyssey.IsPlayerInCombat` for combat, `Odyssey.QueuedTransitionMovieCount()` for
  "the game already has one queued"), and acts: `PlayNow` calls `OdysseyMovies.PlayMovieNow`
  directly (guarded by the new `MovieStaging.DecidePlayNow`, reusing the SAME refusal
  vocabulary staging already has); `DeferToNextTransition` calls the SAME guarded
  `Odyssey.QueueTransitionMovie` write `moviewarp queue` already uses
  (`MovieStaging.Decide`, `nameCount: 1`); `Ignore` does nothing.

### 10.3 The immediate-play path: `OdysseyMovies.cs`

`MovieStaging`'s existing verbs (`ModeWarpWithMovies`/`ModeQueueOnly`/`ModeClear`/`ModeShow`)
all write into the transition queue; none of them calls the engine's movie player directly. So
this pass adds the smallest such call, in a new file rather than an `Odyssey.cs` addition (the
same reason `OdysseyTriggers.cs`/`OdysseyEffects.cs` are standalone — several concurrent
worktree agents already touch that file, and `Odyssey` is not `partial`):
`OdysseyMovies.PlayMovieNow` calls `CClientExoApp::PlayMovie 0x005EDB30` — the SAME engine
entry `MoviePlayHook` watches — reproducing `ExecuteCommandPlayMovie`'s own argument shape
(`&name, &scratch, 0, 0`, matching the NWScript semantics rather than the menu-gallery's
`(..., 0, 1)`). It performs no engine-state checks of its own; every call site gates it behind
`MovieStaging.DecidePlayNow` first, the same division of labour `Odyssey.QueueTransitionMovie`
already has with `MovieStaging.Decide`.

### 10.4 Block and protocol changes

| What | Old | New | Guard |
| --- | --- | --- | --- |
| `PluginHost._block->Version` | 47 | **48** | `TransitionMovieTests.TheBlockVersionMovedInBothHalves` |
| `KotorGameBridge.PluginSignature` byte 8 | `0x2F` | **`0x30`** | same test — it reads both files |
| `SharedBlock.TotalSize` | `0x4000` | **`0x4000` (unchanged)** | the region fits the free gap `OffsetClientDoorRefusalArmed` leaves before the peer-effect region (`0x392C..0x3A00`, 0xD4 bytes) |
| `ProtocolConstants.Version` | 33 | **34** | `PacketReader` rejects a mismatch; a stale peer gets a typed `Reject` |
| `PacketType.MidModuleMovie` | — | **50** | `PacketType`'s own duplicate-number test |

New shared-block region, `0x392C..0x396C` (0x40 bytes, no `TotalSize` growth):

| Offset | Direction | Field |
| --- | --- | --- |
| `0x392C` | plugin → agent | serial (bumped last) |
| `0x3930` | plugin → agent | resref, 16 bytes null-padded ASCII (0x3940..0x394C reserved) |
| `0x394C` | agent → plugin | request serial |
| `0x3950` | agent → plugin | request resref, 16 bytes |
| `0x3960` | plugin → agent | applied (echoes the request serial) |
| `0x3964` | plugin → agent | status code (0x3968..0x396C reserved) |

`MidModuleMoviePacket(PeerId, Module, Serial, Movie)` — a single resref, unlike
`TransitionMoviePacket`'s list of up to six: a mid-module movie is one independent event with no
transition queue to ride, so the receiver decides per-movie whether to interrupt what it is
doing (§10.2), which a shared "queue everything for the next load" shape cannot express. `Module`
is where the announcer IS (not "leaving", since nothing is transitioning), and a receiver filters
on it — cheaply, in `CoopAgent.PumpMidModuleMovies` — before ever writing a request into the
plugin block, so `MovieRelayQueue.Decide`'s `sameModule`/`hasMovieName` inputs are always true
by the time the plugin sees them.

### 10.5 Tests

`tests/KotorCoop.Tests/MidModuleMovieRelayTests.cs`:

* the "already staged by transition" exclusion (`MovieRelayQueue.IsAlreadyQueued`, pure);
* the wire codec round-trip and the wrong-packet-type refusal;
* the block record, over a real `SharedBlockView` on a pinned scratch buffer (added to the test
  project's Compile list alongside `MovieStaging.cs`/`MovieRelayQueue.cs` — confirmed to compile
  in the 64-bit test host despite the project's own long-standing "SharedBlockView will not
  compile here" caution, which turned out not to apply to this class specifically: it touches no
  `Odyssey` call, no P/Invoke, nothing but `byte*` arithmetic over whatever address the caller
  supplies): the serial-bump-on-publish, the 16-byte field not overrunning its 12 bytes of
  reserved padding on an oversized name, the agent-facing request region round-tripping through
  the plugin's own accessor, and the offset math against its neighbours;
* dedup by `(peer, serial)` and the same-module filter, through a real `CoopAgent.PumpOnce()`
  against a minimal fake bridge/session (the same shape `TravelPumpTests` already uses for
  `PumpTransitionMovies`).

`MovieRelayQueueTests.cs` (already existing, from the decision-only pass) needed no changes —
its inputs did not change.

### 10.6 What this pass deliberately did not do

**No live test.** The task's own instruction was explicit: do not run the game this pass — the
rig is in use. Everything above is static RE plus unit tests. The live-test note below is what a
session with the rig should run next.

**No change to the transition-queue channel (§9).** Mid-module and transition movies remain two
separate packet types, two separate block regions, two separate controllers — a coincidence in
naming is not a reason to conflate two channels with different receiver semantics (§10.4).

**No skip mirroring**, for the same reason §3.2.3 and §9.5 already give: a skip is consumed
inside `WaitForMovie`, unobservable from outside it.

### 10.7 The live-test note

Preconditions as §5.0/§7.5: `build.ps1 -Install` on **both** installs (`demo.ps1` does not
deploy the plugin), `movie_relay=1` in both `coop.ini`s, restart the session (the gate is read
once at plugin load).

**On `END_M01AA` there is no mid-module movie to fire naturally** — its own `01a.bik` plays on
`OnEnter` before a co-op session would even be connected, and the demo save typically starts
past that point. Two ways to get a real one without walking a long way in:

1. **The Taris bombardment.** Reach `tar_m02ab`/`ac` (`console 'warp tar_m02ab'` is faster than
   walking the storyline there) and progress to the point the module fires `05` — a mid-module
   `PlayMovie` in a live save, per §4.4's inventory. Host side should log
   `movie-relay: captured mid-module PlayMovie('05') - published for the session`, and the
   OTHER instance — provided it is idle, not in dialogue, not in combat, and the engine's own
   transition queue is empty — should log `movie-relay: playing '05' now - a peer's mid-module
   movie` followed by the SAME multi-second main-thread stall `cinematic:` already knows how to
   measure (§1.3).
2. **The Dantooine vision landing**, `05_2a`/`05_2c` per §4.4 — same shape, further into the
   game.
3. **Fake one with `moviewarp`'s own verbs**, the fastest path and the one that isolates THIS
   channel from "did the story trigger even fire": `moviewarp` stages a movie onto the
   TRANSITION queue, not the mid-module path this pass built, so it cannot exercise
   `MoviePlayHook` directly — but the console's own generic script-call surface can. Play a
   movie by resref on the host through the console
   (`ConsoleCommandTable`/`FuncInterpHook`'s existing NWScript passthrough, if one reaches
   `PlayMovie`; if not, this is the one gap worth a small dedicated debug verb before the next
   live pass — `movieplay <resref>` calling `OdysseyMovies.PlayMovieNow` directly on the HOST,
   which would ALSO exercise the hook since it goes through `0x00602CF0` too) and watch whether
   the CLIENT plays it.

**Pass signal, in order, on the CAPTURING side:**

```
movie-relay: captured mid-module PlayMovie('<resref>') - published for the session
```

**Pass signal on the RECEIVING side, if idle:**

```
movie-relay: playing '<resref>' now - a peer's mid-module movie
cinematic: the main thread was gone for NNNNN ms (in a world; 0 transition movie(s) were queued going in) ...
```

**Pass signal on the RECEIVING side, if mid-dialogue or mid-combat:**

```
movie-relay: deferred '<resref>' onto this instance's own next transition
```

— followed, on that peer's NEXT transition of any kind, by the SAME `cinematic: the module
transition has queued 1 movie(s): ...` line §7/§8/§9 already validate, naming the deferred movie.

**Distrust list:** a `movie-relay: not playing ... - {reason}` line where the reason names a
transition already pending or a movie already playing is a genuine race, not a bug — the whole
point of `MovieStaging.DecidePlayNow`'s re-check is that the engine state read at DECISION time
(inside `MovieRelayQueue.Decide`) can be stale by the time `PlayNow`'s OWN re-check runs a few
instructions later; confirm which one fired using the specific reason text. A capture with no
matching publish means the exclusion (§10.1's "defence in depth") triggered — check whether the
engine's transition queue genuinely held that name at that moment (`cinematic:` channel's own
queue lines are the independent confirmation). No capture at all despite a movie visibly playing
on the capturing instance's own screen means the hook did not fire — re-run
`python tools/callaudit.py --bad` before suspecting anything else, the same first move
§7.8/§8's own crash-triage sections already recommend.

### 10.8 Live pass (2026-08-22): the test hook was built, and it found a real hang — `movie_relay` stays OFF

Option 3 of §10.7 — the "fastest, most isolated" path — was built: `movieplay <resref>` is a new
staging verb (`CoopCommandKind.MoviePlay` / `SharedBlock.CheatKindMoviePlay = 15` /
`CheatKinds.MoviePlay = 15`, following `applyeffect`'s exact shape end to end — `ConsoleCommand`
parse case, `CoopCommandDispatcher.MoviePlay`, `CheatController.MoviePlay`) that calls
`OdysseyMovies.PlayMovieNow` directly on the typist's own instance, gated by the SAME
`MovieStaging.DecidePlayNow` check `MidModuleMovieController.PlayNow` uses before calling the
identical function. It compiles clean in both the plugin (win-x86 NativeAOT) and the 64-bit test
project, and `CheatKindSyncTests` confirms the two hand-written copies of the kind number agree.

**What happened when it actually ran.** Fresh `demo.ps1` pair, both instances in `END_M01AA`,
`movie_relay=1` on both. On the HOST: `console 'movieplay biologo'` (a startup-logo asset,
3.37 MB on disk, confirmed present) via `tools/blockpoke.ps1`. The plugin logged
`movieplay: playing 'biologo' now - the main thread stalls for its length`, then — nothing else,
ever. `blockpoke ... cheat` polled every few seconds for over **4 minutes**: `CheatApplied`
never advanced past the previous serial, meaning `OdysseyMovies.PlayMovieNow` never returned.
`Get-Process` showed the process still `Responding: True` throughout, with ~33-100% single-core
CPU continuously consumed (not a deadlock in the OS-blocked sense — something is actively
spinning), and a screenshot of both game windows showed solid black — no logo frame ever
rendered. The rig was killed and relaunched (`demo.ps1 -Stop` then a fresh `demo.ps1`).

**Repeated with a different, unrelated movie to rule out a `biologo`-specific cause.** Fresh
pair, `console 'movieplay 0a'` (the Taris Star Map hologram reveal, 3.73 MB, one of the doc's own
"proven in-place `PlayMovie`" candidates per §4.4's inventory — unlike `biologo`, `0a` genuinely
is fired via `ExecuteCommandPlayMovie` in the retail game, from `k_hbas_playtat`). **Same
result**: no completion after 60+ seconds of polling (this time on the very first cheat of a
fresh session — `serial 1, applied 0` — so it is not a state left over from the first attempt),
CPU pinned near 100% on that core. Recovered the same way.

**Two different real `.bik` files, both present on disk at plausible sizes for a
several-second clip, both hung the identical call indefinitely.** This rules out "the file is
missing" and "this one specific asset is special-cased" as the explanation, and makes it very
unlikely this is a coincidence of movie choice. What it does NOT rule out:

* **A genuine hang bug in `OdysseyMovies.PlayMovieNow` or the `0x00602CF0` call itself**, unverified
  before this pass (§10.6 explicitly deferred all live testing) — the CExoString construction
  it uses (`CStrConstructor 0x005E5A90`) is the SAME address already exercised successfully
  elsewhere in this codebase (`Odyssey.cs`, `MenuLoadController.cs`, `NativeBrowserController.cs`,
  `PasswordEntryModal.cs`), so that half is unlikely to be the fault; the untested half is the
  `PlayMovieFn` call onto `0x005EDB30`/`0x00602CF0` itself — nothing in this codebase had called
  it before this pass.
* **Reentrancy.** `CheatController.OnFrame()` (and `MidModuleMovieController.OnFrame()`, which
  calls the identical `PlayMovieNow` from its own `PlayNow` branch) both run from inside
  `PluginHost.OnFrame`, which is itself the body of the `FrameHook` detour on `SwapBuffers`.
  Calling back into engine code that pumps Windows messages (`WaitForMovie`'s own
  `Global::messagepump` call, §1.3) from inside a hook ON THE FUNCTION THAT NORMALLY SIGNALS "a
  frame is done" is untested territory this codebase has not exercised before — every other
  blocking engine call this plugin makes runs either outside a hook (the console/menu paths) or
  is a bounded, non-message-pumping call. **Hypothesis, not proof**: no debugger was attached to
  confirm what the spinning thread is actually doing.
* **An environment-specific Bink/rendering limitation.** This session's very first pair-up
  produced completely normal, BOUNDED main-thread stalls during module load (563 ms, 5359 ms,
  5079 ms, all followed by more log and a working session) — but those stalls' own log lines
  read `0 transition movie(s) were queued going in`, meaning no actual `.bik` decode was
  necessarily involved. **This pass produced no confirmed case of a real movie file completing
  playback in this environment at all**, by any path, so a Bink-specific rendering/decode issue
  in this VM cannot be excluded either. `PlayLoadModuleMovies`/the transition-queue path (the
  one the earlier `01c`/`01f` escape-pod pass exercised, `b0841bf`) was NOT re-tried this
  session to distinguish these — a genuinely valuable next step, and cheaper than it sounds
  (`moviewarp <module> 01g` from a safe module, or the escape-pod conversation itself).

**Because the underlying function is untested in production and now has two independent,
reproducible hangs against it, `movie_relay` stays OFF** (`coop.ini movie_relay=0` on both rig
installs, reverted from the `=1` set for this pass; the code default,
`PluginConfig.ReadBool("movie_relay", false)`, was already `false` and is unchanged). This is
not "unverified, so left off out of caution" — it is "actively tested and found broken." The
`movieplay` verb itself ships (it is useful, self-contained diagnostic infrastructure, and its
CAPTURE half — `MoviePlayHook`'s detour on `0x00602CF0` — is a separate, cheap, synchronous
instruction-level read that this pass has no reason to distrust), but its own doc comment and
`SharedBlock.CheatKindMoviePlay`'s doc now say plainly that calling it can hang the game, so
nobody reaches for it expecting a quick answer.

**What a future pass should do, in order of cost:** (1) re-run this exact repro with a debugger
attached (WinDbg/x86, `!analyze` or a manual break during the spin) to see what the thread is
actually doing — this is the single highest-value next step and was not available this pass;
(2) test whether `PlayLoadModuleMovies` (the transition-queue path) still completes normally for
a real `.bik` in THIS environment, to separate "this call site is broken" from "movie playback
is broken here" — cheapest test: `moviewarp <any nearby module> 01g` and watch for the SAME
`cinematic: the main thread was gone for N ms` shape the `01c`/`01f` pass already validated,
this time actually reading a queue count > 0; (3) if (2) shows the transition path is fine, the
bug is narrowed to `0x00602CF0` specifically (or the reentrancy hypothesis above), and the fix
is either to defer the call to a non-hooked context (e.g. arm a one-shot flag `MainLoopHook`
reads, since that hook fires OUTSIDE `SwapBuffers`) or to find the actual root cause with a
debugger before trying again live.

### 10.9 Root-caused and fixed (2026-08-22): the call site, not the function — `movie_relay` is now ON

No debugger was available this pass either (no `cdb`/`windbg`/`procdump` on the rig), so this
went straight to §10.8's own ranked list, item (3): move the call to the call site §10.8's own
reentrancy hypothesis named as untested — H1 in the task that drove this pass. It held on the
first try, empirically, with no ambiguity.

**The mechanism, read off the code rather than guessed.** `CheatController.MoviePlay` and
`MidModuleMovieController.PlayNow` (both callers `OdysseyMovies.PlayMovieNow` has) run from
`CheatController.OnFrame`/`MidModuleMovieController.OnFrame`, which `PluginHost.OnFrame` calls
directly (`src/KotorCoop.Plugin/PluginHost.cs:796,830`). `PluginHost.OnFrame` is registered as
`FrameHook`'s callback (`PluginHost.cs:178`) — `FrameHook` detours the IAT slot for
`GDI32!SwapBuffers` (its own header comment: "the swap is the last thing in the frame, after
both `CClientExoApp::MainLoop` and `CServerExoApp::MainLoop`"). So both callers ran from INSIDE
the hooked `SwapBuffers` call, at the very end of the frame. §10.1's own call-graph shows neither
retail caller of `PlayMovie` ever stands there: `ExecuteCommandPlayMovie` runs from
`CVirtualMachine` execution, itself inside `MainLoop`'s script/AI update — before render, before
`SwapBuffers`; `CSWGuiTitleMovies::OnMoviePicked` runs from a GUI input callback, off the message
pump, never nested inside `SwapBuffers` either. `MainLoopHook` (`MainLoopHook.cs`) already reaches
that same "outside render, outside `SwapBuffers`" territory on purpose — it rewrites WinMain's own
call site for `CClientExoApp::MainLoop` so its callback runs immediately BEFORE `MainLoop`, every
frame, at the menu and in-world alike. That is exactly the deferral target §10.8's own item (3)
named.

**The fix.** `src/KotorCoop.Plugin/PendingMoviePlay.cs` (new): a one-shot `Arm(Action)` /
`RunIfArmed()` / `Cancel()`. `PluginHost.MenuFrame` (`MainLoopHook`'s callback) calls
`PendingMoviePlay.RunIfArmed()` first thing, before anything else that callback already does.
`CheatController.MoviePlay` and `MidModuleMovieController.PlayNow` no longer call
`OdysseyMovies.PlayMovieNow` themselves — each still runs its own `MovieStaging.DecidePlayNow`
check immediately (so a refusal is reported exactly as it always was, same frame), then, only on
`Stage`, copies the resref into a **heap** `byte[]` (not `stackalloc` — the buffer has to survive
into next frame's `MainLoopHook` callback, a full call-stack away, which a stack-allocated span
does not) and arms a closure that re-checks `MovieStaging.DecidePlayNow` (one frame of staleness
is real, however small — the same "distrust a stale decision" posture §10.7 already documents
for this exact check) and then calls `OdysseyMovies.PlayMovieNow` for real. Both callers own
writing their own completion field (`SharedBlockView.CheatApplied` /
`SharedBlockView.MidModulePlayApplied`) from inside the deferred action now, guarded by a
`_deferredMovieSerial`/`_deferredRequestSerial` field so `OnFrame`'s own unconditional write
doesn't mark the request "applied" a frame before the engine call has actually run.
`PluginHost.InvalidateAll` calls `PendingMoviePlay.Cancel()` on a module change, alongside the
two controllers' own `Invalidate()`, for the same reason every other cross-frame pending write in
this codebase gets dropped there.

**Live result: the hang is gone.** Fresh `demo.ps1` pair, `movie_relay=0` (isolating the fix from
the relay), both in `END_M01AA`. Host: `console 'movieplay biologo'` — plugin log:

```
[05:21:09.241] movieplay: 'biologo' staged - deferred to the pre-MainLoop callback (...), plays next frame
[05:21:09.242] movieplay: playing 'biologo' now - the main thread stalls for its length
[05:21:12.972] WARNING: frame hook installed but has not fired for ~2s (normal while a movie or a module load holds the main thread)
[05:21:18.006] cheat: played 'biologo' (returned from the block) (status 1)
[05:21:18.007] cinematic: the main thread was gone for 8765 ms (in a world; 0 transition movie(s) were queued going in) ...
```

`CheatApplied` advanced, the `cheat` verb read back `status 1` / `'played 'biologo' (returned
from the block)'`, and the stall (8765 ms) is the length a 3.37 MB `.bik` actually implies — not
the 4+ minutes of spinning §10.8 measured for the exact same file from the exact same function.
`console 'movieplay 0a'` immediately after, same instance: staged 05:21:39.996, `CheatApplied`
advanced with `status 1` at 05:21:49.574, stall 9593 ms. **Two different real movies, two clean
returns**, both `Get-Process` `Responding: True` throughout with CPU that settled back to normal
afterward (the busy-spin CPU reading in §10.8 was never diagnostic on its own — `WaitForMovie`'s
own disassembly, §1.3, is a tight loop with no `Sleep`, so pinned CPU is what a WORKING wait looks
like too; what was diagnostic is that `IsMoviePlaying()` now actually returns false). A
post-movie `state` read showed the frame counter still climbing and the phantom/remote counts
intact — the session kept running normally, not just the one call. A screenshot taken right after
came back solid black for both windows, same as the (inconclusive) screenshots §10.8 flagged as
suspicious — but the plugin's own log, the block's own completion fields and the world's own
continued tick all agree the movie played and finished, so this is a pre-existing screen-capture
limitation in this rig's environment (H3-shaped, but about the TOOLING, not the engine's Bink
path), not evidence against the fix.

**Hypotheses, resolved:**

* **H1 (call-site/re-entrancy) — CONFIRMED, this is the bug.** Moving the identical call three
  frames earlier in the same process, same build, same `.bik` files, turned an unconditional
  4+-minute/60+-second hang into an 8.7s/9.6s return that matched the files' real lengths, with
  no other change. That is as close to a controlled experiment as this rig can run without a
  debugger, and it is unambiguous.
* **H2 (bad trampoline in `MoviePlayHook`) — REFUTED.** `MoviePlayHook`'s detour never moved and
  is unrelated to `PlayMovieNow`'s call site — it watches `0x00602CF0`'s ENTRY, `PlayMovieNow`
  calls it via the THUNK at `0x005EDB30` (a completely different address, §10.1). Every capture
  in this pass read the correct resref (`'biologo'`, `'0a'`) with the correct length every time,
  across a dozen-plus calls including the relay round below — a bad jump-back would either crash
  or corrupt the capture, not simply relocate the hang to a different call site while leaving the
  capture perfectly accurate.
* **H3 (Bink/environment cannot play movies here) — REFUTED.** Both files decoded and played to
  completion, for a duration matching their real length, with the main thread returning normally
  afterward. The solid-black screenshot is real and still unexplained, but it is now clearly a
  SEPARATE, narrower issue (screen capture of this window in this environment) rather than "Bink
  cannot render here."

**A second bug the fix surfaced: an unbounded relay replay loop.** With the hang gone, the next
test was the actual relay (`movie_relay=1` on both, fresh session). Host `console 'movieplay
biologo'` played and returned as above, and 8.8s later — the delay is `HandleCapture` only
running on the NEXT `SwapBuffers`-nested `OnFrame` after the blocking call itself returns —
captured and published:

```
host:   [05:24:03.775] movie-relay: captured mid-module PlayMovie('biologo') - published for the session
client: [05:24:03.938] movie-relay: 'biologo' staged - deferred to the pre-MainLoop callback, plays next frame
client: [05:24:03.939] movie-relay: playing 'biologo' now - a peer's mid-module movie
```

The relay itself worked — but the client's own `PlayMovieNow` call, made to SERVE the host's
request, goes through the SAME `0x00602CF0` `MoviePlayHook` watches, so it was captured too, and
republished right back:

```
client: [05:24:12.706] movie-relay: captured mid-module PlayMovie('biologo') - published for the session
host:   [05:24:12.760] movie-relay: 'biologo' staged - deferred to the pre-MainLoop callback, plays next frame
host:   [05:24:12.761] movie-relay: playing 'biologo' now - a peer's mid-module movie
host:   [05:24:21.526] movie-relay: captured mid-module PlayMovie('biologo') - published for the session
```

— and so on, both instances replaying `biologo` back and forth every ~8.7–9s, unbounded, until
the rig was torn down and relaunched. §10.2's existing exclusion (`IsAlreadyQueuedByTransition`)
does not catch this: it only excludes a resref already sitting in the engine's TRANSITION queue,
and a relayed `PlayNow` never touches that queue at all. **Fix**, in
`MidModuleMovieController.PlayNow`'s deferred action, right after `OdysseyMovies.PlayMovieNow`
returns: `_lastHookSerial = MoviePlayHook.Sequence;` — consumes the capture this instance's OWN
relay-driven call just produced before `HandleCapture`'s next poll can see it as "new," so it is
never republished. A movie played because a SCRIPT or the `movieplay` verb fired it (the intended,
still-captured case §10.7's own design asks for) is untouched — only a play made to serve a peer's
request is suppressed, and only that one call's single capture. `CheatController.MoviePlay`
needed no equivalent change: `DeferToNextTransition`'s write goes through
`Odyssey.QueueTransitionMovie`, a completely different path `0x00602CF0` never touches (§10.1),
so it was never at risk of this loop.

**Live result after the loop fix: one clean round trip, then silence.** Same rig, rebuilt,
reinstalled, fresh pair. Host `console 'movieplay biologo'`:

```
host:   [05:27:24.061] movieplay: 'biologo' staged - deferred to the pre-MainLoop callback (...), plays next frame
host:   [05:27:24.061] movieplay: playing 'biologo' now - the main thread stalls for its length
host:   [05:27:32.831] cheat: played 'biologo' (returned from the block) (status 1)
host:   [05:27:32.837] movie-relay: captured mid-module PlayMovie('biologo') - published for the session
client: [05:27:33.030] movie-relay: 'biologo' staged - deferred to the pre-MainLoop callback, plays next frame
client: [05:27:33.030] movie-relay: playing 'biologo' now - a peer's mid-module movie
```

— and nothing further from either side. Both `movie-relay:` line counts (8 and 8 at that point)
held steady across a further 10+ seconds of idle polling and a further 15 seconds after the defer
test below; the client's log tail through that window is ordinary gameplay (`gait:` sync lines),
not another replay.

**The Defer case, live.** Same session: `talk end_trask` on the client via `blockpoke.ps1` opened
a real conversation (`dialogue` readback: `speaker 'Trask'`, `partner 'end_trask'`). With the
client mid-dialogue, host `console 'movieplay 0a'` (a different movie, to keep the evidence
unambiguous) played and returned normally (9578 ms), captured and published as before. The
client, busy, deferred instead of playing:

```
client: [05:29:03.921] movie-relay: deferred '0a' onto this instance's own next transition
client: [05:29:03.921] cinematic: the module transition has queued 1 movie(s): .\movies\0a.bik
client: [05:29:03.921] cinematic: published 1 movie resref(s) for the session: 0a
```

The second and third lines are the SAME transition-queue confirmation §7/§8/§9 already
live-validated for a real transition actually playing a queued movie — the new part this pass
exercised is only the RELAY writing into that queue (`Odyssey.QueueTransitionMovie`, gated by
`MovieStaging.Decide`), not the queue's own playback, which earlier passes already settled. No
`movie-relay: captured` line followed on the client for `'0a'` (confirming `DeferToNextTransition`
never touches `0x00602CF0`, as the design argued) — so no ping-pong risk on this path either.

**Both processes stayed healthy throughout** (`Get-Process` `Responding: True`, CPU in the normal
range once each blocking call returned) and a final `demo.ps1 -Stop` tore the rig down cleanly.

**Both builds clean, full suite green.** `dotnet build` on `KotorCoop.Plugin.csproj` (win-x86):
0 errors, 1 pre-existing unrelated warning (`PortraitController.cs` CA2014, not touched this
pass). `build.ps1 -Install` against both GOG installs (`C:\GOG Games\Star Wars KOTOR (GOG)`,
`D:\Games\swkotor-gog-p2`): both clean. `dotnet test tests/KotorCoop.Tests`: **1467/1467**,
matching the baseline — `PendingMoviePlay.cs` is pure managed code with no engine addresses, but
was not added to the test project's own compile list this pass (no behaviour in it is testable
without a real frame-hook callback sequence; its `Arm`/`RunIfArmed`/`Cancel` shape is exercised
live above instead, the same way `MoviePlayHook`'s own detour is).

**`movie_relay` default flips to ON** (`coop.ini movie_relay=1`,
`PluginConfig.ReadBool("movie_relay", true)` in `MidModuleMovieController.cs`,
`profiles/coop.ini.example` updated to match) — per this pass's own instruction to flip it only
if both the play-now and defer paths passed live, and both did, cleanly, with the loop bug fixed
in the same pass rather than left for later.

**What remains.** No debugger ever attached this pass (still not available on the rig) — the
empirical A/B (same call, two call sites, one hangs and one doesn't) is strong enough that this
pass did not chase one down, but a future session with `cdb`/WinDbg could still say PRECISELY
what the spinning thread was doing at the SwapBuffers-nested call site, which remains inferred
(re-entrancy into a message-pump-adjacent engine state) rather than directly observed. The Defer
case's own eventual playback (the client's NEXT transition actually running `0a.bik`) was not
walked to completion this pass — only the relay's WRITE into the transition queue was new and
needed verifying; the queue's own playback was already settled by the `01c`/`01f` escape-pod pass
(`b0841bf`). Combat-case relay (`inCombat` → `Ignore`, per `MovieRelayQueue.Decide`) and a
second, DIFFERENT movie racing a genuine story-triggered mid-module `PlayMovie` (Taris
bombardment `05`, per §10.7's own inventory) remain untested live, same as before this pass.

---

## Key addresses

| Address | Symbol / meaning |
| --- | --- |
| `0x005EDB40` | `CClientExoApp::IsMoviePlaying` — `__thiscall`, **no args** (chain bottoms at `0x00404DD0`, a bare `ret`) |
| `0x005F2BD0` / `0x0070C1F0` / `0x00404DD0` | the rest of that chain; `movie_player` at `CClientExoAppInternal+0x12C`, playing flag at `CExoMoviePlayerInternal+0x30` |
| **`0x005F6F20`** | **`CClientExoAppInternal::WaitForMovie`** — the blocking spin loop `0x005F7063`–`0x005F70B5`; runs only `Global::messagepump 0x00401510`; skip flag `[clientInternal+0x254]`; cleanup `RemoveMoveToModuleMovies` + `SetGlobalUnpauseableState(0)` |
| `0x00603003` / `0x0060300F` | MainLoop's own `IsMoviePlaying` test and `WaitForMovie(1)` — inside the in-world block, which is why the MENU movie never blocks |
| `0x00602AF0` | `CClientExoAppInternal::PlayMoviesInExoArrayList` — takes the pause lock (`SetGlobalUnpauseableState(1)` at `0x00602B09`), calls `WaitForMovie` at `0x00602BA6` |
| `0x005EDB30` / `0x00602CF0` | `CClientExoApp::PlayMovie` (`ret 0x10`, four args) → internal, which appends to `+0x2D8` and plays it. **§10: `MoviePlayHook`'s detour point, `0x00602CF0`'s prologue (`push esi; mov esi,ecx; mov eax,[esi+0x1F8]`, 9 bytes)** |
| `0x006DCF50` | `CSWGuiTitleMovies::OnMoviePicked` — the main-menu gallery's replay-on-click; the OTHER (harmless) caller of `0x005EDB30`, per `xrefs 0x005EDB30`. **§10.1** |
| `0x00540ED0` | `CSWVirtualMachineCommands::ExecuteCommandPlayMovie` — NWScript `PlayMovie`; pops the name, `HideLoadScreen(0,1)`, then `PlayMovie` |
| `0x005481E0` / `0x00548210` / `0x005482D0` | script `IsMoviePlaying` / `QueueMovie` / `PlayMovieQueue`; `movie_queue` at `CClientExoAppInternal+0x2E4` |
| **`CClientExoAppInternal+0x2D8`** | **`move_to_module_movies : CExoArrayList<CExoString>`** — the transition queue |
| `0x005EDB50` → `0x006027A0` | `CClientExoApp::AddMoveToModuleMovie(CExoString*)` — appends to it; **the design lever, and now CALLED** (§7). `__thiscall`, `ret 4` at `0x006027B1`, and EAX carries `AddMovieToExoArrayList`'s verdict straight out |
| `0x005FBBF0` | `CClientExoAppInternal::AddMovieToExoArrayList(CExoString*, CExoArrayList*)` — `ret 8`. Refuses `""`; for a name with no `:` opens `MOVIES:%s` and returns **0, adding nothing**, if the file is not there; adds through `AddUnique 0x005F7C10`, so a duplicate is dropped silently |
| `0x005EE380` | `CClientExoApp::RemoveMoveToModuleMovies` — `mov eax,[ecx+4]; mov [eax+0x2DC],0; ret`. A bare `ret`: no arguments. Zeroes the list's SIZE field; `moviewarp clear` is this |
| `0x005EDB60` | `CClientExoApp::GetMoveToModuleMovies` — `mov eax,[ecx+4]; add eax,0x2D8; ret` |
| `0x005EDB20` → `0x00602650` | `CClientExoApp::PlayLoadModuleMovies` — one caller only |
| **`0x004BA920`** | **`CServerExoAppInternal::StartNewModule`** — calls `PlayLoadModuleMovies` at `0x004BA9CA`; one caller, `CServerExoAppInternal::MainLoop 0x004BABB0` at `0x004BAD93`. **The co-op warp path lands here too.** |
| `0x00544390` | `ExecuteCommandStartNewModule` — NWScript `StartNewModule(module, waypoint, movie1..6)`; six `AddMoveToModuleMovie` calls at `0x0054459E`–`0x005445DA` |
| `0x006CA250` | `CSWGuiSaveLoad::LoadPCAutoSave` — restores six queued transition movies (`0x006CA8B8`–`0x006CA91E`) |
| `0x0050D6C0` | `CSWSArea::EventHandler` — queues a transition movie at `0x0050DA9F` |
| `0x005EE160` / `0x0070C1E0` | `CClientExoApp::CancelMovie` / `CExoMoviePlayer::CancelMovie` (`RemoveMoveToModuleMovies` is above) |
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
| **`0x0058B850`** | **`CSWSDoor::EventHandler`** — the door transition, in two phases: `0x0058C230`-`0x0058C315` raises `SetTransitionPending(1, id)` + a 0.5 s fade, and `0x0058C185`-`0x0058C1BE` does the same three move-to-module calls a co-op warp does, then `SetTransitionPending(0, 0)`. **§8.2** |
| `0x00506B30` / `0x00506AC0` | `CSWSArea::SetTransitionPending(int, char)` — writes `trans_pending` at **CSWSArea+0x2C4**, the byte `Odyssey.IsAreaTransitionPending` reads / `GetNextTransitionPendingID` |
| `0x00635350` @ `0x0058C228` | **`CSWParty::IsPartyTogether`** — bare `ret`. False here and the door runs `k_trg_transfail` (`0x007499C4`) INSTEAD of transitioning, with no trace. Measured live, and the reason `usedoor` gathers |
| **`0x00565B00`** | **`CSWPartyTable::PositionParty`** — bare `ret`, no args; the engine's own party collection. One caller: `CServerExoAppInternal::MovePlayerToArea 0x004B3D10` @ `0x004B3E9E`. Uses `ComputeSafeLocation 0x004AEB60` + `SetPosition`/`SetOrientation` per member, resets follow data (`0x00637890`), clears `pt_solomode` (+0x190). ⚠ **CSWPartyTable+0x10C is a one-shot skip flag** tested at `0x00565B6E`: set means this call moves nobody and only clears it |
| `0x004AEE70` | `CServerExoApp::GetPartyTable` — `mov eax,[ecx+4]; add eax,0x1B770; ret`. The table is embedded, so it cannot be null while the server lives. +0x00 member count, +0x04 ids |
| `0x005235B0` | `CSWSMessage::HandlePlayerToServerInputWalkToWaypoint` — a complete implementation that **nothing ever sends** (input type 1; all 17 `SendPlayerToServerMessage 0x00677410` sites enumerated). Dead code, and the reason `blockpoke walk` cannot move the PC |
| **`0x0058C594` / `0x0058C5C8`** | **The door EventHandler's event jump table.** Only event **`0x1E`** reaches the transition arm (`0x0058C0BE`). Using an object posts **`0x1A`** (`AIActionUseObject` `0x0057ECC0`, `AIActionOpenDoor` `0x0057D661`), which lands at `0x0058BD62` — the OnUsed-script arm, and no transition |
| **0x1E is built ONLY by movement** | `HandleSubAreaStateChanges 0x00516020` (×3, and only for party index 0 — `0x00516263`), `WalkUpdateLocation 0x0051BB10` (×2), `DriveUpdateLocation 0x0051CD20`, `WalkUpdateLocation_QuickWalk 0x0051D9C0`, `AIActionMoveToPoint 0x0051F4F0` (×2). **A transition is a creature CROSSING INTO the door, noticed by its own movement update** — which is why a `tp` snap can never fire one |
| `0x00677D10` | `CSWCMessage::SendPlayerToServerInput_UseObject` — `ret 4`. ⚠ The **placeable** message: one caller, `CSWCPlaceable::UsePlaceable 0x00682660`. Server side type `0x0B` -> `0x00525B1A` -> `AddUseObjectAction 0x0057C810` -> `AIActionUseObject`, which walks to the action point and stops SHORT of the threshold. Cannot transition |
| `0x00677D70` / `0x00683D90` | `SendPlayerToServerInput_ChangeDoorState` (`ret 8`, `0x2725` opens) / `CSWCDoor::ToggleDoorState`, its only caller. The door input message — and it cannot transition either, because it posts `0x1A` too |
| **`0x00589C70`** | **`CSWSDoor::OpenDoor(openerId)`** — `ret 4`. Picks swing state 1 or 2 from `dot(opener − door, orientation@+0x9C)` (`0x00589C97`), records the opener at +0x31C, `SetOpenState(state, deferred)`, opens the LINKED twin (`GetLinkedObject 0x00589580`), runs OnOpen at +0x228. Ignores the lock |
| `0x00589680` → `0x00508180` | `MoveToNextOpenState` commits pending → current and calls **`CSWSArea::BudgeCreatures`** with the door's position and its open bit — the update that stops the area treating the door as solid. **A closed door is a pathing obstacle**, which is why a walk-through ends at its face |
| `0x005890D0` | `CSWSDoor::GetIsLinked` — `linked_to_flags` at **CSWSDoor+0x384**, 1 or 2 = a module transition. `linked_to` +0x388, `linked_to_module` +0x390 |
| `0x0073D00C` / `0x004046BD` | the SwapBuffers IAT slot (movie-gated, so `FrameHook` is dead during a movie) / the `MainLoop` call site `MainLoopHook` rewrites |
