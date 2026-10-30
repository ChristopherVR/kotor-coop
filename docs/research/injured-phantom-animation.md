# Injured peer animation (2026-10-30)

The "Explore together" clip showed a low-health player holding their stomach on
their own screen while their remote stand-in remained upright. The September 17
health-copy fix was necessary but insufficient: `DrivePhantom` selected only normal
idle/walk/run, and `DriveBody` wrote that selection to the client twin's looping
animation (`+0x58`) and its animation/position handlers. That overrode the engine's
injury presentation. A stationary health change also left the normal gait latch
unchanged, so it could not trigger a pose update.

## Engine evidence

Read from the installed GOG 1.0.3 executable with `tools/disasm.py`:

- `CSWCCreature::GetPauseReadyAnimation`, `0x0060F020`, is a no-argument thiscall
  returning an animation ID (plain `ret`). It checks combat readiness first,
  then the server's injury decision and the client presentation restrictions.
  The injured branch returns `0x276C`; the normal branch returns `0x2710`.
- `CSWSCreature::IsInjured`, `0x004EFF30`, uses the effective current/max HP
  ratio (below 20%), with an additional creature-state exclusion.
- The movement branches at `0x00615B92` and `0x00615C0A` select injured run
  `0x276E` and injured walk `0x276D`. The server setter also substitutes those
  movement IDs, but it does not select the injured idle.

## Change

After speed/hysteresis chooses the phantom's ordinary gait, consult the engine's
pause/ready decision on its validated creature twin. If it reports injured idle,
map the normal idle/walk/run to `0x276C/D/E` before driving either half of the body.
The resolved ID reaches the existing change latch, so injury and healing update
the pose even while stationary, while unchanged states do not restart animations.
Run hysteresis recognises both normal and injured run IDs.

Downed bodies continue to bypass the drive. Dialogue stunt and drive-bisection
guards bypass the added query. No wire/shared-memory layout change is needed.

## Validation

- Native x86 plugin built with `build.ps1 -WarnAsError`.
- Gait regression tests cover idle/walk/run injury and healing, unchanged-state
  latching, preservation of authored animations, and injured-run hysteresis.
- Two disposable GOG copies on private desktops, with the candidate plugin on
  both, connected through loopback. Local artifacts:
  `artifacts/injured-gait-20261030/results.json` and the adjacent screenshots.
- Host at 1/12 HP: the joiner's stand-in held its stomach. Server `+0xD4`, twin
  current `+0x48`, and twin loop `+0x58` all read `0x276C` across eight samples.
  The diagnostic resolved its model animation to `pauseinj`.
- Healing to 12/12 restored all three IDs to `0x2710`. Repeating injury and
  healing from joiner to host produced the same results.
- Moving the injured host with native input produced `0x276E` on the receiver
  (`runinj`), stopping restored `0x276C`, and healing restored `0x2710`.
  Injured walk selection is unit-tested; a separate live walk was not established.

This is a targeted animation check, not a full combat, death/revive, or campaign
regression pass. Existing published showcase media has not been replaced.
