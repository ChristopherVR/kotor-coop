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

`tools/install.ps1` now catches this automatically: every plain install run (not just
`-Uninstall`) diffs the existing `coop.ini` against the table of keys whose shipped default
changed (`menu_button`, `effect_sync`, `trigger_relay` — the same three CONFIG.md's footnotes
4/5/7 describe) and warns by name when the old value is still there — a dry run by default, so
nothing is written unless you pass `-UpdateDefaults`, which rewrites just those lines in place
(order, comments and every other line untouched). See `tools/CoopIniDefaults.ps1` and
`tools/README.md`'s "Manual verification" section for the decision logic itself.

## Nothing happens at all — no `kotorcoop-plugin.log` beside the game

The plugin did not load. `dinput8.dll` must sit in the same directory as `swkotor.exe`
(not a subfolder), and must be the x86 build (`build.ps1` checks this). In chain mode, read
`dinput8-chain.log`: it lists every entry the loader tried and which one it forwards to.

## Another mod already uses `dinput8.dll` (ReShade, an ASI loader, ...)

The installer refuses to overwrite a `dinput8.dll` it does not recognise as its own (the
double-click setup asks; `KotorCoop.Agent.exe install` and `install.ps1` need `--chain`/
`-Chain` or `--force`). In chain mode the small `KotorCoop.Loader` wrapper becomes
`dinput8.dll`, the plugin is installed as `KotorCoop.Plugin.dll`, the other mod is renamed
`dinput8.prev.dll`, and `dinput8.chain` lists all of them in load order (edit it by hand to
add more; `--load a.dll` does the same). Extracting the zip straight over the other mod
overwrites it — the package README says how to recover by hand. The game's DirectInput calls go to the first proxy in the list (ours), which
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

## My damage numbers look wrong in a swarm

Not a bug — two separate, deliberate simplifications, both worth knowing about if a fight
against several enemies at once looks like the wrong creature gets credited or blamed.

The wire that reports damage a peer's phantom took carries one CUMULATIVE total plus the id of
the LAST attacker only — not a per-hit, per-attacker breakdown
([combat-when-apart.md](research/combat-when-apart.md) §6b,
[combat-participation.md](research/combat-participation.md) §4.3). When two or more hostiles
land hits in the same short window, all of that damage is folded into one number and the whole
amount is attributed to whichever of them hit last, so a floaty number or a "took N damage from"
log line can name the wrong enemy in a crowd even though the total itself is correct.

Separately, `local_combat` only ever lets ONE mirrored enemy fight a given player's own engine
at a time — `EnemyPresenceController`'s exclusion slot holds a single creature, by design (one
shared-block field, one fight). Every other hostile that is in range and eligible keeps being
mirrored (position, health, animation) exactly like normal, just not handed to this engine's own
AI yet; whatever it deals still arrives through the ordinary forwarded-damage channel, so no
damage goes missing — but "which enemy is actually hitting me right now" can look inconsistent
with what is on screen during a real swarm, for the same attribution reason as above. A
"`N` more enemies engaged - mirrored" HUD line (coop.ini `local_combat_deferred_notice`, default
on; roadmap-seamless.md Tier 2 item 14) shows whenever this is happening, so a swarm's numbers
not quite matching what is on screen has a visible explanation instead of looking like a desync.

## A feature that "should" be on isn't doing anything

Two `coop.ini` keys (`companion_local_combat`, `dialogue_recruit_sync`) default OFF in the
code but ON in the shipped file, and a `coop.ini` installed before 2026-08-27 shipped
`effect_sync`, `trigger_relay` and `menu_button` OFF — see the footnotes in
[CONFIG.md](CONFIG.md). The installer never overwrites an existing `coop.ini`, so check your
actual file against that table rather than assuming either side's "default" applies.

## Companion stalls partway through a leg while following

A known, narrow, well-characterised stall at the very last waypoint of a movement leg — see
[party-reconciliation.md](research/party-reconciliation.md). Two fix candidates exist
(`party_follow_extend_final_node`, `party_follow_advance_destnode_phase6`); both ship off by
default because neither is confirmed live yet.

## Swoop racing / a minigame won't start or gets stuck

Confirmed, currently unresolved engine-level block, not a co-op regression — see
[minigames.md](research/minigames.md)'s "blocked" entry for the exact mechanism and why
diagnosing it further needs a native debugger this project doesn't have.

## `kotorcoop-*.log` keeps growing / disk usage from logs

`kotorcoop-plugin.log` and every per-channel split (`kotorcoop-combat.log` etc.) rotate to a
single `*.1.log` sibling once they cross `coop.ini`'s `log_max_mb` (default 8) — both at
plugin startup and roughly every 30 seconds during play (`PluginHost.RotateLogsIfNeededMidSession`,
`LogRotation.PeriodicCheckIntervalMs`), so a session that runs long enough to cross the cap on
its own no longer has to wait for a restart to shrink back down. Only one prior generation is
kept; a `*.1.log` from before the most recent rotation is gone.

`kotorcoop-agent.log` (the log for the agent process the plugin auto-starts) is different: it
only rotates once, right before that agent process is launched (`AgentLauncher.RotateAgentLogIfNeeded`).
It cannot rotate mid-session from either side — the agent's console output is a raw OS file
handle inherited from the plugin's `CreateProcess` call (see the comment on
`AgentLauncher.Launch`'s `StartupInfo.StdOutput` assignment), not a stream either side can
safely re-point at a fresh file while the process is running. In practice this rarely matters:
the plugin only relaunches the agent on a crash/hang (`AgentLiveness`), which is also when the
log rotates. A single agent process kept alive for an unusually long session is the one case
where this file can grow past the cap before the next relaunch.

The `--log` file `tools/demo.ps1`/`verify.ps1` point the agent at directly (`agent1.log`,
`agent2.log` under `$env:TEMP\kotorcoop-demo\`) is a plain scratch file the agent writes with
its own `File.AppendAllText`, and rotates the same once-at-launch way as the other two.

## A partner's game crashes during or shortly after a module transition (`0xC0000005`)

There are at least two distinct, unrelated causes behind crashes shaped like this — do not
assume it is either one without checking `kotorcoop-plugin.log`/`tools/crashlog.ps1` first.

**A fixed cause: the `CGuiInGame+0x78` write storm.** On builds up to 2026-08-27,
`PartySelectSuppressionController` re-detected and re-tore-down the same stale party-selection
panel address hundreds of times a second for the life of a session — a write storm through a
dangling pointer that could corrupt whatever heap block a module load later reallocated at that
address. Root-caused and fixed 2026-08-28 (touch a given panel address at most once per module
instead of every frame forever); the write-storm ITSELF is confirmed fixed (one suppress/sweep
pair per session instead of 70,000+). **If you hit a crash on a build from before 2026-08-27, the
fix is to upgrade** — reinstall from a current release or rebuild from `main` (`build.ps1
-Install`).

**An open, NOT-yet-fixed cause: a pre-existing engine bug, `0x0040D386`, unrelated to any
`coop.ini` feature.** This is the same crash address the write-storm fix was hoped to have closed
(the STUNT_00 escape-pod path it was first seen on) — but a 2026-08-28/29 live round reproduced
it again, post-fix, via an ordinary two-instance `warp` cycle that never opens the galaxy map or
touches party-select at all, and confirmed it is not gated by ANY optional `coop.ini` flag (a
pass with every feature simultaneously OFF still crashed). It is inside `swkotor.exe`'s own
`CExoKeyTable::DestroyTable` — a resource-table subsystem, nothing this project's own code calls
or references — most often (not exclusively) following a transition through `tar_m02aa`. **There
is no known workaround or `coop.ini` setting that avoids it**; it is not specific to co-op play
(the recipe that reproduces it is a bare console `warp`, something a solo player's own game could
in principle also hit). See
[transition-crashes.md](research/transition-crashes.md) for the full attribution round and
[galaxy-map-and-party-select.md](research/galaxy-map-and-party-select.md)'s 2026-08-28/29 entries
for the RE and the correction to the earlier "fixed" claim; see
[session-save.md](research/session-save.md) for the write-storm's own fix.

## The game crashed and the plugin log doesn't explain why

`kotorcoop-plugin.log` covers access violations and managed exceptions inside the plugin
(`CrashProbe`), but a .NET NativeAOT fail-fast (`RaiseFailFastException`/`__fastfail`) bypasses
every user-mode handler — the process is just gone, log ending mid-sentence, with no trace of
why. Windows' own Error Reporting can capture a minidump for the NEXT crash, but it needs to be
turned on first (off by default, machine-wide): from an **Administrator** PowerShell, in the
extracted package folder,

```powershell
.\install.ps1 -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)' -EnableCrashDumps
```

A non-elevated shell gets a clear error naming the reason (the setting lives under
`HKEY_LOCAL_MACHINE`) rather than silently doing nothing. The next crash leaves a `.dmp` file
under `<game folder>\crashdumps` (override with `-DumpFolder <path>`), capped at the 10 most
recent (~200 MB each — a 32-bit game's full dump). Turn it back off with
`install.ps1 -GameDir '...' -DisableCrashDumps`; it does not expire or undo itself. The
dev-only `tools/crashlog.ps1 -EnableDumps` does the identical registry write for a hardcoded
dev dump folder — the two are equivalent, just aimed at different audiences.

## Where to look next

`docs/live-verification.md` has the two-instance checklist this project's own "live-verified"
claims are checked against; `docs/research/README.md` indexes every feature write-up by
status, including every "off by default" and "open" item.
