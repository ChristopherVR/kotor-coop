# NPC and campaign mod support

Implemented 2026-09-27. Protocol 76, content digest scheme 2, shared-block layout 81.

## Framework

Version-controlled compatibility declarations live in `profiles/mods/*.json`, one
file per mod. Packaging and installation copy them to each game's `coop-mods/`
directory. Schema 2 detects installed content, merges active rules, supports numeric
story-global conditions and holds recovery when a companion identity is unknown.
See [the profile reference](../../profiles/mods/README.md) for the fields and rules.
The optional legacy `coop-mods.json` schema 1 remains accepted.

Native mod scripts still recruit companions normally. These rules select a UTC
only when the co-op roster reconciler needs to recover a companion missing on the
other instance. KOTOR's nine companion slots and two active seats are unchanged.
Additional playable appearances use the installed appearance table; this framework
does not add engine slots or implement temporary-PC campaign policies.

The Solomon profile is deliberately experimental. Its actual `ship6/add.nss` sets
`Tar_ZelkaRm=30` and recruits `p_channa` in slot 0, so that is the one declared
template mapping. Slots 0, 1 and 2 otherwise hold recovery, including in stock areas.
The mod reuses those slots for other characters without consistently changing a
story global. Explicit slot-0 holds in `bos_*`, `ship*` and `vision*` take precedence
even at phase 30; the declared recovery applies outside those campaign areas.
Guessing a scene's occupant from its module would recruit the wrong creature.
Holding also protects a roster update that arrives before its story-global update.
This conservative policy can delay automatic stock companion recovery in earlier
saves with the mod installed. Existing native-script recruitment still runs.

## Content consistency

`KotorCoop.Agent.exe mods --game-path <game> --compare-with <peer-game>` reports
active/inactive profiles, declared template availability and differing file paths.
It reads global resources and module archives for UTC presence; presence in one
archive does not prove availability in every module covered by a prefix.

Override and module files plus active compatibility declarations receive
full SHA-256 hashes. The wire carries compact digests over canonical paths and
hashes. Same-size edits are detected. Inactive profiles do not affect fingerprints.
Changed/unreadable inputs are reported unchecked; malformed profiles fail the audit.
Hashes run at startup, outside the frame/pump path. Restart agents after mod changes.

Known gameplay mismatches disable shared gameplay channels with `W_GAMEPLAY`;
avatar and independently checked appearance channels remain eligible. Loose
textures/models are classified separately. Save identity reads a replacement `.mod`
before its stock `.rim`. Since 2026-09-30, the manifest also hashes `chitin.key`,
every BIF referenced by that index, and nested movie, voice, sound, music and lip
files. Same-size, same-timestamp edits are detected. Base-archive and external-media
differences conservatively disable shared gameplay, including cosmetic changes;
startup auditing reads several GB. Mutable saves, logs and co-op runtime files are
excluded. See the current [manifest implementation](../../src/KotorCoop.Memory/ModContentManifest.cs)
and [15,336-file stock comparison](limitations-live-2026-09-30.md#dedicated-saves-and-compatibility-audit).
Matching files prove identical audited inputs, not successful campaign behavior.

## Isolation and qualification

Brotherhood of Shadow: Solomon's Revenge was installed from the
[author's distribution](https://deadlystream.com/files/file/761-brotherhood-of-shadow-solomons-revenge/)
in two full game copies under `artifacts/solomon-isolation`. The ordinary game
installations were not patched with campaign assets. The bundled post-Leviathan
save, custom NPC dialogue, forced module-entry dialogue and custom module transition
were exercised with two connected instances. See
[the test report](solomon-isolation-2026-09-27.md) for evidence and limitations.

## Remaining framework work

1. Capture actual recruitment templates and relay occupant identity with a generation
   number. A same-slot replacement can leave availability/active masks unchanged.
   Reset health/equipment caches when the occupant changes and recover identity on load.
2. Add explicit campaign policies for solo scenes, script-local state and temporary
   player-character substitutions. Template recovery cannot implement these behaviors.
3. Qualify full campaign playthroughs, including alternate branches, scripted combat,
   flashbacks, replacement companions, save adoption and reconnect.

Regression tests cover marker detection, active-only hashing, conflicts, strict field
validation, story conditions, stale-global holds, path validation, catalogue packaging,
legacy configurations, content mismatch gating and .mod save precedence. The final
fast suite passed 5,079 tests. Native x86 publishing and the agent build passed with
warnings as errors.
