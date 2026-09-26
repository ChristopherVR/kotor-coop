# Remote lightsaber combat visual, 2026-09-27

The trailer showed the joined player's yellow saber on their own screen but
missing/unlit on the host screen during the Dantooine kath hound fight.
Equipment delivery was present: the phantom's equipped main-hand object was
attached to its client twin, and its weapon-hide flag was zero. The difference
was the client twin's combat visual state at `+0x138`: the local fighter had 1,
while the remote phantom had 0.

The phantom replays its owner's animations but has no local native attack round.
Native client updates can therefore stand down its weapon between replays.
Replaying a swing does not itself keep a saber powered.

## Binding and change

`CSWCCreature::SetCombatState(int)` at `0x006182A0` is the client visual setter.
Disassembly of the supported retail executable reads `twin+0x138` at `0x6182AB`,
writes it at `0x6182B1`, then updates both equipped client items' power states.
It returns with `ret 4` at `0x6183B7` / `0x6183C7`. It is not the server creature
combat setter and does not start an attack round or generate damage.

`Odyssey.MirrorWeaponCombatVisual` validates the client twin vtable `0x0074F5F8`
and animation base before calling that setter when the desired state differs.
`PhantomController.Drive` reconciles this visual state while the peer reports
combat and once on exit. It does not continuously force idle outside combat,
which would interfere with a voluntary weapon flourish.

## Verification

- Native x86 plugin build passed with `-WarnAsError`.
- Focused regression selection (`Phantom|Combat|Animation`) passed all 131 tests.
  The initial whole-suite attempt was interrupted after several minutes without
  a result; it is not counted as a full-suite pass.
- Installed the built DLL into both disposable recording copies and verified
  their hashes against the build output; original game installs were untouched.
- Repeated explicit kath hound attacks. The yellow saber is visible in the host
  view and the owner's view throughout sampled attack swings. The checked-in
  `media/trailer/footage/kath-hounds-fixed.mp4` preserves the complete 18-second
  paired take; samples at 2, 6, 10 and 14 seconds were visually inspected.
- After stopping attacks and moving both players away, both remote client
  visual-state values read 0. The fix does not leave the weapon forced into its
  combat state after the peer exits combat.

This fixes the reproduced saber power/render-state mismatch. It does not claim
frame-exact synchronization of all remote body animation timing. The older
[v5 investigation](../../tools/trailer/combat-investigation-v5.md) records those
separate observations and the earlier recording limitations.
