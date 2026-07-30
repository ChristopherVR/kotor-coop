# Finding KOTOR's offsets

The co-op stack is complete and tested. What it needs from your install is a handful of
memory offsets, because those differ per build and cannot be shipped as constants that are
trustworthy. This is how to find them.

## Why this cannot be shipped pre-filled

Three reasons, in order of how much they matter:

1. **A wrong offset is worse than no offset.** It reads plausible-looking floats out of
   unrelated memory, and the failure surfaces as "the networking is broken" rather than
   "the profile is wrong". `profiles/kotor1.json` therefore refuses to load until
   `layout.position` is set, rather than defaulting to zero.
2. **Heap addresses are not stable.** The player object is allocated at load, so its
   address changes between runs and between save loads. What is stable is a *pointer path*
   from a static global, and that has to be derived on a live process.
3. **The image is packed.** `swkotor.exe`'s entry point is at `0x0086D2ED`, inside a
   `.bind` section, which is a DRM/packer stub. The bytes of `.text` on disk are not
   guaranteed to be the bytes that execute, so signatures taken from the file are
   unreliable. Everything must be scanned against the running process.

## What is already known about your executable

Established from the PE header, so these you can rely on:

| Property           | Value                                            |
| ------------------ | ------------------------------------------------ |
| MD5                | `06B34D1B8A1ECEFBAAD0BF5E26556C71`               |
| Version            | 1.0.3 (Steam)                                    |
| Machine            | x86, 32-bit -> **pointer chains are 4 bytes**    |
| ImageBase          | `0x00400000`                                     |
| SizeOfImage        | `0x004C3000` (module spans `0x400000`-`0x8C3000`)|
| DllCharacteristics | `0x0000` -> **no ASLR**, addresses stable across runs |
| `.text`            | `0x00401000`, size `0x33B1D0`                    |
| `.rdata`           | `0x0073D000`, size `0x04FBFE`                    |
| `.data`            | `0x0078D000`, size `0x0A8498` <- static globals  |
| `.rsrc`            | `0x00836000`                                     |
| `.bind`            | `0x0086D000` <- packer stub, holds the entry point |

The absence of ASLR is the single most helpful fact here: once you find a static global in
`.data`, its address is the same every launch, so a `ModuleOffset` anchor plus a pointer
chain gives a profile that keeps working.

## Step 1: launch windowed, and check that it really is windowed

```bash
dotnet run --project src/KotorCoop.Agent -- launch --windowed
```

This backs up `swkotor.ini` to `swkotor.ini.kotorcoop-backup`, then sets **both**
`FullScreen=0` and `AllowWindowedMode=1`. To undo it, copy the backup back.

**`FullScreen=0` on its own is silently ignored.** Without `AllowWindowedMode=1` the game
still takes an exclusive fullscreen display mode, and that breaks everything downstream in
ways that do not look like a windowing problem:

- **Screen capture returns a stale frame.** It looks like a real screenshot of the game and
  it never updates, so the game appears frozen while actually running at 100% CPU.
- **Injected input is ignored.** DirectInput at an exclusive cooperative level does not
  observe `SendInput`, so automation silently does nothing.

The second one is the dangerous one, because the tooling still *runs*. Discovery appears to
work, walks the character, narrows millions of candidates, and reports a winner. All of it
is measuring ambient churn, because the character never moved. Three full runs were burned
before the flat screenshots gave it away.

How to verify in one step: check the desktop resolution. If it has changed to the game's
resolution (800x600 here), you are in exclusive fullscreen; a genuine window leaves the
desktop alone and gives the client area a non-zero screen origin such as `(8, 31)`.

Then load a save and take control of your character, standing in open ground. The scan
needs you in-world, not at a menu.

### Two other traps that cost real time

- **The Windows firewall prompt steals focus.** The first time the agent binds a UDP port,
  Windows raises a security dialog hosted by `PickerHost.exe`. It takes foreground, so
  injected input stops reaching the game and the character freezes mid-run. Worse, the
  host window has an empty title, so closing dialogs by title misses it. Look for
  `PickerHost.exe` specifically.
- **The character walks into walls.** A "walk" round that produces no movement looks
  identical to a broken scanner. Turn between walks, and confirm movement with a
  screenshot diff before trusting a discovery run.

## Step 2: use an exact value, not a float scan

**Measured on this install, differential float scanning does not work well on KOTOR, and
an exact integer scan does.** This is the opposite of what usually gets recommended, so
the evidence:

| Approach | First-pass candidates | Outcome |
| --- | --- | --- |
| Differential float, `[-5000, 5000]` | ~2,000,000-3,300,000 | after four walk/stand rounds and a shape filter, the survivors were transform matrix rows and vertex data |
| Exact int32 | **2,380** in 0.4s | narrows to a handful in one more pass |

The reason is the composition of the process. KOTOR has ~200 MB of private committed
memory and most of it is graphics: vertex buffers, normals, and per-frame 4x4 transform
matrices. The translation row of a transform matrix is indistinguishable from a position
by any static test. It holds three finite floats in world range, and while the character
walks it moves smoothly, at a constant rate, in every sample. Three separate automated
runs converged on matrix rows rather than the player object, recognisable afterwards by
the trailing `1.00` homogeneous `w` component and by the memory being recycled seconds
later.

An exact integer taken off the UI has no comparable population competing with it.

```bash
dotnet run --project src/KotorCoop.Agent -- scan --process swkotor
```

```
int 1450          # your current credits, read off the game UI
                  # now buy or sell something so the number changes
int 1200          # narrows to a handful of addresses
near 0x...        # show world-range float triples around one of them
                  # walk in game
near 0x...        # the triple that changed is the position
```

Good values to use, best first:

- **Credits.** Exactly displayed, changes only when you choose, never drifts.
- **Current hit points.** Also exact, but regenerates and changes in combat.
- **A skill or attribute rank.** Small numbers have many false matches; use as a last resort.

Once `int` has narrowed to a few addresses you are *inside the player's structure*. `near`
then lists float triples around it, and the position is whichever one moves when you walk.
From there, `ptr` gives you a pointer path exactly as described below.

### A worked example, done on this install

Character "Lanik Star", Soldier level 2, on Taris Upper City South. Read off the character
sheet: Vitality 25/28, STR 16, DEX 14, CON 14, INT 10, WIS 12, CHA 10, Experience 3155.

```
int 3155                      ->  8 addresses (0.4s)
find 10 0E 0E 0A 0C 0A        ->  8 addresses (0.7s)   the six attributes as bytes
```

Cross-referencing the two lists, `0x23066B60` (experience) and `0x23066B3C` (attributes)
are `0x24` apart, so that allocation is the player's creature record. `dump` confirmed it
outright:

```
+0x03C  10 0E 0E 0A 0C 0A     attributes, base
+0x042  10 0E 0E 0A 0C 0A     attributes, current
+0x054  19 00 1C 00           Vitality 25 / 28, as two uint16
+0x060  53 0C 00 00           0xC53 = 3155 experience
+0x078  "po_pmhc3"            portrait resref
```

Then `near 0x23066B00` listed the float triples around it, the character was walked, and
the block was dumped again. Exactly two floats moved:

| address | before | after |
| --- | --- | --- |
| `0x23066A04` | 113.518 | 114.397 |
| `0x23066A08` | 82.369 | 82.367 |

So position is at `0x23066A04`. A nearby static triple `(97.70, 93.29, 1.50)`, which
appears twice and never moves, is the module spawn point rather than the player.

Confirmed by driving the character and sampling:

```
start           (114.40, 82.37)
turn left       (114.40, 82.37)   turning does not move you
walk forward    (114.62, 89.65)
walk forward    (115.23, 89.74)
turn right      (115.23, 89.74)   turning again does not move you
walk forward    (121.48, 84.94)
walk backward   (117.79, 89.31)
```

That pattern, turning never moving and walking always moving, is what distinguishes a
position from a camera, a matrix row or a waypoint.

The result is committed as `profiles/kotor1-session.json`. It uses an `Absolute` anchor and
is therefore **valid only for that run of the game**; it exists as evidence, not as a
usable profile. See the next step for making it durable.

## Step 3: guided float discovery (fallback)

If you cannot use an exact value, the differential float path still exists. Expect it to
need patience and a manual eye on the candidates.

```bash
dotnet run --project src/KotorCoop.Agent -- track --process swkotor
```

The command walks you through a differential scan:

1. **Baseline snapshot.** A raw copy of the game's private committed memory. On this
   install that is ~120 MB captured in about 0.1s.
2. **Walk, then stop.** The snapshot is diffed against live memory and every
   4-byte-aligned float that *changed* and looks like a world coordinate is kept.
3. **Stand still.** Keeps candidates whose value *did not change*. This is the step that
   kills timers, animation blends and physics scratch values.
4. **Repeat.** Two more rounds of walk/stand usually leaves single digits.
5. **Vector3 filter.** Keeps only addresses where the next two floats are also plausible
   world coordinates, since Odyssey stores position as three consecutive floats.

The first pass is a snapshot diff rather than an enumerate-everything scan for a concrete
reason: 120 MB of memory is ~31 million floats, and a candidate list that large costs
about a gigabyte. Capping the list instead is actively harmful, because the cap biases the
survivors towards low addresses and can silently exclude the player object. Diffing two
snapshots gives a complete first pass at a fixed cost.

Then pick a candidate and it prints your coordinates live. Walk around: the numbers should
track you, and the "travelled" counter should climb only when you actually move.

Tips:

- During "stand still", the camera may drift. That is fine. What matters is that your
  *character* does not move.
- If everything is eliminated, you moved during a still round or did not move during a
  walk round. Just run `track` again.
- If several candidates survive and all track you, they are usually the position, a
  "previous position" copy, and the camera target. Prefer the one whose values are the
  most stable when you stand still.

## Step 4: make it durable

The address `track` prints is a heap address, valid only for that run of the game. To get
a profile that survives a restart, find a static pointer to it:

```bash
dotnet run --project src/KotorCoop.Agent -- scan --process swkotor
scan> ptr 0x1A2B3C40
```

`ptr` reports every pointer in the process that points at, or just before, that address,
along with the offset. What you want is a hit whose *pointer address* lies inside
`0x400000`-`0x8C3000`, which means it lives in `swkotor.exe`'s own static data.

Suppose `ptr` reports:

```
0x0079C4A8 -> base + 0x1C
```

`0x0079C4A8` is inside `.data`, so it is a static global. Its offset from the module base
is `0x0079C4A8 - 0x400000 = 0x39C4A8`. The player structure is at whatever that global
points to, and the position is `0x1C` into it. That gives:

```jsonc
"anchor": {
  "mode": "ModuleOffset",
  "module": "swkotor.exe",
  "moduleOffset": 3785896,      // 0x39C4A8
  "dereferenceAnchor": true,    // the global holds a pointer; follow it
  "pointerChain": []
},
"layout": {
  "position": 28                // 0x1C
}
```

If the global points at something that in turn points at the player (common: a game object
manager holding a pointer to the party leader), use `pointerChain` for the intermediate
hops. The semantics are Cheat Engine's: every offset except the last is a dereference, the
last is a field offset.

Verify by restarting the game and running:

```bash
dotnet run --project src/KotorCoop.Agent -- run --process swkotor --profile kotor1 --host
```

The dashboard should show `memory read ok` and your position updating as you walk.

## Step 5: the remaining fields

With the anchor established, the other fields are offsets from the same structure and are
much easier, because you now know where to look:

| Field        | How to find it                                                                   |
| ------------ | -------------------------------------------------------------------------------- |
| `facing`     | A single float that changes when you rotate but not when you walk in a straight line. Usually within 0x20 of position. Radians. |
| `moduleName` | A fixed-width null-padded ASCII field. Odyssey ResRefs are 16 bytes. Its value should match the current area, e.g. `end_m01aa` on the Endar Spire. |
| `health`     | An int32 that drops when you take damage. Scan for it separately with `scan` using an exact value, since it is not a float. |
| `animation`  | An int/uint that changes when you switch between idle, walk and run.              |

Fill each in and re-run. Fields you leave as `null` are simply not replicated.

## Step 6: writing remote players

There is no step 6 yet, and this is where an honest proof of concept stops.

Reading your own position is a scanning problem, solved above. Writing a *second* player
in is a different class of problem: stock KOTOR has no spare creature to drive. There is
no dormant object you can fill in with coordinates and have the renderer draw. You would
have to make the engine spawn a creature through its own object system and then keep
driving it, which means understanding the Odyssey object manager, the area object list,
and the animation system.

That is why `profiles/kotor1.json` deliberately omits the `remotes` block and the agent
reports `write: n/a (read-only profile)`. The mock harness has a `remotes` block because
it was designed to have one; the retail game was not.

If you get that far, the profile schema is already there to describe it: give `kotor1.json`
a `remotes` section pointing at whatever slot array you can drive, and the agent starts
writing without any code change.

## Reference: profile schema

```jsonc
{
  "name": "...",
  "processNames": ["swkotor"],

  "anchor": {
    "mode": "PatternScan" | "ModuleOffset" | "Absolute",
    "pattern": "8B 0D ?? ?? ?? ?? 85 C9",  // PatternScan only, '??' is a wildcard
    "scope": "PrivateCommitted" | "Images" | "AllReadable",
    "module": "swkotor.exe",               // ModuleOffset, or to scope a PatternScan
    "moduleOffset": 0,                     // ModuleOffset only
    "absoluteAddress": 0,                  // Absolute only
    "displacement": 0,                     // added to the resolved address
    "dereferenceAnchor": false,            // treat the anchor as holding a pointer
    "pointerChain": []                     // Cheat Engine semantics
  },

  "layout": {                              // offsets from the resolved anchor
    "position": 0,                         // 3 consecutive floats; required
    "facing": null,                        // float, radians
    "moduleName": null,                    // fixed-width ascii
    "moduleNameLength": 16,
    "health": null,                        // int32
    "animation": null                      // uint32
  },

  "remotes": null                          // omit for a read-only profile
}
```
