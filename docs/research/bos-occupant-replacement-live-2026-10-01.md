# BoS same-slot companion replacement: bounded live evidence

Run on 2026-10-01 Brisbane time using immutable Native89 (SHA256 `19BA23D2F15EE55A0B6E246638B934ED29EF41004DB3889072A5280BABD64EC6`) and Agent wire82. Only the isolated Solomon host/client installs were used. The retained second rig is host42120/client8284, port35143. This is a controlled test of the distributed script, not completion of its natural dialogue or temporary-PC sequence.

## Verified prerequisite and inactive replacement

Both copies loaded the bundled Game375 save, then the host set `Tar_ZelkaRm=30` and recruited slot0 through the native engine. Both actual `gameinprogress/AVAILNPC0.utc` files identified Shadow, appearance519 and tag `bastila`. The serialized UTC omits TemplateResRef; the independently read native local occupant bank BC00 recorded `p_channa`, generation2. Both party tables had one active companion and a current typed creature at0x40CB.

The host called the real `RemovePartyMember(0)` routine through `removeparty0`. Both native party counts became0 and both packed active-party masks cleared. The retained spawned object ID stayed0x40CB, so availability alone was not used as evidence of inactivity.

Exact distributed `bos_orion.mod` resources were staged in both isolated Override directories: `orn_chara3.ncs`,75bytes, SHA256 `3a3e0948a715e052ba08827345818617f0a179b1008f3a5272ca0e61db5f7a5d`; `orn_fakechanna.utc`,4318bytes, SHA256 `5114dfc0374793dbd0a97ee0b59acd10b383f2f2ce19908489b7b035b77864f2`. The script consists of RemoveAvailableNPC0 followed by AddAvailableNPCByTemplate0,"orn_fakechanna".

The first dispatch after staging resources into an already running game acknowledged the RunScript call but changed neither UTC nor generation. That ACK does not prove execution. After a scoped restart with the same resources present at startup, the baseline was re-established and the host dispatched the unchanged script once. No replacement script was dispatched on the client.

Both actual available UTCs then identified Sera Degana, appearance511. Both local native banks changed generation2 ->5, template `p_channa` ->`orn_fakechanna`, with replacement flag1. The client's bound host bank also contained generation5/templateorn_fakechanna/replacement1. Its native log states: `party occupant: slot0 generation5 recruited exact 'orn_fakechanna' (inactive cache)` at00:43:43.490. Packed roster2147484159 was unchanged across replacement, proving detection was not inferred from changed availability/party masks.

Sixteen paired bank snapshots over10.059seconds retained generation5, the exact template, and inactive native party count0. There was no observed recruit/generation loop. This proves exact-template replacement of the client's inactive cache. It does not prove active companion replacement or saved-world recovery.

## Save qualification and omitted natural teardown

A subsequent host `save Co-op` exposed an important fixture qualification. Before saving, host and client available UTC reads reported Sera511. Saving serialized the retained host Shadow body back into AVAILNPC0: actual archived UTC name Shadow, appearance519,21578bytes, SHA256 `3abf73acdf4aa0e72757a47dd7f58cd967814484eefaa17534333bee459a326c`. The save archive SHA256 is `4db5c3c5f416def643eab85e12a26c4ab712bd745cca80204898b5d1cee08252`. The client still held Sera511,7809bytes, SHA256 `23839b136224c26a3d401392b47aa3071eab75b86d1436c06e21421d2f0ae4cb`. Captured native generation5/templateorn_fakechanna therefore does not, by itself, certify the host's later serialized payload.

Exact GOG disassembly corroborates the retained-body path. `RemoveNPC` (`0x5646D0`) clears only availability at table+slot*4+0x30. `GetNPCObject` (`0x564700`) returns the cached table+slot*4+0x0C ID when it is not OBJECT_INVALID. `SaveMember` (`0x563E80`) resolves that cached ID at0x563EBC/0x563EC4 and calls `SaveCreature` at0x563F7E without first proving it is the replacement occupant. The disassembly is preserved in the live artifact. These observations do not justify blindly destroying a placed story actor.

Read-only inspection of the original dialogue supplies a missing prerequisite in this controlled shortcut. Its actual sequence is Entry75(`orn_chara3`) ->Reply75 ->Entry76(`orn_prefade2`, pause/fade) ->Reply77(`orn_prefade`) ->Entry79(`orn_chara`). The last script switches player character to NPC_BASTILA and destroys the actor found by tag `bastila`; it also manipulates Orion scene actors, strips equipment and jumps to Orion locations. Running only orn_chara3 in the Ebon Hawk omitted that following teardown. The save result is consequently not certified as a natural mod bug. The full scene script was not run outside its intended area, and no placed Shadow actor was blindly destroyed.

Active replacement, the natural dialogue/temporary-PC sequence, and saved recovery remain unproven here. A transferred payload must reject a generation/template claim when the verified save serializes a different occupant.

## Evidence and retained ownership

Artifacts are under `artifacts/bos-occupant-live-v89`: `restart-shadow-*.json`, `restart-replacement-*.json`, `quiet10.json`, `post-save-*.json`, `post-save-archive.json`, and actual available UTC copies. The primary native logs remain in the isolated installs and the scoped agent logs are in `restart-rig`. Read-only source/resource inventory and original backups are also preserved under `artifacts/bos-occupant-plan-v89`.

The first and second rigs were stopped through their own manifests. Before stopping the second pair, its changed saves, gameinprogress and logs were preserved in `pre-crafted-evidence`. Pre-run native/config/saves/gameinprogress copies are in `bos-occupant-live-v89/backup-host` and `backup-client`; both staged Override paths were originally absent. Cleanup must remain scoped to these two installs and restore those copies after the follow-up. No original game install was changed.

## Crafted coherent Sera host preflight

A separate, explicitly labelled private fixture tests actual saved-payload loading without pretending the omitted natural route ran. It copies original Game375 and substitutes the real captured client Sera UTC. All 54 archive resource payloads were compared; only AVAILNPC0 changed. The final fixture retains the original party table, unavailable slot0, empty active members and original globals (`Tar_ZelkaRm=99`). Its NFO name labels it crafted. The fixture archive SHA256 is `86052ba2dcbf41ac22e95b07ff21e4b4ee28dc8e70d8dcacf23f0f1a2139a41b`.

Two preceding bounded attempts explain the final prerequisites. A fixture with phase30 and available slot0 automatically entered `bos_ebovis` and made Sera the controlled character on load; it was preserved and excluded from this inactive-slot preflight. Retaining phase99 but making slot0 available caused the Ebon Hawk on-enter logic to spawn a current-table Sera511 body while party count remained0. This was real Sera loading, but its captured template generation was unknown0. Both attempts were scoped stopped and archived, without destroying any actor.

The final fixture loaded in the actual engine with slot0 cached ID OBJECT_INVALID, party count0, Ebon Hawk module and the original player character. The host then dispatched the unchanged `orn_chara3`, establishing generation2/template `orn_fakechanna`, followed by normal native `recruit 0`. It spawned slot0 as0x40C9 and added it to the party. Independent current-object-table probes resolved the host and client bodies and read native server appearance511 at CSWSCreature+0xA60. This is first admission from an unavailable slot, replacement flag0; it is not proof of active replacement.

At 2026-09-30T15:21:56Z (2026-10-01 01:21:56 Brisbane), host `save Co-op` requested dedicated slot377 through SendSaveGameRequest, received native success, and completed its normal phantom save flush. The archive remained unchanged across an850ms stability check. Independent extraction identified Sera Degana, appearance511,8929bytes, SHA256 `022acb98783b278929877a692c29b1dded5300ad033b0ecf0c714c313a888644`; external party metadata contained one active member, slot0. The real engine reserialized Sera, so this hash differs from the pre-load captured UTC. The coherent host save archive SHA256 is `06db2816d7f877885bec01caddccf5a9a75fbdf0739307f7b0153568d8277d07`.

The copied coherent save is `artifacts/bos-occupant-live-v89/coherent-sera-host-save`, with independent `crafted-host-save-proof.json`, native body snapshots and final fixture manifest. The retained rig for the next authorized version90 test is host52952/client48688, port35146, `crafted-phase99-unavailable-rig`. Active saved replacement and the natural BoS scene remain unproven. The earlier stale Shadow archive and all original backups remain preserved.

