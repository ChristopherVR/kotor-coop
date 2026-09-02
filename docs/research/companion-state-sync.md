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

---

## 7. Root cause of the live FAILURE, and a fix (worktree round, base commit `c9d08bb`)

This worktree branched from `main` before the round that built the `equip`/`grantxp` dev
verbs and ran the live pass recorded there (`main`'s `docs/live-verification.md`, "Rig pass
2026-08-27 round 2" — 23 commits ahead of this worktree's base at the time of writing; see
`git log HEAD..main`). That live pass's own verdict, quoted from `main`: **"a real bug —
equip lands on the player, not the companion"**, with the operator's own leading (explicitly
"not confirmed") hypothesis being that `EquipItemFromTemplate`'s caller-object redirect
"doesn't hold for a live, AI-active party member the way it does for a phantom." No rig was
available this round either, so this section is disassembly-only, same tag discipline as the
rest of this document.

### 6.1 The caller-object redirect is NOT the defect — VERIFIED by disassembly

Full static disassembly this round of both halves of `Odyssey.EquipItemFromTemplate`
(`src/KotorCoop.Plugin/Odyssey.Equipment.cs`):

- **`CreateItemOnObject`'s VM handler, `CSWVirtualMachineCommands::ExecuteCommandCreateObject`
  is the wrong name — the actual routine is at `0x0052F570`.** It pops its three VM-stack
  arguments (`sTemplate`, `oTarget`, `nStackSize`) and resolves `oTarget` — the explicit
  argument our code pushes, `creatureObjectId` — via `CServerExoApp::GetObjectArray` +
  `CGameObjectArray::GetGameObject` (`0x0052F624`/`0x0052F62B`), casts it to a creature via its
  own vtable `+0x30` ("AsCreature," the same cast this codebase's own `QueueAttackAction`
  already documents using), and — when that cast succeeds — calls
  `CSWSCreature::AcquireItem` (`0x005158E0`) **on that resolved target**, putting the new item
  in ITS inventory. **Nothing in this function reads `CSWVirtualMachineCommands::
  has_caller_object`/`caller_object_id` (`implementer+0x04`/`+0x08`) at all.** `oTarget`
  resolution is entirely independent of VM caller-object state. VERIFIED.

- **`ActionEquipItem`'s VM handler, `CSWVirtualMachineCommands::
  ExecuteCommandActionEquipItem` (`0x005355F0`, confirmed by name in `tools/kotor1_0_3.db`),
  reads `caller_object_id` (`this+0x08`, matches the symbol DB's own `offsets` table:
  `has_caller_object` +0x004, `caller_object_id` +0x008) into a register at the very top of
  the function** — before either VM-stack pop — resolves it through the identical
  `GetObjectArray`/`GetGameObject`/AsCreature-cast sequence, and (gated on `has_caller_object
  == 1`, `this+0x04`, checked at `0x0053567F`) calls `CSWSCreature::AddEquipItemActions`
  (`0x004F0420`) **on that resolved creature** — the SAME object our C# code writes into
  `caller_object_id` immediately before the dispatch, restored in a `finally` only after the
  call returns. VERIFIED: the redirect correctly targets the companion in both halves, with no
  window in which a stale/reverted value could be read instead.

This directly refutes the live pass's own leading hypothesis. The redirect mechanism itself is
sound for a live party member exactly as it is for a phantom.

### 6.2 The real defect: `ActionEquipItem` queues, and nothing ever drains the queue for a mirrored companion

`CSWSCreature::AddEquipItemActions` (`0x004F0420`) does not equip anything itself — for the
ordinary (non-head-slot) path it calls `CSWSCombatRound::AddEquipAction` (`0x004D3C30`) on
`creature+0x9C8` (the creature's own combat round). **`AddEquipAction`, disassembled in
full this round, allocates an action node (action-type byte = 6) and appends it to
`round+0x9B0`** — the exact "pending-action list" `Odyssey.Combat.Attack.cs`'s own
`QueueAttackAction` already documents at length for the attack case, where draining it needs
the round's own processing gate (`round+0x958`) armed via `SetRoundTimerActiveFn`/
`SetRoundDelayFn`/`StartCombatRoundFn` — calls `QueueAttackAction` makes explicitly and
`AddEquipAction`'s own call chain (which reaches only `CSWSCombatRound::
DecrementRoundLength`, `0x004D3440`) never does. **`EquipItemFromTemplate` never arms the
round either.** So the queued equip node has no confirmed path to ever being processed for a
creature whose round the engine's own AI never separately armed.

Separately: `Odyssey.ClaimCreature` (called by `PartyPresenceController.DriveOne` every pass
for a mirrored companion, immediately after `ApplyCompanionEquipment`) calls
`ClearAllActionsFn` (`CSWSObject::ClearAllActions`, `0x004CCD80`), which itself calls
`creature->combatRound->ClearAllAttacks` (`0x004D37E0`, same `+0x9C8` offset). Disassembled
this round: `ClearAllAttacks` loops only the round's five embedded `0x14C`-byte maneuver
sub-structures (`round+4+i*0x14C`) — it does **not** touch `round+0x9B0` directly, so the
queued equip node is not itself wiped by the claim. What IS true, and load-bearing: a
phantom is never claimed at all (`PhantomController.OnFrame` calls
`ApplyEquipment`/`DrivePhantom` every step and never calls `Odyssey.ClaimCreature` anywhere in
`PhantomController.cs`/`PhantomController.Drive.cs`), so nothing about its round is ever
touched post-spawn either way, while a live party member's round is repeatedly re-entered by
`ClaimCreature` a few times a second. Which exact downstream step (an un-armed round's
processing branch, or some other per-pass write this round's static pass did not trace) is
what actually stops the node from ever being drained on a companion specifically was not
pinned to a single instruction — **INFERRED, not fully closed** — but the queue/no-arming gap
itself (§7.2's first paragraph) is VERIFIED and is, on its own, sufficient reason the equip
never lands: nothing in this call chain ever asks the engine to process the pending list at
all, on any creature, phantom included, unless some other system happens to arm the round
first. Whether that "something else" exists for a real, previously-fought companion but not
for a fresh phantom was not settled this round.

⚠ **What this section does NOT explain**: the live pass's specific observation, "the item
lands on the LOCAL PLAYER" (`items` showed the new weapon `held by 0x9`, confirmed to be the
PC). §7.1 shows both `CreateItemOnObject` and `AddEquipItemActions`/`AddEquipAction` correctly
resolve and stay scoped to the companion object throughout — nothing in the disassembled call
chain re-derives "self" as the player at any point. Two candidate explanations, neither
confirmed: (a) the SAME live round independently found and fixed a `KotorGameBridge.Items.cs`
item-tag-cache bug ("a missed item-tag lookup was permanently blacklisted instead of retried")
that could plausibly have misattributed the `items` diagnostic's possessor read during that
same session; (b) a mechanism downstream of `AddEquipAction` this round's static pass did not
reach. The live test plan below is written to settle this directly rather than guess further.

### 6.3 The fix: bypass the queue entirely

`Odyssey.EquipItemFromTemplateDirect` (new, `src/KotorCoop.Plugin/Odyssey.Equipment.cs`)
keeps `GiveItemToInventory` (`CreateItemOnObject` alone — already VERIFIED target-based,
unchanged) to create the item on the companion, then equips it through
`CSWSCreature::EquipItem` (`0x004FEB90`, confirmed by name in the symbol DB, `__thiscall`,
param size 20 bytes = 5 dwords) called **directly** on the resolved companion — the same
SYNCHRONOUS primitive the workbench's install path and the AI's own
`ForceEquipClothing`/`EquipMostDamagingMeleeWeapon`/etc. helpers call (15 xrefs to
`0x004FEB90`, all inside those two families; VERIFIED). No action queue, no combat round, no
arming needed — the call runs `CanEquipWeapon`, unequips whatever already occupies the slot,
applies the stat/appearance changes and broadcasts the client update, all inside the one call
(disassembled in full this round). This sidesteps §7.2's whole open question rather than
resolving it: there is nothing left pending afterward for `ClaimCreature` — or anything else —
to race.

The internal slot argument is not the NWScript 1/4/5 convention `ActionEquipItem` uses; it is
the `CSWInventory` byte offset divided by 4 (body `+0x08` → 2, right weapon `+0x14` → 5, left
weapon `+0x18` → 6 — VERIFIED offsets, reused from §2.5/`item-identity.md`). The body mapping
(slot 2) is cross-checked live-in-the-binary this round at `CSWSCreature::
ForceEquipClothing`'s own call site (`0x00501D69`, a literal `push 2`); the two weapon slots
were not independently confirmed against a literal (both weapon-equip call sites disassembled,
`0x00501FC2`/`0x005022D8`, push a register-carried value from their own caller rather than a
constant) — **INFERRED from the verified offset arithmetic only, NEEDS-LIVE**. Pulled into a
dependency-free pure rule, `EquipSlotMap` (`src/KotorCoop.Plugin/EquipSlotMap.cs`, compiled
into `KotorCoop.Tests` the same way `EquipApplyDecision` already is), so the mapping itself is
unit-tested independently of the engine call that consumes it
(`CompanionStateSyncTests.EquipSlotMap*`).

`PartyPresenceController.ApplyCompanionEquipment` now calls `EquipItemFromTemplateDirect`
instead of `EquipItemFromTemplate`. **`PhantomController.ApplyEquipment` is unchanged** — it
still calls `EquipItemFromTemplate`, the proven-live-for-a-phantom path — so this fix carries
zero regression risk to the shipped phantom equipment channel. `companion_gear_sync` stays OFF
(unchanged default); this is a NEEDS-LIVE fix, not a verified one.

⚠ **`main`'s dev verbs are out of reach from this worktree.** The `equip`/`grantxp` console
verbs, `CheatController.Progression.cs`, and this document's own `§6` "Live-verified
2026-08-27" section that recorded the original failure all live only on `main` (23 commits
ahead of this worktree's base at the time of this round; `CheatController.Party.cs` in this
worktree has no equip-related content at all). Whoever merges this fix forward should also
point `main`'s `CheatController.Progression.cs::Equip()` at
`Odyssey.EquipItemFromTemplateDirect` instead of `Odyssey.EquipItemFromTemplate`, so the dev
verb exercises the same path the real `companion_gear_sync` channel now uses — otherwise the
verb would keep reproducing the OLD (queued, un-armed) failure while the shipped channel uses
the new one.

### 6.4 Live test plan (precise, for the next rig pass — re-runs the exact failing scenario)

1. Build and install both instances (`src/KotorCoop.Plugin/build.ps1 -Install` on both, or via
   `demo.ps1`'s own install step — a stale plugin silently re-tests the OLD code, see
   "demo.ps1 does not deploy the plugin").
2. On the HOST, with `companion_gear_sync` still OFF, use the direct primitive through
   whatever staging path is available on the branch under test (the `equip` dev verb once
   forward-ported per §7.3's note above, or a temporary local call) targeting Trask
   (party-roster slot 0) with a real, visually distinct `.uti` — e.g. `equip <trask-id>
   g_w_lghtsbr01 4` (right-weapon slot; internal slot 5 per `EquipSlotMap`).
3. **Watch for a crash or hang first.** `CSWSCreature::EquipItem` has never been called from
   this codebase before; if the engine's own internal checks (`CanEquipWeapon`, the stats
   recompute) assume state this call path does not set up, a bad call is more likely to
   surface as an access violation than a silent no-op. Confirm the plugin log's own
   `equip: ... equipped on 0x...` line, then confirm the process is still alive.
4. `items` / `resolve <id>` (or the equivalent live memory read): confirm the new item's
   possessor is the COMPANION's object id, not the PC's — this is the exact assertion the
   prior round's `items` output got wrong. Confirm the item is actually in the CORRECT
   internal slot (right hand, not the body slot) — visually, and via
   `KotorGameBridge.ReadEquippedResref` at the `+0x14` offset if a console primitive for that
   exists on the branch under test.
5. Repeat for the body slot (`nwscriptSlot=1` → internal 2, the one literal-confirmed mapping)
   and the left-weapon slot (`nwscriptSlot=5` → internal 6, the other un-confirmed mapping) —
   if either produces a visibly wrong result (item in the wrong slot, or a crash), `EquipSlotMap`
   is the first place to re-derive from a live probe rather than the offset arithmetic alone.
6. Only once 3-5 are clean: flip `companion_gear_sync=1` on both instances and re-run this
   document's existing §5 "Companion equipment" live test plan (re-equip on the host, confirm
   the client's mirrored copy updates, confirm the gear survives a `companion_local_combat`
   release, confirm anti-echo) — that plan's steps are unchanged by this fix; only the
   underlying primitive `ApplyCompanionEquipment` calls changed.

## 8. Live pass (2026-08-28): §7.3's fix confirmed for the direct primitive — the item lands
## on the companion, not the player — but the auto-sync channel itself found a NEW live gap

Two-instance rig (`tools/demo.ps1`), `END_M01AA`, Trask (party-roster slot 0) confirmed live
object id `0x127` on both instances again this round (`leader 0` — a dev-only, local verb —
reports `control switched to party slot 0 (0x127)`, then `leader pc` restored control before
anything else ran). `pwsh src\KotorCoop.Plugin\build.ps1 -Install -WarnAsError` on both game
directories first (hashes of the deployed `dinput8.dll` on both installs confirmed byte-identical
to the fresh build).

### §6.4 steps 1-5 (the direct primitive, `companion_gear_sync` still OFF): PASS, live-confirmed

`equip 0x127 g_w_lghtsbr01 4` (right weapon) on the HOST — no crash, `equip:
'g_w_lghtsbr01' equipped on 0x127 slot 4` in the plugin log, and `items` confirmed the NEW item
(`0x132`) `held by 0x127`, not the PC (`0x9`, which still only held its own original
`0xA g_w_lghtsbr01`) — the exact assertion the prior round's own live FAILURE got wrong.
Repeated for the body slot (`equip 0x127 g_a_class4001 1` -> new item `0x133 held by 0x127`) and
the left-weapon slot (`equip 0x127 g_w_lghtsbr02 5` -> new item `0x134 held by 0x127`) — both
clean, no crash, both correctly possessed by the companion. §7.3's fix
(`Odyssey.EquipItemFromTemplateDirect`, `CSWSCreature::EquipItem` called directly instead of
queuing through `ActionEquipItem`) is confirmed working for a live, AI-active party member, not
just a phantom, closing the "NEEDS-LIVE" tag §7.3 shipped it under. `CheatController.Progression
.Equip()` (the console verb) already calls the fixed primitive on `main`, so no forward-port was
needed this round.

### §5 (the auto-sync channel, `companion_gear_sync=1` on both instances): NOT clean — a real,
### reproducible propagation gap, distinct from the original "lands on the player" bug

Restarted the rig with `companion_gear_sync=1` written into both `coop.ini`s first (the flag is
read once at agent start, so this needs a fresh `demo.ps1`, not a mid-session toggle). Confirmed
the channel is wired correctly for gear present **at connect time**: the CLIENT's plugin log
showed, within the first second of the session, `party drive: companion 0x127 equips
g_a_clothes01 (slot 1)` (Trask's starting body slot, applied to the mirrored copy, matching the
host's real gear) — the whole pipeline (host reads gear -> publishes -> client writes to its
plugin block -> `PartyPresenceController.ApplyCompanionEquipment` calls the same
`EquipItemFromTemplateDirect` fix -> succeeds) genuinely works for gear the companion is already
wearing when the client joins.

**A re-equip made mid-session did NOT propagate, observed over an 8-minute window.**
`equip 0x127 g_w_vbrdblswd01 4` on the HOST at `04:33:48` (confirmed live: `items` on the host
showed the new item `0x133 g_w_vbrdblswd01 held by 0x127`, so the underlying engine-side equip
genuinely succeeded, same as the direct-primitive test above). The CLIENT's plugin log
(`kotorcoop-general.log` and `kotorcoop-plugin.log`, checked repeatedly) never showed a second
`party drive: companion 0x127 equips ...` or `... refused ...` line for this weapon at any point
through `04:41` — both `swkotor.exe` processes and both `KotorCoop.Agent.exe` processes stayed
alive and `Responding=True` throughout (the client's own gait/animation lines kept appearing every
few seconds, so the plugin's per-frame loop was genuinely still running — this is not a hang).
**This is a real propagation gap in the auto-sync channel, separate from and downstream of the
already-fixed "equip lands on the player" bug**: the primitive that APPLIES a change (confirmed
working, above) never got asked to, because the change never reached the client's shared block in
an applicable form.

**Root cause not isolated this pass** (no time for a debugger attach); the leading, unconfirmed
suspect is the AGENT-side item-tag cache `KotorGameBridge.Items.cs`'s `ReadEquippedResref` uses to
turn a companion's raw equipped-slot OBJECT ID into a resref string on the HOST. That cache
resolves a freshly-created item's id by scanning committed memory for the `CSWSItem` vtable, rate
limited to one rebuild scan per `ItemTagRebuildMs` (15 seconds) **regardless of how many lookups
miss** — a deliberate anti-thrash design (this same file's own doc comment records the OPPOSITE
failure mode, a permanent per-id blacklist, being removed for exactly this reason on an earlier
round). A NEWLY EQUIPPED item is, by construction, an id the cache has never seen: if the miss
happens to land inside the 15-second cooldown window since the last rebuild (plausible here, since
the client's own connect-time equipment read would have primed a rebuild only ~58 seconds before
this test's `equip` call), the read returns `string.Empty` for a full cooldown cycle, and an empty
resref is `PartyMemberState`'s own documented "leave it alone" convention
(`EquipApplyDecision.ShouldApply`'s `length > 0` gate) — meaning the host would keep publishing
its OLD equipped-gear reading indefinitely rather than eventually catching up once the cooldown
lapses. This is a plausible mechanism, not a confirmed one: it does not by itself explain why the
gap persisted for the FULL 8 minutes (several rebuild windows) rather than resolving within 15-30
seconds once the cooldown first lapsed, so something else may also be involved (the AGENT-side
`_appliedProgression`-style caches were not audited this round for a "successfully read once,
never re-read" bug of their own). **Flagged for the next round to attach a debugger or add a
throwaway diagnostic log (the same technique `minigames.md`'s 2026-08-28 pazaak round used to find
its own range-guard bug) rather than guess further.**

### A second, independent bug found along the way: exact-16-character resrefs are silently
### truncated crossing the wire/shared-block boundary

At connect time, the CLIENT's plugin log recorded `party drive: g_w_blstrpstl00 refused for
companion 0x127 (failure 8)` — Trask's real starting sidearm is `g_w_blstrpstl001` (16
characters, VERIFIED by counting), but the name that reached the client's `ApplyCompanionEquipment`
is missing its trailing `1`. `BlockCompanionEquipResrefLength` (`KotorGameBridge.Party.cs`) and
`ProtocolConstants.MaxResrefLength` (the wire codec, `PacketCodec.Party.cs`/`PacketWriter.cs`) are
both exactly `16` — a resref that is itself 16 characters long has no room left for the buffer's
own null terminator once it crosses either boundary, and whichever write path is doing the
truncation (not isolated this pass — either `TryWriteAsciiString`'s own 16-byte contract or the
wire codec's `WriteString(_, 16)`) drops the last character rather than the byte that should have
been the terminator. The truncated name is not a valid blueprint resref, so
`EquipItemFromTemplateDirect`'s own `CreateItemOnObject` half correctly refuses it (`failure 8`) —
a fail-safe outcome, not a crash, but a real, reproducible bug: **any companion gear whose resref
is exactly 16 characters can never be synced correctly** by this channel as it stands. Not fixed
this pass (out of scope for a live-verification round); left for whoever next touches this
channel, alongside the propagation gap above.

### Verdict: `companion_gear_sync` stays OFF; the direct-primitive fix is confirmed and durable

The fix `§7.3` shipped is real and now live-confirmed for a live, AI-active companion body across
all three equip slots — that half of this round's task is closed. The auto-sync channel built on
top of it is not yet safe to ship: it correctly seeds a companion's STARTING gear on connect, but
a live gear CHANGE was observed, live, to not propagate within an 8-minute window, and a second,
independent truncation bug affects any exactly-16-character resref regardless of the propagation
gap. `companion_gear_sync` stays at its existing default (`0`, OFF) in `coop.ini`/
`AgentSettings`/`CoopConfig`/`profiles/coop.ini.example`/`docs/CONFIG.md` — no default was
flipped this round. `dotnet build KotorCoop.slnx -warnaserror` and `src/KotorCoop.Plugin/
build.ps1 -WarnAsError`: both clean (no source changed this pass — live rig verification and a
build-script environment fix only, see `src/KotorCoop.Plugin/build.ps1`'s own `$env:Platform`
comment for the latter, unrelated to this feature).

---

## 9. Both §8 bugs fixed (2026-08-28, worktree round, disassembly/static only — no rig)

Both of §8's findings are code-inspection-confirmed root causes with in-tree fixes this round.
No rig was available; every VERIFIED-by-test claim below is a passing unit/loopback test in
`tests/KotorCoop.Tests`, not a live observation. `companion_gear_sync` **stays OFF** — see §9.3.

### 9.1 Root cause A: the 16-character resref truncation

**Not the wire codec.** `PacketWriter.WriteString`/`PacketReader.ReadString`
(`src/KotorCoop.Protocol/PacketWriter.cs:140`/`PacketReader.cs:128`) are length-PREFIXED (one
length byte, then that many bytes) — a 16-character value writes length byte `16` and 16 content
bytes, and the reader's `length > maxLength` guard passes at exactly `16 == 16`. Confirmed by a
new pinning test at all three sizes the task named
(`CompanionStateSyncTests.PartySnapshotCompanionEquipmentRoundTripsAtEveryResrefBoundaryLength`,
1/15/16 chars). This channel was never the bug.

**The real bug: `ProcessMemory.TryWriteAsciiString`**
(`src/KotorCoop.Memory/ProcessMemory.cs:228`, pre-fix), the helper every FIXED-WIDTH shared-block
ASCII field goes through — `BlockCompanionEquipResrefLength`/`BlockEquipResrefLength`/
`BlockItemResrefLength` (all 16, `KotorGameBridge.Party.cs:545` /
`KotorGameBridge.PluginBlock.cs:39,50`), `BlockCheatTextLength` (32), journal tags, browser
entries, and every other `TryWriteAsciiString` call site in `KotorCoop.Memory`. It capped
content at `Math.Min(value.Length, fieldLength - 1)` — reserving the field's LAST byte for a
null terminator that a KOTOR `CResRef` (a bare `char[16]`, up to 16 characters, no terminator
required) never needs. Any value exactly `fieldLength` characters long silently lost its last
character on the way into the block. `ProcessMemory.TryReadAsciiString`
(`ProcessMemory.cs:172`) was never the problem — it already falls back to the full field width
when no null byte is found (`end < 0 → end = maxLength`), which is exactly why the live symptom
was a truncated STRING (`g_w_blstrpstl00`) rather than a crash or a read failure: the write
dropped a byte, and the correctly-lenient read faithfully reported what was actually there.

**Fix**: `TryWriteAsciiString` now writes up to the full `fieldLength` bytes
(`Math.Min(value.Length, fieldLength)`); the buffer is a freshly allocated, zero-initialised
array, so anything past the written content is still correctly null-padded for a shorter value.
No other file needed to change — `TryReadAsciiString`, the plugin's own C-side resref readers
(`PartyPresenceController.ApplyCompanionEquipment`'s `while (length < 16 && resref[length] != 0)`,
`PhantomController.Drive.cs`, `Odyssey.CreatureAnim.cs`, all already capped at the true field
width `16` with no `-1`), the `ItemTemplateSpawnHook` ring (`ResRefBytes = 16`, copies exactly 16
raw bytes both into its own capture ring and `SharedBlockView.PublishItemTemplateSpawn`'s shared-
block ring — never null-terminator-aware), and `KotorGameBridge.ItemTemplateSpawns.cs`'s drain
(reads via `TryReadAsciiString`) were all already correct. **Audited and left alone**:
`CheatController.*.cs`'s `while (length < SharedBlock.CheatTextLength - 1 && text[length] != 0)`
(`CheatTextLength = 32`) reads one byte short of that field's own 32-byte width — a real
inconsistency with the field's own size, but not a resref-affecting one (every legitimate
`CheatText` payload — a 16-char resref, a tag, a global's name, capped at 22 characters per
`SharedBlock.cs`'s own remark on `OffsetCheatText` — fits comfortably under the 31-byte cap it
actually enforces), so left as a latent, non-triggering oversight rather than touched blind this
round.

**Tests** (new): `CompanionStateSyncTests.PartySnapshotCompanionEquipmentRoundTripsAtEveryResrefBoundaryLength`
(wire, 1/15/16 chars — pins the codec was already correct);
`ExternalTargetTests.FixedWidthResrefFieldsRoundTripAtExactly16BytesWithNoReservedTerminator`
(the actual fix, against a REAL separate process via `ProcessMemory.TryWriteAsciiString`/
`TryReadAsciiString` directly at `fieldLength: 16`, 1/15/16 chars — this test fails against the
pre-fix code, since the 16-char case used to round-trip as 15 characters).

### 9.2 Root cause B: mid-session re-equip never propagating

`PartyPump.PumpParty`'s host branch (`src/KotorCoop.Session/Pumps/PartyPump.cs:20,50`) publishes
`bridge.ReadPartyMembers(settings.SyncCompanionGear)`, which for a companion reads each equip
slot through `KotorGameBridge.ReadEquippedResref` (`KotorGameBridge.Items.cs:38`, pre-fix). That
method — written 2026-08-27, before `_spawnedItemResrefs`/`ItemTemplateSpawnHook` existed
(item-identity.md §12/§13, 2026-08-28) — duplicated only HALF of `TryGetItemResref`'s own
precedence: it went straight to `_itemTags` (the vtable/Tag committed-memory scan) and, on a
miss, was gated to at most one rescan per `ItemTagRebuildMs` (15 seconds) **regardless of how
many lookups miss** — the exact rate-limit `TryGetItemResref` also has, but reached WITHOUT ever
first checking `_spawnedItemResrefs`, the cache `RememberSpawnedItemResref` populates and
`DrainItemTemplateSpawns` feeds the instant the plugin's `ItemTemplateSpawnHook` ring reports a
freshly-created item. A companion re-equip's item id is, by construction, exactly the kind of id
that cache learns about immediately (`EquipItemFromTemplateDirect`'s own `CreateItemOnObject`
call is the SAME VM command the hook watches) — but `ReadEquippedResref` never asked it.

A second, compounding ordering bug: `CoopAgent.PumpOnce` (`src/KotorCoop.Session/CoopAgent.cs`)
called `_bridge.DrainItemTemplateSpawns()` AFTER both of `PartyPump.PumpParty`'s call sites (the
`party_fast_pump` fast path and the `!PartyFastPump` fallback), so even a fixed
`ReadEquippedResref` would have read one tick stale on the exact tick a re-equip's spawn record
first became available — no live-observable gap on its own (party read next tick would still
resolve it, at 30-60 Hz), but backwards from the "feed the cache before the read that consumes
it" ordering this file's own `PumpContainerLoot`/`PumpScriptItemGrants` ordering already
establishes for the identical reason, and worth fixing on the same principle rather than leaving
a one-tick race for later.

Neither `PartyPresenceDirtyCheck.CanSkipWrite` (`PartyPresenceDirtyCheck.cs`, agent-side, gates
the whole-slot block write) nor `EquipApplyDecision.ShouldApply` (plugin-side, gates the actual
engine equip call) has a "stale last-published snapshot" bug — both were checked by inspection:
`PartyMemberState` is a `record struct`, so `CanSkipWrite`'s `candidate == lastWritten` compares
the equip fields too, and correctly does NOT skip a slot whose reported gear differs from what
was last written; `EquipApplyDecision` compares a freshly-computed hash against the plugin's own
per-slot `_appliedEquip` every pass with no snapshot to go stale. The propagation gap was
entirely upstream of both, inside the read that decides WHAT to publish in the first place.

**Fix**: `ReadEquippedResref` now delegates to `TryGetItemResref` (spawn-cache-first, then
Tag-cache, then the same rate-limited rescan) instead of duplicating half of it — made `internal`
(from `private`) for direct testability, the same test-seam pattern
`RememberSpawnedItemResref`/`HasSpawnedItemResref` already use. `CoopAgent.PumpOnce` now calls
`_bridge.DrainItemTemplateSpawns()` once, before either `PartyPump.PumpParty` call site (moved
from after `PumpParty`/`PumpContainerLoot` to immediately after the client control-lock block,
ahead of the "combat path" section), so a re-equip's spawn record is available to the SAME tick's
party read.

**Tests** (new): `CompanionEquipResrefCachePrecedenceTests.AFreshlyEquippedItemResolvesImmediately
FromTheSpawnCache` (self-attached, fabricates a creature/inventory in the test host's own
unmanaged memory; fails against the pre-fix `ReadEquippedResref`, which would have hit the
15-second gate and returned empty — and would additionally have risked triggering a real
committed-memory sweep of the test process, which is why the class deliberately never tests the
uncached-miss path); `CompanionGearLoopbackTests.AMidSessionReEquipOnTheHostReachesTheClient
WithinAFewTicks` (real `CoopHost`/`CoopClient`/`CoopAgent` over loopback UDP, a fake bridge
standing in for `KotorGameBridge` — proves the SESSION layer above the bridge (dirty check, wire,
apply-gating) has no propagation gap of its own once the bridge reports a change, complementing
rather than reproducing the actual root cause, which lives entirely inside the bridge the fake
replaces).

### 9.3 `companion_gear_sync` stays OFF

Both fixes are disassembly/code-inspection-confirmed and unit/loopback-tested, but NEITHER has
been observed against the real game — no rig this round. §8's own live test plan (its numbered
steps 1-4) is unchanged and still what the next rig pass should run, extended with:

1. Re-run §8's step with `g_w_blstrpstl001` specifically (Trask's real starting sidearm, 16
   characters) at CONNECT time — confirm the client's log no longer shows
   `party drive: g_w_blstrpstl00 refused for companion 0x... (failure 8)`, and instead shows the
   full 16-character name applied.
2. Re-run §8's mid-session re-equip step (`equip <trask-id> g_w_vbrdblswd01 4` on the HOST while
   connected) and confirm the CLIENT's log shows the new gear applied within a few seconds — not
   the 8-plus-minute stall (or worse) §8 observed.
3. Confirm `LastItemTemplateSpawnLostCount` (`KotorGameBridge`) stays at 0 across the session —
   a nonzero count would mean the ring's 64-entry capacity is being outrun between drains, which
   would reintroduce a version of the same gap from a different angle.

`dotnet build KotorCoop.slnx -warnaserror` and `src/KotorCoop.Plugin/build.ps1 -WarnAsError`:
both clean. `dotnet test tests/KotorCoop.Tests --no-build`: **2352 passed, 0 failed**, including
this round's 10 new tests (3 wire-codec boundary cases, 3 fixed-width-block boundary cases
against a real external process, 3 cache-precedence unit tests, 1 loopback test).

---

## 10. Live pass (2026-08-28, rig round): §9's two fixes CONFIRMED for the BODY slot — a
## THIRD, distinct bug found for WEAPON slots, upstream of everything §7-9 fixed

Two-instance rig (`tools/demo.ps1`, `END_M01AA`, Trask confirmed live object id `0x127` on both
instances via `partystate.ps1`), both installs freshly `build.ps1 -Install -WarnAsError`ed
(deployed `dinput8.dll` hash-confirmed byte-identical to the fresh build on both sides), both
`coop.ini`s reset from `profiles/coop.ini.example` with `companion_gear_sync=1` written in before
launch. `dotnet build KotorCoop.slnx -warnaserror`, `src/KotorCoop.Plugin/build.ps1 -WarnAsError`
and `dotnet test tests/KotorCoop.Tests --no-build` (2352/2352) all clean before the rig came up.

### 10.1 Item 1 (§9's fixes): the 16-character resref — PASS, live-confirmed both ways

`equip 0x127 g_w_blstrpstl001 4` on the HOST: plugin log read `equip: 'g_w_blstrpstl001'
equipped on 0x127 slot 4` — the full 16 characters, not the `g_w_blstrpstl00` truncation §8
found. The CLIENT's connect-time seed for this exact resref (Trask's real starting sidearm)
showed the full name too: `party drive: companion 0x127 equips g_w_blstrpstl001 (slot 4)`.
`scriptgive g_w_blstrpstl001` on the CLIENT, then waiting for `script_item_sync` to forward it:
the HOST's agent log read `materialised loot: submitting 'g_w_blstrpstl001' for target
0x7FFFFFFF through the giveloot channel` — full 16 characters, confirming the peer receives the
untruncated name too. `TryWriteAsciiString`'s fix (§9.1) is live-confirmed on both the
shared-block and wire paths.

### 10.2 Item 2 (§9's fixes): mid-session propagation — the BODY slot is clean; the WEAPON
### slots are blocked by a THIRD bug, distinct from and upstream of §7-9's fixes

**Body slot: PASS, clean, fast.** `equip 0x127 g_a_class4001 1` then `equip 0x127 g_a_class5001
1` on the HOST, ~5s apart. Both reached the CLIENT's log as a genuine `party drive: companion
0x127 equips ...` line, measured host-call-to-client-log latency **925 ms** and **235 ms**
respectively — well under the "a few seconds" bar §9.3's test plan set. This is the first live
confirmation that §9.1/§9.2's fixes (the `TryWriteAsciiString` full-width write and
`ReadEquippedResref`'s spawn-cache-first read with the corrected drain ordering) actually close
the propagation gap §8 found, for a slot where the underlying engine state genuinely changes.

**Weapon slots: BLOCKED — but not by anything §7-9 touched.** `equip 0x127 g_w_vbrdblswd01 4`,
then `g_w_shortswrd01 4`, then `g_w_lghtsbr01 4` (right weapon, nwscript slot 4 → internal slot
5) each reported success in the plugin log (`equip: '...' equipped on 0x127 slot 4`) but the
CLIENT never showed a new `party drive` line for any of them, reproducing §8's original
symptom. This round did NOT stop at "the cache/wire pipeline is still broken" — it read the
HOST's own `CSWInventory` slot directly (creature `+0xA2C` → inventory pointer → `+0x14` for
right weapon), bypassing every agent-side cache, and found:

- Right-weapon slot (`+0x14`) held object id `0x129` (the FIRST of the four equip calls made
  this round) through all three SUBSEQUENT calls — the slot's raw content never changed, despite
  each call logging success with a different resref and a different freshly-created item
  (confirmed alive and `held by 0x127` via `items 60`, i.e. `GiveItemToInventory`'s half
  genuinely worked every time).
- Left-weapon slot (`+0x18`) stayed `0x0` (empty) even after its OWN first-ever equip call
  (`equip 0x127 g_w_lghtsbr02 5`) reported success — so this is not "works once per slot then
  gets stuck"; the left slot's very first write never landed at all.
- Body slot (`+0x08`), by contrast, was independently re-equipped twice in a row this same
  session (`0x128 → 0x134 → 0x135`, both changes landing immediately) with zero failures.

**Root cause not isolated this round — a real engine-call defect, not a cache/wire issue.**
`EquipItemFromTemplateDirect` (`src/KotorCoop.Plugin/Odyssey.Equipment.cs:305-343`) calls
`EquipItemFn(creature, internalSlot, item, 1, 0, 0)` (`CSWSCreature::EquipItem`, `0x004FEB90`)
and **never checks its return value** (the delegate is typed to return `int`; the call site at
line 341 discards it and the function unconditionally returns `true`) — so a refusal inside the
engine's own `CanEquipWeapon` (or wherever the real gate lives) is completely invisible to this
codebase; every equip verb, log line and the propagation pipeline all believe the call
succeeded regardless. `EquipSlotMap`'s own doc comment already flagged the weapon internal-slot
values (5/6) as "NOT independently found at a clean, single-value call site... INFERRED from
the verified offset arithmetic only, NEEDS-LIVE" — this round's live evidence is consistent with
either that mapping being wrong for weapons specifically, or (more likely, since the FIRST
right-weapon call this round DID land) with `CanEquipWeapon`/an equivalent internal gate
genuinely refusing a weapon swap under some condition this round did not isolate (proficiency,
a "busy" state, or something about the slot already holding a non-stock item) while never
refusing a body/armor swap. **Distinguishing between these needs the one thing this round could
not do live: read `EquipItemFn`'s actual return value** — either a debugger breakpoint on
`0x004FEB90`'s `ret`, or (cheaper) a one-line code change to capture and log it, the same
"add a throwaway diagnostic" technique `minigames.md`'s 2026-08-28 round used for its own
range-guard bug.

**This finding supersedes §8's leading theory.** §8/§9 assumed the propagation gap was entirely
in the resref-cache/wire pipeline (§9.1/§9.2's fixes), and this round's body-slot result proves
that pipeline genuinely does work once fixed. But for weapon slots specifically, the HOST's own
live engine state never changes past the first attempt (or, for a slot that starts empty, never
changes at all) — no amount of cache/wire correctness can propagate a change that never happened
on the authoritative side. The two bugs are independent: §9 fixed a real bug in the read/wire
path (confirmed here for body), and this round found a second, real bug in the write/apply path
that is specific to weapon slots and was never exercised by §8's own three-slot live pass (§6.4
used `g_w_lghtsbr01`/`g_a_class4001`/`g_w_lghtsbr02` but each was equipped to a FRESH creature
that had never been touched by this primitive before in that pass — a single first-touch per
slot, exactly the case this round shows still works).

### 10.3 Item 3 (release interaction): not completed — pre-existing follow-AI stall, not a new
### finding

Staged `companion_local_combat=1` (default ON) by teleporting the CLIENT player ~120 units to a
cluster of hostiles and ordering one to attack (`attack 0xE 0x7FFFFFFF`). `local_combat`'s own
exclusion mechanism engaged correctly and was observed twice (`local_combat: fighting 0xE, 1
other hostile deferred (mirrored)`, then `local_combat: fighting 0x10, 1 other hostile deferred
(mirrored)` after the first target died/disengaged) — the PLAYER-side half of this feature is
healthy. But `creatures.ps1` confirmed Trask's mirrored position on the client never moved from
his spawn point (`19.35, 22.77`) through the whole ~30s window, so he was never within
`companion_local_combat_range` (10 units) of the fight and no `party drive: companion 0x127
released to fight locally` line ever appeared. This is the companion follow-AI's own
already-documented terminal-stall behaviour (`docs/research/party-reconciliation.md`), not a new
regression, and not something this round's time budget could route around (no hostile spawns
naturally within release range of Trask's own position in this save). Given the weapon-slot bug
in §10.2 also means a "released companion attacks with the re-equipped weapon" scenario has no
re-equipped weapon to check yet, this item is deferred rather than forced.

### 10.4 Item 4: verify.ps1 baseline, full test suite, native build

`pwsh tools\verify.ps1` against a freshly-connected session (no warps/tp beyond the item 3
probe above, run last): **41 passed, 8 failed** — the failing checks (`phantom damageable, not a
sink`; `the peer really loses those hit points`; `a client hit costs the enemy real hit points`;
`a downed player is reported to the agent`; `the co-op revive stands the player back up`; `every
engine call signature matches the binary`; `a container opened on the client is held and shown
open on the host`; `no per-frame spam`) match `docs/live-verification.md`'s documented
40-42/7-9 baseline exactly, both in count and in which specific checks fail — no regression.
`dotnet test tests/KotorCoop.Tests --no-build`: **2352 passed, 0 failed** (run twice this round,
once before the rig and once — via the background test run — overlapping the rig's own build;
both green). `dotnet build KotorCoop.slnx -warnaserror` and `src/KotorCoop.Plugin/build.ps1
-WarnAsError`: both clean, no source changed this round (a live-verification and root-cause
round only).

### 10.5 Verdict: `companion_gear_sync` stays OFF

§9's fixes are now live-confirmed correct (body slot, full round trip, fast). The channel as a
whole is still not safe to ship: a weapon re-equip — the more common real-world case (a looted
blaster, a workbench upgrade) than a body/armor swap — silently fails to reach even the HOST's
own engine state past the first attempt, for a reason this round narrowed to `EquipItemFn`'s
unchecked return value but did not fully root-cause. `companion_gear_sync` stays at its existing
default (`0`, OFF) in `coop.ini`/`AgentSettings`/`CoopConfig`/`profiles/coop.ini.example`/
`docs/CONFIG.md` — no default was flipped this round. **Next step for whoever picks this up**:
capture `EquipItemFn`'s real return value (a one-line diagnostic log is enough) on a weapon-slot
call that reports success but does not land, and follow whatever non-zero code comes back into
`CanEquipWeapon`'s own disassembly (`0x5A4950`) rather than guessing further.

---

## 11. §10's weapon-slot bug, root-caused (2026-08-28, worktree round, disassembly/static only —
## no rig): `EquipItemFn`'s return value was a red herring; the real bug is `EquipSlotMap`'s slot
## encoding, and the fix is fully disassembly-VERIFIED, not INFERRED

No rig was available this round (`Get-Process swkotor,KotorCoop.Agent` showed a live pair at the
start of the round — another session's, per this repo's own parallel-session hazard rules — and
it was gone by the time this round's builds finished; nothing here was exercised against a
running game). Every claim below is either read directly out of `C:\GOG Games\Star Wars KOTOR
(GOG)\swkotor.exe` with `tools/disasm.py`/`tools/q.py`/raw jump-table extraction, or a passing
unit test — tagged VERIFIED/INFERRED/NEEDS-LIVE throughout, same discipline as every other
section here.

### 11.1 `CSWSCreature::EquipItem` (0x004FEB90), fully disassembled: single entry, single exit,
### unconditional success

The complete function body is `0x004FEB90`-`0x004FECDC` (`ret 0x14` — 20 bytes of stack args,
matching the 5-dword signature `EquipItem(int nInternalSlot, object oItem, int p3, int p4, int
p5)` used from C#). Tracing every jump target in that range: **every branch lands inside
0x004FEB90-0x004FECDC, and the function has exactly one `ret`, which unconditionally sets
`eax = 1` first** (`0x004FECCC: mov eax, 1`). There is no refusal path, no `CanEquip`/feat/
proficiency check, and no two-handed-conflict check anywhere in this function. VERIFIED.

The calls inside, identified against `tools/kotor1_0_3.db` (all `param_size_bytes` cross-checked
against the binary's own `ret` immediate by `tools/q.py`'s built-in auditor, no mismatch
warnings):

| Address | Call | What it actually does (VERIFIED by disassembly, this round) |
| --- | --- | --- |
| 0x005A4950 | `CSWInventory::GetItemInInventory(item)` | **Not** a can-equip gate — despite the DB name and this doc's own earlier (§7) comment calling it `CanEquipWeapon`. Loops the inventory's 18 equip-slot dwords (`inv+4` .. `inv+0x4C`) comparing each to the incoming item's own id (`item+0x14`) and returns whether the item is ALREADY equipped somewhere on this creature. |
| 0x004FAA70 | `CSWSCreature::UnequipItem(creature, item)` | Called ONLY if the check above was true — i.e. only to detach the INCOMING item from wherever it was already equipped when moving it to a new slot. Never called on whatever currently occupies the DESTINATION slot. |
| 0x00553BB0 | `CSWSItem::ApplyItemProperties(item, creature, slot, p4)` | Gated on `p3` (the call site's third trailing arg, `1` at every clean call site found). |
| 0x005A4BE0 | `CSWInventory::PutItemInSlot(slot, item)` | The actual write — see §11.2, this is where the bug lives. |
| 0x004ECE80, 0x005ADDC0 | `ComputeArmourClass`, `UpdateCombatInformation` (stat recompute) | Unconditional, no return checked. |

This is a bare "put this item in this slot right now" primitive with no validation of its own —
consistent with its only other 15 xrefs, all inside the AI-helper families `ForceEquipClothing`/
`EquipMostDamagingMeleeWeapon`/etc., which call it only on state they already control.

### 11.2 The actual bug: `CSWInventory::PutItemInSlot`'s write is silently conditional on the
### slot argument resolving to a valid pointer — VERIFIED by disassembling its slot resolver and
### reading its jump tables as raw data

`PutItemInSlot` (0x005A4BE0) itself is three instructions: call a private slot-to-pointer helper
(0x005A49C0) with the slot argument, and if the returned pointer is non-NULL, write the item's id
(`item+0x14`) there; if NULL, `ret 8` with **no signal to the caller** — `EquipItem` still runs
every subsequent step (stat recompute, broadcast) and still returns 1.

The resolver, 0x005A49C0, was fully disassembled and its two jump tables (at 0x5A4A88 and
0x5A4AA8, plus the byte-remap table at 0x5A4ABC) read as raw dwords/bytes with a small script
(not disassembled as code — capstone mis-decodes table data as garbage instructions, which is
what produced nonsense like `loope`/`add al,4` runs in a naive linear disassembly of that range).
**The slot argument is a single-bit BIT-FLAG, not an array index or a byte-offset-divided-by-4
value**:

| Bit-flag value | `CSWInventory` offset | NWScript `INVENTORY_SLOT_*` (nwscript.nss) |
| --- | --- | --- |
| 0x0001 | +0x04 | HEAD (0) |
| **0x0002** | **+0x08** | **BODY (1)** |
| 0x0004 | +0x0C | (unused slot 2) |
| 0x0008 | +0x10 | HANDS (3) |
| **0x0010** | **+0x14** | **RIGHTWEAPON (4)** |
| **0x0020** | **+0x18** | **LEFTWEAPON (5)** |
| 0x0040 | +0x1C | LEFTARM (6) |
| 0x0080 | +0x20 | RIGHTARM (7) |
| 0x0200 | +0x28 | BELT (9) |
| 0x0400 | +0x2C | (unused slot 10) |
| 0x1000 | — (explicitly invalid) | (unused slot 12) |
| 0x4000 | +0x30 | CWEAPON_L (14) |
| 0x8000 | +0x34 | CWEAPON_R (15) |
| 0x10000 | +0x38 | CWEAPON_B (16) |
| 0x20000 | +0x3C | CARMOUR (17) |

Every value that is NOT one of these single bits (3, 5, 6, 7, 0x1000, ...) hits the resolver's
own invalid path and returns NULL. VERIFIED — read directly from the resolver's own jump-table
bytes.

**`EquipSlotMap`'s old formula — internal slot = `CSWInventory` byte offset ÷ 4 — gave 2/5/6 for
body/right-weapon/left-weapon.** Body's value (2) is correct by pure coincidence: `0x08 ÷ 4 == 2`
and BODY's real bit-flag is also `2`. The two weapon slots were NOT so lucky: `0x14 ÷ 4 == 5` and
`0x18 ÷ 4 == 6`, and **neither 5 nor 6 is a valid bit-flag** — both hit the resolver's invalid
path every time, so `PutItemInSlot`'s write silently never happened for a weapon slot, while
`EquipItem` kept reporting success. This fully explains §10.2's live symptom (a weapon-slot raw
`CSWInventory` dword that never changed across repeated "successful" equip calls) without needing
any `CanEquipWeapon`/proficiency theory at all — no such gate exists in this call path (§11.1).

### 11.3 Independent, second confirmation: the engine's own NWScript-to-internal-slot conversion

`CSWVirtualMachineCommands::ExecuteCommandActionEquipItem` (0x005355F0 — the VM handler for
NWScript's `ActionEquipItem`, confirmed by name in `tools/kotor1_0_3.db`) validates its
`nInventorySlot` argument is in `[0, 18)` (`cmp eax, 0x12; jge <fail>`) and then, at
`0x00535667`-`0x0053567B`, converts it with an explicit floating-point computation:
`fld qword ptr [0x741df8]` (loads the constant `2.0`), `fild dword ptr [esp+8]` (loads the
NWScript slot as a double), `call 0x6fc1f0` (`pow`), `call 0x6fae8c` (double→int), storing the
result back over the original slot value — i.e. **the engine itself computes
`internalSlot = 1 << nwscriptSlot`** before ever calling `CSWSCreature::AddEquipItemActions`
(0x004F0420). VERIFIED by disassembly.

`AddEquipItemActions`'s own refusal gate (0x004F043C-0x004F044D, "if item is already equipped in
one of these slots AND the slot is one of {...}, refuse") compares its slot argument against the
literals `2`, `1`, `0x10`, `0x20` — the identical four bit-flag values from §11.2's table, not
5/6. Third independent confirmation of the same scheme.

**`1 << nwscriptSlot` for NWScript slots 1/4/5 gives 2/0x10(16)/0x20(32)** — exactly the values
the task's own framing named as the thing to check ("a bit-flag mask like NWScript's
INVENTORY_SLOT_RIGHTWEAPON=16 / LEFTWEAPON=32 / BODY=2"). VERIFIED, three independent ways, all
from this round's own disassembly of the GOG 1.0.3 binary — no longer INFERRED/NEEDS-LIVE the way
§7's original slot table was.

### 11.4 The fix

`EquipSlotMap.ToInternalSlot` (`src/KotorCoop.Plugin/EquipSlotMap.cs`) now computes
`InternalSlotBody = 1 << 1 = 2`, `InternalSlotRightWeapon = 1 << 4 = 0x10`,
`InternalSlotLeftWeapon = 1 << 5 = 0x20`, replacing the old "byte offset ÷ 4" formula. A new
`EquipSlotMap.ToSlotByteOffset(nwscriptSlot)` exposes the same three raw `CSWInventory` offsets
(0x08/0x14/0x18) for verification reads (§11.5) — kept as a second, independent function rather
than derived from the bit-flag value, so a test can catch the two ever disagreeing.

`Odyssey.EquipItemFromTemplateDirect` (`src/KotorCoop.Plugin/Odyssey.Equipment.cs`) no longer
trusts `EquipItemFn`'s return value alone (it is VERIFIED, §11.1, to always be 1) — it checks it
anyway (defensive, in case a future build or an untraced call path ever does refuse) and then, on
top of that, reads the raw slot back (`creature+0xA2C` → `CSWInventory*`, offset from
`EquipSlotMap.ToSlotByteOffset`) and confirms it now holds the newly created item's id before
reporting success. A mismatch (unresolved slot, or any other silent engine-side no-op) is now a
real, distinct failure code (25 = inventory pointer null, 26 = the slot did not end up holding the
new item) surfaced through the existing `out int failure` — which `PartyPresenceController
.ApplyCompanionEquipment` and `CheatController.Progression.Equip()` already log and report to the
caller (`party drive: ... refused ... (failure N)` / the cheat reply), unchanged this round. No
call site needed to change: both already treated a `false` return as a real failure — the bug was
that `EquipItemFromTemplateDirect` itself never returned `false` for this specific case.

### 11.5 What is VERIFIED vs still NEEDS-LIVE

**VERIFIED** (read directly from the GOG 1.0.3 binary this round, or a passing test): the full
`CSWSCreature::EquipItem` disassembly and its single unconditional-success return (§11.1); that
`0x5A4950` is `GetItemInInventory`/"is already equipped", not a can-equip gate; the `PutItemInSlot`
→ slot-resolver call chain and its complete bit-flag table (§11.2), read as raw table data, not
guessed; the `2^slot` conversion inside `ExecuteCommandActionEquipItem` (§11.3); the matching
literals in `AddEquipItemActions`'s own refusal gate; `EquipSlotMap`'s new values and
`ToSlotByteOffset` (`CompanionStateSyncTests.EquipSlotMapMaps*`); `dotnet build
KotorCoop.slnx -warnaserror` and `src/KotorCoop.Plugin/build.ps1 -WarnAsError` both clean;
`dotnet test tests/KotorCoop.Tests --no-build`: **2355 passed, 0 failed**.

**NEEDS-LIVE** (reasoned from a fully-disassembled call chain, not yet observed running — no rig
this round): that the corrected bit-flag values actually make a live weapon-slot equip land on a
running game exactly as the disassembly predicts; that the new readback verification
(`creature+0xA2C` → slot dword) reads what is expected on a live creature immediately after the
call (the same offset is already load-bearing elsewhere — `Odyssey.Combat.Attack.cs`,
`Odyssey.PartyPool.cs` — but not yet read back in THIS specific sequence, same call, same frame);
that failure 24 (`EquipItemFn` returning 0) never actually fires in practice, matching the
disassembly's single-`ret` finding. `companion_gear_sync` **stays OFF** — this round is
disassembly/code-inspection-confirmed and unit-tested only, not live-verified, the same standard
§9 held itself to before its own live pass.

### 11.6 Live test plan for the next rig pass (re-runs §10.2's exact failing scenario)

1. Build and install both instances fresh (`src/KotorCoop.Plugin/build.ps1 -Install
   -WarnAsError` on both game directories — a stale plugin re-tests the OLD 5/6 encoding).
2. On the HOST, with `companion_gear_sync` still OFF: `equip 0x127 g_w_lghtsbr01 4` (right
   weapon, nwscript slot 4 → internal slot now `0x10`) via the `equip` dev verb, targeting Trask.
   Confirm the plugin log's `equip: 'g_w_lghtsbr01' equipped on 0x127 slot 4` line, same as
   before.
3. **Read the raw `CSWInventory` weapon slot on the HOST directly**, the same technique §10.2's
   own rig round used: `creature (0x127) + 0xA2C` → inventory pointer → `+0x14` for right weapon.
   Confirm it now holds the NEW item's object id (not the pre-existing `0x129`/whatever was there
   before) — this is the exact assertion §10.2 found broken.
4. Repeat step 2-3 immediately with a SECOND different resref to the SAME slot (the specific
   thing that never worked in §10.2 even once past the first attempt) — confirm the raw slot
   updates to the second item's id, not stuck on the first.
5. Repeat for the left-weapon slot (`equip 0x127 <resref> 5`, internal `0x20`, raw offset
   `+0x18`) with two consecutive different resrefs the same way.
6. If any of 3-5 fails: capture `EquipItemFromTemplateDirect`'s `out int failure` value directly
   (a console read or a temporary log line) — failure 25/26 means the readback itself caught a
   real remaining issue (worth its own root-cause pass); a still-successful `true` return with a
   raw slot that does not match means this round's verification logic itself has a bug, not the
   underlying equip.
7. Only once 2-6 are clean: flip `companion_gear_sync=1` on both instances and re-run §8's
   original "Companion equipment" live test plan (re-equip on host, confirm client's mirrored
   copy updates for a WEAPON slot specifically, confirm anti-echo) — the body slot half of that
   plan is already live-confirmed (§10.2); this closes the loop for weapons.

---

## 12. Live pass (2026-08-28, rig round): §11.6's plan run in full — both weapon slots and the
## body slot all confirmed live, mid-session propagation clean end to end, `companion_gear_sync`
## flipped ON

Two-instance rig (`tools/demo.ps1`, default save), Trask (party-roster slot 0) confirmed live
object id `0x127` on both instances (`partystate.ps1`: host pid 59848 `soloMode 0`, client pid
51380 `soloMode 1`, both report `member 0: slot 0 objectId 0x127`). `pwsh
src\KotorCoop.Plugin\build.ps1 -Install -WarnAsError` on both game directories first — the
deployed `dinput8.dll` hash-confirmed byte-identical (`sha256sum`) to the fresh build on both
installs. Both `coop.ini`s reset from `profiles/coop.ini.example` with `companion_gear_sync=1`
written in before launch (needed for item 2, the propagation channel). `dotnet build
KotorCoop.slnx -warnaserror` clean; `dotnet test tests/KotorCoop.Tests --no-build`: 2353/2355,
the 2 failures both in `CoopCommandDispatcherTests` (real UDP ports, this repo's own documented
concurrent-`dotnet test` flake per `docs/CONTRIBUTING.md`) — reran that class alone, 8/8 clean,
confirming no regression. Commands were sent through `tools/blockpoke.ps1 -ProcId <pid> console
'<line>'` (the documented unattended channel — injected OS keystrokes never reach this game),
which writes the exact same agent<-plugin field the in-game `~` console does; raw slot reads used
a small throwaway script following the exact `creature+0xA2C -> CSWInventory* -> +offset`
sequence `EquipItemFromTemplateDirect`'s own readback already uses, resolving each creature
pointer with the `resolve <id>` console verb first (host: `0x127` -> `0x17E54770`; client: `0x127`
-> `0x18674EB8`).

### Item 1 (§11.6 steps 1-5, the direct primitive, right AND left weapon plus body): PASS, all
### four calls, no crash

| Call (HOST) | Reply | Raw slot after (creature+0xA2C+offset) |
| --- | --- | --- |
| `equip 0x127 g_w_lghtsbr01 4` (right weapon) | `equipped 'g_w_lghtsbr01' on 0x127 slot 4` | `+0x14` = `0x131` (new item) |
| `equip 0x127 g_w_blstrpstl001 4` (right weapon, 16-char resref) | `equipped 'g_w_blstrpstl001' on 0x127 slot 4` | `+0x14` = `0x132` (changed again, not stuck on `0x131`) |
| `equip 0x127 g_w_blstrpstl002 5` (left weapon) | `equipped 'g_w_blstrpstl002' on 0x127 slot 5` | `+0x18` = `0x134` (was `0x0` before this call, per §10.2's own finding for a slot's first-ever write) |
| `equip 0x127 g_a_class4001 1` (body) | `equipped 'g_a_class4001' on 0x127 slot 1` | `+0x08` = `0x135` |

This is the exact scenario §10.2 found broken (a weapon slot's raw `CSWInventory` dword never
changing past the first attempt, and the left slot's very first write never landing at all) —
`EquipSlotMap`'s bit-flag fix (§11) closes it: **both weapon slots now update on every call,
including repeated right-weapon calls to different resrefs and a left-weapon slot's first-ever
write.** (One incidental non-bug: `equip 0x127 g_w_blstrpstl01 5` — a typo'd, non-existent
resref — correctly refused with `failure 8`, the fail-safe "blueprint not found" path; the very
next call with the correct `g_w_blstrpstl002` succeeded normally.) A screenshot after the
sequence (`host_trask_final.png`) shows Trask's visible model changed from the starting
`g_a_clothes01` look to the newly-equipped armor. Both `swkotor.exe`/`KotorCoop.Agent.exe` process
pairs stayed alive and `Responding=True` throughout.

### Item 2 (mid-session propagation to the CLIENT within ~5s): PASS, all four calls, fast

Client `kotorcoop-plugin.log`, matched against the host's own equip-call timestamps:

| Slot | Host equip call time | Client `party drive: companion 0x127 equips ...` time | Latency |
| --- | --- | --- | --- |
| right weapon (`g_w_lghtsbr01`) | 08:12:07.150 | 08:12:09.718 | **2.57 s** |
| right weapon (`g_w_blstrpstl001`, 16-char) | 08:17:12.058 | 08:17:13.090 | **1.03 s** |
| left weapon (`g_w_blstrpstl002`) | 08:17:35.060 | 08:17:36.407 | **1.35 s** |
| body (`g_a_class4001`) | 08:17:51.345 | 08:17:52.955 | **1.61 s** |

All four well under the "a few seconds" bar §9.3's test plan set, and all four correctly reached
the client — including the SECOND right-weapon call, the case §10.2 could never get even the
HOST's own state to change on, let alone propagate. Independently confirmed by reading the
client's raw `CSWInventory` slots directly (not just trusting the log): right weapon `+0x14` =
`0x131`, left weapon `+0x18` = `0x135`, body `+0x08` = `0x136` — all three non-zero and each a
freshly-created LOCAL item (the client creates its own item on receipt; ids do not need to match
the host's own numbering, and coincidentally overlapping with an earlier host id, e.g. right
weapon's `0x131`, is not itself meaningful). No `refused`/`failure` line appeared anywhere in the
client's plugin log across the whole sequence. Both process pairs stayed alive and
`Responding=True` after every check.

### Verdict: `companion_gear_sync` flipped ON

Items 1 and 2 are both clean. Per this document's own §11.6 step 7 and the task's own item 3:
`CoopConfig.CompanionGearSync` changed from `IsTruthy(...)` (default OFF) to
`!kv.ContainsKey("companion_gear_sync") || IsTruthy(...)` (default ON, matching
`CompanionXpSync`'s own pattern); `AgentComposition.Compose`'s `SyncCompanionGear = config?.
CompanionGearSync ?? false` changed to `?? true` (matching `SyncCompanionXp`'s own `?? true`
fallback, so a session with NO coop.ini at all now also defaults to ON, consistent with every
other already-ON flag rather than a special case); `AgentSettings.SyncCompanionGear`'s own C#
property default changed to `= true` to match; `PartyPresenceController`'s
`PluginConfig.ReadBool("companion_gear_sync", false)` changed to `true` (the plugin-side gate,
independently read); `IPartyBridge.cs`'s doc comment corrected. `profiles/coop.ini.example`'s
`companion_gear_sync=0` -> `=1`; `docs/CONFIG.md`'s two rows updated to ON; `README.md`'s status
table, roadmap row and limitations paragraph updated; `docs/roadmap-seamless.md`'s Tier-1 item 11
row and Wave B summary updated; `docs/research/README.md`'s index row updated.
`CompanionStateSyncTests.CompanionGearSyncOffByDefault`/`CoopConfigDefaultsCompanionXpOnGearOff`
renamed to `CompanionGearSyncOnByDefault`/`CoopConfigDefaultsCompanionXpAndGearOn` and
re-asserted against the new default — `dotnet build KotorCoop.slnx -warnaserror` and
`src/KotorCoop.Plugin/build.ps1 -WarnAsError` both clean, `dotnet test tests/KotorCoop.Tests
--no-build`: **2355 passed, 0 failed**. Item 3 of the original queue (a `companion_local_combat`
release with re-equipped gear) was not re-attempted this round — it is a pre-existing,
already-documented follow-AI stall (§10.3) unrelated to gear sync, and this round's time budget
went to the weapon-slot closure above instead.

### Item 4: `verify.ps1` baseline, full test suite, native build — PASS, no regression

Both game directories reinstalled from the post-flip build (`build.ps1 -Install -WarnAsError`,
hash-confirmed byte-identical deploys on both sides) and the rig relaunched fresh for this check.
`pwsh tools\verify.ps1` against a freshly-connected, untouched session: **40 passed, 9 failed.**
Seven of the nine failures match the long-standing documented baseline exactly (`phantom
damageable, not a sink`; `the peer really loses those hit points`; `a downed player is reported
to the agent`; `the co-op revive stands the player back up`; `every engine call signature matches
the binary` (187/189); `a container opened on the client is held and shown open on the host`; `no
per-frame spam`). The other two (`a client hit costs the enemy real hit points`,
`an item picked up here vanishes on the peer`) are the SAME two checks this document's own prior
"item 5" round (2026-08-28) flagged as environment-flaky substitutions that swap in and out
between runs — squarely inside the documented 40-42/7-9 range, no new failure and no regression
from `companion_gear_sync` now being ON. `dotnet test tests/KotorCoop.Tests --no-build`:
**2355 passed, 0 failed** (same run reported in the verdict above — no source changed between
the two checks). `dotnet build KotorCoop.slnx -warnaserror` and `src/KotorCoop.Plugin/
build.ps1 -WarnAsError`: both clean. No crash on either instance across the whole round.

---

## 13. Companion damage TAKEN (`companion_damage_sync`, 2026-09-02) — closing the gap
`PartyPresenceController`'s own class comment named: "their damage goes nowhere, because there
is no client->host channel for companion health, so the host never sees any of it"

### 13.1 The gap, restated

`companion_local_combat` (§0 above and `party-reconciliation.md`'s dated entries) releases a
companion near a client's own local fight to that client's own engine AI instead of mirroring
it. Released, the companion is a real, faction-PLAYER, non-pinned body that can genuinely take
hits from the enemy it and the client are fighting — but until this round, nothing carried
those hits anywhere. The host's `PartyPresenceController.DriveOne` writes the host's absolute
health onto every mirrored companion every pass (`⚠ THE HOST OWNS COMPANION HEALTH`), and the
one companion released this pass is the only exception (it "never reaches this line at all this
pass"). So on a MIRRORING instance the released companion took real local damage; on the HOST
its health never moved; and on the client's own NEXT release cycle (or a peer reclaiming the
slot) the host's untouched, higher figure would have overwritten whatever the client's own fight
had done to it, the moment mirroring resumed. In practice this meant: a released companion could
not be worn down, could not go down, and any HP loss visible on the client's own screen was
cosmetic and reverted the instant the host mirror wrote again.

### 13.2 Design — mirror the enemy damage-DEALT ledger, direction reversed, keyed by roster slot

Read `docs/research/peer-damage-ledger.md` first — this channel is a direct structural copy of
the one it already proved, with one simplification: **VERIFIED** (design, by construction; the
arithmetic itself is unit-tested, see §13.4) health cannot carry a companion's damage taken for
the identical reason it could not carry a peer's damage dealt to an enemy — it is a LEVEL that
has to be sampled, and the owner's own mirror write erases it between samples. A cumulative SUM
of damage taken cannot be raced, because nothing in this codebase ever reads it back out of the
game.

**Two differences from the enemy channel, both simplifications:**

1. **Direction.** The enemy channel is peer → owner (client deals damage, host applies it to
   ITS enemy). This channel is the same peer → owner shape (client's companion takes damage,
   host applies it to ITS companion) — so structurally identical, not mirrored.
2. **Key.** An enemy is keyed by engine object id, which is reassigned per module load, so the
   enemy ledger needs a module label and a per-module epoch. A companion is keyed by NPC ROSTER
   SLOT (`PartyPresenceView.RosterSlotPlusOne`'s own "plus one" convention, 1..9), which names
   the same companion identity for the WHOLE SESSION — `PartyPresenceController` already
   resolves companions by roster slot for exactly this reason. So `CompanionDamageLedger`
   (`src/KotorCoop.Session/CompanionDamageLedger.cs`) carries no module and no epoch; only a
   per-report GENERATION (the sender's own reset marker) survives from the enemy design, for the
   ordinary "tell a legitimate restart from a stale datagram" reason every cumulative channel
   here needs.

**The channel, end to end:**

```
client (released companion)                          host (owns the companion)
────────────────────────────                          ─────────────────────────
PhantomHitHook.OnDamage
  targetId matches a WATCHED companion slot
  (PartyPresenceController.WatchCompanion,
  only while released)?
      _companionCumulative[slot] += total
                │
   CompanionDamageController (frame thread)
      block 0x4A08: 4 x {rosterSlotPlusOne,
      cumulative, serial} + count + generation
                │
   agent: bridge.ReadCompanionDamageTaken()
                │
      CompanionDamagePacket (type 71, ~150ms
      cadence — CombatPump.PumpCompanionDamage,
      same gate PumpEnemies uses)
                │
                └──────────►  CoopHost: freshness (2s) filter
                                       │
                              CompanionDamageLedger.Merge
                                bank each peer's RISE per roster slot
                                       │
                              bridge.TryPublishCompanionDamageOwed
                                block 0x4A44: 9 x {rosterSlotPlusOne,
                                cumulative} (agent -> plugin)
                                       │
                              CompanionDamageController.ApplyOwed
                                delta = owed − applied (DamageOwedLedger,
                                reused as-is, keyed by roster slot instead
                                of object id)
                                Odyssey.ApplyDamageEffect(objectId, delta)
                                       │
                              the engine's own damage pipeline — resistance,
                              shields, difficulty scaling on the HOST's real
                              body all apply — and, at ≤ 0 hp, the engine's
                              own REAL temp-death (TempDeathUpdate)
                                       │
                              the ordinary PartySnapshot channel carries the
                              new (lower) health back down to every mirroring
                              client — no new wire type needed for that half
```

**Why no host → client packet is needed for the "owed" half.** The host's `ApplyDamageEffect`
call changes the host's OWN authoritative companion body's hit points through the ordinary
engine pipeline, which the existing `PartySnapshot`/`PartyMemberState.Health` channel already
carries to every client every tick. The gap this closes was entirely in the OTHER direction
(client → host); once the host's body is genuinely hurt, everything downstream of that already
worked before this round.

**Why the "same creature, re-run the pipeline" argument still holds.** Exactly the peer-damage-
ledger.md §2 argument, unchanged: the number carried on the wire is read at `OnApplyDamage`'s
entry, before the target's resistance/immunity/reduction virtuals run and overwrite it (§1.1
there), and the two bodies — the client's released companion and the host's authoritative one —
are the SAME creature (same blueprint, same equipment, same NPC identity), so applying it
through the host's own `EffectDamage` runs the pipeline exactly once, not twice.

### 13.3 Block layout, packet, version numbers

SharedBlock version 63 (plugin `PluginHost.cs`, agent `KotorGameBridge.cs`'s
`PluginSignature`), region `0x4A00..0x4A90` (`SharedBlock.CompanionDamage.cs`), `TotalSize`
grown `0x4800 -> 0x4B00` — `0x4800..0x4A00` deliberately left unused (reserved for concurrent
versions 61/62 on this tree, the same "leave a parallel branch's claimed range alone"
discipline every earlier growth in `SharedBlock.cs` follows):

```
block, plugin -> agent (client role: what this instance's engine has actually done)
  0x4A00   u32 count
  0x4A04   u32 generation      (the plugin's watch restarting — module change, re-attach,
                                 a fresh release after a reclaim)
  0x4A08   4 x { u32 rosterSlotPlusOne, u32 cumulative, u32 serial }   (id written last)

block, agent -> plugin (host role: what every peer has dealt to OUR companions)
  0x4A40   u32 count
  0x4A44   9 x { u32 rosterSlotPlusOne, u32 cumulative }   (id written last)
  0x4A8C   u32 applications run (plugin -> agent diagnostic)
```

`ProtocolConstants.Version` 47 → 49 (48 reserved for concurrent work not yet merged),
`PacketType.CompanionDamage` = 71 (70 was the highest in use, `HostBye`; 72 stays reserved).
`CompanionDamagePacket(PeerId, Generation, IReadOnlyList<CompanionDamageEntry>)`, one entry per
released companion (`RosterSlotPlusOne`, `CumulativeDamage`), capped at
`ProtocolConstants.MaxPartyMembers` (4) per datagram — a handful of bytes; the whole packet is
nowhere near the datagram budget (`AFullCompanionDamageReportFitsInOneDatagram`).

### 13.4 What is VERIFIED (by test) versus NEEDS-LIVE

**VERIFIED (unit-tested):**
* `CompanionDamageLedgerTests` — the rise-only bank, a repeated/out-of-order report changing
  nothing, two companions banked independently, two peers' damage to the same slot summed, a
  peer going silent not un-hurting the companion, the generation reset rule, a roster slot of 0
  ignored, saturation instead of wrap, and an end-to-end "one hit is one hit through loss and
  duplication" run reusing the plugin's own `DamageOwedLedger` unmodified.
* `CompanionDamagePacket`/codec — round trip, a truncated packet rejected, a full 4-entry report
  fits one datagram (`PacketCodecTests.cs`).
* `CompanionDamageLoopbackTests` — a real `CoopHost`/`CoopClient` pair over loopback UDP: a
  client's report reaches `CoopHost.GetCompanionDamage()`, a rising report supersedes the last
  one, and the host never reports to itself.
* Plugin build (`build.ps1 -WarnAsError`) and root build (`dotnet build -warnaserror`) both
  clean — `SharedBlock`/`SharedBlockView`/`PhantomHitHook`/`PartyPresenceController`/
  `CompanionDamageController` all compile and wire together.

**NEEDS-LIVE (reasoned, not yet observed running — fails safe: worst case is the pre-existing
gap, not a new failure):**
1. **The headline scenario itself.** A companion released via `companion_local_combat` actually
   takes real hits on the client's screen, the host's log shows `companion damage: applied N of
   a peer's damage to companion 0x... (roster slot S, owed M)` climbing, and the HOST's own
   `PartyMemberState.Health` for that slot genuinely falls — not just the client's local copy.
2. **A companion downed on the client stays down on the host**, and the ordinary co-op
   temp-death channel (`death-handling.md`) then treats it exactly like any other companion
   down: the host's `TempDeathUpdate` runs its real recovery timer, and every client (including
   the one that dealt the damage) sees it as a genuine down/stand-up rather than the old
   "pop back up on the next mirror pass" artifact.
3. **`PhantomHitHook.WatchCompanion`'s "only while released" guard** — that a still-MIRRORED
   companion's block region stays at count 0 even while combat is happening nearby (i.e., the
   mirror write path never accidentally trips the companion-damage hook because it writes hit
   points directly rather than through `OnApplyDamage`).
4. **Two companions released simultaneously**, each taking independent damage, banked into
   independent roster slots with no cross-slot contamination — the same scenario
   `party-reconciliation.md`'s 2026-08-24 entry (§13.5 below) already live-confirmed for the
   RELEASE/ATTACK half; this round adds the DAMAGE half on top of that same recipe.
5. **A module transition mid-release.** The companion is reclaimed on the client (release state
   is not saved-game state; `ReleaseSession`/the roster-freeze guard already restore faction/pin
   on any transition), and `CompanionDamageController.Invalidate`'s generation bump + the
   plugin-side watch clear together mean nothing is owed against a stale roster-slot debt from
   before the transition. Reasoned from the exact same guard the enemy channel already relies
   on; not independently observed for this channel.

### 13.5 The exact live recipe (verbatim from the release-half's own live-confirmed round —
`party-reconciliation.md`'s "the two-companion `companion_local_combat` release... Flipped ON
by default" entry, 2026-08-24 — extended with what THIS round's damage channel needs on top)

1. `tools/demo.ps1` (or the two-instance manual rig), a save with Trask still native (slot 0)
   and Zaalbar recruited into an active party slot (slot 8) on the HOST — the exact roster the
   2026-08-24 round used, chosen because it needs no fresh recruit ceremony beyond what that
   round already did once. `companion-combat-credit-plan.md`'s round 82/83 entries cover how to
   stage the recruit if starting from a fresh save instead (`CheatKindRecruit`/`recruit <slot>`
   console verb, local-only, never crosses the network).
2. Both instances' `coop.ini`: `companion_local_combat=1`, `companion_local_combat_range=80`
   (generous — the companion follow-AI stall makes physically reuniting two companions
   unreliable in most modules, so a wide range lets both qualify independently without needing
   them adjacent) and `companion_damage_sync=1` (the shipped default; set explicitly if testing
   against an older `coop.ini`). **These are read once at plugin construction** — a full rig
   restart is required after editing them, not just a reconnect (the same trap
   `party-reconciliation.md`'s "second, unrelated, newly-discovered bug" entry names for
   `companion_local_combat` itself).
3. Connect both instances; confirm `PartySnapshot` has converged (both companions visible in the
   client's mirrored party).
4. Position the CLIENT's own player and a single excluded hostile within
   `companion_local_combat_range` of BOTH companions and the player at once — the 2026-08-24
   round used `tar05_gammorean` (object `0x3`) with a fresh `tools/creatures.ps1` position sweep
   confirming player-to-enemy 57.2 units, Zaalbar-to-enemy 25.5 units, Trask-to-enemy 55.5 units,
   all under the 80-unit range.
5. Force the fight (an inert scripted placement will not swing on its own — the same reason
   every other combat channel in this codebase stages via a console verb rather than trusting
   ordinary AI): `blockpoke.ps1 attack 0x7FFFFFFF 0x3` on the CLIENT, exactly the command the
   2026-08-24 round used to get both companions' `party drive: companion 0x... released to fight
   locally` lines to fire in the same tick.
6. Let the fight run long enough for the enemy to land several hits on at least one released
   companion (a Gammorean is not trivial; do not `slay` it immediately).

**What proves it worked — read BOTH logs:**
* **Client** `kotorcoop-plugin.log`: the existing `party drive: companion 0x... released to
  fight locally` / `drove companion 0x...'s attack on 0x3 directly` lines (release half, already
  live-confirmed), PLUS (new) nothing extra is logged on the taken side by design — the watch is
  silent bookkeeping; confirm instead via `blockpoke.ps1` reading the companion's live HP
  dropping on the client's own screen through ordinary play.
* **Host** `kotorcoop-plugin.log`: `companion damage: applied N of a peer's damage to companion
  0x... (roster slot S, owed M)` climbing across the fight, and — if the fight runs long enough —
  either a run of refusals stopping (a temp-death applied) or, if the debt exceeds current HP,
  the companion's own `TempDeathUpdate`-driven down/stand-up cycle firing exactly as it does for
  any other companion.
* **Cross-instance agreement**: the client's own locally-observed HP for the released companion
  and the HOST's `PartyMemberState.Health` for the same roster slot should converge to the same
  figure within one `CombatPumpIntervalMs` cycle (~150ms) plus one heavy-pump `PartySnapshot`
  broadcast — diff the two directly (`blockpoke.ps1` against each instance, or the console
  `census`-style read this codebase already has for the enemy channel) rather than trusting
  either side's own belief alone, the same "the agent believes X is not the same claim as X is
  true" discipline `verify.ps1`'s own header states.

### 13.6 Test suite and build

`CompanionDamageLedgerTests` (10 pure-arithmetic tests), `PacketCodecTests` (+3: round trip,
truncation, datagram-budget), `CompanionDamageLoopbackTests` (+2: wire round trip, host-does-
not-report-to-itself). `dotnet build KotorCoop.slnx` and `src/KotorCoop.Plugin/build.ps1
-WarnAsError`: both clean. `dotnet test KotorCoop.slnx`: see the session's own final report for
the exact before/after count.

### 13.7 Live (2026-09-02, night): items 1 and 2 VERIFIED, two defects found and fixed on the way

Full log excerpts and the staging in live-verification.md ("Companion damage TAKEN live",
2026-09-02, night). The short form:

* **Item 1 — the host's health genuinely falls.** tar_m10aa, Trask released on the client against
  `tar10_vulkarpt02`: the host applied `5, 3, 4, 1, 6, 6, 5, 12, 4` (owed 5→46) and its SERVER
  Trask read `cur 1` (floored — see below), its client-side twin still 36. Ask the server
  object, never the twin, when reading what the host's engine thinks.
* **Item 2 — downed on the host, revived by the host's own `TempDeathUpdate`.** With the floor
  cleared, one more client-side `hurt 5 0` took the host's Trask to `-11`; both copies read `-11`
  until the host's engine resurrected him (`cur 1`, `listening +0x19C` = 1) and the mirror
  followed. The revive waited on the LAST hostile that could still perceive the party — one at
  15.7 m held it, well past death-handling.md §3's 10 m figure; slaying it stood him up within
  5 s.
* **Rig caveat, not the channel:** the host's Trask carries the Endar Spire `SetMinOneHP` pin
  (`+0x200` = 1 on the server object) into every later module on this save, so nothing downs him
  on the host until it is cleared. The client's released copy reads 0 there — the pin is the
  host's, and a released copy is unpinned by design.

**Defect A — the watch under-credited a reclaim/re-release cycle (fixed before the pass).**
`PhantomHitHook`'s companion watch was keyed by object id and reset on every release, so the
second release of the same companion within a module published a cumulative total that
restarted at 0; `DamageOwedLedger` on the host, rise-only by design, saw a total it had already
banked and applied nothing. Keyed by roster slot (`_companionCumulative`/`_companionSerial`
sized `PartyRosterSlots + 1`, never reset within a module), every one of the three release
cycles in the pass applied on the host.

**Defect B — a reclaimed copy that had been downed never stood up.** §13's own reasoning about
the revive ("write what the host says, then repair the state that implies") was right; the code
asked `Odyssey.IsDowned` AFTER writing the host's positive hp, and `IsDowned` is
`GetDeadTemp || hp <= 0` where `GetDeadTemp` computes `hp <= 0` on the spot (min-one-hitpoint.md
§1: there is no stored flag). So the Resurrection repair was unreachable: the client's copy kept
the prone body and the death writes (`listening +0x19C` = 0) under a 1-hp bar. Fixed by reading
`wasDown` before the write; verified on the next launch (`party drive: stood companion 0x127 back
up — it was down here while the host has it on 1 hp` at the 25 s reclaim, both copies
`listening` = 1 afterwards).

Still NEEDS-LIVE from §13.4: item 3 (a mirrored companion's region stays at count 0 — not
contradicted, the host applied nothing outside release windows, but not isolated), item 4 (two
companions released at once — one companion on this rig), item 5 (a module transition
mid-release — the warp in this round happened while Trask was mirrored, not released).
