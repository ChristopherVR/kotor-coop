kotor-coop {{VERSION}}
======================

License: personal, noncommercial use of official releases is permitted with a
lawfully acquired game. Contributions to the official project are permitted;
independent modified releases and commercial reuse require written permission.
Read KotorCoop.LICENSE.txt. This is an unofficial fan mod; game rights remain
with their owners. We do not condone piracy or claim endorsement. Third-party
licenses apply separately. Legal review and the full dependency notice audit
remain outstanding; see KotorCoop.LEGAL.txt and KotorCoop.THIRD-PARTY-NOTICES.txt.

Two-to-eight-player co-op for Star Wars: Knights of the Old Republic (GOG or Steam,
v1.0.3), on 64-bit Windows. Each player needs their own copy -- on one machine or across a network
-- and this package bridges them into one shared world: you see each other as real in-game
characters, travel, fight, loot, talk and pause together.

The game executable on disk is unchanged; the mod hooks the running game.
Mod files live beside swkotor.exe and can be removed.
Co-op play writes dedicated saves and uses the game's Auto Save slot; see "Play" below.
Your own autosave is kept first, as a save called "Autosave before co-op".

Updates preserve coop.ini. Setup previews player settings matching older defaults
and asks before applying changes, with a backup first. Decline to keep those values.
KotorCoop.DEFAULTS.ini contains this package's defaults for comparison.

What to expect. This is an experimental beta. General-release sign-off is pending.
A complete co-op campaign from start to finish
has NOT been verified. Early-game routes and selected later scenes have been tested;
three-to-eight-player campaign play is less extensively tested. Either player can
lead through doors and area changes, and either player can pick the destination on the galaxy
map -- the other one is taken along automatically, take-off movie and all. Enemies have one
shared health bar, your hits land, and stuns, slows and heals reach your partner for real. The
backpacks and credits are personal by default; quest items, merchant stock, doors, chests,
quests and the journal are shared. Companions follow the host's game; set companion_local_combat = 1 in coop.ini
(off by default) to let one also fight for the other player through their own copy of the game.
One conversation runs at a time: whoever starts it picks the replies and the other player watches
it live. Pazaak and the swoop race are single-seat -- the other player is told they're waiting.
If somebody saves, loads, quits to the menu, or the host's game crashes, the other side notices
and rejoins on its own. The first time you join you create a co-op character through the normal
New Game screen; the mod keeps it and uses it on every join after. Mouse and keyboard.
The full list -- what works, what has a catch, and what is deliberately left to the host -- is
docs/limitations.md in the full repository (link at the bottom).

Install
--------------------

Close KOTOR and its helper agent. Back up your game's saves folder before your first
co-op session. Extract the whole zip into its OWN folder and double-click
KotorCoop.Agent.exe. It finds your KOTOR install
(GOG or Steam, or asks if it finds none or several), copies everything in, and offers to launch
the game.

Setup asks "Allow friends to connect to games you host?" Say yes, and say yes again when
Windows asks for permission. If you skipped it, run KotorCoop.Agent.exe again.

If your game is in Program Files (GOG Galaxy's default), Windows protects that folder. Setup
notices and offers to restart as administrator; say yes. If it says "No GOG or Steam KOTOR
install found", find the folder yourself: on Steam, right-click the game > Manage > Browse
local files; in GOG Galaxy, open the game, click the settings cog > Manage installation >
Show folder. If setup says it is running from inside the zip file, extract the whole zip
first. If swkotor.exe is not version 1.03, setup warns you; update the game through GOG or
Steam and run setup again.

Use this installer for updates on every player's computer; it preserves coop.ini.
If another mod already has dinput8.dll (ReShade, an ASI loader, a
widescreen fix...) the installer offers to preserve it and load both mods together
-- see "Another mod already uses dinput8.dll" below; you don't need to know anything about it
in advance. This preserves the loader; it does not certify gameplay compatibility.
Use the same co-op version and matching game mods for your first session.

Direct extraction into the game folder is only for a clean install without dinput8.dll
or coop.ini already there. Extracting over an existing install can overwrite another mod
and your settings; Explorer cannot run the installer's preservation logic.

Windows says it is unsafe, or antivirus removed a file
-------------------------------------------------------

The files are not code-signed, so Windows SmartScreen may say "Windows protected your PC".
Click More info, then Run anyway. To check the download, compare its SHA-256 with the one
on the release page.

Some antivirus programs quarantine dinput8.dll after install. Then the Co-op button does not
appear. Add the game folder as an exclusion (Windows Security > Virus & threat protection >
Manage settings > Exclusions) and run KotorCoop.Agent.exe again to restore the file.
KotorCoop.Agent.exe install --check says when the file is missing.

What the mod changes
--------------------

- It adds its files beside swkotor.exe. The game executable itself is not changed.
- It turns on the game's cheat console setting (EnableCheats=1 in swkotor.ini) so the ~
  console works. Your original file is kept as swkotor.ini.kcoop-orig, and uninstall puts
  the original setting back.
- Before co-op uses the Auto Save slot, your own autosave is kept as a save called "Autosave
  before co-op" (the two most recent are kept). If the game crashes during a co-op save,
  your own quick save is restored, or kept as "Recovered quicksave".
- It adds a "Co-op" save and saves\kotorcoop-character.* for your co-op character.

Play
----

1. Start KOTOR normally. (The mod starts its own helper process, KotorCoop.Agent.exe, in
   the background -- that is expected.)
2. Click Co-op on the main menu.
3. One player presses Host; the other picks that server from the list and presses Connect.
   If the host is not on your network, pick "Connect to an address..." at the bottom of the
   list, type the address the host gave you and press OK (the box remembers the last one).
   If the host set a password, a box asks for it.
4. Host: load a save. Joining for the first time: the game's character-creation screens
   open -- make your character exactly as you normally would. Your game starts as usual, and a
   few seconds later you are pulled into the host's world with it. Joining again later: nothing
   to do; your co-op character is taken straight into the host's world.

What the join actually does: the host must have a save on disk (any manual save, or the
game's own first autosave) -- that save is what the joining player receives. Their co-op
character is separate from any single-player game on the same install: the mod keeps it in
saves\kotorcoop-character.*, refreshes it whenever you save while connected, and drops it into
the host's world at the area entrance. While connected the mod keeps its own dedicated save --
"Co-op" in the Load Game list -- refreshed every 10 minutes and as you leave, holding the
HOST's world. Your named saves and Quick Save are not touched by the
mod (if the game crashes during a co-op save, your Quick Save is put back, or kept as
"Recovered quicksave"). The game's own Auto Save slot is still used to bring the host's world
in the instant you join or rejoin (an engine requirement), and keeps autosaving through it as
you play. Your own single-player autosave is kept first, as "Autosave before co-op" (the two
most recent are kept). Load "Co-op" for the save meant to be kept, not Auto Save. To start
a brand-new game together, the host plays until the first autosave (or saves once), then hosts.

Want a different co-op character? Set join_mode = newcharacter in coop.ini for one join and
you go through character creation again (the old one is replaced). join_mode = save is the old
behaviour -- the character from your own most recent save is the one carried in.

Related: auto_adopt_world (on by default) is what fetches the host's world whenever yours
does not match theirs. With it off you can still join, but enemies, doors, containers and the
party stop being shared -- the two of you are in worlds that disagree about which object is
which. Neither setting ever writes your named saves.

If the host stops responding, after about a minute you see "The host has stopped responding.
Still trying - type `disconnect` to play on alone." After five minutes the mod gives up:
"Gave up on the host after 5 minutes. Use Co-op > Connect, or type `reconnect`, to try
again."

When your install differs from the host's, you get a plain sentence on screen, for example
"Your co-op mod version differs from the host's ... Both players need the same release." or
"Wrong password. Ask the host for the password and try again." The short error tags are
written to the logs only.

Press ~ in game for the console: connect <ip> [password], disconnect, reconnect,
say <message>. Tab completes, Up/Down recalls history. You don't need the console just to
see what your partner said, though: messages and status lines show up on their own in a screen
corner, fading out after a few seconds -- Page Up/Page Down scrolls back through them. Both the
console and the chat readout are configurable (position, size, how long lines last) in
coop.ini.

The agent window is its own full-screen console: a sticky status line at the top that never
scrolls away, a scrolling transcript of chat/replies/events in the middle, and a > prompt
always visible at the bottom with Tab completion (Shift+Tab cycles backward, a short list
shows the top matches as you type) and Up/Down recall. Same commands, same dispatcher, no
alt-tabbing into the game required. Scroll the transcript with the mouse wheel, PageUp/PageDown
or the scrollbar; Ctrl+Home/Ctrl+End jump to the top/bottom, End jumps back to the bottom and
resumes following new lines (a "N new lines below" note appears while you're scrolled up).
Type clear or cls, or press Ctrl+L, to wipe the transcript. F1 opens a details panel (network
stats, this player, other players, the shared world) in place of the transcript. F2 drops a
demo item, F3 picks one up.

The host's game decides what happens with enemies and the party; each player owns their own character.
Only one conversation runs at a time. To watch it live, click the same person your partner
is talking to; scenes the game starts by itself include both players automatically. If your partner
goes to the main menu their character disappears and a line says so; a one-line status in
the top-left always shows the connection state.

Recruiting a companion in conversation gives them to both players (dialogue_recruit_sync, on by
default). If a companion is missing on one side, leave and rejoin and they will be there. Setting
dialogue_recruit_sync = 0 in coop.ini restores the old behaviour where only whoever ran the
conversation keeps them.

Console commands
-----------------

Press ~ in game to open the console. Tab completes a command name, Up/Down recalls history.
"Host" means it only does something useful for whoever is hosting; "Client" means it's for
whoever joined; "Both" behaves the same on either side.

  connect <ip|#n> [pw]       Both    Join a host by address, or by its row number from the last
                                     `servers` listing (connect #1 joins the first one found).
  password <password>        Client  Answers a join's password prompt.
  host                       Host    Start hosting (the same as pressing Host on the main menu).
  servers                    Client  List co-op games found on the local network.
  disconnect                 Both    Tear down the current session.
  reconnect                  Both    Restart the last session with the same address/settings.
  say <message>              Both    Send a chat line to the other player. Past chat is kept
                                     in the pause menu's Messages screen (Dialog tab).
  status                     Both    Show the connection state. While hosting, also shows your
                                     UPnP status and whether a public address was found (see
                                     "Play over the internet" below).
  who                        Both    List connected players and which area each is in.
  where                      Both    Show your own area and position.
  public_address             Host    Show the address/port to share for an internet game, and
                                     copy it to the clipboard. This is the only place the
                                     address is shown, so it stays off stream and screenshots
                                     until you ask. Answers "not hosting" on a client.
  steamstatus                Both    Report whether Steam invites are working. While hosting,
                                     also prints the exact string a friend's Join Game carries.
  steaminvite <steamid64>    Host    Invite one friend by SteamID64. Refused when not hosting.
  clear                      Both    Clear the on-screen chat/status overlay.
  help                       Both    List every console command, including internal diagnostic/
                                     testing verbs not covered here (see `cheats` for those) -
                                     nothing beyond this table is needed to play.

Play over the internet
-----------------------

The server browser only finds games on your own network. To play with someone elsewhere:

1. The host presses Host as usual, then types public_address at the ~ console. Your
   address is not shown on screen until you do. It prints something like:

       public address: 203.0.113.5:27140 - UPnP mapped - local self-test passed; internet access unconfirmed

   Automatic port opening (UPnP) was tried once on a real home connection and did not work.
   If a friend cannot connect, use a tunnel such as playit.gg. When it does work, the
   router accepted the mapping. The self-test runs from your own PC, not outside:
   - "local self-test passed; internet access unconfirmed": share the address and have
     your friend try connecting. This tests a local UDP path, not internet access or TCP.
   - "possible additional NAT": check upstream routers or use a tunnel such as playit.gg.
     A private or different router address can mean another router, ISP address sharing,
     or another egress route. It does not establish that forwarding is impossible.
     The mod also handles a UDP-only tunnel on its own.
   - "could not confirm": nothing contradicted the mapping, but the check could not prove
     it either (many routers don't route their own public address back inside). Try it
     with your friend; if they can't connect, use a tunnel.
   If it instead says "UPnP unavailable - forward UDP+TCP 27140 manually", open your router's
   admin page and forward that port (both UDP and TCP -- TCP carries the initial save
   transfer, UDP carries the session) to this PC, for both. Most home routers support UPnP
   and this step is automatic; if yours doesn't, forwarding by hand is the alternative --
   there's no relay of our own.

   Windows Firewall: setup asks "Allow friends to connect to games you host?" and Windows
   asks for permission (UDP on the session port plus 27016 for the LAN browser, TCP on the
   session port). Say yes. If you skipped it, run KotorCoop.Agent.exe again, or run the two
   netsh commands it prints from an Administrator prompt. KotorCoop.Agent.exe install --check tells you whether the rule
   exists. Without it Windows may silently drop your friend's traffic: the agent runs
   behind the game, so its firewall prompt is easy to miss.
2. Share the address the host was given (or a hostname/DDNS address pointing at it -- connect
   accepts either) with the other player.
3. The joiner clicks Co-op, picks "Connect to an address..." at the bottom of the server
   list (there's nothing to browse for an internet game), types that address and presses OK.
   The box remembers the last address. Typing connect <address> at the ~ console does the
   same thing.

If a join just sits there and eventually says "no reply... is UDP port N forwarded on the
host?", that usually means the port really isn't reachable from outside the host's network yet
-- re-check the router's port forward (or UPnP status) before trying again.

Privacy note: discovering the host's own public address contacts a small, fixed list of
public STUN servers (Google's, by default) over UDP -- nothing about your session or characters
is sent, only enough to ask "what does the internet see me as". Set stun_servers = (empty) in
coop.ini to turn this off if you'd rather not; you can still play by sharing a manually
port-forwarded address instead. There is currently no relay or NAT hole-punching, so a router
with UPnP off *and* a restrictive ("symmetric") NAT type will need the manual port forward --
see docs/research/network-topology.md if you want the full picture.

Requirements
------------

- 64-bit Windows. Nothing else to install: KotorCoop.Agent.exe carries its own .NET
  runtime (that is why it is ~40 MB).
- The game runs at its own 800x600 size in co-op. Wide-screen resolutions were tried and
  the game fell back to 800x600.
- KOTOR 1, v1.0.3, from GOG or Steam. Mixed-store sessions have been tested;
  see "Steam" below for same-PC restrictions and optional invites.
- For two players on one machine: a second copy of the game folder, with this installed
  into both; at least one must be DRM-free (GOG). If using Steam, launch it first.
  Start the second DRM-free copy through the helper (it renames the game's single-instance
  lock): KotorCoop.Agent.exe launch --instance 2 --game-path <second copy>\swkotor.exe.

Steam
-----

Steam's KOTOR is retail 1.0.3 wrapped in Valve's DRM. The wrapper only encrypts the code
section on disk and unpacks it before the game starts, so this mod's engine addresses are
correct on the Steam copy too and dinput8.dll loads the same way, live-verified end to end
including a Steam player and a GOG player sharing a session (module transitions, world
transfer and all -- see docs/research/steam-support.md if you want the evidence). Two caveats
remain:

- Two copies on one PC needs a DRM-free build. The Steam copy always goes through the
  Steam client, which will not run it twice.
- If the Steam overlay is in the way (not seen in testing, but the fallback if a future
  overlay build ever changes this), kotorcoop-plugin.log will say
  "frame hook NOT installed: ... something else already hooked it". Turn the overlay off for
  KOTOR (right-click the game -> Properties -> General) and relaunch.

Friends-list invites ("Join Game", steam_invites in coop.ini) let a friend join without
being told an IP. Optional and off unless you set it up: copy a steam_api64.dll next to
KotorCoop.Agent.exe (from the Steamworks SDK, or from any Steam game you own -- this mod is
not allowed to ship Valve's). Without that file you get one line in the log and everything else
works exactly as before. Both players need to own KOTOR on Steam, and while the agent runs
Steam will show you as playing KOTOR. Once it's active:

- Type steamstatus at the ~ console any time to check it's working -- it prints your own
  SteamID64 and, while hosting, the exact string a friend's "Join Game" carries.
- A friend just needs to click Join Game on your entry in their Steam friends list; nothing
  else to do on either side.
- To invite someone who isn't looking at your profile right now, type
  steaminvite <their SteamID64> while hosting (their SteamID64 is on their Steam profile:
  "Copy Player ID", or the digits at the end of the profile URL).

Set steam_invites = 0 to stop the agent talking to Steam at all.

Configure
---------

coop.ini beside swkotor.exe is the only config file. Nothing in it needs editing to play;
open it and every key is commented in plain language, grouped under Connecting, Your character,
Screen, Internet play and Advanced. The most commonly-touched ones:

  name               Your name as shown to your partner (blank = a generated one).
  password           Optional. Must match on both sides.
  port               Session port (shipped settings: 27140; code fallback: 27015).
                     Use the actual port shown by the host. UDP carries the session;
                     TCP carries the initial save transfer, with UDP transfer fallback.
  host / mode        Address to join and host/join mode. Optional: the in-game server list and
                     its address box don't need them. A host set here pre-fills the address
                     box until you first use it; after that the box remembers what you last
                     typed (kotorcoop-last-address.txt beside the game).
  menu_button        1 shows the Co-op button on the main menu.
  follow_lead        who may lead an area transition the other player follows automatically.
  dialogue_auto_join whether you're pulled into a conversation your partner starts.
  stun_servers       STUN servers asked for the host's public address (see "Play over the
                     internet"). Empty disables it.
  upnp               1 (default) tries to forward the port automatically when hosting. It has
                     not worked on the one real home connection tried; use a tunnel if needed.

That's the whole file -- it's deliberately short. The mod has many more internal switches for
tuning and bisecting bugs during development; they aren't in coop.ini on purpose, since a
player never needs them. Every one of them is documented, with what it does and its shipped
value, in the project's docs/CONFIG.md under "Developer switches" -- setting one by hand is
unsupported.

Another mod already uses dinput8.dll (ReShade, an ASI loader, ...)
--------------------------------------------------------------------

Only one file can be called dinput8.dll on disk. If another mod already owns it, kotor-coop
notices automatically and sets both mods up to run together -- no prompt, no flag, nothing
for you to do. All you'll see is one line saying what it found, for example:

    found another mod in dinput8.dll (ReShade); installing as a chain: our loader takes
    dinput8.dll, the other mod is kept as dinput8.prev.dll and loaded after us

Any .asi mods (next to the game, or in scripts\/plugins\) are left alone either way. A newer
kotor-coop version installed later keeps this working the same way -- nothing to redo.
Something seem off? Run this; it reports exactly what's installed and checks it, without
changing anything:

    KotorCoop.Agent.exe install --check

If you'd rather decide yourself instead of the automatic detection, three flags on
KotorCoop.Agent.exe install (or install.ps1) cover it:

    --chain        always run alongside the other mod, even before one is detected
    --force        replace the other mod outright instead of running alongside it
    --load <file>  also add another DLL (e.g. a second mod) to the ones that run together

For example: KotorCoop.Agent.exe install --game-path "..." --chain --load ReShade32.dll

Technical details, only needed if something has to be fixed by hand: the automatic setup
installs a small wrapper as dinput8.dll, our plugin as KotorCoop.Plugin.dll, renames the
other mod to dinput8.prev.dll, and writes the load order to dinput8.chain (an annotated
blank example is dinput8.chain.example; dinput8-chain.log records what actually happened on
the last launch). If you extracted the zip straight over another mod and it got overwritten,
running KotorCoop.Agent.exe install --chain again puts everything back the way it should be
-- there's no need to move files by hand.

Uninstall
---------

From the extracted zip (not from inside the game folder -- the helper cannot delete itself
while running from there):

    KotorCoop.Agent.exe uninstall --game-path "C:\GOG Games\Star Wars KOTOR (GOG)"

Removes what was installed: the mod files, the coop-mods folder, the kotorcoop-agent logs and
kotorcoop-last-address.txt. It recognises its own dinput8.dll by a marker and leaves any
other mod's alone, restoring dinput8.prev.dll to its original name, and it puts your original
EnableCheats setting back in swkotor.ini. It keeps, on purpose, your coop.ini, the "Co-op"
save and saves\kotorcoop-character.*, and lists them when it finishes. If it finds two
installs, it asks, or add --game-path.
install.ps1 -GameDir ... [-Uninstall|-Chain|-Force|-Check] is the same thing for PowerShell
users.

How it works, briefly
----------------------

    swkotor.exe (x86)                                         swkotor.exe (x86)
      dinput8.dll = plugin: frame hook, engine calls,           dinput8.dll = plugin
      a shared memory block the helper reads/writes
            |                                                        |
      KotorCoop.Agent.exe (x64, started by the plugin)          KotorCoop.Agent.exe
            \______________________ UDP ____________________________/

Windows looks for dinput8.dll in the game's own folder before the system one, so the game
loads our plugin itself -- no injector. The plugin lives inside the game and does the in-engine
work (spawning your partner's body, mirroring enemies, doors, items, dialogue) and starts the
helper. The helper lives outside, attaches to the game process, and speaks UDP to the other
player's helper. Clients talk only to the host.

Something is wrong
-------------------

- No Co-op button, and dinput8.dll is missing: antivirus probably removed it. See "Windows
  says it is unsafe" above.
- Not sure the install is right (especially with a chained mod involved): run
  KotorCoop.Agent.exe install --check (or install.ps1 -Check, from the extracted package).
  It reports what's actually there -- plain, chained, or nothing installed -- checks that every
  chained DLL is really present, and prints one PASS/FAIL line per check without changing
  anything. Every ordinary install run already ends with this same report.
- Upgraded from an older version and something that should be on isn't (no Co-op button,
  the party's shared stash or a shop's stock not shared, ...): the installer never overwrites
  your coop.ini, so a file from an older version keeps that version's settings. Every install
  ends by listing what's out of date; install.ps1 -GameDir '...' -UpdateDefaults rewrites just
  those lines (and removes leftover developer switches from older, longer templates) without
  touching anything else in the file. Two shop limitations remain even when on: a shop's stock
  is only read while somebody has that shop open, and an infinite-stock item's purchases never
  replicate.
- You join and come up as the host's character: an older version could copy the host's
  character into your saved co-op character when you quit. Delete saves\kotorcoop-character.pifo
  and saves\kotorcoop-character.json in your game folder once and join again -- you will make a
  fresh character, and from then on it is kept correctly.
- Partner never appears / "no reply from host": ports must match and the host's UDP port
  must be open; passwords must match exactly. Your friend can only reach you if you said yes
  to the firewall question during setup.
- Nothing happens at all: look for kotorcoop-plugin.log beside the game. If it does
  not exist the plugin never loaded -- dinput8.dll must be in the same folder as
  swkotor.exe (read dinput8-chain.log if you use the chain loader).
- Second instance on one machine exits immediately: start it through the helper (see
  Requirements), which renames the game's single-instance mutex.
- The game crashed and you want to report it: double-click KotorCoop.CollectLogs.cmd
  in the game folder or extracted package. It puts kotorcoop-support-<date>.zip on your
  Desktop: the co-op logs, the crash record and a copy of coop.ini with the password and
  host address blanked. Attach that one file. Nothing is uploaded, and the logs can still
  show IP addresses and character names. For a crash the log itself can't explain (the game just vanishes,
  nothing after "FATAL"), turn on Windows' own crash dumps for swkotor.exe:

      # from an Administrator PowerShell, in the extracted package folder:
      .\install.ps1 -GameDir "C:\GOG Games\Star Wars KOTOR (GOG)" -EnableCrashDumps

  This needs elevation (a plain PowerShell gets a clear error naming the reason) because it
  writes a machine-wide Windows Error Reporting setting, not a per-game file. The next crash
  leaves a .dmp file in <game folder>\crashdumps (or wherever -DumpFolder pointed) --
  attach that to a bug report. Turn it back off with
  .\install.ps1 -GameDir "..." -DisableCrashDumps; it does not undo itself automatically.

Full documentation, source and issue tracker: https://github.com/ChristopherVR/kotor-coop
