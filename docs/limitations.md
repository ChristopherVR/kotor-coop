# Limitations — what works, what's partial, what isn't (2026-09-02)

An honest status list for someone who wants to play KOTOR 1 start to end with a friend using this
mod. Ordered the way a player hits things: connecting, moving, fighting, talking, managing the
party, shops/minigames, more than two players, input, then a list of things that look like bugs on
the rig but aren't real limitations. Sources: `docs/roadmap-seamless.md` (roadmap row numbers
below), `docs/live-verification.md`, `README.md`, and the `docs/research/` write-ups named inline.

Status words used below: **Works** (live-verified on the two-instance rig), **Partial** (works
with a real, stated gap), **Not supported** (missing or refused by design), **Unverified**
(built and unit-tested, never watched on the running game — 🎮 NEEDS-LIVE in the roadmap).

## Joining, hosting, saves and rejoins

| Item | Status | What you'll see | Source |
| --- | --- | --- | --- |
| Host/join from the in-game Co-op menu button | Works | Host presses Host, joiner picks the server and presses Connect; the agent starts itself | README Quick start |
| Bringing a save (`join_mode=save`, default) | Works | Your character carries into the host's world; a mismatched world auto-adopts the host's | README "Joining: which character you bring" |
| Bringing a brand-new character (`join_mode=newcharacter`) | Works | Play New Game normally; ~2 min later the mod quicksaves you and drops you into the host's world | live-verified 2026-08-29, `world-adoption.md` |
| `coop.ini` divergence warning (`BehaviourDigest`) | Works (core mechanism) | A real setting mismatch shows a `W_CFG` warning on both screens; nine host keys are adopted live | roadmap Tier 1 #7 |
| Protocol-version mismatch reject (`E_PROTO`) | Unverified | Should show a typed reject instead of a 15s silent timeout; never watched live | roadmap Tier 1 #6 |
| Mid-session save/load, host or client | Works | `client_load_rejoin`/`host_load_readopt` both confirmed; a genuine save-vs-load edge announces and rejoins correctly | roadmap closed #14, #32 |
| Host reload while both inventory channels are on | Works | 6/6 clean cycles, block version 64, zero pool fabrications | live-verification.md 2026-09-02 "Host in-world reload" |
| Full wipe → game-over → host reload → rejoin | Works | One down + peer rescue + game-over held; a full wipe releases on both, client follows the host's reload out of it, 3 clean cycles | `death-handling.md` §6.4-6.7, live-verification.md 2026-09-02 |
| Transition-autosave rejoin loop | Fixed | Used to loop forever after a host-only warp (read the wrong module name from the save); closed 2026-09-02 | live-verification.md "Companion damage TAKEN live" |
| Peer quits to the main menu (`FlagAway`) | Works | Peer is parked as "away" with an on-screen toast instead of a frozen statue | roadmap Tier 1 #5 |
| Host process disappears (`host_loss_release`) | Works | Every co-op lock on the client releases within ~6s, client keeps playing solo, auto-rejoins when the host returns | roadmap closed #entry, `disconnect-reconnect.md` §11 |
| Steam ↔ DRM-free (GOG) session | Works | Verified live 2026-08-29: connect, mutual presence, a module transition with the world transferred | README "Steam" section |
| Internet play (no LAN) | Partial | Public address (STUN) and UPnP port mapping are built and unit-tested; never watched against a real router; no relay for symmetric NAT | `network-topology.md` |
| 3+ players (up to 8) | Works | `demo3.ps1`/`verify3.ps1`, 34/34 checks: connect, chat, phantom mirroring across three real instances | roadmap Tier 2 #19 |

## Moving through the world and module transitions

| Item | Status | What you'll see | Source |
| --- | --- | --- | --- |
| Traveling together, either player leading | Works | The engine's own transition path; loading screens overlap; `follow_lead`/`--no-follow` configurable | roadmap closed #13 |
| Companion follow-AI "stuck at the last waypoint" stall | Fixed | A runtime detector spots the wedge and relocates the companion to the party (same call a transition itself uses); 5/5 reps recovered first try | roadmap Tier 1 #10, round 105 |
| Mirrored companion follow-AI while mirroring holds | Not supported (by design) | A companion controlled by the OTHER player doesn't run its own follow-AI at all while mirrored — structural, not a bug | roadmap Tier 1 #10 |
| Host relocating-warp crash | Fixed | Was 6/6 fatal to the host; root-caused to a party-count restore bug and fixed; 5/5 warps clean afterward on shipped defaults | live-verification.md "POOL4" round, `transition-crashes.md` |
| Real door crossing under the same fix | Unverified | The warp-crash fix was exercised via the `warp` console verb, not a real door — every real-door attempt in that round stalled short on a staging/pathing issue, not a transition failure | live-verification.md "POOL4" round |
| Repeatedly chaining the `warp` console verb | Not supported (pre-existing engine bug) | Can crash the retail engine outright; unrelated to co-op, not gated by any setting | README "Honest limitations" |
| Fog-of-war map reveal (`map_reveal_sync`) | Partial | The engine call fires correctly and survives a transition; only the on-screen M-key appearance is unconfirmed (screenshot tooling was broken) | roadmap Tier 1 #8 |
| Peer rendering as the wrong character after a warp | Fixed | Two root causes (undressed twin, census leak reading a companion as a hostile) both closed 2026-08-29 | roadmap `name-plates.md` |
| Container/door interaction race (two clicks on one chest) | Fixed | Host-arbitrated lock: exactly one winner, confirmed correct outcome and fast-path timing | roadmap Tier 1 #4 |
| Re-locking/re-sealing a door or placeable by script | Not supported (by design) | Doors/placeables are monotonic — a script re-lock never replicates to the peer | roadmap Tier 2 #16 |
| A decorative door re-swung to its other "open" value | Partial (cosmetic) | Doesn't mirror to the peer; no visible difference either way, low priority | roadmap Tier 3 #33 |

## Combat — enemies, companions, downed/revive/wipe, XP/loot

| Item | Status | What you'll see | Source |
| --- | --- | --- | --- |
| Mirrored enemies — health/death, real hits | Works | Host-authoritative, live-mirrored; a hit on your partner's phantom lands for real | README status table |
| Local combat, up to 3 real fights per client at once | Works | Two Sith engaged one client at once in the same scan, both landing real hits, two cross-side kills, an "immortal Sith" bug found and fixed the same round; the engage fallback (`local_combat_engage_fallback`) verified 2026-09-02 night: two droids engaged 752 ms after a silent script fire and the host banked their forwarded hits | roadmap Tier 2 #14, live-verification.md "N-slot local combat" and "Engagement fallback live" 2026-09-02 |
| Per-attacker damage attribution / AI creator retargeting | Live-measured, stays OFF | The write lands (`+0x158` and `+0x160` read the attacker's phantom id after a forwarded hit) but K1's shipped AI does not retarget an already-engaged creature on it; the idle → engaged case is unmeasured. Correct "who last hit me", not an aggro pull; default stays OFF | `peer-damage-ledger.md` §8.10, live-verification.md "Attacker attribution live" 2026-09-02 |
| Non-damage effect sync (stun, slow, choke, etc.) | Partial | 9 of 12 kinds live-verified both directions; 3 more (heal, temporary hitpoints, confused) built and unit-tested, never observed | README status table, `effect-sync.md` |
| Companion equipment sync (`companion_gear_sync`) | Works | Body slot and both weapon slots confirmed live, propagation under 3s | roadmap Tier 1 #11 |
| Companion XP sync (`companion_xp_sync`) | Works | Host- and client-side script XP grants converge to the higher value; cross-side kill XP also confirmed agreeing (150→300 on both sides) with no merge needed | roadmap Tier 1 #12, N-slot round |
| Companion damage TAKEN while released (`companion_damage_sync`) | Works | A client's hits downed the host's Trask (36→1→-11 hp), the engine revived him, both copies converged; two bugs found and fixed the same night | roadmap Tier 1 #34, live-verification.md 2026-09-02 (night) |
| Vendor-purchase gold ledger convergence | Unverified | Code complete, never exercised against a real vendor purchase | roadmap Tier 1 #3 |
| Downed player + revive + rescue | Works | A downed player can be rescued by a peer; full wipe/reload/rejoin cycle confirmed 3x clean | `death-handling.md` §6, live-verification.md 2026-09-02 |
| Wipe with a hostile still visible | Works | One cycle under a real fight (8+ Vulkars and droids shooting, no `down` verb): host fell first, client 14 s later, both released at once, host reload + client rejoin clean, 0 faults | live-verification.md "Wipe with hostiles visible" 2026-09-02 |
| Rejoin churn after a wipe reload | Works | Re-run after the `TryReadWorldLoadSerial` fix: exactly one `WipeRecovery` rejoin (plugin route), no SaveMismatch/OwnLoad follow-ups, both at 12/12 in 6 s | live-verification.md "Wipe reload re-run" 2026-09-02 |
| Host reload duplicates the host PC's saved equipment | Fixed twice, needs live re-verify | The "settled" item resweep ran while the engine was still finishing the loaded PC, so the save's clothes + short sword were diffed in as a script grant: the peer got a second set and the pool grew 3 → 5 keys. Fix 1 (`ItemSweepProvenanceRule.Settling`, two spaced sweeps must agree) closed at +3 s and the grant still fired at +4 s (live 2026-09-03 00:00): the ids agreed while the POSSESSOR field was still being assigned. Fix 2: the settle set is `Fingerprint(id, possessor, tagKnown)` | `shared-inventory-v2.md` §18.4, live-verification.md "Wipe reload re-run" 2026-09-02 |
| Client vanishes on the first follow after a plugin-route wipe-recovery rejoin | Open, reproduced 2/2 | Plugin-route rejoin from the game-over screen, then the host's first `warp` kills the client 5 ms after `epoch: world unloading`. `ExitProbe` named it: `EXITPROCESS(0xC0000005) called from 0x6FB0C7 (swkotor.exe)` — the engine's own top-level handler swallowing an access violation, which is why nothing else logged. The two menu-route rejoins (23:02) followed cleanly. Faulting eip pending the persisted crash record | `transition-crashes.md` "The CLIENT vanishing" 2026-09-02/03 |
| Two companions released to local combat at once | Unverified | Only one companion release was exercised in the damage-taken round | `companion-state-sync.md` §13 |
| Endar Spire / Taris duel HP floor (`SetMinOneHP`) | Not a mod limitation | KOTOR's own tutorial script floors HP at 1 on those maps — nobody can be downed there, co-op or not, until the pin clears | `min-one-hitpoint.md` |
| Container-loot race (two clicks, same chest) | Fixed | See "moving through the world" above | roadmap Tier 1 #4 |
| Creature liveness reconciliation (body alive on one screen, dead on the other) | Not supported (off by default) | `liveness_sync` ships OFF; correction mechanism exists but not enabled | roadmap Tier 3 #22 |

## Dialogue and cutscenes

| Item | Status | What you'll see | Source |
| --- | --- | --- | --- |
| One conversation at a time, spectator mode | Works | Whoever starts a conversation drives it; the other player spectates live, clicks and skips both swallowed, late-joiners catch up instantly | README status table, `dialogue-sync.md` |
| Companion recruitment sync (`dialogue_recruit_sync`) | Partial | Live-confirmed for two companions; one fix for a stale pre-recruit body isn't yet confirmed live | README "Honest limitations" |
| Mid-dialogue partner transition (spectator's copy left orphaned?) | Unverified | Machinery exists (`DialogueSpectateController`); never observed live | roadmap Tier 1 #13 |
| Star Forge finale — Bastila/Malak duel spectate | Works | Roster empties, peer's phantom hides, spectator's input parks and camera follows, a real refill ends the segment — 6 reps, no crash | roadmap Tier 1 #26, `star-forge-finale.md` |
| Input lock stopping a real physical WASD press (duel spectate) | Unverified | Only the lock's own mechanism (commandable flag, `HoldCreatureStill`) is confirmed to engage; injected keystrokes can't reach the game to test the real press | roadmap Tier 1 #26 |

## Party management

| Item | Status | What you'll see | Source |
| --- | --- | --- | --- |
| Shared party item pool (`party_pool_sync`, mode 2) | Works | ON by default; three clean host deposits mirrored one-for-one, a client deposit/withdrawal each applied exactly once, a cross-channel echo bug closed | roadmap Tier 1 #1 |
| Only the host switches active party character | Not supported (by design) | Mirrored to everyone else; control returns to your own character on connect | README "Honest limitations" |
| Galaxy map / pre-departure party-select GUI | Not supported | Each instance runs the vanilla UI with no co-op awareness; the client's panel is suppressed but galaxy-map gating and an auto-confirm fallback are undone; the legs themselves have never been driven live (no non-story primitive reaches them) | roadmap Tier 2 #18 |
| `end_m01ab` escape-pod party-select GUI | Not supported (by design) | Only the host can answer it — no console primitive reaches inside; confirmed to gate the corridor's entrance for every start-to-end run | roadmap Tier 2 #31 |

## Stores, Pazaak, minigames (swoop)

| Item | Status | What you'll see | Source |
| --- | --- | --- | --- |
| Merchant store stock (`store_sync`, mode 2) | Works | ON by default; three clean sessions in a row — buy, sell-back, re-buy succeeding once and refused the second time, 155s idle with zero spurious writes | roadmap Tier 1 #20 |
| Store reads while nobody has the shop GUI open | Not supported (engine limitation) | A shop's stock can only be read while some player has that GUI open | README "Honest limitations" |
| Pazaak detection + "waiting" notice | Works | Latch, park, and the waiting toast all confirmed on a real completed hand | roadmap Tier 1 #9 |
| Ending a Pazaak hand (the CLEAR direction) | Unverified | No console primitive drives `EndPazaakGame`; only a real completed GUI hand reaches it | roadmap Tier 1 #9 |
| Swoop racing | Works, one player at a time | The race branch runs end to end once the right dialogue path is taken; the other player is held with a "racing — waiting" notice instead of being warped onto the single-seat track; rewards reach everyone | roadmap Tier 3 #21 |
| Input lock stopping a real WASD press (swoop) | Unverified | Same limit as the duel spectate lock above — injected keystrokes never reach this game | roadmap Tier 3 #21 |

## 3+ peers

| Item | Status | What you'll see | Source |
| --- | --- | --- | --- |
| Three-plus real instances (host + 2 joiners, up to 8) | Works | 34/34 checks passed: connect, chat, phantom mirroring all agreeing across three real instances | roadmap Tier 2 #19 |
| Multi-minute door-transition soak with 3+ peers | Unverified | Needs a cutscene-free warp target; not yet run | roadmap Tier 2 #19 |

## Input

| Item | Status | What you'll see | Source |
| --- | --- | --- | --- |
| Gamepad/controller support | Not supported (by design, no fix planned) | The server browser, password entry and the `~` console are mouse/keyboard only | roadmap Tier 2 #27 |

## Known rig-only artifacts (not real limitations)

These showed up during live testing and are worth knowing about if you're chasing a "bug" — they
are testing-rig quirks, not things a real playthrough will hit:

- **Console `god on` on the client PC** trips the absorb watchdog during combat traces — a testing
  artifact of the dev cheat, not a co-op defect (live-verification.md, N-slot round).
- **A stale plugin build in a second/third game directory** produced a false "nothing happening on
  the client" reading in two separate rounds (3+ peers, swoop) — always compare installed DLL
  sizes before trusting a negative result.
- **The `movenear`/`movenear <id>` console verb** resolves a stale id against Trask in `tar_m10aa`,
  snapping him to (0,0,0) — a dev-verb quirk, not a gameplay bug; move the host's PC instead.
- **Screenshot/screen-capture tooling on the rig has been unreliable**, which is why a few
  on-screen-only checks (the connection indicator's render, the M-key map appearance) are marked
  Unverified even though their underlying data path is confirmed working.

## Unverified — implemented but never observed live

Everything in this list is built and unit-tested; nobody has watched it run on the two-instance
rig yet. Good targets for the next live-test pass:

1. Vendor-purchase gold ledger convergence (progression sync, roadmap #3).
2. *(measured 2026-09-02: the field is written, the AI does not retarget on it — see the table.)*
3. Three newly-added effect-sync kinds: heal, temporary hitpoints, confused.
4. `E_PROTO` protocol-version-mismatch reject.
5. Mid-dialogue partner transition — whether the spectator's copy of the conversation orphans.
6. Ending a Pazaak hand (the CLEAR direction / `EndPazaakGame`).
7. The keyboard half of the input lock (swoop peer-lock and Star Forge duel spectate) actually
   stopping a real physical key press — injected keystrokes can't reach this game to test it.
8. A real door crossing exercising the host relocating-warp crash fix (only `warp` has been used).
9. The 3+ peer door-transition soak (needs a cutscene-free warp target).
10. Internet play against a real router (STUN discovery + UPnP mapping).
11. *(done 2026-09-02, one cycle under a real fight — see the table.)*
12. Two companions released to local combat at the same time.
13. Galaxy map / escape-pod leg's own co-op-specific gating (#2/#4 in roadmap Tier 2 #18) —
    the legs themselves have no non-story primitive to drive them.
14. The persistent connection indicator's actual on-screen render (data path confirmed, pixels not).
