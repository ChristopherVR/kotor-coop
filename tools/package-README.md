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

**No gamepad/controller support.** The server browser, the password box and the `~` console
all assume mouse and keyboard; a controller can't navigate them.

## Play over the internet

The server browser only finds games on your own network. To play with someone elsewhere:

1. The **host** presses Host as usual, then checks `status` (or types `public_address`) at
   the `~` console. It prints something like:

   > `public address: 203.0.113.5:27140 - UPnP mapped`

   That means it worked automatically — nothing else to do. If it instead says
   `UPnP unavailable - forward UDP+TCP 27140 manually`, open your router's admin page and
   forward that port (both UDP and TCP — TCP carries the initial save transfer, UDP carries
   the session) to this PC, for both. Most home routers support UPnP and this step is
   automatic; if yours doesn't, forwarding by hand is the only alternative — there's no relay.
2. Share the address the host was given (or a hostname/DDNS address pointing at it — `connect`
   accepts either) with the other player.
3. The **joiner** presses Connect and types that address (or picks it from a saved shortcut —
   there's nothing to browse for an internet game).

If a join just sits there and eventually says "no reply... is UDP port N forwarded on the
host?", that usually means the port really isn't reachable from outside the host's network yet
— re-check the router's port forward (or UPnP status) before trying again.

**Privacy note:** discovering the host's own public address contacts a small, fixed list of
public STUN servers (Google's, by default) over UDP — nothing about your session or characters
is sent, only enough to ask "what does the internet see me as". Set `stun_servers =` (empty) in
`coop.ini` to turn this off if you'd rather not; you can still play by sharing a manually
port-forwarded address instead. There is currently no relay or NAT hole-punching, so a router
with UPnP off *and* a restrictive ("symmetric") NAT type will need the manual port forward —
see `docs/research/network-topology.md` if you want the full picture.

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
| `stun_servers` | STUN servers asked for the host's public address (see "Play over the internet"). Empty disables it. |
| `upnp` | `1` (default) tries to forward the port automatically when hosting. |

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

- **No Co-op button:** `menu_button = 1` in `coop.ini`. Upgrading over an install from before
  2026-08-27 keeps that file's OLD default (`0`) because `coop.ini` is never overwritten —
  `install.ps1` now warns about this after every install and `install.ps1 -GameDir '...'
  -UpdateDefaults` rewrites just that line (and the other defaults that changed the same day,
  `effect_sync`/`trigger_relay`) without touching anything else in the file.
- **Partner never appears / "no reply from host":** ports must match and the host's UDP port
  must be open; passwords must match exactly.
- **Nothing happens at all:** look for `kotorcoop-plugin.log` beside the game. If it does
  not exist the plugin never loaded — `dinput8.dll` must be in the same folder as
  `swkotor.exe` (read `dinput8-chain.log` if you use the chain loader).
- **Second instance on one machine exits immediately:** start it through the helper (see
  Requirements), which renames the game's single-instance mutex.
- **The game crashed and you want to report it:** `kotorcoop-plugin.log`'s last lines are the
  first thing to attach. For a crash the log itself can't explain (the game just vanishes,
  nothing after "FATAL"), turn on Windows' own crash dumps for `swkotor.exe`:
  ```powershell
  # from an Administrator PowerShell, in the extracted package folder:
  .\install.ps1 -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)' -EnableCrashDumps
  ```
  This needs elevation (a plain PowerShell gets a clear error naming the reason) because it
  writes a machine-wide Windows Error Reporting setting, not a per-game file. The next crash
  leaves a `.dmp` file in `<game folder>\crashdumps` (or wherever `-DumpFolder` pointed) —
  attach that to a bug report. Turn it back off with
  `.\install.ps1 -GameDir '...' -DisableCrashDumps`; it does not undo itself automatically.

Full documentation, source and issue tracker: https://github.com/ChristopherVR/kotor-coop
