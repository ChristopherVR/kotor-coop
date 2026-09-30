# Personal Leviathan locker adoption, 2026-10-01

A live staged Leviathan rescue check found that a saved-world reload could erase
unclaimed personal gear. The host looted all eight original locker entries through
Get Items. The joiner had a verified native Co-op capture with an empty backpack
and eight `lev40_ptylocker` entries. Adoption filtered the host's six personal
backpack entries, kept its two shared quest entries, and imported its emptied locker.
The joiner's remaining personal gear disappeared.

Evidence is under `artifacts/leviathan-final95`: `client-locker-before.json`,
`host-locker-after.json`, `client-after-reload-staged.json`, complete Agent logs and
preserved final save directories. The original inherited post-release fixture is
also preserved. The raw earlier periodic capture was subsequently overwritten by
adoption, so the before-state oracle is its parsed snapshot and recorded hashes;
this is a staged-fixture finding, not a natural full-campaign proof.

Commit `7d9b2bf` extends personal-inventory grafting to the same private locker
already exempt from the live container-loot relay. The initial fix covered matching active modules; the later archive extension below
also preserves unclaimed gear after leaving the prison. The joiner retains its own locker items with
all GFF fields, stacks, properties and upgrades. Shared quest entries already
supplied in the adopted backpack are excluded from the locker to prevent duplicate
returns. Other placeables and their fields use the host's current state. An empty
own locker remains empty. A first join cannot copy the host's personal locker gear.
Shared inventory mode retains its existing behavior.

The private locker and nested GIT must resolve unambiguously. Malformed or ambiguous
locker state refuses staging through the existing adoption error path. The initial patch
covered the same active Leviathan module; the archive extension below broadens that
transform. Neither certifies the original full capture/rescue route.

25 focused personal-inventory and world-adoption tests pass. They cover an upgraded
4100 rifle, stack size3, nested item properties, unchanged own capture, an unrelated
container, empty-own and first-join behavior, and the shared-mode byte-for-byte
regression. A warning-clean standard win-x64 Agent is published at
`artifacts/live-agent-v95-locker-x64`; all 5,519 fast tests pass, zero failures, 1m38s. Live replay remains pending.


The follow-up `0f45162` binds locker reads to the unchanged save bytes recorded
alongside the selected own-character sidecar: exact source directory, compound
SHA256 and length. It selects that recorded source rather than assuming the first
folder named Co-op is current. The graft checks the proof before and after reading
items. Missing, changed or ambiguous proof holds staging and preserves the own
source. Old sidecars need a fresh own capture before this private-locker recovery.
Follow-up `0e60de6` records the same proof on ordinary own-save captures, with a
content check before and after character extraction; direct unbound sidecars do not
prove inventory ownership. No native block or network layout changes were needed.

The combined controlled-host-load recovery and provenance changes pass 92 focused
tests and all 5,539 fast tests, zero failures, 1m43s (`0f45162`). The ordinary-capture
follow-up passes 32 focused persistence tests. Final full-tier validation and the
actual combined live replay remain in progress.


Final immutable `0e60de6` passes all 5,540 fast tests, zero failures, 1m39s;
its standard win-x64 Agent publish is warning-clean. Logs and source are under
`artifacts/companion-v95-recovery-final`. The final slow tier passes all 117 tests, zero failures, 9m30s. The first
combined live setup on `0f45162` reproduced the ordinary-save metadata gap directly
(null source fingerprint), confirming that replay must use the follow-up or a
confirmed dedicated capture before the interruption. It is not a recovery pass.


Commit `66c4580` also preserves the private locker in previously visited module
archives, even when both current saves name a different active area. Only the
exempt private container's item list is overlaid; the current area and all other
host world state stay authoritative. The same recorded-source proof is checked
around reads, and duplicate module or locker mappings hold staging. A regression
models both players leaving the prison while the joiner's gear remains unclaimed.
28 focused inventory/adoption tests and all 5,554 fast tests pass, zero failures,
1m45s, with a warning-clean standard x64 Agent publish. Evidence:
`artifacts/companion-v95-archived-locker`. Cross-area live proof remains pending.


The combined automatic host-load replay on 2026-10-01 completed the joiner's
native load in five seconds and retained all eight verified own locker entries,
with the joiner's own classes and effective 12 HP intact. The host's actual
locker retrieval separately produced eight backpack entries and an empty host
locker. The joiner's post-load received-items popup still requires a completed
movement/retrieval/reconnect check. Developer popup cleanup is being corrected
independently; it must allow the engine's normal deferred OK dismissal to finish.
See [the live recovery report](leviathan-recovery-live-2026-10-01.md) for exact
scope and saved-health evidence. This same-area result does not establish the
cross-area case above.


The subsequent `96eefe6` Native 95 replay identified and repaired the repeating
received-items notification. A hardware write watch showed native item acquisition;
a 1 ms bank sampler then recorded 44 busy publication samples in five seconds while
the companion identity stayed unchanged. The controller had discarded successful
equipment state whenever proof was temporarily unavailable. It now withholds writes
and preserves that state, resetting it for a verified source session, occupant
generation or current engine body change. A reused body ID must also resolve to the
same current engine pointer before the cache can be trusted.

The matching repaired replay caught 30 further busy samples without repeated item
creation. After the normal OK dismissal, 120 samples across 12 seconds stayed outside
the modal with input and busy flags clear. Normal forward input moved the joiner's
own character 8.14 metres. No position, health or quest alteration was needed.
Actual locker retrieval and later reconnect remain in progress. Twelve focused
cache/identity tests and all 5,560 fast tests pass, zero failures; the x86 native
publish is warning-clean. Evidence: `artifacts/companion-v95-proof-cache`,
`artifacts/companion-v95-proof-cache-validated` and the live recovery report.


The actual reconnect exposed a second, independent native equipment defect: the
joiner's clothes stack of three was present in the staged character, but the new
companion clothes item held four units after CreateItemOnObject merged the stacks.
Unlinking that item removed the player's entire clothing record. This was native
post-load loss, rather than a failure of the saved inventory graft.

Commits `2e91bf2` and `b9a6baa` create mirror equipment without that shared-repository
merge, using the engine's floor-create, immediate RemoveFromArea and synchronous
equip paths. The actual repeated retrieval/save/reconnect replay retained all eight
original item records with matching contents and stack sizes. Each shared quest
item appeared once, the private locker was empty, and the companion's clothes and
weapons each held one unit. The saved area contained zero floor-item rows. The scene
rendered normally after reconnect; one earlier container screenshot was transiently
black, followed by a normal registered container screen before retrieval. It is not
a persistent black-world result. All 5,568 fast tests pass, zero failures, 1m50s;
the immutable x86 native build is warning-clean. Evidence:
`artifacts/companion-v95-isolated-equipment-final` and the live recovery report.
Cross-area unclaimed-locker testing remains in progress.


The separate cross-area unclaimed-locker replay also passes. The verified own source
was a native Ebon Hawk save containing six still-unclaimed private locker records
in the inactive `lev_m40aa` archive and only the two quest items in the backpack.
The host's Ebon Hawk save retained an empty prison locker. After actual reconnect,
world adoption and native load, a fresh native save preserved the full locker item
list exactly, including all fields, with both quest items still present once.
The earlier collected eight-item backpack was not used as this locker source.
Evidence: `isolated-cross-area-proof.json` and exact before/after native saves under
`artifacts/leviathan-recovery-v95`; see the linked live recovery report. Both private
installs were stopped and all 56 protected originals restored with exact hashes and
modification times. This closes the archive/save/reconnect mapping checks. A full
natural capture, rescue and escape route remains unverified.
