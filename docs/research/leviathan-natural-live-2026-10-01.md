# Leviathan route, live staging (2026-10-01)

Build main `1445deed`, private game4/game5, host fixture `000013 campaign_lev_ready` (Bastila, Canderous, Carth available; no save is a real pre-capture Hawk). Evidence: `artifacts/leviathan-natural-live-20261001-2025/`.

Capture trigger: `setglobal K_STAR_MAP 40`, `K_CAPTURED_LEV 5`, `K_CURRENT_PLANET 6`, `warp ebo_m12aa`, `galaxymap depart 4` (decoded from `k_sup_galaxymap.ncs`: `K_STAR_MAP==40 && K_CAPTURED_LEV==5` selects the capture stunt; a wrong planet row sent the host to STUNT_00 and split the players).

| Stage | Result |
| --- | --- |
| Capture (STUNT_16) | Partial. Both games ran the natural scene, then stalled 6 minutes because no object tagged `bastila` existed (fixture limit); a spawned `p_bastilla001` and re-queued `sc16_converse` moved the host to `ebo_m40aa`, and the joiner needed the same two steps. |
| Carth conversation to prison | Pass. Same `lev40_carth` lines on both; Canderous picked; both landed in `lev_m40aa` together (needed a staged `p_bastilla` body). |
| Interrogation (Saul) | Pass. Same `lev40_saul403` lines on both. |
| Control swap | Pass with caveat. Host `solo segment: began ... SwitchPlayerCharacter`; joiner `spectating ... input parked`, W moved 0.0; released with `the fight ended - control is yours again`. Combat was skipped (host teleported to the terminal); the joiner screen was mostly black while spectating and it was still in its cage afterwards. |
| Gear | Host pass (6 rows in the locker). The joiner started with no gear, so private-gear loss was not tested; real locker retrieval GUI not exercised. |
| Ghost bodies | Partial. After release the joiner held stale `sithcommander`, `sithtrooper1`, a second `carth` and a `bastila` in the cage that the host did not have. |
| Host save and load mid-scene | Pass. Host `loadgame` 21:27:01; joiner re-adopted and rendered by 21:27:11; one click on the "Item(s) Received" popup, then it moved 8.1 m. |
| Gate (elevator to `lev_m40ab`) | Intermittent fail. Run 1: joiner crashed 100 ms after the host took the elevator, AV `0x4D7E44` in `CServerExoAppInternal::UnloadModule` / `CGameObjectArray` (`run2-evidence/game5-kotorcoop-crash.bin`), while the joiner was in its post-reload scene state. Two repeats transitioned together without a crash. |

Not reached: command deck, Saul on the bridge, hangar, Malak reveal, escape to the Hawk. Real combat and the gear-retrieval GUI were staged around. Open items: stale joiner-side copies after release, the elevator crash (needs an owner), and a pre-capture Hawk save with Bastila in the party.
