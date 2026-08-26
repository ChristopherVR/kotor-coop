# Troubleshooting

Common problems and what actually causes them. If a fix here references a `coop.ini` key,
see [CONFIG.md](CONFIG.md) for its default and where it's read; for a console verb, see
[CONSOLE.md](CONSOLE.md).

## "Co-op" button doesn't appear on the main menu

`menu_button` defaults ON in the code (both readers — see CONFIG.md), but the shipped
`profiles/coop.ini.example` explicitly sets `menu_button=0`, with a comment describing the
menu rebind as awaiting live verification. That comment predates the live-verified Connect
flow this project ships today ([menu-frame-hook.md](research/menu-frame-hook.md),
[connect-flow.md](research/connect-flow.md)). Set `menu_button=1` in your `coop.ini` if the
button is missing.

## Second KOTOR instance won't launch, or refuses to run alongside the first

The retail single-instance guard is a named mutex, not real DRM — but it only yields to
`agent launch --instance 2` (which renames the mutex in memory) on the **DRM-free GOG
build**. The Steam build's own DRM wrapper refuses a second instance outright and its packed
binary means none of this project's engine addresses apply to it. See Requirements in the
top-level README.

## `build.ps1` installs into the wrong game directory

`src/KotorCoop.Plugin/build.ps1`'s own `-GameDir` default is a Steam path
(`C:\Program Files (x86)\Steam\steamapps\common\swkotor`). Always pass your GOG directory
explicitly with `-GameDir`.

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
