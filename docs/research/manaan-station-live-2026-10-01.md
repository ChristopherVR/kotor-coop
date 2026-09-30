# Manaan station live preflight and combat recovery — 2026-10-01

The unchanged inherited Manaan station fixture loaded at actual native 12/12 HP on both players. Original survivor dialogue, station movement, nearby combat rescue and host-reload/client-recovery worked. The suit, underwater trip, Rift puzzle, reunion and reconnect after that trip remain **unproven**. This bounded run did not complete the route and does not establish natural Manaan arrival.

## Scope and ownership

Two private copies (`artifacts/live-installs/game1` and `game2`) ran on port 39147 with Native95 from `artifacts/live-native-v95-status-dismiss` (9fc39e6) and Agent95 from `artifacts/live-agent-v95-archived-locker` (66c4580), wire84. The original `000022 - campaign_manaan_return_ready` was privately copied unchanged as `000922 - staged_manaan_original`. Owned native PIDs were 24436 and 45380; scoped harness ownership records are in `artifacts/manaan-route-v95/live/rig/rig.json`.

No raw HP, class, quest/global or AI-bit writes were used. Movement used native queued walking; original doors were opened through the engine primitive and their original callbacks. No diagnostic warp was used for the station route. Developer `attack stop` cancelled the own PC's actual combat round to permit withdrawal. Original installs were untouched.

## Observed results

| Check | Actual result |
|---|---|
| Correct saved-health oracle | Both loaded `manm28aa` at 131.1,196.5,5.3 with actual 12/12 HP. Raw saved CurrentHitPoints 0/HitPoints 0/MaxHitPoints 12 is healthy under the verified GOG SaveStats encoding. |
| Original station dialogue | Walking through original doors triggered the survivor conversation. Normal displayed replies completed the discussion about the Selkath attack and entering the station; no dialogue/global bypass was used. |
| Real station combat | Original patrol/Selkath encounter downed the host. Native stats recorded -11/12. The real Journal Entry Added popup was dismissed through the native registered OK action and world movement subsequently continued. |
| Nearby rescue | The client walked from the station entry to the downed host. Agent logs at 07:50:37 report rescue at 3.3 units. Native death logs independently record revive, standing up and restoration to 12 HP. Further actual attacks caused repeated downs/rescues; both players subsequently walked to 188.4,150.6 at 12/12. |
| Full wipe | Both ultimately downed. Actual screenshots show stock main menus, while native frame hooks continued; this was not a black-screen/crash observation. |
| Host manual reload → client recovery | After the host manually loaded, the client automatically adopted/rejoined at 07:57:36–41 and completed a dedicated periodic save at 07:57:43. This first reload accidentally selected the existing Endar `000901 - Co-op` through a numeric console index, so it proves wipe recovery to that world, not Manaan return. |
| Correct named station reload | `loadgame staged_manaan_original` then selected the exact staged 000922 source. Host returned alive to Manaan 12/12. Client logged adoption of `manm28aa` (2,053,172 bytes, identity 0x98433219) at 07:59:38 and rejoined at 07:59:43. |
| Stock party GUI | Inventory/Character Sheet/Map opened through owned native input. Map Party Selection displayed “That function is unavailable at this time.” This restriction was not bypassed. |
| Later healthy checkpoint | Manual `save Co-op` requests were accepted at 08:00:22/23; request replies alone are not completion proof. The client later explicitly logged completed periodic dedicated capture at 08:07:45, with QuickSave untouched. Final save folders were preserved before restoration. |

The fixture is a weak level 1 Soldier with inherited Jedi Sentinel level 0, not a naturally progressed Manaan character. It had clothing, a short sword and two medpacs. No level-up, class change or medpac use was performed. A late `recruit0` attempt during world teardown timed out; a later successful developer recruitment happened in the accidentally loaded Endar world and was discarded by the subsequent station reload. Neither is evidence of normal Manaan companion recruitment.

Exact original NPC health before/after was not independently measured, so this report does not claim numerical NPC damage attribution. The actual own-PC damage, repeated native revive callbacks and subsequent movement are the bounded combat-recovery evidence.

## Remaining route and suitable next input

The run stopped before reaching the suit giver or original airlock. It therefore leaves original underwater parking, the harvester overload puzzle, station reunion, save and reconnect untested. A naturally progressed station checkpoint with sufficient combat equipment and legitimate party availability would be a better input than bypassing this fixture's story restriction. Static original-script route preparation remains in `artifacts/manaan-route-v95/stock-route.json`; it is preparation, not live proof.

## Evidence and restoration

Evidence is under `artifacts/manaan-route-v95`: original fixture hashes/census, corrected nine-save health census, command replies, screenshots, final save snapshots, complete Agent logs and preserved native death/dialogue/combat/door logs. Particularly useful images are `live/preflight.bmp`, `live/station-first-combat.bmp`, `live/host-after-popup.bmp`, `live/host-teardown.bmp`, `live/client-teardown.bmp` and `live/party-gui.bmp`.

The owned pair was stopped through the scoped harness. `preserve.py restore` compared all original saves/runtime/config/native files against their pre-run SHA256, size and modification timestamp and returned PASS for both private copies. Original QuickSave and character sidecars were restored as part of the complete save-directory snapshots. GPU capacity was then released to the independent bounded notification trace.
