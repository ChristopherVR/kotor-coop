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

## Host disconnected (crashed, quit, or the network dropped)

A client that loses its host is not stuck: within `host_loss_timeout_ms` of silence (default
5000ms, same as the ordinary reconnect trigger — near-instant instead if the host shut down
cleanly rather than crashing) every co-op lock releases and you keep playing solo — companions
respond to clicks again, doors and dialogue work normally, nothing stays frozen. A HUD/chat
line says "Host connection lost - playing solo, will rejoin automatically." while this is
happening.

- The client keeps re-Helloing in the background the whole time; if the host comes back
  (relaunched, or the network recovers) you rejoin automatically and everything re-mirrors —
  no player action needed. A second HUD/chat line ("Host connection restored - rejoining.")
  confirms it.
- If you were mid-conversation with a shared NPC when the host vanished, you keep the reply
  list you were already looking at and can keep clicking through it on your own — dialogue is
  initiator-led, so this does not require the host.
- `coop.ini host_loss_release = 0` turns this off and restores the old behaviour: every lock
  stays held until you manually run `disconnect` in the console. `host_loss_timeout_ms` lets
  the release wait longer than the reconnect attempt itself, if you would rather bet on a brief
  blip resolving first. See [CONFIG.md](CONFIG.md) and
  [disconnect-reconnect.md §11](research/disconnect-reconnect.md).
- This does not apply to the HOST losing a client — a client vanishing mid-dialogue or
  mid-interaction only ever releases what THAT client was holding; the host and every other
  connected player are unaffected either way.

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

**Fixed and on by default since 2026-08-29** (`party_follow_stall_recovery`). The engine's own
"has the follower reached this waypoint" test is a STRICT comparison, and at the end of a leg the
recorded waypoint collapses onto the follower's cached previous position — so the test compares a
number against itself, can never pass, and the companion freezes where it stands. Live-reproduced
4/4, including the severe form: a leader teleported 16.5 units away that the companion never
recovered from at all.

A runtime detector now watches for that exact wedged state and, once it has held for two seconds
with the companion genuinely separated, gathers the party through the engine's own
`CSWPartyTable::PositionParty` — the same call a module transition makes. Across five reps every
wedge recovered on the first attempt (`party follow stall: recovered 0x... via positionparty` in
`kotorcoop-plugin.log`). Note it RELOCATES the companion rather than walking it back: the engine's
own long-distance pathfinding limit underneath is separate and still unfixed.

Turn it off with `party_follow_stall_recovery=0`; `party_follow_stall_recovery_method` selects
between six recoveries (the other five were each tried live and are documented, with their
results, in [party-reconciliation.md](research/party-reconciliation.md) round 105). Two much older
candidates (`party_follow_extend_final_node`, `party_follow_advance_destnode_phase6`) remain off —
round 105 folded what they do into method `5` and measured it insufficient on its own.

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
in principle also hit). A separate 2026-08-28 round found a second address in the same bug family
(`0x0023D4B3`, `CSWCObject::GetServerObject`) reproduces on a build from BEFORE this project's
current feature set even existed (`c9d08bb`) — confirming neither address is a regression this
project introduced — and, more usefully for a player: **a 12-transition soak that only ever used
real in-game doors, on the same build, produced zero crashes**, while every soak that used the
`warp` console verb as the transition trigger itself crashed reliably by the second or third hop.
The working theory is that `warp` fires a transition near-instantly, with none of a real door's
few seconds of walk-up and fade to let a transient engine state (a lingering conversation, in
particular) settle first — so this crash is most likely to actually appear during rapid `warp`
chaining (dev/test use), not during ordinary door-based play. See
[transition-crashes.md](research/transition-crashes.md) for the full attribution round and
[galaxy-map-and-party-select.md](research/galaxy-map-and-party-select.md)'s 2026-08-28/29 entries
for the RE and the correction to the earlier "fixed" claim; see
[session-save.md](research/session-save.md) for the write-storm's own fix.

## The party's shared item stash and a merchant's stock aren't shared between us

This is expected, not a bug. `party_pool_sync` (the "stash gear in the party" pool,
`pt_item_repo`) and `store_sync` (a merchant's buy/sell stock) both ship OFF by default, and stay
that way by DECISION, not merely caution: every wire-sync design tried for either channel has
been live-disqualified by a real, reproducible defect. Mode 1 (a bidirectional op-log) hit a
severe amplification runaway on the party pool (three deposits inflated past 500 units) and a
blackout+reconnect double-apply on the store. Mode 2 (`=2`, a host-authoritative absolute
snapshot plus client intents, `docs/research/shared-inventory-v2.md`) was built specifically to
be structurally immune to mode 1's failure class, but three separate 2026-08-29 rig rounds found
it ALSO disqualified — a distinct doubling/re-insertion/re-detection defect on the party-pool
side every round, and a severe unbounded client-side re-insertion loop on the store side. Buying,
selling and depositing/withdrawing all work correctly on your OWN instance either way — it is
only the cross-instance mirroring that is off. Mode 2 is left in the tree behind
`party_pool_sync=2`/`store_sync=2` for testers who want to help close the remaining defects, but
is not recommended for real play. See [CONFIG.md](CONFIG.md), and
[party-pool-sync.md](research/party-pool-sync.md)/[store-sync.md](research/store-sync.md)/
[shared-inventory-v2.md](research/shared-inventory-v2.md) for the full history.

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

## My partner looks like the wrong character (a stranger, or a companion) after a warp

The body a peer is drawn with has two halves — the server object the mod writes, and the
CLIENT TWIN the engine actually renders — and only the second one is what you see. Before
2026-08-29 the mod decided "this phantom already wears the right look" from the server half
alone, so a phantom re-spawned after a module change (a `warp`, a door, any transition) could
be left rendering the `k_coop_phantom` blueprint for the rest of the module: the server field
was correct, the twin did not exist yet at the moment it was written, and nothing re-applied
it. It did not show at first connect, only after a transition — that ordering is the whole
tell. Fixed by re-asserting the look on the twin until it lands, per body; see
[research/name-plates.md](research/name-plates.md).

If it still happens on a current build, the peer is probably PUBLISHING the wrong look rather
than the mod applying it wrongly — i.e. that player's own agent has latched onto a body that is
not their character. Check that side's agent log for `player: adopted the engine's own creature
0x...` and `player 0x...: is-PC flag is ...` lines around the transition, and the receiving
side's plugin log for the spawn line, which carries all three numbers that can disagree:

```
spawned phantom in slot 0: id 0x12D at 0x... (x, y, z) appearance 378 (peer published 378, renders 114), portrait 0
```

`appearance` is the server row, `renders` is the twin's row (what you see), and `peer
published` is what came over the wire. Those three agreeing is the fix working; `renders`
lagging is the twin gap above; `peer published` being someone else's row is the peer's own
agent resolving the wrong player, which is a different bug and worth reporting with that line.

## A second Trask (or Carth, or Bastila) is standing next to the party after a warp

This one is not your partner wearing the wrong body — it is a **copy of your own companion**,
and it arrives through the enemy channel, not the player channel. The tell is that the extra
body has no name band and does not move with the party.

Before 2026-08-29 the host decided "is this creature one of ours" from the engine's
party-FOLLOW pointer, which the engine attaches some time *after* a companion's body exists.
A module change (a `warp`, a door) is exactly that window, so Trask read as an ordinary
stranger for a second, went out in the enemy census, and the other instance — which does not
necessarily hold that companion under the same runtime object id, because party bodies are
spawned by the party table at load rather than placed in the module — built a stand-in for
him: Trask's own appearance row (378), standing where Trask stands.

Fixed on both sides: the census now takes membership from the party TABLE's roster, and a
mirror refuses to build a body for one of its own roster ids or for anything standing on one
of its own companions wearing that companion's look. On a current build the receiving side's
plugin log says so out loud when it happens anyway (an older host, say):

```
materialise: REFUSING owner 0x127 (appearance 378) - it is one of OUR party roster bodies
```

If you still get a duplicate companion, check the HOST's agent log rather than the client's:
the census is published by whoever owns the fight, and a companion appearing in it at all is
the defect. See [research/name-plates.md](research/name-plates.md) ("the census leak") and
[research/enemy-materialisation.md](research/enemy-materialisation.md).

## Where to look next

`docs/live-verification.md` has the two-instance checklist this project's own "live-verified"
claims are checked against; `docs/research/README.md` indexes every feature write-up by
status, including every "off by default" and "open" item.
