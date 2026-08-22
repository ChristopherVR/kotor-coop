# Symmetry audit (2026-08-22)

The rule this pass audits against: **everything is symmetric.** Whatever one player does —
open a door, start a fight, start a conversation, walk into a story trigger — happens for the
other, regardless of who is host. This document reads the CURRENT source (not the docs — the
docs are cited only where they add context that still matches the code) for every shared-state
channel in the mod, states who can originate the action, whether the other side actually
receives or acts on it, and names the exact file:line an asymmetry lives at when one exists.

Two verdicts recur throughout and are worth naming up front, because they look like violations
of the headline rule and are not:

- **DELIBERATE, documented, structural.** A handful of channels are single-authority by
  necessity, not oversight: exactly one peer's engine may ever mutate a piece of world state
  that consumes the object-id counter or governs a fight's outcome (enemy HP/death,
  encounter spawns, the creature-liveness census). Two independently-simulated copies of that
  state would diverge, not converge — there is no "make it symmetric" fix available short of
  redesigning how KOTOR's engine assigns object ids. These are marked **STRUCTURAL** below.
- **Genuine gaps.** A channel that COULD be symmetric — nothing about the underlying engine
  state prevents it — but isn't, because nobody built the reverse direction yet. Two of these
  were found and fixed this pass (B1, B2 below); the rest are called out as open work.

Tag key in the table: **SYMMETRIC** (verified, no asymmetry), **STRUCTURAL** (single-authority
by necessity, documented), **FIXED** (was asymmetric, closed this pass or a prior one — cited),
**GAP** (asymmetric today with no structural reason it must be).

## The audit table

| Channel | Originates | Other side receives/acts | Asymmetry |
| --- | --- | --- | --- |
| Doors | Both — either peer's own engine-native open/unlock/swing is detected locally, no authority branch (`CoopAgent.PumpDoors`, `src/KotorCoop.Session/CoopAgent.cs:2466-2481`) | Yes — reliable, order-independent flag channel (`EventFlagStore`, Lamport-serial merge), applied by `DoorController.Apply` on every instance (`src/KotorCoop.Plugin/DoorController.cs:70-123`), monotonic (never interrupts a swing) | None. **SYMMETRIC** |
| Placeables | Both — same mechanism as doors, different flag tag (`CoopAgent.PumpPlaceables`, `src/KotorCoop.Session/CoopAgent.cs:2489-2502`) | Yes — same reliable flag channel, applied via `src/KotorCoop.Plugin/PlaceableController.cs` | None. **SYMMETRIC** |
| Ground items | Neither player "originates" one (seeded from module/save data); either can pick one up (`CoopAgent.PumpItems`, `src/KotorCoop.Session/CoopAgent.cs:3304-3325`, no authority branch) | Yes — `CoopClient.RemoveItem` (`src/KotorCoop.Session/CoopClient.cs:1197-1211`) reports a client pickup to the host; `CoopHost.RemoveItem` (`CoopHost.cs:172`) applies it and the merged remaining set republishes to every instance | None. **SYMMETRIC** |
| Container loot | Both — `CoopAgent.PumpContainerLoot` (`src/KotorCoop.Session/CoopAgent.cs:2504-2552`) detects and publishes either peer's own removal | Yes — `session.PublishContainerLoot`/`session.ContainerLootRemovals` merge and republish to every instance | **FIXED** — the method's own comment records a former host-only asymmetry ("This used to be host-only... only this pump was one-sided") already corrected before this pass; cited here as history, not an open gap. |
| Materialised loot | Host only — inherent: it rides the enemy-authority (only the host's engine ever produces the kill that drops it) | One-way, by construction — `CoopAgent.PumpMaterialisedLoot` (`src/KotorCoop.Session/CoopAgent.cs:979-1032`) is a no-op on the host (`ICoopSession.ResetMaterialisedLootDedup` defaults to nothing there); a client has no symmetric "I killed this, here's its loot" publish because a client's engine never owns an enemy death | `CoopAgent.cs:979-1032`. **STRUCTURAL** — tied to enemy authority below, not a defect of the loot channel itself. |
| Enemies / damage / local_combat | Both peers deal damage locally; enemy HP/death authority is always the host's engine, hardcoded (`ICoopSession.IsEnemyAuthority`; `CoopHost.cs:277` → `true`, `CoopClient.cs:232` → `false`) | Partial by design — a peer's damage DEALT is reported up (`ICoopSession.GetEnemyDamage`, `ICoopSession.cs:115`) and spent as real engine damage only on the host's copy; the kill, loot and XP all happen there | `ICoopSession.cs:52-55`, `CoopHost.cs:277`, `CoopClient.cs:232`. **STRUCTURAL** — documented in the interface's own comment: "Exactly one peer does — the host — and it is the only one whose fights actually kill anything." No shared-HP-pool primitive exists to make this otherwise. |
| Effects (peer effect sync) | Both — any instance's engine landing a non-damage effect (stun/paralyze/knockdown/etc.) on any watched phantom (`PhantomEffectHook`) | Yes, fully — `PublishPeerEffect`/`DrainPeerEffects` implemented identically on `CoopHost.cs:794,818` and `CoopClient.cs:934,948`; `CoopAgent.cs:1230-1273` runs unconditionally, no authority branch | None. **SYMMETRIC** |
| Encounters | Effectively both peers' presence, but the actual spawn only ever fires through the host's engine | Partial by design — host fires its own directly (`CoopAgent.cs:2655-2661`, `SetEncounterWatch(false)`); a client's own encounters are disarmed outright and relayed to the host to fire (`CoopAgent.cs:2717-2718`, `SetEncounterWatch(true)` + `PublishEncounterTriggers`, `GetEncounterFireRequests` at `CoopAgent.cs:3137`) | `CoopAgent.cs:2655-2661` vs `2717-2718`. **STRUCTURAL** — an encounter spawn picks creatures pseudo-randomly from a spawn table, so two independently-fired copies would not converge (unlike a generic trigger's deterministic script, which is exactly why triggers COULD be made symmetric and encounters structurally cannot without replicating the spawn outcome itself). |
| Story triggers (generic `CSWSTrigger` relay) | Both — either peer's real PC entering an eligible trigger | Yes, now fully symmetric — **B1, this pass**: `TriggerFireHook` (`src/KotorCoop.Plugin/TriggerFireHook.cs`) observes any instance's own engine natively firing a trigger; `TriggerRelayController.HandleNativeFireObservation` filters/allowlists it; `CoopAgent.PumpTriggerFiring`/`PumpNativeTriggerFires` (`CoopAgent.cs`, ~3183-3288) run the SAME combined request path on both host and client; `TriggerRelayStore` shares one fired-set between the old watch-relay path and the new native-fire path (`ClaimNativeFires`) so neither direction double-fires; `TriggerRelayPacket.NativeFiredIds`/`NativeFiredOriginPeerIds` carry it both directions, protocol 36. | **FIXED this pass.** Was: host's own trigger entry untracked, never reached a client (see docs/research/trigger-relay.md §12 for the full derivation). Now none remaining. |
| Dialogue start (token acquisition) | Both — host and client can each claim the conversation token | Yes — every claim, host's own or a client's, goes through the single arbiter `DialogueArbiter.Apply` (`src/KotorCoop.Session/DialogueArbiter.cs:35`); `DialogueGateHook.cs:238-266` runs identically on every instance | `DialogueArbiter.cs:21-27`. **GAP, documented and only partially mitigated**: "first speaker wins" structurally favours the host — the host's own claim reaches the arbiter in-process at zero latency while a client's crosses the network, so two SIMULTANEOUS clicks on DIFFERENT free NPCs resolve to the host every time. The interaction lock (`InteractionLockController`, claimed the instant a player starts interacting, BEFORE the token race, consulted at `DialogueArbiter.cs:62`) makes a CONTESTED NPC fair; an uncontested simultaneous claim on two different NPCs still is not. Out of this pass's scope (B1/B2 did not touch the arbiter); flagged as the one remaining host-favouring race in the dialogue system. |
| Dialogue join | Both — either player's conversation can be auto-joined by the other | Yes, symmetrically — **B2, this pass**: `InteractionLockController.TryForceOpen` runs identically on every peer's own instance, each deciding FOR ITSELF using only its own local facts (own distance to the partner, own combat/minigame state); `DialogueAutoJoinRule.ShouldAutoJoin` (`src/KotorCoop.Protocol/DialogueAutoJoinRule.cs`) has no "am I the host" or "who started this" parameter at all | None remaining — verified by `DialogueAutoJoinRuleTests.TheRuleProducesTheSameAnswerRegardlessOfWhoStartedTheConversation`. **SYMMETRIC** (narrower than dialogue start's own arbiter race above: this only decides whether a JOIN request is sent for a conversation that already exists). |
| Dialogue end | Only the token owner (host or client, whichever is talking) | Yes — `DialogueArbiter.Apply`'s release branch (`DialogueArbiter.cs:100-106`) accepts a release only from `current.OwnerPeerId`, broadcast host→all the same as every other dialogue-state field | None — gated on ownership, not host/client identity. **SYMMETRIC** |
| Dialogue grants (reward items) | Both — whichever peer's conversation runs the reward script | Yes, explicitly symmetric — `PublishDialogueGrant`/`DrainDialogueGrants` (`ICoopSession.cs:447,457`) implemented on `CoopHost.cs:250,1807` (own grant broadcast, AND relays a client's report — "the host's half of the symmetric channel") and `CoopClient.cs:785,798` | None. **SYMMETRIC** |
| Story triggers — see above | | | |
| Encounters — see above | | | |
| Transitions/announce/lead-swap | Both — `TransitionAnnounceWatch.OnTransitionBegan` runs unconditionally on every instance, no host/client branch (`PluginHost.cs:678-681`) | Yes, symmetric by default — `follow_lead=any` (default; `CoopConfig.cs:239`) lets either player's committed crossing become the announce the other follows; simultaneous transitions resolve host-wins deterministically as a tiebreak (`LeadFollowRule.cs:74-78`), not a standing bias | None by default. `follow_lead=host` (`LeadFollowRule.cs:15`) is an opt-in, documented restoration of the OLD client-only-follows behaviour — a config choice, not a defect. **SYMMETRIC** (default). |
| Movies (transition + mid-module) | Both — `MidModuleMovieController.HandleCapture`/`HandleRequest` (`MidModuleMovieController.cs:116,188`) run unconditionally on every instance; `MoviePlayHook` installs on any instance's engine | Yes — `PublishMidModuleMovie` exists on both `CoopHost.cs:1386` and `CoopClient.cs:626`; `BroadcastMidModuleMovies` relays to all peers except the originator (`CoopHost.cs:1434`, the same `ExceptPeer` pattern chat uses) | None. **SYMMETRIC** |
| Minigames | Both — `MinigameController.LocalInMinigame` (`MinigameController.cs:47`) is a pure per-instance local read, no role branch, folded into each peer's own ordinary `PlayerState.FlagInMinigame` | Yes — `MinigameRule.ShouldHidePeer` (`MinigameRule.cs:27`) keys only on the OTHER peer's own flag and is applied uniformly by whichever instance is looking, regardless of host/client (`CoopAgent.cs:385,1199,1295,2680`) | None. **SYMMETRIC** |
| Pause | Both — either player pausing pauses both worlds (`PauseController.cs` header, line 4), no host/client branch | Yes — mirrors via the engine's single pause bit both ways; either player may RELEASE a shared pause regardless of who started it (`PauseController.cs:126-149`, explicitly fixed from an earlier release-originator-only bug) | None — **FIXED** prior to this pass, kept as a case study: the file's own comment trail documents the earlier asymmetry and the fix. **SYMMETRIC**. |
| Chat | Both (obviously) | Yes — `CoopHost.cs:538` re-enqueues an inbound client's chat for broadcast to every OTHER peer excluding only the sender, the identical relay shape used for the host's own chat (`CoopHost.cs:469`) — a third peer receives a client's line exactly as it receives the host's | None. **SYMMETRIC** |
| Journal/globals | Both — any peer can advance/remove a quest tag or set a flag | Yes — `JournalStore.Merge`/`ApplyRemote` and `EventFlagStore` use a per-key Lamport `(Serial, OriginPeerId)` clock; a remote entry is adopted whenever strictly newer, symmetric in direction (`JournalStore.cs:18-26,159`) | `JournalStore.cs:22`. **Minor, documented GAP**: ties on a genuine same-instant collision on the IDENTICAL key favour the host; every non-colliding case is fully symmetric. Not a general authority asymmetry. |
| Progression (party gold + PC XP) | Both — either peer can earn/spend gold or gain XP locally | Yes, fully symmetric — host merges every client's reported ledger delta and republishes (`CoopAgent.cs:1440-1517`, `PumpProgressionHost`); a client reports its own delta and adopts the host's merged result (`CoopAgent.cs:1446-1457`) | None — gold is a cumulative delta, XP is max-merged, both commutative/idempotent, so the host-as-aggregator role is a routing choice with no functional bias (`ICoopSession.cs:352-379`). **SYMMETRIC** |
| Party roster/presence | Both engines publish their own roster every frame (`PartyRosterController.cs:6-7`) | Partial by design — the host's published roster is canonical; a client's own published roster is a documented no-op (`CoopClient.cs:315-319`: "Only the host is authoritative for the roster; a client's own view is dropped, the same way its party snapshot is") | `CoopClient.cs:315-319`. **STRUCTURAL**, matching the codebase's general "host owns world state, clients mirror" pattern also used for enemies/encounters. Note this is about the ROSTER'S source of truth, not recruitment REACHING both players — a recruit/dismiss conversation on EITHER peer still reaches both party tables via `dialogue_recruit_sync` (see Dialogue grants / journal notes above); only the canonical LIST itself is host-sourced. |
| Minigames — see above | | | |
| Pause — see above | | | |
| Chat — see above | | | |
| Liveness (creature alive/dead/gone census) | Host (enemy-authority) only, by explicit design | One-way only — `LivenessReconcileController.cs:13-15`: "The owner never has anything to reconcile against itself... CoopAgent only ever writes the liveness region on the non-authority side" | `LivenessReconcileController.cs:13-15`. **STRUCTURAL** — mirrors the same single-authority-over-object-ids model as enemies, encounters and materialised loot above; not comparable to the trigger/dialogue-join gaps this pass fixed, since those were social/script actions with no id-authority requirement and creature liveness is core world-authority state. |

## What this leaves open

Two genuine, non-structural gaps remain after this pass, neither touched by B1/B2:

1. **Dialogue start's arbiter race** (`DialogueArbiter.cs:21-27`) — two players clicking
   DIFFERENT, uncontested NPCs at the exact same instant resolve toward the host, because the
   host's own claim never crosses the network. The interaction lock already makes a CONTESTED
   NPC fair (whoever's claim reaches the arbiter first, by real wall-clock order, wins); it does
   not remove the tie-break bias for two claims that were never contesting each other but happen
   to land in the same tick. A full fix needs either a vector-clock-style tie-break independent
   of arrival order, or accepting the current behaviour as a rare, low-stakes edge case (losing
   a race for an NPC nobody else wanted yet costs one click, not a stuck state).
2. **Journal/EventFlag same-key collision** (`JournalStore.cs:22`) — the SAME narrow shape:
   only a genuine simultaneous write to the identical quest tag or flag from both peers ties
   toward the host. Every other case (the overwhelming majority) is fully symmetric by serial
   comparison.

Both are narrow, already-documented, low-frequency host-favouring ties inside otherwise-fair
arbitration — not silent one-way channels. They are named here rather than fixed because
neither was in this pass's B1/B2 scope and both need a live pass to judge whether the tie-break
is ever actually felt in play before spending a redesign on it.

## Files this pass added or touched for B1/B2

See docs/research/trigger-relay.md §12 (B1, native trigger-fire symmetry, protocol 36) and
docs/research/shared-state-audit.md's "RESOLVED (2026-08-22): `dialogue_auto_join` replaces the
flag, seamless" section (B2) for the full file lists, VERIFIED/INFERRED/NEEDS-LIVE tagging, and
exact live-test scripts for each.
