# kotor-coop {{VERSION}}

Two-player co-op for **Star Wars: Knights of the Old Republic** (the DRM-free **GOG** build,
v1.0.3). Two full copies of the game run side by side — on one machine or across a network —
and this package bridges them into one shared world: you see each other as real in-game
characters, travel, fight, loot, talk and pause together.

No game file is modified. Everything installs beside `swkotor.exe` and can be removed again
with one command.

## What is in this zip

| Path | What it is |
| --- | --- |
| `install.ps1` | The installer. Run it; it copies everything below into your game directory. `-Uninstall` takes it all back out. |
| `plugin\dinput8.dll` | The in-process plugin. The game loads it itself because Windows looks in the game's own folder for `dinput8.dll` before the system one. |
| `agent\` | `KotorCoop.Agent.exe` and its files — the external process that does the networking. The plugin starts it for you. |
| `loader\dinput8.dll` | A tiny generic wrapper for when **another mod already uses `dinput8.dll`** (ReShade, an ASI loader…). See "Sharing dinput8.dll with another mod". |
| `loader\dinput8.chain.example` | The list file the wrapper reads, annotated. |
| `override\k_coop_*` | A handful of game assets (your partner's body blueprint, a spawn point). Every one is prefixed `k_coop_`, so they never collide with a mod. |
| `coop.ini` | The configuration template, installed only if you do not already have one. |

## Requirements

- Windows, and the **.NET 10 runtime** (the agent is a .NET app; Windows will offer to
  install the runtime the first time it starts if it is missing).
- **KOTOR 1 from GOG.** Steam's copy refuses to run twice and is packed differently; the
  engine addresses this mod relies on are verified against the GOG binary only.
- For two players on **one** machine: a second copy of the game folder. Install this package
  into both.

## Install

Extract the zip anywhere, open a PowerShell window there, and point the installer at the game:

```powershell
pwsh .\install.ps1 -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'
```

(`powershell.exe` works too if you do not have `pwsh`.) That is the whole install. It prints
every file it placed. Re-running it over an existing install upgrades in place and leaves your
`coop.ini` untouched.

## Play

1. Launch KOTOR normally.
2. Click **Co-op** on the main menu.
3. One player presses **Host**; the other picks that server from the list (or types the
   host's address) and presses **Connect**. A password set in `coop.ini` is asked for.
4. Load a save or start a new game. Your partner appears next to you.

Press `~` in game for the console: `connect <ip> [password]`, `disconnect`, `reconnect`,
`say <message>`. Tab completes, Up/Down recalls history.

The host is authoritative for enemies and the party; each player owns their own character.
Only one conversation runs at a time — the other player watches it live.

## Configure

`coop.ini` beside `swkotor.exe` is the only config file. The important keys:

| Key | Meaning |
| --- | --- |
| `mode` | `host` or `join`. The in-game menu sets this for you; it matters for the console `connect`. |
| `host` / `port` | Address and UDP port (default `27140`). The host's port must be reachable from the client. |
| `password` | Optional. Must match on both sides. |
| `name` | Your name as shown to your partner. |
| `menu_button` | `1` shows the Co-op button on the main menu. |
| `auto_start_agent` | `1` (default) lets the plugin start `KotorCoop.Agent.exe` for you. |

Every other key is documented in the file itself and in the project's `docs/CONFIG.md`.

## Sharing dinput8.dll with another mod

Only one file can be named `dinput8.dll`. If one is already in your game folder that is not
ours, the installer stops and says so. You then have two options:

```powershell
# Keep the other mod: the wrapper becomes dinput8.dll and loads both, in order.
pwsh .\install.ps1 -GameDir '...' -Chain

# Same, and also load extra DLLs (paths relative to the game folder).
pwsh .\install.ps1 -GameDir '...' -Chain -Load ReShade32.dll, some-mod.asi

# Or overwrite it (that mod stops working).
pwsh .\install.ps1 -GameDir '...' -Force
```

`-Chain` installs `loader\dinput8.dll` as `dinput8.dll`, puts our plugin in as
`KotorCoop.Plugin.dll`, renames the mod that was there to `dinput8.prev.dll`, and writes
`dinput8.chain`:

```
KotorCoop.Plugin.dll
dinput8.prev.dll
```

How the chain works: the wrapper loads every listed DLL and hands the game's DirectInput calls
to the **first** one that is itself a `dinput8` proxy (ours). Our plugin then forwards to the
entry **after** itself, and the last proxy forwards to Windows' real `dinput8.dll`. A DLL that
only needs to be loaded (an `.asi`) can sit anywhere in the list; a second proxy should come
after ours. You can edit `dinput8.chain` by hand at any time; `dinput8-chain.log` beside the
game records what the wrapper did on the last launch. `coop.ini` also accepts
`dinput8_next = <file>` to point the plugin at a specific next DLL regardless of the list.

## Uninstall

```powershell
pwsh .\install.ps1 -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)' -Uninstall
```

Removes only what it installed (it recognises its own `dinput8.dll` by a marker and leaves any
other mod's alone, restoring `dinput8.prev.dll` to its original name), keeps your `coop.ini`.

## How it works, briefly

```
swkotor.exe (x86)                                         swkotor.exe (x86)
  dinput8.dll = plugin: frame hook, engine calls,           dinput8.dll = plugin
  a shared memory block the agent reads/writes
        |                                                        |
  KotorCoop.Agent.exe (x64, started by the plugin)          KotorCoop.Agent.exe
        \______________________ UDP ____________________________/
```

The plugin lives inside the game and does the in-engine work (spawning your partner's body,
mirroring enemies, doors, items, dialogue). The agent lives outside, attaches to the game
process, and speaks UDP to the other player's agent. Clients talk only to the host.

## Something is wrong

- **No Co-op button:** `menu_button = 1` in `coop.ini`.
- **Partner never appears / "no reply from host":** ports must match and the host's UDP port
  must be open; passwords must match exactly.
- **Nothing happens at all:** look for `kotorcoop-plugin.log` beside the game. If it does
  not exist the plugin never loaded — check that `dinput8.dll` is in the same folder as
  `swkotor.exe` (and read `dinput8-chain.log` if you used `-Chain`).
- **Second instance on one machine exits immediately:** it must be started through the
  agent (`KotorCoop.Agent.exe launch --instance 2 --game-path <path to swkotor.exe>`), which
  renames the game's single-instance mutex.

Full documentation, source and issue tracker: https://github.com/ChristopherVR/kotor-coop
