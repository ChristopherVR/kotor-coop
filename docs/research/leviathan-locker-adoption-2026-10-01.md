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
already exempt from the live container-loot relay. When the own capture and staged
save select the same active module, the joiner retains its own locker items with
all GFF fields, stacks, properties and upgrades. Shared quest entries already
supplied in the adopted backpack are excluded from the locker to prevent duplicate
returns. Other placeables and their fields use the host's current state. An empty
own locker remains empty. A first join cannot copy the host's personal locker gear.
Shared inventory mode retains its existing behavior.

The private locker and nested GIT must resolve unambiguously. Malformed or ambiguous
locker state refuses staging through the existing adoption error path. This patch
covers adoption in the same active Leviathan module; it does not establish recovery
of locker contents from an earlier module or the original full capture/rescue route.

25 focused personal-inventory and world-adoption tests pass. They cover an upgraded
4100 rifle, stack size3, nested item properties, unchanged own capture, an unrelated
container, empty-own and first-join behavior, and the shared-mode byte-for-byte
regression. A warning-clean standard win-x64 Agent is published at
`artifacts/live-agent-v95-locker-x64`; all 5,519 fast tests pass, zero failures, 1m38s. Live replay remains pending.
