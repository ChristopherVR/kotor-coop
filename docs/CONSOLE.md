# Console

Press `~` in-game. Every verb below is parsed in `src/KotorCoop.Session/ConsoleCommand.cs`
and completes via `src/KotorCoop.Plugin/ConsoleVerbCatalog.cs` (Tab, including the game's own
retail verbs — `giveitem`, `heal`, `revealmap` and the rest are untouched, since they only
ever touch the typist's own single-player state). `warp` is the one deliberate exception: the
co-op layer intercepts it because a module transition has to go through the session (every
spawned body is flushed before the module unloads, and the peer follows). See
[console-cheats.md](research/console-cheats.md) for the full authority reasoning, the wire
format, and the live-verification checklist — this page is the short reference.

## Session and connection

| Verb | Args | Authority | What it does |
| --- | --- | --- | --- |
| `connect` / `join` | `<ip\|#n> [password]` | any player | connect to a host |
| `host` | `[password]` | any player | start hosting |
| `disconnect` / `dc` / `leave` | — | any player | leave the session |
| `reconnect` / `rc` | — | any player | reconnect to the last session |
| `password` / `pw` | `<pw>` | any player | set the password before connecting; never recorded in console history |
| `say` / `chat` / `msg` | `<message>` | any player | send a line to every player |
| `status` / `stat` | — | any player | connection state, player count |
| `lagstats` | — | any player | interpolation delay, real send/recv intervals, agent pump cost |
| `public_address` / `publicaddress` / `pubaddr` | — | host only | this host's public IP:port (STUN) and UPnP mapping verdict, for sharing with a joiner over the internet — see [network-topology.md](research/network-topology.md) |
| `who` / `peers` | — | any player | list connected peers |
| `where` | — | any player | print your own coordinates |
| `servers` / `browse` / `list` | — | any player | list discovered servers |
| `warp` | `<module> [waypoint]` | any player | **the one shadowed retail verb** — travels through the session so the peer follows, instead of the engine's own unsynced warp. **Not safe to chain rapidly for testing**: a pre-existing, unfixed `swkotor.exe` engine defect (stale-pointer bugs in `CExoKeyTable::DestroyTable`/`CSWCObject::GetServerObject`, unrelated to any coop code — see [transition-crashes.md](research/transition-crashes.md)) is far more likely to fire when a transition happens near-instantly, as `warp` does, than through a real door's several seconds of walk+fade; a 12-transition real-door-only soak found zero crashes on the same build where a `warp`-driven soak reliably crashes by transition 2-3 |
| `netsim` / `net` | `off\|status\|loss <pct>\|latency <ms> [jitter]\|blackout <secs> [in\|out\|both]` | any player | fault-injection: simulate loss/latency/an outage |
| `clear` / `cls` | — | any player | clear the console |
| `help` / `?` / `commands` | — | any player | list commands |

## Staging cheats (co-op-aware)

Added only where the answer depends on who typed it, or where nothing else reaches the
scenario. Parsed in `ConsoleCommand`, dispatched by `CoopCommandDispatcher`, executed by
`CheatController` on the game thread.

| Verb | Args | Authority | What it does |
| --- | --- | --- | --- |
| `god` | `[on\|off]` | yours only | pins your own character's damage floor at 1 hp |
| `topup` | `[hitpoints]` | yours only | heals your own character, clamped to max |
| `standup` / `getup` | `[hitpoints]` | yours only | stands your own downed body back up |
| `hurt` | `<amount> [slot]` | dev-only, local only | applies REAL damage through the engine's own `EffectDamage`/`DoDamage` path (shields, difficulty scaling and both 1-hp floors apply exactly as they would to a live hit) to yourself or a party-roster companion; blank slot = yourself, `0-8` = the companion slot — see [death-handling.md](research/death-handling.md) |
| `down` | `[slot]` | dev-only, local only | forces the SAME genuine temp-death state a real lethal hit produces (not a fake flag) on yourself or a party-roster companion: bypasses a `min1hp` pin for one hit, then restores it — a real party member never truly dies from this, it enters exactly the state the downed/revive pipeline already knows how to recover from. Blank slot = yourself, `0-8` = the companion slot — see [death-handling.md](research/death-handling.md) |
| `slay` | `[tag\|id]` (blank = nearest hostile) | **authority split** — see below | kills an enemy: directly if you own it, forwarded through the damage ledger if a peer owns it |
| `tp` | `<x> <y> [z]` | yours only | teleports your own character (no walkmesh check) |
| `goto` | `<player\|#n>` | same module only | teleports you to a peer's last known position |
| `dropitem` | `<resref\|0x131>` | shared by construction | drops a real, clickable item everyone sees; `0x..` drops a copy of a live item with its real stack/charges/upgrades |
| `destroyobject` / `destroy` | `<id>` | host-only, no forward path | runs the engine's real `DestroyObject` — refuses outright on a mirroring instance instead of forwarding |
| `items` | `[n]` | read-only, local | lists live items (id, name, holder, identity) in the agent log |
| `item` | `<0x131>` | read-only, local | reads one item back, for the identity-loop's receiving side |
| `setitem` | `<0x131> [stack=N] [charges=N] [max=N] [upgrades=0xN] [identified=0\|1]` | yours to stage, local | pokes one live item's identity fields through the real apply call |
| `giveloot` | `<owner id> <resref> [stack]` | yours to stage, local | gives a materialised enemy's stand-in one item of its real loot |
| `applyeffect` | `<kind> [target] [durationMs] [magnitude]` | staging only | applies a non-damage effect locally; aimed at a phantom, it also forwards for real |
| `effects` | — | read-only, local | last 8 captured/applied peer-effect records |
| `leader` | `<slot\|pc>` | dev-only, local | switches YOUR control to a party-roster companion 0-8, or back to your PC |
| `recruit` | `<slot> [tag]` | dev-only, local | forces a party-roster slot's NPC into your party (live-test primitive) |
| `skipline` | — | yours to stage, local | finishes the current dialogue line/VO immediately (conversation owner only) |
| `skipkey` | — | yours to stage, local | sends a skip KEYPRESS into the dialogue panel's own input dispatch (`CSWGuiDialog::HandleInputEvent(panel, 0x27, 1)`, vtable slot 0x3C, shared by both dialogue panel classes) — the only route that passes through `DialogueSkipHook`, the spectator skip gag. `skipline` cannot test that hook: it pokes the owner's object flag directly, never entering the input path, and refuses outright on a spectator. The answer line reports the gag's swallow count before → after, which is the oracle (blockpoke-only, see docs/research/dialogue-skip-desync.md) |
| `selectreply` | `<slot>` | yours to stage, local | commits a reply on your own conversation, like a real click |
| `save` | — | yours only, local only | quicksaves through the engine's own `CClientExoAppInternal::DoQuickSave` — the same call the retail quicksave binding makes, `GetIsAllowedToSave` gate and all. ⚠ "applied" means REQUESTED: it sends a client→server message and the directory appears a tick or more later. Used by `join_mode = newcharacter` to obtain a first-time joiner's freshly created character, since a save is the only portable form KOTOR has for one (blockpoke-only, see docs/research/world-adoption.md) |
| `loadgame` | `<slot>` | yours only | runs the real engine Load Game path against save slot `<slot>` (0-based, most-recent-first) |
| `quittomenu` | — | yours only, local only | tears THIS instance down to the main menu through the engine's own `ShutDownToMainMenu` — the same call a real "Quit Game → Yes" click reaches; built to exercise the Away flag without click-navigating the pause menu (blockpoke-only, see docs/research/disconnect-reconnect.md §9) |
| `newgame` | — | yours only, local only | opens the game's OWN character-creation screens from the main menu, by replaying `CSWGuiMainMenu::OnNewGamePicked`'s body. For automation and for `join_mode = newcharacter`; a player just clicks NEW GAME |
| `chargen` | `quick [class 0-5]` \| `accept` | yours only, local only | `quick` picks a class through the panel's own `OnSelectButton` and applies the Quick Character template; `accept` presses Play (`CSWGuiMainCharGen::BeginPlayGame`), which starts the new game and loads the Endar Spire. (⚠ it does NOT write `TEMP:pifo`, as this row previously claimed — the chargen writer emits `Temp\temp.bic`; see docs/research/world-adoption.md's 2026-08-29 correction.) Calls the engine's own handlers — no synthesised mouse input, which on this rig is measurably unreliable |
| `scriptgive` | `<resref> [stack]` | yours only, local only | gives an item straight into your own inventory with NO suppression-ledger entry, simulating an untracked script (OnEnter/OnDeath/heartbeat) grant to test `script_item_sync` (blockpoke-only, see docs/research/dialogue-consequences.md) |
| `loottake` | `[tag\|id]` (blank = nearest container) | yours only, local only | removes a container/corpse's first item through the real engine `RemoveItem` call and gives an equivalent copy to you — reproduces a genuine container/corpse take (both a real removal and a real new inventory row) for `script_item_sync`'s container/corpse-loot control, since no injected click can drive the container GUI (blockpoke-only, see docs/research/dialogue-consequences.md) |
| `pooladd` | `<resref> [stack]` | yours to stage, local | adds a resref stack to the shared party item pool, through the same `CItemRepository::InsertItem` channel a peer's own pool deposit uses — the live-test primitive for `party_pool_sync` (see docs/research/party-pool-sync.md) |
| `poolremove` | `<resref> [stack]` | yours to stage, local | removes a resref stack from the shared party item pool, the removal counterpart to `pooladd` |
| `grantxp` | `<id\|me> <amount>` | yours to stage, local | adds a signed XP delta to a creature's `CSWSCreatureStats::experience`, simulating a script-granted award aimed at one creature — the live-test primitive for `companion_xp_sync`'s merge (see docs/research/companion-state-sync.md) |
| `equip` | `<id\|me> <resref> [slot]` | yours to stage, local | re-equips a creature (1 body, 4 right weapon — default, 5 left weapon) through `Odyssey.EquipItemFromTemplate`, the same call the phantom equipment channel already ships, aimed at a live companion body — the live-test primitive for `companion_gear_sync` |
| `grantgold` | `<amount>` | yours to stage, local | adds a signed delta to the party wallet through the same agent-side field read/write `progression_sync` already uses — the live-test primitive for gold convergence (no GUI vendor purchase needed) |
| `setglobal` | `<name> <int\|true\|false\|on\|off>` | yours only, local only | writes a script global by name through the engine's own type-checked setter (`CSWGlobalVariableTable::SetValueNumber`/`SetValueBoolean`), not a raw poke — see docs/research/quest-sync.md |
| `pazaak` | `[npc-tag\|id] [deck]` | yours only, local only | starts a Pazaak hand through `CClientExoApp::StartPazaakGame` directly (no wager, no tutorial), so `pazaak_detect` can be verified positively — see docs/research/minigames.md |
| `pazaak` | `end` | yours only, local only | ends the current Pazaak hand through `CClientExoApp::EndPazaakGame` directly, so `FlagInMinigame` can be watched clearing without playing a real hand to completion through the GUI — see docs/research/minigames.md |
| `openstore` | `<resref\|tag\|id>` | yours only, local only | opens the store GUI against a resolved store through `CGuiInGame::ShowStoreGui` directly, so `store_sync` can be verified without a manual click — see docs/research/store-sync.md |
| `closegui` | — | yours only, local only | closes the full-screen in-game GUI (equipment / inventory / character / abilities / journal / map / options) through the engine's own `CSWGuiInGame::HideSWInGameGui`. Exists for `openstore`: that screen is what sets `CGuiInGame+0x30`, the one precondition `ShowStoreGui` refuses on, and nothing else in the console clears it — which is why the CLIENT half of four consecutive live store rounds never ran (`skipline`/`selectreply 0`/`partyselect ok` all leave that field alone). Answers "already closed" as success, and is a deliberate no-op when the screen is not up — see docs/research/shared-inventory-v2.md §14 |
| `popmodal` | `[count]` | yours only, local only | pops up to `count` panels (default 1, max 8) off the GUI manager's own modal stack through the engine's own `CSWGuiManager::PopModalPanel` 0x0040BE00 (`__thiscall`, no stack arguments, a safe no-op on an empty stack — VERIFIED by full disassembly). The half `closegui` was missing: `HideSWInGameGui` bails at 0x0062CBD1 while that stack is occupied, so `closegui` alone could only ever REPORT the store blocker, never clear it. What puts a panel there is this project's own menu-load path, which constructs a `CSWGuiSaveLoad` panel and never pops it — see docs/research/shared-inventory-v2.md §15 |
| `buy` | `<store-tag\|id> <resref> [count]` | yours only, local only | buys `count` (default 1) units of `resref` from the named store through `CSWGuiStore::BuyItem` — the REAL Buy-button handler, so gold and item both move through the engine's own path; requires the store's own GUI to already be open (`openstore` first) and matching; refuses each unit it cannot afford (naming the price and current gold) rather than letting `SetGold` underflow, since `BuyItem` itself has no affordability check of its own — see docs/research/store-sync.md |
| `sell` | `<store-tag\|id> <resref> [count]` | yours only, local only | sells `count` units of `resref` out of your own inventory (resolved through `CSWSCreature::GetItemRepository`, the SAME shared-party-pool accessor the store GUI's own sell list uses) to the named store — the sell side of `buy`, same requirements. Drives `CSWSStore::AcquireItem`/`CSWGuiStore::GetItemSellValue`/the split-stack helper directly rather than the real `CSWGuiStore::SellItem` click handler (that handler was found to silently no-op via four early bail-out gates). Live-confirmed working 2026-08-28 — item leaves the player and gold is credited by the correct price, verified three independent ways; `store_sync` itself stays OFF because the automatic wire mirror to the other instance was never observed to fire — see docs/research/store-sync.md |
| `startdialog` | `<npc-tag\|id> [dlg-resref]` | yours only, local only | starts a conversation between you and the named NPC through the engine's own `ActionStartConversation` VM routine, queued on you exactly as a real click would be, so scripted conversations can be triggered without walking into their trigger and the existing dialogue hooks observe it normally — see docs/research/dialogue-sync.md |
| `removeparty` | `<slot>` | dev-only, local only | **DEV-ONLY**: calls the engine's real `RemovePartyMember(nNPC)` on party-roster slot 0-8 through the same VM parameter-stack dispatch `startdialog` uses, so the solo-segment detector's script-agnostic `RemovePartyMemberHook` can be exercised without reaching `k_sta_bastila.dlg`'s own entry 70 — see docs/research/star-forge-finale.md §11 |
| `setgold` | `<amount>` | dev-only, local only | **DEV-ONLY**: sets the party's gold directly to `amount` (non-negative, absolute) through the real `CSWSCreature::SetGold`, clamped server-side to 999,999,999 — unlike `grantgold`'s signed delta, this repairs a save whose gold was driven negative-then-wrapped by an unaffordable `buy` before this pass's own affordability guard — see docs/research/store-sync.md and docs/live-verification.md |
| `followkick` | `<slot>` | dev-only, local only | **DEV-ONLY**: dumps NPC roster slot `<slot>` (0-8)'s follow-record fields (phase, attempts, target, current path node) to the plugin log, then forces `record+4=10` (`TeleportForward`) UNCONDITIONALLY, regardless of the companion's current phase — unlike the automatic `party_follow_phase2_kick` pass (phase 2 only). A raw diagnostic poke for testing whether the same recovery move also resumes the phase-7 near-target freeze — see docs/research/party-reconciliation.md Round 96 |
| `followstall` | `<slot> [method]` | dev-only, local only | **DEV-ONLY**: dumps NPC roster slot `<slot>` (0-8)'s follow-record WEDGE SIGNATURE (phase, attempts, pathNode/destNode/writeCursor, and the two squared distances `PathWaitDest`'s own strict accept test compares at 0x00512BF2) to the plugin log, then applies ONE follow-stall recovery UNCONDITIONALLY — no wedge check, no 2-second stall timer, no cooldown, unlike the automatic `party_follow_stall_recovery` pass. `method` is `1` phasereset, `2` followleader, `3` positionparty, `4` priortie; omit it for whatever `party_follow_stall_recovery_method` selected. Exists so a live A/B can isolate all four candidates inside one game session — see docs/research/party-reconciliation.md Round 105 |
| `partyselect` | `ok` | yours only, local only (meant for the HOST) | confirms the native party-selection screen with its current selection through the panel's own OK/Done handler (`ConfirmPartySelection`), the same code path a real click reaches — see docs/research/galaxy-map-and-party-select.md |
| `cheats` | — | any player | prints this verb list |

### The `slay` / `destroyobject` authority split

Both verbs run `CheatAuthority.ForSlay` / `ForDestroy` in this order: refuse if the target
can't be resolved, refuse a player's own character, refuse a peer's phantom, refuse a party
companion, refuse a plot-flagged creature. What's left depends on
`SharedBlock.OffsetDamageDealtEnabled` (whether this instance mirrors somebody else's
enemies):

- **`slay`**, not mirroring → kills it here directly (`KillCreature`, real corpse and loot).
  Mirroring → forwards `100000` damage through the peer-damage ledger instead of touching the
  local copy, so the owner's engine produces the real death.
- **`destroyobject`**, not mirroring → runs `DestroyObject` here. Mirroring → **refuses**
  outright (`RefuseNotOwner`) — there is no destroy-ledger to forward through; carrying a
  destroy to a mirroring instance is the [creature-liveness](research/creature-liveness.md)
  channel's own job.

## Diagnostic and dev primitives

Lower-level probes used to stage or observe a live test; not day-to-day player commands.

| Verb | Args | What it does |
| --- | --- | --- |
| `census` | `[id]` | this instance's enemy census, or one creature's entry |
| `resolve` | `<id>` | what the engine knows about one id |
| `firescript` | `<id> [fire [270]]` | peeks or fires an object's `+0x260`/`+0x270` event script |
| `movenear` | `<id> [x y [z]]` | snaps a creature next to the LOCAL PLAYER by default (a fixed +X offset, no facing math), or to an explicit point when `x y [z]` are given (2026-09-02: used to snap to a hardcoded Endar Spire point regardless of module — a live `tar_m10aa` `movenear 0x46` sent a creature off the map; see `docs/research/shared-state-audit.md` round 36's dated addendum and `CheatController.MoveNear`'s own doc comment) |
| `walkto` | `<x> <y> [z]` | drives your own character toward coordinates via synthetic input (unlike `tp`'s instant snap; there is no native "walk player here" server message to borrow, so this drives local input instead) |
| `moviewarp` | `<module> <movie...> \| queue <movie...> \| clear \| show` | stages a movie for the next transition |
| `usedoor` | `[tag\|id]` (blank = nearest transition door) | uses a door through the engine's own input, like a click |
| `opendoor` | `[tag\|id]` (blank = nearest door) | opens a door with no transition needed |
| `useplaceable` | `[tag\|id]` (blank = nearest container) | uses a chest/corpse/switch through the engine's own input |
| `closeplaceable` | `[tag\|id]` (blank = nearest container) | closes what `useplaceable` opened |
| `movieplay` | `<resref>` | plays a movie right now (blocks the main thread) |
| `faction` | `<id\|me> [vsId\|me]` | read-only: reports the engine's own `GetAIStateReputation` verdict (both directions) plus each side's raw faction id, for narrowing a "why won't this creature fight" question — see docs/research/combat-participation.md §10 |

## Notes

- **Passwords are never recorded.** `password`/`pw` lines (including `/pw` and `coop
  password`) are dropped from console history on the way in and the way out.
- **Tab completion covers the engine's own verbs too** — `ConsoleCommandTable` reads the
  retail command table live, so a mod that registers its own verb completes for free.
- Every staging cheat that fails says *what it saw* (`"0x2A is a peer's phantom, not an
  enemy"`, `"no creature tagged 'foo' here"`) rather than silently no-opping — see
  [console-cheats.md](research/console-cheats.md) for the full detail-string convention and
  the wire format shared by every cheat.
