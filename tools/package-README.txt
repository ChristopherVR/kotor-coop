kotor-coop {{VERSION}}
======================

Two-player co-op for Star Wars: Knights of the Old Republic (the DRM-free GOG build,
v1.0.3). Two full copies of the game run side by side -- on one machine or across a network
-- and this package bridges them into one shared world: you see each other as real in-game
characters, travel, fight, loot, talk and pause together.

No game file is modified. Everything lives beside swkotor.exe and can be removed again.

What to expect. You can play the whole game together, start to finish. Either player can
lead through doors and area changes, and either player can pick the destination on the galaxy
map -- the other one is taken along automatically, take-off movie and all. Enemies have one
shared health bar, your hits land, and stuns, slows and heals reach your partner for real. The
party's inventory, credits, merchant stock, doors, chests, quests and the journal are all one
shared copy. Companions follow the host's game; set companion_local_combat = 1 in coop.ini
(off by default) to let one also fight for the other player through their own copy of the game.
One conversation runs at a time: whoever starts it picks the replies and the other player watches
it live. Pazaak and the swoop race are single-seat -- the other player is told they're waiting.
If somebody saves, loads, quits to the menu, or the host's game crashes, the other side notices
and rejoins on its own. The first time you join you create a co-op character through the normal
New Game screen; the mod keeps it and uses it on every join after. Mouse and keyboard, or any
Xbox-style controller -- see "Controller" below.
The full list -- what works, what has a catch, and what is deliberately left to the host -- is
docs/limitations.md in the full repository (link at the bottom).

Install -- pick one
--------------------

A. Extract into the game folder. Unzip this whole archive into the folder that contains
swkotor.exe (for GOG usually C:\GOG Games\Star Wars KOTOR (GOG)). That's it -- the zip *is*
the install. Start the game.

B. Double-click KotorCoop.Agent.exe. Unzip anywhere, run the exe. It finds your KOTOR install
(GOG or Steam, or asks if it finds none or several), copies everything in, and offers to launch
the game.

Both do the same thing. Re-installing over an older version just works; your coop.ini is
never overwritten. If another mod already has dinput8.dll (ReShade, an ASI loader, a
widescreen fix...) both methods notice and set kotor-coop up to run alongside it automatically
-- see "Another mod already uses dinput8.dll" below; you don't need to know anything about it
in advance.

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
the host's world at the area entrance; your own save files are never written. To start a
brand-new game together, the host plays until the first autosave (or saves once), then hosts.

Want a different co-op character? Set join_mode = newcharacter in coop.ini for one join and
you go through character creation again (the old one is replaced). join_mode = save is the old
behaviour -- the character from your own most recent save is the one carried in.

Related: auto_adopt_world (on by default) is what fetches the host's world whenever yours
does not match theirs. With it off you can still join, but enemies, doors, containers and the
party stop being shared -- the two of you are in worlds that disagree about which object is
which. Neither setting ever writes your own saves.

Press ~ in game for the console: connect <ip> [password], disconnect, reconnect,
say <message>. Tab completes, Up/Down recalls history. You don't need the console just to
see what your partner said, though: messages and status lines show up on their own in a screen
corner, fading out after a few seconds -- Page Up/Page Down scrolls back through them. Both the
console and the chat readout are configurable (position, size, how long lines last) in
coop.ini.

The host's game decides what happens with enemies and the party; each player owns their own character.
Only one conversation runs at a time -- the other player watches it live. If your partner
goes to the main menu their character disappears and a line says so; a one-line status in
the top-left always shows the connection state.

Recruiting a companion in conversation gives them to both players (dialogue_recruit_sync, on by
default). If a companion is missing on one side, leave and rejoin and they will be there. Setting
dialogue_recruit_sync = 0 in coop.ini restores the old behaviour where only whoever ran the
conversation keeps them.

Controller: plug in any Xbox-style (XInput) pad and it works, in the menus and in the
world -- left stick walks and turns, right stick is the cursor, A is the default action / click,
B is Escape, X cancels combat / right-clicks, Y switches character, LB/RB cycle targets or
pages, LT pauses, RT clicks, the D-pad is action slots 1-4 (scrolls on a menu), Back is the
map, RS click forces cursor mode. Your swkotor.ini bindings are respected. A pad can't type:
the address box and the password box (their titles say so), chat and the ~ console still
need a keyboard. gamepad = 0 in coop.ini turns it off.

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
  say <message>              Both    Send a chat line to the other player.
  status                     Both    Show the connection state. While hosting, also shows your
                                     public address and UPnP status (see "Play over the
                                     internet" below).
  who                        Both    List connected players and which area each is in.
  where                      Both    Show your own area and position.
  public_address             Host    Show the address/port to share for an internet game.
                                     Answers "not hosting" on a client.
  steamstatus                Both    Report whether Steam invites are working. While hosting,
                                     also prints the exact string a friend's Join Game carries.
  steaminvite <steamid64>    Host    Invite one friend by SteamID64. Refused when not hosting.
  gamepad status             Both    Report what the controller support is doing (pad found,
                                     mode, held keys). gamepad on/off toggles it without editing
                                     coop.ini.
  clear                      Both    Clear the on-screen chat/status overlay.
  help                       Both    List every console command, including internal diagnostic/
                                     testing verbs not covered here (see `cheats` for those) -
                                     nothing beyond this table is needed to play.

Play over the internet
-----------------------

The server browser only finds games on your own network. To play with someone elsewhere:

1. The host presses Host as usual, then checks status (or types public_address) at
   the ~ console. It prints something like:

       public address: 203.0.113.5:27140 - UPnP mapped - reachable from the internet

   That means it worked automatically -- nothing else to do. A few seconds after hosting
   starts the mod checks its own address from outside, so the line tells you honestly:
   - "reachable from the internet": share the address.
   - "NOT reachable (your ISP shares your address - use a tunnel such as playit.gg)": your
     home connection sits behind your ISP's own address sharing, and no port forward on
     your router can get through it. A free tunnel program (playit.gg or similar, UDP AND
     TCP on the same port) is the way; the mod copes with a UDP-only tunnel on its own.
   - "could not confirm": nothing contradicted the mapping, but the check could not prove
     it either (many routers don't route their own public address back inside). Try it
     with your friend; if they can't connect, use a tunnel.
   If it instead says "UPnP unavailable - forward UDP+TCP 27140 manually", open your router's
   admin page and forward that port (both UDP and TCP -- TCP carries the initial save
   transfer, UDP carries the session) to this PC, for both. Most home routers support UPnP
   and this step is automatic; if yours doesn't, forwarding by hand is the alternative --
   there's no relay of our own.

   Windows Firewall: the installer adds an inbound rule for KotorCoop.Agent.exe when it is
   run as Administrator (UDP on the session port plus 27016 for the LAN browser, TCP on the
   session port); otherwise it prints the two netsh commands to run once from an
   Administrator prompt. KotorCoop.Agent.exe install --check tells you whether the rule
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

- Windows -- or Linux / Steam Deck with the game running under Proton or Wine, see "Linux and
  Steam Deck" below. Nothing else to install: KotorCoop.Agent.exe carries its own .NET
  runtime (that is why it is ~40 MB).
- KOTOR 1, v1.0.3. A DRM-free (GOG) copy is what this is developed and tested against.
  Steam's copy is the same binary under a DRM wrapper -- the engine addresses are correct on
  it too -- but it is only partly supported; see "Steam" below.
- For two players on one machine: a second copy of the game folder, with this installed
  into both. Start the second copy through the helper (it renames the game's single-instance
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

Linux and Steam Deck
---------------------

The mod is two Windows programs that live inside the game's own Wine/Proton prefix: the
dinput8.dll the game loads, and KotorCoop.Agent.exe, which the DLL starts and stops for
you. Neither needs anything installed into the prefix (the agent carries its own .NET). What
you need to do differently from Windows is two things:

1. Put the files in the game folder. Get the Linux package
   (kotor-coop-<version>-linux.tar.gz; the Windows zip has the same files minus the script)
   and run its installer -- it runs natively, no Wine involved:

       tar -xzf kotor-coop-<version>-linux.tar.gz
       ./kotor-coop-<version>/install.sh           # finds the game, installs, prints step 2

   It looks in every Steam library (including a Steam Deck's SD card and the Flatpak client),
   ~/Games/Heroic and ~/Games; pass --game-dir /path/to/swkotor if it finds none or
   several. It does exactly what the Windows installer does: if another mod already owns
   dinput8.dll (ReShade, an ASI loader, a widescreen fix) it keeps that mod and chains it
   behind kotor-coop (see "Sharing dinput8.dll with another mod"), it never overwrites an
   existing coop.ini, and install.sh --check / install.sh --uninstall report and undo.
   Manual alternative: extract the archive straight into the game folder -- Steam:
   ~/.local/share/Steam/steamapps/common/swkotor/ (on a Steam Deck in Desktop Mode, or
   right-click the game -> Manage -> Browse local files); Heroic / Lutris: "Open install
   folder" -- which is a plain install with no chaining. The Windows installer also works from
   inside the prefix (protontricks-launch --appid 32370 KotorCoop.Agent.exe, Heroic's
   "Run EXE on prefix", Lutris' "Run EXE inside Wine prefix"); it finds the game under
   Z:\home\... the same way.
2. Tell Wine to load our dinput8.dll, not its own. Wine ships a built-in DirectInput
   and prefers it over a DLL beside the exe unless told otherwise:
   - Steam / Steam Deck: right-click the game -> Properties -> Launch Options:
     WINEDLLOVERRIDES="dinput8=n,b" %command%
   - Heroic: game settings -> Advanced -> Environment variables: WINEDLLOVERRIDES = dinput8=n,b
   - Lutris: Configure -> Runner options -> DLL overrides: dinput8 -> n,b

   Without it the game runs fine and the mod simply never loads. kotorcoop-plugin.log
   appearing in the game folder is the sign it worked; it logs "running under Wine x.y".

Then play as on Windows -- the same host/join, the same coop.ini. A few things to know:

- Firewall. If your distribution runs one (ufw, firewalld), the host has to open the
  UDP port (27140 by default) like a Windows host would; UPnP works the same way. A Steam
  Deck has no firewall out of the box.
- Controller. The Deck's built-in controls (and any pad Steam Input presents as an Xbox
  controller) drive the game through the mod's controller support; the address box, chat and
  the console still want a keyboard -- on a Deck, the on-screen keyboard (STEAM + X).
- Copying the address to the clipboard from the agent's console may do nothing under
  Wine; the address is always printed on screen and in the log.
- Two copies on one Linux machine (launch --instance 2) has not been tried.

Proton 9 or newer (or the current Proton Experimental / GE) is what to expect this to work
with. None of this has been run on a Linux machine by the developers yet -- it is built to
work under Wine and checked against Wine's documented behaviour, and the first reports from
real Decks and desktops are what will turn "should work" into "works". Please send them,
with kotorcoop-plugin.log and the agent's log.

Configure
---------

coop.ini beside swkotor.exe is the only config file. Nothing in it needs editing to play;
open it and every key is commented in plain language, grouped under Connecting, Your character,
Screen, Internet play and Advanced. The most commonly-touched ones:

  name               Your name as shown to your partner (blank = a generated one).
  password           Optional. Must match on both sides.
  port               UDP port (default 27140). The host's port must be reachable from the
                     client.
  host / mode        Address to join and host/join mode. Optional: the in-game server list and
                     its address box don't need them. A host set here pre-fills the address
                     box until you first use it; after that the box remembers what you last
                     typed (kotorcoop-last-address.txt beside the game).
  menu_button        1 shows the Co-op button on the main menu.
  follow_lead        who may lead an area transition the other player follows automatically.
  dialogue_auto_join whether you're pulled into a conversation your partner starts.
  stun_servers       STUN servers asked for the host's public address (see "Play over the
                     internet"). Empty disables it.
  upnp               1 (default) tries to forward the port automatically when hosting.

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

Removes only what was installed (it recognises its own dinput8.dll by a marker and leaves any
other mod's alone, restoring dinput8.prev.dll to its original name) and keeps your coop.ini.
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
  must be open; passwords must match exactly.
- Nothing happens at all: look for kotorcoop-plugin.log beside the game. If it does
  not exist the plugin never loaded -- dinput8.dll must be in the same folder as
  swkotor.exe (read dinput8-chain.log if you use the chain loader).
- Second instance on one machine exits immediately: start it through the helper (see
  Requirements), which renames the game's single-instance mutex.
- The game crashed and you want to report it: kotorcoop-plugin.log's last lines are the
  first thing to attach. For a crash the log itself can't explain (the game just vanishes,
  nothing after "FATAL"), turn on Windows' own crash dumps for swkotor.exe:

      # from an Administrator PowerShell, in the extracted package folder:
      .\install.ps1 -GameDir "C:\GOG Games\Star Wars KOTOR (GOG)" -EnableCrashDumps

  This needs elevation (a plain PowerShell gets a clear error naming the reason) because it
  writes a machine-wide Windows Error Reporting setting, not a per-game file. The next crash
  leaves a .dmp file in <game folder>\crashdumps (or wherever -DumpFolder pointed) --
  attach that to a bug report. Turn it back off with
  .\install.ps1 -GameDir "..." -DisableCrashDumps; it does not undo itself automatically.

Full documentation, source and issue tracker: https://github.com/ChristopherVR/kotor-coop
