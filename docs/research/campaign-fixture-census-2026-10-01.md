# Remaining stock campaign fixture census (2026-10-01)

Read-only parsing of all 127 save directories in the original GOG, P2 and P3 saves folders found no better preserved natural swoop or Star Forge finale entry. All127 parsed; `artifacts/campaign-route-census-v95/saves.json` records exact paths, active modules, SAVEGAME SHA256, selected globals, and relevant actors in every nested saved area. The originals were opened for reading only.

* `000005 - starforge` is active `sta_m45ac` with Malak-tag creatures (`sta45c_malak`, `sta_45darthmalak`), HP 114 and empty conversations, but no saved Bastila creature. Later campaign saves that contain this area inherit the same insufficient actor state. Actors in a visited nested area do not establish the active story route or fresh finale prerequisites.
* `000003 - zzz_swoop` is active `END_M01AA`, not a race entry. `Tar_SwoopEng=1`, but `Tar_SwoopNew` and `TAR_FIRST_SWOOP_USE` are zero. The name does not establish natural swoop admission.
* `000035 - dark_handoff_ready` has actual `bastilae` with `unk44_evilbast` conversation in `unk_m44ac`. This is a temple handoff fixture, not Bastila or Malak on the Star Forge.
* `STUNT_57` and `stunt_56a` saves contain ending/transition scenes and cannot prove the preceding fight.

The independently progressing natural Endar/Taris route remains the best route to genuine first swoop admission and its later Jedi-training prerequisites. Star Forge fight coverage still requires a verified natural or explicitly staged prerequisite state; this census does not create it.

The separate Manaan census is under `artifacts/manaan-route-v95`: four available station/seafloor fixtures have incomplete story/puzzle globals, so a full original-script underwater walk starting there must be described as staged save entry. Stock resources identify suit OnUsed, airlock conversations, the second-station survivor/pressure dialogue, Rift harvester choices and original door return scripts. No Manaan live run is claimed by this census.

## Manaan saved-player health interpretation

The raw `CurrentHitPoints` field alone is not a death oracle. Verified GOG `SaveStats` instructions at `0x5B1FE2..0x5B2026` serialize CurrentHitPoints as stored maximum minus virtual derived maximum plus virtual current HP; MaxHitPoints is the derived maximum and HitPoints the stored maximum. For this verified encoding, effective saved HP is **CurrentHitPoints - HitPoints + MaxHitPoints**. Earlier preparation incorrectly treated raw zero as a dead player; that interpretation is withdrawn.

All nine original active Manaan save payloads have stored HitPoints 0 and MaxHitPoints 12. Eight have CurrentHitPoints 0 and therefore effective saved HP 12: `manaan station`, `campaign_seafloor`, `campaign_manaan_aligned`, `campaign_manaan_return_ready`, `campaign_return_restored`, `campaign_return_peer_save`, `campaign_return_peer_flush`, and `campaign_sep23_return_host`. `campaign_return_fixed_host` has CurrentHitPoints -11 and effective saved HP 1. The complete three-field oracle and exact fixture paths are recorded in `artifacts/manaan-route-v95/player-health-census.txt`.

This is a serializer-qualified offline result, not an observation of their current native loaded health. The upcoming private preflight must still read the actual loaded player and engine readiness. In particular, the closest station fixture `campaign_manaan_return_ready` must not be rejected solely for its raw zero field.
