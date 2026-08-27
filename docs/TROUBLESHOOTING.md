# Troubleshooting

Common problems and what actually causes them. If a fix here references a `coop.ini` key,
see [CONFIG.md](CONFIG.md) for its default and where it's read; for a console verb, see
[CONSOLE.md](CONSOLE.md).

## "Co-op" button doesn't appear on the main menu

`menu_button` defaults ON in the code (both readers — see CONFIG.md) and the shipped
`coop.ini` sets `menu_button = 1`. A `coop.ini` from before 2026-08-27 shipped with
`menu_button = 0` and a stale "awaiting live verification" comment — the installer never
overwrites an existing `coop.ini`, so an upgraded install keeps the old value. Set
`menu_button = 1`. If it is set and the button is still missing, the plugin never loaded:
see the next two sections.

## Nothing happens at all — no `kotorcoop-plugin.log` beside the game

The plugin did not load. `dinput8.dll` must sit in the same directory as `swkotor.exe`
(not a subfolder), and must be the x86 build (`build.ps1` checks this). In chain mode, read
`dinput8-chain.log`: it lists every entry the loader tried and which one it forwards to.

## Another mod already uses `dinput8.dll` (ReShade, an ASI loader, ...)

`install.ps1` refuses to overwrite a `dinput8.dll` it does not recognise as its own. Install
with `-Chain`: the small `KotorCoop.Loader` wrapper becomes `dinput8.dll`, the plugin is
installed as `KotorCoop.Plugin.dll`, the other mod is renamed `dinput8.prev.dll`, and
`dinput8.chain` lists all of them in load order (edit it by hand to add more; `-Load a.dll`
does the same). The game's DirectInput calls go to the first proxy in the list (ours), which
forwards to the next entry, and so on to the system DLL — so a proxy that always loads the
system DLL itself must be the last proxy. `coop.ini`'s `dinput8_next = <file>` forces the
plugin's next hop regardless of the list. `-Uninstall` puts the other mod back under its
original name.

## Second KOTOR instance won't launch, or refuses to run alongside the first

The retail single-instance guard is a named mutex, not real DRM — but it only yields to
`agent launch --instance 2` (which renames the mutex in memory) on the **DRM-free GOG
build**. The Steam build's own DRM wrapper refuses a second instance outright and its packed
binary means none of this project's engine addresses apply to it. See Requirements in the
top-level README.

## `build.ps1 -Install` deployed into the wrong game directory

`src/KotorCoop.Plugin/build.ps1`'s `-GameDir` defaults to
`C:\GOG Games\Star Wars KOTOR (GOG)`; pass `-GameDir` for any other copy (the rig's second
instance lives at `D:\Games\swkotor-gog-p2` by `demo.ps1`'s defaults). Before 2026-08-27 the
default was a Steam path.

## Client can't connect / "no reply from \<host\>"

- Host and client `port` must match (`coop.ini port=`; the code's own default is 27015,
  but the shipped `coop.ini.example` sets `port = 27140` explicitly — see CONFIG.md).
- A session `password` must match exactly on both sides, or be left empty on both.
- `connect_timeout_ms` (default: the built-in 15000) is how long a client waits before
  giving up; raise it on a slow network.
- Firewalls: this is a direct UDP connection (star relay — clients talk only to the host),
  so the host's UDP port must be reachable from the client.

## I can't see my partner / their phantom never appears

- Confirm both players are actually in the same module — a peer in a different module is
  filtered out by design (`--no-module-filter` on the CLI removes this filter, mainly useful
  for bridging different games or a mock harness).
- An unfocused or minimised instance keeps simulating; it should not need to be in the
  foreground to publish state.
- `status` in the console (or `who`) shows connected peers and their module.

## Companions won't fight for a client, or a client can't take one over

Both are by design, not bugs: only the host can switch party character
([client-control-lock.md](research/client-control-lock.md),
[control-return.md](research/control-return.md)), and a mirrored companion can't swing on a
client except through `companion_local_combat`, which is off by default in the code (the
shipped `coop.ini.example` turns it on) — see
[party-reconciliation.md](research/party-reconciliation.md).

## A feature that "should" be on isn't doing anything

Several `coop.ini` keys default OFF in the code but ON in the shipped example file, and at
least two (`effect_sync`, `trigger_relay`) default ON in the code but are shipped OFF in the
example — see the footnotes in [CONFIG.md](CONFIG.md) for the full list. Check your actual
`coop.ini` against that table rather than assuming either side's "default" applies.

## Companion stalls partway through a leg while following

A known, narrow, well-characterised stall at the very last waypoint of a movement leg — see
[party-reconciliation.md](research/party-reconciliation.md). Two fix candidates exist
(`party_follow_extend_final_node`, `party_follow_advance_destnode_phase6`); both ship off by
default because neither is confirmed live yet.

## Swoop racing / a minigame won't start or gets stuck

Confirmed, currently unresolved engine-level block, not a co-op regression — see
[minigames.md](research/minigames.md)'s "blocked" entry for the exact mechanism and why
diagnosing it further needs a native debugger this project doesn't have.

## Where to look next

`docs/live-verification.md` has the two-instance checklist this project's own "live-verified"
claims are checked against; `docs/research/README.md` indexes every feature write-up by
status, including every "off by default" and "open" item.
