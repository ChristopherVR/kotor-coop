# Troubleshooting

Common problems and what actually causes them. If a fix here references a `coop.ini` key,
see [CONFIG.md](CONFIG.md) for its default and where it's read; for a console verb, see
[CONSOLE.md](CONSOLE.md).

## Reporting a problem: one file with everything

In the game, open **Co-op → Create support report**, or press **F9** and choose that action.
If the game cannot start, double-click **KotorCoop.CollectLogs.cmd** in the installed game
folder or extracted package. From a package it lets you select the game folder when needed.
No terminal command is required. The optional command is `KotorCoop.Agent.exe logs`.

The report is a `kotorcoop-support-<date>.zip` on your Desktop (or the game folder if no
Desktop is available). Open **READ-ME-FIRST.txt** inside it for the contents and privacy
summary. It includes plugin/agent logs and their older copies, setup and loader logs when
present, a redacted configuration, a system note and a crash summary. Recognized addresses,
paths, connection credentials and your configured player name are removed automatically
from text. Other players' names and chat may remain; review the report before sharing.
Nothing is uploaded, and the original files are unchanged. Saved games, game assets and raw
crash stack memory are excluded. The original crash record remains in the game folder for
any specifically requested follow-up.

Use the [bug report form](https://github.com/ChristopherVR/kotor-coop/issues/new?template=bug_report.yml).
Include the package version for every player, who was hosting, the area and last action,
expected and actual results, game mods, and whether this was LAN or internet play.
For a session problem, collect a bundle from both host and joiner before repeated launches
rotate the useful logs away. With multiple installations, specify the affected one:

```powershell
.\KotorCoop.Agent.exe logs --game-path "C:\GOG Games\Star Wars KOTOR (GOG)"
```

The bundle includes `dinput8-chain.log` when present and records the collecting agent's
build version plus the version installed in the selected game folder. It does not include
saved games; if reproducing needs one, keep a separate copy of that save folder.

## Windows says it is unsafe, or antivirus removed a file

The download is not code-signed, so Windows SmartScreen may show "Windows protected your PC".
Choose **More info**, then **Run anyway**. To check you have the file we published, compare
its SHA-256 with the one on the release page.

Some antivirus programs quarantine `dinput8.dll` after install. When that happens the
**Co-op** button does not appear. Stop and report the detection, package SHA-256 and
scanner definition version for investigation. Do not disable protection or add exclusions
to suppress a detection. `KotorCoop.Agent.exe install --check` says when the file is missing.

## Setup cannot find the game, or says it cannot change the folder

- **"No GOG or Steam KOTOR install found."** Point setup at the folder that holds
  `swkotor.exe`. On Steam, right-click the game, then Manage > Browse local files. In GOG
  Galaxy, open the game, click the settings cog, then Manage installation > Show folder.
  Then choose that folder in the setup program and continue.
- **It says it is running from inside the zip file.** Extract the whole zip into its own
  folder first, then run `KotorCoop.Agent.exe` from there.
- **"Windows is blocking changes to this folder."** A game under Program Files (GOG
  Galaxy's default library) is protected by Windows. Setup offers to restart as
  administrator; say yes. Nothing is changed before that. This replaces older advice to
  move the game or to start the whole thing as administrator by hand.
- **A warning that `swkotor.exe` is not version 1.03.** The mod needs KOTOR 1 version 1.03.
  Update or repair the game through GOG or Steam and run setup again.
- **The host cannot be reached by friends.** Setup asks "Allow friends to connect to games
  you host?" and Windows then asks for permission. If you said no or never saw the prompt,
  run setup again and leave the firewall option enabled.

## Messages you may see when joining

Join and compatibility problems now appear on screen as plain sentences, for example:

- "Your co-op mod version differs from the host's ... Both players need the same release."
  Both players install the same release.
- "Your game executable differs from the host's. Both players need KOTOR 1 version 1.03."
- "Wrong password. Ask the host for the password and try again."
- "Your game files differ from the host's (area layouts), so ... are off ..." The sentence
  names what was switched off. Rejoin, or reach an area that matches, to get it back.

The short technical tags (`E_PROTO`, `E_EXE`, `E_PASSWORD`, `W_GIT`, `W_SAVE` and the
others) are written to the logs only. If a developer asks which tag fired, it is in the
agent log in your support bundle.

## The host stopped responding

The mod tries to rejoin automatically. If it does not recover, the host opens **F9 →
Start/resume hosting** and the friend chooses **Retry last connection**. Use **Connection
help** for network checks or **Leave co-op** to stop retrying. The same actions are available
in the main-menu Co-op browser. The automatic retry eventually stops after five minutes.
Rejoining restores the connection, not progress lost since the last usable save.

## The `~` console and `swkotor.ini`

The mod turns on the game's cheat console setting (`EnableCheats=1` in `swkotor.ini`) so
the `~` console works. The first time, it keeps your original file as
`swkotor.ini.kcoop-orig`, and uninstall puts your original setting back. If the console
does not open, check that `swkotor.ini` still has `EnableCheats=1` under `[Game Options]`.

## Uninstalling

For a setup installation, use **Windows Settings → Apps → Installed apps → kotor-coop →
Uninstall**. For a ZIP installation, run `KotorCoop.Agent.exe uninstall` from the extracted
zip folder (add `--game-path
"<folder>"` if it finds two installs). It removes the mod files, the `coop-mods` folder, the
`kotorcoop-agent` logs and `kotorcoop-last-address.txt`, and it restores your original
`EnableCheats` setting. It keeps, on purpose, `coop.ini`, the "Co-op" save and
`saves\kotorcoop-character.*`, and lists them when it finishes. Delete them by hand if you
want them gone.

## Your own saves

Before co-op uses the Auto Save slot, your own single-player autosave is kept as a save
called "Autosave before co-op" (the two most recent are kept). If the game crashes while a
co-op save is being written, your own Quick Save is put back, or kept as "Recovered
quicksave". Look for both in the Load Game list.

## Setup refuses an update or removal because a file is in use

Close the game window for that installation and its co-op helper before updating or
uninstalling. The installer checks existing game and mod files before changing them;
its refusal names the blocked file and says that no files were changed. A helper can
remain open briefly after the game exits, so wait for it to close and retry. If both
are closed, check that your account can write to the game folder. `install --check`
is read-only and can still run while the game is open.

## The helper restarted, but the host is no longer hosting

With `auto_start_agent = 1`, the plugin can restart a failed helper automatically. The new
helper starts in menu mode; it may wait for a fresh Host or Connect choice rather than resume
the previous session. A joiner can show "reconnecting" while the host's helper is already running.

On the host, press **F9 → Start/resume hosting**. Keep the same hosting port.
The joiner should reconnect; if it does not, choose **Retry last connection**.
Collect both players' support bundles if recovery fails. Automatic helper restart alone does
not establish that the multiplayer session has recovered.

## Restarting after the game exits unexpectedly

Keep both players' logs and the last good checkpoint. Restart the game and load that
checkpoint, then connect again. Unsaved progress is not guaranteed to survive a hard
exit; a local forced-exit check restored saved credits and lost the later unsaved change.
If the game tells you your world differs from the host's (the logs call this `W_SAVE` or
`W_GIT`), shared channels are disabled. Follow the world-recovery steps below and wait for normal control and the connection to return before continuing.
Successful reconnection alone does not confirm that shared objects are syncing.

## Shared objects stop syncing after an object-mapping warning

The on-screen sentence "Your game files differ from the host's (area layouts), so ... are
off ..." (logged as `W_GIT`) means the saved object IDs differ between the two worlds. The mod disables affected
channels because applying a door, enemy or item update to the wrong object would be unsafe.
Keep both players' logs and save folders before trying recovery.

On the joiner, enter `reconnect world` in the in-game `~` console to adopt the host's latest
saved world with your own character. If asked to save first, both players should save, then
the joiner should retry within the stated window. This reloads the joiner's world; wait for
normal control and the connection to return. In personal inventory mode, your own saved
inventory and credits should be retained. Report any loss or duplication with both log bundles.

## Disconnect says it kept the last verified character checkpoint

That message means the fresh disconnect save could not be proved complete. It does not mean
your latest progress was captured. While still in a playable world, make a named save and keep
its folder before closing the game. Collect the support bundle if the warning repeats.

The 3 October development fix addresses a reproduced ten-second capture timeout after world
adoption: the engine reused a longer save-name buffer, and the completion hook rejected the
shorter Co-op name. The r5 verification package completed the same capture in 1.29 seconds.
That package is a local candidate; check your installed build when comparing results.

## "Co-op" button doesn't appear on the main menu

If antivirus removed `dinput8.dll`, follow the detection-reporting guidance above.
Investigate the detection before attempting another installation.

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

The command-line installer automatically preserves another mod's `dinput8.dll` by chaining;
the double-click setup offers to keep it and load both. `--chain` / `-Chain` explicitly
selects the same mode. Avoid `--force`, which overwrites the other DLL. In chain mode the small `KotorCoop.Loader` wrapper becomes
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
`agent launch --instance 2` (which renames the mutex in memory) on the **DRM-free GOG build**.
The Steam copy goes through the Steam client, which allows one copy of an app, and it refuses
regardless: the rename succeeds (`launched pid NNNNN with mutex name 'kcoop2'`) and the process
still vanishes a moment later, usually with
`error: Process was not started by this object` from the launcher that lost it.

**Two instances on one PC with only one DRM-free copy: run the Steam copy FIRST** (it takes the
plain `swkotor` mutex), then `agent launch --instance 2` on the DRM-free copy. Host and client
roles come from the agents' `--host`/`--join` arguments, not from launch order. Verified live —
docs/research/steam-support.md §18.5.

(The old claim that "its packed binary means none of this project's engine addresses apply" was
wrong and is retracted: the wrapper decrypts `.text` in place before the game starts, so every
address is the same at runtime.)

## Steam: nothing works / "the exe differs" when playing with a GOG friend

Fixed. The compatibility handshake used to hash the `swkotor.exe` **file**, and the Steam copy
is the same code under Valve's SteamStub wrapper, which encrypts `.text` on disk — so two
installs that hook identically hashed differently and the join was refused with
`RefusalReason.Executable`. It now hashes the **running image**, which the two builds share
byte for byte. A Steam client and a DRM-free host have been verified in one live session
(docs/research/steam-support.md §18).

If a cross-store join is still refused on the executable, **both sides need this version or
newer**. Check each agent's startup line — the two `exe image` values must match, and the
`exe file` values are expected to differ:

```
GOG   content digest: exe image C6962619, ... [exe file 8B8EC44D (DrmFree)]
Steam content digest: exe image C6962619, ... [exe file 220AC31D (SteamStub)]
```

If the two `exe image` values differ because one player uses an exe patch (a widescreen hex
edit, for example), the host can set `exe_mismatch = allow` in its `coop.ini`. The joiner is
then let in, and both screens show a `W_EXE` warning. The mod uses fixed engine addresses, so
a patch that changed one of them can still misbehave or crash. Mention `W_EXE` in a bug report.
The 4GB (large-address) patch does not change the `exe image` value.

**Both players installed KMRP but get an executable mismatch:** KMRP patches code in
memory, and its allocated addresses can differ even with the same release. Builds with
the KMRP compatibility fix compare the executable file, runtime binaries and patch settings
as a separate check. Update both players to the same co-op build and restart both games.
Matching supported installs connect with a `W_KMRP` note; display resolutions may differ.
Different patch options, missing runtime files or unsupported layouts still need attention.
The agent log includes a `KMRP install` identity when this check is available.

On an older co-op release, the host's existing `exe_mismatch = allow` option is a workaround
for testing, with the fixed-address risks described above. A successful connection does not
establish full KMRP gameplay compatibility; that still needs a two-player playtest. See
[the investigation and limits](research/kmrp-compatibility.md).

If a session logs `executable identity unchecked: one side published no live image digest`, one
peer's plugin did not publish one (an older plugin, or a plugin that failed to read its own
image — its log says which). The session still runs; the executable check is simply not in
force, so make sure both sides really are on the same build.

Expect content warnings (`W_OVERRIDE`, `W_APPEARANCE`, `W_TLK`, `W_2DA`, `W_GLOBALS`) whenever
the two installs are modded differently. Those are not Steam-specific and none of them ends a
session — they name the channel each mismatch degrades.

## Steam overlay and the frame hook

Not a problem in practice. With the overlay loaded, the plugin logs

```
frame hook installed: hooked IAT 0x73D00C (real SwapBuffers 0x779857F0)
```

and the frame counter climbs normally, so the overlay does not own that import slot. If some
future overlay build ever does, the plugin **refuses rather than corrupting it** and names the
culprit:

```
IAT slot 0x73D00C holds 0x... but gdi32!SwapBuffers is 0x...; something else already hooked it. Refusing.
```

The one-click fix for that is Steam → KOTOR → Properties → General → uncheck "Enable the Steam
Overlay while in-game".

⚠ **A frame counter of 0 at the main menu is normal on every build** — the swap hook is dormant
there and the menu runs off the `MainLoop` call site instead. The plugin says so itself
(`frame hook installed but has not fired for ~2s`). Do not read it as an overlay conflict.

## Steam: friends-list "Join Game" does nothing

`steam_api64.dll` has to be sitting beside `KotorCoop.Agent.exe`; this project cannot ship one
(see the README's Steam section). With it present the host's agent logs, once:

```
steam: ready as app 32370 (ISteamFriends via SteamAPI_SteamFriends_v018)
steam: friends can now join this session (kc1/203.0.113.7:27140)
```

Without it, exactly one line and then silence — everything else works as before. Other failure
lines to expect: `steam: SteamAPI_Init returned false` (Steam is not running, or the account
does not own KOTOR — it must own **app 32370**, whichever store the install came from), and
`steam: steam_api64.dll exports neither SteamAPI_InitFlat nor SteamAPI_Init` (the file is not a
Steamworks redistributable).

## `build.ps1 -Install` deployed into the wrong game directory

`src/KotorCoop.Plugin/build.ps1`'s `-GameDir` defaults to
`C:\GOG Games\Star Wars KOTOR (GOG)`; pass `-GameDir` for any other copy (the rig's second
instance lives at `D:\Games\swkotor-gog-p2` by `demo.ps1`'s defaults). Before 2026-08-27 the
default was a Steam path.

## Client can't connect / "no reply from \<host\>"

- Host and client `port` must match (`coop.ini port=`; the code's own default is 27015,
  but the shipped `coop.ini.example` sets `port = 27140` explicitly — see CONFIG.md).
- A session `password` must match exactly on both sides, or be left empty on both. A wrong
  one shows "Wrong password. Ask the host for the password and try again."
- The host must have said yes to the firewall question in setup (see below).
- `connect_timeout_ms` (default: the built-in 15000) is how long a client waits before
  giving up; raise it on a slow network.
- Firewalls: this is a direct UDP connection (star relay — clients talk only to the host),
  so the host's UDP port must be reachable from the client. See the next two sections.

## Windows Firewall: the host's prompt never appeared, or was dismissed

The agent starts windowless behind a fullscreen game, so Windows' "allow this app through
the firewall?" prompt on the host's first listen lands where nobody sees it — and an
unanswered prompt is a silent deny: the host's own status line looks fine while no joiner's
datagram ever arrives. The installer handles this (`WindowsFirewall.cs`). Setup asks "Allow friends to connect to
games you host?"; answer yes and Windows asks for permission. If you skipped it, run setup
again. Details:

- Run as Administrator, `KotorCoop.Agent.exe install` (and `setup`, `install.ps1`,
  `build.ps1 -Install`) adds one inbound program rule named `KotorCoop Agent` for
  `KotorCoop.Agent.exe`: UDP on the session `port` from `coop.ini` plus the LAN browser's
  discovery port 27016, and TCP on the session port (the save transfer). Re-running
  `install` after changing `port` replaces it.
- Not elevated, it prints the two exact `netsh advfirewall firewall add rule ...` commands
  to run once from an Administrator prompt.
- `KotorCoop.Agent.exe install --check` prints `PASS: Windows Firewall inbound rule
  'KotorCoop Agent' exists` or a `NOTE:` with the commands (a NOTE, not a FAIL — a LAN-only
  host whose partner is on the same PC never needs it).
- `uninstall` removes the rule when elevated, or prints the `delete rule` command.

## Internet play: what the status line's reachability wording means

Automatic port opening (UPnP) was tried once on a real home connection and did not work.
If a friend cannot connect, use a tunnel such as playit.gg.

Your public address is shown only when you type `public_address` (which also copies it to the
clipboard); the hosting announcement and `status` say only that one was found, so it does not
appear on a stream or screenshot by accident.

`status` / `public_address` on a hosting player never says a bare "UPnP mapped", because a
mapping alone does not prove the port is reachable from outside
([network-topology.md §16](research/network-topology.md)). After the router answers, the host
probes itself (§17 there: the router's WAN address vs the address STUN sees, plus a datagram
sent from a second socket to its own public address) and the line says one of:

- `UPnP mapped - local self-test passed; internet access unconfirmed` — the probe came back
  locally. That tests a local UDP path, not the internet. Share the address and have your
  friend try.
- `UPnP mapped - possible additional NAT; check upstream routers or use a tunnel such as
  playit.gg` — the router's WAN address is not the address the internet sees (another router,
  or an ISP that shares addresses). This does not prove forwarding is impossible, but a
  tunnel is the dependable path. `UPnP unavailable - possible additional NAT; ...` is the
  same with the mapping refused.
- `UPnP mapped - could not confirm it is reachable; if a friend cannot connect, use a tunnel
  such as playit.gg` — nothing contradicted the mapping but the probe did not come back
  (most home routers do not "hairpin" traffic to their own public address). Try it with
  your friend.
- `UPnP unavailable - local self-test passed on port N; internet access unconfirmed` — the
  port answered locally, for example because you forwarded it by hand.
- `UPnP unavailable - forward UDP+TCP N manually` — forward that port to this PC, both UDP
  and TCP, or use a tunnel.

`public_address` prints a second `reachability:` line with the evidence (router WAN address,
STUN address, what the probe did). The probe runs once, a few seconds after hosting starts,
after STUN and UPnP have both answered (up to 30 s).

## Host disconnected (crashed, quit, or the network dropped)

A client that loses its host is not stuck. For the player-facing messages (after about 60
seconds, then giving up at 5 minutes) see "The host stopped responding" above. Within
`host_loss_timeout_ms` of silence (default
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

## Host went to the main menu (or sat on a load screen) and I'm frozen

The host's agent keeps running at the main menu, so the link never goes silent and neither
of the two triggers above fires — before 2026-09-18 a joiner sat in a frozen world holding
every lock for as long as the host stayed parked. Now a host that stays flagged away (the
same `FlagAway` that shows "X left the world (main menu / loading)") for longer than
`host_away_timeout_ms` (default 20 s; `0` turns just this trigger off) counts as host loss
too: the same release runs, the chat line reads "Host has left the world (main menu /
loading) - playing solo until they return.", and the moment the host is back in the world
mirroring resumes on its own ("Host connection restored - rejoining.") — no reconnect, since
the link never dropped. A load screen or a quick visit to the save menu is well under the
grace period. Gated by `host_loss_release` like the other two triggers (`HostAwayRule`,
[disconnect-reconnect.md §13](research/disconnect-reconnect.md)).

## The host sees nothing when a friend on the wrong build (or wrong password) tries to join

Fixed 2026-09-18. The joiner always got a typed `E_PROTO` / `E_PASSWORD` reject (now shown to the joiner as a
plain sentence; the tag stays in the logs); the host
now also gets a chat/console/log line — `co-op: a player at 203.0.113.9 on an older build
tried to join (v50 vs your v52) - both sides need the same kotor-coop version`, or `... tried
to join with the wrong password` — once per address per 30 s, however often that client
retries (`RejectedJoinNotices`).

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
client except through `companion_local_combat`, which is **off by default in the code AND in the
shipped `coop.ini.example`** — set `companion_local_combat = 1` yourself if you want it. See
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

Separately, the default allows up to **three** mirrored enemies to fight through your local
engine at once (`local_combat_slots`, default 3). Additional enemies remain mirrored and
use forwarded damage. The “N more enemies engaged - mirrored” line explains when the local
fight capacity is full. The old one-enemy description applied to an earlier implementation;
setting `local_combat_slots = 1` deliberately restores that limit.

## A feature that "should" be on isn't doing anything

Fresh installs enable `dialogue_recruit_sync`: recruited companions are made available to
both players. `companion_local_combat` remains **off** by default; it controls a different
feature, a companion fighting for the other player through their own engine. Both values
are now explicit in the supplied settings template.

An older installation can retain earlier defaults, including disabled recruitment or shared
state features. Run the current setup program and review its recommended settings changes;
applying them backs up your existing settings first. Deliberate custom choices are preserved
unless you choose to change them. Advanced users can compare `coop.ini` with
`KotorCoop.DEFAULTS.ini`; [CONFIG.md](CONFIG.md) lists the current defaults.

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

Fixed 2026-08-29 — the "the race never dispatches" report was a wrong test recipe, not an
engine-level block: the race branch of Gadon's dialogue needs the `Tar_SummonGadon` global (not
`Tar_SwoopEng`, which an earlier round had set instead) plus three real reply picks, and with
those the race runs end to end. A KOTOR swoop track is a single-seat area, so the co-op layer
holds every other peer still with a "&lt;name&gt; is in a solo section - waiting" notice
(`minigame_lock_peers`, ON by default, `minigame_lock_timeout_s=600`) and refuses to follow-warp
anyone onto the track (`minigame_modules`); rewards (gold, journal, the `Tar_Swoop*` globals)
reach everyone through the ordinary globals channel. The one remaining gap is NEEDS-LIVE: the
keyboard half of the peer lock only confirms its own mechanism engages, never confirmed to stop
a real PHYSICAL keypress (injected keystrokes never reach this game, same limit as the Star
Forge spectate lock). The `delay_probe` diagnostic splice used to trace the original dispatch
chain ships OFF by default. See [minigames.md](research/minigames.md) §24.

## `kotorcoop-*.log` keeps growing / disk usage from logs

`kotorcoop-plugin.log` and every per-channel split (`kotorcoop-combat.log` etc.) rotate their
generations up (`*.log` -> `*.1.log` -> `*.2.log` -> ...) once they cross `coop.ini`'s
`log_max_mb` (default 8) — both at plugin startup and roughly every 30 seconds during play
(`PluginHost.RotateLogsIfNeededMidSession`, `LogRotation.PeriodicCheckIntervalMs`), so a
session that runs long enough to cross the cap on its own no longer has to wait for a restart
to shrink back down. `coop.ini`'s `log_keep` (default 3, clamped 1-10) prior generations are
kept, oldest dropped; a rotation older than that is gone. Before 2026-09-02 only one
generation was ever kept, and a mid-session rotation could overwrite the evidence for a real
bug (a corrupt damage effect logged, then rotated away) before anyone had a chance to read it
— see `LogRotation.ShiftPlan`.

`kotorcoop-agent.log` (the log for the agent process the plugin auto-starts) also rotates
during a session now: `kotorcoop-agent.log`, then `.1`, `.2` and `.3`, each capped at 8 MB by
default (`log_max_mb`; `log_keep` sets how many older copies stay). It also rotates when the
agent is launched (`AgentLauncher.RotateAgentLogIfNeeded`), so a long session no longer grows
one file without limit.

The `--log` file `tools/demo.ps1`/`verify.ps1` point the agent at directly (`agent1.log`,
`agent2.log` under `$env:TEMP\kotorcoop-demo\`) is a plain scratch file the agent writes with
its own `File.AppendAllText`. Since 2026-09-18 it rotates the same way as the plugin logs: at
agent start AND about every 30 s during the session (`AgentDashboard.RotateLogMidSessionIfDue`),
shifting `.1.log` … `.3.log` up at the plugin's default 8 MB cap. Before that it rotated only
at construction, so one long session grew it without limit.

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

**As of 2026-08-29 both channels ship ON, at mode 2** — if your stash and a merchant's stock are
not shared, the usual cause is an OLD `coop.ini`: the installer never overwrites that file, so an
install from before 2026-08-29 keeps its own `party_pool_sync=0`/`store_sync=0` lines. Set them to
`2`, or re-run `install.ps1 -GameDir '...' -UpdateDefaults`.

Two limitations are real and expected even with the channels on:

* **A shop's stock can only be READ while some player has that shop's GUI open.** That is an
  engine limitation (`CSWSStore+0x230` is only reachable through the open panel), unchanged from
  mode 1. Applying a peer's buy/sell never needs the GUI — only noticing your own does.
* **An infinite-stock item's buys never replicate**, because the engine's own stock never changes
  for one.

The history, for context: mode 1 (a bidirectional op-log, still reachable by writing `=1`) was
live-disqualified by a severe amplification runaway on the party pool (three deposits inflated
past 500 units) and a blackout+reconnect double-apply on the store, and stays disqualified. Mode 2
(a host-authoritative absolute snapshot plus client intents,
`docs/research/shared-inventory-v2.md`) replaced it and took six live rig rounds to become clean;
the last two rounds passed the party pool's own recipe with zero fabricated units and the store's
recipe three sessions in a row. Buying, selling and depositing/withdrawing have always worked
correctly on your OWN instance regardless of these flags. See [CONFIG.md](CONFIG.md), and
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

**2026-09-02:** none of the above helps if the process was TERMINATED rather than left to
report its own fault — WER off (the default) plus an engine filter that calls
`TerminateProcess` right after the crash leaves the plugin log ending mid-sentence with no
`FATAL`/`FIRST-CHANCE` line at all, because that line is only ever written by the plugin's
WORKER thread on its next poll, and a terminated process has no next poll. This killed a
client 100 ms into a follow after a wipe-recovery rejoin with nothing anywhere to explain it
(see the "CLIENT vanishing" case in
[research/transition-crashes.md](research/transition-crashes.md)). `CrashProbe`'s vectored
stub now hands the same fault record straight to `kernel32!WriteFile`, on the faulting
thread, in the same breath as filling it — no managed code involved, so it survives a
`TerminateProcess` the log-writing path never gets a chance to run for. The result lands next
to the plugin log as `kotorcoop-crash.bin`; decode it with:

```powershell
pwsh tools\crashbin.ps1 -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'
```

`tools/crashlog.ps1` points at the `.bin` for any game directory that has one, alongside the
Application-log scan above.

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
