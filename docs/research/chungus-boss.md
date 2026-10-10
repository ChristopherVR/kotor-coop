# Chungus: the arena boss cheat

Status: built 2026-10-10. Unit-tested and the model file checked by two independent readers.
**Not yet run in the game**: every item in the NEEDS-LIVE list at the end is open. Tags as in the
other research docs: **VERIFIED** = read from game files, the binary or repo code here;
**INFERRED** = deduced; **NEEDS-LIVE** = must be tried in the game.

`chungus` takes every player in the session to the PvP arena module to fight one boss together: a
very fat grey-and-white rabbit wearing a jewelled golden gauntlet. He has his own model, texture,
animations and sounds, all made for this mod. He swings like any stock creature and, every few
seconds, uses the gauntlet. When he is beaten, or has beaten everyone, or somebody types `yield`,
everyone is taken back to where they stood, as they were. Nothing is kept.

## 1. What the player sees

| Step | What happens |
| --- | --- |
| `chungus` | The host can always start it. A joiner can when the host has `pvp = arena`. Refused with a plain line if a duel is on, or if any player is in a conversation, in another area than the host, or does not have the boss installed |
| Way in | The arena round's own single warp: the host's game changes module once, everyone follows |
| Countdown | 5 s once everyone has arrived. The party stands in a row across the east end. Everyone is pinned at 1 hit point minimum from here: nobody can die |
| Fight | He appears at the west end. Stock melee AI plus one gauntlet power every 7 to 10 s (5 to 7 once he is below a quarter health) |
| End | He dies (win), every player is at the 1 hp floor together for 2.5 s (loss), `yield` from anyone (called off), the host leaves the arena, or 10 minutes pass |
| Way back | One warp home, each player put back on their spot with the hit points and Force points they came with |

The powers (`BossPower`, `BossPowers`, `ChungusRule.Landing`):

| Power | Aimed at | Lands after | What it does to a player | His animation | Sounds |
| --- | --- | --- | --- | --- | --- |
| Snap | everyone | 1.3 s | half of current hit points, thrown down | `ctaunt` | charge, snap |
| Power stone | one | 0.9 s | 30% of maximum hit points, thrown back | `castout1` | charge, blast |
| Space stone | everyone | 0.7 s | lifted and spun for 4 s | `castout3` | warp |
| Reality stone | one | 0.9 s | turned into a gizka for 12 s, and slowed | `castout2` | charge, slam |
| Mind stone | everyone | 0.7 s | frightened for 5 s | `cvictory` | laugh |
| Soul stone | one | 1.0 s | 15% of maximum hit points and choked for 3 s; he heals a twelfth of his health | `powerup` | charge, warp |
| Time stone | everyone | 0.7 s | slowed to half speed for 8 s | `castout3` | warp |

The snap is kept for when it matters: the first time he drops to 60% health and again at 25%.

## 2. How it is put together

### The round (host, `BossRound`)

A second round object beside `PvpDuelRound`, not a mode of it: the duel round is keyed on exactly
two players everywhere. `CoopHost` holds both; whichever is active is what `PvpState` and
`PvpStatePacket` carry. The boss round reuses the arena phases (`Entering`, `Countdown`, `Live`,
`Leaving`) and constants, so everything keyed on `PvpView.ArenaActive` works unchanged: `ArenaFlow`
(the one warp each way, origin memory, restore), save blocking (dispatcher and plugin hook), the
rescue of a host left in the arena.

- `PvpView.Boss` / `PvpStatePacket.Boss`: bit 1 of the trailing byte that already carried `Arena`.
  A build without the boss reads the byte as "not 1" and sees a plain duel between the host and
  nobody, so it follows the host's warp and is otherwise untouched. `Challenger` is the host,
  `Target` is 0.
- `PvpView.IsFighting` is true for every peer in a boss round, which is what pins everyone and
  keeps the campaign's wipe vote and menu protection out of it.
- `PvpPumpRule.Levels` gives a boss round level 1 (pinned) for the countdown **and** the fight,
  never level 2. Level 2 is what makes a duellist's stand-in hostile and makes `PhantomHitHook`
  drop every hit on a stand-in that the local player did not land; in a boss fight his hits on
  the joiners' stand-ins have to count. It also ignores the `pvp` setting.
- Round ids start at `0x40000001` so a boss round and a duel round never share a number.
- Notices addressed to peer 0 are for everyone. That is new; the duel's are always for one peer.
- Readiness: a game says it can load the boss with bit 0 (`PvpAwareBits.Boss`) of the otherwise
  unused target word of its `Aware`/`AwareArena` packet. That handshake is now sent whenever the
  game has the arena, whatever `pvp` is, so the host can start the fight with `pvp = off`.

### Powers (every game plays its own)

The host decides (`BossBrain`, pure, seeded by the round id) and says so with a
`PvpNoticeCode.BossPower` notice whose `Other` word is a packed `BossCue` (power, strength, target
peer). Every game, the host's included, gets it twice over: as a line of text, and through
`DrainBossCues` into `ChungusPump`, which asks its own plugin for two things through the cheat
mailbox (`CheatKinds.Chungus` = 112): the wind-up now (his animation on this game's copy of him,
and a sound), and the landing after `BossPowers.LandDelayMs` (a sound, and the result on this
game's **own** player if the cue hits it).

Why each game hurts its own player: the alternative is the host applying effects to a joiner's
stand-in and trusting the forwarding channels, several of which have never run for a hostile
creator, one of which (disguise) is off by default on the receiver, and all of which are capped
and scaled for ordinary combat. A player's own game applying a known effect to its own character
is the path with nothing in between. The pin means a wrong number cannot kill anyone.

The agent keeps the time, not the plugin: two requests through the one mailbox, the second sent
when it is due and the first has been answered. The mailbox has a single slot shared with the
human console; a typed cheat in the same instant can overwrite one of these (accepted, as for the
arena's teleports).

### The boss himself (host)

`ChungusPump` on the host, when the round goes live and the host stands in the arena: one spawn
request (retried up to five times), then it reads the id out of the plugin's answer
(`boss 0x...`), watches him in `ReadEnemies` twice a second (health for the brain; dead, or gone
for 3 s after being seen, means down), holds 3 s so the fall is seen, and calls
`ReportBossDown`. On `Leaving` it asks for him to be destroyed before the warp, so neither he nor
his corpse is written into the arena module's saved state.

The spawn goes through the routine table (`viaScriptTable: true`), so `ScriptCreateObjectHook`
names his blueprint to every joiner and their copy is built from the same file (the host-spawns-only
path, VERIFIED in code). A joiner's copy has its scripts blanked like every mirrored enemy.

### Plugin additions

- `Odyssey.PlaySound(name)`: `CExoSound::PlayOneShotSound` at `0x005D5E00` through the global at
  `0x007A39EC`. **VERIFIED by disassembly**: the function is a thunk to `0x005D7550`, which ends
  in `ret 0x18`; all 16 direct callers pass a `CResRef*`, a priority group (0xA or 0xB) and four
  zeros (read at `0x006467A0`, `0x00655990`, `0x006800A0`). A 2D one-shot, so every player hears
  it at full volume wherever the camera is. NEEDS-LIVE that a plain PCM `.wav` in Override plays.
- `CheatController.Chungus`: spawn, wind-up, landing, remove, plus two staging forms (any
  animation id on him, any sound by name).
- `ChungusRule` (pure, linked into the tests): the plugin's copy of the power numbers and what
  each does. `ChungusBossTests.ThePluginsNumbersAreTheWiresNumbers` pins it to the protocol.

No shared-block field was added and the block version did not change.

## 3. The model

Made from scratch by `tools/chungus/` (Python; numpy, scipy, Pillow). No Blender, no third-party
art, no BioWare bytes.

| File | What |
| --- | --- |
| `kmdl.py` | Writer for the K1 PC binary model format (`.mdl` + `.mdx`): dummy nodes, textured triangle meshes, keyframe animations. No skinning |
| `geom.py` | Stretched spheres and tapered tubes, rotations |
| `model.py` | The body: 24 nodes, 12 meshes, about 6,450 triangles. Every part is rigid and hangs off a pivot, so no skin weights are needed |
| `anims.py` | 32 animations written as poses over time, sampled at 15 keys a second |
| `texture.py` | One 512 x 512 texture, painted in code |
| `sfx.py` | Nine sounds synthesised in code (22 kHz mono 16-bit PCM) |
| `preview.py` | A software renderer for looking at the model and animations without the game |
| `build.py` | Writes everything to `profiles/override` (`--preview DIR` also renders pictures) |
| `mdldump.py` | Dumps any K1 binary model's structure; `--cover` lists which bytes were accounted for |

Format facts (**VERIFIED** against the game's own `c_gizka`, `c_rancor`, `c_gammorean`, read out
of `models.bif`; KotorBlender's reader was the starting reference for the layout):

- Layout: 12-byte file header (0, mdl size, mdx size), 80-byte geometry header, 116-byte model
  header, name offsets, names, animation offsets, each animation (136-byte header, events, its
  node tree), then the geometry nodes. Every offset is relative to byte 12.
- A single-model creature (appearance.2da `modeltype` S) carries its own animations; the
  supermodel is `NULL`. The names the engine asks for are the `c` set: `cpause1`, `cpause2`,
  `cwalk`, `crun`, `cwalkinj`, `creadyr`, `cdodgeg`, `cdamages`, `cdie`, `cdead`, `ctaunt`,
  `cvictory`, `chturnl`, `chturnr`, `tlknorm`, and `g0a1`/`g0a2` for the attack with a `Hit`
  event at 0.5 s. Larger creatures add `choke`, `horror`, `sleep`, `paralyzed`, `whirlwind`,
  `ckdbck`, `ckdbcklp`, `cgustandb`, `cspasm`. He has all of them.
- animations.2da has a creature block from row 256 that also names `castout1..3`, `powerup`,
  `powered`, `powerdown`. His powers ride on those and on `ctaunt`/`cvictory`. The ids passed to
  `PlayOneShotAnimation` are 10000 + row (INFERRED from `AnimIdle = 0x2710` and the live note on
  0x27AD; NEEDS-LIVE that these rows resolve on his model).
- An animation's node tree repeats the model's nodes as bare 80-byte headers with controllers:
  position (type 8), orientation (20, four floats x y z w), and on mesh nodes scale (36) and
  self-illumination colour (100). In animations the unknown word of a position or orientation
  key is type + 8 (0x10, 0x1C), as in the stock files; it is 0xFFFF on geometry nodes.
- The gauntlet's stones are their own mesh so the animations can light them (self-illumination
  keyframes). The belly breathes with a scale key.

Checks that run without the game: `tools/chungus/mdldump.py --cover` accounts for every byte of
the file with no gaps (it is also how the stock models were read); PyKotor, an independent reader, parses it to
the same 24 nodes, 12 meshes, per-mesh vertex and face counts, texture names, 32 animations and
events; `ChungusBossTests` checks the shipped headers, sizes and animation names.

## 4. Install

His model, texture and sounds are ordinary `k_coop_*` files in the package's Override folder, so
the existing copy, uninstall and locked-file probe cover them with no change.

Two things cannot ship ready-made, because they depend on the player's own appearance table:

- **A row in appearance.2da** (`ChungusInstaller`): the table (Override copy if a mod put one
  there, else the game's) with one row labelled `coop_chungus` appended, copied from the stock
  gizka's row with the model name, size and walk/run distances changed. Written to
  `Override\appearance.2da`.
- **`k_coop_chungus.utc`** (`ChungusBlueprint`): generated with that row's number. Stock default
  combat scripts by name (`k_def_*`, as `g_kathhound01` carries them), one stock creature weapon
  (`g_w_crslash003`), 20 soldier levels, 800 hit points, faction Hostile_1, not plot, not pinned.

`PackageInstaller` does both after the Override copy; the agent also does it at start when missing
(the Linux route, as for the arena). The game reads its tables at launch, so a boss installed
while the game runs counts from the next start (`SessionSettings.BossReady`). Uninstall takes the
row out again when it is still the last row, and deletes `Override\appearance.2da` when what is
left is exactly the game's own table.

Consequences to know about:

- A game that had no `Override\appearance.2da` now has one. Two players on the same package and
  the same base game get identical bytes, so the content digests still match. A player with an
  appearance mod gets a different row number from one without; that pair already differed.
- With different row numbers on the host and a joiner, the joiner's copy of him is built from the
  joiner's own blueprint (right model) but the enemy mirror may repaint it with the host's row
  number (wrong model). NEEDS-LIVE; only arises when the two tables differ.
- `install.sh` cannot edit a binary table; on Linux the agent installs him at first start.

## 5. NEEDS-LIVE checklist

Stage 1 needs one game, no session. Use the staging forms.

1. **He loads.** After `install` and a restart, `chungus spawn`: he appears 4 m east of the player,
   textured, standing in `cpause1`, and the game does not crash. If it does, the fault address and
   `tools/crashlog.ps1` say which loader step; the model writer is the first suspect.
2. **Look.** Screenshot from the front and side: proportions, texture seams, the gauntlet on his
   left hand, shadows. Check he stands on the floor, not in it.
3. **Animations.** `chungus anim 10263` (snap), `10260`, `10293`, `10294`, `10295`, `10296`: each
   plays the named clip once and returns to idle. If none plays, the 10000 + row reading is wrong;
   try the generic rows (taunt 33, victory 17).
4. **Sounds.** `chungus sound k_coop_chsnap` and the others: audible, not clipped, not looping.
5. **He fights.** Left alone he walks at the player (`cwalk`/`crun`, feet not sliding), swings
   (`g0a1`/`g0a2`), takes hits (`cdamages`) and dies with `cdie` into `cdead`.
6. **Powers on self.** `chungus power 1` to `7`: the wind-up animation and sound, then the landing
   sound and the effect on the player (half health; thrown; lifted; gizka; frightened; choked;
   slowed). The gizka (disguise) has never run on a game: check the player turns back after 12 s.
7. **Remove.** `chungus remove` takes him away.

Stage 2 needs a host and a joiner, both restarted after install.

8. **Refusals.** `chungus` from the joiner with `pvp = off` on the host: "Only <host>, the host,
   can call for Chungus". With a player in a conversation or another area: named, nothing moves.
9. **Way in and countdown.** One `arena: Enter` on the host, the joiner follows, both stand in the
   row at the east end facing west, both log `duel: boss round ... pinned at 1 hp minimum`.
10. **He appears for both.** Host log `chungus: the boss is in the ring as 0x...`; the joiner sees
    the same model within a second or two (built from his blueprint, not a repainted stand-in).
11. **Both can hurt him**, and his health on the joiner follows the host's.
12. **His melee reaches both**: the host directly, the joiner through its stand-in on the host
    (not dropped by the duel hit filter; this is what level 1 instead of 2 is for).
13. **Powers on both.** Each power's line, sounds and effect on both games at about the same
    moment; an aimed power only on the named player. His animation plays on both.
14. **Win.** Kill him: the defeat sound, "Chungus is beaten", 3 s, the way back, both restored to
    their snapshot, on their spots. No boss or corpse in the arena on a second visit.
15. **Loss.** Let him take both to 1 hp: "Chungus beat everyone" after 2.5 s, the way back.
16. **Yield, host leaving, a joiner dropping mid-fight.**
17. **Saving** stays blocked from the way in until everyone is home; the autosave slot names the
    campaign module afterwards.
18. **Scaling.** With `enemy_health` on auto, record what his 800 hit points become for 2 players.

Most likely to fail: 1 (a model written by a new writer), 3 (the animation id reading), 6 (the
disguise effect), 12 (stand-in damage in the arena).
