# kotor-coop {{VERSION}}

Two-player co-op for **Star Wars: Knights of the Old Republic** (the DRM-free **GOG** build,
v1.0.3). Two full copies of the game run side by side — on one machine or across a network —
and this package bridges them into one shared world: you see each other as real in-game
characters, travel, fight, loot, talk and pause together.

No game file is modified. Everything lives beside `swkotor.exe` and can be removed again.

**What to expect.** You can play the whole game together, start to finish. Either player can
lead through doors and area changes, and either player can pick the destination on the galaxy
map — the other one is taken along automatically, take-off movie and all. Enemies have one
shared health bar, your hits land, and stuns, slows and heals reach your partner for real. The
party's inventory, credits, merchant stock, doors, chests, quests and the journal are all one
shared copy. Companions follow the host's game, and a companion can fight for the client. One
conversation runs at a time: whoever starts it picks the replies and the other player watches
it live. Pazaak and the swoop race are single-seat — the other player is told they're waiting.
If somebody saves, loads, quits to the menu, or the host's game crashes, the other side notices
and rejoins on its own. If you have never played the game, `join_mode = newcharacter` lets you
create a character through the normal New Game screen and join with it. Mouse and keyboard only.
The full list — what works, what has a catch, and what is deliberately left to the host — is
`docs/limitations.md` in the full repository (link at the bottom).

## Install — pick one

**A. Extract into the game folder.** Unzip this whole archive into the folder that contains
`swkotor.exe` (for GOG usually `C:\GOG Games\Star Wars KOTOR (GOG)`). That's it — the zip
*is* the install. Start the game.

**B. Double-click `KotorCoop.Agent.exe`.** Unzip anywhere, run the exe. It finds your GOG
install (or asks), copies everything in, and offers to launch the game.

Both do the same thing. Re-installing over an older version just works; your `coop.ini` is
never overwritten. If another mod already has `dinput8.dll` (ReShade, an ASI loader, a
widescreen fix...) both methods notice and set kotor-coop up to run alongside it automatically
— see "Another mod already uses `dinput8.dll`" below; you don't need to know anything about it
in advance.

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

**Never played this game before?** Then you have no save and no character to bring. Set
`join_mode = newcharacter` in `coop.ini` and step 4 changes: instead of loading a save, choose
**New Game** and create a character exactly as you normally would. When you finish, you are
taken into the **host's** world with it rather than into the Endar Spire. Save once afterwards
and you can put `join_mode` back to `save` (the default) for every join after that.

Related: `auto_adopt_world` (on by default) is what fetches the host's world whenever yours
does not match theirs. With it off you can still join, but enemies, doors, containers and the
party stop being shared — the two of you are in worlds that disagree about which object is
which. Neither setting ever writes your own saves.

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
- **KOTOR 1, v1.0.3.** A DRM-free (GOG) copy is what this is developed and tested against.
  Steam's copy is the *same binary* under a DRM wrapper — the engine addresses are correct on
  it too — but it is only partly supported; see **Steam** below.
- For two players on **one** machine: a second copy of the game folder, with this installed
  into both. Start the second copy through the helper (it renames the game's single-instance
  lock): `KotorCoop.Agent.exe launch --instance 2 --game-path <second copy>\swkotor.exe`.

## Steam

Steam's KOTOR is retail 1.0.3 wrapped in Valve's DRM. The wrapper only encrypts the code
section on disk and unpacks it before the game starts, so this mod's engine addresses are
correct on the Steam copy too and `dinput8.dll` loads the same way. Three caveats:

- **Both players should be on the same store.** A Steam install and a DRM-free one currently
  refuse each other: the compatibility check compares the exe *file*, and the wrapped and
  unwrapped files differ even though the code is identical. Steam + Steam is fine.
- **Two copies on one PC needs a DRM-free build.** The Steam copy always goes through the
  Steam client, which will not run it twice.
- **If the Steam overlay is in the way**, `kotorcoop-plugin.log` will say
  `frame hook NOT installed: ... something else already hooked it`. Turn the overlay off for
  KOTOR (right-click the game → Properties → General) and relaunch.

None of the above has been verified on a live Steam install yet — it is read off the binaries.

**Friends-list invites** ("Join Game", `steam_invites` in `coop.ini`) let a friend join without
being told an IP. Optional and off unless you set it up: copy a `steam_api64.dll` next to
`KotorCoop.Agent.exe` (from the Steamworks SDK, or from any Steam game you own — this mod is
not allowed to ship Valve's). Without that file you get one line in the log and everything else
works exactly as before. Both players need to own KOTOR on Steam, and while the agent runs
Steam will show you as playing KOTOR. Set `steam_invites = 0` to stop the agent talking to
Steam at all.

## Configure

`coop.ini` beside `swkotor.exe` is the only config file. Nothing in it needs editing to play;
open it and every key is commented in plain language, grouped under Connecting, Your character,
Screen, Internet play and Advanced. The most commonly-touched ones:

| Key | Meaning |
| --- | --- |
| `name` | Your name as shown to your partner (blank = a generated one). |
| `password` | Optional. Must match on both sides. |
| `port` | UDP port (default `27140`). The host's port must be reachable from the client. |
| `host` / `mode` | Address to join and host/join mode — the in-game menu sets these for you; they matter for the console `connect`. |
| `menu_button` | `1` shows the Co-op button on the main menu. |
| `follow_lead` | who may lead an area transition the other player follows automatically. |
| `dialogue_auto_join` | whether you're pulled into a conversation your partner starts. |
| `stun_servers` | STUN servers asked for the host's public address (see "Play over the internet"). Empty disables it. |
| `upnp` | `1` (default) tries to forward the port automatically when hosting. |

That's the whole file — it's deliberately short. The mod has many more internal switches for
tuning and bisecting bugs during development; they aren't in `coop.ini` on purpose, since a
player never needs them. Every one of them is documented, with what it does and its shipped
value, in the project's `docs/CONFIG.md` under "Developer switches" — setting one by hand is
unsupported.

## Another mod already uses `dinput8.dll` (ReShade, an ASI loader, ...)

Only one file can be called `dinput8.dll`. Both install methods notice when it already belongs
to something else and set kotor-coop up to run alongside it, automatically — no prompt to
answer, no flag to remember. What you'll see is one line naming what was found, for example:

> found another mod in dinput8.dll (ReShade); installing as a chain: our loader takes
> dinput8.dll, the other mod is kept as dinput8.prev.dll and loaded after us

Concretely: the small `chain-loader\dinput8.dll` wrapper is installed as `dinput8.dll`, our
plugin as `KotorCoop.Plugin.dll`, the other mod is renamed to `dinput8.prev.dll`, and
`dinput8.chain` is written:

```
KotorCoop.Plugin.dll
dinput8.prev.dll
```

Any `.asi` mods next to the game (or in `scripts\`/`plugins\`) are left exactly where they are
and the summary says so — nothing about them changes. If the file already in the `dinput8.dll`
slot turns out to be a plain copy of Windows' own `dinput8.dll` rather than a mod, that's
detected too and it's simply replaced — there's nothing to chain.

Re-installing over an existing chain install (a new version of kotor-coop, for instance) keeps
it chained and just upgrades the plugin and loader in place; nothing above needs repeating.
`KotorCoop.Agent.exe install --check` (or `install.ps1 -Check`) reports the install layout and
checks it without changing anything — useful if something seems off.

If you'd rather choose yourself: `--chain` forces chain mode even before anything foreign is
there, `--force` overwrites a foreign `dinput8.dll` outright instead of chaining it, and
`--load` adds extra DLLs to the chain:

```powershell
KotorCoop.Agent.exe install --game-path 'C:\GOG Games\Star Wars KOTOR (GOG)' --chain --load ReShade32.dll
```

If you installed by extracting the zip over the other mod instead, you overwrote it — put its
`dinput8.dll` back as `dinput8.prev.dll`, copy `chain-loader\dinput8.dll` over `dinput8.dll`,
rename our `dinput8.dll` to `KotorCoop.Plugin.dll` first, and create `dinput8.chain` as above
(`dinput8.chain.example` is annotated), or just run the `install --chain` command above over
it — that puts everything back in the same state.

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
`install.ps1 -GameDir ... [-Uninstall|-Chain|-Force|-Check]` is the same thing for PowerShell users.

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

- **Not sure the install is right (especially with a chained mod involved):** run
  `KotorCoop.Agent.exe install --check` (or `install.ps1 -Check`, from the extracted package).
  It reports what's actually there — plain, chained, or nothing installed — checks that every
  chained DLL is really present, and prints one PASS/FAIL line per check without changing
  anything. Every ordinary install run already ends with this same report.
- **The party's shared stash and a merchant's stock aren't shared between us:** both channels
  ship ON as of 2026-08-29 (`party_pool_sync=2`/`store_sync=2`), so the usual cause is an OLD
  `coop.ini` — the installer never overwrites that file, so an install from before then keeps its
  own `=0` lines. Set them to `2`, or run `install.ps1 -GameDir '...' -UpdateDefaults`. Two real
  limitations remain even when on: a shop's stock is only READ while somebody has that shop's GUI
  open, and an infinite-stock item's buys never replicate (the engine's own stock never changes
  for one). See `docs/CONFIG.md` and `docs/research/shared-inventory-v2.md` in the full repo.
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
