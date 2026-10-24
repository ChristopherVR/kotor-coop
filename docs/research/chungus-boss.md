> **Superseded design:** the rabbit/gauntlet visuals and voice described in the historical sections below
> were replaced by Cairn Sentinel on 2026-10-24. See the [current dated redesign section](#2026-10-24-cairn-sentinel-replacement).
> Earlier live results do not verify the replacement model, animation or audio.

# Chungus: the arena boss cheat

Status: built and run in the game 2026-10-10, alone and with two players (section 5), and
rebuilt the same day with a sculpted, skinned model, the game's own effects and voice, and the
ways he breaks the game's rules (blinking, rolling, stopping time). What nobody has checked yet
is in section 6, the sounds by ear above all. Tags as in the other research docs: **VERIFIED** = read from game files, the binary or repo code here;
**INFERRED** = deduced; **NEEDS-LIVE** = must be tried in the game.

`chungus` takes every player in the session to the PvP arena module to fight one boss together: a
very fat grey-and-white rabbit wearing a jewelled golden gauntlet. His model, textures and
animations are made for this mod; his grunts and the look and sound of his powers are the game's
own (the Hutt's voice, the Force powers' effects). He hits with the gauntlet, uses one of its
stones every few seconds, and does not get about the way anything else in the game does. When he is beaten, or has beaten everyone, or somebody types `yield`,
everyone is taken back to where they stood, as they were. Nothing is kept.

## 1. What the player sees

| Step | What happens |
| --- | --- |
| `chungus` | The host can always start it. A joiner can when the host has `pvp = arena`. Refused with a plain line if a duel is on, or if any player is in a conversation, in another area than the host, or does not have the boss installed |
| Way in | The arena round's own single warp: the host's game changes module once, everyone follows |
| Countdown | 5 s once everyone has arrived. The party stands in a row across the east end. Everyone is pinned at 1 hit point minimum from here: nobody can die |
| Fight | He appears at the west end. Stock melee AI (a gauntlet punch and a gauntlet backhand) plus one gauntlet power every 7 to 10 s (5 to 7 once he is below a quarter health), and between powers he blinks |
| End | He dies (win), every player is at the 1 hp floor together for 2.5 s (loss), `yield` from anyone (called off), the host leaves the arena, or 10 minutes pass |
| Way back | One warp home, each player put back on their spot with the hit points and Force points they came with |

The powers (`BossPower`, `BossPowers`, `ChungusRule.Landing`):

| Power | Aimed at | Lands after | What it does to a player | His animation | What is seen and heard (all the game's own unless said) |
| --- | --- | --- | --- | --- | --- |
| Snap | everyone | 1.3 s | half of current hit points, thrown down | `ctaunt` | Force aura on him, the mod's snap sound, a Force wave from him, screen shake; 0.9 s later he says "Perfectly balanced, as all things should be." (the mod's `k_coop_chline.wav`) |
| Power stone | one | 0.9 s | 30% of maximum hit points, thrown back | `disabled` (held 1.6 s) | Force aura, a dark lightning beam from his hand |
| Space stone (`Lift`) | everyone | 0.7 s | moved to a random spot in the ring and thrown down there; he goes somewhere else too | `chturnl` (the open hand raised) | whirlwind burst, screen shake |
| Reality stone | one | 0.9 s | turned into a gizka for 12 s, and slowed | `usecomp` (a hammer blow) | polymorph burst |
| Mind stone | everyone | 0.7 s | frightened for 5 s | `cvictory` (a laugh) | mind-mastery on him, horror on the player |
| Soul stone | one | 0.85 s | 15% of maximum hit points and choked for 3 s; he heals a twelfth of his health | `chturnr` (a reach and a clench) | drain beam from his hand |
| Time stone (`Slow`) | everyone | 0.7 s | held still for 3.5 s (`EffectParalyze`); half a second in, he appears beside one of them | `chturnl` | suppress burst |
| Blink | (names one player) | at once | nothing | none | Force aura where he lands. No line of text |

The animation names look odd because a creature model can only be told to play a short, fixed
list of one-shots (section 3); his powers are stored under the names on that list. `Lift` and
`Slow` keep the names they had when they lifted and slowed, because the numbers are on the wire.

The snap is kept for when it matters: the first time he drops to 60% health and again at 25%.

How he gets about (the part that is meant to look wrong for this game):

- **He bounces instead of walking** (`cwalk`: the whole of him leaves the floor twice a cycle and
  lands on his belly) and **rolls instead of running** (`crun`: one head-over-heels turn a cycle).
  `walkdist` 2.0 and `rundist` 6.3 in his appearance row are what those cycles cover.
- **He blinks.** Every 5 to 8 s between powers (and not for 4 s after one, so its animation is
  not cut off) the host's pump says `BossPower.Blink` at a random player. Half the time he lands
  3.2 m from them on the side the middle of the ring is; the other half he lands across the ring,
  and rolls back.
- **He walks through stopped time**, as above.

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
a sound and an effect on him), and the landing after `BossPowers.LandDelayMs` (a sound, effects,
and the result on this game's **own** player if the cue hits it).

Three powers ask for more, all through the same queue:

- **Snap**: a third request 0.9 s after the landing plays his line (`OpSound`).
- **Space stone**: just before the landing, each game sends its own player the ordinary
  `Teleport` cheat (walkmesh-guarded) to `ArenaFlow.RingSpot`, a random point on a circle about
  the middle of the ring. Each game picks its own spot, so nothing about where a player went has
  to cross the wire; the others see it through that player's ordinary position updates.
- **Anything that moves him** (blink, space stone, time stone): the host alone sends `OpBlink`
  with a position, and the plugin moves him with `Odyssey.SnapTo`, the walkmesh-guarded snap, so
  a spot that is not floor leaves him where he was. Joiners see their copy follow through the
  enemy mirror (run with two players: the copy was where the host's was in every pair of frames).

The blink is decided in the pump, not in `BossBrain`: the brain stays the thing that picks a
power on a timer, and its tests did not have to learn about a second timer.

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
- `Odyssey.PlayAnimationNow(objectId, scriptAnimation, seconds)`: NWScript `PlayAnimation`
  through the VM with the creature as the caller (section 3 has what the handler does).
- `OdysseyEffects.ApplyVisualEffect(objectId, effect, durationMs)`: NWScript
  `EffectVisualEffect` (handler `0x005346B0`) applied to an object, instant or for a time. The
  number is the id in the first column of visualeffects.2da.
- `OdysseyEffects.ApplyBeam(objectId, beam, fromObjectId, durationMs)`: `EffectBeam` (handler
  `0x00531260`), drawn from the other object's left hand node (`lhand`, which his model has on
  the gauntlet).
- `CheatController.Chungus`: spawn, wind-up, landing, remove, blink, plus two staging forms (any
  animation on him, by script constant or engine id, and any sound by name).
- `ChungusRule` (pure, linked into the tests): the plugin's copy of the power numbers and what
  each does. `ChungusBossTests.ThePluginsNumbersAreTheWiresNumbers` pins it to the protocol.

No shared-block field was added and the block version did not change.

## 3. The model

Made from scratch by `tools/chungus/` (Python). No Blender, no third-party art, no BioWare
bytes. The first build was stretched spheres hung rigidly off pivots, and looked it. The second
is one continuous sculpted surface, skinned to a skeleton:

| File | What |
| --- | --- |
| `sdf.py` | Shapes as distance fields (ellipsoids, tapered limbs, boxes) blended smoothly into one surface, turned into triangles by marching cubes and thinned to a budget; the same fields give normals, ambient occlusion and, per vertex, how much of it each bone carries |
| `sculpt.py` | What shape he is: the skeleton (23 body bones, 10 finger bones), the body's parts and which bone carries each, the gauntlet's plates, the six stones, eyes, lids, teeth |
| `bake.py` | Lays the surfaces out on a texture (xatlas) and paints every texel from a function of its point in space, so there are no seams |
| `texture.py` | What colour every point is: fur with baked shading, the face, gold with a mirror mask in the alpha channel, the stones |
| `model.py` | Puts it together: 67 nodes, about 16,800 triangles; five skinned meshes for the body, rigid pieces for the face and the gauntlet |
| `kmdl.py` | Writer for the K1 PC binary model format (`.mdl` + `.mdx`): dummy nodes, rigid and skinned triangle meshes, keyframe animations |
| `anims.py` | 28 animations written as poses over time, with inverse kinematics for both arms, sampled at 15 keys a second |
| `sfx.py`, `voice.ps1` | The two sounds the game has nothing like: the snap (synthesised), and his line (a Windows text-to-speech voice, `voice_raw.wav`, slowed, dropped in pitch, doubled an octave down and put in a hall) |
| `preview.py` | A software renderer, skinning included, for looking at the model and animations without the game |
| `build.py` | Writes everything to `profiles/override` |
| `mdldump.py` | Dumps any K1 binary model's structure; `--cover` lists which bytes were accounted for |

It needs more than the first build did (scikit-image, fast-simplification, xatlas beside numpy,
scipy and Pillow), so it has its own environment:
`python -m venv artifacts/chungus-venv`, install those, then
`artifacts/chungus-venv/Scripts/python tools/chungus/build.py`.

Skinning facts (**VERIFIED** by reading stock skins with `mdldump.py`, then in the game):

- A skin node (type flag `0x40`) is a mesh header (332 bytes) followed by a 100-byte skin
  header: where the weights and bone indices sit in an MDX row, a bone map with one float per
  node of the model (the bone's number in this skin, or -1), and per bone its inverse rest
  orientation (w, x, y, z) and the negative of its rest position.
- An MDX row is 64 bytes: position, normal, texture coordinate, four weights, four bone numbers
  as floats (-1 for unused).
- **A skin names at most 16 bones.** His body is split into five skins (head, torso, each arm,
  legs) by which bone carries most of each triangle.
- **A skin node may carry position, orientation and self-illumination controllers and no
  others.** With the alpha and scale controllers a rigid mesh gets, the skin drew flat and
  untextured in the game (it was right in every offline reader). Found live.
- The gauntlet's texture is 32-bit with a `.txi` beside it saying `envmaptexture CM_Baremetal`,
  the same line the game's droids carry: where the alpha is low the engine blends its stock
  metal reflection in.
- His body has a little self-illumination (0.2). Without it the arena, which is dark and lit
  blue, turned grey fur into a navy silhouette (seen live, then fixed).

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
- **Animation ids are not rows of animations.2da** (the first build assumed 10000 + row; live,
  every one was "in no playable class"). `CSWCAnimBase::ServerToClientAnimation` (`0x0069F650`)
  turns an id into a row, and for a model whose animbase byte `+0xC6` is not 1 (a creature) it
  takes the branch at `0x0069FCBB`: four jump tables (`0x6A017C`/`0x6A01B8`, `0x6A01E4`/`0x6A022C`,
  `0x6A0288`, `0x6A02AC`/`0x6A02C4`), decoded from the binary. The one-shots a creature can be
  told to play: 10028 `ctaunt`, 10044 `cvictory`, 10053 `chturnl`, 10054 `chturnr`, 10125
  `usecomp`, 10147 `cpause2`, 10015/10016 `g0a1`, 10014 `cdamages`, 10219 `ckdbck`, 10221 `cdie`,
  10223 `cgustandb`, 11000 `b0a1`. Looping: 10001 `creadyr`, 10118 `disabled`, 10023 `cspasm`,
  10117 `whirlwind`, 10124 `horror`, 10137 `sleep`, 10138 `paralyzed`, 10150 `choke`, 10400
  `ckdbcklp`. The `castout`/`powerup` rows of the creature block are not reachable at all.
- **On a creature that is fighting, a client-side one-shot is accepted and not shown.** The
  script command is what shows: NWScript `PlayAnimation` (routine 300, handler `0x00540550`)
  calls `CSWSObject::ClearAllActions(1)` and `AddActionToFront` with action 6, so his attack is
  dropped and the animation goes first; his own scripts pick the fight up again afterwards. Its
  constants go through the handler's table at `0x00540C44`: 107 -> 10028, 108 -> 10044, 100 ->
  10053, 101 -> 10054, 113 -> 10125, 19 -> 10118. The host plays his powers this way
  (`Odyssey.PlayAnimationNow`); a joiner replays the engine id on its own copy.
- A position key in an animation is an offset from the node's rest position, not a position
  (the stock `c_gammorean` root dummy rests at z 1.203 and its keys read z -0.19). Found live:
  the first build wrote whole positions and his body floated a metre above his feet.
- An animation's node tree repeats the model's nodes as bare 80-byte headers with controllers:
  position (type 8), orientation (20, four floats x y z w), and on mesh nodes scale (36) and
  self-illumination colour (100). In animations the unknown word of a position or orientation
  key is type + 8 (0x10, 0x1C), as in the stock files; it is 0xFFFF on geometry nodes.
- The gauntlet's stones are their own mesh so the animations can light them (self-illumination
  keyframes).

Checks that run without the game: `tools/chungus/mdldump.py --cover` accounts for every byte of
the file with no gaps (it is also how the stock models were read); PyKotor, an independent
reader, parsed the first build to the same nodes, meshes and animations (not repeated for the
skinned one); `ChungusBossTests` checks the shipped headers, sizes, animation names, that
there are five skins and that none names more than 16 bones.

## 4. Install

His model, textures and two sounds are ordinary `k_coop_*` files in the package's Override folder, so
the existing copy, uninstall and locked-file probe cover them with no change.

Two things cannot ship ready-made, because they depend on the player's own appearance table:

- **A row in appearance.2da** (`ChungusInstaller`): the table (Override copy if a mod put one
  there, else the game's) with one row labelled `coop_chungus` appended, copied from the stock
  gizka's row with the model name, size and walk/run distances changed. Written to
  `Override\appearance.2da`. A row already there from an earlier version has its own cells
  brought up to date in place (the distances changed when he stopped walking).
- **`k_coop_chungus.utc`** (`ChungusBlueprint`): generated with that row's number. Stock default
  combat scripts by name (`k_def_*`, as `g_kathhound01` carries them), one stock creature weapon
  (`g_w_crslash003`), 20 soldier levels, 800 hit points, faction Hostile_1, not plot, not pinned.

`PackageInstaller` does both after the Override copy; the agent also does it at start when missing
(the Linux route, as for the arena). The game reads its tables at launch, so a boss installed
while the game runs counts from the next start (`SessionSettings.BossReady`). The row number also
rides along with every power request (`SessionSettings.BossAppearanceRow`), which is how a
joiner's game picks its copy of him out of the creatures it mirrors. Uninstall takes the
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

## 5. Live results (2026-10-10)

Run on the GOG install (host, 12 hp fixture character, Star Forge save) and a second copy of it
(joiner, 6 hp), both under the rig's full page heap, driven through `tools/blockpoke.ps1 console`
and `tools/shot.ps1`. Log lines are from `kotorcoop-plugin.log` and the agent's log.

| What | Result | Evidence |
| --- | --- | --- |
| The model loads | PASS | `chungus: spawned 0x11A ... 900 hp, hostile yes`; no fault under page heap; screenshots show him textured, named, with a health bar |
| He stands on the floor | PASS after a fix | first build floated a metre up: animation position keys are offsets (section 3) |
| He fights by himself | PASS | walked to the player and hit for 9 to 18 with the stock scripts; killed the 12 hp character in one blow |
| Power animations by engine id on a fighting creature | accepted but not seen | `PlayOneShotAnimation` returned true for 10028/10044/10053/10054/10125; frames showed his combat stance |
| Power animations by script command | PASS | `script animation 107/19/113/100/101/108 played`; frames show a different gauntlet pose for each, mid-fight |
| Sounds | called, not heard | `PlaySound` returns for every name (the engine gives no failure for a missing file), and nobody was listening |
| Every power on a player | PASS | per power: damage and effect applied (8 thrown, 9 lifted, 16 gizka + 4 slowed, 5 frightened, 7 choked); screenshot of the player as a gizka |
| Round, host alone | PASS | one `arena: Enter`, pinned (`snapshot hp 12`), spawned at (190.5, 99.9), entrance, first power, lost at the floor after 2.5 s, boss removed, one `arena: Return`, hp 12 restored |
| Round, two players | PASS | joiner followed the warp, pinned (`snapshot hp 6`), `materialise: owner 0x23 -> local 0x20 (appearance 509 ...)`; both screens show him in the ring |
| Powers on both games | PASS | each cue logged on both within 50 ms; an aimed power lands on the named player only (`not aimed at this player` on the other); the soul stone healed him 66 on the host |
| Win | PASS | `slay` on the host: `the boss is down (dead)`, "Chungus falls over", 3 s, "Chungus is beaten", boss removed, one warp home, both restored on their spots |
| A joiner starts it | PASS | with `pvp = arena` on the host |
| `yield` from a joiner | PASS | "Malak called off the fight with Chungus", both home and restored |
| Joiner finds its copy of him | PASS after a fix | first run `boss 0x0` (his blueprint's name reached the joiner after the copy was built); now found by appearance row: `boss 0x21, animation 10044 played`; name plate reads Chungus |
| Banner during the fight | PASS after a fix | was "Duel with Someone."; now "Fight with Chungus. F12 yield (calls it off for everyone, nothing is lost)" |
| Saving blocked | PASS | `arena: refused a save request (slot 61) inside the arena module`, `duel: refused a save request ... while the duel pin is up` |

Found on the way, not his fault: a 12 hp character loses in seconds (the floor is reached on the
first blow), so the two-player runs gave the joiner temporary hit points to keep the round open.

The second build, same day, same two installs:

| What | Result | Evidence |
| --- | --- | --- |
| The skinned model loads and animates | PASS after a fix | under page heap, no fault; first drew flat and untextured (extra controllers on the skin nodes, section 3) |
| He can be seen in the arena | PASS after a fix | a navy silhouette at first; with 0.2 self-illumination the face, belly and ears read from across the ring |
| The gauntlet mirrors the room | PASS | frames show the gold picking up the room's light, stones lit |
| The game's effects on him and on players | PASS | `effect 1011 shown`, `2 effect(s) shown` per landing; frames show the lightning beam on the player, the horror burst, the Force aura on him |
| Time stops | PASS | `power 7 lands - ... effect 2 applied` on both games, then `blink to (...) done` half a second later |
| Space throws | PASS | `cheat: moved from 212.0,100.4 to 203.9,97.5` on the joiner and its own on the host, each followed by `effect 8 applied`; he moved in the same instant |
| He blinks | PASS | `power 10 begins`, `blink to (...) done` every 5 to 8 s; both screens show him in the new place |
| His line after the snap | queued, not heard | the request is the staging `chungus sound` path, which has run; nobody was listening |
| Two players through the new powers | PASS | 60 s of fight, every cue on both games within 50 ms, both games responding at the end, no first-chance faults in either log |

## 6. Still open (NEEDS-LIVE)

1. **Sounds by ear.** The snap and his line are well-formed PCM, the stock names were read out
   of the game's sound archive, and the engine call is the one its own interface sounds use, but
   nobody has heard any of it in the game: not the Hutt grunts, not the line. If the mod's two
   are silent, the next thing to try is the priority group (0xA instead of 0xB) and then
   `streamsounds`. The line's voice is a slowed text-to-speech voice and wants a human opinion.
2. **How the animations look in motion.** Stills show distinct poses; timing against the landing
   (the snap at 1.3 s), and the bounce and the roll at the game's own walk and run speeds, need
   eyes. The stills that caught him moving were too few to judge the roll.
3. **Loss with two players**, the host leaving the arena mid-fight, a joiner dropping mid-fight,
   the ten-minute limit. Unit and loopback tested only.
4. **Three or more players**: the row of spots at the east end (unit-tested for spacing) and a
   power aimed at a third player.
5. **Different appearance tables** on the host and a joiner (one of them with an appearance mod):
   the joiner's copy may be repainted with the host's row number.
6. **Scaling**: what `enemy_health` on auto makes of his 900 hit points for 2 and more players.
7. **A real party**: his melee against characters with real armour and hit points, and companions
   (they are not benched here, they fight too and are not restored).
8. **A second visit**: that neither he nor a corpse is in the arena when it is entered again.

## 2026-10-24 Cairn Sentinel replacement

Updated 2026-10-24. This replaces the previous rabbit/gauntlet design. Existing class,
resource and packet identifiers containing `chungus` remain compatibility identifiers;
they do not describe the current character. The player command is `sentinel` (`chungus`
remains a legacy alias).

### Design and provenance — VERIFIED source inspection

Cairn Sentinel is an articulated basalt automaton with a triangular crown, blunt tools,
cyan mineral seams and one chest resonator. Its geometry, textures, mechanical motion
and two nonverbal sounds are generated by the project's own Python code. No external
reference image, extracted game geometry, sampled speech, text-to-speech voice or
third-party character model is used as an input. The old rabbit sculpt, character paint,
Windows voice script and recorded voice input have been removed from the current tree.
Old versions remain private development history and must not be published as source archives.

The resonator discharges a pressure wave and answers with a synthetic chime. There is no
finger-snap gesture, jeweled glove, six-stone visual scheme or film quotation. The blueprint
uses sound-set 65535 (no stock creature voice). Runtime game effects and default combat
scripts are referenced from each player's installed game; those assets are not bundled.
This provenance reduces avoidable borrowed-character risk; it is not a legal opinion or
clearance of game modding, trademarks, or the project as a whole.

### Player behavior — VERIFIED automated rules; replacement NEEDS-LIVE

The host types `sentinel`; joiners may request it when `pvp = arena`. The existing arena
round transports the party, waits for arrivals, starts the boss, and returns everyone to
their prior state on victory, defeat, `yield`, timeout or host departure. No death or reward
is kept. A dialogue, separated party or missing installation blocks entry.

The existing wire IDs and combat rules are retained: resonant shockwave (half remaining
health), resonator blast, displacement field, temporary creature distortion, alarm/fear,
energy drain, stasis and relocation. These are technical compatibility mappings, not
references to stones or another fictional character. Legacy `Snap`, `Gizka`, `Chungus`
enum names stay internal so current peers and persisted installs remain compatible.

### Build and validation — VERIFIED offline checks

Run `artifacts/chungus-venv/Scripts/python.exe tools/chungus/build.py --preview artifacts/cairn-sentinel`
(or use any environment with the requirements in the builder). Outputs are the seven existing
`profiles/override/k_coop_ch*` resources. The model has 32 nodes, 268 triangles, 28 creature
animations and rigid articulated plates, avoiding skin bone limits. Texture dimensions and
PCM sound formats remain compatible with the installer and game reader.

The software-rendered multi-view image and animation sheets are in the ignored
`artifacts/cairn-sentinel/` directory. Binary structure, required animations, texture/sound
formats, command aliases and fight logic are covered by `ChungusBossTests`.

**NEEDS-LIVE:** the new rigid model's rendering, all animation
transitions, gait/foot sliding, attack-contact timing, sound levels and two-player replication.
The previous design's 2026-10-10 playtest is not evidence that these replacement visuals and
sounds work in-game. Do not describe the replacement as live-verified until that check is done.

**VERIFIED automated result:** 368 focused boss, PvP, console and menu tests passed.
All seven generated asset outputs reproduced byte-for-byte in a separate output folder.
The rendered multi-view preview was visually inspected; it is not a running-game capture.
