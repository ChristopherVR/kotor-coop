# Research (2026-07-31)

Five parallel deep-dives into the retail GOG binary and game files, feeding the
"proper co-op experience" build. Each is verified against `swkotor.exe` (GOG, disk ==
memory) or the symbol DB / game files, not folklore. Nothing in the game install was
modified.

| File | Topic | Headline |
| --- | --- | --- |
| [name-plates.md](name-plates.md) | phantom name in the selection band | server write was correct; must ALSO write the client twin (band never re-transmits names). No TLK. |
| [map-presence.md](map-presence.md) | peer on the map | map reads waypoint position live/frame — a directly-constructed peer-pin waypoint, moved per tick. ~150 lines, no asset. Bonus: shared fog reveal. |
| [override-surfaces.md](override-surfaces.md) | game files vs plugin | ship `k_coop_phantom.utc` (blanks stock AI, fixes equip profs, sets min1hp/faction); main-menu = hybrid; never ship 2DA/TLK. |
| [party-reconciliation.md](party-reconciliation.md) | companions drift | ownership split via solo mode + explicit ActionFollowLeader, keyed by NPC slot. Corrects trap 5 (commandable≠stop). Medium, needs live measurement. |
| [mod-compatibility.md](mod-compatibility.md) | different mods per peer | 20 assumptions (several C); two-phase digest handshake; wire the already-computed globals digest first. |
| [connect-flow.md](connect-flow.md) | in-game Connect UI | rebind BTN_WARP; reuse CSWGuiSaveNamePanel as an IP prompt; password stays in coop.ini. |
| [console-overlay.md](console-overlay.md) | in-game `~` console | retail ships the whole Aurora dev console; enable via EnableCheats and READ the command buffer — no guessed callback ABI, no crash risk. `PrintStringScreenPos 0x0044CCA0` paints reply lines. |
| [connect-flow.md](connect-flow.md) | in-game Connect UI | achievable — rebind BTN_WARP, reuse CSWGuiSaveNamePanel as an IP prompt (real editbox at panel+0x548); no password masking (keep it in coop.ini). |
| [enemy-presence-sync.md](enemy-presence-sync.md) | one visible fight, not two private ones | owner streams enemy position/facing/stance (protocol v12); mirror drives its copies with the phantom recipe and disarms local enemy AI per pass. Combat-participation options recorded. |
| [client-encounters.md](client-encounters.md) | a CLIENT walking into an ambush | event 0xC is just `[+0x2E0] = enterer; SpawnIfAppropriate` — reproduce it on the host with the peer's phantom. `player_only` (34 of 47 base-game encounters) is the gate. §8's materialisation gap is CLOSED — see enemy-materialisation.md. |
| [enemy-materialisation.md](enemy-materialisation.md) | the mirror BUILDS an enemy it lacks | no blueprint name survives on a live creature and the tag lies (53/156 encounter entries) — so the census carries appearance+faction and the mirror rebuilds from the phantom recipe, with an ownerId↔localId map. The `+0x08` type-field trap, birth grace, and the fault-proof sweep live here. |
| [peer-damage-ledger.md](peer-damage-ledger.md) | carrying a peer's contribution to a fight | health was a level the owner's own mirror kept overwriting before it could be sampled — so a peer now sends a cumulative sum of damage DEALT, and the owner spends it as real engine damage. Proves from the instruction stream that re-running the receiver's resistance pipeline is not double-counting (the amounts are pre-resistance; difficulty never applies to an enemy). The kill falls out of the arithmetic. |
| [world-adoption.md](world-adoption.md) | a joiner plays the HOST's world, with their own character | the save-identity gate could only ever refuse a player who had played alone; adoption makes it pass by construction. The host's save is staged over the autosave slot and the joiner's character is carried in as `Temp\pifo.ifo`, so the engine's own `LoadCharacterFromIFO(-1)` flavour — the one `MenuLoadController` deliberately reroutes AWAY from — becomes the correct path. Measured: a saved PC's `ObjectId` is the `0x7FFFFFFF` sentinel, so nothing can collide. ⚠ §3: a host AUTOSAVE carries a pifo, which would put both players in the same body. |
| [network-topology.md](network-topology.md) | peer-to-peer, and a host with nobody at the controls | two verdicts. **Keep the star relay**: the transport is already destination-per-send, the host already survives a client rebinding, and the identity + session-id material a hole punch needs is already on the wire (the session id is minted, sent, and thrown away) — but the prize is one hop, at three or more players, and every channel is shaped for one writer. **Dedicated = a parked PC, not headless**: the engine's login chain always manufactures a player and the load handshake waits for two of them, and four `_player is not null` gates mean a PC-less host fails *silently*. 68 host-has-a-human sites enumerated, 24 already graceful; the primary-player role is "lowest live peer id", which join order already gives for free. |
| [engine-api.md](engine-api.md) | every engine call the plugin makes | the callee's own `ret` immediate IS the argument size, so a declaration can be checked mechanically. Two were wrong; `RemoveFromArea` leaked four bytes of stack per call, which is how a 0xC0000409 fast-fail appears somewhere unrelated. 5,399 DB rows now confirmed against this binary, 310 known bad. `tools/callaudit.py` guards it in verify.ps1. |

Priority order for implementation (value / risk):
1. **Name plates client-twin write** — small, closes a wrong claim, verifiable in memory.
2. **Map presence** — small, no protocol, high visible value.
3. **k_coop_phantom.utc + shrink MarkAsRemotePlayer** — small, mod-safe, fixes equip risk.
4. **Mod-compat hardening** — wire the globals digest; failure-code + latch for missing
   blueprint (#3); clamp appearance rows (#2); exe hash (#5). Medium.
5. **Digest handshake** — protocol v4, needs the C# game-file readers. Medium-large.
6. **Party reconciliation** — medium, gated on live solo-mode measurement.
