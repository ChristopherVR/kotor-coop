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
| [client-encounters.md](client-encounters.md) | a CLIENT walking into an ambush | event 0xC is just `[+0x2E0] = enterer; SpawnIfAppropriate` — reproduce it on the host with the peer's phantom. `player_only` (34 of 47 base-game encounters) is the gate. ⚠ §8: the enemy channel never materialises a creature the mirror lacks, so host spawns are invisible to a client TODAY. |

Priority order for implementation (value / risk):
1. **Name plates client-twin write** — small, closes a wrong claim, verifiable in memory.
2. **Map presence** — small, no protocol, high visible value.
3. **k_coop_phantom.utc + shrink MarkAsRemotePlayer** — small, mod-safe, fixes equip risk.
4. **Mod-compat hardening** — wire the globals digest; failure-code + latch for missing
   blueprint (#3); clamp appearance rows (#2); exe hash (#5). Medium.
5. **Digest handshake** — protocol v4, needs the C# game-file readers. Medium-large.
6. **Party reconciliation** — medium, gated on live solo-mode measurement.
