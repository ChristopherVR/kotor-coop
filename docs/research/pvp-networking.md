# PvP: networking and authority (research, 2026-10-09)

Status: proposal, nothing built. Companion to `pvp-product.md` (rules, UX). Tags: **READ** = read from code this pass, **DERIVED** = arithmetic over cited constants, **INFERRED** = reasoning, **NEEDS-LIVE** = unmeasured. Nothing here was run.

## 1. Today's latency budget

Topology is a star: a client-to-client avatar goes client -> host -> client (`network-topology.md` §5), so two players who are both clients pay two network legs; with a host-and-one-client pair it is one.

Phantom position (READ, `lag-audit.md:61-85`): tick 30 Hz (`ProtocolConstants.DefaultTickRate`, `ProtocolConstants.cs:614`; config key `tick_rate`, `CoopConfig.cs:1058`); agent poll also 30 Hz; `StateUpdate` (client to host) and `WorldSnapshot` entries are unreliable and superseded each tick. The receiver buffers in `SnapshotBuffer` and renders `interp_ms` in the past: default 80 (`ProtocolConstants.cs:625`), read by `CoopConfig.cs:1061` (`interp_ms`), applied at `AgentComposition.cs:87-91`, `CoopClient.cs:250`. `dejitter` (`CoopConfig.cs:1064`, default off) regularises arrival stamps (`SnapshotBuffer.cs:140`). Then the plugin smoother (`smooth_ms` 60 on phantoms) and a ~80 ms repath cooldown. The audit's total is about 240 ms typical, 330-400 ms worst, plus transit. `interp_ms` is also a negotiated behaviour value (`BehaviourCompatibility.cs:104`), so peers in a session agree on it.

Phantom animation: one-shots ride the same snapshot stream (`animation-sync.md`), so they share the same budget. A swing therefore appears on the victim's screen roughly 250-400 ms plus one-way latency after the attacker pressed it. Hits land on a different clock (below), so the visible swing and the damage are not aligned.

Attack on an ENEMY (READ, `peer-damage-ledger.md` §3):
- The attacker's own engine rolls the hit against its local copy of the enemy. The plugin hooks `CSWSEffectListHandler::OnApplyDamage 0x004DFA40` (`PhantomHitHook.cs:60`), which sees the amount after hit/miss, crit and sneak attack but before the target's resistance. Misses never arrive.
- Amounts accumulate in a cumulative `DamageDealtTable`. The agent publishes it as `EnemyDamage` (type 33, unreliable, per tick, `PacketCodec.Damage.cs:12`). The host's `PeerDamageLedger` banks each rise and the plugin spends it as real engine damage through `Odyssey.ApplyDamageEffect` (`PeerDamageController.cs:465`). The kill happens in the owner's engine.
- Cadence (DERIVED): the enemy pump runs every `CombatPumpIntervalMs` = 150 (`AgentSettings.cs:48`, gate at `CoopAgent.cs:894`), then up to 33 ms to the next tick, one-way transit, a host ledger and block write, and a frame. Roughly 150-250 ms plus transit before the owner's engine applies damage. The victim sees the enemy's health change on the next `EnemySnapshot`, so add about another tick plus `interp_ms`. About 350-500 ms end to end (DERIVED, NEEDS-LIVE).

Attack on a PEER phantom, which is the PvP-shaped path that already exists (READ): the attacker's engine rolls against the phantom, a stand-in body, immortal (min 1 hp), with a bare blueprint. The hook publishes cumulative damage per phantom slot (`CombatPump.PhantomAndEffects.cs:34-44`, on the 150 ms pump). `PhantomDamage` (31, unreliable; `CoopHost.Damage.cs:134`) goes to the host, which applies it if addressed to it or relays on its next broadcast (`CoopHost.cs:516`). The defender's pump hands the running total to the plugin as level state (`CombatPump.PhantomAndEffects.cs:59-61`). Worst case about 150 + 33 + 33 + 150 + frame, near 370 ms plus RTT/2 (DERIVED). Non-damage effects (stun, paralyze, knockdown, slow, sleep, choke, push) use `PeerEffect` (44), explicitly unreliable and cosmetic-adjacent (`PacketType.cs:306-318`).

Two properties matter for PvP. First, the roll uses the PHANTOM's defence, not the real character's: AC, saves, resistances and immunities are the stand-in's. `PeerDamageController.cs:14-24` states this plainly for the phantom case ("different creatures, a bare spawn versus a real character"). Second, the number on the wire is pre-resistance, so the defender's engine applies its real character's reduction once (that is the one good fidelity property today).

## 2. Authority models

Existing parts reused: forwarded-damage applier (`ApplyDamageEffect` with attacker attribution, `peer-damage-ledger.md` §8), `PhantomHitHook`, the cumulative-total ledger, `PeerEffect`.

| | (a) Attacker-authoritative | (b) Defender-authoritative | (c) Host arbitration |
|---|---|---|---|
| Mechanism | Attacker's engine rolls vs the phantom, sends damage (exists today) | Attacker sends an attack intent (attacker id, weapon or power, round id, time). Defender's engine re-rolls against its REAL character | Both report; the host decides who hit whom, applies results |
| Fairness at 50-200 ms RTT | "Favour the shooter": the attacker sees a hit and it counts, so the defender takes hits they already dodged on their screen. The phantom's AC is wrong, so hit rates are wrong. Delay about 370 ms is invisible only because KOTOR rounds are 3 s | "Favour the defender": hits the attacker saw land may miss. Correct AC, saves, immunities. Intent adds one transit, so about the same latency | Fair in the sense of one truth, but the host is a player: its own hits resolve with zero latency (the same asymmetry that broke interaction locks, `network-topology.md` §5). Worse with a host-as-client pair |
| Cheating | Attacker forges any damage number. Cumulative totals are trivially inflated. The host cannot verify | Only intent is forged (rate, target), the roll stays on the victim's machine. Defender can still lie by never applying a hit (clamp hp, ignore the packet). A modified defender cannot be stopped in any peer model | Host can range-check and rate-limit. Does not stop a host cheater | 
| Reuse | Highest: just flip phantom hostility. Needs a clamp (cap per round, per weapon) | Medium: new intent packet, new "roll against local PC from a foreign attacker" call. Engine's attack action cannot be run with a phantom as attacker without puppeting, see below | Lowest: host must model both characters' stats, which it does not have (client characters are not host-resident) |
| Same-frame mutual kill | Both engines apply the other's damage within about 370 ms, so both go to 0: a true double-KO is common | Each defender decides its own death, so both can die, then a round result resolves it | Host orders reports by its own receive time. Fixed by the round-result rule below |
| Force powers with saves | The attacker's engine rolls the power against the phantom's saves: wrong | Right saves, so right. Needs a way to cast a power at a foreign attacker's target. Hard | Same problem as (c) in the stat table |
| Stun, stasis, knockdown | `PeerEffect` is unreliable and best-effort, so a lost one is a free escape; no duration agreement | Defender applies the effect to itself from the intent, so the defender is stunned for real, on its own clock | Host would need to time-box it |

Edge case worth settling regardless of model: effects freezing a player (stasis, stun, knockdown) need a deadline that the defender's engine owns (the effect lives on the real character), with the attacker's phantom showing the animation by a reliable edge event, not a lost-able datagram.

Reading of the options: (a) is nearly free and reuses the most, but is wrong on AC and saves. (b) is correct on stats but needs the attacker's attack to execute against the defender's real body. Practical hybrid: **defender-authoritative resolution of a pre-rolled attack**. The attacker's engine rolls the attack against the phantom (this is required so the swing animates and costs a round), but the wire carries a **hit report** `{seq, round, attacker, weapon/power, raw damage pre-resistance, damage type, crit flag}`. The defender's engine applies it through the existing `ApplyDamageEffect` (so resistance, immunity, damage reduction use the real stats). Saves for powers: defender rolls its own saving throw on receipt. To-hit stays attacker-rolled against the phantom, with the phantom's AC overwritten to match the defender's (the stand-in appearance and equipment sync already exists; AC copy is an extra field in state, NEEDS-LIVE whether the phantom's defence can be set). This keeps the shooter's perception (hits that look hit, count) and fixes damage and saves.

## 3. New packets and state

Reliability follows the repo's rule (`network-topology.md` §5: decisions are reliable or host-held; levels are unreliable cumulative).

| Message | Direction | Class | Content |
|---|---|---|---|
| `PvpChallenge` / `PvpReply` | peer to host to peer | reliable (retransmit-until-ack, like `CreditGrant`, `CoopHost.CreditGrant.cs:53`) | match id, format, rules digest, accept/decline/timeout |
| `PvpRound` (state) | host to all, level, plus reliable edges | reliable for edges (start, pause, end), unreliable broadcast for clock | match id, round number, phase (countdown, live, paused, over), pause budget, deadline |
| `PvpHit` | attacker to host to defender | reliable with sequence number and ack (a lost hit is a free miss; cumulative totals cannot carry per-hit crit or type) | match id, round, seq, attacker, damage, type, source; defender dedups on `(attacker, seq)` |
| `PvpEffect` | same | reliable | effect, duration, seq (replaces unreliable `PeerEffect` inside a match only) |
| `PvpResult` | defender (and host) to all | reliable | match id, round, loser, reason (hp, yield, disconnect, leave) |

HP reports: the defender reports `Downed` already via `FlagDowned` in `PlayerState` (`PlayerState.cs:30`); add `FlagInDuel` (bit 256, next free after `FlagSoloSegment` = 128) so nobody else needs a separate stream.

Capability and negotiation. Add `SessionCapabilities.Pvp = 1 << 13` (`SessionCapabilities.cs`; `All` must include it) so `ContentCompatibility` can drop it on a mismatch the way `Appearance`/`Equipment` are dropped. Add `BehaviourFlags.Pvp` plus the rule keys (`pvp`, `pvp_pause`, `pvp_force_powers`, `pvp_companions`) as host-authoritative values in `BehaviourDigest` (`BehaviourDigest.cs:60+`; mismatch policy in `BehaviourCompatibility.cs:95-110`). Bump the protocol version because new packet types are added (`ProtocolConstants.cs:101` documents the pattern). A peer lacking the capability never sees `PvpChallenge`, and the challenger's `duel` verb says "the other player's version does not support duels" (same wording rules as `pvp-product.md` §2). A mid-match digest change cancels the match.

## 4. Strict isolation

Entry is double-opt-in (host `pvp` != `off`, then explicit accept from both). Suspended for the pair while the match is live (INFERRED from the channel list, each NEEDS-LIVE):

- Progression (XP/credits): `Progression` channel off for duellists, so there is no XP or loot leak even if the engine awards XP on a defeated PC (it does not for a PC, but guard anyway).
- Inventory: `inventory_mode` shared pool writes and `CorpseTakes` / body bag loot suppressed for the duration; consumables used are tracked locally and refunded at match end (restore), or the shared pool is snapshotted and diff-restored.
- World state, journal, dialogue grants, containers, doors, mines: no writes caused by the match; the match rejects entry during dialogue, cutscene and minigame (matching `MinigameRule`) and cancels on area change.
- Combat sync: enemy damage and the sticky-aggro table ignore duellists (`ThreatTable` must not treat a phantom hit as a threat contribution, `PhantomHitHook.cs:60+`); companions stand aside (`pvp_companions = off`, benched via the existing release path).
- Health: both characters' hp and Force points are snapshotted at the countdown and restored at the end. Dying in a duel must not reach the death handling in `death-handling.md` (no "game over" or load). Use min-1-hp (`min-one-hp.md`) on both bodies during the match and treat reaching 1 hp as defeat, so the engine never plays a real death.
- Saves: block autosave while a match is live (`pvp-product.md` §5).

## 5. Recommendation and phases

Recommended model: **defender-resolved hit reports with host as round arbiter** (the hybrid in §2). Not (c) alone (the host lacks the stat blocks), not pure (a) (wrong AC and saves).

- Same-frame double KO rule: the loser is the first defender to report `Downed`, ordered by the **host's** receive time of `PvpResult`; ties within 100 ms are a draw (round replays). The defender decides its own death, so a client cannot be killed by a forged packet alone. The host rejects hit reports whose damage exceeds a per-weapon plausible cap, or whose rate exceeds one hit per attacker per ~1.5 s round (rate-limit plus clamp).
- Powers: attacker sends intent, defender rolls the saving throw on its real character.
- Pause: host-owned budget as in `pvp-product.md` §1.

Phases (effort in engineer-days, counting no live-testing delays; live rig time roughly doubles it):
1. Opt-in plumbing: capability, behaviour flags, challenge handshake, `PvpRound`, UI verbs, suspension of the campaign channels in §4, hp restore. 4-6 d.
2. Hit path: phantom hostility scoped to the pair, `PvpHit` with sequence and ack, defender apply through `ApplyDamageEffect`, attribution, double-KO resolution, `PvpResult`. 5-7 d.
3. Fidelity: AC sync onto the phantom, power saves, reliable `PvpEffect` and stun deadlines owned by the defender, pause budget. 5-8 d, most uncertain (NEEDS-LIVE: whether the phantom's defence stat is writable; whether the engine lets a phantom cast a power at the PC).
4. Hardening: clamps, rate limits, fault-injection runs (`NetworkSim` at 50, 100, 200 ms RTT and 5 percent loss), cheat-resistance review, `verify.ps1` coverage. 3-4 d.

Total about 17-25 engineer-days. The first two phases give a playable casual duel. The main risk is fidelity (§2), not transport.
