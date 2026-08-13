# Spectate-on-down: the engine's camera handoff (2026-08-14)

How the third-person camera decides what to follow, the one engine call that moves it,
and the controller that gives a downed player their partner's view instead of a corpse
close-up. Verified against the GOG retail 1.0.3 binary by disassembly and then live on
the tat_m20aa pair.

## The engine's own mechanism — VERIFIED by disassembly

**`CSWCModule::SetCameraFollow(uint clientObjectId, int bImmediate)` at `0x0063F9D0`**
(`__thiscall` on the CLIENT module, `[clientInternal + 0x18]`) is the single funnel every
engine-initiated camera move goes through. Its callers, from `tools/xrefs.py`:

| Caller | Site |
| --- | --- |
| `CClientExoAppInternal::AttachPlayerGameObjectToArea` (area login) | `0x5F5D8B` |
| `CClientExoAppInternal::ChangeCharacter` (the party control swap) | `0x5F6C27` |
| `CSWCMessage::HandleServerToPlayerCreatureUpdate_ControlObject` | `0x64CBB8`, `0x64CC76` |
| `CSWCCreature::ReAttachCamera` (deferred pending attach) | `0x60E736` |

Every shipped call passes `bImmediate = 1`. Body facts that matter to a caller:

- Resolves the id via `CClientExoApp::GetCreatureByGameObjectID` (`0x5ED5B0`) — so the
  argument is a CLIENT id: server id `| 0x80000000`, the documented
  `ServerToClientObjectId` conversion (`0x5F2AC0`).
- **Early-outs while `CClientExoApp::GetCombatMode()` (`0x5EDE70`) is non-zero** — the
  weapons-drawn UI state. A caller must treat the call as a LEVEL and re-assert.
- Module not fully loaded (`module+0xC != 3`): queues into `module+0x314/0x318`, which
  `ReAttachCamera` services later — mid-load calls are safe by design.
- Reuses the installed `CAurBehaviorCameraFollow` (behaviour id `0x106A` on the manager
  at `module+0x40`); allocates one (0x138 bytes) only when absent, so re-assertion does
  not leak. The behaviour holds the followed CLIENT creature at `+0x14` and reads its
  position from `+0x78` (`GetTargetWorldPosition 0x49F0E0`).

`Odyssey.SetCameraFollowServerObject` / `RestoreCameraToControlledCreature` wrap exactly
this: client app → client module → `SetCameraFollow(id, 1)`, with the restore taking the
controlled creature's own id from `CClientExoApp::GetPlayerCreature` (`0x5ED540`).

## The controller

`SpectateController`, after DeathController each frame, re-asserted every 30 frames:

- **Gate = the CONTROLLED creature is downed** (`GetPlayerCreaturePointer` +
  `IsDowned`), not the co-op down flag alone. On the host the engine's own
  `ChangeCharacter` swaps a downed player onto a living companion — the controlled body
  is then alive, the player is still playing, and spectate must not steal that camera.
  On a client the swap is suppressed (companions are host-driven mirrors), the
  controlled body IS the corpse, and spectate fills exactly that hole.
- Target = first peer slot with `Active && Health > 0` whose phantom resolves.
- Holds through conversations (the dialogue camera owns the view), retargets when the
  watched peer dies, and restores through the same engine call on revive.
- Kill switch: `spectate_on_down=0`.

## Live-verified, all three transitions (tat_m20aa)

```
05:58:37.972  death: the local player is down
05:58:38.621  spectate: down — watching the player behind phantom 0xBC     (engage, 0.6 s)
05:59:46.873  spectate: nobody left standing to watch                      (the watched host
                                                                            went down mid-spectate;
                                                                            camera restored, honestly)
06:02:58.556  spectate: down — watching the player behind phantom 0xBC     (re-engage next down)
06:03:30.423  spectate: back up — the camera is yours again                (26 ms after revive)
```

The host side stayed silent through its own downs — the engine swapped it onto Trask and
the controlled-creature gate held, which is the designed asymmetry.

## Rig lessons paid for on the way (they cost most of the session)

1. **The agent's event voice was never plugged in.** `CoopAgent.Log` and
   `SessionManager.Log` had existed for the life of the project with nothing assigned —
   every "X revived you", channel decision and pump failure was silently discarded. Now
   wired through `AgentDashboard.Note` into `--log` (lines prefixed `* `). The first
   thing the restored voice said solved a "bug": `* Lanik revived you (1.4 units: they
   -26.3,67.7 you -27.4,66.8)`.
2. **Both players arrive ~1 unit apart** at a warped module's start waypoint, so any
   down within seconds of arrival is instantly rescued by the proximity revive
   (3.5-unit range). Death-path tests must separate the players FIRST — the "instant
   revive bug" chased across three staging attempts was the rescue working correctly.
3. **A walk order to an unreachable point silently no-ops** (`blockpoke walk 2 44` from
   the tat_m20aa waypoint never moved — blocked path), so "I ordered it away" is not
   "it is away": verify position before staging anything that depends on distance.
4. **`shot.ps1` cannot capture the 3D viewport** (OpenGL does not BitBlt; menus and GUI
   only) — camera verification is logs plus the follow behaviour's target, not pixels.
5. **A `dotnet build` that reports success while agents are running can still leave the
   OLD binaries deployed** — the copy step is what fails. Verify with a string probe
   (`strings -el`) before trusting a rebuilt agent.
