# KOTOR Co-op 0.1.0-beta.8

**KMRP startup compatibility fix.** This experimental beta fixes a loader conflict
that could stop KOTOR from starting when co-op was installed alongside KMRP's
optional Modern Driver Compatibility component. See [issue #1](https://github.com/ChristopherVR/kotor-coop/issues/1).

## What changed

- **Ultimate ASI Loader keeps its required filename.** Co-op now uses its
  `dinput8Hooked.dll` original-library slot instead of renaming the ASI loader to
  an unsupported name. Existing ASI mods keep their normal locations.
- **Setup repairs older affected installs automatically.** It restores an ASI
  loader previously kept as `dinput8.prev.dll`. If another mod owns the hooked
  slot, setup refuses to overwrite it. Windows and Linux installers use this rule.
- **Better startup support reports.** Reports include the DirectInput chain,
  KMRP patch configuration, known KMRP/controller settings and logs, and the
  driver compatibility log when present, with the existing text redaction.
- Includes the previously unreleased setup, recovery controls and player-support
  improvements: F9 connection help, address copying, retry/resume-hosting actions,
  and in-game support-report access.

## Updating an affected installation

1. Close KOTOR and `KotorCoop.Agent.exe`.
2. Run this release's setup program against your game folder. Alternatively,
   extract the Windows ZIP into a **separate folder** and run its
   `KotorCoop.Agent.exe`.
3. Let setup repair the loader layout, then launch KOTOR normally.

Do not extract over another mod's DLLs or manually delete unknown files. If setup
reports an occupied `dinput8Hooked.dll` slot, keep the files and report the conflict.
Both players should update to the same co-op version and use matching game mods.

## What was tested

The startup failure pattern was reproduced with KMRP **1.5.1**, its bundled K1
Modern Driver Compatibility **1.2.0**, and High FPS Fix **1.0.1**, using the original
Steam KOTOR executable on Windows 11 / NVIDIA RTX 5080. The old layout produced
ASI-loader errors or access-violation crashes in all six driver-enabled attempts.
Changing only the loader layout reached the main menu with the Co-op button bound
in **all six retests**, including with both optional components enabled. High FPS
alone also started successfully.

These were controlled startup tests in disposable game copies on private desktops,
not a multiplayer playthrough or a full interactive-installer test. The reporter's
exact environment is still unconfirmed. Other KMRP versions, GPUs, operating
systems and full KMRP multiplayer gameplay remain unverified.

**Still experimental:** back up your saves and start with two players. A complete
co-op campaign and independent-PC internet scenarios are not certified by these
startup tests. If startup still fails, run `KotorCoop.CollectLogs.cmd`, review the
report for personal information, and attach it to issue #1 with your game/mod
versions and enabled KMRP components.
