# Companion experience and equipment sync (2026-08-27, roadmap items 11/12)

Two gaps left in the companion mirror, both shipped behind flags default OFF: a companion's
XP never had its own channel (only the local PC's `CSWSCreatureStats+0x068` did, via
`progression-sync.md`), and a companion released under `companion_local_combat` fights with
whatever gear the mirroring client's copy of the body happened to be wearing when the two
instances last agreed, because nothing ever re-dressed it.

Tag legend, matching every other doc here: **VERIFIED** = read out of the binary, or asserted
by a test. **NEEDS-LIVE** = reasoned but not yet observed running — every one of these fails
safe. **INFERRED** = a reasonable deduction, not directly proven. Nothing in this document
was newly reverse-engineered; both features reuse offsets and engine calls
`progression-sync.md` and `item-identity.md`/`Odyssey.Equipment.cs` already established and
live-verified for the PC and the phantom respectively. What is new is the plumbing that keys
them by NPC roster slot and carries them across the wire.

---

## 1. Companion XP (`companion_xp_sync`, roadmap item 12 — see `docs/roadmap-seamless.md`)

### 1.1 Where it lives and how it is read/written

Identical mechanism to the PC's own experience (`progression-sync.md` §2): each active
companion has a `CSWSCreatureStats*` at `creature+0x0A74`, and `stats+0x068` is its XP
(u32). **VERIFIED** — same offsets `progression-sync.md` already verified against the binary
and the symbol DB, applied to a companion body instead of the PC's.

The companion is resolved by NPC roster slot, the same chain `KotorGameBridge.ReadPartyMembers`
and `PartyPresenceController` already use (`party-reconciliation.md`): `partyTable+0x04`
(`int[2]`, active slot indices) → `partyTable+0x0C + 4*slot` (`u32`, the spawned object id for
that slot, `0x7F000000` = not spawned) → resolve the id to a live creature. **VERIFIED** layout,
already load-bearing for the shipped position/health mirror.

New bridge methods, `src/KotorCoop.Memory/KotorGameBridge.Party.cs`:

- `TryReadCompanionExperience(uint[] xpBySlot, out ushort presentMask)` — host side. Walks the
  ACTIVE slots only (0..1 today, K1's own 2-companion cap), reads each one's `stats+0x068`, and
  sets bit `slot` in `presentMask`. A recruited-but-benched companion has no live body and so no
  bit — the same "0 = unspawned" convention `ReadPartyMembers` already uses.
- `TryWriteCompanionExperience(IReadOnlyList<uint> xpBySlot, ushort presentMask)` — client side.
  Writes only the slots the mask names; a slot the mask does not name is left untouched.

Both are pure `ReadProcessMemory`/`WriteProcessMemory` — no plugin, no shared block, no engine
call, exactly like the PC's own progression channel. A wrong offset can only make the write
fail; it cannot corrupt the game (same argument `progression-sync.md` §5 already makes).

### 1.2 Why kill XP does not need this channel, but script-granted XP might

`progression-sync.md` §2 disassembled `CSWPartyTable::DistributeExperience` (`0x005653A0`):
it loops the ACTIVE slot array (`partyTable+0x04`, the same array §1.1 above reads) and calls
`ReceiveExperience(creature, xp)` with the **full**, undivided amount for every member — not
just the PC. **VERIFIED** (disassembly quoted in that doc). Since every co-op instance runs a
full simulation and a companion's body exists (and is spawned) on both instances whenever the
roster is in sync, a kill that fires `DistributeExperience` on one instance's engine fires the
identical grant on the other's, independently, for the identical companion. Kill XP is
therefore expected to already agree between instances **without any wire channel** — the same
argument `progression-sync.md` already makes for the PC's own kill XP, extended to companions
because they are counted in the same loop.

What genuinely diverges is script-granted XP aimed at a specific creature or the party as a
whole from a conversation, a stealth award, or `GivePlotXP` running on only one instance's
engine (the same class of event `progression-sync.md` §4 names as the PC channel's real reason
to exist). `companion_xp_sync` exists for that case, and for the transient window where the two
instances' rosters briefly disagree (a companion spawned on one side, not yet reconciled on the
other — `party-reconciliation.md`'s roster channel is what closes that gap, not this one).
**INFERRED**, not measured: no live pass has compared a companion's `stats+0x068` across two
instances after a real kill.

### 1.3 Wire and merge design

New packet pair, `src/KotorCoop.Protocol/Packets/ProgressionPackets.cs`,
`PacketCodec.Progression.cs`, `PacketType.CompanionXpSnapshot = 51` /
`PacketType.CompanionXpReport = 52` (next free values; see `PacketType.cs`'s own skip-number
discipline):

- `CompanionXpSnapshotPacket(long ServerTimeMs, ushort PresentMask, uint[] Xp)` — host → clients,
  broadcast every tick once published, mirroring `ProgressionSnapshotPacket`.
- `CompanionXpReportPacket(uint PeerId, ushort PresentMask, uint[] Xp)` — client → host, sent
  every tick, mirroring the experience half of `ProgressionReportPacket`.

`Xp` is fixed-length (`ProtocolConstants.PartyRosterSlots` = 9, 36 bytes); `PresentMask` says
which entries are meaningful, the same "a bit means the value exists" discipline `PartyRoster`
already uses for its own masks (so a legitimate 0-XP companion is never confused with "no
reading").

**Absolute, merged upward by max — never a delta**, for the identical reason §1.2 gives: a delta
would double-count independently-granted kill XP. The merge is a new pure class,
`src/KotorCoop.Session/CompanionExperienceMerge.cs`:

```csharp
public static void MergeInto(
    ref ushort mergedMask, uint[] merged, ushort candidateMask, IReadOnlyList<uint> candidateXp)
```

Applied per-slot: a slot absent from both stays absent, a slot present in only one is adopted
as-is, a slot present in both takes the higher value. Both `PartyPump.PumpCompanionExperience`
(the host's per-report loop) and its client branch (host-vs-local merge) call the same function,
so the rule is defined once and tested without a bridge or a session
(`CompanionStateSyncTests.cs`, `MergeInto*` cases).

Host side (`Pumps/PartyPump.cs`): reads its own companions' XP, merges every fresh client
report on top by max, writes the merged result back onto its own party table (so a
client-only script grant reaches the host's own companion body too — a case the PC's own
channel does not need, since the host and the PC's "own reading" are the same thing there),
then publishes the merged truth. Client side: reports its own reading, then writes
`max(host, local)` per slot onto its own companions — never below its own value, the identical
guarantee `PumpProgression` already gives the PC's XP.

Gated on **both** `SessionCapabilities.Progression` (a content-independent scalar channel,
what the PC's own XP needs) **and** `SessionCapabilities.Party` (the slot keying needs the same
save both instances loaded — a mismatched save maps slot N to two different companions). The
PC's own progression channel only needs the former, because a bare scalar carries no roster key
at all.

### 1.4 Load-bearing constants (VERIFIED, reused from `progression-sync.md`)

| | |
| --- | --- |
| `CSWSCreatureStats::experience` | `stats+0x068` (u32) |
| `creature->creature_stats` | `creature+0x0A74` |
| `CSWPartyTable` active-slot array | `partyTable+0x04`, `int[2]` |
| `CSWPartyTable` per-slot object id | `partyTable+0x0C + 4*slot`, `int[9]`, `0x7F000000` = unspawned |
| `DistributeExperience` | `0x005653A0` — loops the active-slot array, full XP per member |

---

## 2. Companion equipment (`companion_gear_sync`, roadmap item 11)

### 2.1 The gap

`companion_local_combat` (`party-reconciliation.md`, live-verified 2026-08-24, ON by default)
releases a companion standing close enough to both players and the currently-excluded enemy to
its own engine AI on a CLIENT. That AI attacks with whatever the client's copy of the
companion's body is wearing — its own equip slots (`CSWInventory`, `creature+0x0A2C`), never
touched by the position/health mirror. If the host player has since re-equipped that companion
(a workbench upgrade, a looted weapon) since the two instances last happened to agree, the
released companion fights with stale gear on the client's screen. No channel published a
companion's equipment before this round; only the PC's own three visible slots did
(`PlayerState.EquippedBody`/`EquippedRightWeapon`/`EquippedLeftWeapon`, consumed by the phantom
channel).

### 2.2 Reusing the PC's own read and the phantom's own apply

The read is the exact function the PC's own equipment channel already uses,
`KotorGameBridge.ReadEquippedResref(creature, slotOffset)` (`KotorGameBridge.Items.cs`) — a
`CSWInventory` slot holds an **object id**, not a pointer (documented there, live-measured
trap), resolved through the same tag cache built by scanning for the `CSWSItem` vtable.
Applied to a companion creature instead of the local player: **VERIFIED** mechanism (the read
itself, for the PC), **INFERRED** that it behaves identically for a companion body — nothing
about the function is PC-specific, and a companion is a bare `CSWSCreature` with the same
`CSWInventory` layout.

The apply is the exact function the phantom equipment channel already uses,
`Odyssey.EquipItemFromTemplate(creatureObjectId, resref, length, inventorySlot, out failure)`
(`Odyssey.Equipment.cs`) — `CreateItemOnObject` then `ActionEquipItem`, redirecting
`OBJECT_SELF` to the target creature via the VM implementer's caller-object fields exactly as
the move/pickup dispatches already do. **VERIFIED live** for a phantom
(`docs/research/phantom-equip-crash.md`'s rebuild-after-poke lesson is about appearance, not
equipment, but the equip call itself has shipped and been observed dressing phantoms in real
sessions). **NEEDS-LIVE for a companion body specifically**: the function takes a bare object
id and never inspects what kind of body it is, so it is expected to work identically — the
caller-object redirect and the two VM dispatches do not distinguish "phantom" from "real party
NPC" — but this has never been exercised against a live companion. Unequip is not separately
implemented: `ActionEquipItem` replaces whatever already occupies the slot (the same behaviour
a real click on a new item does), so there is no separate "take this off" call needed for the
gear-refresh use case; a companion is never asked to go bare-handed by this channel.

### 2.3 Wire: riding the party snapshot, not a new packet

Rather than a parallel packet keyed by roster slot (which risks arriving out of order relative
to the position snapshot it has to agree with), companion gear rides
`PartyMemberState` — the same record `PartySnapshotPacket` already carries position, facing,
animation and health in — as three added fields:

```csharp
public readonly record struct PartyMemberState(
    byte SlotIndex, Vector3f Position, float Facing, uint Animation, int Health,
    uint OneShotSerial = 0, ushort OneShotAnimation = 0,
    string EquippedBody = "", string EquippedRightWeapon = "", string EquippedLeftWeapon = "");
```

Empty = "leave the slot alone" (gear sync off on the sender, an unreadable slot, or an older
build that never set the field) — the identical convention `PlayerState`'s own three equip
fields already use, so a mixed old/new pair degrades to exactly today's behaviour rather than
stripping a companion's gear. `PacketCodec.Party.cs`'s `Write`/`ReadPartySnapshot` append the
three resrefs with the existing `MaxResrefLength`-bounded `WriteString`/`ReadString` helper — no
new packet type, no `ProtocolConstants.Version` bump (a field addition, not a new negotiated
capability; existing `PartySnapshotRoundTrips`-style codec tests pin the new bytes).

`KotorGameBridge.ReadPartyMembers(bool includeEquipment = false)` only pays the extra
per-companion resref lookups when the caller opts in — `PartyPump.PumpParty`'s host branch
passes `settings.SyncCompanionGear`, so a session with the flag off costs nothing extra on the
every-tick party pump. **Anti-echo is structural, not a checked rule**: `PartyPump.PumpParty`'s
client branch never calls `ReadPartyMembers` at all — a client mirrors the host's companions,
it never reads its own copies to publish them — so there is no code path through which a client
could report its own (possibly stale) gear back to the host, symmetric with how a client's
`PublishParty`/`PublishCompanionExperience` calls are dropped by the real `CoopHost`/`CoopClient`
implementations. `CompanionStateSyncTests.AClientNeverReadsItsOwnCompanionsToPublishThem` pins
this.

### 2.4 Shared block: a new region, keyed by presence slot

Equipping needs an in-process engine call, so the client-side apply is the plugin's job, the
same shape the phantom's `PhantomController.ApplyEquipment` already uses. A new
`SharedBlock.Party.cs` region carries the host's companion gear from the agent into the plugin:

```
OffsetCompanionEquipSlots = 0x4000   // agent -> plugin
CompanionEquipSlotStride  = 0x30     // 3 resrefs x 16 ascii bytes
```

Keyed by **presence slot index** (0..3, `PartyPresenceCapacity` — the same index
`OffsetPartyPresenceSlots` already uses, i.e. the position in `PartySnapshotPacket.Members` the
host sent it at), **not** NPC roster slot — a separate, parallel array so the existing
0x28-byte movement layout at `OffsetPartyPresenceSlots` does not have to move. `TotalSize` grew
`0x4000` → `0x4100` to hold it (the free tail after the party-roster-body-identity region left
only 0x90 bytes, not the 0xC0 needed). `SharedBlock.Version` bumped 53 → 54 (both halves —
`PluginHost.cs`'s `_block->Version` and `KotorGameBridge.cs`'s `PluginSignature` literal — kept
in sync by `ItemHarnessTests.TheBlockVersionIsTheSameNumberInBothHalves` and
`TransitionMovieTests.TheBlockVersionMovedInBothHalves`, both updated and green).

`KotorGameBridge.TryWritePartyMembers(members, applyEquipment)` writes the three resrefs per
presence slot every pass `applyEquipment` is true, independent of the existing position
dirty-check (a re-equip with an unchanged position must still update the block).
`PartyPresenceController.ApplyCompanionEquipment` (plugin side) reads them back, hashes each
16-byte buffer (FNV-1a, the identical scheme `PhantomController.ApplyEquipment` already uses —
there is no managed string on that side of the process boundary), and calls
`EquipItemFromTemplate` only when the hash changed and the value is nonempty — the decision
factored into a pure, unit-tested class, `EquipApplyDecision.ShouldApply(length, desiredHash,
lastAppliedHash)` (`src/KotorCoop.Plugin/EquipApplyDecision.cs`, compile-included into
`KotorCoop.Tests` per this project's own trick for Odyssey-free plugin logic). One equip per 30
frames, the same cooldown the phantom channel already uses (an equip is as heavy as a spawn).

Applied **unconditionally at the top of `DriveOne`**, before the `companion_local_combat`
release gate: a companion has to already be wearing the right gear before it is handed to its
own engine AI, because once released this controller stops touching the body entirely
(`CompanionLocalFightAction.StayReleased`'s own "hands off" behaviour) — there would be no later
chance to fix a still-stale equip.

Gated independently by **two** copies of the same coop.ini key, the identical pattern
`movie_relay` already uses: `AgentSettings.SyncCompanionGear`/`CoopConfig.CompanionGearSync`
(agent-side: whether the host reads/publishes and whether a client hands what it receives to
the plugin at all) and `PluginConfig.ReadBool("companion_gear_sync", false)` (plugin-side:
whether the actual engine call fires). Both default OFF; either one being off is enough to stop
the engine write.

### 2.5 Load-bearing constants (VERIFIED, reused from `item-identity.md`/`Odyssey.Equipment.cs`)

| | |
| --- | --- |
| `creature->inventory` | `creature+0x0A2C` (`CSWInventory*`) |
| Inventory slot offsets (body/right/left) | `+0x08` / `+0x14` / `+0x18` (`InvSlotBody`/`InvSlotRightWeapon`/`InvSlotLeftWeapon`) |
| `CreateItemOnObject` VM routine | `0x0052F570` |
| `ActionEquipItem` VM routine | `0x005355F0` |
| `EquipItemFromTemplate` | `Odyssey.Equipment.cs` — VM dispatch pair, caller-object redirect |
| `SharedBlock.OffsetCompanionEquipSlots` | `0x4000`, stride `0x30`, block `TotalSize` `0x4000` → `0x4100` |
| `SharedBlock.Version` | 53 → 54 |

---

## 3. What was built (summary)

- **Protocol**: `PartyMemberState`'s three new equip fields (+codec); `PacketType.CompanionXpSnapshot`
  (51) / `CompanionXpReport` (52) + their packets and codec.
- **Session**: `IPartySession`'s four new companion-XP members (default no-op bodies, matching
  every other optional channel in that interface); `CoopHost.CompanionProgression.cs` /
  `CoopClient.CompanionProgression.cs` (store-and-broadcast / report-and-mirror, no merge logic
  of their own); `CompanionExperienceMerge.cs` (the pure max-merge rule);
  `Pumps/PartyPump.PumpCompanionExperience` (host merge + apply + publish, client report +
  mirror); `PartyPump.PumpParty` extended to pass `settings.SyncCompanionGear` through to the
  bridge on both branches; `AgentSettings`/`CoopConfig` gained `SyncCompanionXp`/
  `CompanionXpSync` and `SyncCompanionGear`/`CompanionGearSync` (both OFF), wired in
  `AgentComposition.cs`.
- **Memory**: `IPartyBridge`'s new default-bodied members;
  `KotorGameBridge.TryReadCompanionExperience`/`TryWriteCompanionExperience`;
  `KotorGameBridge.ReadPartyMembers(bool includeEquipment)` /
  `TryWritePartyMembers(members, bool applyEquipment)` overloads.
- **Plugin**: `SharedBlock.Party.cs`'s `OffsetCompanionEquipSlots` region (`TotalSize` grown to
  `0x4100`, `Version` 54); `SharedBlockView.Combat.cs`'s `GetCompanionEquipResref`;
  `PartyPresenceController.ApplyCompanionEquipment` + `EquipApplyDecision.cs` (the pure
  diff-detection rule).
- **coop.ini**: `companion_xp_sync=0`, `companion_gear_sync=0` (both new, both OFF), documented
  in `docs/CONFIG.md` and `profiles/coop.ini.example`.
- **Tests**: `CompanionStateSyncTests.cs` (codec pinning for both new packets and the extended
  `PartySnapshot`; `CompanionExperienceMerge` pure-rule cases — adopt-only-present, max on
  conflict, never invents an absent slot, independent per-slot; `EquipApplyDecision` pure-rule
  cases — skip empty, skip unchanged, apply new, apply first-ever; `AgentSettings`/`CoopConfig`
  default-off assertions; `PartyPump`-level capability-gating and settings-gating tests through
  `CoopAgent.PumpOnce`; the anti-echo structural test). `SessionIntegrationTests.cs` gained two
  real-socket loopback tests: `CompanionEquipmentRidesThePartySnapshotHostToClient` and
  `CompanionExperienceMirrorsFromHostAndAPeerReportReachesTheHost`, both using real `CoopHost`/
  `CoopClient` instances over loopback UDP. `dotnet test tests/KotorCoop.Tests` (Debug, no
  filter): **2003 passed, 0 failed** (was 1978 before this round's 25 new tests). Plugin builds
  clean with `build.ps1 -WarnAsError`.

## 4. VERIFIED vs NEEDS-LIVE, in one place

**VERIFIED** (read from the binary/symbol DB, or asserted by a passing test): every offset and
engine call in §1.4/§2.5 (all reused from `progression-sync.md`/`item-identity.md`, which
independently verified them for the PC and the phantom); `DistributeExperience` granting full
XP to every active-slot member; the codec round-trips and the pure-rule behaviour
(`CompanionExperienceMerge`, `EquipApplyDecision`); the two loopback session tests (real
sockets, real `CoopHost`/`CoopClient`, both channels observed crossing the wire and mirroring
correctly); the block-version-bump discipline (both halves agree, both existing pin tests
green); the plugin builds clean.

**NEEDS-LIVE** (reasoned, not yet run against the real game — both flags stay OFF for this
reason):

1. Whether a companion's `stats+0x068` genuinely tracks identically on both instances after a
   real, ordinary kill (the theoretical basis for "kill XP does not need this channel" —
   §1.2), or whether some path this document did not trace grants XP differently for a
   companion than for the PC.
2. Whether `EquipItemFromTemplate` behaves for a real, live companion body exactly as it does
   for a phantom — no reason to expect otherwise (the call takes a bare object id), but never
   observed.
3. Whether the plugin's own equip cooldown (30 frames) and the agent's own dirty-check-free
   every-pass write interact cleanly with `companion_local_combat`'s own release timing — the
   equipment write happens on the same frame pass as the release-gate check, and the two have
   never run together live.
4. Whether writing a companion's equip resrefs while `companion_local_combat` has ALREADY
   released it (mid-fight) causes any visible hitch — the design applies gear unconditionally
   every pass regardless of release state (§2.4), on the theory that a hash-gated no-op costs
   nothing once the gear has converged, but this has not been watched live during an active
   release.
5. `TryWriteAsciiString`'s exact truncation/padding behaviour for a resref at the 16-byte
   boundary has not been re-verified for this new call site specifically (it is the same
   helper the PC's own equipment channel already uses in production, so low risk, but not
   re-checked here).

## 5. Live test plan (for whoever runs the next rig pass)

Both flags default OFF; the following is what turning them on and observing would need,
mirroring the recipe `party-reconciliation.md`'s own `companion_local_combat` live passes
already used successfully.

**Companion XP** (`companion_xp_sync=1` on both instances):

1. Recruit a companion on both instances (roster in sync — `party_roster_sync` already
   handles this). Note the companion's starting XP via a `resolve`/`items`-style console read
   on both sides.
2. Have the HOST's player land a kill while the companion is in the active party. Read the
   companion's XP on BOTH instances immediately after. **Expected** (per §1.2): both already
   agree, with or without the flag — if they do not, that is evidence the "kill XP already
   agrees" theory is wrong and the channel is doing more real work than expected.
3. Run a conversation or stealth award that grants XP to the party from ONE instance only
   (a scripted `GivePlotXP`), then read the companion's XP on both sides. **Expected**: without
   the flag, they diverge; with it, they converge to the higher value within one heavy-pump
   cycle (~1 s).
4. Confirm a client's own locally-granted script XP is not lost — the host must end up at
   `max(host, client)`, never reverting a client's own higher figure.

**Companion equipment** (`companion_gear_sync=1` on both instances, alongside
`companion_local_combat=1`/`companion_local_combat_range` generous enough to release a
companion, per `party-reconciliation.md`'s own recipe):

1. On the HOST, re-equip a companion with a REAL, visually distinct weapon (a `.uti` that
   exists in this install — see `item-identity.md` §10 for the "does not check the blueprint
   exists" trap). Confirm the CLIENT's mirrored copy of that companion visually updates within
   one `party_fast_pump` cycle, independent of `companion_local_combat`.
2. Stage the release: get the client's player and the companion both within
   `companion_local_combat_range` of a `local_combat`-excluded enemy, per the existing recipe.
   Confirm the released companion's swing animation/weapon model matches what was just
   equipped in step 1, not whatever it wore before.
3. Re-equip the companion again on the host WHILE it is released on the client. Confirm the
   gear updates on the next pass (the write is unconditional, per §2.4) without visibly
   interrupting the companion's attack loop.
4. Confirm anti-echo: with the client's own copy of that companion carrying (deliberately)
   different local equipment than the host's real one, confirm the CLIENT never overwrites the
   HOST's copy — `party drive: companion 0x... equips ...` should only ever appear in the
   CLIENT's log, never the host's, for this companion.

## 6. Live-verified 2026-08-27

Two-instance rig (`tools/demo.ps1`), `end_m01aa`, both `coop.ini`s carrying
`companion_xp_sync=1`. A new pair of dev-only staging cheats was built to reach this test at
all — `grantxp <id|me> <amount>` (pokes `CSWSCreatureStats::experience` on any creature,
simulating a script-granted award) and `equip <id|me> <resref> [slot]` (calls
`Odyssey.EquipItemFromTemplate` on any creature) — see `docs/CONSOLE.md`. Trask (party-roster
slot 0, live object id `0x127` on both instances — the same save loaded both sides) was the
test companion.

### Companion XP — PASS

`grantxp 0x127 0` (a zero-delta read) confirmed both instances already agreed at a 3081 XP
baseline. §1.2's "kill XP already agrees without the channel" theory was **not exercised**:
`slay` (the only kill primitive this codebase has) calls `Odyssey.KillCreature` directly and
produced no XP change at all on either side — real kill-XP grants run through the game's own
combat/death scripts, which a direct engine kill bypasses, so this specific claim is still
INFERRED, not measured, and no automated primitive in this codebase can reach it (a real
combat kill is not currently stageable without playing the fight by hand).

The channel's actual purpose — a script-granted award converging — is a clean pass:

1. `grantxp 0x127 500` on the HOST → host reads 3081 → 3581.
2. Within 2 s, the CLIENT independently read 3581 → 3581 (converged, no action taken there).
3. `grantxp 0x127 300` on the CLIENT → client reads 3581 → 3881.
4. Within 2 s, the HOST converged to 3881 (not reverted to its own 3581) — `max(host,
   client)`, exactly as designed. Both sides confirmed at 3881 afterward.

**`companion_xp_sync` default flipped ON** (`coop.ini`/`AgentSettings`/`CoopConfig`/
`profiles/coop.ini.example`/`docs/CONFIG.md`) on the strength of this pass — the merge logic
was already unit-tested and the live pass confirms it converges correctly in both directions
within one heavy-pump cycle. The kill-XP claim (§1.2) remains open and does not block this,
since the channel does not need it to be true — it only needs script-granted XP to converge,
which it does.

### Companion equipment — FAILED, real bug found (not fixed)

Enabling `companion_gear_sync=1` and running `equip 0x127 g_w_lghtsbr01 4` on the HOST found a
genuine bug, not the "expected to work identically, never observed" gap §2.2 predicted:

**The item does not land on the companion at all — it lands on the LOCAL PLAYER.** `items`
showed `0xA g_w_lghtsbr01 held by 0x9`, and `resolve 0x9` confirmed `0x9` is the PC (vtable
`0x7470C8`, the exact PC-identification signature `progression-sync.md` uses), not `0x127`
(Trask). `EquipItemFromTemplate`'s caller-object redirect
(`ImplHasCallerObject`/`ImplCallerObjectId`, set to the target and restored in a `finally`
immediately after the call returns) is verified live for a PHANTOM, but a live,
AI-active party member appears not to honour it the same way — the leading hypothesis,
**not confirmed**, is that `ActionEquipItem` QUEUES the actual equip rather than running it
synchronously despite `bInstant=1`, so by the time the queued action actually executes (a
later frame), the redirect has already been reverted to whatever the ordinary caller context
is (the local player). Waited over a minute in the client's plugin log for any
`party drive: companion 0x127 equips ...` line — none ever appeared, consistent with the item
never having reached the companion's inventory in the first place.

**`companion_gear_sync` stays OFF.** Shipping it as-is would silently hand a re-equipped
companion's gear to the PLAYER instead — a visible, confusing bug (the player's own weapon
changes for no reason the player did), worse than the shipped "stale companion gear" gap it
was meant to close. This needs real RE work (confirming or refuting the action-queue
hypothesis, likely against `CSWSObject::AddAction`/the action-queue drain, not attempted this
round) before it can be tried again. Left for a future round; `companion-state-sync.md` §2.2's
"NEEDS-LIVE for a companion body specifically" is now a confirmed live FAILURE, not an open
question.
