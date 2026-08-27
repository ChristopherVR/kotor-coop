# kotor-coop {{VERSION}}

Two-player co-op for **Star Wars: Knights of the Old Republic** (the DRM-free **GOG** build,
v1.0.3). Two full copies of the game run side by side — on one machine or across a network —
and this package bridges them into one shared world: you see each other as real in-game
characters, travel, fight, loot, talk and pause together.

No game file is modified. Everything lives beside `swkotor.exe` and can be removed again.

## Install — pick one

**A. Extract into the game folder.** Unzip this whole archive into the folder that contains
`swkotor.exe` (for GOG usually `C:\GOG Games\Star Wars KOTOR (GOG)`). That's it — the zip
*is* the install. Start the game.

**B. Double-click `KotorCoop.Agent.exe`.** Unzip anywhere, run the exe. It finds your GOG
install (or asks), copies everything in, and offers to launch the game.

Both do the same thing. Re-installing over an older version just works; your `coop.ini` is
never overwritten.

## Play

1. Start KOTOR normally. (The mod starts its own helper process, `KotorCoop.Agent.exe`, in
   the background — that is expected.)
2. Click **Co-op** on the main menu.
3. One player presses **Host**; the other picks that server from the list (or types the
   host's address) and presses **Connect**. A password set in `coop.ini` is asked for.
4. Load a save. Your partner appears next to you.

What the join actually does: the **host must have a save on disk** (any manual save, or the
game's own first autosave) — that save is what the client receives. The client keeps their
**own character** from their own most recent save and is dropped into the host's world at the
area entrance; the client's own save file is never written. To start a brand-new game
together, the host plays until the first autosave (or saves once), then hosts.

Press `~` in game for the console: `connect <ip> [password]`, `disconnect`, `reconnect`,
`say <message>`. Tab completes, Up/Down recalls history.

The host is authoritative for enemies and the party; each player owns their own character.
Only one conversation runs at a time — the other player watches it live. If your partner
goes to the main menu their character disappears and a line says so; a one-line status in
the top-left always shows the connection state.

## Requirements

- Windows, and the **.NET 10 runtime** (Windows offers to install it the first time
  `KotorCoop.Agent.exe` starts if it is missing).
- **KOTOR 1 from GOG.** Steam's copy refuses to run twice and is packed differently; the
  engine addresses this mod relies on are verified against the GOG binary only.
- For two players on **one** machine: a second copy of the game folder, with this installed
  into both. Start the second copy through the helper (it renames the game's single-instance
  lock): `KotorCoop.Agent.exe launch --instance 2 --game-path <second copy>\swkotor.exe`.

## Configure

`coop.ini` beside `swkotor.exe` is the only config file. Nothing in it needs editing to play;
the useful keys:

| Key | Meaning |
| --- | --- |
| `name` | Your name as shown to your partner (blank = a generated one). |
| `password` | Optional. Must match on both sides. |
| `port` | UDP port (default `27140`). The host's port must be reachable from the client. |
| `host` / `mode` | Address to join and host/join mode — the in-game menu sets these for you; they matter for the console `connect`. |
| `menu_button` | `1` shows the Co-op button on the main menu. |
| `auto_start_agent` | `1` (default) lets the game start `KotorCoop.Agent.exe` for you. |

Every other key is documented in the file itself and in the project's `docs/CONFIG.md`.

## Another mod already uses `dinput8.dll` (ReShade, an ASI loader, ...)

Only one file can be called `dinput8.dll`. The double-click setup notices a `dinput8.dll`
that is not ours and asks whether to keep it; say yes and it installs the small
`chain-loader\dinput8.dll` wrapper as `dinput8.dll`, our plugin as `KotorCoop.Plugin.dll`,
renames the other mod to `dinput8.prev.dll`, and writes `dinput8.chain`:

```
KotorCoop.Plugin.dll
dinput8.prev.dll
```

If you installed by extracting the zip over the other mod instead, you overwrote it — put its
`dinput8.dll` back as `dinput8.prev.dll`, copy `chain-loader\dinput8.dll` over `dinput8.dll`,
rename our `dinput8.dll` to `KotorCoop.Plugin.dll` first, and create `dinput8.chain` as above
(`dinput8.chain.example` is annotated). Or from a command line:

```powershell
KotorCoop.Agent.exe install --game-path 'C:\GOG Games\Star Wars KOTOR (GOG)' --chain --load ReShade32.dll
```

How the chain works: the wrapper loads every listed DLL and hands the game's DirectInput calls
to the **first** one that is itself a `dinput8` proxy (ours). Our plugin then forwards to the
entry **after** itself, and the last proxy forwards to Windows' real `dinput8.dll`. A DLL that
only needs to be loaded (an `.asi`) can sit anywhere in the list; a second proxy should come
after ours. `dinput8-chain.log` beside the game records what the wrapper did on the last
launch; `coop.ini`'s `dinput8_next = <file>` forces the plugin's next hop regardless.

## Uninstall

From the extracted zip (not from inside the game folder — the helper cannot delete itself
while running from there):

```powershell
KotorCoop.Agent.exe uninstall --game-path 'C:\GOG Games\Star Wars KOTOR (GOG)'
```

Removes only what was installed (it recognises its own `dinput8.dll` by a marker and leaves any
other mod's alone, restoring `dinput8.prev.dll` to its original name) and keeps your `coop.ini`.
`install.ps1 -GameDir ... [-Uninstall|-Chain|-Force]` is the same thing for PowerShell users.

## How it works, briefly

```
swkotor.exe (x86)                                         swkotor.exe (x86)
  dinput8.dll = plugin: frame hook, engine calls,           dinput8.dll = plugin
  a shared memory block the helper reads/writes
        |                                                        |
  KotorCoop.Agent.exe (x64, started by the plugin)          KotorCoop.Agent.exe
        \______________________ UDP ____________________________/
```

Windows looks for `dinput8.dll` in the game's own folder before the system one, so the game
loads our plugin itself — no injector. The plugin lives inside the game and does the in-engine
work (spawning your partner's body, mirroring enemies, doors, items, dialogue) and starts the
helper. The helper lives outside, attaches to the game process, and speaks UDP to the other
player's helper. Clients talk only to the host.

## Something is wrong

- **No Co-op button:** `menu_button = 1` in `coop.ini`.
- **Partner never appears / "no reply from host":** ports must match and the host's UDP port
  must be open; passwords must match exactly.
- **Nothing happens at all:** look for `kotorcoop-plugin.log` beside the game. If it does
  not exist the plugin never loaded — `dinput8.dll` must be in the same folder as
  `swkotor.exe` (read `dinput8-chain.log` if you use the chain loader).
- **Second instance on one machine exits immediately:** start it through the helper (see
  Requirements), which renames the game's single-instance mutex.

Full documentation, source and issue tracker: https://github.com/ChristopherVR/kotor-coop
