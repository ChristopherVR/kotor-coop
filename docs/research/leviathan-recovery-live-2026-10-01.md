# Leviathan controlled-scene recovery and personal locker

These tests use private game4/game5 copies and inherited stock saves. They do not establish a natural campaign capture or the complete rescue combat route. The inherited Ebon Hawk prerequisite has Trask in slot0 and no Bastila; the approved Prison entry is an explicit staged warp, followed by the original `lev40_saul403` dialogue and its genuine Canderous control transition. No campaign globals, player health, AI targets or controlled-slot fields were changed by the test.

## Original failure

Evidence is retained under `artifacts/leviathan-final95`. The original interrogation reached host controlled slot1/Canderous, while the joiner's own PC remained alive and command-locked. Two seconds of genuine native W input moved the joiner exactly zero distance. An inherited weak, unarmed rescuer subsequently died during real technician combat; this is not a successful complete escape.

A genuine host load of inherited `000036 - lev_gate_host` left the joiner in a black rendered scene. Automatic host-reload adoption waited for a fresh character save that the abandoned local scene could not produce, then timed out after90seconds. A real selected client load recovered the world. Subsequent adoption also replaced the joiner's eight-item private equipment locker with the host's empty locker after the host used the real GetItems GUI. The joiner received the two shared quest items but lost the six private locker items. Those two defects motivated a controlled-abort checkpoint path and the personal-mode locker graft.

## Final agent replay and saved-health finding

`artifacts/leviathan-recovery-v95/rig-final` used Native95 default-localvar DLL SHA256 `7A32C12B418AA591DBDD9612541AB7EC4AA072CD2A8CF125659F065C1CDE9B73` and x64 Agent from `artifacts/live-agent-v95-recovery-final`, source0e60de6. Host PID64440 and client PID43516 were exclusively owned. A preceding client-only agent update during an active conversation produced a save mismatch and was discarded as a clean-test oracle; its evidence remains separate.

The clean pair's pre-scene native Co-op save recorded a content-bound source proof, independently matching the preserved archive's hash and length. Original interrogation again reached host F0=1/Solo1, with client F0=-1, native HP12, Commandable0, InputClass0 and both GUI busy fields0. Actual W down/up over two seconds again moved the client zero distance.

At20:42:55Z, a fresh real client Co-op save captured the stripped original PC and all eight private locker items, inventory0. Its metadata bound exact `game5/saves/000901 - Co-op`, compound SHA256 `CBDD0A31D8B37E72D28C34838E50B108CEED1CE5F6F997A5ED1BDE6EF7730237`, length2285555. The raw archive, sidecar and parsed semantic inventory are preserved as `client-stripped-verified-save`, `client-stripped-character.*` and `client-stripped-verified-state.json`.

The subsequent genuine host load selected the new checkpoint-recovery branch but refused the checkpoint as supposedly dead. This was a bug in the checkpoint health test, which treated the saved `CurrentHitPoints` field as absolute native HP.

The exact GOG `CSWSCreatureStats::SaveStats` instructions at0x5B1FE2..0x5B2026 store:

```
CurrentHitPoints = stored HitPoints - derived GetMaxHitPoints(1) + GetCurrentHitPoints(1)
```

`MaxHitPoints` separately stores that derived maximum at0x5B2030..0x5B2041. The player load branch at0x5B130D..0x5B1335 reverses the offset before calling `SetCurrentHitPoints`. Literal labels were verified from executable addresses0x74ADE0 (`HitPoints`),0x74ADCC (`CurrentHitPoints`) and0x74AF98 (`MaxHitPoints`). Disassembly is retained in `saved-health-serializer.txt` and `saved-health-loader.txt`.

Both actual pre-scene and stripped client saves have stored HitPoints0, CurrentHitPoints0, MaxHitPoints12, matching live native HP12. The inherited post-release save has CurrentHitPoints-10/HitPoints0/MaxHitPoints12 and genuinely loads at native HP2. Saved base health must therefore be decoded as `CurrentHitPoints - HitPoints + MaxHitPoints` for this checkpoint decision. The repair changes no saved health fields. It conservatively refuses missing fields, out-of-short-range values, negative stored maximum, nonpositive derived maximum, nonpositive effective health and effective health above the derived maximum.

The repaired oracle and actual dispatcher checkpoint fixtures passed47 focused tests. A fresh live replay on the repaired binary is still required before certifying interrupted-scene recovery or locker preservation.

Both discarded runs were scoped-stopped, and all56 original private files were restored with exact SHA256 and modification ticks. Original QuickSave directories were empty in this scope; this is not a six-file QuickSave preservation proof.

## Corrected health agent: automatic recovery proven

The third clean pair used Agent `artifacts/live-agent-v95-health` (d877221), unchanged Native95 default-localvar DLL, host PID38344 and client PID59636, port35163. It again entered through the original staged Prison interrogation and genuine Canderous control transition. Client Commandable0/F0=-1 and two seconds of real W produced distance0. A fresh real client save at20:57:12Z retained eight locker items and inventory0, with content-bound source SHA256 `738BD4AE3A98FFBB53B1F3332A14EEAD4EA2C75E07587A1FD44E3341F5CB4209`, length2156040. Raw files, character sidecar and item semantics remain in `client-health-stripped-verified-save` and related artifacts.

The genuine host post-release load was detected at20:57:35Z. The client logged both the verified living checkpoint selection and the controlled-scene interruption path, adopted a save retaining its own eight private-locker entries, then completed the actual native load at20:57:40Z. Post-load native own PC0x7FFFFFFF was alive at12HP, F0=-1 and Commandable1, retaining classes0@1/5@0. The named screenshot `lev_health_clientreturn.bmp` shows a rendered normal world and original character. The original black scene and90-second checkpoint timeout did not recur.

The actual host GetItems GUI then emptied the host locker into eight backpack rows, independently verified in the host's completed Co-op archive. Before any client retrieval, native party-pool observations still showed zero client copies of the two quest items. Attempts to instantiate `dan13_plotcrys` and `man28_envirosuit` returned native OBJECT_INVALID (GiveItemToInventory failure8: unavailable blueprint). Their full saved item GFFs remain in the client's own locker. These observations do not prove successful live quest-template replication or duplicated quest items.

The client's post-load Status Summary (`Item(s) received`) remained modal, blocking movement and new saves. Read-only native inspection verified that the current top modal was exactly GUI+A8, vtable0x0074FF68, count1. Synthetic OK clicks, Enter and Escape did not dismiss it; this does not establish a physical human-input defect. The existing explicit closegui action also refused its plain HideSWInGameGui teardown. A dev-only correction selects the already verified native Status Summary OK path only when that exact typed panel is currently registered at the modal-stack top. Movement, real client GetItems and repeated reconnect checks await that rebuilt test action.
