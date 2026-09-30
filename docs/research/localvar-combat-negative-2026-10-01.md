# Local-variable combat exclusion live check

**Result: PASS for a controlled stock-AI fight**, using the immutable ABI 95 final save-guard
plugin and Agent `e69d7bc` worldless build. This does not prove every arbitrary campaign
script is safe to share. `local_var_sync` remains off by default.

The isolated two-player rig enabled `local_var_sync=1` on both installs. Both native status
words were 3 (enabled and capture hook installed), in `tar_m10aa`, native epoch 2 and load
serial 0. Read-only snapshots resolved each object through the current native object array,
checked its creature vtable and ID, and fenced module, epoch, load serial and capture cursor.

## Positive controls used the actual VM hook

The original 799-byte `k_act_talktrue` script (SHA-256
`ea2306dfd07ab0369bae23896fae68a64ac3deb1261dd900679e483e3691e647`) contains
`SetLocalBoolean(OBJECT_SELF, 10, 1)`. The developer `runscript` command executed this
unaltered stock script on verified current watched NPCs, rather than calling the console
`localvar set` helper, which bypasses the VM capture splice.

Before combat, `bartenderdroid` object `0x17` had b10=0 on both instances. The stock script
set it to 1; the host captured entry 1, and the client logged native apply 0→1. After combat,
the same stock script on `pooldroid` object `0x44` produced entry 2 and another client apply
0→1. The client's capture cursor remained 0: applying received values did not echo them.

## Combat negative

The current stock creatures `tar10_vulkarpt01` (`0x2`) and `vulkarpool` (`0x38`) retained
their original AI callbacks. Their `k_def_combend01`, `k_def_attacked01` and
`k_def_heartbt01` wrappers execute `k_ai_master`; the 92,832-byte master script has SHA-256
`e8c30d6b06af22778a420aa35436bf585fb942f875991f2f10f3b2c02aafa881`.
Static inspection found real local setters, including excluded b29/b41/b42/b59/b65/b67
and n0/n3/n4. Its bounded abstract interpretation exhausted its budget, so this is not a
complete audit of every path through that script.

Initial NPC-to-NPC attack orders changed `0x38` b65 from 0 to 1 and n3/n4 from 0 to 2, but
did not establish successful hits. Those orders alone were not accepted as the fight proof.
The follow-up ordered the actual NPC onto the host PC and the host PC onto that NPC.
Native snapshots then showed target `0x7FFFFFFF`, NPC HP 22→15→14→13 and host HP 6→1.
No raw HP, class, global or AI-variable writes were used, and no creature scripts were blanked.

For about 110 seconds of this controlled fight, the host capture cursor stayed at 1.
The NPC's real AI number slots changed repeatedly: n3 reached 8 and n4 cycled through
2/3/4. The ring contained only the first b10 positive control. The later positive control
then advanced it to 2; both committed entries were exactly the two watched NPC b10 values.
There was no captured excluded Boolean or Number key.

The client's local table stayed unchanged during the initial short sampling window, then
its own AI independently set b65/b69. This is expected per-engine behavior, not a requirement
that all local tables remain frozen. The instances still disagreed on host-private values
(host n3=8 versus client n3=2; host b29=1 versus client b29=0), while both positive story
flags had reached the client. Therefore the oracle is actual excluded native mutation with
no capture, bracketed by functioning VM positives; it is not blanket table equality.

## Evidence and cleanup

Artifacts are under `artifacts/localvar-combat-negative-v95`: `proof.json`, the read-only
`snapshot.ps1`, original resource analyses/hashes, coherent before/after and eight fight
snapshots, native combat-target/HP probes, command replies, and both complete native/Agent
logs. Only owned PID/start-time pairs were stopped. Original configurations and native DLLs
were restored; other campaign rigs were untouched. Normal disposable save changes remain.
