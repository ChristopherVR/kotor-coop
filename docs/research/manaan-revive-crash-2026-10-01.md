# Manaan queued effect-removal crash, 1 October 2026

## Actual failures

The first underwater route attempt used immutable Native 96 `7cf5706` (DLL SHA256
`843148E23B21340F612CB393186D589AAB7CEF361438797FBD100C83C2CA6CB8`), matching
Agent 96/wire 85, and private game 4/5. The entry was an untouched copy of the existing
`000009 - campaign_seafloor` save. This is a staged saved entry, not a natural campaign
or suit-acquisition proof.

Host 72028 activated the original `28bw` airlock through `usedoor`, and the engine
returned from `manm28ab` to `manm28aa`. The rendered station contained hostile
Selkath. Its level-one PC had effective 12 HP; genuine NPC attacks repeatedly downed it,
and the nearby peer triggered real native resurrection. The last recorded rescues
were 09:14:50.636, 09:14:52.991 and09:14:55.388. At 09:14:56.394 the host recorded
AV at `0x4FED74`; it exited with `C0000005` at 09:15:02.637. No measured full
underwater trip had begun. No HP/class/global/position edits, forced targeting,
template injection or route-skipping warp were used.

The crash record and full logs/saves are preserved under
`artifacts/manaan-underwater-v96/first-return-crash`; all 56 original owned files were
restored with exact SHA256, size and mtime (`first-return-restoration.json`). An
independent Native 96 NPC encounter also failed at the same instruction at 09:25:42:
`artifacts/newgame-gui-live-v96/host-crash.bin` and `host-crash-decoded.txt`.

## Verified native lifetime and repair scope

`CSWSCreature::EventHandler` at `0x4FECE0` handles event 14 by reading the two DWORD
effect identifier from its borrowed payload pointer. The faulting instruction
`0x4FED74` reads payload+4; the first crash's payload `0x48E62F70` was unreadable.
The following call to `0x4D06C0` removes the matching effect, including its destructor
and allocator free. Native event creators pass the actual effect pointer, not an
owned identifier copy. `DeleteEventData(14,...)` is the default no-op branch;
`EventHandler(14,null)` is also explicitly a no-op.

Native `OnApplyResurrection` at `0x4E13C0` immediately removes type 57 effects whose
subtype low three bits are 1. It restores positive HP before that removal. Our
`ReviveCreature` invokes this via an instant VM effect inside the plugin frame,
whereas normal creature-update resurrection follows the AI event drain. A pending
event that still borrows one of those freed effects explains the demonstrated
fault shape. A live matching queue/effect witness remains necessary to confirm this
specific timing as the cause in the failing encounter.

The repair snapshots only queued event 14 entries targeting the exact current typed
revival body whose payload equals one of those type 57/subtype1 effects. After the VM
apply succeeds, it walks the current queue again: no cached node or record is
dereferenced. Same world/current resolved body, exact event record/day/time/sender/
target/type/payload and absence of both the original effect pointer and identifier
are required before setting that redundant event's payload to null. It allocates or
frees no native event data. Foreign, persistent, surviving or changed effects/events
remain untouched. Companion resurrection uses the same fence.

The fence validates the actual native instruction bytes for the effect-list fields,
type/subtype/removal call, event record fields, null-handler branch and no-op
destructor branch. Enumeration is bounded to 1024 effects, 4096 queue nodes and 64
matching callbacks. Invalid/unreadable layouts or exceeded bounds refuse the native
revival (failure 10/11) rather than continuing without the fence. This is a
conservative refusal, not a general stale-event suppression mechanism.

## Validation status

Nine focused rule tests passed, including every-byte mutation of the native layout
contracts, event identity changes, foreign target, unsuccessful resurrection and
surviving-effect refusal. Managed native Release/x86 compilation passed with
warnings treated as errors. Immutable AOT publication and the genuine repeated-NPC
rescue replay are pending. No crash repair, complete underwater route or campaign
compatibility is certified by these unit tests.

### First repair replay exposed an incomplete queue walk

The immutable first fence (`cea2f03`, native SHA
`99AB48E1AEEBBB61D8EDA35C36C7276C8377B1F57A6452CA3B67F064CC246FA2`)
passed 5,589 fast tests and warning-clean AOT publication. Its original airlock/NPC
replay on host21048/client50616 still failed at `0x4FED74` at09:44:38.449, after
many genuine nearby-peer rescues; it produced no matching captured/retired event
witness. It is **not a proven repair**. Exact failure artifacts and all owned files
are retained under `artifacts/manaan-underwater-v96/revive-events-head-only-failure`;
all56 originals were restored exactly.

Fresh disassembly of native `GetHead` at `0x5E9C80` proves that node+0 is the
previous link and node+4 is the next link. The first implementation mistakenly
followed+0 and only inspected the queue head. The correction follows+4 in both
walks, validates the exact GetHead instructions for next-link/data/head writes, and
uses one shared bounded walker in the production pre/post paths. Regression tests
exercise a foreign head followed by a matching second event, a cyclic queue and
an unreadable second node. Twelve focused tests and warning-clean managed native
compilation passed. A fresh immutable corrected build and matching live witness
are still pending.

### Corrected traversal still fails; actual effect lifetime is unresolved

The corrected immutable `9e1e957` native DLL (SHA
`807298C2CD3B2D14EADD0F381B47DB633A2F9CCD9DB745B02C1C86A90376B122`)
passed 5,592 fast tests and warning-clean AOT publication. The new private4/5
replay, host56464/client49796, again failed at `0x4FED74`, at09:52:01.044.
There were no captured/retired type57 event witnesses. The host's sampled native
revive serial/applied counters progressed through0,1,2,3; its short downed windows
were visible in native death/NPC logs, not the500ms sampled DownedFlag. The client
remained alive; its binary crash file contains a Windows thread-name notification,
not a fatal crash. Artifacts are retained under
`artifacts/manaan-underwater-v96/revive-events-forward-failure`, and all56 owned
original files were again restored exactly. The type57 fence is **not a repair of
the demonstrated crash**.

Further native inspection identifies event14 creation at `0x4DB7E6` as part of
`OnApplyPoison` (`0x4DB320`, type35). It queues the incoming poison effect when
the target is plot/dead/temp-dead/already poisoned. This is one candidate producer,
not an established cause. `AddEventDeltaTime` can also free its payload on a
rejected time addition. The engine dequeues and frees the event record before
dispatch, so a queue snapshot cannot observe an already-dispatching payload.

The next diagnostic is default OFF (`revive_effect_probe=1` only in an owned test
configuration). It reuses the byte-guarded delayed-event splices, preserves flags
and registers, and adds a guarded effect-destructor observer. Fixed records contain
the actual enqueue caller/target/source/payload/UID, destructor caller for a matching
payload+UID, dispatch/discard resolved body and readable payload, and pre/post
resurrection type35/57 effects and all queued event14 targets. Effect subtype,
creator and child links are recorded as guarded observations. Callbacks allocate
no records, call no engine functions, perform no file I/O and change no native
game state; the frame hook drains the bounded ring. Record kinds1/2/3/4 mean
enqueue/destructor/due/discard; kinds5/6 are current effects before/after revival,
and7/8 are queued events before/after revival. The diagnostic does not establish
ownership or implement a general effect-identifier copy until actual evidence
shows the creator and freeing path.

The diagnostic keeps the latest captured enqueue sequence/fingerprint for each
payload address. Dispatch/discard use that captured fingerprint rather than reading
the payload again; a matching destructor marks it freed before the engine frees it.
Known-freed current/queue rows also use the captured fingerprint. `pagereadable`
is only an OS memory-region query and does not mean the allocation is alive;
`fingerprintreadable` describes the captured snapshot. Unknown payload snapshots
are explicitly uncaptured. All ring/history access is restricted to the native
install thread; foreign-thread callbacks are ignored. No callback dereferences
a payload already witnessed as freed. Ninety focused configuration/context/fence
tests passed, including flag preservation and original ESP adjustment/overflow
refusal, and the managed native build remains warning-clean.
