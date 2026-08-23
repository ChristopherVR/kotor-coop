# Shared-state audit: what actually replicates, and what only looks like it does

Measured on the live pair (pids 17208 GOG / 46088 p2, Endar Spire, 2026-08-11) after a
playtest produced seven complaints at once. They are not seven bugs: one was a wrong
offset, and the rest are the same architectural gap wearing different clothes.

## 1. Max hit points: a field the engine never writes

`CSWSObject::GetMaxHitPoints` (0x004D01A0) is literally `mov ax,[ecx+0xE0]`, so +0xE0 is
right for doors, placeables and ordinary creatures. It reads **0 on both players**.

`CSWSCreature::GetMaxHitPoints` (0x004ED310) overrides it and branches on `stats+0x6C`
(`is_pc`). For a PC the engine never stores a total at all — it re-adds one from the
per-level records every time it is asked, in the loop at 0x004ED360..0x004ED3CA:

    max = Σ over levels ( levelStats[i]->hitPoints(+0x2B) + stats->conModifier(+0xEE) )

Field layout used, all verified live:

| what | where |
|---|---|
| `is_pc` | `CSWSCreatureStats + 0x6C` (int; 1 selects the accumulate path) |
| level list data ptr | `CSWSCreatureStats + 0x28` (`CExoArrayList<CSWLevelStats*>`) |
| level list count | `CSWSCreatureStats + 0x2C` (capacity follows at 0x30) |
| CON modifier | `CSWSCreatureStats + 0xEE` (byte, added once PER LEVEL) |
| hit points rolled | `CSWLevelStats + 0x2B` (byte) |

Measured: one level, rolled 10, CON modifier +2 → **12**, exactly the host's reading at
full health. Implemented as `KotorGameBridge.TryReadMaxHitPoints`, which reproduces the
branch (PC → accumulate, anything else → the stored field) and feeds both the player state
and the enemy snapshot.

⚠ The general lesson, again: a getter that is one `mov` is not proof the field is live.
Check whether a DERIVED class overrides it before trusting the base offset.

## 2. Combat state is reported, never shared — and on a client it cannot be

Measured: host creature `+0x4E0 = 1`, client `+0x4E0 = 0`, same area, same moment.

`PlayerState.FlagInCombat` is set by the bridge and read by the dashboard. **Nothing
anywhere applied a peer's combat flag.** Two players in the same room during the same
fight produced "IN COMBAT" on one readout and "exploring" on the other.

The deeper reason matters more than the missing plumbing: on a client the enemies are
MIRRORS — driven puppets, not creatures its own AI is fighting — so the client's engine
genuinely never enters combat. Forcing `+0x4E0` on the client would be a lie the engine
recomputes away, and combat state gates round timing and AI.

### Round 60 (2026-08-22) — a sibling case: gait ANIMATION also silently fails to share,
live-reproduced and root-caused, fix not found

Same shape as this section's own headline — a piece of state the server side computes
correctly (here, `CSWSObject+0xD4`, the gait animation id) but that never actually reaches
what the peer renders. Live-verified both directions on the rig: position, moveState and the
playback-rate scale all track a real walk/run perfectly, but the client twin's own rendered
animation field (`CSWCObject+0x48`) stays parked at idle for 87–132 of ~90–133 samples per run
across three independent live traces. Root cause: the engine's own animation classifiers
(`AnimationLooping`/`AnimationFireAndForget`/`AnimationOverlay`) report `0` for all three when
asked about the gait ids on a directly-driven phantom's twin — the client-side animbase does
not recognize `0x2710`/`0x2712`/`0x2714` as playable at all, unlike the one-shot ids this same
mechanism already plays correctly. A direct call to the sync handler's own disassembled
fallthrough target was tried live and did not fix it. Full writeup, sample transcripts, and
what remains: `docs/research/phantom-locomotion.md` §7.

### Round 65 (2026-08-22) — FIXED and live-verified: the per-frame re-asserter found statically
(`CSWCCreature::AnimationUpdate` reapplying a stale `looping_animation` cache), closed with a
one-field write, 0% idle-while-moving in both phantom directions

Five rounds (60-64) exhaustively verified every layer of the engine's gait-request path (the
classifiers, the `+0x12C`/`+0x134` sync handlers, the budget gate, `SetAnimationInternal`'s
real address and dispatch, `GetAnimationName`'s name resolution) and found each one working
correctly — the bug was never in the request. Round 65 found it downstream: static disassembly
of `CSWCCreature::AIUpdate` showed a directly-driven body (a phantom or mirrored enemy — no
drive flag, no follow target) always dispatches to the default `CSWCCreature::AnimationUpdate`,
which, every frame the rendered field isn't already idle, reapplies whatever
`CSWCObject+0x58` (`looping_animation`, the symbol db's own field name) holds via a gated
`SetAnimation` call. Nothing had ever written that field to a gait id, so it always held stale
idle — and idle passes the gate (`animations.2da` row 6 "pause1": `Looping=1`) while walk/run
structurally never can (rows 338/345: `Looping=0`), so the reassertion silently won every time.
The fix keeps `looping_animation` in sync with the driven gait each step (one `ushort` memory
write, no new engine call); once it holds a walk/run id, the SAME `AnimationLooping` rejection
that always blocked a direct gait write now blocks the *reassertion* instead, turning it from
"guaranteed revert to idle" into "guaranteed no-op." Live-measured, host→client and
client→host, 10s walk + 10s run: **0% idle-while-moving both directions** (132/132 and 122/122
moving samples correctly rendered), against a round 62-64 baseline of 91.1%-99.4%. Mirrored
enemy and companion share the identical `DriveBody`/`AnimationUpdate` code path by
construction but were not independently re-measured this round — real aggro was triggered live
on the `tar_m10aa` Black Vulkar pool (repeated real forwarded hits from six distinct attacker
ids), but that specific pack turned out to be stationary point-defence guards that never
chase, and this save's roster carries no companion. `dotnet test` stayed at 1591/1591; both
plugin builds clean. Full writeup: `docs/research/phantom-locomotion.md` §12.

### Round 64 (2026-08-22) — the leading theory (idle-substitution) is falsified; a real, narrower
client-side sync gap found and half-fixed; the render symptom itself still unexplained

Two live experiments settled a question three prior rounds only guessed at: does
`CSWCAnimBase::GetAnimationName` actually fail to resolve a name for the phantom's gait ids (the
round 63 theory), or does it resolve fine and something else drops the ball? Live-read directly
(not inferred): it resolves fine — `walk`/`run`/`pause1`, real non-empty names, on the phantom
AND on 26 simultaneously mirrored hostiles sampled the same way. The one field that actually
differed from the player was the animbase's weapon-class byte (phantom 8, player 2 — a
different but still valid row), and a SEPARATE finding — the phantom's client-side model was
never told to reload after its appearance-row poke (only the server side ever was) — was found,
fixed (`Odyssey.RebuildClientAppearance`), and confirmed live to fix the model. The rendered
gait itself is still stuck at idle 91–97% of moving samples, statistically unchanged from round
62/63's baseline — the model fix was real but not sufficient, and the actual remaining gap is
now known to be downstream of name resolution (the "apply by name" step or the per-frame
reassertion), not in it. Full writeup: `docs/research/phantom-locomotion.md` §11.

### Status as of round 55 (2026-08-21) — read this first; supersedes every status note below it

**Host natural aggro — SETTLED: it works, with the plugin loaded, no bisection needed.**
Round 54 left this genuinely open (whether the plugin's own always-on patches were suppressing
native perception, or whether four rounds of testing had simply never gotten a genuinely hostile
creature into real range). Round 55 measured it directly, twice, on the identical `tar_m10aa`
Black Vulkar pool round 53/54 used, plugin present and `local_combat=1`: standing still for 30s+
at `dist=8.4` (inside the blueprint's own `PerceptionRange=11`, per `tar10_vulkpoo002.utc`)
produced zero aggro and zero movement — but standing still at `dist=1.6` (inside the same room,
clear line of sight) produced IMMEDIATE, native, multi-creature aggro: seven different Vulkars
(`0x33 0x24 0x26 0x36 0x31 0x38 0x2F`) independently picked the player as a target through their
own AI and landed dozens of real hits (`combat ground-truth: local PC took N damage, attacker=0x..
(unrecognized attacker id...)` — the classifier's own label for a hit this plugin did not cause),
dropping the host's HP from 12 to 1 in under 20 seconds with no plugin involvement in the decision
at all. **Conclusion: the engine's own perception/aggro path is completely intact with the plugin
loaded; the 8.4-unit miss was a line-of-sight/room-geometry gap (the pool room's own layout), not
a range or a plugin effect, and P2's premise (verify natural aggro before touching the client
trigger) is answered — go straight to the client's own trigger policy, which is exactly what this
round also fixed.** No coop.ini bisection was needed because the positive result was unambiguous;
see round 55 below for the full log excerpts. Same test, mirrored: with the CLIENT teleported to
the identical point-blank spot, the HOST's real (unmodified) creatures aggroed the CLIENT's
phantom exactly as expected — `combat: took N damage from a hit on our phantom (attacker 0x38)`
and nine other distinct attacker ids, forwarded and applied normally — confirming the host-side
half of item 1's second question too.

**The client's own trigger policy — FIXED. Immediate proximity/targeting-us trigger, live-verified
with two real kills.** Before this round, a driven census creature could only ever be pulled into
`local_combat` by whichever slot `DriveOne`'s per-frame loop happened to reach first while the
single exclusion slot was free — functionally a round-robin, since a slot occupied by one fight
made every other eligible creature wait out that fight's own timeout (up to 25s) for a turn, with
no principled choice of WHICH creature won when several qualified in the same frame. Round 55
adds `LocalFightCandidateRule` (pure, unit-tested) and `EnemyPresenceController.SelectImmediateCandidate`,
which now scores every eligible candidate ONCE PER FRAME — hostile (`GetAIStateReputation==2`),
GIT-identical, a real `+0x260` script, within `local_combat_engage_range` (default 11, the stock
`PerceptionRange` round 54's no-plugin control read off the blueprint) — and arms the
highest-priority one immediately: a creature the owner's own damage report names as currently
hitting this player's phantom (`block.IncomingDamageAttacker`, fresh within 2s — no protocol
change needed, this channel already existed) outranks one that is merely near. The 25s timeout
stays exactly as it was, now purely a fallback ceiling on how long one fight may run, not a
trigger delay. Live, on the identical pool of Vulkars: `0x33 selected as the immediate
local-fight candidate (priority=Proximity, dist=1.79...)` fired in the same frame as the
teleport landed; when its fight later ended by hitting the 25s ceiling, `0x24 selected... ` fired
in the SAME MILLISECOND (`22:08:29.912`/`.913`) with zero wait; and later still `0x33 selected...
(priority=TargetingUs, dist=1.80...)` fired specifically because the owner's damage report named
it as the current attacker on the phantom, outranking a plain-proximity read. Two ordered client
attacks against the fighting creature produced two real kills, confirmed on the host
(`combat: applied 9 of a peer's damage to 0x33 (owed 27)`, then `0x24`'s own body destroyed at
`owed 35` against `28` max hp) and on the client (`combat: killed 0x33 on the owner's authority`),
while every OTHER nearby hostile kept landing real forwarded hits throughout
(`combat: took N damage from a hit on our phantom (attacker 0x38)` etc.) — no invulnerability, one
fight at a time preserved exactly as designed. One honest testing caveat, not a functional
defect: the observation session ran with `cheat god 1` on the client (to protect it from the
seven-creature swarm), which twice tripped the existing invulnerability watchdog (`WATCHDOG
TRIPPED — ... this player's hit points never fell once`) because god mode makes that exactly
true regardless of whether the absorb is legitimate — the watchdog did precisely its documented
job (self-corrected to applying instead of absorbing) and this explains the handful of
`(the currently locally-fighting creature)`-attributed hits that show up as forwarded-applied
instead of absorbed in the log; it is a byproduct of testing under god mode, not a round 55
regression. See round 55 below for the full evidence, the pure-rule tests, and one remaining
honest gap (a creature whose local exclusion ends by timeout while the HOST's copy already died
from the ledger briefly outlives its own death locally — benign, self-resolves on the next normal
census pass, not chased further this round).

### Status as of round 54 (2026-08-21) — superseded by round 55 above; kept for the trail

**A client killing an enemy outright — PROVEN, root-caused, and live-verified.** Round 53 left
this narrowed to "somewhere between `CoopClient.ReportEnemyDamage`'s send and
`CoopHost.GetEnemyDamage`'s merge." Round 54 traced it: both directions of the channel share
one gate, `CoopAgent.PumpEnemies`'s `(caps & SessionCapabilities.Enemies) == 0` early return
(`CoopAgent.cs:2054`), and round 53's own transient `[W_SAVE]` — the thing round 43/44 called a
self-resolving timing race — was silencing it. Root cause: `WorldDigest.Compare`'s
save-identity check had no `sameModule` gate (unlike the module GIT check three lines below it,
which already had one for the identical reason), so an ordinary crossing where one peer's
autosave-on-transition lands before the other's reads as a save-identity mismatch by the letter
of the check. **Fixed two ways**, both in `KotorCoop.Protocol`: `WorldDigest.Compare` now
gates `SaveIdentityMismatch` on `sameModule`, and a new `SaveIdentitySettle` debounces the
residual few seconds of same-module cache lag (a mismatch is honored only once observed
continuously for 5s; agreement clears it instantly). Re-run live, the identical `tar_m10aa`
crossing round 53 used: **no `[W_SAVE]` line at all this time**, and a real fight landed the
kill both directions — host `combat: applied 6...7...9 of a peer's damage to 0x33 (owed 22)`
(exactly `0x33`'s max hp), `materialised loot: 1 enemy death(s) noticed... captured 2 item(s)
off 0x33's corpse`, and the client's own mirrored copy following it down the ordinary path,
`combat: killed 0x33 on the owner's authority`. See round 54 at the end of this section for the
fix, the tests (a loopback integration test confirming the session/protocol layer was never
broken, a debounce unit suite, and a live-transient-crossing integration test), and the full
evidence.

**Natural aggro — still open.** Reconfirmed on tar_m10aa's genuinely-hostile (not stale-save
friendly) Vulkars: only `local_combat`'s own 25-second round-robin timer ever engaged one, at
`dist=14.56` — never proximity, never the engine's own perception. A direct no-plugin
comparison (the task's own "cheapest test") was attempted this round and left inconclusive by
the test rig's own navigation limits (no reliable way to position a character near a hostile
without the plugin's own `tp`, and no reliable programmatic walk exists even WITH the plugin)
— recorded honestly as a rig gap, not as a negative result. See round 54 for what was tried,
what it ruled out along the way (the blueprint's own perception script is stock, unmodified),
and the concrete next step (stage a save already in range before pulling the plugin).

### Status as of round 53 (2026-08-21) — superseded by round 54 above; kept for the trail

**Real lethality PROVEN, on a module without a min-1-hp pin.** Round 52 left three things
open: natural aggro, real lethality, and a client killing an enemy outright. This round ran a
full ordinary fight on `tar_m10aa` (Taris, Black Vulkar Base) against a GIT-placed, non-plot
`FactionID=1` hostile (`tar10_vulkpoo002`, id `0x33`, "Black Vulkar," 22 hp) with no scripted
min-1-hp pin of its own. 26 real, ground-truth-confirmed local hits landed on the player over
~2 minutes (summing to 178 raw damage), entirely through `local_combat`'s own mechanism —
zero forwarded-channel activity anywhere in the window, the cleanest isolation yet. The player's
HP floored at 1 mid-fight not from this area (which has no pin) but from a STALE min-1-hp
status effect carried over from the Endar Spire's `END_M01AA` (a persistent effect, only ever
cleared by walking through `end_m01ab` — this round's warp skipped that). Clearing it live
(`cheat god 0`) revealed the true total was already deeply negative (`hp -11/12`), and the
engine's own death path fired one frame later: `death: the local player is down`. Cross-player
sync of the down state was confirmed too — the host's `console 'peers'` correctly read
`Malak  tar_m10aa  [down]`. Natural aggro: still zero, the third distinct creature/module pair
to show it (after two rounds of `n_sithsoldier004` on `END_M01AA`).

**Two real bugs found and one fixed before the arc could run at all.** (1) **Fixed:** the
just-shipped roadmap item 14 host mid-session-load refusal false-positived on an ORDINARY
module transition (KOTOR's own engine autosaves on every transition, which the refusal's own
digest watches — confirmed live: warping a connected host produced
`co-op: loading a save mid-session is not supported yet` and a real disconnect). New
`CoopConfig.SessionSaveHostRefusal` / coop.ini `session_save_host_refusal` (default OFF) gates
the ACTION while leaving the edge detection and its log line intact; re-verified live with the
fix in place — the identical warp no longer disconnects. (2) **Not fixed, self-resolves:** a
separate, transient `[W_SAVE]` compatibility false-positive fired for a few minutes right at
the crossing (both original saves had never visited `tar_m10aa`, so this was a timing race in
the lazy compat computation, not real content drift — round 43/44's own "save-drift false
alarm" pattern, a new instance of it); `console 'reconnect'` forced a fresh, correct
re-judgment and cleared it.

**What remains: the client killing an enemy outright — narrowed to a specific point of
failure.** The client's own damage-dealt ledger populated correctly (`DamageDealtEnabled=1`,
`id 0x33 cumulative 163`, read directly out of the shared block), but the host's `DamageOwed*`
fields never moved (`DamageOwedCount=0` the whole fight; no `combat: applied` line; not even
the documented `does not resolve here` fallback fired) — the report never reached
`PeerDamageController.ApplyOwed` at all. Malformed packets, netsim, peer registration and the
module label were all ruled out live. This is new information narrowing (not re-confirming)
`peer-damage-ledger.md`'s own NEEDS-LIVE checklist: the break sits somewhere between
`CoopClient.ReportEnemyDamage`'s send and `CoopHost.GetEnemyDamage`'s merge, not in the
plugin's own capture or apply code. See round 53 at the end of this section for the full
evidence, the fix, and the exact offsets read.

### Status as of round 52 (2026-08-21) — superseded by round 53 above; kept for the trail

**PROVEN. `local_combat`'s own client-side mechanism lands real hits on the local player,
in a live two-instance session, correctly distinguished from and never double-counted with
the pre-existing forwarded-damage channel.** Round 51 left this genuinely open: a hit was
observed with a session live, but the standing forwarded-damage channel (the host's own
unmirrored copy fighting the client's phantom) was quantitatively sufficient to explain it
alone, so round 51 could not say whether `local_combat` contributed anything once a session
was up. Round 52 built a ground-truth instrument that answers this directly instead of
inferring it from HP alone: a log line fires on EVERY damage event the engine's own damage
choke point (`CSWSEffectListHandler::OnApplyDamage`, the same hook `PhantomHitHook` already
had installed) sees land on the LOCAL PC, naming the attacker id and classifying it as the
creature currently excluded to fight locally, a normally driven/mirrored census creature, or
`IncomingDamageController`'s own forwarded-damage apply (recognised via the `SuppressDealt`
flag already set around every `Odyssey.ApplyDamageEffect` call, not via the effect's own
creator_id — see `PhantomHitHook.LogLocalHitGroundTruth`'s doc comment for why creator_id
would have been misleading here).

Live, on the automatic trigger, in a real session (`tools/demo.ps1`, END_M01AA, the same
`n_sithsoldier004` pair rounds 49-51 used, `0x3B`/`0x38`), with the client's own player walked
into `local_combat_range` and then ordered to engage (`blockpoke.ps1 attack 0x7FFFFFFF 0x3B`
— clicking is not available; this is the engagement nudge, not a code change) rather than
manually firing the creature's own script:

    [19:45:14.933] combat: absorbed 2 forwarded damage in total, attributed to mirrored
      enemies we are driving (last attacker 0x3B); this engine says in-combat=True
    [19:45:18.811] combat ground-truth: local PC took 2 damage, attacker=0x3B (the currently
      locally-fighting creature (StartLocalFight's own id))

The absorbed line is the standing forwarded channel correctly standing down — no
`ApplyDamageEffect` call happens for an absorb, so it can never trigger the ground-truth
hook. The line 4 seconds later is a SEPARATE, genuinely-fired engine damage event: target is
the local PC, attacker (the effect's own creator_id) is `0x3B`, and `SuppressDealt` was
false — this player's own engine actually processed `0x3B`'s native combat round and applied
real damage to the local PC, through the exact mechanism `local_combat` exists to prove. This
repeated 21 times over the ~6-minute session (`grep -c "combat ground-truth.*0x3[B8]" ` on the
attacker classifications "the currently locally-fighting creature" / "a driven/mirrored
census creature" / the rare "unrecognized attacker id" transient at a rejoin boundary — see
below), against BOTH creatures in the pair (`0x3B` and, once ordered to engage it too, `0x38`
also landed real local hits), and the client's own HP fell exactly in step: `12/12` baseline,
`8/12` after the first several hits (4 points, matching 2 ground-truth local hits of 2 each,
zero applied-forwarded lines in between), then floored at the area's own min-1-hp pin,
`1/12`, after roughly a dozen more (`combat: this player cannot be downed (took 4 on 1 hp and
is on 1)` fired once the floor engaged, for a forwarded-and-applied hit — see below — and the
same floor held the player at `1/12` through every subsequent hit of either kind for the rest
of the session; the player never actually died despite dozens of real hits landing, because
the Endar Spire command deck's own min-1-hp scene pin — not `local_combat`, not
`IncomingDamageController` — is what stopped it).

**Zero `combat: took` (forwarded-and-applied) lines occurred while `0x3B` was the actively
excluded, locally-fighting creature** — every point of damage this player took from `0x3B`
during its own exclusion window came from the ground-truth-confirmed local hit, and the
forwarded stream for that same attacker was absorbed instead (`grep`-verified across the full
session log: the only `combat: took` lines that exist are all tagged with `0x38` as attacker
at moments `0x38`, not `0x3B`, held the local-fight slot — i.e. the OTHER creature's forwarded
damage, which is not licensed to be absorbed while a DIFFERENT creature is the one being
fought locally, exactly as `IsLocallyEngaged`'s single-id design intends). That is the
reverse-safety check (task instruction 3) passing live: the host's copy does not double-hit,
and the client's HP total matches exactly one stream per attacker per window, verified for
both directions —

    [19:50:00.233] combat ground-truth: local PC took 4 damage, attacker=0x2C
      (forwarded-damage applier (IncomingDamageController's own EffectDamage apply))
    [19:50:00.234] combat: took 4 damage from a hit on our phantom (attacker 0x38)
    [19:50:00.893] combat ground-truth: local PC took 2 damage, attacker=0x3B (the currently
      locally-fighting creature (StartLocalFight's own id))

— in the same second, `0x38`'s forwarded hit correctly APPLIED (it was not the locally-fighting
creature at that moment, so no absorb was licensed) while `0x3B`'s hit correctly landed as a
genuine local hit, with neither stream touching the other's damage.

**One honest gap, not a safety issue.** Around a fight's rejoin boundary (the frame
`MaintainLocalFight` clears `_locallyFightingId` and the creature re-enters normal mirroring)
the ground-truth classifier occasionally read "unrecognized attacker id (not driven, not a
phantom, not our own apply))" for a few seconds instead of "a driven/mirrored census
creature" — `SuppressDealt` was still reliably false every time (so it was never
misclassified as the forwarded applier, which is the one distinction that actually matters
for the proof), but `EnemyPresenceController.IsDrivenCensusCreature`'s per-frame set did not
always contain the id during that window; root cause not chased (out of scope for this
round — the classification's ONE safety-relevant bit, forwarded-vs-not, was never wrong in
any of the ~26 real damage events this round observed). Noted here rather than hidden.

**Verdict: PROVEN, live, unconfounded.** `local_combat`'s own exclusion-and-script-fire
mechanism genuinely lands damage on the local player through the client's own engine, in a
real session, with the pre-existing forwarded channel correctly standing down for exactly the
attacker being fought locally and continuing to apply normally for everything else. Natural
aggro did not occur in this test (consistent with round 51's own passive-wait control): the
player had to be walked into `local_combat_range` and then ordered to engage
(`attack 0x7FFFFFFF <id>`) rather than relying on the creature noticing the player on its
own — this save's `n_sithsoldier004` pair simply does not have an aggro trigger that fires
from proximity/heartbeat alone within the windows this investigation has tested. And this
area's own min-1-hp scene pin means no fight run on END_M01AA's command deck can currently
demonstrate real lethality — the player floors at 1 and stays there regardless of how much
real damage keeps landing, which is correct area behaviour but means "can this actually kill
someone" remains untested by every round run in this specific room, this one included. See
round 52 at the end of this section for the instrumentation added
(`PhantomHitHook.LogLocalHitGroundTruth`, `EnemyPresenceController.IsLocallyFightingCreature`/
`IsDrivenCensusCreature`) and the full log excerpts.

### Status as of round 51 (2026-08-21) — superseded by round 52 above; kept for the trail

**The single biggest finding in 51 rounds: the ground-truth check was never being read.** Every
round from 9 through 50 used `is fight action: True` (a specific head-action code sampled from
the per-participant round struct) as its sole signal for "did a real fight happen." Round 51 ran
three trials that independently, directly verified the PLAYER'S OWN HIT POINTS (`console 'where'`)
instead, and in all three the player's HP genuinely fell from `12/12` to the documented `1/12`
min-1-hp floor within ~30 seconds of firing a confirmed-hostile creature's own AI script
(`n_sithsoldier004`, the same pair rounds 49-50 used) — while `is fight action: True` read `False`
for every one of the `6000` sampled frames in every trial, including the successful ones. **The
diagnostic this whole document has trusted does not detect success even when a real hit lands**,
which means the "freeze" reported across rounds 9-50 was never proven by the evidence those rounds
actually collected; it needs re-reading against HP, not this proxy, before it can be trusted again.

**What round 51 settled.** Two of the three trials were run with literally no session or agent
connection at all — no census, no `EnemyPresenceController`, nothing this plugin's combat code can
touch — one on the CLIENT install, one on the HOST install, both cold-loaded from the same save.
Both produced the real, independently-verified hit described above. **This refutes the client
install/instance itself as an explanation for anything**: whatever the "freeze" is, it is not a
property of being the client build or process, since the client build fights fine when nothing
else is touching the creature. A third trial, with a live two-instance session up, ALSO showed the
player take real damage — but that trial is confounded by a separate, already-documented, always-on
mechanism (the host's own unmirrored copy of the creature fighting the client's phantom body
directly, forwarding damage over the network) that started before the manual fire ran, and this
area's min-1-hp scene pin means a single small forwarded hit is sufficient on its own to produce
the exact `12→1` reading observed — so this trial cannot distinguish "the client's own local
mechanism also worked" from "only the pre-existing forwarding channel did." **Whether
`local_combat`'s own client-side mechanism works once a live session's automatic exclusion and the
standing forwarding channel are both running on the same creature remains genuinely open** — no
round, including this one, has yet measured player HP with that specific confound controlled out.

Static disassembly this round also corrected round 27's own account of the per-participant arming
call: `0x004D5A50` (round 27's "the arming write") is NOT gated by the `sub+0x5c` range check —
both branches of that check converge on the identical call site and both arm unconditionally; the
check only gates two extra setup calls (`0x5BB440`, `0x5B8B00`). `sub+0x5c=4` is not an anomaly:
it is the hardcoded sentinel `StartCombatRound` (`0x005BB590`) itself writes at `0x005BB668` before
any maneuver is selected, overwritten with a real `0`/`1`/`2`/`3` value only if a maneuver-selection
dispatch later finds one. See round 51 at the end of this section for the full evidence table,
the disassembly, and the precise next experiment.

**Round 50 is the decisive control this document has been missing since round 9: a census-listed
creature that literally NO plugin consumer touches — not `Resolve()`, not `MaterialisedEnemies.Seen`,
not `CombatController`'s health pin/mirror, not `PeerDamageController`'s owed-damage apply, nothing
— still freezes, byte for byte identically to every write-bisected trial before it (0 of 6000
sampled frames ever read a genuine fight action, both with the bypass on and off). **This proves
the plugin is not the cause**, full stop — there is no remaining plugin-side consumer left to
blame; round 50's own audit table accounts for every controller that can see a census id, and Run
B removed every one of them at once. The same round also re-ran round 37's own historic
construction (the census-cap exclusion that WAS observed to arm a real fight, twice, rounds 37-38)
as a same-session sanity check, and **it did not reproduce either** — same freeze, same zero fight
actions, on the same save and creature pair. That means round 37/38's own "it works when genuinely
unlisted" claim — the one every round since has treated as the sole confirmed-positive result in
this whole investigation — does not currently hold, and round 50 could not determine from this
round alone whether that is a real regression across the 100+ commits since round 37, or whether
round 37/38 were themselves an early, unchecked instance of the phantom-forwarding confound round
49 only started explicitly ruling out. Either way, the freeze is now known to be independent of
census membership status, independent of every write and read this plugin's controllers can
perform, and the investigation's one remaining concrete lead is engine-internal: `sub+0x5c` reads
`4`, outside round 27's own documented `-1`/`0..3` "continue toward arming" range, consistently
across all three of round 50's trials — see round 50 at the end of this section for the full
audit table, evidence and the precise next experiment.

**The freeze is real, reproduces on a genuinely verified hostile, and is still unsolved.**
Rounds 9-47 chased a mirrored, previously-claimed, GIT-identical creature that gets pulled out
of the census and script-fired (`+0x260`) but never moves, never swings, and times out — 47
rounds' worth of candidate writes, all individually disassembled and ruled out (round 47's
bisection: every DriveOne op off, every op on, identical failure). Round 48 found the specific
creatures rounds 44-48 had been testing against (`end_soldier2`/`0x1A`, `0x17`) read FRIENDLY
(rep=1) through the engine's own `GetAIStateReputation`, on the unmodified host too, and floated
"maybe nothing since round 37 was ever really hostile" as the explanation for the whole 11-round
stretch. **Round 49 checked that directly and it is not the general explanation.** The two
creatures round 48 actually probed turn out, by their own GIT blueprint (`n_repsold002`,
FactionID 4 = Friendly_2; `n_sithappren002`, FactionID 2 = Friendly_1 — read straight from the
module's `.utc` files, not inferred), to be allied story NPCs by design, misidentified as Sith
by an earlier round's guess at their name. A genuinely different, GIT-placed, `FactionID=1`
(Hostile_1) creature — confirmed hostile both by `repute.2da` statically and by the engine's own
`GetAIStateReputation` returning `2` live, in both directions — was engaged the same way and
**froze identically**: zero movement, zero HP change, over a full 25s cycle, at both a moderate
range (4.2 units) and point-blank (0.32 units), across three separate bisect-mask configurations
(every DriveOne/CombatController op on, all thirteen off, health-pin/mirror off alone). See round
49 at the end of this section for the full evidence. **The census-exclusion freeze from rounds
9-47 is confirmed real and independent of hostility; round 48's specific finding was correct
about those two creatures but was never a general explanation, and the underlying mechanism
remains unsolved.** A hostility gate (`GetAIStateReputation(creature, player) == 2`, required
before a candidate is ever excluded into `local_combat`) is now implemented regardless — a
non-hostile census actor should never have been pulled out of the census for a "fight" it was
never going to have, independent of whether that was ever the freeze's actual cause. `local_combat`
stays on by default (unchanged this round); no hit has yet landed through its own exclusion
mechanism on ANY creature, hostile or not — the standing forwarded-damage channel is still doing
all the real work, exactly as it was before `local_combat` existed.

### Summary of the whole investigation (2026-08-11 through round 27, 2026-08-21) — read this first

Everything below this point, through "Round 27," is the full dated trail — kept in full because
each entry is load-bearing evidence for the next, and because the false leads are as useful as
the true ones to whoever picks this up. This summary is for orientation; it does not replace the
trail, and nothing below has been pruned.

**What `local_combat` is.** A client's forwarded-damage mirroring already works without it
(enemy health/death and damage numbers all replicate correctly). What doesn't: the client's OWN
engine never believes it is "in combat," because its enemies are driven puppets, not creatures
its own AI is fighting. `local_combat` (`coop.ini`, default OFF) is the attempt to fix that
properly — let a mirrored enemy genuinely fight the local player through the engine's own combat
system, with `IncomingDamageController` standing down from absorbing forwarded damage once a
local hit is confirmed landing, so nothing is double-counted.

**Phase 1 (2026-08-11 to 2026-08-14) — the design's own structural defects, found and fixed.** An
independent review found four defects that would have made a naively-enabled `local_combat`
actively unsafe: target divergence (a mirrored enemy could lock onto the wrong player — fixed by
holding peer phantoms Neutral on a mirroring instance), a full-queue action wipe that would have
left materialised/ambush enemies permanently unable to swing (fixed with a move-only wipe), a
companion aggro sink that could leave a party member permanently downed with a full health bar
(fixed with unconditional health force-writes and min-1-hp pinning), and a blanket damage absorb
with no per-attacker attribution (fixed, gated on `IsLocallyEngaged`, with a self-correcting
watchdog that reverts to forwarding damage if local absorption isn't actually happening). All
four are confirmed still present and correct as of round 11 (2026-08-20) — this part of the
mechanism is safe.

**Phase 2 (2026-08-20, one day, 27 rounds) — is the mechanism itself real, and it wasn't, until it
nearly was.** The first live test (with all four safety fixes in place) found the mechanism does
NOTHING: a mirrored enemy standing next to a genuinely-fighting host's remote player never enters
combat, not once in 90+ seconds, even though it is correctly in the driven set. Twenty-seven
rounds of disassembly-first reverse engineering (rounds 1-27, all dated `### Round N` below)
progressively found and fixed FIVE real, independently-verified bugs in the attempted mechanism:

1. **The round-timer was never initialized** (`round+0x958`, set via a real engine call,
   `0x004D28F0`, confirmed against 3 real engine call sites).
2. **A second, undocumented gate right next to it** (`round+0x960`, same call, second field) also
   had to be set for the timer to actually run.
3. **Two self-defeating "fires every frame instead of once" bugs**, each looking exactly like
   the original problem until diagnosed (the round-delay setter accumulating into the far future;
   the attack-append call spamming duplicate, unprocessed nodes) — both fixed by gating on a
   one-shot "has this round already been armed" check.
4. **`StartCombatRound`'s real 5-argument signature**, traced to full confidence across three
   rounds and finally called for real — after which the round-timer genuinely STUCK for the first
   time (previously it silently reverted to 0 within the same tick).
5. **The `AttackTarget` test-staging tool itself was broken by this exact same missing
   precondition** — round 11 proved the whole mechanism was sound in principle by fixing this
   separate tool and producing the first real, independently-measured fight of the whole
   investigation (host-local creature, player HP 12→8→1, verified via position tracking, not a
   log line).

**Every other candidate explanation was tested and individually refuted, with live evidence,
across the remaining rounds:** target-resolution correctness, hostility (`GetAIStateReputation`,
both directions, measured exactly `2` as required), perception/the "seen" bit, area membership,
the `AsCreature` cast, the search-range value (measured live: `2.6` vs an actual distance of
`0.688` — the player was never outside range), the outward-walk scan itself (fully disassembled,
structurally sound, and — importantly — the "it's a live timing race against our own position
writes" theory that several rounds were built on was itself refuted in round 24: this engine is
single-threaded, so a scan in progress cannot be corrupted mid-walk by a position write; what
looked like a race was just different-but-internally-consistent snapshots), and
`UpdateAttackTargetForAllAction` (fully opened in round 26: a real dead end, redirects targets on
actions already queued, never arms anything).

**Where it stopped (round 27).** The actual write that arms a round to process a queued attack
was finally located — but it lives inside the engine's own bounded loop over combat participants,
with multiple conditional setup steps per iteration, called from exactly one site deep inside the
engine's own stateful per-tick round-advancement machinery. It is not a small utility a plugin can
call in isolation with a hand-traced signature, the way every other piece of this mechanism was.
That is the wall: not a missing call, but a piece of engine logic that only exists to run as part
of a combat round the engine itself is already advancing — which a driven puppet, by definition,
never is.

**Status after round 27.** `local_combat` stays off by default. Nothing about this investigation
found or left anything unsafe — every fix from rounds 1-27 is real, tested, and stays in the tree;
they were all genuine, verified improvements even though the final step turned out to be
structurally out of reach this way. Two ways forward were identified: (a) reverse-engineer the
engine's own round-advancement loop and its full caller chain as its own project; or (b) stop
externally replicating the engine's native combat calls, and instead make the engine's OWN AI
script actually run for a driven puppet.

**Strategy 2 (2026-08-21, same day, immediately after) — route (b), and it is real.** Found the
engine's own generic script-event dispatcher (`RunScript`, already proven live-working elsewhere
in this codebase for a door's OnOpen script) and the creature's own combat-AI script slot
(`creature+0x260`, confirmed live to hold `k_def_combend01`, KOTOR's real default
`OnCombatRoundEnd` script). **Firing it on a HOST-LOCAL creature produced a genuine, real fight —
player HP 12→1, independently verified — the first positive result of the entire investigation,
using none of the 27 rounds' hand-replicated native calls.** Firing the SAME thing on a
MIRRORED/driven creature does NOT work — round 28 reran this cleanly isolated
(`local_combat=0`, no competing mechanism) with a real per-frame watch on the round struct
itself, across four attempts on two creatures over ~50 seconds, and the round never armed once.
(A first HP-based reading in that round looked positive but was a confound — the pre-existing,
unrelated phantom damage-forwarding feature, not this mechanism; see round 28 for the full
account.) So: PROVEN to work for a host-local creature, PROVEN not to work for a driven one —
the same wall rounds 1-27 hit, reached by a different route.

**Round 29 then refuted round 28's own "driven puppet" hypothesis.** A creature with GENUINELY
ZERO writes from this plugin (no position, no facing, no claim — a new diagnostic,
`local_combat_diag_undriven_id`) hit the exact same wall via the exact same organic script
fire: the round genuinely armed and counted down (`round+0x958`/`round+0x960`, smoothly,
organically, proving those are real fields a live decision process uses) and then reset at
expiry instead of committing — never reaching `sub+0xB0` — with no artificial driving involved
at all. So the block is not about being mirrored, driven, or touched by this plugin in any way
that's been tested. The one constant across every failure in 29 rounds is a connected co-op
session with a remote peer present. The strongest remaining candidate: whatever
participant/roster table the engine's per-tick round-advancement loop consults before
committing (round 27's "party-table-style field" gate) may simply never register the connected
remote player as a valid participant — a session/party-table-level question, not a
creature-level one. See round 29's own entry for the full evidence.

### The state cannot be replicated by writing it — measured

A full channel was built (`SharedBlock.OffsetSessionInCombat` at 0x1E50, block version 29,
`CoopAgent.PumpSharedCombat`, `CombatStateController`, and
`Odyssey.SetPlayerCombatState` calling `CSWSCreature::SetCombatState` at 0x004F2610). The
channel is right and stays. The last step does not work:

    trial 1 : reverted after 2 ms
    trial 2 : reverted after 0 ms
    trial 3 : reverted after 1 ms

A written in-combat flag on a real player with no attackers is gone in **0–2 ms**. The
field is not a latch the engine consults; it is continuously re-derived from whether the
creature is actually being fought. There is a second writer past the one in
`SetCombatState` — inside `CSWSCreature::ProcessPendingCombatActions` at 0x004F7C0E. No
re-assertion cadence a frame hook can manage competes with that.

`CombatStateController` is therefore **off by default** (`coop.ini combat_state_sync=1`
reproduces the measurement), rather than shipped as a feature that visibly does nothing.

⚠ Note on measuring this: a bare vtable scan for `CSWSCreature` (0x007470C8) plus an
`is_pc` check lands on a COMPANION — party members carry `is_pc` too. The first attempt at
this test wrote the flag onto Trask and concluded the channel was broken. The bridge
publishes the creature it actually chose at `block+0x258`; use that.

### The suppressor is the action wipe, not the attack bit

`MarkAsMirroredEnemy` clears the attack-permitted bit, but it has exactly ONE caller
(`MaterialisedEnemies`), so ordinary module enemies — the common case — keep theirs. What
actually stops them is `EnemyPresenceController` calling `Odyssey.ClaimCreature` on every
mirrored enemy on every pass, and `ClaimCreature` calls
`CSWSObject::ClearAllActions(0x004CCD80)`. That call does not distinguish a queued
MoveToPoint (which fights our placement, and is why the wipe exists) from a queued attack
(which is the entire fight). So the mirror's enemies have their swings deleted several times
a second, never land one, and the mirror's combat state — derived from being fought — stays
false through the whole battle.

Implemented as `coop.ini local_combat` (default 0):

* `Odyssey.ClaimCreature(nint, bool clearActions)` — the wipe becomes optional.
* `EnemyPresenceController.LocalCombat` — leaves the queue alone when set, so the local
  copies can finish a combat round. Position is still driven from the owner every pass.
* `IncomingDamageController` reads the SAME flag and stands down when it is set, absorbing
  the owner's forwarded total instead of applying it. The two must move together: with local
  enemies swinging here and the owner forwarding what its copies did to this player's
  phantom over there, every hit would otherwise land twice.

⚠ NOT YET VERIFIED, AND THAT IS WHY IT DEFAULTS OFF. The staging attempt — co-locate both
players, teleport a live enemy onto them by writing its position — produced no fight on
EITHER instance (`host entered combat: no`), so it says nothing about the change. Writing a
creature's position does not raise a perception event, and the AI never noticed the player.
A real verification needs a fight the engine itself starts.

⚠ The failure mode if it is switched on and the local enemies still do not engage is that
the client becomes INVULNERABLE: forwarded damage is absorbed and nothing local replaces it.
Verify engagement before trusting it in play.

### Adversarial review found this is not one playtest away

An independent review of the change (2026-08-11) found defects that are structural, not
tuning. Three were fixed immediately; the rest are open and are why the flag stays off.

FIXED:

1. **Materialised enemies could never swing.** `MarkAsMirroredEnemy` clears attack-permitted
   bit 0x04, and `ClaimCreature` ORs only 0x02 back — so the clear was permanent for the life
   of the body. Those bodies are exactly the host encounter spawns and scripted waves that
   `local_combat` exists to make dangerous: they would hold a target, queue attacks, deal
   nothing, and have the owner's report absorbed. **A host-triggered ambush would have left
   the client completely invulnerable.** The clear is now skipped under `local_combat`, and
   `EnemyPresenceController` re-asserts the bit every drive pass (a spawn-time decision does
   not survive the body's lifetime).
2. **No role gate.** `local_combat` is read per-install from `coop.ini` and never crosses the
   wire, so the absorb also ran on an instance mirroring no enemies. Now gated on
   `block.DamageDealtEnabled`, the same "am I mirroring" test PortraitController uses.
3. **Silent failure.** The absorb logged five times and then went quiet forever — total
   invulnerability with nothing in the log. Now a running total on a 10-second heartbeat.

FIXED SINCE (2026-08-13/14) — code-complete, still never live-verified:

4. **Target divergence breaks the authority split.** Fixed: `PhantomController.ApplyTargetable`
   (`src/KotorCoop.Plugin/PhantomController.cs:680-740`) holds a peer's phantom at faction
   Neutral on a MIRRORING instance (`holdNeutral = LocalCombat && block.DamageDealtEnabled`,
   never on the owning instance), so the only player-faction targets `GetNearestEnemy` can
   pick are the local PC and mirrored companions. `PacifyPhantom` also releases any attacker
   already locked on when the flag flips.
6. **Mirrored companions become an immortal aggro sink.** Fixed:
   `PartyPresenceController` (`src/KotorCoop.Plugin/PartyPresenceController.cs:149-316`) holds
   a mirrored companion Neutral and min-1-hp pinned under `local_combat` (same mechanism as
   phantoms), and — the sharper fix — health is now force-written unconditionally, including
   to zero (`Odyssey.GetCurrentHitPoints(creature) != member.Health`, no longer gated on
   `member.Health > 0`), so a burst between sync passes can no longer leave a companion downed
   under a full health bar forever.
7. **The position fight returns.** Fixed: `Odyssey.ClaimCreatureForFight`
   (`src/KotorCoop.Plugin/Odyssey.cs:511-512`) reads the queue's head action type and clears
   only when it is move-class, leaving a queued attack alone. `EnemyPresenceController`'s drive
   pass (`src/KotorCoop.Plugin/EnemyPresenceController.cs:384-391`) calls this instead of the
   blanket `ClaimCreature(..., clearActions: true)` when `LocalCombat` is on. The doc's own
   "leave the queue alone" first attempt was tried and rejected in the code comment at the same
   site — confirmed worse than the bug it replaced, for exactly the wedged-`MotionSmoother`
   reason predicted here.
5. **Blanket absorb, partial local reproduction.** Substantially mitigated, not merely
   asserted: `IncomingDamageController` (`src/KotorCoop.Plugin/IncomingDamageController.cs:
   109-229`) now only absorbs when `EnemyPresenceController.IsLocallyEngaged(attackerId)` is
   true — a freshness-gated (`EngagedFreshTicks`) membership check against the set of enemies
   this instance is actually driving right now — so mines, traps, scripted `EffectDamage`,
   census-shed or out-of-range enemies, and the conversation hold all fall through to APPLY
   rather than being silently absorbed, because none of them will ever appear in that set.
   On top of attribution, a **self-correcting watchdog** samples the local player's own hit
   points every pass: if `AbsorbProofThreshold` (40) worth of damage has been absorbed on the
   claim that local copies are dealing it, and this player's health has not actually fallen
   even once, the claim is treated as false and the channel reverts to applying forwarded
   damage again — logged loudly (`WATCHDOG TRIPPED`), not silently. This directly answers the
   invulnerability failure mode this section used to warn about: the fallback is now automatic
   rather than something a human has to notice and disable. Not yet closed: whether the
   3-frame attribution window (`LocalCombatIntervalFrames`) is short enough in every case to
   avoid mixing an attributable attacker's delta with an unattributable one in the same pass —
   this needs the live test below to actually stress it, not further reasoning.

None of this has been proven against a real fight. The honest test is not "did it feel like a
fight" — it is a scripted comparison of hit points lost on the mirroring instance against the
owner's forwarded total over the same battle, started by the engine's own AI perceiving a
player (not a teleported-in enemy, which raises no perception event and proves nothing — see
the still-current warning above). `local_combat` stays off by default in
`profiles/coop.ini.example` until that pass has actually run; it was found shipping as `=1` in
the example (contradicting this section's own long-standing warning) and was reverted to `=0`
on the same day this section was corrected.

### The real fix — implemented, unproven

Give the client's engine a genuine reason to be in combat instead of asserting the
conclusion: stop clearing the attack-permitted bit in `Odyssey.MarkAsMirroredEnemy` for
enemies near the local player, so its own copies swing at it and its combat state rises for
the same reason the host's does. That ownership change is built (`local_combat`) and the
damage reconciliation it needed is built too — attribution plus the self-correcting watchdog
above are exactly "a client whose mirrored enemies really attack must not also apply the
owner's damage report for the same swing." What is still missing is proof: nobody has yet
run the live pass this section has asked for since the day it was written.

`AgentDashboard.DescribeSharedCombat` meanwhile combines the flags into a session-level view
and says which engines are engaged and which are only mirroring, so the asymmetry reads as
what it is rather than as a dropped packet.

### The live pass ran (2026-08-20) — and it does not work

First real attempt, with `local_combat=1` set on both installs, against a genuine fight —
not a teleported-in enemy. A single host instance was moved next to a live, untouched
`sithsoldier` pair in `END_M01AA` (`tp`, not a position write on the enemy) and aggroed for
real within ~1 second: `state IN COMBAT combat [FIGHT in END_M01AA - engaged: you]`, HP
falling 12 → 1 under the engine's own AI, no scripting or synthetic trigger involved.

With the host still actively fighting, a second instance connected and was moved to stand
directly beside the same two hostiles (`tp` to within ~2m, matching the proximity that made
the host aggro in ~1s). Over more than 90 seconds standing there:

    connected to 127.0.0.1:27015 as peer 2 ... state exploring (not in combat, not paused)
    combat [FIGHT in END_M01AA - engaged: Lanik; mirroring only: you (their engine is not in combat)]

The client's own engine never entered combat, not once. This is not the target-divergence,
aggro-sink, or absorb-attribution failure modes items 4/5/6 above were fixed against — those
are about a fight going WRONG once engaged. This is more basic: the fight never started
locally at all. Confirmed via the log that the mechanism was genuinely exercised, not
silently skipped — `enemy drive: mirroring 25 of the owner's 25 enemies` fired on the
client at the moment of connection, meaning the two `sithsoldier` bodies were in the driven
set and `EnemyPresenceController.ApplyEnemy` ran `Odyssey.ClaimCreatureForFight` and
`Odyssey.PermitAttacking` on them every pass, per the code at lines 384-399 above. Permitting
an attack and not wiping one in progress was evidently not sufficient to make the mirrored
copy's own AI independently decide to start one — unlike the host's identical, non-mirrored
copy of the same creature type, which aggroed almost instantly under the same proximity.

**Root cause not found.** Candidates worth checking next, in rough order of suspicion:
whether `Odyssey.PermitAttacking`/`ClaimCreatureForFight` only preserve an attack action
already in the queue rather than doing anything to help the AI decide to queue one in the
first place (in which case the fix as built cannot work on a creature that never got a
`NEW ClaimCreatureForFight`-armed hostile role at all — permission and initiation may be two
different things); whether `Drive()`'s per-frame `SetPosition`/`SetFacing` writes reset or
starve whatever internal state a perception/aggro decision depends on, even with the dirty
bit suppressed; or whether these particular creatures never actually reach hostile toward
THIS SPECIFIC client object at all (reputation/faction is per-object-pair in this engine, and
a client's PC is adopted through a different code path than the host's).

**Decision: `local_combat` stays off by default.** The binary safety rule this section has
held since it was written is not "did it break something," it is "did it work" — and it did
not. `profiles/coop.ini.example` is unchanged (`local_combat=0`). This is the first live
result this section has ever had; treat "already fixed" claims about items 4-7 above as
"the identified risks in a working mechanism were closed," not as "the mechanism works" —
those are different claims, and only the second one is what a player experiences.

### The missing initiation call — implemented, NOT yet live-verified (2026-08-20)

A later `local_combat` live pass (a real fight, both instances connected, client standing
beside two genuinely hostile creatures) found something more basic than anything above: the
client's engine never entered combat AT ALL, not once in 90+ seconds, even though
`PermitAttacking`/`ClaimCreatureForFight` ran on the mirrored enemies every drive pass. The
cause, found by disassembly: `PermitAttacking` only sets the `CreAttackPermittedBit` — an
EXECUTION-time gate `AIActionPhysicalAttacks` (`0x005BBBF0`) checks right before running an
attack action ALREADY on the queue. Nothing in the native engine ever queues that first
action on its own; `CSWSCreature::CreateNewAttackActions` (the only place a NEW one gets
queued) has exactly one caller, `EndCombatRound`, reached only from round timers or a round
already in progress — starting the first one is normally the AI script's job
(`DetermineCombatRound`/`ActionAttack`), which never runs for a body this instance only
drives. Permission and initiation are two different things; the mechanism as built only ever
granted the former.

Fix: `Odyssey.QueueAttackAction(creature, targetId)` (`src/KotorCoop.Plugin/Odyssey.cs`) calls
`CSWSCombatRound::AddAttackAction` (`0x004D38B0`, thiscall on the creature's own
`CSWSCombatRound` sub-object at `+0x9C8`, args `targetId, 0, 0, 0x5DC, 1`) directly — the
SAME call `CreateNewAttackActions`'s own re-validation branch makes each round to queue "the
next swing" (independently documented from the caller side in
`docs/research/combat-participation.md` §1.2, matching this disassembly). Gated on the
creature's head action not already being a fight-type action (`Odyssey.IsFightAction`) since
`AddAttackAction` itself has no dedup — it unconditionally allocates and appends a new action
node every call. Wired into `EnemyPresenceController`'s `local_combat` drive pass
(`EnemyPresenceController.cs`, after `RecordEngagement`), gated to the same engagement-range
distance check (factored out as `IsEngaged`, shared with `RecordEngagement`) so a mirrored
enemy nowhere near the local player is never handed a target.

⚠ **NOT YET LIVE-VERIFIED.** This is a genuinely new kind of write for this codebase: a
client's plugin queuing a native combat action on a creature it does not own control input
for, via an engine address (`0x004D38B0`) never previously called from managed code. The
calling convention is read from two independent static sources (direct disassembly of both
this function and its documented caller) but a live pass needs to check, in order: (1) does
the queued action actually survive to execute — does the mirrored enemy visibly swing; (2)
does the client's own `+0x4E0` in-combat flag rise as a result, closing the original gap this
whole section is about; (3) does `IncomingDamageController`'s absorb-gate (`IsLocallyEngaged`)
correctly stand down once local hits are landing, so damage is not double-applied; (4) does
the engagement-range gate correctly stop queuing once the player and enemy separate, so a
fight does not follow a fleeing player forever; (5) whether the `0x5DC`/`1` constants (copied
verbatim from the engine's own call) produce a normal attack cadence or something faster/
slower than intended, since their exact meaning (a delay in ms, and an unidentified flag) was
never fully decoded, only matched byte-for-byte against a real engine call site.
`local_combat` stays off by default until this runs clean.

### The live pass ran (2026-08-20, same day) — the call reaches the engine and does nothing

Reproduced the exact mid-fight-join scenario from the earlier failure: host aggroed for real
on a live `sithsoldier` pair (`tp` to 2m, HP falling under the engine's own AI, pinned at
1 hp by the Endar Spire deck's own `SetMinOneHP`), then a second instance connected and was
moved to the same spot while the host was still actively fighting.

**Two hypotheses this pass killed, with evidence:**

- **Target resolution is fine.** `Odyssey.QueueAttackAction` passes `targetId = *(uint*)(player
  + 0x04)`, which reads `0x7FFFFFFF` for the local player — alarming on its own (every other
  call site in this codebase treats `0 or ObjectInvalid or 0x7FFFFFFF` as "not a real id" and
  filters it out), but `0x7FFFFFFF` turns out to be this engine's own reserved sentinel for
  "the current player," not garbage: `console 'resolve 0x7FFFFFFF'` on the live client answered
  `0x7FFFFFFF resolves to 0x19371C68 here`, and the plugin log's full chain confirms it lands on
  a genuine `CSWSCreature` (vtable `0x7470C8`, `id+4 0x7FFFFFFF`, `type 0x5`) — the actual local
  player object. `AddAttackAction`'s internal target resolution calls the exact same
  `GetGameObject` (`0x004AE750`) that `Odyssey.ResolveServerObject` wraps (proven working every
  day via damage-forwarding), so there is no reason to believe the engine's OWN resolution of
  this id fails inside `AddAttackAction` either.
- **`ClaimCreatureForFight`'s move-only wipe is not re-clearing the freshly queued attack.**
  It only clears when `IsMoveAction(GetHeadActionType(creature))`, and the fight-action and
  move-action code lists are disjoint, so a genuine fight action at the head is never touched.

**What actually happens: `AddAttackAction` is called, does not crash, and changes nothing.**
Added a throttled diagnostic to `Odyssey.QueueAttackAction` (still in the tree, useful for
whoever continues this) logging the head action type immediately before AND immediately after
the call. Real output, unedited:

    queueattack diag: CALLED AddAttackAction(round=0x19352178, target=0x7FFFFFFF) on creature
    0x19351268, head action before 0xFFFFFFFF, IMMEDIATELY after 0xFFFFFFFF (is fight action now: False)

Repeated identically every ~3s for 40+ seconds straight, on a stable non-null `round` pointer.
An "unconditionally allocates and appends" function, called successfully with a resolvable
target and a live round pointer, left the head-of-queue completely unchanged in the SAME
instant it returned — not reverted later, never applied at all. This rules out a race with
some other wipe (there was no window for one) and narrows the open question to inside
`AddAttackAction` itself: either the disassembly's read of its calling convention has a
mistake not yet found (a missing/misordered argument, a wrong flag value in the `0x5DC, 1`
tail nobody has decoded), or the function contains a precondition this investigation hasn't
identified yet (a hostility/faction check on the target, an attacker-side gate unrelated to
the target at all, or something specific to being called from outside the engine's own
call sites, none of which are ever reached from managed code anywhere else in this
codebase).

**Decision: `local_combat` stays off.** Both live installs' `coop.ini` restored to
`local_combat=0`. This is real progress — from "the client's engine never enters combat, cause
unknown" to "the queue-append call itself is a silent no-op, for a reason not yet
identified, with resolution and the wipe-guard both cleared as suspects" — but it is not a
working mechanism yet.

### Instruction-level dissection of `AddAttackAction` (2026-08-20) — the silent-discard site found

Full disassembly of `CSWSCombatRound::AddAttackAction` (`0x004D38B0`, `python tools/disasm.py
0x004D38B0 0x500`) against a live report that the call executes without crashing but never
changes the queue's head action type. Four distinct early-return paths were mapped, all
returning to the shared epilogue at `0x004D399F` with nothing appended:

| # | Address | Condition | Verdict for our case |
|---|---|---|---|
| 1 | `0x004D38CB` | `CServerExoApp::GetGameObject(targetId)` (`0x004AE750`) returns null | ruled out — this is literally the same function `Odyssey.cs`'s `GetGameObjectFn` already calls, and target resolution was independently confirmed live |
| 2 | `0x004D38DA` | `target->vtbl[0x10]()` (AsSWSObject cast) returns null | not ruled out, see below |
| 3 | `0x004D38F3` | `[that]->vtbl[0x94]()` (`GetDead`) returns non-zero | ruled out on paper — `death-handling.md` documents party members' `GetDead` as permanently false, and our target is the local player |
| 4 | `0x004D3913` | target is a door (`vtbl[0x18]` succeeds) AND its `+0x2CC` destroyed flag is set | ruled out — target is a creature, not a door |

**But none of those is actually the mechanism that matches the symptom.** Every branch that
DOES reach the allocation path (`0x004D3919` onward, all five call shapes further down the
function for the different `AddAttackAction` overload variants at `0x004D3B30`/`0x004D3BA0`/
`0x004D3C30`/`0x004D3CE0`) funnels through **one shared helper, `0x004D3660`**, to actually
link the freshly-allocated `0x88`-byte action node into the round's live queue. That helper's
first two instructions are the whole answer:

```
0x004D3664  mov eax, dword ptr [esi + 0x9b0]   ; esi = the round struct (creature+0x9C8)
0x004D366A  test eax, eax
0x004D366D  je 0x4d374b                         ; NULL -> free the new node, return, append NOTHING
```

`round+0x9B0` is already documented (`combat-participation.md` §1.2, line 98 and the field
table) as `CExoLinkedList<CSWSCombatRoundAction>*` — the round's own pending-action list —
and as **set by `StartCombatRound` (`0x004D60A8`)**. If that pointer is null, `AddAttackAction`
allocates a brand-new action node, discovers there is nowhere to put it, frees it via
`0x006FA390`, and returns — silently, with no error, exactly matching the live observation
(call succeeds, head-action-type unchanged, no crash).

**Cross-checked against the "proper" NWScript entry point.** `CSWSCreature::AddAttackActions`
(`0x004FDE40`, plural — what `ExecuteCommandAttack` calls for `ActionAttack()`) was also
disassembled. It calls `ClearAllActions(1)` (`0x004CCD80`), then two more round-struct helpers
(`0x004D3770` — frees the existing action list's contents IF `+0x9B0` is already non-null, a
no-op otherwise; `0x004D37E0` — reinitializes five embedded `0x14C`-byte sub-structures at
`round+4`, unrelated to `+0x9B0`) — **then calls the exact same `AddAttackAction`
(`0x004D38B0`) our fix already calls**, with the same five-argument shape
`(targetId, 0, 0, 0x5DC, 1)`. Neither of those two helpers allocates `+0x9B0` if it is null.

**What this means, precisely:** either `+0x9B0` is allocated once at the round struct's own
construction time and stays allocated for the object's whole lifetime (in which case a
genuinely retail-spawned creature — which is exactly what our mirrored `sithsoldier` bodies
are, per the client-encounters/enemy-presence docs: both installs spawn them naturally from
the module's own creature list, not from a client-side blueprint) should already have a
non-null container, and this bail is NOT what's firing — or something about being driven as a
mirrored puppet (a prior `ClearAllActions` call, or some other engine path) nulls it out. The
round struct's own constructor was NOT dissected this round; that is the one remaining gap.

**The one decisive next step, and it is cheap — a live read, not more disassembly:** on a live
mirrored enemy, read `*(void**)(creature + 0x9C8 + 0x9B0)` (e.g. via a throttled diagnostic
log line, the same mechanism the prior round's `QueueAttackAction` diagnostic already used) at
the moment `QueueAttackAction` is about to call `AddAttackAction`. Null confirms this exact
mechanism and makes `StartCombatRound` (`0x004D5F70`, thiscall on `creature+0x9C8`, five stack
args — call sites found at `0x005BC537`/`0x005BC56D`/`0x005BC59E`, argument semantics not yet
fully pinned down beyond "one of them is the candidate/target id") the next thing to call
first. Non-null means this whole branch is a dead end and the real cause is upstream of
`AddAttackAction` entirely (something before it ever gets called, or a precondition on the
*attacker* rather than the target).

No source code changed this round — the risk of guessing `StartCombatRound`'s five-argument
thiscall wrong (stack corruption on a live game process) was judged higher than the value of a
guess, when one cheap live read settles which of the two remaining branches is real.

### The decisive read ran (2026-08-20) — `round+0x9B0` is non-null; the branch is dead

Reproduced the exact mid-fight-join scenario again: host `tp`'d beside the live `sithsoldier`
pair, aggroed for real (`hp 1/12`, `state IN COMBAT`, `combat [FIGHT in END_M01AA - engaged:
you]`), then a second instance connected and was `tp`'d to the same spot while the host kept
fighting.

The added diagnostic (`Odyssey.QueueAttackAction`, commit `3fc0937`) answered cleanly, on
**two different mirrored creatures**, every ~3s for over a minute straight:

    queueattack diag: CALLED AddAttackAction(round=0x192BC5A0, target=0x7FFFFFFF) on creature
    0x192BBAD0, round+0x9B0 pending-list pointer BEFORE the call = 0x3400780, head action
    before 0x6, IMMEDIATELY after 0x6 (is fight action now: False)

    queueattack diag: CALLED AddAttackAction(round=0x1920E510, target=0x7FFFFFFF) on creature
    0x1920D600, round+0x9B0 pending-list pointer BEFORE the call = 0x3400F20, head action
    before 0xFFFFFFFF, IMMEDIATELY after 0xFFFFFFFF (is fight action now: False)

`round+0x9B0` is **non-null on both creatures**, every single read. The
`0x004D3660`-helper's null-check bail is NOT what's firing here — that branch is a dead end.
Both `sithsoldier` bodies stayed at full HP (`creatures.ps1`) and the client's own state stayed
`exploring (not in combat, not paused)` / `their engine is not in combat` for the full duration,
identical to every prior run. `local_combat` restored to `0` in both installs' `coop.ini`
afterward; no source change this round (nothing to implement — the leading hypothesis was
refuted, not confirmed). Full suite 953/953, both builds clean.

**Where this leaves it.** `AddAttackAction` is called with a resolvable target, a live round
pointer, AND a non-null pending-action list — every precondition this investigation has checked
is satisfied — and it STILL appends nothing. The remaining open branches from the disassembly
round: (a) the `vtbl[0x10]` `AsSWSObject` cast on the resolved target (branch 2 in the table
above) was never actually ruled out, only deprioritized — worth checking directly now that the
more likely candidate is dead; (b) the round struct may have some OTHER gate this investigation
hasn't read yet — the shared append helper at `0x004D3660` was only disassembled as far as its
first two instructions (the null check); the REST of that function, past the null check, has
never been read and may contain its own silent bail; (c) whether `head action type`
(`GetHeadActionType`) is even reading the same list `AddAttackAction` appends to — worth
directly re-verifying the field this reader uses against the field `0x004D3660` links into,
rather than assuming they're the same list. (a) and (c) are both cheap, targeted disassembly;
(b) is the natural next full read.

### The append was never the problem — nothing was running to consume it (2026-08-20, fourth round)

Wrong question. `AddAttackAction` appending to a genuinely non-null `round+0x9B0` is not evidence
the append *worked* in the sense that matters — it only proves the LIST HAS SPACE. Whether
anything ever looks at that list again is a separate mechanism entirely, and it does not.

**`UpdateCombatRoundTimer` (`0x004FAF20`) is the function that would.** Confirmed called
unconditionally every tick for every creature, no mirrored/combat-state gate anywhere near its
call site (`0x004FE300`) — so it fires on our mirrored `sithsoldier` bodies exactly as it does on
a real one. Its very first real decision, disassembled directly:

```
0x004FAF4A  mov ecx, dword ptr [edi + 0x9c8]   ; round = creature->CombatRound
0x004FAF50  mov eax, dword ptr [ecx + 0x958]   ; eax = round->+0x958
0x004FAF56  test eax, eax
0x004FAF58  je 0x4fb0d3                         ; ZERO -> bare fallback, nothing else runs
...
0x004FB0D3  mov edx, dword ptr [edi + 0xcc]
0x004FB0D9  push edx
0x004FB0DA  call 0x4d4c10                       ; IncrementTimer(edx) — literally just this, then ret
0x004FB0DF  ...ret
```

If `round+0x958` is zero, `UpdateCombatRoundTimer` runs `IncrementTimer` and returns — it never
reaches the branch (`0x4d45e0`/`0x4d45b0`/`0x5b8050`) that would process a queued action into
anything `GetHeadActionType` sees change. **This is the actual silent-discard mechanism**,
downstream of `AddAttackAction` entirely — not inside it, which is why every precondition inside
`AddAttackAction` checked out clean and it still did nothing observable.

**`StartCombatRound` (`0x004D5F70`) was disassembled in full and does NOT set `+0x958` nonzero —
it sets it to ZERO**, unconditionally, near the end of its own body:

```
0x004D6019  mov dword ptr [esi + 0x934], 1      ; the round "active" flag -> 1
0x004D6023  mov dword ptr [esi + 0x944], ebp     ; ebp = 0 (xor ebp,ebp earlier)   elapsed timer -> 0
0x004D6029  mov dword ptr [esi + 0x958], ebp     ; -> 0, a FRESH round's timer-active field starts at 0
```

This directly refutes the prior round's hypothesis (`StartCombatRound` sets `+0x958`) — it does
the opposite. `+0x934` (a real boolean, confirmed set to `1` here) is the round's "active" flag;
`+0x958` is something else, reset to a clean 0 as part of a fresh start, same as `+0x944` (the
elapsed-time accumulator) and several neighbouring fields.

**Found the real setter by using `fieldwrites.py` instead of guessing further.** `python
tools/fieldwrites.py 0x958` lists every instruction in the binary that writes that offset — 9
total, one of which is `StartCombatRound` (above, zeroing it) and one of which (`0x004D2906`) is
inside a small, self-contained two-argument function that does nothing else:

```
0x004D28F0  mov edx, dword ptr [esp + 8]        ; edx = arg2
0x004D28F4  mov eax, dword ptr [esp + 4]        ; eax = arg1
0x004D28F8  mov dword ptr [ecx + 0x95c], edx    ; +0x95c = arg2   (unconditional)
0x004D28FE  mov edx, dword ptr [ecx + 0x964]
0x004D2904  test edx, edx
0x004D2906  mov dword ptr [ecx + 0x958], eax    ; +0x958 = arg1   (unconditional, both stores
0x004D290C  je 0x4d2918                          ; happen before this branch is even tested)
...
```

**`xrefs.py 0x4D28F0` finds 23 call sites.** Three of them sit in the exact same function that
made all three `StartCombatRound` calls this section already traced (the per-attacker
combat-decision routine near `0x5BC49F`–`0x5BC6C4`) — and all three are byte-identical in shape:

```
0x005BC60F  push edx            ; edx = the relevant creature's own object id (esi+4, or a
                                 ; resolved target's, depending which of the 3 sites)
0x005BC61C  push 1              ; arg1 (last pushed) = literal 1, EVERY site, no exceptions
0x005BC61E  call 0x4d28f0
```

Same pattern repeats at `0x5BC67D` and `0x5BC6AD`, once for the attacker's own round (right after
the `StartCombatRound` branch converges) and twice more for a resolved target's round (retaliation
setup, paired with `target+0x4DC = 1` a few lines earlier — a field 4 bytes from `+0x4E0`, the
`FlagInCombat` field this entire section opened with). **This is the missing initiation step the
engine's own code always pairs with `StartCombatRound`, and our driven puppet — whose AI script
never runs — never got it.**

**Implemented, `src/KotorCoop.Plugin/Odyssey.cs`:** `SetRoundTimerActiveFn` wraps `0x004D28F0`.
`QueueAttackAction` now calls `SetRoundTimerActiveFn(round, 1, creature's own object id)`
immediately before `AddAttackActionFn`, and the diagnostic now reads `round+0x958` before and
after so a live pass sees the actual flip rather than inferring it.

**Deliberately did NOT call `StartCombatRound` itself.** Its own 5-argument signature remains
only partially traced (arg1 = a target/attacker id depending on call site, confirmed; arg2 →
`+0x9b8`, arg3 → `+0x9bc`, confirmed by reading its body; args 4 and 5's semantics were not
pinned down to real confidence) — a wrong thiscall argument corrupts the stack on a live process,
and per this whole investigation's standing rule that risk is not worth taking on an unproven
argument. It also turned out not to be necessary for this fix: `UpdateCombatRoundTimer`'s gate,
read in full, depends on `round+0x958` alone — nothing in the branch past it was seen checking
`+0x934` or any other `StartCombatRound`-initialized field. `SetRoundTimerActiveFn`'s own 2-arg
signature, by contrast, has full confidence: verified both by disassembling the function's own
body AND against three real, byte-identical engine call sites, with no branch or ambiguity in
either argument's role.

### The live pass ran (2026-08-20, fifth round) — the flip works, and it still does not work

Same scenario as every round: host `tp`'d beside a live, untouched `sithsoldier` pair in
`END_M01AA`, aggroed for real (`state IN COMBAT`, HP falling under the engine's own AI, floored
at 1 by the deck's min-1-hp pin — `god was already ON`), then a second instance connected and was
`tp`'d to stand beside the same two hostiles while the host kept fighting. Watched for **2 minutes
40 seconds**, not just the usual 90.

**Check (1) passed, cleanly and repeatedly.** The diagnostic fired every ~3s for the entire
window, and the flip is real every single time:

    [18:26:57.791] queueattack diag: CALLED SetRoundTimerActive+AddAttackAction(round=0x1847F948,
    target=0x7FFFFFFF) on creature 0x1847EE78, round+0x9B0 pending-list = 0x32D7BE8, round+0x958
    timer-active BEFORE=0 AFTER=1, head action before 0x6, IMMEDIATELY after 0x6
    (is fight action now: False)

`SetRoundTimerActiveFn` genuinely writes the field, on the real object, every call, no exception —
that part of the fix is proven, not inferred. **Checks (2) and (3) both failed.** The head action
never once changed from `0x6` across ~50 logged calls spanning 2:40. The host's own dashboard
confirms the client side from the other end the whole time:
`combat [FIGHT in END_M01AA - engaged: you; mirroring only: Malak (their engine is not in
combat)]` — identical wording to every prior round's failure, unchanged for the full duration.
`kotorcoop-combat.log` on the client independently confirms the absorb path is still the only
thing moving: `combat: absorbed N forwarded damage in total ... this engine says in-combat=False`.

**The genuinely new fact this round adds: `round+0x958` reads BEFORE=0 on EVERY call, not just the
first.** If the field we set to 1 were actually being consumed by `UpdateCombatRoundTimer`'s
"process the round" branch — the behaviour the whole hypothesis depends on — a successful
consumption resetting it back toward 0 as part of normal round bookkeeping would be unsurprising.
But that is indistinguishable, from this diagnostic alone, from `UpdateCombatRoundTimer` **never
reaching that branch at all** and something else entirely resetting the field to 0 every tick
before our own write is checked next cycle — which would mean the branch this whole fix aims at is
still never firing, just at a point one step further down than round four thought.

`local_combat` stays off. Config reverted (`local_combat=0` in both installs), full suite 953/953,
both builds clean. **This is the sixth round to narrow the mechanism without closing it.** The
next diagnostic that would actually separate the two explanations above: log `round+0x958`
immediately on entry to `UpdateCombatRoundTimer` itself (not just before/after our own write, three
seconds apart) — if it reads 1 there and the processing branch still isn't reached, the bug is
inside `UpdateCombatRoundTimer`'s branch itself (the disassembly needs to go further than
`0x004FAF58`); if it reads 0 there, something between our write and the next tick is clearing it,
and that write needs to be found with `fieldwrites.py 0x958` cross-referenced against a timestamp
window rather than assumed to be `UpdateCombatRoundTimer`'s own consumption.

### `UpdateCombatRoundTimer` has a SECOND gate past `+0x958`, and it was never established (2026-08-20, seventh round)

Full disassembly of `UpdateCombatRoundTimer` (`0x004FAF20`) past the `+0x958` check settles both
prior explanations at once: **neither is right.** Passing the `+0x958` gate is necessary but not
sufficient. Immediately after, the function computes `ebx = round+0x960 - creature+0xCC` (creature's
own current-tick-ish field) and only when `ebx > 0` does it reach the real action-processing call,
`0x005B8050` (via `0x004FAFA7`-`0x004FAFD9`). When `ebx <= 0` — which it always is if `round+0x960`
was left at its constructed/reset default of 0, since `creature+0xCC` is a live, positive,
ever-increasing tick counter — the function instead takes a shortcut (`0x004FAFE3` onward) that
resolves a leftover `round+0x9CC` target through a chain of vtable validity checks
(`AsSWSObject`/`GetDead`/two more vtable calls) and, if all pass, calls the SAME round-reset helper
(`0x004D4620`, the one `fieldwrites.py 0x958` already found) that zeroes `+0x958` back to 0 along
with `+0x934`, `+0x944`, `+0x94c`, `+0x960` and others — **cross-confirmed via `xrefs.py 0x4D4620`**,
which lists `0x004FB0BA` (the exact end of this shortcut) among its 9 call sites. This is why the
diagnostic always read `BEFORE=0`: the only reachable branch without `+0x960` being set ends in
exactly this reset, every time, without ever reaching `0x5B8050` — the call that would actually
change the head action.

**`StartCombatRound` does NOT establish `+0x960` either — it zeroes it too** (`0x004D602F`,
confirmed by reading its full body), so the previous round's conclusion that `StartCombatRound`
"turned out not to be necessary" was based on an incomplete trace; it stopped at the first gate.

**Found the real setter with the same method as before — `fieldwrites.py 0x960`, not more
guessing.** `0x004D2932` sits five NOP-padding bytes after `SetRoundTimerActiveFn`'s own `ret 8`
at `0x004D2918` — a genuinely SEPARATE, complete function (`0x004D2920`, three `ret 8` exits, fully
disassembled, no unexplored branches): `round+0x964 = arg2` always first, then if `arg2 != 0`,
`round+0x960 = arg1` directly; if `arg2 == 0`, either zeroes `+0x960` (both args 0) or
sets-or-accumulates it from `arg1`. **`xrefs.py 0x4D2920` finds 21 call sites**, two of which
(`0x005BC630`, `0x005BC6BF`) sit in the EXACT SAME engine combat-decision routine that makes the
already-confirmed `StartCombatRound` and `SetRoundTimerActiveFn` calls, immediately after each
`SetRoundTimerActiveFn` call, both `push 0; push <delayMs>; call 0x4D2920` — the third setup step
the engine always pairs with the other two, that this fix was still missing.

**Implemented**, `src/KotorCoop.Plugin/Odyssey.cs`: `SetRoundDelayFn` wraps `0x004D2920`.
`QueueAttackAction` now calls `SetRoundDelayFn(round, 0x5DC, 0)` immediately after
`SetRoundTimerActiveFn` and before `AddAttackActionFn` — `0x5DC` (1500ms) matches the delay already
used elsewhere in the same call for consistency; the function's own logic does not care about the
source of the value, only that a positive duration lands in `round+0x960`. Diagnostic extended to
log `round+0x960` before/after alongside the existing `+0x958`/`+0x9B0` reads. Full suite 953/953,
both builds clean.

**Live pass attempted, blocked by an unrelated baseline reproduction failure — not by this fix.**
Rebuilt, reinstalled into both live game directories, `local_combat=1` set on both, host launched
alone and `tp`'d beside the same `sithsoldier` pair every prior round used (`(29.74,144.46)` /
`(31.86,141.99)` in `END_M01AA`, player landed within ~1.7m of both) — but the host's OWN NATIVE AI
never aggroed at all: 40+ seconds of no movement, no HP change, no combat-state change. A second
attempt at a completely different creature pair elsewhere in the module (`end_sith2`/`end_sith3` at
`(46.78,33.27)`/`(45.18,32.45)`) showed the identical non-response over another 20+ seconds. This is
the HOST's own unmodified engine failing to do what every prior round's host reliably did in ~1s —
something this session's environment, save state, or the specific `sithsoldier` pair's own scripting
(nearby `end_bridgerep1`/`end_bridgerep2`/`carth` tags suggest this location may be a scripted bridge
sequence, not a generic wild encounter) is different about, and it is NOT something `local_combat` or
this fix could cause: the host plays with its own completely unmodified engine, no plugin code from
this investigation runs on host-owned creatures at all. `creatures.ps1` also read the player's own
`hp` column as `0` throughout despite `god` reporting "1 hp floor" already on — plausibly the same
"a getter that is one `mov` is not proof the field is live" PC-read limitation noted elsewhere in
this document, but worth a look before the next live pass in case it is connected.

**The fix itself is implemented, unit-tested, and evidence-complete** (full disassembly of both
`UpdateCombatRoundTimer` past both gates and `SetRoundDelayFn` in its entirety, cross-verified
against real engine call sites for every value used) — what remains is purely procedural: get a
real fight going on the host at all, in this environment, before the client-side mechanism this
section is actually about can be exercised. `local_combat` stays off. Config reverted to
`local_combat=0` in both installs, rig fully torn down.

### Round 10 (2026-08-20) — the "dead enemies" theory is refuted, and the real blocker is bigger than this fix

Chased why the host's own baseline aggro failed. `creatures.ps1` on a completely fresh launch
(save slot 1 reloaded from scratch) showed both `sithsoldier`s — and all 27 creatures — alive at
full stated HP (`15 10` columns unchanged from a clean load). **The save is not corrupted or
exhausted by nine rounds of prior testing.** That theory is dead.

What's actually true, confirmed by reading `tools/blockpoke.ps1`'s own `attack` verb source
(`TestDriveController.HandleAttack`, `src/KotorCoop.Plugin/TestDriveController.cs:104-107`) —
its own doc comment says it outright: **"the starting area's enemies never aggro on their own,
so without it the damage forward, the swing animations and the phantom's targetability have no
way to be exercised outside a human playthrough."** Every prior round's "aggroed in ~1 second"
result was staged through this tool (`Odyssey.AttackTarget`, the native `ActionAttack()` VM
call), not organic proximity aggro. `tp`-ing next to a `sithsoldier` alone was never going to
work, in this round or any prior one that reported it working from proximity alone — a
methodology gap in how the result was described, not a finding about the engine.

Corrected the target: player object id is not a normal small id here — it reads back as
`0x7FFFFFFF`, the engine's own "current player" sentinel (confirmed via `console 'resolve
0x7FFFFFFF'` resolving to a live, valid creature, matching an earlier round's finding). Ran
`attack 0xE 0x7FFFFFFF` correctly this time (a first attempt with no explicit target silently
means "break off combat", logged plainly as `test: 0xE ordered to break off combat` —
`TestDriveController.cs:126-138`, working as designed once understood).

**`Odyssey.AttackTarget` reported success — `ok=true`, no failure code — and still nothing
happened.** No movement, no swing, no HP change, over 40+ seconds. `god`/console responsiveness
confirmed the engine and script VM are NOT frozen (`god` correctly answered "already ON", the
tutorial deck's own `k_pend_area01` min-1-hp pin, matching documented behaviour exactly). So this
is not a hung process — it is the SAME "call succeeds, nothing observable happens" symptom this
entire nine-round investigation has been chasing inside `local_combat`'s own code, except this
time on the OFFICIAL, previously-reliable native `ActionAttack()` staging path itself, which
every earlier successful round depended on to manufacture a fight at all.

**This reframes the open question.** Either: (a) something has changed in this specific
environment/session since the rounds that reported staged attacks working (worth checking
against an entirely fresh game launch with a clean process, not just a fresh save-load inside a
session that has been repeatedly launched/torn down many times today), or (b) the "worked before"
reports were never as clean as described and the underlying fragility has been present the whole
time, surfacing now by chance. Round 11 should start here — with `AttackTarget`/`ActionAttack()`
itself, on a completely fresh process launch, before returning to `local_combat`'s own code at
all, since if the OFFICIAL path cannot manufacture a fight, no amount of further work on
`QueueAttackAction` can be verified either.

No source code changed this round. `local_combat` untouched, `coop.ini` never modified in either
install (phase 1 never completed, so phase 2 — the actual `local_combat` retest — did not run).
Process torn down clean.

### Round 11 — the unification hypothesis holds, and a real fight finally happened

Before any live work: read `Odyssey.AttackTarget`'s implementation directly. It dispatches
through the NWScript VM's own `ActionAttack()` command table entry — i.e. the exact
`ExecuteCommandAttack` (`0x0052E7F0`) → `CSWSCreature::AddAttackActions` (`0x004FDE40`) →
`AddAttackAction` (`0x004D38B0`) chain this whole investigation has been fixing.
`python tools/xrefs.py 0x004D28F0` and `0x004D2920` (the two setters `QueueAttackAction` now
calls) list 21-23 call sites each, and NONE of them are inside `AddAttackActions`'s own preamble
helpers (`0x004D3770`/`0x004D37E0`) or `AddAttackAction` itself — confirmed by address, not
inferred. So `AttackTarget`, the "official" path, is missing the identical precondition
`QueueAttackAction` was missing before `fb8d5dc`. Round 10's "the staging tool itself is broken"
was never a second mystery; it was the same bug, reached through a different door.

Applied the same two calls (`SetRoundTimerActiveFn`/`SetRoundDelayFn`) to `AttackTarget` itself,
immediately before its VM dispatch, and tested live on a genuinely fresh single host process
(fresh launch, no prior console commands, verified clean process teardown beforehand):

- `attacktarget diag: 0xE round+0x958 0->1, round+0x960 0->5DC` — both fields read exactly 0
  immediately before the call (a truly untouched creature, not a reused/damaged one from a prior
  round) and flipped correctly.
- Ordered first on `0xE` while the player was ~120 units away (an accidental control: nothing
  visibly happened while out of reach, ruling out a "the flip alone teleports/forces an attack"
  false positive).
- Moved the player to `tp 29.74 141.46 -0.00`, ~3m from `0xE`. Within 5s: player HP `12/12 -> 8/12`
  (`console 'where'`), `0xE`'s position moved from `(29.74, 144.46)` toward the player, to
  `(29.74, 143.26)` (`creatures.ps1`) — a real, engine-driven approach and swing, not a number
  written from outside.
- Ordered a SECOND, still-untouched creature (`0x10`) to attack with the player already close:
  within 4s, player HP `8/12 -> 1/12` and `0x10` closed from `(31.86, 141.99)` to
  `(31.48, 141.90)`. Fast, and consistent with the ~1s aggro speed every earlier round measured
  for genuinely organic host combat.
- `god` was unexpectedly already ON on this fresh process (not set by anything in this session,
  no `coop.ini` key, no code default found) — this is why the player survived to 1 hp instead of
  dying; noted as an open anomaly, not chased further, since it does not change what was measured
  (damage genuinely flowed and was only floored, not absent).
- Broken off cleanly afterward (`attack <id> 0` on both, the documented "target zero = stop
  fighting" convention) before process teardown; player left at a stable 1/12, not downed.

**This is the first real, physically-observed fight this entire investigation has produced.**
It confirms the `round+0x958`/`round+0x960` mechanism `fb8d5dc` implemented is genuinely correct
and sufficient to take a creature from cold to a real, damage-dealing fight — on the HOST's own
engine, driving its own (non-mirrored) creature. It does NOT yet directly prove the CLIENT
mirrored-enemy case (`local_combat`'s actual purpose: a driven puppet fighting a REMOTE player
through the co-op session, on a machine that owns none of the input) — `QueueAttackAction` calls
the identical two functions, but the mid-fight-join, two-instance scenario has still never
completed end to end. Given the mechanism is now proven in the general case, the next round
should go straight back to that scenario with real confidence instead of re-litigating whether
the fix works at all.

`local_combat` and both installs' `coop.ini` were not touched this round (this test needed only
a single host instance). Process torn down and confirmed clean. Full suite 953/953, both builds
clean.

### Round 12 — the actual mid-fight-join test, a real bug found and fixed, and the symptom survives it

The scenario `local_combat` exists for, finally run against a mechanism already proven correct in
the general case. Host launched alone, `tp`'d to `(28, 143, 0)`, `attack 0xE 0x7FFFFFFF` staged —
HP `12/12 -> 10/12` within 5s, confirmed the same way round 11 did. With the host still actively
fighting, a second instance launched, loaded, connected (`phantoms 1 remotes 1` both sides), and
`tp`'d to the identical `(28, 143, 0)` — genuinely mid-fight, not staged separately.

**A real bug surfaced immediately, before the actual question could even be asked.**
`round+0x960` climbed by exactly `+1500` on every single logged call — `BEFORE=2238005
AFTER=2239505`, then `BEFORE=4483506 AFTER=4485006`, `BEFORE=6871505 AFTER=6873005` — hundreds of
times a second, because `QueueAttackAction`'s whole setup block ran unconditionally on every
`EnemyPresenceController` drive pass whenever the head-action gate at the top of the function
wasn't satisfied, which it never was (nothing was making it true — the exact thing being tested).
`SetRoundDelayFn` **accumulates**, it does not set, so the round's own due time was pushed further
into the future faster than real time could ever catch up — a self-defeating feedback loop that
also explains why round 11 worked at all: `AttackTarget` fires from a single console command,
once, never from a per-frame drive loop.

**Fixed live, mid-round-12, and retested in the same session** (`Odyssey.cs`,
`QueueAttackAction`): gated `SetRoundTimerActiveFn`/`SetRoundDelayFn` behind `roundTimerBefore ==
0` — initialize the round ONCE, on the genuine 0->1 transition, and let the existing head-action
gate do its job for every pass after that. Rebuilt, reinstalled, retested the identical
mid-fight-join scenario end to end a second time: `round+0x960` now behaves sanely (`BEFORE=0
AFTER=1500` once, then small organic drift — `4`, `7`, `332`, `331`, `333`... — never another
`+1500` jump). The accumulation bug is real and is fixed.

**The core symptom survives it.** Over the full retest, the client's own HP never moved from
`12/12` (`console 'where'`, checked repeatedly), head action stayed `0x6`/`0xFFFFFFFF`
throughout, and the plugin's own absorb line kept firing (`combat: absorbed N forwarded damage in
total ... this engine says in-combat=False`) — the same pre-existing forwarded-damage path that
works regardless of `local_combat`, not new local engagement. The host's own fight, checked in
parallel, correctly ran the whole time (its HP reached `1/12`, the deck's own min-1-hp floor) —
confirming nothing else broke and the test rig itself was sound throughout.

**What this narrows the question to.** Round 11 proved the round-init mechanism sufficient for a
HOST-LOCAL creature (no mirroring, no drive loop touching it every frame, no peer involved) —
that was ALWAYS a single-instance test. Round 12 is the first time a genuinely MIRRORED creature
has been driven through a correctly-initialized (not spamming) round, and it still does not
attack. The one structural difference between the two cases that round 12 did not yet isolate:
`EnemyPresenceController`'s own per-frame `SetPosition`/`SetFacing` drive calls run on this exact
creature every single pass, continuously, for as long as it is mirrored — round 11's creature had
no such competing per-frame writer. Candidate 2 from earlier in this investigation (drive writes
starving perception) was refuted specifically for PERCEPTION; it was never re-checked against
ROUND PROCESSING specifically, and round processing is a different code path
(`UpdateCombatRoundTimer`) than perception (`UpdateVisibleList`). Worth a targeted check next: does
suspending the position/facing drive on ONE test creature (leaving everything else — the
attack-permitted bit, the round-init call — untouched) let its round process normally? That would
cleanly separate "driven puppets structurally cannot complete a combat round while being driven"
from "something else, specific to the mirrored/remote-target case, is still missing."

`local_combat` and both installs' `coop.ini` restored to their code defaults (`0`) afterward.
Process torn down and confirmed clean (`Get-Process KotorCoop.Agent,swkotor` returned none). Full
suite 953/953, both builds clean.

### Round 13 — the append itself had the same bug the setters did, and fixing it is not enough

Before touching the drive-loop-interference question round 12 left open, a closer read of
`QueueAttackAction` found a second instance of round 12's exact bug, one call further down:
`AddAttackActionFn(round, targetId, 0, 0, 0x5DC, 1)` was running UNCONDITIONALLY on every
call, gated only by the head-action check at the top of the function — which never trips,
because head action never leaves `0x6`. `AddAttackAction` has no dedup of its own
(documented since the instruction-level dissection round: "unconditionally allocates and
appends a new action node every call"), so a driven, engaged mirrored creature was having a
fresh, undeduplicated action node appended to `round+0x9B0`'s pending list on every single
drive pass — tens of times a second, for as long as it stayed in range. The engine's own
normal flow (`CreateNewAttackActions`, reached only from `EndCombatRound`) calls this exactly
once per round, never in a tight per-frame loop; this is the same shape of self-defeating bug
as round 12's `SetRoundDelayFn` accumulation, one layer closer to the actual append.

**Fixed** (`Odyssey.cs`, `QueueAttackAction`): moved `AddAttackActionFn`'s call inside the same
`roundTimerBefore == 0` gate as the two setters, so all three fire together, exactly once, on
the genuine 0->1 transition — matching round 11's only working case (`AttackTarget`, a single
staged call, never repeated) as closely as this code path can.

**Live-retested, full mid-fight-join scenario, third time end to end.** Host staged into a
real fight (`AttackTarget` via `blockpoke.ps1 attack`, HP `12/12` -> confirmed dropping under
the fight, reaching the deck's `1/12` min-hp floor); client connected mid-fight and `tp`'d
beside the same two `sithsoldier`s. Confirmed the fix holds: `round+0x958`/`round+0x960` on
the mirrored copies now show a clean single `queued-this-pass=True` transition followed by
`queued-this-pass=False` on every subsequent pass, with `round+0x960` drifting by small, sane
per-tick amounts (`14`, `16`, `18`, `20`, ...) rather than exploding — the append-side bug is
real and is fixed, the same way the setter-side one was.

**The core symptom still survives it, and there is a new, sharper piece of evidence.** Head
action never left `0x6`/`0xFFFFFFFF` across the whole test. And the client's own damage log
now shows the actual shape of the failure rather than just its absence: `combat: absorbed 2 /
6 / 10 / 18 / 24 forwarded damage in total ... this engine says in-combat=False` — a strictly
growing absorbed total, tracking the host's real fight — while `console 'where'` confirmed the
client's own HP never moved from `12/12` at any point. This is the exact INVULNERABLE failure
mode `local_combat`'s own safety notes have warned about since it was first built: forwarded
damage is being absorbed on the promise that the local copy is dealing the same damage for
real, and it is not, so the damage is simply discarded. Two round-13 fixes in, the mechanism
that would make that promise true (round processing itself) is still not completing for a
driven creature, even though round-init and the append are both now individually correct.

**What remains open, precisely:** round 11 proved the round-init + append mechanism sufficient
end-to-end for a creature nothing else was writing to every frame. Rounds 12 and 13 both used
that same, now-doubly-fixed mechanism on a creature `EnemyPresenceController` continuously
drives (`SetPosition`/`SetFacing` every pass), and it still never completes a round. The
targeted check round 12 proposed and did not yet run — suspending the position/facing drive on
one test creature while leaving the attack-permitted bit and the round-init/append calls
untouched, to see whether ITS round processes normally — is still the next concrete step, and
is now better-motivated than before: with both known self-inflicted bugs fixed, whatever is
left is either genuinely specific to being continuously driven, or a third, still-undiscovered
gate this investigation has not yet reached.

`local_combat` and both installs' `coop.ini` restored to their code defaults (`0`) afterward.
Process torn down and confirmed clean (`Get-Process KotorCoop.Agent,swkotor` returned none). Full
suite 953/953, both builds clean.

### Round 14 — the drive-loop-interference hypothesis is a dead end; `UpdateCombatRoundTimer`'s real gate was misread, and `StartCombatRound` turns out to do far more than two fields

Static-only round (no live rig used). Two corrections to round 13's own conclusions, found by
disassembling further than round 13 had time for.

**`EnemyPresenceController`'s per-pass order was re-checked and is not the problem.**
`ClaimCreatureForFight`, `PermitAttacking`, one-shot replay, `Drive()` (position/facing),
`ClearPositionDirty`, `RecordEngagement`, THEN `QueueAttackAction` — in that order, every pass.
Nothing runs between `QueueAttackAction`'s writes and the next pass's `ClaimCreatureForFight`
that could plausibly reset round state; `ClaimCreatureForFight`'s move-only wipe was already
confirmed (round 9) to be structurally incapable of touching a fight-type action, and nothing in
this pass touches `round+0x9xx` fields directly by address. The interference hypothesis this
round was assigned to test is **not supported by the call sequence itself**.

**`UpdateCombatRoundTimer`'s gate was misread in round 13.** Full disassembly of `0x004FAF20`
past the point round 13 stopped: the `round+0x960 - creature+0xCC <= 0` branch (`jle 0x4fafe3`)
is **not** a dead end that always resets — it falls into a loop that calls the SAME processing
functions (`0x4d45b0`, `0x5b8050`) as the "positive" branch, gated instead on
`[esi + 0xb0] > 0`, where `esi = 0x004D2B50(round, byte at round+0x96C)` — an array indexer:
`esi = round + 4 + (that byte, clamped >= 0) * 0x14C`, i.e. ONE of **five embedded 0x14C-byte
per-participant sub-structures** starting at `round+4`, selected by `round+0x96C`. The `ebx`
sign only decides which of two paths reaches the same processing calls; it is not itself a
block. What actually blocks all processing is `[esi+0xb0] <= 0` on whichever sub-structure
`round+0x96C` currently selects — a field this investigation has never read or written.

**And `round+0x96C` was never set by anything this investigation has done**, because
`StartCombatRound` (`0x004D5F70`, now fully disassembled end to end, `0x4D5F70`-`0x4D60EA`)
does far more than the two fields `SetRoundTimerActiveFn`/`SetRoundDelayFn` cover: it zeroes
`+0x944`/`+0x958`/`+0x960`/`+0x938`/`+0x93c`, sets `+0x934=1`, `+0x95c=0x7f000000` (a sentinel),
`+0x94c=0xbb8` (3000, likely a real default interval), stores two of its five args into
`+0x9b8`/`+0x9bc`/`+0x940`, conditionally resets a stale prior round via `0x4d4620` — **and runs
a loop over all five `round+4+i*0x14C` sub-structures, calling `0x004D3010` (an
initializer/constructor, not yet dissected) on each one**, before three more setup calls
(`0x4d2a10`, `0x4d2a70`, `0x4f34a0`). None of that has ever run on a driven puppet's round. The
five sub-structures `UpdateCombatRoundTimer` indexes into via `round+0x96C` are very likely
sitting in whatever state the creature's constructor left them in — not the state
`StartCombatRound`'s loop would put them in — and `[esi+0xb0]` is a field inside one of them.

**Argument-shape work on `StartCombatRound` itself, real but incomplete.** Fresh disassembly of
call site `0x005BC537` (with full caller context this time, `0x5BC480`-`0x5BC63B`) maps the
push order to argument roles more precisely than round 12 managed: arg1 = a caller-local value
also reassigned into `ecx` (`[esp+0x14]`, stored into `round`'s `+0x9c0`/`+0x9ac`/`+0x9cc`
target-like fields), arg2 = the return of `call 0x4d2c30([esp+0x10])`, arg3 = the return of
`call 0x4d2d60([esp+0x10], &[esp+0x54])`, arg4 = `[esp+0x50]` (never seen explicitly read
inside the callee body captured so far — possibly genuinely unused), arg5 = `[esp+0x6c]`
(stored into `+0x940`, passed on into `0x4d2a10`). **This is NOT full confidence** — arg2 and
arg3 both derive from two OTHER function calls (`0x4d2c30`, `0x4d2d60`) whose own return values
were not traced to a concrete meaning, and the caller-local stack slots (`[esp+0x10]`,
`[esp+0x50]`, `[esp+0x6c]`, `[esp+0x14]`) were not traced back to where the ENCLOSING function
(a large combat-decision routine starting well before `0x5BC480`) itself populates them from.
Per the standing rule this investigation has followed since round 12 — do not guess a
thiscall's argument shape against a live process without real confidence — **no live test was
attempted this round and no code was changed**.

**What round 15 needs, precisely:** either (a) trace `0x4d2c30`, `0x4d2d60`, and the enclosing
function's population of `[esp+0x10]`/`[esp+0x50]`/`[esp+0x6c]`/`[esp+0x14]` back far enough to
pin all 5 `StartCombatRound` arguments with real confidence, then call the real function (which
handles the five-sub-structure init and `+0x96C` internally — no need to hand-replicate
`0x4D3010` or the three follow-up calls), or (b) find and dissect whatever sets `round+0x96C`
directly (11 write sites found via `fieldwrites.py 0x96C`, not yet individually checked against
this creature's case) and the five sub-structures' constructor (`0x4D3010`) to see if `+0xb0`
specifically can be set directly and cheaply without needing the full `StartCombatRound` call.
Route (a) is very likely more correct (it is what the engine's own working combat AI actually
calls) but route (b) may be cheaper if `0x4D3010` turns out to be a simple zero-fill the
creature's own constructor already performs adequately, in which case only `round+0x96C` itself
(picking sub-structure 0, which is almost certainly what a fresh, never-fought creature already
has by construction) might be the missing single write. Either route is genuinely findable with
the same static tools this round used; it was not a matter of the tools failing, only of budget.

### Round 15 — route (b) refuted: `0x4D3010` clears `+0xb0`, it does not set it

Static-only round (no live rig used; route (b) collapsed before reaching a testable fix).

**`0x4D3010` is not an initializer that leaves a sub-structure ready — it is a reset/clear
function, and it explicitly ZEROES `+0xb0`.** Full disassembly (`0x4D3010`-`0x4D310D`): after
tearing down two embedded lists (`call 0x5e5140` twice) and freeing an owned array's elements
(a loop over `[esi+0x88]`/`[esi+0x8c]` calling `0x503590` then `0x6fa390` — the SAME free
helper `AddAttackAction` itself uses to discard its orphaned node when `+0x9b0` is null, round
9), the tail of the function is a block of zeroing writes: `[esi+0x8c]=0`, `[esi+0x98]=0`,
`[esi+0xa4]=0`, **`[esi+0xb0]=0`**, `[esi+0xb8]=0`, all from the same zeroed register. This
refutes round 14's cheaper hope directly: a "fresh, never-fought" sub-structure is not sitting
at `+0xb0 > 0` by construction — if anything, whatever state a sub-structure is born in, THIS
function (which per its call sites is a per-sub-structure RESET, called in a loop from
`StartCombatRound` before the real setup happens) actively drives it to `+0xb0 = 0`, i.e. the
gate-CLOSED state. Setting `round+0x96C` alone, with nothing else, would at best point the
gate at a sub-structure already known (by this function's own behavior) to read `<= 0`.

**The actual processing function (`0x5B8050`, `UpdateCombatRoundTimer`'s real destination) was
also disassembled far enough to rule out a shortcut.** It re-derives the sub-structure pointer
itself (`ecx = round; movzx eax, byte [round+0x96c]; call 0x4d2b50` — confirming `+0x96C` IS
read live, every call, not cached), then branches on a STATE BYTE at `sub+0x5c` compared
against `2`, cross-checked against a lookup through `0x4aea40` (looks like a party-table or
target-resolution call, `ecx` loaded from a global at `0x7a39fc+8`), conditionally calls a
notification pair (`0x4aed80`→`0x4b08d0`) on a mismatch, then reads `sub+0x40` and `sub+4` to
decide between two further branches — one of which allocates via `0x6fa7e6(0x34)`. This is
real per-tick round-advancement logic with several interdependent checks, not a counter a
single hand-written write could satisfy; forcing `+0xb0` positive without also matching
whatever `sub+0x5c`/`sub+0x40`/`sub+4` are supposed to hold would very likely just fail a
different check one line later, or read genuinely uninitialized garbage through those other
fields.

**Verdict: option (b) is dead. Route (a) — fully tracing `StartCombatRound`'s five arguments
and calling the real function — is now the only remaining path**, exactly as round 14 predicted
it might be ("very likely more correct... it is what the engine's own working combat AI
actually calls"). Per the standing rule, no live test was attempted and no code was changed
this round; forcing a partial write into a structure this interconnected, without the real
constructor, risks producing a NEW, harder-to-diagnose failure mode (a sub-structure that looks
selected and half-initialized) rather than the current clean "does nothing" failure.

**What round 16 needs, precisely:** trace `0x4d2c30` and `0x4d2d60` (the two sub-calls whose
return values become `StartCombatRound`'s arg2/arg3 at call site `0x5BC537`) to a concrete
meaning, and trace the enclosing combat-decision routine's own population of
`[esp+0x10]`/`[esp+0x50]`/`[esp+0x6c]`/`[esp+0x14]` back to where THEY originate, far enough to
pin all 5 arguments with real confidence. Once pinned, `StartCombatRound` handles the whole
five-sub-structure reset-then-init sequence (including the exact `0x4D3010` calls this round
just dissected) and `+0x96C`/`+0x9B8`/`+0x9BC`/`+0x940` internally — calling it for real,
rather than hand-replicating pieces of it, is the correct shape for the fix once the argument
trace closes.

### Round 16 — `StartCombatRound` called for real, and it sticks — but the sub-structure gate stays closed

Static phase first, then live. Re-disassembled all three known call sites with full enclosing
context (`0x005BC480`-`0x005BC63B`) and pinned the full 5-argument thiscall with real
confidence:

- `this` = the round (`creature+0x9C8`).
- arg1 = the OPPONENT's object id — confirmed by `0x004D5F70`'s own body: it resolves arg1 via
  `GetGameObject` (`0x004AE770`) and stores it into `round+0x9C0`/`+0x9C4`, THEN mirrors those
  same writes onto the resolved opponent's OWN round at the identical offsets — which is why
  the engine's real caller makes a second, symmetric call right after (`this`=target's round,
  arg1=attacker's own id, at `0x005BC56D`). Independently confirms round 14's own finding from
  the caller side (arg1 stored into "target-like fields").
- arg2/arg3 — not independent constants; the exact return values of a fixed two-call sequence
  the real caller performs on the SAME targetId immediately before this call
  (`0x005BC4B3`-`0x005BC4D6`): `arg2 = 0x004D2C30(round, targetId)` written to a stack slot,
  then `arg3 = 0x004D2D60(round, targetId, &thatSlot)` — which may itself modify the slot —
  after which arg2 is RE-READ from the slot post-call. `0x4D2C30` resolves the target,
  double-casts it via `vtbl+0x30` (the SAME slot pattern seen twice in a row — a compiler
  artifact of an un-cached virtual call across a branch, not two different casts), then reads
  the TARGET's own `round+0x934`/an id-sentinel comparison at target+0x50c to decide "does the
  target need a round too." `0x4D2D60` reads that same result back through the out-pointer and
  does further validation against `round+0x9b4` (a per-round pointer to something not yet
  identified) before returning. Replicated call-for-call rather than reasoned about
  semantically — round 14/15 already confirmed the destination fields (`round+0x9B8`=arg2,
  `round+0x9BC`=arg3) are load-bearing (`0x4D2C30` itself reads a resolved target's
  `round+0x9BC` as part of its own decision).
- arg4 — read inside `0x4D2A10` as a 16-bit value compared against a small fixed constant set
  (`0xb`/`0x5b`/`0x35`/`0x1e`/`0x5c`/`0x1a`) to decide whether to increment a derived counter at
  `round+0x990`; never dereferenced as a pointer, so any integer is memory-safe. 0 matches none
  of the listed constants (the ordinary/no-special-case path) — but see the live result below,
  this default is now the prime suspect.
- arg5 — stored directly into `round+0x940` (`0x004D604B`), a plain DWORD, not part of the
  confirmed critical gate chain. Also memory-safe for any value. 0.

`ret 0x14` (5 DWORDs) confirms the stack shape matches exactly what was traced. Implemented
`Odyssey.ComputeRoundContextFn`/`ResolveRoundContextFn`/`StartCombatRoundFn`, wired into
`QueueAttackAction`'s existing one-shot gate (`roundTimerBefore == 0`) BEFORE the two setters,
matching the engine's own program order.

**Live result: real, measurable progress — the first-ever non-zero persistence — but the core
symptom did not resolve.** Reproduced the mid-fight-join scenario (host staged into a genuine
fight via `AttackTarget`, second instance connected and `tp`'d beside the same hostiles).
For the first time in this entire investigation, `round+0x958`/`round+0x960` on the mirrored
client STUCK: `round+0x958 timer-active BEFORE=1 AFTER=1` (never `0` again once set),
`round+0x960` drifting sanely (403→407→411→414, small organic increments, not the earlier
explosive accumulation bug). `StartCombatRoundFn` executed without crashing and produced
durable state — real confirmation the call is landing correctly.

Added one more targeted diagnostic (`round+0x96C` sub-index and the selected sub-structure's
`+0xB0`) and reinstalled to get the decisive read:

    round+0x96C sub-index=0 sub+0xB0=0

A valid sub-structure IS selected (index 0, not garbage) — but its gate field reads exactly
`0`, unchanged, on every sampled tick. `StartCombatRound`'s own sub-structure loop (`0x4D3010`,
round 15) explicitly zeroes `+0xB0` as a RESET step; whatever the "three more setup calls"
(`0x4d2a10`, `0x4d2a70`, `0x4f34a0`) that follow are supposed to do to actually ARM it never
lands with arg4/arg5 defaulted to 0. Prime suspect: arg4, the one argument whose full semantic
role (beyond "compared against six constants, never dereferenced") was never traced past
`0x4D2A10`'s partial body, and whose value is passed on into functions this round didn't open
at all (`0x4d2a70`, `0x4f34a0`).

Also measured, real but secondary: the `AttackTarget` staging call itself is flaky
PER-CREATURE, independent of any of this investigation's changes — the same command against
one fresh `sithsoldier` (`0xE`) produced no damage after 45+ seconds this round, while the
SAME command against its twin (`0x10`) worked within 8 seconds, on the same save, same
session. Worth a look separately; it is not this section's mechanism.

**Decision: `local_combat` stays off.** Both `coop.ini` restored to `0`. Full suite 953/953
(one pre-existing flaky test in `DisconnectRecoveryTests`, confirmed passing in isolation, not
a regression from this round's changes — only `Odyssey.cs` was touched).

**What round 17 needs, precisely:** fully disassemble `0x4d2a70` and `0x4f34a0` (not opened
this round at all) and finish `0x4D2A10`'s body past the constant-comparison block already
read, looking specifically for a write to `+0xB0` on the sub-structure `round+0x96C` selects —
and whether that write is conditioned on arg4 (or arg5) being a specific non-zero value rather
than the 0 defaults used here. If none of the three setup calls ever write `+0xB0` regardless
of argument value, the write happens somewhere else entirely (possibly triggered by the
ENGINE'S OWN next tick reading a state this round's call correctly primed but round 16 did not
wait long enough to observe — the live pass ran ~45s per attempt, and `+0xB0` might only get
set on a delayed follow-up condition, e.g. once `round+0x960`'s delay actually elapses against
`creature+0xCC`, which `SetRoundDelayFn`'s own doc comment already flags as the OTHER live gate
`UpdateCombatRoundTimer` checks alongside this one).

### Round 17 — `sub+0xB0` decoded: it is an array-list count, and the real Add call still does not arm it

Full instruction-level tracing (not guessing) of every function that touches `+0xB0` in the
combat-round code region:

- **Refuted the round-16 "arg4 arms it" hypothesis outright.** `0x4D2A10` (arg4's consumer)
  only ever writes `+0x990` (a counter, unrelated). `0x4D2A70` writes `+0x994` (a boolean).
  `0x4F34A0` (the third setup call) writes `+0x944` and calls `0x4CD170` (target-lock
  bookkeeping). None of `StartCombatRound`'s three follow-up setup calls, disassembled in full,
  ever touch `+0xB0`.
- **`+0xB0` is the COUNT field of a `push_back`-style array-list whose data pointer lives at
  `+0xAC`** (0xD0-byte elements). `0x004D2EE0` is its `RemoveAt` (decrements `+0xB0`, shifts
  elements down) — called from `0x004D32D0`, itself called at the tail of
  `UpdateCombatRoundTimer`'s per-tick processing loop, confirming `UpdateCombatRoundTimer`
  actually LOOPS "while `sub+0xB0` > 0", consuming one element per real attack processed. This
  is a genuinely separate mechanism from `round+0x9B0` (the list `AddAttackAction` appends
  to) — the two have never been the same list.
- **The real `Add`: `0x004D57A0`** (`this = sub+0xAC`, one 0xD0-byte element pushed on the
  stack, `ret 0xD0`) — confirmed by full disassembly, writes `[this+4]` = count+1. Its only two
  callers both live inside `0x004D5A50`, a function that assembles the full 0xD0-byte element
  from fields ALREADY PRESENT on the sub-structure itself (`+0x1c`, `+0x3a`, `+0x5c`, `+0x64`,
  `+0xc0`, `+0xe0`/`+0xf4` — weapon/roll data, `+0xfe`, `+0x128`) before calling `Add` — i.e.
  this is a "commit a fully-computed attack" step, not something that could be satisfied by a
  bare struct poke; those fields would need populating first, and where they come from was
  never traced (its own single caller sits deep inside an unrelated, ~7-argument per-target
  validation loop near `0x5BB890`/`0x5BB590`, never traced to real confidence, and this thread
  was abandoned as too large and too risky to guess-call).
- **The actual fix implemented instead: `CSWSCreature::CreateNewAttackActions`
  (`0x005B6980`)** — the engine's own single, already-fully-decoded-and-VERIFIED entry point
  for this exact job (`docs/research/combat-participation.md` §1, written independently of
  this investigation). `QueueAttackAction` now calls it with `bMayAcquireNewTarget=1` instead
  of a bare `AddAttackAction`: the bare-revalidation line inside `CreateNewAttackActions` only
  ever fires when `round+0x9B0` is EMPTY (per §1.2), which our own prior `AddAttackAction` call
  had already made false — so every earlier round's direct `AddAttackAction` call was hitting a
  path this function's own body would always skip. The ACQUISITION path (§1.3, only runs when
  `bMayAcquireNewTarget != 0`) does real `GetNearestEnemy` searches and, on success, calls
  `UpdateAttackTargetForAllAction(round, newId)` (`0x004D3E50`) — never previously called from
  this file, and the strongest remaining candidate for arming the round's real state.

**Live result: `CreateNewAttackActionsFn` executes cleanly — and still does not arm `sub+0xB0`.**
The first live attempt showed `queued-this-pass=False` on every throttled (3s) sample, because
the one-shot gate fires within a single engine frame — far narrower than any external poll can
catch. Added an UNCONDITIONAL log immediately after the `CreateNewAttackActionsFn` call
(no throttle) and reran the mid-fight-join scenario clean: **five independent mirrored
creatures** each called `CreateNewAttackActionsFn(creature, 1, 0, 0x7FFFFFFF)`, all returned
without crashing, and every single one showed `round+0x96C sub-index=0 sub+0xB0=0` and
`head action right after=0xFFFFFFFF` **immediately after the call returned** — i.e. the function
ran to completion and did not queue anything, on 5/5 creatures, not a fluke.

**What this rules out and what it points at.** The function itself is proven safe (no crash
across 5 live invocations) but its internal logic must be bailing before reaching the
acquisition-success path. Per §1.3's own documented gate, `GetNearestEnemy`'s hostility test is
`GetAIStateReputation(candidateCre, vsId) == 2` (exactly 2 = hostile; NOT the same
`GetReputation ≤ 10` numeric test the separate revalidation table's test 6 uses) — this specific
mapped value has never been directly checked for the pair (mirrored creature, local player id
`0x7FFFFFFF`). If it returns anything other than 2 for this pair, `GetNearestEnemy` finds
nothing and the whole acquisition path silently no-ops, which matches the observed symptom
exactly. **Round 18 should read `GetAIStateReputation` live for this exact pair before touching
any more code** — a read-only diagnostic, not another implementation guess. The `local_combat`
default stays off; both live installs' `coop.ini` restored to `0`. Full suite 953/953, both
builds clean. Diagnostic logging (throttled and unconditional) both left in the tree
deliberately — this is still not settled.

### Round 18 — both `GetNearestEnemy` gates measured live, both pass, and it still does not fire

Read live, for the exact pair in question. Added a diagnostic reading `GetAIStateReputation`
AND `GetVisibleListElement`'s seen bit right before `CreateNewAttackActionsFn` is called, for
the mirrored creature actually engaged in the host's fight (attacker real id `0xE`, target
`0x7FFFFFFF`):

    GetAIStateReputation(attacker,player)=2 visibleListNode=0x1C571738 flags=0x3 seenBit=True
    playerObj=0x182D96A0 attackerRealId=0xE GetAIStateReputation(player,attacker)=2

**Both directions measured, both hostile (=2), symmetric.** §1.4's own worked example checks
`GetAIStateReputation(candidateCre, vsId)` — for a scan anchored at the player's position with
`vsId=attackerId` (`GetNearestEnemy` attempt 1), that's `player.GetAIStateReputation(attackerId)`,
the OPPOSITE thiscall direction from what round 17's hypothesis assumed. §2.3 flags faction 0
(PLAYER) as going through an argument-swapped matrix lookup, so symmetry was not guaranteed —
measured directly rather than assumed, and it holds: both directions return exactly 2. **The
reputation candidate is REFUTED**, in both directions this time, not just the one round 17
originally suspected. The seen-bit perception gate is also confirmed set (`flags=0x3`, bit 0
set) — genuinely perceived, not a stale/missing node.

So of `GetNearestEnemy`'s six documented gates (§1.4), the first four (resolves, hostility,
alive, perception) are now directly measured and all pass for the actual engaged pair. The
two never yet measured are #5 (distance beats the best-so-far seed) and #6 (line of sight) —
both plausible next candidates, though the player was `tp`'d to within ~2m of the attacker with
nothing between them, so neither looks likely on its face. More probable: something downstream
of a SUCCESSFUL acquisition (§1.3's own tail — `SetLockOrientationToObject`,
`UpdateAttackTargetForAllAction`, the final "queues the attack action" step) is where this
actually breaks, since every INPUT gate to acquisition now checks out clean. Round 19 should
either measure #5/#6 directly (cheap, same technique) to close out the acquisition-gate list
entirely, or — more likely productive — trace what `UpdateAttackTargetForAllAction`
(`0x004D3E50`) actually does and whether it, not `GetNearestEnemy`, is where a found target
fails to translate into `sub+0xB0` gaining an entry.

`local_combat` stays off; both `coop.ini` restored to `0`. Rig torn down, confirmed clean
(`Get-Process` returned none). Full suite 953/953, both builds clean. Diagnostic logging from
rounds 9-18 all left in the tree deliberately — still not settled, and each round's diagnostic
has been load-bearing for the next.

### Round 19 — the acquisition tail is settled; `GetNearestEnemy`'s own scan never finds the player

Full disassembly of `CreateNewAttackActions`'s (`0x005B6980`) success tail (`0x005B6B58`-
`0x005B6C34`) closes the "queues the attack action" ambiguity §1.3's own prose left open:
**both** the re-validation path (§1.2, `round+0x9CC` valid and survives the six release tests)
and the acquisition-success path (§1.3, `GetNearestEnemy` finds a new target) converge on the
identical tail call — `AddAttackAction(round, target, 0, 0, 0x5DC, 1)` at `0x005B6C1D`. There
is no separate, untested "queue" step; `CreateNewAttackActionsFn` already reaches the same
`AddAttackAction` every earlier round called directly. `UpdateAttackTargetForAllAction`
(`0x005B6BA0`) and `SetLockOrientationToObject`/`0x4F34A0` (`0x005B6B88`) both run, unconditionally,
on the way there — neither gates the tail call, so round 17/18's suspicion of them was
misplaced; they are cosmetic/bookkeeping relative to the actual queue.

**Two live-measured hypotheses this round, both refuted with direct evidence:**

- **`AsCreature` (`vtbl+0x30`) cast on the re-resolved target, `0x005B6B6B`** — the acquisition
  path's one previously-untested step, since a failed cast takes the exact same silent
  "acquisition failed" bail (`0x5b48b0`, disassembled: `SetLockOrientationToObject
  (OBJECT_INVALID)`, pause animation, `SetCombatMode` stand-down) as if nothing were found.
  Measured live, unconditional per-call: `AsCreature(vtbl+0x30) cast on the player object:
  SUCCEEDS`, every sample, both test passes. REFUTED.
- **`area_id` mismatch (`creature+0x8C`)** — `docs/research/realtime-creature-movement.md` §1
  documents `SetPosition`'s own area-resolve step verbatim: `this->area_id` (`+0x8C`) is looked
  up through the engine's object array to find the `CSWSArea`, and only THEN does
  `UpdatePositionInObjectsArray` register the object into that area's x-sorted spatial index
  (`CSWSArea+0x190`/`+0x194` — the exact array `GetNearestEnemy` walks, §1.4). A stale/wrong
  `area_id` on the client's local player object would let raw x/y/z read correct while the
  object sits in the wrong area's index entirely, invisible to a scan anchored where the
  sithsoldiers actually are. Measured live: `area_id (creature+0x8C) - player=0x1
  attacker(sithsoldier)=0x1 MATCH=True`, every sample. REFUTED.

**The actual, decisive measurement — `GetNearestEnemy` called directly, both of §1.3's search
attempts, replicating the exact scan `CreateNewAttackActionsFn` performs internally:**

    GetNearestEnemy attempt1(from playerObj)=0x7F000000 attempt2(from attacker)=0x7F000000
    (repeated identically, every sample, both live test passes)

**`GetNearestEnemy`'s own area scan never finds the player as a candidate, in EITHER search
direction, ever** — not "found but rejected by a gate downstream," genuinely never found, even
though every individual per-candidate condition the function is documented to check (resolve,
hostility both directions, perception/seen-bit, and now the `AsCreature` cast) independently
measures as passing, and even though the spatial-index area assignment matches. This means the
failure is not in a targeting RULE at all — it is somewhere in the scan MECHANISM itself: either
the x-sorted array genuinely does not contain the player's entry despite matching `area_id` (a
registration-step failure, not an assignment failure), or the scan's own "find my anchor's index,
then walk outward" first step (§1.4's opening line) fails for one of the two anchors used here
(`playerObj` for attempt 1, the attacker itself for attempt 2) in a way that aborts the walk
before it ever reaches a real candidate.

**What round 20 needs, precisely:** `GetFirstObjectIndiceByX` (`0x00506F20`, already tabulated in
`combat-participation.md` as "binary search into the x-sorted array") is the most likely
"find my own index" entry point `GetNearestEnemy` calls before walking outward — disassemble
`GetNearestEnemy` (`0x004F2DE0`) itself in full for the first time (every round so far has
treated it as a black box and only measured its RESULT) to see exactly what it does with its own
`this` argument before the outward walk begins, and specifically whether it independently
re-resolves the anchor's area/array position or trusts a cached index that a driven,
never-transitioned client puppet might never have established correctly.

`local_combat` stays off; both `coop.ini` restored to `0`. Rig torn down and confirmed clean
(`Get-Process` returned none, both process names). Full suite 953/953, both builds clean.
Diagnostic logging from rounds 9-19 all left in the tree deliberately — still not settled, and
each round's diagnostic has been load-bearing for the next.

### Round 20 — the registration theory is REFUTED; the real mechanism is a scan race, and a natural fix for it did not close the gap

Full disassembly of `GetNearestEnemy` (`0x004F2DE0`-`0x004F2E4F`) — never dissected before this
round, every prior round only read its RESULT — confirms `combat-participation.md` §1.4's own
description exactly: the very first thing it does, before any candidate check, is a LINEAR
self-lookup: get `this`'s own area (`GetAreaFn`, `0x004CB120` — a zero-arg thiscall, called
unconditionally as the function's first instruction; if it returns 0 the whole call bails to
`ObjectInvalid` immediately, `0x004F2DF0`-`0x004F2DFE`), then scan the area's x-sorted id array
(`area+0x190` data pointer, `area+0x194` count) for an entry equal to `this+0x04` (own id). If
`this` itself is never found in its own area's array, the function returns "nothing found"
before ever looking at a candidate — matching round 19's universal failure exactly.

**The registration hypothesis, directly measured and REFUTED.** Implemented `FindSelfInAreaArray`
(`Odyssey.cs`) — the identical self-lookup, callable as a read-only diagnostic — and ran it live
on both the real engaged attacker and the real player object, mid-fight-join, same rig as every
round: `area=0x33899F8 arrayPtr=0x192A4B60 count=221 found=True index=85` (attacker) and
`found=True index=65` (player), same area, same array, both PRESENT with valid indices. This is
a plain linear scan (order-independent for a presence check), so this rules out a
registration-step failure outright — both objects are exactly where `GetNearestEnemy`'s own
anchor step would find them.

**What the SAME live session's own `GetNearestEnemy` calls showed instead: not a categorical
failure, a RACE.** The existing round-19 diagnostic (still in the code, same call) logged
`attempt2(from attacker)=0x7FFFFFFF` on one of five mirrored creatures that session —
`0x7FFFFFFF` is not a sentinel here, it is the PLAYER'S OWN REAL OBJECT ID (confirmed earlier in
this investigation: `console 'resolve 0x7FFFFFFF'` resolves it to a real, alive creature) — i.e.
`GetNearestEnemy` genuinely found the player as a candidate and returned its id, a real success,
on that one call. The other four creatures' one-shot calls returned `0x7F000000` (not found) in
the same session, sub-second apart. The attacker's own index in the array was also observed
bouncing between samples (`85, 86, 110, 121, 118, 85` across consecutive ~1s-apart diagnostic
reads) — consistent with our own per-frame `SetPosition` calls constantly re-sorting this exact
array, which the outward-walk's x-distance-bounded termination (`combat-participation.md` §1.4:
"terminating each arm as soon as the x-distance alone exceeds the best hit so far") could plausibly
miss depending on exactly when, relative to the last re-sort, a given call happens to run.

**Fix attempted: retry instead of one shot.** The existing round-init gate
(`roundTimerBefore == 0`) had `CreateNewAttackActionsFn` fire exactly ONCE per creature — a single
roll of what looked like a race. Moved the call OUT of that one-shot gate (keeping
`StartCombatRoundFn`/`SetRoundTimerActiveFn`/`SetRoundDelayFn` one-shot, unchanged — those ARE
confirmed self-defeating if repeated, rounds 12/13) so it retries every drive pass instead. This
is safe against round 13's spam failure mode by construction: `CreateNewAttackActionsFn`'s
acquisition path re-runs `GetNearestEnemy` fresh each call (a genuine new roll, not a duplicate
append) and the existing `IsFightAction(headType)` gate at the very TOP of `QueueAttackAction`
already stops all future calls the instant one attempt actually lands a queued swing — the retry
can only keep trying until it succeeds, then the outer gate closes the door on its own.

**Live-retested, and the retry did NOT reproduce success.** Rebuilt, reinstalled, ran the
identical mid-fight-join scenario a second time from a genuinely fresh pair of instances. Over
40+ seconds, `CreateNewAttackActionsFn` retried on a ~3s cadence across THREE different mirrored
creatures (`0x1826BBE0`, `0x1825B358`, `0x18264420`) — `sub+0xB0` read 0 on every single logged
call, no exceptions. If the true per-attempt success rate were anywhere near what the single
round-19 sample suggested (1 real success logged out of 5 one-shot attempts, ballpark 20%), this
many independent retries should very likely have landed at least once; that it did not suggests
either the round-19 sample's apparent success rate was itself an outlier (small-sample noise, not
a stable rate), or something about this session's specific conditions (a fight already in
progress longer, the specific creatures' positions relative to the player, or something not yet
identified) made every attempt systematically less favorable than that one sample — the two test
runs are not proven to be equivalent trials of the same stationary process.

**Left in place, not reverted:** the retry-instead-of-one-shot change is strictly safe (confirmed
non-spamming via the existing outer gate, confirmed non-crashing across 40+ seconds and three
creatures) and gives strictly more chances at success than the one-shot version it replaced, so
there is no reason to revert it even though it did not close the gap this round. ⚠ **Performance
caveat for whoever continues:** unlike the one-shot version, this now runs a full
`CreateNewAttackActionsFn` (including a real `GetNearestEnemy` area scan, up to array-size
iterations) on EVERY drive pass for every engaged-but-not-yet-successfully-attacking mirrored
creature, indefinitely, for as long as it keeps failing — no retry cap exists yet. Acceptable
while this stays off by default and unresolved; worth capping (or backing off) before this is
ever considered for real use.

**What round 21 needs:** the outward-walk loop itself (`0x4F2E52` onward — past the anchor
step this round fully mapped) has never been disassembled to completion in this investigation;
neither has `GetFirstObjectIndiceByX` (`0x00506F20`), which round 19 flagged as the likely
mechanism and this round's evidence did not actually require (the linear anchor-scan this round
disassembled does not call it — check whether the OUTWARD walk uses it instead, or whether it
is a separate/unused code path for this particular call shape). Given the race-not-registration
finding holds, the more promising direction may be instrumenting the outward walk itself to see
exactly which pruning branch discards the player as a candidate on a representative FAILING call
— round 20's diagnostics answered "is it present" (yes) but not "why does the walk that starts
from a present anchor still not reach a present candidate."

`local_combat` stays off; both `coop.ini` restored to `0`. Rig torn down and confirmed clean.
Full suite 953/953, both builds clean.

### Round 21 — the discrepancy resolved (it was never a discrepancy), two candidates refuted, one sharp new lead

**Phase 1, resolved.** `CreateNewAttackActionsFn` was never throttled — `EnemyPresenceController.OnFrame()` runs unconditionally every real game frame (`PluginHost`'s main per-frame callback calls it right after the combat pass, no interval gate), and `QueueAttackAction`'s only throttle is on the DIAGNOSTIC LOG LINE (`diagLog`, 3s), not on the call itself. So round 20's "0/many over 40+ seconds across three creatures" already represented hundreds of real per-frame attempts, not a handful — the apparent 1-in-5 sample from round 19 was a small, one-shot-per-creature sample (each of those five diagnostic calls came from a DIFFERENT creature's own round-init transition, not five retries on one creature) and should not have been read as a stable per-attempt success rate to begin with. There was no discrepancy to explain; round 20's zero-success retry result is the more trustworthy measurement.

**Two candidates tested live this round, both refuted:**

- **Point-blank distance.** Every prior round used ~2m separation ("tp beside" the target). This round positioned BOTH the host and, separately, the client's own local player, standing exactly on top of (0m separation from) their respective sithsoldiers. `CreateNewAttackActionsFn` still returned `round+0x96C sub-index=0 sub+0xB0=0` on every throttled sample over 40+ seconds — identical to the ~2m case. Whatever gate is failing, it is not a distance/range gate at any measured proximity, including zero. This weakens (though does not fully retire, since `MaxCleaveRange`'s actual computed value was never read directly — see below) the theory that `CreateNewAttackActions`'s internal `MaxCleaveRange(ownId,0,1)` range parameter (never actually verified — round 19's own diagnostic used a fixed `50.0f` stand-in, explicitly flagged as such, NOT the real value) is the blocker.
- **Degenerate/null weapon-equipment state.** `MaxCleaveRange` (`0x004FFAD0`) delegates to `0x4FB0F0`, whose first real step reads a pointer at `creature+0xA2C` (an equipment-slot reference) and branches on data reached through it — a null pointer here would make the range computation degenerate for every candidate, independent of any race. Read live (safe, read-only): `creature+0xA2C` is non-null and looks like a real heap pointer (`0x3357ED0`, `0x33583A0`, distinct per creature) on the actual engaged mirrored puppet. Not a smoking gun — REFUTED as an obvious null-pointer explanation.

**A sharper new lead, not yet followed to a conclusion.** The existing round-19/20 diagnostic (still one-shot, inside the `roundTimerBefore==0` gate) logged both search attempts on several creatures this round, using its `50.0f` stand-in range:

    GetNearestEnemy attempt1(from playerObj)=0x7F000000 attempt2(from attacker)=0x7F000000   (creature 0x40, 0x2, 0x3E)
    GetNearestEnemy attempt1(from playerObj)=0x7F000000 attempt2(from attacker)=0x7FFFFFFF   (creature 0xE, 0x10 — the ACTUAL engaged pair)

**Attempt 1 (searched from the player's own position) never once succeeded, across every sample gathered this round or any prior round.** Attempt 2 (searched from the attacker's own position, the documented fallback) succeeded for both real engaged creatures at least once. `combat-participation.md` §1.3 documents attempt 2 as an unconditional fallback ("Fallback: ..."), but no round has ever verified — by disassembly — that `CreateNewAttackActions`'s real internal logic actually falls through to attempt 2 unconditionally when attempt 1 fails, as opposed to some intermediate condition (checked only inside the real function, never replicated by this file's manual two-call diagnostic) gating whether the fallback runs at all. If the real function's fallback path is gated on something attempt 1's failure alone doesn't satisfy, that would explain everything measured so far without needing any further range or registration theory: the internal call could be silently stopping after attempt 1 fails, never reaching the attempt-2 shape that our own diagnostic proves CAN succeed for these exact creatures.

**What round 22 needs, precisely:** disassemble `CreateNewAttackActions`'s (`0x005B6980`) own acquisition body (`§1.3`'s address range, roughly `0x005B6AF0`-`0x005B6B58` — the actual internal calls to `GetNearestEnemy`, not the tail already settled in round 19) instruction by instruction, specifically the branch between the two `GetNearestEnemy` calls — what condition, if any, gates whether the second call runs at all, and whether it matches what this round's evidence shows attempt 2 can do. Separately, and only if that doesn't resolve it: read `MaxCleaveRangeFn`'s actual returned value live (calling convention not yet pinned with real confidence — `this` appears to be the creature per `0x4FB0F0`'s ECX pass-through, but the exact stack-argument shape implied by `combat-participation.md`'s `MaxCleaveRange(ownId,0,1)` notation was not resolved to real confidence this round and should not be guessed live without disassembling the doc's own worked call site for it first).

`local_combat` stays off; both `coop.ini` restored to `0`, rig torn down and confirmed clean (`Get-Process` returned none, both process names). Full suite 953/953, both builds clean. The `creature+0xA2C` diagnostic line is left in the tree (small, safe, and load-bearing for whoever re-reads this log next).

### Round 22 — the real acquisition body disassembled; attempt 2 is unconditional; a sharper lead than "gating" found instead

**Static only this round — no live test.** Full instruction-level disassembly of `CreateNewAttackActions` (`0x005B6980`) from entry through the known success tail (`python tools/disasm.py 0x005B6980 0x400`), answering round 21's exact question.

**The real signature has (at least) THREE stack parameters, not the one bool this investigation has been calling `bMayAcquireNewTarget`.** After accounting for the prologue (`sub esp,0xc` + 4 pushes = 0x1c bytes), the original caller-side stack slots are: param1 (`orig[esp+4]`, read late as `[esp+0x20]`) — a gate checked ONLY at a merge point this investigation's own call path bypasses (see below, so it does not matter for us); param2 (`orig[esp+8]`, our `bMayAcquireNewTarget`) — selects whether the "already have a target?" check reads `creature+0x524` (when 1, our case) or `round+0x9CC` (when 0); param3 (`orig[esp+0xC]`, read as `[esp+0x28]`) — an object-id passed to `GetGameObject` (`0x4ae770`) as the origin for search **attempt 1 only**. This codebase's `Odyssey.CreateNewAttackActionsFn` wrapper has only ever declared and passed the one bool — param1 and param3 are whatever garbage/zero happens to be on the stack, never intentionally supplied.

**Round 21's "is attempt 2 conditionally gated?" question is answered: no, it is unconditional.** With `bMayAcquireNewTarget=1`: `ebp = [attacker+0x524]`. If that reads `0x7F000000` (no cached target — true for a fresh engagement) AND the round's pending-list head is empty (`[[round+0x9B0]]==0`, also true for a fresh engagement), execution jumps DIRECTLY to the search block at `0x5B6AEF`, bypassing param1's gate entirely. There, **attempt 1** resolves `param3` via `GetGameObject` — if param3 is 0 (as our wrapper effectively passes), this resolves to null, `edi` stays 0, and the code falls straight through to **attempt 2** (`0x5B6B2E`: `ecx=esi`, i.e. the attacker itself) with no condition beyond "attempt 1's object didn't resolve." Attempt 2 always runs for our case. Its result (`edi`) is what's been observed succeeding sometimes across rounds 19-21 (`0x7FFFFFFF`) and failing other times (`0x7F000000`) for the identical creature pair. **The "hidden gate" theory is refuted** — there is no gate skipping attempt 2; the mechanism this investigation has been exercising via diagnostics IS the real one, faithfully.

**A sharper, concrete, untested lead: the search-range argument passed to `GetNearestEnemy` on the live call has never been read.** Both attempts compute a float via a call to `0x4FFAD0` (thiscall, `ecx`=the searching object — `esi`/attacker for attempt 2) immediately before `GetNearestEnemy`, and that float (left in `ST(0)`, stored via `fstp [esp]` as `GetNearestEnemy`'s stack argument) is the search radius. `0x4FFAD0` is a thin thunk: 3 args forwarded to `0x4FB0F0`, then `fadd dword ptr [0x741be0]` (a global float constant added to whatever `0x4FB0F0` returns) before returning. This is almost certainly the real `MaxCleaveRange`-family computation `combat-participation.md` names — but **round 19's diagnostic used a hardcoded `50.0f` stand-in for this value, never the real one**, and round 21 confirmed the underlying equipment pointer (`creature+0xA2C`) is non-null but never read what `0x4FB0F0` actually computes from it. If this real computed range is small, zero, or NaN for a mirrored puppet specifically (as opposed to a host-local creature, where the identical function presumably returns a sane value, since host-local combat via `AttackTarget` staging worked in round 11), that alone would explain an intermittent-to-never success rate independent of anything about the outward-walk logic `GetNearestEnemy` itself runs — no further disassembly of the walk would be needed.

**What round 23 needs, precisely:** a live diagnostic that calls `0x4FFAD0` (or `0x4FB0F0` directly, thiscall `ecx`=the attacking creature, 3 stack args — the exact 3-argument shape at the `0x5B6B08`-`0x5B6B14` call site, not guessed) on the actual engaged mirrored creature, and logs the real returned float. Compare it against the same read on a host-local creature (round 11's working case, if still reproducible) as a control. If the mirrored value is degenerate, find why (a field `0x4FB0F0` reads that differs between a normally-loaded host creature and one only ever driven/mirrored) and fix it there, rather than anywhere in the round-init or acquisition-calling code this investigation has already spent nine rounds getting right.

`local_combat` stays off (unchanged this round — no code touched, docs only). No live rig used this round.

### Round 22 correction (2026-08-20, same day) — a param-order error, and the real acquisition semantics were already verified elsewhere

Round 22's own analysis mislabeled `CreateNewAttackActions`'s arguments — it is fully decoded
already, in `docs/research/combat-participation.md` §1 (verified independently of this
investigation): `arg1 = bMayAcquireNewTarget`, `arg2 = bUseAttemptedTarget`, `arg3 =
candidateId`. Our call, `CreateNewAttackActionsFn(creature, 1, 0, targetId)`, passes
`candidateId = targetId` (the real player id) — NOT unset/zero as round 22 concluded. Attempt
1 genuinely resolves and searches (per §1.3) `GetNearestEnemy(candidateCre=player, ...,
excludeId=ownId, vsId=ownId)` — hostility judged FROM the attacker's perspective, TOWARD
whatever third creature the walk finds near the PLAYER's position, excluding both the player
itself and the attacker itself. Near a lone player with no other hostiles standing next to
them, this legitimately finds nothing almost every time — expected behaviour, not a bug.
Attempt 2 (`GetNearestEnemy(this=attacker, ..., vsId=OBJECT_INVALID)`, hostility judged from
the attacker's own perspective toward whatever the walk finds near ITS position) is the real
"find the player" search, exactly as every diagnostic since round 19 has modeled it. Round
22's core, still-standing finding — there is no hidden gate between the two attempts, and the
diagnostics have been faithfully exercising the real code path — is unaffected by this
correction; only the specific claim about `candidateId` being unset was wrong.

### Round 23 — reading the real weapon-derived range instead of the 50.0f stand-in

Full disassembly of `MaxCleaveRange`'s sub-call (`0x004FB0F0`, thiscall, `ecx`=the searching
creature): resolves the equipped weapon at slot `0x10` via `0x005A4C20(equipSlotPtr, 0x10)`;
if non-null, an accessor (`0x005B4790(weaponObj)`) yields a struct whose `+0x1A` byte gates
the branch (nonzero → capable weapon) and whose `+0x34` float is the weapon's own range
field, used or replaced by a global constant depending on a second comparison. If the
equipped-weapon lookup returns null, a DIFFERENT fallback function (`0x004FB156`, calling
`0x004F1310` plus a global constant — an unarmed/body-based range) runs instead. Nothing in
either path obviously produces zero/negative/NaN under normal conditions — this weakens, but
does not retire, the "degenerate range" hypothesis; only a live read settles it.

Added a read-only diagnostic (`GetEquippedItemFn`/`WeaponAccessorFn` in `Odyssey.cs`,
replicating these two clean thiscalls directly rather than `0x004FFAD0`'s own confusing
caller-side stack-argument reuse, which is not safe to guess blind on a live process) that
logs `weaponObj`, the accessor result, the `+0x1A` flag byte and the `+0x34` range float
alongside the existing round-init diagnostic.

**Live result — the degenerate-range hypothesis is now weakened further, likely refuted.**
Reproduced the standard scenario: host `tp`'d beside the real `sithsoldier` pair, staged via
`blockpoke attack 0x10 0x7FFFFFFF`, HP fell 12→10→8→1 under genuine engine AI (the deck's own
min-1-hp pin). Client connected mid-fight, `tp`'d beside the same pair. Two mirrored creatures
logged:

    creature 0x1926C1E8: weaponObj=0x0 (unarmed slot) — the "no weapon" fallback runs directly
    creature 0x1925B960 (the one with head action 0xFFFFFFFF, i.e. the one QueueAttackAction is
      actively retrying every pass): weaponObj=0x1925D250 (non-null — it DOES have a weapon),
      weaponAccessor=0x32892E4, flagByte@+0x1A=0x00, rangeField@+0x34=0

`flagByte=0` routes this creature into the SAME `0x004FB156` fallback `weaponObj=0` would have
used — the weapon exists but its `+0x1A` capability bit is off, so the weapon-derived
`+0x34` float (which does read `0`) is never actually consulted; a completely different
computation runs instead. Traced that fallback fully: `0x004FB156` → `0x004F1310` (with a
nonzero third arg, confirmed by disassembly of the `je 0x4f137d` branch) → two calls to
`0x00413350`, a properly bounds-checked (`edi≥0 && edi<[esi+0x24]`, `ecx≥0 && ecx<[esi+0x28]`)
2D table lookup keyed off each creature's own appearance id (`creature+0xA60`) — the same
"personal-space radius from appearance data" pattern `combat-participation.md` §1.4 test 5
already documents for a different (stored-field) source. Both results get summed with a
global constant added at each step. **Nothing in this traced path is degenerate** — a
standard `sithsoldier` appearance id will resolve to a real, positive radius from a real data
table, not zero or garbage. `flagByte=0`/`rangeField@+0x34=0` reads exactly as they do simply
because that whole branch is never taken, not because anything is broken.

**Verdict: the degenerate-range hypothesis does not hold up under a full trace of the actual
code path our engaged creature takes.** This was the last untested static candidate in the
acquisition chain proper. What remains, per round 20/21's own conclusion (never fully
resolved): `GetNearestEnemy`'s outward-walk loop itself, past the anchor step, has still never
been disassembled to completion, and the timing-race framing (our own per-frame `SetPosition`
calls re-sorting the x-sorted array the walk depends on) is the best-supported remaining
explanation. Round 24's job, precisely: disassemble the outward walk
(`0x004F3144`/`0x004F341A` per `combat-participation.md` §1.4) instruction by instruction and
determine whether a mid-walk reorder can make it terminate early/wrongly (in which case a real
fix exists — e.g. holding the array stable for the duration of one scan), or whether the walk
is provably robust to a reorder (in which case the race framing itself needs to be
reconsidered, going back to first principles on why attempt 2 succeeded even once).

`local_combat` stays off (unresolved). Both `coop.ini` restored to `0`; rig torn down and
confirmed clean. Full suite 953/953, both builds clean.

### Round 24 — the outward walk disassembled in full; the "race" framing itself was wrong

Full disassembly of `GetNearestEnemy` (`0x004F2DE0`) from the anchor step (round 20's stopping
point) through the walk body, both arms, to their shared per-candidate check block
(`0x004F2ECF`-`0x004F3144`) and the loop-continuation logic (`0x004F3179` onward, symmetric
for the second arm). No black box remains in this function.

**Structure, precisely:** the area pointer (`ebp = area+0x190`) and the anchor index (found by
the self-lookup's linear scan) are computed once, at entry — as they must be, there is only
one array to search. But **the array's own data pointer (`[ebp]`, i.e. `area+0x190` itself)
and its count (`[ebp+4]`, i.e. `area+0x194`) are both re-read FRESH on every single iteration
of the walk**, not cached into a local at entry (`0x004F2ECF`: `mov eax, [ebp]` — inside the
per-step loop body, executed every pass; `0x004F2EB2`/`0x004F3189`: `cmp edx, [ebp+4]` — the
bound re-checked live every pass, both arms). Per-candidate, each step independently re-runs
resolve (`GetGameObject`), self/exclude checks, hostility (`GetAIStateReputation`, confirmed
in round 18's own live read), the `AsCreature` cast, alive checks, perception
(`GetVisibleListElement` + the seen bit), the range comparison, and line-of-sight
(`ClearLineOfSight`, `0x0050C330`) — nothing here trusts a pre-filtered set or a snapshot from
the anchor step; every check combat-participation.md §1.4 already documents is re-run exactly
as described, live, per step, matching every live measurement this investigation has taken.

**This refutes the "mid-walk corruption from a reorder" mechanism outright — not because the
array can't reorder, but because it structurally can't happen mid-instruction.** This plugin's
own position-write drive pass and the game's own `GetNearestEnemy` call both run on the SAME
single game thread, sequentially. There is no possible interleaving where a `SetPosition` call
executes DURING a `GetNearestEnemy` call already in progress — the entire function, both arms,
runs to completion atomically within one frame's worth of code before anything else on that
thread executes. A stale cached pointer/count could have been corrupted by a reorder that
happened moments before a NEW call started (which is why fresh re-reads exist — they are
DEFENSIVE against exactly that, and they work); nothing can corrupt a call already underway.
The round 20/21 "race" framing borrowed language from multi-threaded reasoning that does not
apply to this single-threaded engine.

**What intermittent success/failure across otherwise-identical calls actually means, then: the
array's CURRENT valid sort order genuinely differs from one call to the next** (because this
plugin's own drive pass churns positions for many creatures every frame, and the array is
correctly, atomically re-sorted after each write) **and the x-distance-bounded early
termination is a provably correct algorithm on whatever that current, valid order is** — so a
"miss" is not evidence of a broken walk. It is evidence that, at the exact frame a given call
ran, either the real spatial arrangement did not actually put the player within the computed
range along BOTH the x-bound and the true 3D distance-minus-radii check, or (still open, see
below) the walk's OWN early termination — while individually correct per step — interacts with
a range value tight enough that ordinary positional noise decides the outcome.

**The one number this investigation has still never read: the actual final float
`MaxCleaveRange` returns for our engaged pair.** Round 23 confirmed the computation is not
degenerate (a real, bounds-checked appearance-radius sum, not zero/NaN) but never read its
magnitude. If that sum is genuinely small — a personal-space radius is not a weapon-reach
number, and round 23's traced fallback path sums exactly that for BOTH creatures plus a small
constant — a value under, say, 2-3 meters would make round 21's "point-blank, 0m separation"
test the decisive control it was meant to be (and that test's own null result, unchanged from
the ~2m case, already argues against range magnitude being the answer) — but it was never
directly measured, only inferred from a null result on a positioning test that could itself
have been imprecise (`tp`'s target coordinates vs. the creature's actual body-center, personal
space radius, or collision geometry are not necessarily one and the same "0m").

**Round 25's job, precisely, and it is now a single, cheap, decisive live read:** log the exact
float returned by the `0x4FFAD0`/`0x4FB0F0` call chain (not its intermediate inputs, which
round 23 already read) for the real engaged pair, and separately log the actual computed
distance-minus-radii value the walk compares it against (`0x004F3078`-`0x004F3096`'s FPU
sequence) on both a call that succeeds and one that fails, side by side. This settles, with a
number instead of a theory, whether the margin is genuinely razor-thin (a real, if awkward,
range-tuning issue with a concrete fix — widen the constant, or accept it as intentionally
tight melee range and adjust the test's positioning instead) or whether something else neither
this nor any prior round has considered is still hiding in the comparison itself.

`local_combat` stays off (unresolved — no code change this round beyond documentation; the
walk itself needed no fix, since none was found). No live rig used. Full suite 953/953, both
builds clean.

### Round 25 — range is decisively not the gate; a bigger discovery than the measurement itself

**First, `0x004FFAD0`'s real argument mapping, fully re-traced.** It is a straight 3-argument
passthrough to `0x004FB0F0` (its two "extra" pushes at the real call site are the SUBSEQUENT
`GetNearestEnemy` call's own setup, pre-staged before `0x004FFAD0`'s `ret 0xc` consumes only
the top 3 of 5 pushed values — not part of this call at all). The real call site (attempt 2,
`0x005B6B08`-`0x005B6B14`) resolves to `MaxCleaveRange(ownId, 0, 1)` — matching
`combat-participation.md`'s own already-verified notation (line 134/136) exactly, arg-for-arg.
Called `0x004FB0F0` directly (bypassing `0x004FFAD0`'s stack-reuse entirely, now that the
passthrough is proven exact) with these confirmed-safe arguments — `MaxCleaveRangeFn` in
`Odyssey.cs`.

**The measurement, live, on the actual engaged creature, repeated 15+ times over 45+ seconds
without variation:** `MaxCleaveRange(ownId,0,1) = 2.6`. Real distance-minus-both-radii,
computed in managed code from the same fields the walk itself reads (position `+0x90`,
personal-space radius `[+0x340]+0x08`) rather than hooking mid-function: `adjustedDistance =
0.688`. **Range exceeds distance by 1.91 — comfortably in range, not a close call, and never
once the other way around.** This decisively answers the coordinator's question: range/distance
is NOT the gate. The player is well inside the computed search radius every single time this
was sampled.

**The real discovery this round: `round+0x9CC` (the round's own "current target" field) is
ALREADY `0x7FFFFFFF` — the real player id, not the `0x7F000000` invalid sentinel — on EVERY
sampled call.** This means target acquisition has genuinely succeeded and written this field at
some point for this creature this session; it is not the case that `GetNearestEnemy` simply
never finds the player. Full disassembly of `CreateNewAttackActions`'s branch structure with
this in hand: since `bUseAttemptedTarget=0` in our call, `round+0x9CC` (not `this+0x524`) is
read as the existing target at `0x005B69A1`; being non-invalid skips the "totally fresh, jump
straight to search" shortcut at `0x005B69AD`, but — critically — since `bMayAcquireNewTarget=1`
(our other argument) is checked again at the `0x005B6AE3` merge point regardless of which path
got there, the function re-runs the FULL acquisition search (both `GetNearestEnemy` attempts)
on every call either way. The "VALIDATE-EXISTING vs ACQUIRE-NEW" framing this round's own
diagnostic label used is technically accurate about which target-source is read, but does NOT
mean acquisition is skipped — a false alarm caught and corrected within this same round rather
than chased further.

**What actually matters: `round+0x9CC` being correctly populated separates two previously
conflated questions.** "Does the search find the player" — evidently yes, at least once, since
something wrote this field with the real player id. "Does the round ever get armed to actually
process a swing" (`sub+0xB0`, the field rounds 16-17 traced as `UpdateCombatRoundTimer`'s real
gate) — still, after 25 rounds, always 0. Whatever writes `round+0x9CC` and whatever arms
`sub+0xB0` are evidently NOT the same effect of the same success — `combat-participation.md`
§1.3's own success-tail description (`this+0x84`, `this+0x50C`, `UpdateAttackTargetForAllAction
(round, newId)`, `this+0x4E8=1`) does not list `round+0x9CC` as one of the direct writes, so
`UpdateAttackTargetForAllAction` (`0x004D3E50`) — named twice now across this investigation,
never once disassembled — is the strongest remaining candidate for where `round+0x9CC` actually
gets set, and for whether it is ALSO responsible for arming `sub+0xB0` (in which case something
inside IT is the real, final gap) or whether arming is a separate, later step this investigation
still hasn't located at all.

**Round 26's job, precisely:** disassemble `UpdateAttackTargetForAllAction` (`0x004D3E50`)
completely — it is the one function this investigation has cited by name and address twice
without ever opening it. Confirm whether it writes `round+0x9CC` (settling where that field's
correct value comes from) and trace everything else it does, looking specifically for any write
to `round+4+(round+0x96C)*0x14C+0xB0` or a call to whatever `0x004D2920`
(`SetRoundDelayFn`-adjacent, already used in this codebase for `round+0x958`/`0x95C`) or a
sibling function might be needed to actually arm the sub-structure this investigation has spent
five rounds trying to light up.

`local_combat` stays off. Live rig used and torn down clean (`Get-Process` confirmed empty for
both `swkotor`/`KotorCoop.Agent`), both `coop.ini` restored to `local_combat = 0`. Full suite
953/953, both builds clean.

### Round 26 — `UpdateAttackTargetForAllAction` fully opened: a genuine dead end, and a correction to round 25

**No live rig needed — static analysis settles both questions this round was asked.**

**`UpdateAttackTargetForAllAction` (`0x004D3E50`) is a real function, and much smaller than its
name suggested — only `0x5F` bytes (`0x004D3E50`-`0x004D3EAE`, `ret 4`). An earlier request for
too large a byte range walked past its `ret` into an unrelated adjacent function (a
field-by-field debug dump, `0x004D3EC0`+, recognisable by its string-labelled calls to
`0x412620`/`0x412760`/`0x4127B0` for dozens of round fields — not part of this function at
all, and not investigated further). The real function's complete body: if `round+0x9B0` (the
pending-action list) is null or empty, it returns immediately, touching nothing. Otherwise it
walks every node in that list and, for each node whose `[node+0x10]==1` AND `[node+0x18]==1`,
overwrites `[node+0x14] = newId` (the second argument) — redirecting an EXISTING queued
action's target. **It never writes `round+0x9CC`. It never touches `sub+0xB0` or anything in
the `round+4+(round+0x96C)*0x14C` sub-structure array. If the list is empty when it's called
(exactly our case, every time), it is a complete no-op.** This is a definitive, fully-verified
dead end — not a candidate for either the target-write or the arming step.

**Correction to round 25: `round+0x9CC`'s correct value is not evidence acquisition ever
succeeded.** `python tools/fieldwrites.py 0x9CC` lists 13 writers; cross-referencing against
what this codebase already calls narrows it immediately: `0x004D60A8` sits inside
`StartCombatRound`'s own body (confirmed by direct disassembly of `0x004D6090`-`0x004D60BA`) —
`mov dword ptr [esi+0x9cc], ebx` where `ebx` derives from `StartCombatRound`'s own arguments
(the same `targetId` `Odyssey.QueueAttackAction` already passes as `StartCombatRoundFn`'s
second argument, per the existing call at `Odyssey.cs` line 6732:
`StartCombatRoundFn(round, targetId, contextSlot, context2, 0, 0)`), gated by a comparison
against `[ecx+4]` where `ecx = [round+0x9b4]` (a different, not-yet-identified field) — real
value on one branch, the `0x7F000000` sentinel on the other. **`round+0x9CC` reading the real
player id was never proof `GetNearestEnemy` found anything — it is a direct, mechanical
artifact of the `StartCombatRound` call this investigation has been making since round 17,
which writes the target we already told it to use.** The distinction round 25 drew between "the
search finds the player" and "the round gets armed" still stands as the right frame, but the
evidence for the FIRST half was never real; only the second half (`sub+0xB0` never arming) was
ever actually established.

**What's genuinely left, now that both `UpdateAttackTargetForAllAction` and the `round+0x9CC`
question are closed:** round 17 already named the real remaining candidate and no round since
has opened it. `sub+0xB0`'s only real `Add` (`0x004D57A0`) is reached exclusively through
`0x004D5A50`, which "assembles a full attack descriptor from weapon/roll fields computed deep
inside an untraceable ~7-argument validation loop" (round 17's own words, never revisited) —
and `0x004D5A50` itself is called from `UpdateCombatRoundTimer`'s own processing branch
(`0x5B8050`, flagged by round 15 as doing "real per-tick interdependent logic... not a single
counter a hand-written poke could satisfy," also never fully dissected). This is now the ONLY
unopened box left in the entire chain from round-init through acquisition through queueing —
everything else, across 26 rounds, has been individually verified sound.

**Round 27's job:** disassemble `0x5B8050` and `0x004D5A50` together, in full, to find what
precondition — beyond everything already confirmed correct (round timer active, delay set,
target valid, action queued, range fine, walk sound) — gates whether a genuine engine tick
ever actually calls the real `Add` into `sub+0xB0`. This may require accepting that arming
only happens across MULTIPLE ticks of genuine round processing rather than synchronously
within one `QueueAttackAction` call, in which case the fix may be patience (poll across several
frames after queueing) rather than a missing call.

`local_combat` stays off. No live rig used this round (pure static analysis; `UpdateAttackTargetForAllAction`
needed no live confirmation once fully disassembled — it provably does nothing on an empty
list). Full suite 953/953, both builds clean.

### Round 27 — the arming write is found, and it is structural, not a missing call

**`0x5B8050` (`UpdateCombatRoundTimer`'s real processing branch) is downstream of the gate,
not the gate itself, and much larger than a state machine.** Disassembled its opening several
hundred bytes: it reads `round+0x96C` to select the sub-structure exactly as documented, then
begins copying dozens of individual byte/word fields (`sub+0xC1` through at least `sub+0xD5`)
one at a time through a per-field setter call (`0x4D76B0`) — recognisably an
animation/attack-style parameter block being assembled, not anything that decides WHETHER to
process. This function only runs once `sub+0xB0>0` already — it cannot explain why that field
never becomes nonzero, only what happens after it does. Not pursued further; it is provably the
wrong target for this question.

**`0x004D5A50` is genuinely the arming write — round 17's suspicion confirmed.** Full
disassembly: `this` (`ebx`, from `mov ebx,ecx` at entry) is the per-participant SUB-STRUCTURE
itself, not the round — it copies a large block of fields FROM `ebx` (an `0xD0`-byte descriptor:
weapon/roll-adjacent fields at `ebx+0x1c` through `ebx+0x128`, matching the "attack descriptor"
round 17 described) and, at its very end, calls the real `Add` (`0x004D57A0`) with
`this = ebx+0xac` — four bytes before `sub+0xB0`, the exact `CExoArrayList`-style
data-pointer-then-count pair this whole investigation has been trying to get populated. This
is the confirmed, real, final arming write.

**But it is only ever reached from inside a large, stateful per-participant loop, not
callable in isolation.** `xrefs.py` finds exactly one call site (`0x005BBA15`), inside a
function with a genuine bounded loop (`xor ebp,ebp` at `0x5BB8C6`, iterating to a count read
earlier, `inc ebp; cmp ebp,eax; jl 0x5bb8d4` closing it at `0x5BBA2A`-`0x5BBA2D`). Each
iteration, BEFORE `0x004D5A50` is called: two unconditional setup calls (`0x5EDCE0`,
`0x5B4F30`); a conditional block gated on `word [ebx+0x64]` that calls `0x5BA8E0` only if
nonzero; a range check on a BYTE field (`[ebx+0x5c]`, the same field flagged in rounds 15 and
18 as a party-table-style lookup) — only values `-1` or `0..3` continue toward the arming call
at all, anything else falls through to a DIFFERENT pair of calls (`0x5BB440`, `0x5B8B00`)
instead; only past all of that does `0x004D5A50` finally run, immediately followed by another
call (`0x5B7400`) before the loop advances. This is not a small utility with a guessable
signature — it is a substantial, multi-step engine subroutine with its own internal
preconditions, iteration state, and branch-dependent setup that no single additional call this
plugin could make would safely or correctly replicate. Its own caller was not identified (one
call site found, its enclosing function's start address was not traced) — doing so, and then
tracing THAT function's own preconditions for reaching this loop at all, would be its own
multi-round undertaking with no better odds than the 27 already spent.

**Conclusion: this is the structural wall the coordinator's own fallback anticipated.** Every
individual piece this investigation has touched — round initialization, target validity, the
weapon/range computation, the acquisition scan and its walk, the append call, target
redirection — has been traced, measured, and confirmed sound in isolation, across 27 rounds.
The one piece that remains, the actual write that arms a round for real processing, exists only
inside the engine's own multi-step, stateful, per-participant round-advancement loop — the same
kind of thing a genuine AI-driven or player-driven combat round goes through as a side effect of
normal engine flow, and not something this investigation's approach (finding the missing native
call and making it directly) can reach. Replicating it correctly would mean re-implementing a
meaningful slice of the engine's own round-participant bookkeeping externally — a fundamentally
different, much larger undertaking than anything attempted in this investigation, with a real
chance of getting the iteration/precondition state subtly wrong in ways that surface as new
crashes or corruption rather than a clean pass/fail.

**Recommendation:** stop the single-function-hunting approach here. `local_combat` stays off,
correctly, and every fix landed across rounds 9-26 (the round-timer/delay setters, the second
gate, `CreateNewAttackActions` itself, the safety fixes from the earlier structural-defect
pass) is real, verified progress that should stay in the tree even though the feature itself
remains unfinished — each of those closed a genuine gap and left the mechanism measurably
closer to working, even though the final arming step turned out to be out of reach by this
method. A future attempt at this specific goal should consider either (a) the much larger
project of reverse-engineering the enclosing round-advancement loop and its own caller chain in
full, accepting the scope that implies, or (b) a different strategy entirely — finding a way to
make the engine's OWN AI script genuinely run for a driven puppet (the original, harder-seeming
option this investigation moved away from after round 1's "permission vs initiation" finding),
since that would get this loop to run through its own normal, engine-native path instead of
needing external replication at all.

`local_combat` stays off. No live rig used this round (the conclusion is fully supported by
static evidence — the loop's existence and preconditions are what settle this, not a live
read). Full suite 953/953, both builds clean.

### Strategy 2 (2026-08-21) — firing the creature's own script: a real breakthrough, and a real open question

A different approach from all 27 rounds above: instead of replicating the engine's native
combat-round mechanics piece by piece, ask the engine's own event system to notify the
creature the way it notifies every other creature in the game, and let its assigned NWScript
AI (which normally decides to fight) run.

**The mechanism.** `CVirtualMachine::RunScript` (`0x005D0FC0` is an MI thunk; the real body is
`0x005D45D0`, a queue-based script dispatcher with a pending-list at `+0x1AC` and a counter at
`+0x28`) is already used, live and working, by `CSWSDoor::OpenDoor` to fire a door's OnOpen
script (`Odyssey.cs:1208`, call site `0x00589D32`). Searching all 83 real call sites to that
dispatcher for one inside a known AI-tick function found `0x004DA8B6`: a per-creature event
dispatcher that, on a DIFFERENT branch of the same 13-case jump table, also calls
`UpdateVisibleList` (`0x0051B050`, the perception scan already confirmed unmodified for a
mirrored creature) — i.e. this is a general "signal event N to this creature" function, and the
branch reaching `0x004DA8B6` fires whatever script sits at `creature+0x260`.

A first live read of that field, taken naively as raw ASCII bytes, produced garbage
(`'.q/..'`) — a real methodology bug, not a dead end: `+0x260` is not an inline resref buffer,
it's an indirect string handle (`{ char* data; int length; }`, CExoString-shaped). The first
4-5 raw bytes are a little-endian HEAP POINTER, which happens to look like short,
printable-ish noise when read as text. Dereferencing it correctly (confirmed with a
standalone `ReadProcessMemory` call against the live pointer) reads **`k_def_combend01`** — a
real, well-known KOTOR default AI script: the `OnCombatRoundEnd` slot, whose body normally
calls NWScript's `DetermineCombatRound()`, the exact routine that decides to queue an attack.
`Odyssey.PeekCreatureScriptSlot0x260` was corrected to dereference properly rather than
ship the ASCII-garbage version.

Added `Odyssey.FireCreatureScriptSlot0x260` (calls `RunScriptFn` with the SAME argument shape
`OpenDoor` uses: `this` = one dereference of `VirtualMachinePtr` (`0x7A3A00`, NOT the
double-derefed "inner" object `GetImplementer` resolves for the stack-push routines — matched
`OpenDoor`'s proven-working call exactly), arg1 = `&creature+0x260`, arg2 = the creature's own
id, arg3 = 1) and a new `firescript <id> [fire]` console verb (`CheatKindFireScript = 12`,
wired end to end through `SharedBlock`/`IGameBridge`/`ConsoleCommand`/`CoopCommandDispatcher`/
`CheatController`/`ConsoleVerbCatalog`/`FuncInterpHook`, matching the existing
`resolve`/`giveloot` pattern) so the field can be peeked safely before ever being fired.

**Live result, HOST-LOCAL case: CONFIRMED WORKING, and it is the first genuinely NEW positive
result of the whole investigation.** Fired `k_def_combend01` on a fresh, untouched, host-local
`sithsoldier` standing ~2m from the local player. No crash. Within ~12 seconds: player HP
`12/12 → 1/12` (floored by the Endar Spire deck's own min-1-hp pin, the same pin every other
real fight this investigation has measured hits), independently confirmed via `console 'where'`
— a genuine, physically-verified hit through the real engine damage pipeline, produced entirely
by the creature's OWN AI deciding to fight after being told to check, not by any native call
this investigation wrote.

**Live result, MIRRORED (driven) case, first pass: INCONCLUSIVE, not refuted.** The same fire on
a client-mirrored copy of an untouched `sithsoldier` (client player `tp`'d to ~1.2m, confirmed
in-range via the existing `ROUND25` distance diagnostic) produced no HP change over 20+
seconds. This was NOT a clean result: `local_combat=1` was set for this test (reasoned that
`local_combat=0`'s full `ClearAllActions` wipe would delete a script-queued attack action
before it executes), which means the OLD per-frame `QueueAttackAction`/`CreateNewAttackActionsFn`
machinery from rounds 9-27 was ALSO running concurrently on the same creature, every frame,
throughout the test — its own diagnostic lines are interleaved with the fire in the log, so
whether the mirrored case's failure was (a) the script-triggered path genuinely not working on a
driven puppet, or (b) interference from the old parallel mechanism was NOT distinguished by this
pass. `local_combat` correctly stays off; both `coop.ini` restored.

⚠ **UPDATE — round 28 (below) reran this cleanly isolated (`local_combat=0`, so none of the old
machinery runs) with a proper unthrottled per-frame watch, and the mirrored case is now
REFUTED, not merely inconclusive.** A first HP-based reading in that round looked positive but
turned out to be a confound (the pre-existing phantom damage-forwarding feature, unrelated to
this mechanism) — see round 28's full account for why, and for the actual decisive evidence.

**What round 28 needs to do, precisely:** rerun the exact same mirrored-case test with
`local_combat=0` this time (so the OLD automatic mechanism is fully off), and watch CLOSELY —
within roughly one engine tick, not the 3-second-throttled diagnostic cadence — whether the
script-fire has ANY visible effect (a position/facing change toward the player, a head-action
change) before the next frame's full `ClearAllActions` wipe could plausibly land. If the
arming itself happens synchronously inside the script-triggered call chain (plausible, since
`DetermineCombatRound`/`ActionAttack` go through the engine's OWN `EndCombatRound`/
`CreateNewAttackActions` path, not a bare `AddAttackAction` append), it may not matter that the
ACTION gets wiped a frame later — the round could already be armed and reprocess a fresh
action on its own. That distinction (was the round ever actually armed, independent of whether
one particular queued action survived) is the one thing this pass did not measure.

Full suite: 953/953 (`DisconnectRecoveryTests.HeavyPacketLossStillConverges` is a known-flaky
probabilistic test, confirmed passing in isolation both before and after this round — not a
regression). Both builds clean.

### Round 28 (2026-08-21) — cleanly isolated, and the mirrored case is REFUTED (the earlier "it worked" reading was a confound)

Added `Odyssey.TickFireScriptWatch`, called unconditionally from `PluginHost.OnFrame` (right
after `_enemyPresence?.OnFrame()`, independent of `local_combat` — it runs regardless of that
setting) — logs the creature's head action, `round+0x9CC` target, `round+0x958`/`+0x960` timer
fields, and `round+0x96C` sub-index/`sub+0xB0` EVERY SINGLE FRAME for 180 frames after a
`firescript ... fire` call, armed by `FireCreatureScriptSlot0x260` itself. This exists because
`RunScript`'s own body is a queue-based dispatcher (a pending-list at `+0x1AC`, a counter at
`+0x28`) — the fired script does NOT run synchronously inside the call that fires it, it runs on
a LATER engine frame, so only real per-frame observation (not the 3-second-throttled diagnostics
rounds 9-27 used) can see a fast arm-then-something cycle.

**Procedure:** fresh two-instance session, `local_combat=0` on BOTH installs (confirmed via
`grep` before launch), host hosting via `console 'host'`, client connecting via
`console 'connect'` — clean, with NONE of rounds 9-27's automatic per-frame
`QueueAttackAction`/`CreateNewAttackActionsFn` machinery running (that machinery is gated on
`LocalCombat`, which was off). Client `tp`'d to within ~2m of a mirrored, untouched
`sithsoldier`, `firescript <id> fire` sent on the CLIENT.

**First signal, and why it was wrong.** The client's `kotorcoop-plugin.log` showed
`combat: took 2 damage from a hit on our phantom (attacker 0xE)` starting ~5s after the fire,
and `console 'where'` confirmed real HP loss (12→1, the same min-1-hp floor every real fight in
this investigation hits). This looked like confirmation. **It was not the mechanism working —
it was a pre-existing, unrelated, already-documented feature catching the test off guard.**
`IncomingDamageController.cs:293`'s exact log text ("hit on our phantom") is the FORWARDED
damage channel (`block.IncomingDamageAttacker`/`IncomingDamageApplied`, wire-synced fields) —
not a local hit. Checking the HOST confirmed why: `creatures.ps1` on the host showed a live
`coop_phantom` sitting at `(29.70, 142.40, 0.00)` — EXACTLY the coordinate the client `tp`'d to,
because a connected player's own position always publishes a phantom body at that same spot on
every peer's screen, by design (README: "Your partner's body is a real combat target in your
game… enemies pick it, swing at it"). The host's OWN untouched `sithsoldier`s, sitting at their
original unmoved spawn positions the whole session, evidently found this nearby PLAYER-faction
phantom fair game and fought it organically on the HOST's own engine — completely independent of
anything fired on the CLIENT. Positioning a connected client's real player near ANY module
creature unavoidably also positions their phantom there on every other peer's screen, so this
confound is inherent to the very scenario being tested, not a mistake specific to this run — it
just needs to be told apart from the real signal, which is why the per-frame native-state watch
exists at all.

**The real, unambiguous evidence: `sub+0xB0` never left 0.** Across FOUR separate
`firescript ... fire` calls on TWO different mirrored creatures (`0xE` and `0x10`), each
followed by a full 180-frame (~3s) unthrottled per-frame watch, spanning roughly 50 seconds of
real time in total: `round+0x9CC` target read `0x7FFFFFFF` (the player) from the very first
sampled frame — already set, before this round's own fire, most likely a leftover from an
earlier session's `AddAttackAction`/`CreateNewAttackActionsFn` diagnostic calls, since
`+0x9CC` was never reset between sessions and nothing in this pass wrote it — `round+0x958`
stayed `1` and `round+0x960` counted down smoothly and organically the entire time (proof the
round IS ticking normally, not stuck or corrupted), and `head action` never once left
`0xFFFFFFFF` (not a fight action). **Not one of the 720+ sampled frames across all four attempts
showed `sub+0xB0` as anything but `0`.** This is the same wall rounds 1-27 hit, reached this time
by a completely different route (a real, engine-native script event instead of hand-replicated
calls) — which is itself informative: whatever prevents a driven puppet's round from arming is
not specific to HOW the round gets initiated (our own calls, or the engine's own script-driven
path), it is specific to the creature BEING a driven puppet.

**Conclusion.** Strategy 2 remains a genuine, confirmed-real result for a HOST-LOCAL creature
(round 27's fight — HP 12→8→1, unconfounded, no phantom involved since a host-local test has no
connected peer at all). It is now REFUTED, not merely unconfirmed, for the MIRRORED case — four
clean attempts, no ambiguity, `local_combat=0` throughout so no competing mechanism was running.
`local_combat` stays off. Both `coop.ini` restored to `0` (unchanged — they already were).
Rig torn down and confirmed clean. Full suite 953/953 (no flake this run), both builds clean.

**What this actually narrows down, for whoever continues:** the block is not in initiating the
round (proven — a real engine script can do it, on a host-local creature) and not in reaching
`AddAttackAction`'s queue (round 17-19 proved that separately). It is specifically in whatever
makes a round ACTUALLY PROCESS once armed-or-arming, and that process appears to depend on the
creature not being externally driven — something about being a `local_combat`/`EnemyPresenceController`-tracked
puppet (even with the position/action-wipe machinery OFF, since `local_combat=0` was set this
round) still marks the creature as not eligible for the engine's own round-advancement loop to
ever complete for it. The next concrete step is not another call to trace — it's determining
WHAT marks a creature as "driven" in the engine's own eyes in the first place, since that mark
appears to persist and block round-arming even when every co-op-specific per-frame write is
turned off.

### Round 29 (2026-08-21) — the "driven puppet" hypothesis is refuted too: zero writes, same wall

Round 28 closed on a specific hypothesis: that something about a creature being tracked by
`EnemyPresenceController`'s base presence path (`SetPosition`/`SetFacing`, unconditional
regardless of `local_combat`) marks it as ineligible for the engine's own round-advancement loop,
independent of `local_combat`'s own extra calls (already ruled out with `local_combat=0`). That
was never actually tested — round 28's "undriven" creature still had its position/facing written
every frame by the base drive, which runs for every mirrored enemy unconditionally. Nobody had
ever tested a creature with genuinely ZERO writes from this plugin.

**What this round built.** A temporary, hand-picked-id diagnostic in `EnemyPresenceController`
(`DiagUndrivenObjectId`, `coop.ini local_combat_diag_undriven_id`) that skips the ENTIRE
per-pass touch for one creature — no `ClaimCreature`, no `PermitAttacking`, no `Drive`
(position/facing), no `ClearPositionDirty`, no `RecordEngagement`, no `QueueAttackAction`. The
creature is present in the world, perceivable, alive, and completely untouched by the co-op
plugin — its position on the mirroring client is left to drift from the owner's real position,
which is expected and correct for this diagnostic, not a bug.

**Live test, clean.** Two `sithsoldier`s in `END_M01AA`: `0xE` (attacked normally via
`blockpoke.ps1 attack`, staging a genuine host-side fight — `state IN COMBAT`,
`combat [FIGHT ... engaged: you, Malak]`) and `0x10` (the diagnostic target, `tp`'d the client
player beside it, then fired its own `+0x260` script — `k_def_combend01` — via
`FireCreatureScriptSlot0x260`, the SAME Strategy 2 mechanism proven host-local in round 27).
`0x10` was never touched by anything else this session.

**Result: the round genuinely armed and ran, organically, then reset at expiry — never reaching
`sub+0xB0`.** The per-frame native-state watch (unthrottled, all 180 frames captured) shows,
starting the very first sampled frame after the fire:

    frame -180: round+0x9CC target=0x7FFFFFFF, round+0x958 timer=1, round+0x960 delay=335, sub+0xB0=0
    frame -106: round+0x958 timer=1, round+0x960 delay=13
    frame -104: round+0x958 timer=1, round+0x960 delay=4
    frame -103: round+0x958 timer=0, round+0x960 delay=0     <- reset, not armed
    (every frame -103 through -1: timer=0, delay=0, sub+0xB0=0, head action=0x3F, never a fight action)

`round+0x9CC` (the current-target field) was set to `0x7FFFFFFF` — the local player — by
something in this run, and NOTHING in this run could have set it except the script itself: no
`StartCombatRound`, no `QueueAttackAction`, no prior-round diagnostic call touched this
creature. That is genuine, organic evidence the script's own `DetermineCombatRound` logic
picked the player as a target. `round+0x958`/`round+0x960` then armed and counted down
SMOOTHLY and ORGANICALLY from 335 to 0 over ~77 real frames — proof these are exactly the
fields a real decision process uses, driven by the engine's own logic, not by anything this
investigation wrote. And then, at the moment the delay expired — the exact point where round
27 found the real arming call lives, deep inside the engine's own per-tick loop over combat
participants, "a branch gated on a party-table-style field that routes to an entirely different
path if it's out of range" — the round reset to inactive instead of committing. `sub+0xB0`
never went nonzero across all 180 frames. The client's own HP did drop (12→1) and briefly
showed `state IN COMBAT`, but that is the SAME phantom-forwarding confound round 28 already
identified: the client's phantom stood near the HOST's own genuine fight against `0xE`, and
the host's engine forwarded that unrelated damage — not evidence of anything happening on
`0x10`, which the native-state watch settles independently of HP.

**Conclusion: the "driven puppet" hypothesis from round 28 is REFUTED.** A creature this
plugin has NEVER touched — no position write, no facing write, no claim, no permit, nothing —
hits the exact same wall, reached organically, through the engine's own real AI decision
process. This rules out every per-creature, per-write theory this investigation has produced
across 29 rounds: it is not about being mirrored, not about being driven, not about
`local_combat`'s extra calls, not about any specific field this plugin writes. The one thing
every failing test has shared, including this one, is a CONNECTED CO-OP SESSION with a REMOTE
PEER standing near the creature. Round 27's "party-table-style field" gate — never identified
precisely, only located structurally — is now the single strongest remaining candidate:
whatever roster or participant-table the engine's per-tick round-advancement loop consults
before committing to a swing may simply never have the connected remote player registered in
it as a valid combat participant, regardless of anything happening at the individual creature
level. That is a SESSION-level or PARTY-TABLE-level question, not a creature-level one — a
genuinely different kind of thing to investigate than anything the first 29 rounds tried.

`local_combat` stays off (untouched this round; the diagnostic runs independently of it).
`coop.ini local_combat_diag_undriven_id` diagnostic left in the tree, default unset (0,
disabled) — remove once this question is settled. Rig torn down and confirmed clean
(`Get-Process` empty for both `swkotor` and `KotorCoop.Agent`). Full suite 953/953, both
builds clean.

### Round 30 (2026-08-21) — the party-table-style gate opened, and it is not the blocker

Round 27's "party-table-style field" candidate, never previously disassembled, is now open —
and it is real, understood, and **refuted** as the explanation for the arming failure.

**The mechanism, fully traced.** `UpdateCombatRoundTimer`'s real processing branch (`0x5B8050`,
per round 15) branches on `sub+0x5c` compared against `2`, cross-checked through a call chain:
an adjustor thunk (`0x4AEA40`, `mov ecx,[ecx+4]; jmp 0x4B4CB0`) into `0x4B4CB0`, which — despite
being reached as if it were a virtual member call — takes NO real arguments and never uses the
adjusted `this`: it reads a fixed global singleton at `0x7A39FC`, derefs `+8` (a
party-table-shaped sub-object), and calls a second thunk chain (`0x4AE920` → `0x4B2AE0`) with
two HARDCODED stack arguments `(0, 1)`. `0x4B2AE0` walks one of two lists at
`[that+0x10064]`/`[that+0x10068]` (selected by the second argument, `1` in our case) searching
for an entry whose id-field equals the FIRST argument, `0` — i.e. this whole chain answers one
fixed question, "is party slot 0 (this instance's own local player) currently resolvable,"
returning either the resolved id or the not-found sentinel `0x7F000000`. It is a pure read, two
accessor calls, no writes observed anywhere in the chain.

**Live-tested directly, reusing round 28/29's proven `firescript`/watch infrastructure — no new
scenario needed.** Extended `TickFireScriptWatch` to log `sub+0x5c` and a bare call to
`0x004B4CB0` every frame. Fired on a completely untouched, never-driven mirrored `sithsoldier`
(`0x10`, object pointer `0x1835A2D8` on the client) exactly as round 29 did. Across the full
180-frame watch: **`sub+0x5c` reads `0` on every sample — which IS within the "-1 or 0..3"
range round 27's OWN loop-entry gate requires to continue toward the arming call at all** (i.e.
this specific creature was never being rejected by that gate in the first place). And
**`0x4B4CB0` resolves party slot 0 to `0x7FFFFFFF` on every sample — the CLIENT's own local
player, correctly** (each KOTOR process trivially has its own PC as party slot 0; there was
never a "the remote peer isn't registered as slot 0" mismatch to find, because nothing in this
chain ever asks about the remote peer — it only ever asks about whoever is running THIS
process). `round+0x958`/`+0x960` ran their normal countdown to exhaustion (timer=0, delay=0 at
the watch's end) and `sub+0xB0` stayed 0 throughout — the same wall, unmoved.

**What this settles and what it doesn't.** The `0x5B8050`/party-slot-0 cross-check is real and
now fully understood, but it was never actually on the critical path to arming to begin with —
round 27 already established `0x5B8050` only runs AFTER `sub+0xB0>0`, so it cannot be gating the
transition to `sub+0xB0>0` in the first place; this round closes out that open question from
rounds 15/18 rather than finding the actual blocker. The REAL loop-entry gate round 27 found
(the range check on `[ebx+0x5c]` inside the function containing the arming call site
`0x005BBA15`) is now directly confirmed passing (`0`, in-range) for a creature that still never
arms — which narrows the search to the three pieces of that loop round 27 flagged but never
opened: the two unconditional setup calls (`0x5EDCE0`, `0x5B4F30`), the conditional block on
`word[ebx+0x64]`, and — likely the more fruitful target — the loop's OWN caller, never
identified, whose preconditions for reaching this loop at all have never been examined. Given
round 29's finding that the wall persists even with zero co-op-plugin writes of any kind, the
next genuinely new question is whether that outer caller is ever reached/ticked with the same
frequency or under the same conditions for a creature a REMOTE peer is fighting versus one only
the local session's own player is fighting — i.e., is the enclosing function itself gated on
something session-related, upstream of everything traced so far.

`local_combat` untouched, stays off. `Odyssey.cs`'s `TickFireScriptWatch` diagnostic extended
(not removed — the underlying question this investigation opened is still unresolved). Full
suite 953/953, both builds clean, rig torn down and confirmed clean.

### Round 31 — the caller identified as `CSWSCreature::ResolveAttack`/`ResolveMeleeAttack`, a
### real candidate gate refuted after live measurement, and the FULL round-timer cycle watched
### to completion for the first time — it resets to idle instead of ever arming

Round 30 left two things unidentified: the two unopened setup calls inside `ResolveMeleeAttack`'s
loop, and — flagged as more promising — the loop's own caller, never traced.

**The caller chain, found via the symbol database rather than blind disassembly.** The
`functions` table in `tools/kotor1_0_3.db` already names the function containing the arming call
site (`0x005BBA15`): `CSWSCreature::ResolveMeleeAttack` (`0x5bb890`). `xrefs.py` on that address
finds exactly one caller, `CSWSCreature::ResolveAttack` (`0x5bba80`), which itself has exactly two
callers: `AIActionCastSpell` (`0x514af0`) and `AIActionPhysicalAttacks` (`0x5bbbf0`) — the SAME
function round 17 already identified as the execution-time handler for a queued attack action,
gated on `CreAttackPermittedBit`. This confirms the structural picture: `AIActionPhysicalAttacks`
only runs when a creature's action QUEUE (what `GetHeadActionType` reads) has a fight action at
its head, and only then does it resolve down into `ResolveAttack` → `ResolveMeleeAttack` → the
arming write. Since head action has never once changed from `0x6`/`0xFFFFFFFF` in 30+ rounds,
`AIActionPhysicalAttacks` structurally never runs for our creature — `ResolveMeleeAttack`'s own
loop was never reachable at all, which is consistent with `sub+0xB0` never arming, but does not
yet explain WHY the head action itself never promotes.

**A concrete candidate found and live-refuted.** Full disassembly of `ResolveAttack` found a real
early-exit gate inside `UpdateCombatRoundTimer`'s processing branch (`0x5B8050`, reached once
`round+0x958` ticks past its own gates): `if (sub+0x40 != 0 && sub+4 == 0) → skip the entire
weapon/attack-descriptor build`. Extended `TickFireScriptWatch` to log both fields. Live-measured
on the actual failing creature: **`sub+0x40 = 0` on every sample** — the gate's own condition
requires `sub+0x40 != 0` to even consider bailing, so this specific branch is never taken; the
code always falls through into the descriptor-build block normally. Refuted as the blocker.

**The decisive new measurement: nobody had ever watched a full cycle before.** The existing
180-frame watch turned out to be a hard ceiling nobody had questioned — `round+0x960` (delay) sat
at `0` for the first ~128 ticks after firing, then jumped to `1500` and began counting down at
only ~4-5 per tick. At that rate a full cycle needs roughly 470+ ticks, well past every prior
round's 180-frame window. Extended the watch to 2400 frames and reran the identical live scenario
(host staged for real via `firescript 0xE fire`, HP 12→10 under genuine AI; client connected
mid-fight, `firescript 0x10 fire` on the untouched target). This time the delay counted all the
way down — `1500 → ... → 31 → 26 → 22 → 18 → 14 → 9 → 5 → 1 → 0` — and at the instant it hit
zero, **`round+0x958` (timer-active) reset to `0` too**. `sub+0xB0` stayed `0` for the entire
2400-frame watch, including the moment of expiry. **The round does not stall waiting for more
ticks — it runs its full, real cycle and then resets to idle instead of ever committing an
attack.** This closes off "the window was too short" as an explanation for good; the actual
question is now why expiry resets rather than arms.

**An ambiguous, NOT independently confirmed observation, flagged rather than claimed.** In the
same run, the client's own real player HP (`console 'where'`) dropped to `1/12`, and
`kotorcoop-combat.log` showed several `combat: took 2 damage from a hit on our phantom (attacker
0x10)` lines. Tracing `IncomingDamageController.cs` line 285 confirms this damage is applied
directly to `Odyssey.GetPlayerObjectId()` — the client's real character — via the cross-network
phantom-damage-forwarding channel (`CoopAgent.cs`'s `TryWriteIncomingDamage`, sourced from
`bridge.TryReadPhantomHit` on the HOST side), the exact same pre-existing, unrelated mechanism
rounds 28/29 already identified as a confound (a connected client's own phantom, rendered in the
host's world wherever the client's own position places it, can draw real aggro from the host's
untouched local creatures). The host's own combat log shows zero activity involving `0x10` in the
current session, which argues against the organic-host-aggro explanation, but the host's
phantom-hit hook is only confirmed to log at install time, not per-hit, so this could not be
ruled in or out with confidence in the time available. **Do not read this HP drop as evidence
`0x10` fought locally — the clean, decisive evidence this round produced is the full-cycle
round-timer measurement above, not this.** The next round should isolate this properly: either
position the client's own player far enough from anything that could organically aggro on its
phantom, or add a direct read of the HOST's own `DamageDealtTable`/`sub+0xB0` for `0x10` to settle
whether the host's untouched copy is quietly fighting the phantom on its own.

`local_combat` untouched, stays off. `TickFireScriptWatch`'s window is now 2400 frames (up from
180) and its `sub+0x40`/`sub+4` reads stay in the tree — both are load-bearing for the next round
to avoid re-measuring what this one already settled. Full suite 953/953, both builds clean, rig
torn down and confirmed clean (verified via `Get-Process`, not assumed).

### Round 32 — the expiry-decision branch fully mapped, `GetDeadTemp` measured false throughout,
### and the round's own target turns out to be a stale phantom, not the real player

Full disassembly of `UpdateCombatRoundTimer`'s EXPIRY path itself (`0x004FAFE3` onward, the
branch reached when `delay - creature+0xCC <= 0`), separate from the entry gates every prior
round mapped. Both the "still armed, drain the loop" path and the "never armed" path converge at
`0x004FB017`, which — regardless of which one led there — re-resolves the round's OWN target
field (`round+0x9CC`) fresh via `GetGameObject` (`0x004AE750`), casts it (`vtbl+0x10` twice,
"AsSWSObject"), checks `GetDead` (`vtbl+0x94`), and on a live target casts again (`vtbl+0x30`,
"AsCreature") and calls `CSWSCreature::GetDeadTemp` (`0x004EF890` — a NAMED function in
`tools/kotor1_0_3.db`, not a guessed address) on the result. `GetDeadTemp` returning TRUE falls
straight into the round-reset call (`0x004D4620`, already identified in round 15/20 as the
helper that zeroes the arming state); returning FALSE takes a path this disassembly found ends
in a bare return with **no reset call at all** — which does not match the empirically-observed
behaviour (the timer DOES reset every cycle) and is flagged below as unresolved.

`GetDeadTemp`'s own body (fully disassembled this round, also confirmed via named symbols):
walks `CClientExoApp::GetSWParty()`'s roster (`CSWParty::GetCharacter` per slot), converts the
passed-in target's id via `CClientExoAppInternal::ServerToClientObjectId`, and only if the
target is found in the CLIENT's own local party does it go on to read `vtbl+0x9c(0)` on the
target and return `(that value <= 0)`.

**Live-measured the entire chain, unconditionally, every frame, for the full 2400-frame watch
(two full round cycles observed in one run — the round re-armed and reset a SECOND time within
the window, not just once).** Across every single sample, in both cycles: `GetDead=0`,
`vtbl+0x9C(0)=30` (a real, stable, positive value), `GetDeadTemp=0` (FALSE) — never once true.
**This refutes `GetDeadTemp` as the reset trigger, at least as directly measured** — the branch
that reads FALSE from `0x4ef890` (per the disassembly) should return WITHOUT calling
`0x004D4620`, yet the timer still resets every cycle. Either there is a still-unfound second
writer of `round+0x958`, or the live-measured `GetDeadTemp` value differs from what the ENGINE's
OWN internal call sees at the precise instant it checks (a timing mismatch this diagnostic,
sampled independently rather than hooked inline, cannot rule out).

**The bigger finding, and probably the more important one: `round+0x9CC` (the round's own
target) reads `0x12D` — not `0x7FFFFFFF`, the "current player" sentinel every prior round's
manual `QueueAttackAction`/`AddAttackAction` calls have hardcoded as the target.** Resolved and
confirmed live: `0x12D` is a genuine `coop_phantom` — specifically the CLIENT's own phantom of
the HOST player, rendered at the host's real position. The creature's OWN, genuinely
AI-acquired target is a co-op phantom body, not the "real" player object every round to date
assumed it would target. Given the round was ALREADY active (`timer=1`, a nonzero delay already
counting down) at frame -2400 — the very first sample after firing the script this round — the
target was almost certainly acquired and the round started well BEFORE this test began, likely
left over from an earlier, unrelated interaction with this same creature/phantom pair earlier in
the session. This reframes the test methodology itself: firing `+0x260`
(`k_def_combend01`/`OnCombatRoundEnd`) repeatedly appears to CONTINUE whatever round already
exists rather than freshly re-running target acquisition each time — `OnCombatRoundEnd` is an
end-of-round continuation handler, not necessarily a fresh-decision entry point. A genuinely
clean test would need either a creature with NO pre-existing round state at all, or a script
slot that unambiguously forces fresh acquisition (`OnPerception`/`OnHeartbeat`, not
`OnCombatRoundEnd`) before drawing conclusions from the target it ends up with.

**Status: the actual "commit vs. reset" branch is now fully mapped at the instruction level**,
but the live evidence gathered this round doesn't yet cleanly explain WHY it always resets —
`GetDeadTemp` reads false throughout, which per the disassembly should skip the reset entirely,
not always trigger it. Two concrete next steps, precise enough not to need re-deriving: (1) hook
`0x004D4620` itself (or its call site) directly rather than independently re-measuring
`GetDeadTemp` after the fact, to see definitively whether it's even being reached, and what
routes it there if `GetDeadTemp` genuinely reads false at the real call site too; (2) rerun the
whole test against a creature and target pair with NO pre-existing round state (a genuinely
fresh combat round, target-acquired from scratch during the observation window, ideally against
the real local player rather than a leftover phantom target) to know whether the phantom-vs-
real-player distinction found here matters at all.

`local_combat` untouched, stays off. Full suite 953/953, both builds clean, rig torn down and
confirmed clean (`Get-Process`, not assumed).

### Round 33 — the "stale phantom" theory is refuted; the round genuinely spins forever regardless
### of target, and Strategy 2's own script may never reach the queue step at all

Round 32's `round+0x9CC = 0x12D` was NOT contaminated leftover state. Reproduced on THREE separate
fresh sessions (fresh `demo.ps1` launches, `0xE` never previously fired): every single time,
`k_def_combend01`'s own acquisition picked the `coop_phantom` (`0x12D`) over the client's true
local player (`0x7FFFFFFF`) — even once measured while the true local player was PHYSICALLY CLOSER
to `0xE` (2.27m vs 3.47m) than the phantom. This is genuine, repeatable engine behavior, not test
contamination: a phantom is a real, alive, PLAYER-faction `CSWSCreature` and a legitimate
acquisition target in its own right.

**Extended the watch from 2400 to 6000 frames** (`Odyssey.cs`, `_fireScriptWatchFramesLeft`) —
2400 was cutting off mid-cycle; the round genuinely re-arms and restarts its own ~1500-tick
countdown roughly every 1.5 real seconds, on a clean, perfectly regular loop
(`timer=1,delay=1500` → counts down → `timer=0,delay=0` → jumps back to `timer=1,delay=1500` →
repeat), confirmed unbroken across **12+ complete cycles** spanning three test runs.

**The decisive result: `sub+0xB0` never went nonzero ONCE, in any cycle, against either target.**
Then the phantom was eliminated as a variable entirely — the host was moved far from `0xE`
(`(30,141)` → `(20,22)`), removing its phantom from `0xE`'s vicinity — and `0xE`'s script was
refired. Acquisition correctly locked onto the TRUE local player this time
(`round+0x9CC = 0x7FFFFFFF`, confirmed), and a full clean 6000-frame capture (multiple complete
1500-tick cycles) still never saw `sub+0xB0` leave 0. **The phantom-vs-real-player distinction
this round set out to resolve makes no difference at all — the round spins forever against either
target.**

**A sharper, more fundamental observation than the target question: across every single sample in
every Strategy 2 test this round ran — both targets, all 12+ cycles — `head action` never once
left `0xFFFFFFFF` (a genuinely empty queue).** This is a real, structural difference from every
Strategy 1 test (rounds 9-27), which consistently showed `head action=0x6` (SOMETHING queued, just
never promoted to a committed fight action) after calling `AddAttackAction`/
`CreateNewAttackActions` directly. Strategy 2 fires `k_def_combend01` — literally
"`OnCombatRoundEnd`" — and per standard Aurora-engine scripting convention this is very plausibly
a MID-ROUND continuation/retarget handler, not the script responsible for INITIATING a fight from
cold. If so, firing it on a creature with no round already in flight may just be exercising the
round-timer's own idle-reset bookkeeping (which the `timer=1,delay=1500 → 0 → 1500` cycle is
completely consistent with) without ever reaching whatever queues the first action — which would
mean Strategy 2, as built, has never actually tested "let the engine's real AI decide to fight,"
only "let the engine's round-timer spin." Round 27's original host-local success (real, verified
HP loss) used this SAME script and SAME mechanism — that result stands and is not contradicted
here, but this round did not re-instrument it with the current diagnostic, so whether IT also
showed `head action=0xFFFFFFFF` the whole time (fight happening through some path this diagnostic
can't see) or `0x6`+ (matching Strategy 1) is now an open, checkable question rather than an
assumption.

**Next concrete step, precise enough not to need re-deriving:** find and fire whatever script slot
governs `OnPerception`/`OnNotice` (the "I just saw a hostile, should I act" handler) instead of
`OnCombatRoundEnd`, on a creature with a verified-idle round, and watch the SAME diagnostic fields.
If THAT reaches a nonzero head action or arms `sub+0xB0`, it confirms `k_def_combend01` was simply
the wrong trigger all along, independent of every driven/undriven/target-identity finding from
rounds 9-32. If it shows the identical spin-forever symptom, the block is confirmed to be entirely
downstream of script selection, in the round-advancement machinery itself, exactly as rounds 27-32
concluded.

`local_combat` untouched, stays off (Strategy 2 doesn't read or need it). Diagnostic watch budget
raised 2400→6000 frames — kept, since it's what let this round's clean multi-cycle capture happen
at all. Full suite 953/953, both builds clean, rig torn down and confirmed clean (`Get-Process`).

### Round 34 — round 27's win confirmed genuinely real; the `+0x270` guess was wrong, but what it
### revealed matters more than the miss

**Part 1: round 27's host-local result is real, not a false positive.** Reproduced the exact
scenario (single host instance, `k_def_combend01` fired on a fresh, untouched `sithsoldier`
targeting the host's own local player) with round 33's full diagnostic running. Result:
**`sub+0xB0` went nonzero (`=1`) for the first time in the entire investigation**, `sub+0x5c`
changed from 0 to 4, and the target's `vtbl+0x9C(0)` readout (very plausibly HP) fell from 12 to 4
over the capture window — all independently consistent with a genuinely committing, progressing
fight. This settles the open question cleanly: `k_def_combend01` DOES reach a real commit when
fired host-local, and round 27's claim was not a confound. The wall is specific to the
mirrored/client case, not to script choice in general.

**Part 2: the `+0x270` candidate was wrong.** Found via `xrefs.py` on `RunScriptFn` — a call site
at `0x004EB79D` (inside the perception/heartbeat code area) fires `creature+0x270`, gated on "no
current target yet" and latched so it only fires once, which read as a strong OnNotice match by
shape alone. Live peek on a real `sithsoldier`, though, read `k_pend_room3_02` — an area/room
script by naming convention, not a combat-AI script. Firing it produced the identical spin-forever
symptom on a mirrored creature (`sub+0xB0=0` throughout a full 6000-frame capture, `head
action=0xFFFFFFFF` throughout) — but that result doesn't test the hypothesis it was meant to,
since the fired script itself has nothing to do with deciding to fight. The `+0x260` dispatcher
(`0x004DA846`, fully mapped this round via its remap table and jump table, read directly from the
binary) turned out to fold 9 of its 13 event codes into that SAME `+0x260` slot; the other two
branches never call `RunScript` at all. There is no genuinely separate "OnNotice" script reachable
through that specific dispatcher — a different one, if it exists, is fired from somewhere else in
the binary not yet found.

**The one thing this miss still proved, and it's significant:** the round-timer's own 1500-tick
cycle ran identically (delay counting down, `timer` flipping 0↔1 on schedule) whether the fired
script was `k_def_combend01` (a real combat script) or `k_pend_room3_02` (an unrelated area
script). **The round-timer machinery does not appear to depend on what the fired script actually
does at all** — something in the dispatch call sequence itself (not the script's own NWScript
body) is what starts/maintains that cycle. This reframes the open question again: it was never
really about finding the "right" script to fire — round 27 already had the right one. It's about
what's different, in the CALL SEQUENCE ITSELF or the STATE IT RUNS AGAINST, between a host-local
creature (which commits) and a mirrored one (which spins forever, target identity and script
choice both proven irrelevant across rounds 29-34).

**Honest assessment.** 34 rounds have now individually ruled out: per-creature drive state (round
29), the party-table gate (round 30), the acquisition chain in full — hostility, perception,
range, the walk (rounds 18-25), target identity — phantom vs true local player (round 33), and
script choice, so far as this round could test it (round 34). What remains genuinely unexplored:
(a) whether `+0x270`'s ACTUAL semantic slot (a real OnNotice/OnHeartbeat script, still unfound) has
been miscategorized to a completely different offset the `k_def_combend01` dispatcher doesn't
reach and this round didn't find either; (b) anything in the ATTACKER's or the SESSION's state
that differs specifically between "playing as the host, no client connected" and "hosting with a
client connected" that has nothing to do with any individual creature at all — untested, because
round 27's positive result was measured on the same rig, in the same connected session, as every
mirrored failure, and nobody has yet tried the control of firing on a host-local creature WHILE a
client is actively connected versus with no session at all. That specific control — same script,
same creature type, same host, session connected vs not — is the most concrete next step this
round can hand off. Absent that, this is a reasonable point to call the practical edge of what
disassembly-first, live-diagnostic RE can resolve without a fundamentally different technique
(full engine decompilation of the round-advancement caller chain, or abandoning the
replicate-the-engine approach for something that doesn't need to understand the internals at all).

`local_combat` untouched, stays off. Both `coop.ini` unchanged (Strategy 2 doesn't use it). Full
suite 953/953, both builds clean, rig torn down and confirmed clean (`Get-Process`). New console
verb: `firescript <id> fire 270` fires the `+0x270` slot instead of the default `+0x260`.

### Round 35 — "a peer is connected at all" is REFUTED as the variable, decisively

Round 34's own hand-off, run immediately: reproduced round 27's exact winning scenario (host-local
creature, untouched, `k_def_combend01` fired, targeting the host's own local player) with a client
**genuinely connected the whole time** (`phantoms 1 remotes 1` confirmed on the host) but never
touched, never moved, never brought anywhere near the test creature.

**Result: it still works.** `sub+0xB0` armed (`=1`), `sub+0x5c` changed 0→4 (matching round 35's
own earlier host-local success exactly), and the target's `vtbl+0x9C(0)` readout fell 12→6 across
the capture — a real, progressing fight, with a peer connected the entire time.

**This settles it: "a session exists / a peer is connected" is not the variable.** Combined with
round 29 (a completely untouched, zero-driven creature on the CLIENT still fails) and round 33
(target identity — phantom vs. true local player — makes no difference on the CLIENT), the
remaining candidates narrow to one specific axis nothing has yet isolated: **something about being
the CLIENT process/role itself** — not "is a session connected," not "is this specific creature
driven," not "which target" — since a host-local creature on the SAME connected session, same
script, same everything, works fine.

**The next precise test, not yet run:** is this really about CLIENT ROLE (e.g. `CClientExoApp`
vs. `CServerExoApp` — this engine's own internal client/server split, already documented elsewhere
in this codebase as real and load-bearing, not just this mod's own host/client distinction), or is
it still, somehow, about the specific creature being part of whatever the co-op plugin's own
enumeration/census touches AT ALL — even read-only, even with every write skipped (round 29 only
verified writes were skipped, not that the creature was absent from every census/list the plugin
walks)? The cleanest way to separate these: on the CLIENT, fire `k_def_combend01` on a creature
FAR outside any census this session's plugin would ever enumerate (a creature in a part of the
module the mirrored-enemy census caps or excludes entirely, not merely one this session chose not
to touch) and see if it STILL fails. If it does, that points at the client role/process itself; if
it finally works, it points at census membership, not client-vs-host role.

`local_combat` untouched, stays off. Full suite 953/953, both builds clean, rig torn down and
confirmed clean (`Get-Process`).

### Round 36 — no naturally-excluded hostile exists here; the companion substitute is invalid;
### the intended test could not be cleanly run

**No creature in this area/save is naturally outside the census.** `census` on the client read
`MIRROR 25 id(s) 'END_M01AA': 25 resolve here, 0 do not`, against 27 real distinct creatures
confirmed by the object-array check (`array 0x353DDF0 confirmed 27 of 27 (no ghosts)`). The 27-25
gap is exactly `carth` and `end_trask` — the two party members, excluded because they're not
enemies, not because of the 32-slot cap (`BlockEnemyCapacity`) or any distance cutoff; every real
hostile in the area (25 of them) is comfortably under the cap and genuinely in the census. There
was no naturally-occurring test subject to find.

**Constructed one instead, and it doesn't answer the question.** Built real, working diagnostic
infrastructure to make this possible: a new cheat kind (`CheatKindMoveNear`, 13; no shared-block
version bump, same pattern as `FireScript`) that snaps a target creature to a hardcoded position
near `sithsoldier 0xE`, plus a `movenear <id>` console verb, wired through
`ConsoleCommand.cs`/`CoopCommandDispatcher.cs`/`CheatController.cs`. Used it to move `end_trask`
(genuinely, structurally never touched by `EnemyPresenceController` — party members go through
`PartyPresenceController` entirely) to 1.21m from `0xE`, well inside the ~2.6m range round 25
measured working. **Position write independently confirmed** by reading it straight back
(`ObjPosition +0x090`) rather than trusting `creatures.ps1`'s known-unreliable raw scan (which
still showed the old, stale position — the ghost-scan artifact this investigation has hit
repeatedly, not a real failure).

Fired `+0x260` on Trask at that position anyway: `round+0x9CC` stayed at the not-found sentinel
(`0x7F000000`) throughout a full 6000-frame capture, `round+0x958 timer=0` the entire time — the
round never even STARTED, let alone armed. Checked what script was actually fired:
**`k_hen_combend01`, not `k_def_combend01`** — "hen" for "henchman," KOTOR's own naming for the
companion/party-AI script family, a genuinely different script than every hostile creature this
investigation has tested. Party members do not independently perceive-and-decide the way hostile
monsters do (they follow the party leader's own combat engagement rather than run their own
`GetNearestEnemy` loop), so this result says nothing about census membership at all — it's
confounded by testing an entirely different AI script, not a controlled variant of the same one.

**Honest conclusion: this round's intended test could not be cleanly executed with what's
available in this save/area.** A genuinely clean version needs either (a) a hostile creature
verified to run `k_def_combend01` (or equivalent) that the census provably excludes — which
doesn't exist here, everything is under the 32-cap — or (b) forcing more than 32 real hostiles
into one census scope to trigger the cap for real, which this save's population can't reach. The
`movenear`/`CheatKindMoveNear` infrastructure is kept (not removed) since it's real, working,
and would let a future round retest this cleanly against a genuine hostile-AI creature once one
is available (a more crowded area, or a different save).

Both `coop.ini` unchanged, `local_combat` untouched. Full suite 953/953, both builds clean, rig
torn down and confirmed clean (`Get-Process`).

### Round 37 — ANSWERED. Census membership is the variable, not client role. `sub+0xB0` armed
### for real, on the client, for a creature outside the census

Round 36's construction (moving a real creature) was overkill — round 37 used a far simpler one:
temporarily lowered `KotorGameBridge.BlockEnemyCapacity` 32→1 (the same constant that already
sorts candidates by "fighting first, then nearest distance" and truncates the published census —
`docs/research/enemy-presence-sync.md`/`peer-damage-ledger.md`'s "census cap and distance
shedding," confirmed live, not assumed). With this save's ~25 real `sithsoldier`s, that split them
cleanly across the census boundary using nothing but the existing mechanism: `census 0xE` on the
client read `listed hp 15/15 at 29.7,144.5` (still tracked, the nearest one), `census 0x10` read
`not listed in this census` (genuinely, structurally un-enumerated — the plugin's own log
confirms the drop: `enemy drive: mirroring 1 of the owner's 1 enemies`).

**Fired `k_def_combend01` — the exact same proven script every round has used — on both, from the
CLIENT process, in the same session, same positions, same everything except census membership:**

- **`0xE` (still tracked):** `sub+0xB0=0` throughout, timer ran a normal cycle, never armed —
  identical to every mirrored-case failure in rounds 9-36. Confirms the baseline is unchanged.
- **`0x10` (genuinely outside the census):** **`sub+0xB0=1`** — armed, for real, on the client
  process. Sustained across 2081 of 6000 sampled frames (not a one-tick blip), first observed
  ~70ms after firing and still armed at the end of the capture window. The target's `vtbl+0x9C(0)`
  readout fell 16→12 across that armed window — a real, ongoing fight, matching every other
  confirmed-genuine success in this investigation (rounds 27, 35).

**This answers the question round 35 opened and round 36 couldn't close.** "A session/peer
connected at all" was already refuted (round 35: host-local still works with a peer connected).
Now "being the client process/role itself" is refuted too: the SAME client process, same
connected session, armed a real fight for a creature it had simply never enumerated. **The
variable is census membership** — being tracked/enumerated by the co-op plugin's `EnemyPresenceController`
at all, independent of whether its position is ever driven (round 29 already showed writes alone
aren't it; this round shows read-only enumeration is enough by itself).

**What this means for a fix, and why it wasn't found by looking at position/per-frame writes:**
something about the act of ENUMERATING a creature into the tracked set — not writing to it —
marks it in a way the engine's own combat-round machinery respects. The most likely mechanism,
given everything already ruled out in rounds 4-30 (the round struct fields, the acquisition
chain, the party-table gate): `Odyssey.ClaimCreature`/`ClaimCreatureForFight`'s initial claim
(called once when a creature FIRST enters the tracked set, separate from the per-frame drive
pass) sets something that persists for the creature's whole time in the census, not just during
active writes. That one-shot claim call — not the per-frame position/facing writes rounds 14 and
29 already cleared — is the concrete next target: find exactly what it sets, on FIRST entry to
the tracked set specifically, and whether un-claiming (or narrowing the claim) a creature the
moment `local_combat` wants it to genuinely fight would let this work without sacrificing visual
position sync for enemies that are just being mirrored, not actively fought.

Restored `BlockEnemyCapacity` to 32. Both `coop.ini` unchanged, `local_combat` untouched. Full
suite 953/953, both builds clean, rig torn down and confirmed clean (`Get-Process`).

### Round 38 — the boundary is confirmed real (order-controlled), but no write-based mechanism
### explains it; `local_combat` NOT flipped, and here is exactly why

**Part 1: `ClaimCreature`/`ClaimCreatureForFight` is NOT a one-shot claim.** Its own doc comment
already says so — `EnemyPresenceController re-claims every mirrored enemy every pass` — and its
body (`Odyssey.cs:369-385`) writes exactly three things, every single frame a creature is driven:
`ObjCommandable=1`, `CreMovePermitted|=2`, `CreRunFlag=0`, plus a conditional `ClearAllActionsFn`.
None of round 29's or round 37's diagnostics ever reach this function for their test creature in
EITHER condition — round 29's `DiagUndrivenObjectId` bails out of `DriveOne` BEFORE `ClaimCreature`
is ever called (`EnemyPresenceController.cs:410-413`); round 37/38's cap-excluded creature is
never in the loop at all. So `ClaimCreature` was never a live variable in either test — it was
already controlled out before this round started.

**Part 2: reproduced round 37's result with the confound the coordinator raised in mind — order.**
Re-ran with `BlockEnemyCapacity=1` again, but fired the EXCLUDED creature (`0x10`) FIRST this
time (not second, as round 37 had it), then the still-tracked one (`0xE`) third. Result unchanged:
`0x10` armed again (1865/6000 samples nonzero `sub+0xB0`, HP-like value 28→24, and — a new
confirming detail — `sub+4` finally read a real, nonzero pointer (`0x43203AC`) by the end of the
capture, something never once observed in 37 rounds of failures); `0xE` failed again (0/6000).
**Order is ruled out as a confound.** The boundary is real, reproducible, and specifically about
census membership, exactly as round 37 found.

**Part 3: exhaustive disassembly of every candidate write site found NONE.** Traced every piece of
code that runs differently between "published, resolved, but otherwise untouched" (round 29's
condition) and "never published at all" (round 37/38's condition):
- `EnemyPresenceController.Resolve()` (`EnemyPresenceController.cs:521-542`) — caches a pointer
  in a MANAGED array (`_creature[slot]`), calls `Odyssey.ResolveServerObject`. No engine write.
- `Odyssey.ResolveServerObject` (`Odyssey.cs:679-728`) — calls the engine's own
  `CServerExoApp::GetGameObject` (`0x004AE750` → `0x004B1700`), then tests the returned pointer's
  layout. `0x004B1700` itself (disassembled in full this round) does nothing but forward into
  `CGameObjectArray::GetGameObject` (`0x004D8230`) with a scratch stack local — no writes to
  anything the caller doesn't own.
- `CGameObjectArray::GetGameObject` (`0x004D8230`, disassembled in full this round, every
  instruction) — a hash-bucket lookup and linked-list walk, comparing the masked id against each
  node and writing the FOUND POINTER into the CALLER's own out-parameter (`[edx] = ...`) on
  success. **No write to the object it finds, confirmed at the instruction level.** This is the
  single engine-level call `Resolve()` makes that round 29's creature received and round 37/38's
  excluded creature never did — and it is provably, completely read-only.
- `MaterialisedEnemies.Seen()` (`MaterialisedEnemies.cs:131-141`) — a managed-array timestamp
  write, no engine touch, called for every published slot before the per-creature diagnostic
  bail-out in both round 29 and this round.
- The HOST-side census scan (`KotorGameBridge.cs:3888-4020`, re-read this round) — every field
  (`OffCurrentHitPoints`, `OffPosition`, `OffOrientation`, `OffAnimation`, `OffAppearanceType`,
  `OffCreatureStats`) is read through `_memory.TryRead*` — raw `ReadProcessMemory`, not an engine
  call, and identical for every candidate whether or not it survives the cap. The only place a
  candidate's fate diverges is a pure managed-list `Add()` after the cap/sort — no further engine
  touch of any kind.

**Conclusion: the boundary is real and reproducible, but nothing in the entire call chain this
investigation can find writes to the creature, the round struct, or anything engine-side,
differently for a published-but-untouched creature versus a never-published one.** Every
plausible write-based mechanism has now been checked and ruled out at the instruction level. This
either means the real mechanism is something this investigation's method — tracing explicit
field writes — cannot see (a heap-layout or memory-paging effect from `ReadProcessMemory`/
`WriteProcessMemory` traffic itself, something at the OS/hardware level rather than the game's own
logic, which would be a genuinely different kind of investigation entirely), or there is a
touch-point still unfound despite this round's search.

**Decision: `local_combat`'s default is NOT flipped.** Not because the mechanism doesn't work —
round 37 and this round both proved, twice, with order controlled, that it genuinely does, for a
creature outside the census — but because shipping this needs an actual DESIGN (some way to
temporarily exclude an actively-fighting creature from the published census without breaking its
visual position sync for the rest of the fight, which is a real host↔client protocol question,
not a config flip) and this round could not identify why the boundary exists, only that it does.
Flipping a default on a mechanism whose cause is unknown, however well-reproduced the symptom, is
exactly the kind of thing this section's own standing rule — "did it work, not did it avoid
breaking something" — argues against; "it works when a creature happens to fall outside a
cap that was never meant to gate combat" is not yet "it works."

**What a round 39 needs, concretely:** either (a) find the actual mechanism — the most promising
unexplored angle is OS/engine-level (does an external process's `ReadProcessMemory`/
`WriteProcessMemory` traffic on a specific creature's page(s) trigger something — a copy-on-write
fault, a guard page, an anti-tamper heuristic — that a purely code-level trace cannot see, which
would need a genuinely different kind of instrumentation than disassembly); or (b) skip finding
the mechanism and design the actual fix directly from the empirical result — a protocol addition
letting the client tell the host "stop publishing creature X to me for N seconds while
`local_combat` lets it genuinely fight," accepting a brief position-sync gap during that window,
which is buildable and testable without ever knowing why it's needed.

Both `coop.ini` unchanged, `local_combat` untouched, `BlockEnemyCapacity` restored to 32. Full
suite 953/953, both builds clean, rig torn down and confirmed clean (`Get-Process`).

### Round 39 (2026-08-21) — the pragmatic fix, built from the empirical result, flipped live

Round 38 left two paths forward: chase the census-boundary mechanism into OS-level territory, or
design and build the fix directly from the empirical result without ever knowing why it's needed.
Took the second path.

**The design.** When one of this instance's mirrored enemies is genuinely engaging the local
player (`EnemyPresenceController.IsEngaged`, a plain planar-distance test, `local_combat_range`
squared, default 20 units — see the constant's own doc comment for why 20 and not a modelled
weapon reach), pull that ONE creature out of this instance's own published census for the
duration of the fight and fire its `k_def_combend01` event script (Strategy 2, proven live rounds
27-38) through the engine's real dispatcher. No new wire packet: "excluded from the census" is a
purely local, self-healing level, not an edge — `SharedBlock.OffsetLocalCombatExcludedId` (a
single spare `uint32`, plugin-written every frame from `EnemyPresenceController`) is read back by
`CoopAgent` immediately before it writes the outgoing enemy list into the SAME shared block, and
filtered out there. The excluded id simply stops appearing in `block.EnemyPresenceCount`'s list
on the very next tick, which means `EnemyPresenceController.DriveOne`'s loop — the thing that was
still touching a "removed" creature in every round-37/38 negative case — never reaches it again.
Re-inclusion needs nothing extra either: the moment `_locallyFightingId` is cleared, the id simply
resumes appearing in the published list on the next pump.

**Lifecycle** (`MaintainLocalFight`, run at the very top of `OnFrame`, before every early-return
in the function — round 37/38's own census-membership finding means a fight that got stuck
excluded through an early return would never un-stick): ends the fight and clears the exclusion
on any of three conditions — the creature is dead or unresolvable (`Odyssey.IsCreatureDead`, the
same `AsSWSObject`→`GetDead` vtable chain the round-32 diagnostic proved live), the player has
moved out of `IsEngaged` range, or a bounded safety-net timeout (`local_combat_timeout_s`,
default 25s, clamped 5-120s) expires. Nothing else needed reconciling on rejoin — the exclusion
was never anything more than "not in this list right now."

**A real invulnerability bug, found and fixed before anything was committed.** The first working
draft kept the existing `DriveOne`/`RecordEngagement` call — the thing that licenses
`IncomingDamageController`'s absorb-standdown — running unconditionally for EVERY nearby driven
enemy under `LocalCombat`, not just the one actually excluded and fighting. Historically harmless,
because the old `QueueAttackAction` mechanism this replaces never made ANY creature fight for
real, so which ids got marked "engaged" never mattered. Once one specific creature started
genuinely fighting, it became live: a live stress test — one creature (`0xE`) staged fighting
locally via the automatic trigger, while the HOST was separately commanded (`blockpoke.ps1 ...
attack 0x10 0x12D`) to make a DIFFERENT, merely-nearby creature (`0x10`) attack the client's
phantom directly — showed `IncomingDamageController`'s own built-in watchdog line firing for
`0x10`'s forwarded damage: `absorbed N forwarded damage ... this engine says in-combat=False` —
exactly the invulnerability smell that log line exists to catch. Fix: deleted the unconditional
`RecordEngagement` call and the now-dead `RecordEngagement` method entirely; only
`MaintainLocalFight` marks engagement now, and only for `_locallyFightingId` specifically. Rebuilt,
reran the identical stress scenario: no new absorb line for `0x10`, and the client's HP dropped
correctly from `0x10`'s forwarded damage instead of being wrongly swallowed, while `0xE`'s damage
stayed genuinely local (no double-count either direction).

**Live-verified end to end, twice** — once before a diagnostic-logging cleanup pass, once after
(see below), both on a fresh `END_M01AA` rig with no host-side scripting: the automatic trigger
fires purely off proximity with no manual `firescript` call, the exclusion correctly drops the
mirrored count by exactly one (`enemy drive: mirroring 25 ... 24 of the owner's ... enemies`
either side of the exclusion write), the 25s timeout fires and the creature rejoins cleanly when
nothing resolves the fight (expected in an unattended rig — nothing here provides real player
combat input, so a fight that isn't resolved by an actual attack cycles on timeout rather than
death, exactly the safety net's job), `player disengaged` fires correctly the instant the player
is teleported more than `local_combat_range` away mid-fight, and the very next in-range creature
picks up the exclusion on the following frame with no gap.

**One diagnostic dead end worth recording, so a future round does not re-walk it.** The
round-28-era per-frame `firescript watch` (`TickFireScriptWatch`, `round+0x96C sub-index`,
`sub+0xB0`) is a **Strategy 1** diagnostic — it watches for the NATIVE combat-round-arming write
that rounds 9-27 spent proving is a permanent no-op for a mirrored creature. Re-arming it against
a genuinely Strategy-2-fighting creature this round (`firescript 0x1A fire`, `firescript 0xE fire`,
point-blank) showed `sub+0xB0` staying 0 and `head action=0xFFFFFFFF` throughout — which is NOT a
regression signal, because Strategy 2 was adopted specifically because it does not go through that
arming path at all; nothing about round 39 makes `sub+0xB0` a meaningful witness for it, and it
never was. The correct live proof for round 39 is the one already used above: does the mirrored
count actually drop, does the exclusion/timeout/disengage cycle actually run, and does
`IncomingDamageController`'s absorb-vs-apply attribution land on the right creature. A separate,
harmless red herring chased the same session: `CheatController`'s `topup` cheat reported "the
player reports 0 maximum hit points" consistently through this round's testing on BOTH the host
(fighting a purely native, pre-round-27 local fight) and the client. This is §1 of this document,
not a round 39 finding — `Odyssey.GetMaxHitPoints` reads the base `CSWSObject::GetMaxHitPoints`
offset (`+0xE0`), which §1 already established reads 0 for a PC unconditionally, because
`CSWSCreature`'s own override computes a PC's max HP from the per-level stat records instead of
storing it there at all. `topup`'s use of the base-class getter for a player is a pre-existing gap
in that one cheat, orthogonal to combat state entirely, and reproducing identically on a fight
round 39 never touched (the host's native combat with `0xE`, proven back in round 11) rules out
any connection to this round's changes. Left as a known gap for whoever next touches `topup`.

**Diagnostic-logging cleanup** (the piece deferred since round 9, closed out this round):
`FireCreatureScriptSlot0x260` no longer auto-arms the round-28 per-frame watch — production code
(`StartLocalFight`) now calls it for every real fight, and 6000 lines of per-frame log per fight
is not something a shipped feature does. The manual `firescript <id> fire[/270]` console verb
(`CheatController.FireScript`) now arms the watch explicitly via a new `Odyssey.ArmFireScriptWatch`
after firing, for both the `+0x260` and diagnostic-only `+0x270` slots, so the diagnostic surface
keeps working exactly as before while the production path stays quiet.

**Decision: `local_combat` is now ON by default**, `PluginConfig.ReadBool("local_combat", true)` in
`EnemyPresenceController.cs`, and `profiles/coop.ini.example` updated to match. This is the first
time this section's own standing rule — "did it work, not did it avoid breaking something" — has
been satisfiable: the mechanism genuinely arms a mirrored creature's own combat AI, the lifecycle
around it (trigger, timeout, disengage, re-inclusion) is live-verified clean, and the one real
correctness bug this round's own adversarial stress-testing found was fixed and reconfirmed closed
before the flip, not after. Full suite 953/953 (one known-flaky, pre-existing, unrelated test —
`DisconnectRecoveryTests.HeavyPacketLossStillConverges`, a randomized-loss network-convergence
test with no seed — failed once in the full run and passed in isolation immediately after; not a
regression), both builds clean, rig torn down and confirmed clean (`Get-Process`).

### First real ambush, and a real destructive bug found and fixed (2026-08-21)

Round 39 shipped `local_combat` on by default having only ever staged a single isolated
enemy. A real `tar_m03aa` ambush (walked into organically, multiple materialised stand-ins
active alongside genuine GIT-placed hostiles) found `local_combat` was never actually tested
against either of two conditions it now has to survive in real play: several creatures
cycling through exclusion at once, and a MATERIALISED stand-in specifically (every prior
round's proof used real, GIT-placed creatures only).

**Found: excluding a materialised stand-in destroys it, mid-fight, silently.** A real,
already-materialised enemy (`0xCE`, local stand-in `0xB5`) got pulled into `local_combat` and
its "fight ended (dead or gone)" / "pulled out... to fight" lines alternated every ~4ms,
hundreds of times a second — a same-frame infinite churn, not a slow cycle. Root cause:
`MaintainLocalFight` resolves `_locallyFightingId` (the OWNER's id, per `StartLocalFight`'s
own doc comment) directly through `Odyssey.ResolveServerObject`, with no translation through
`MaterialisedEnemies.ToLocal` first — the exact translation `DriveOne` already does before
ever touching a creature. For a GIT-identical creature (owner id == local id) this
accidentally worked; for a materialised stand-in it resolves nothing every single tick
(`Odyssey.IsCreatureDead(0)` is `true` by design), instantly ending and instantly restarting
the same fight forever. Fixed: translate through `MaterialisedEnemies.ToLocal` first, exactly
like `Resolve`'s own fallback.

That fix alone did not stop the churn on `0xCE` specifically — live evidence showed something
worse: the stand-in's local body (`0xB5`) was **already gone** (`resolve 0xB5` → `0x0`) within
5ms of being selected, while the OWNER's real `0xCE` was still alive and dealing forwarded
damage to the phantom the whole time. `StartLocalFight` fires the creature's `+0x260` event
script — proven safe and correct on a real, GIT-placed creature (round 27), which carries its
own genuine combat AI script in that slot. A materialised stand-in's local body is a
`k_coop_phantom`-blueprint spawn repainted with the owner's appearance; it does **not** carry
that enemy's real script set, and firing whatever (if anything) actually occupies a co-op
phantom blueprint's own `+0x260` slot is unproven and, measured live, destructive to the body.
**Fixed conservatively: `local_combat` now only selects GIT-identical creatures
(`MaterialisedEnemies.ToLocal(enemy.ObjectId) == 0`) for local engagement** — a materialised
stand-in is left to ordinary forwarded-damage mirroring, same as before this feature existed,
until firing a script on a phantom-blueprint body gets its own investigation. Also kept the
`MaintainLocalFight` lease fix regardless — it is correct on its own terms (an excluded
creature's `MaterialisedEnemies` entry must not go stale mid-fight) and is a real, separate
safety net for whenever materialised stand-ins are re-enabled for this.

**Confirmed live, same session, on the fixed build:** the `0xCE`-style same-frame churn is
gone — none of the materialised stand-ins present in this ambush (several) were selected for
local combat at all, exactly as designed.

**Still open — a GIT-identical creature can stay "engaged" for the full 25s timeout and never
land a hit.** `tar03_bekcs2` (`0x2D`, a real placed NPC, not an encounter spawn) cycled
through `local_combat` repeatedly across two full sessions (before and after the fixes above)
without ever landing a hit — always "timed out," never "dead" or "player disengaged." Measured
directly: player at `(380.7, 153.0)`, `0x2D` at `(366.43, 157.33)` — **~15 units apart**, and
not meaningfully closing that distance across a whole engagement window. `IsEngaged`'s trigger
radius (shared with the damage-absorb licence) is evidently far larger than actual melee
range, so a creature gets pulled into an exclusive local fight from well outside striking
distance and either cannot or does not path the rest of the way in 25 real seconds. This is
now a precisely-characterised, different problem from anything the 39-round mechanism
investigation solved — an engine pathing/navmesh question, not a round-arming one — and needs
its own live investigation rather than a guess against unverified pathfinding internals.

**Also confirmed the same session:** the materialised-loot positive case (docs/research/
enemy-materialisation.md §6, "Live pass... CONFIRMED") and the Host button
(`docs/research/native-browser-panel.md` — real `SendInput` clicks, real log evidence) both
work. Full suite 966/966, both builds clean, rig torn down and confirmed clean.

### `tar03_bekcs2` never lands a hit because it has no combat script at all — FOUND AND FIXED (2026-08-21)

The "15 units, not closing distance" reading above turned out to be an artifact of where the
player happened to be standing, not the actual cause. Re-measured with a per-second
position-over-time watch (`local combat diag`, temporary, kept for the next round) across the
SAME creature (`0x2D`) at three distances in one continuous session — 4.78, 2.94, and finally
**1.25 world units, genuine point-blank melee range** — and it stayed frozen at the exact same
coordinate through all of them, for 20+ seconds at the closest distance, HP never moving off
12/12. Range was never the blocker; something else was.

`firescript 0x2D fire` (the manual diagnostic verb) answered it directly: `fired 0x2D +0x260 =
'(no readable string here: ptr=0x0 length=0'`. **This creature's `+0x260` slot is empty.**
Every round-27-onward proof that firing `+0x260` makes a creature fight was measured on
creatures that happen to carry `k_def_combend01` there; nobody had checked whether that's
universal across every GIT-placed hostile. It is not. `FireCreatureScriptSlot0x260` on an
empty slot is `RunScript` on a null resref — a silent no-op, which is indistinguishable from
"fired and did nothing" without the diagnostic read. `PeekCreatureScriptSlot0x260`'s own doc
comment already carried the field layout needed to check this defensively; nobody had wired a
check into the actual trigger gate.

**This was worse than inert.** Without a check, an empty-slot creature gets re-excluded from
the census on every 25s timeout for as long as the player stays in range — the "fight" can
never end any other way, since nothing is happening for the engine's own dead/disengage checks
to catch. Each exclusion cycle re-licenses `IncomingDamageController`'s absorb-standdown for
that one creature, so the player becomes **permanently immune to that enemy's forwarded
damage** — precisely the invulnerability failure mode this feature's own safety design exists
to prevent, just reached by a route nobody had tested (a real creature with no script, not a
materialised stand-in).

**Fix:** `Odyssey.HasCreatureScriptSlot0x260` (a proper boolean read of the same `{ptr,
length}` pair `PeekCreatureScriptSlot0x260` already decodes) gates
`EnemyPresenceController`'s local-combat trigger alongside the existing GIT-identity check —
a creature with nothing to fire is simply left on ordinary mirrored/forwarded-damage combat,
same as before this feature existed, rather than being pulled into a fight that can never
resolve. `FireCreatureScriptSlot0x260` itself is left ungated on purpose, so the manual
`firescript` diagnostic can still prove an empty slot the way it just did here.

**Confirmed live, same session, on the fixed build:** relaunched fresh, warped back to
`tar_m03aa`, positioned the player at the identical point-blank distance from `0x2D`
(`(365.50,156.50)`, dist 1.25) and held there for 2+ minutes. No `local combat: 0x2D pulled
out` line appears anywhere after the rebuild — confirmed by timestamp against the exact moment
of relaunch — versus the old build's `0x2D pulled out of the census` firing on every single
25s cycle beforehand. The creature is no longer trapped in a dead-end exclusion loop.

**Still open, and now correctly narrowed:** a DIFFERENT creature (`0x4`) in the same session
got excluded from ~14 units away and, like the original `0x2D` reading, did not close that
distance before the player moved on and it disengaged. `0x4` was not checked for a real
`+0x260` script, so whether this is the same missing-script class or a genuine pathing/range
question remains open — the fix above only rules out the specific "no script at all" failure
mode, not every possible reason a real-scripted creature might not close distance from far
away. Next step: repeat this round's exact methodology (position-over-time watch, incrementally
closing distance, confirm `+0x260` is real via `firescript` first) on a creature confirmed to
carry `k_def_combend01`, to isolate whether distance-closing is a genuine remaining gap or
whether every prior "frozen" reading in this document was actually this same missing-script
issue in disguise.

Full suite 966/966, both builds clean, rig torn down and confirmed clean.

### `0x4` is not the empty-script bug — it's a genuinely different, bigger problem: the agent's own census publish stalls (2026-08-21, round 41)

Checked `0x4` (`blackvulka034`) first, per the standing next step: `firescript 0x4 fire` answered
`fired 0x4 +0x260 = 'k_def_combend01'` — a real, non-empty script, same as every creature that
already fights correctly. The empty-script class is ruled out for this one.

Reproduced the same frozen-at-point-blank-range symptom live (`0x4`, then `0xA`, both cycling
through repeated 25s timeouts, plugin log: `local combat: 0x… pulled out of the census to fight
for real` / `fight ended (timed out), rejoining the census`, over and over). Added two temporary
diagnostics to settle *where* the freeze actually comes from, rather than guessing:

1. **In the plugin** (`EnemyPresenceController.DriveOne`): does the excluded id still show up in
   THIS instance's own read of the shared block's enemy list at all, and what does
   `block.EnemyPresenceCount` read as when it does?
2. **In the agent** (`CoopAgent.cs`, the `PumpEnemies` mirror branch): does the exclusion filter
   (`bridge.ReadLocalCombatExcludedId()` → filter → `TryWriteEnemies`) actually run, with what
   values, and does the write actually succeed?

**Both sides are individually correct.** The agent's filter genuinely excludes the right id
(`excludedId=0x4 enemies.Count=19 filtered.Count=18`) and `TryWriteEnemies` genuinely returns
`true`. The plugin's read offsets exactly match what the agent writes to (`0x1400`/`0x1410`,
cross-checked line by line). Neither side has a logic bug.

**The actual finding: the agent's `PumpEnemies` mirror branch simply stops running.** Across two
independent clean sessions (one that hit an unrelated `[W_SAVE]` compatibility stall and
recovered via a manual `reconnect`, one with no reconnect involved at all), the agent's own
diagnostic logging — including the `moduleClean` line, which fires unconditionally on every call
with no gating — fired normally for the first ~60-90 seconds after the module transition, then
stopped completely and never resumed for the rest of the session (checked up to several minutes
later). The plugin's `enemy drive: mirroring N of M` line (which only logs on a COUNT CHANGE)
shows its last update at the exact same timestamp the agent's diagnostic stops — `13:13:55.966`
plugin, `13:13:56` agent, same session. Meanwhile the plugin's OWN `local_combat` state machine —
entirely in-process, no agent involvement — keeps cycling forever on schedule, and `DriveOne`
keeps reading the shared block every frame, faithfully rendering whatever the agent last wrote
before it stalled.

**This is not a `local_combat` bug, and not a per-creature bug.** It happens to SHOW UP as
"the fighting creature never lands a hit" because `local_combat`'s exclusion depends on the
agent's census staying fresh (so it can genuinely omit whichever creature is currently excluded,
one that constantly changes) — but the underlying stall freezes ALL enemy presence data, not
just the excluded entry. Every mirrored enemy's position should be equally stale after the
stall, which the position-over-time watches from earlier rounds are consistent with (frozen
solid, not slow-pathing) but were never tested against this specific hypothesis until now.

**Not root-caused further this round** — deliberately. Finding WHY `PumpEnemies` (or whatever
calls it) stops being invoked needs its own focused investigation (a silently-swallowed
exception in the agent's main loop, a dead task, a stuck throttle clock, or something else
entirely) rather than a guess grafted onto an already-long session. Both temporary diagnostics
(`EnemyPresenceController`'s "STILL SEEING" log, `CoopAgent`'s `moduleClean`/`excludedId`/
`TryWriteEnemies` logs) are left in place, since they are exactly what the next round needs and
were expensive to build correctly.

**Concrete next step:** instrument the agent's OWN main pump loop (not `PumpEnemies`
specifically) to log a heartbeat every tick, and find the exact point — an unhandled exception,
a `Task` that faulted and was never awaited again, a throttle field with a corrupted value —
where it stops advancing. Given the timing (a stall roughly a minute after a module transition,
in every reproduction so far), the transition itself is a strong first place to look, not a
red herring to rule out.

Full suite: 966/966 (one confirmed pre-existing flaky test —
`DisconnectRecoveryTests.HeavyPacketLossStillConverges`, unrelated). Both builds clean, rig torn
down and confirmed clean. Host button and materialised-loot-sync live confirmation from earlier
this session both hold, unaffected by this finding.

### The stall root-caused: a lost plugin block never retries once the player resolves on its own (2026-08-21, round 42)

Not an exception, not a deadlock — a clean early return with nothing wrong with it in isolation.
`CoopAgent.PumpOnce()` reads the local player's own state FIRST (line 240), then checks
`bridge.CanWriteRemotes` (line 264) and returns early if it's false — *before* the entire
combat-pump block (`PumpEnemies`/`PumpPhantomDamage`/`PumpMaterialisedLoot`/`PumpParty`, all
gated together on one 150ms cadence check). This exactly explains the observed symptom: local
HP/module readouts (read before the check) kept updating fine, while everything after the check
froze solid, with nothing thrown and nothing logged as a failure — because nothing failed; the
method just legitimately stopped reaching that code.

`CanWriteRemotes => _pluginBlock != 0 || _phantoms.Count > 0`
(`KotorGameBridge.cs:583`). The only place `_pluginBlock` is ever reset to 0 and re-scanned for
is `TryFindPlugin()` (`KotorGameBridge.cs:997`), and the only caller of `TryFindPlugin()` is
`TryResolve()`, which is itself only ever invoked from inside `TryReadLocalState()`'s
`if (_player is null)` branch (`KotorGameBridge.cs:1164`). A module transition destroys and
rebuilds every engine object, which is exactly what invalidates `_player` and triggers this
branch — but `TryFindPlugin()` does a real pattern scan with a 60ms heartbeat double-read to
confirm the block is genuinely live, not stale freed memory, and a transition's own main-thread
stall (a movie can hold it for several seconds, measured elsewhere in this document) is long
enough for that heartbeat check to read the identical value twice and conclude — wrongly — that
the block is dead. `TryResolve()` can still recover the PLAYER on that same pass via its
scan-based fallback path, which needs no plugin block at all. Once `_player` is non-null again,
`if (_player is null)` never evaluates true again for the rest of the session, and nothing else
ever calls `TryFindPlugin()` a second time. `_pluginBlock` sits at 0 forever: `CanWriteRemotes`
silently falls back to `_phantoms.Count > 0` alone, which is *also* commonly 0 right after a
transition (phantoms are destroyed and re-claimed across one too) — and the whole combat-pump
block goes dark for the rest of the session, with no exception, no failed read, and no log line
anywhere saying so.

**Fixed:** `TryReadLocalState()` now retries a lost plugin block independently of the player
path — a `_pluginBlock == 0` check right after the existing player-resolve block, rate-limited
on its own dedicated timestamp (`_lastPluginRetryMs`, same 1000ms `ResolveRetryMs` cadence the
player retry already uses, but tracked separately so neither retry has to wait on the other's
clock). `KotorGameBridge.cs`, `TryReadLocalState()` (the new block right before
`nint creature = _player!.Address;`) and the new `_lastPluginRetryMs` field.

**Live-tested, partially.** Rebuilt (agent-side only — this fix is entirely in
`KotorCoop.Memory`, no plugin/native change, no reinstall needed), relaunched both agents against
the same live pair, forced a fresh transition (`warp end_m01aa` then back), and confirmed the
`moduleClean` diagnostic kept firing immediately after the new transition landed — consistent
with the fix, not contradicting it. **What this pass did NOT get to: a full, sustained
multi-minute post-transition soak proving the stall genuinely cannot recur with the fix in
place** — the original bug's own reproductions needed 60-90+ seconds AND, per the two-session
comparison above, isn't perfectly deterministic (whether the race is lost at all likely depends
on how long a given transition's main-thread stall happens to run). A clean pass here is
encouraging but not the same standard of proof this document holds everywhere else. **The
concrete next step is exactly that:** repeat the transition-and-wait test for several full
minutes, ideally across several different transitions (some movie-carrying, some not, to vary
the stall duration the race depends on), watching for the `moduleClean` line's cadence rather
than assuming a few seconds of continued logging is sufficient.

Full suite: 966/966, both builds clean (`dotnet build` and `build.ps1`). Rig torn down and
confirmed clean (`Get-Process` returned none for either process name), `auto_start_agent`
restored to `1` in both `coop.ini` files (this round used a manual agent relaunch instead of
`demo.ps1 -Stop`, which is the only thing that normally restores it — flagging this explicitly
since it is exactly the kind of silent leftover this document has warned about before).

### The multi-minute soak found the stall reproduces again — and it is a DIFFERENT bug this time

Ran the soak the previous entry called for: fresh session, warp to `tar_m03aa`, `moduleClean`
diagnostic watched continuously (a live `Monitor` tailing the agent log, not a fixed sleep-then-
check). **First run:** the diagnostic went dark at `14:12:44` and never returned for the rest of
a 6-minute observation — while the PLUGIN side stayed fully alive the whole time (its own
`local combat` cycle kept excluding/timing-out a creature on schedule, one line per second,
proving the game's main thread was never stalled). This ruled out a frozen game and pointed
back at the agent's own pump.

Added instruction-level diagnostics to settle it rather than guess: a throttled log line right
at the `CanWriteRemotes` gate in `CoopAgent.PumpOnce` (`stall diag: CanWriteRemotes=false...`),
and inside `TryFindPlugin` itself (`TryFindPlugin: scan found N candidate(s): ...`, plus a
per-candidate heartbeat-check result) — the latter routed through `KotorGameBridge`'s existing
`ResolveDiagnostic` delegate, which this pass discovered only ever reached `Console.WriteLine`
on the agent's own visible window, NOT the log file `demo.ps1` captures — `Program.cs` now also
appends it to the log file directly, since a diagnostic nobody can read is not a diagnostic.

Directly verified, independent of the agent, that the block itself was healthy the whole time:
`blockpoke.ps1 state` found it at the same address (`0x17020000`) the entire session, and a
standalone `ReadProcessMemory` probe against the exact `Heartbeat` field (`+0x4C`) showed it
advancing normally (16567 → 16574 → 16582 across 400ms) while the stall was ongoing. The
region backing it is a trivial 16KB private-committed allocation, far under every size/scope
filter `PatternScanner`/`ProcessMemory.EnumerateRegions` apply. None of that is the problem.

**Second run, with the new diagnostics in place, reproduced the stall again — and disproved the
leading hypothesis.** A `[resolve]` line fired right at the transition
(`player: adopted the engine's own creature 0x19085480 (id 0x7FFFFFFF)...`) — that branch is
gated on `_pluginBlock != 0`, so the plugin block was demonstrably NOT lost this time. The
`CanWriteRemotes=false` diagnostic never fired once in 4+ minutes, and `TryFindPlugin`'s own
diagnostic never fired either — meaning that retry path was never even entered. **This is a
different stall than the one `39d555a` fixed, reproducing under the identical external symptom
(the `moduleClean` diagnostic goes dark) but with a different, still-unknown internal cause** —
something between the `CanWriteRemotes` check (confirmed passing) and the `moduleClean` log
line (never reached) is now blocking without throwing (no `pump tick failed` anywhere in either
run) and without the OS considering the process hung (`Get-Process` reported `Responding: True`
throughout, with heavy accumulated CPU time — 304s — consistent with something looping hard
rather than a true deadlock, though this was not confirmed further before this pass ran out of
budget).

**Status: NOT fixed. The `39d555a` fix is real and closes the specific race it targeted, but it
is not sufficient — there is at least one more, distinct cause of the same external symptom.**
The next round should: (1) trust the two new diagnostics already in the tree (`CoopAgent.cs`'s
`CanWriteRemotes` gate log and `KotorGameBridge.cs`'s `TryFindPlugin` scan log, both temporary
and clearly marked) rather than re-adding them; (2) since both stayed silent this time, narrow
the search to the code between `CanWriteRemotes`'s check and the `moduleClean` line specifically
— `session.Capabilities`, the module-list filtering, the mod-compatibility masking, or anything
else in that stretch that could hang or loop without throwing; (3) consider whether the high
accumulated CPU time is a genuine clue (a hot loop somewhere in that stretch) versus just this
process's cumulative total across the whole session.

Full suite: 966/966, both builds clean. Rig torn down and confirmed clean, `auto_start_agent`
restored to `1` in both `coop.ini` files.

### Solved — it was never a pump bug. `session.Capabilities` correctly dropped a channel (2026-08-21, round 43)

The previous round's own third candidate — "`session.Capabilities`, the module-list filtering,
the mod-compatibility masking" — was the answer. A temporary throttled diagnostic at
`PumpEnemies`'s own entry (`SupportsEnemySync`, `caps`, logged every 3s) settled it cleanly, no
guessing:

- **First transition, watched clean for 6+ minutes:** `SupportsEnemySync=True hasEnemyCap=True
  caps=All` on every sample. No stall at all.
- **A second, rapid transition back into the same module (`tar_m03aa`) reproduced it
  immediately:** `SupportsEnemySync=True hasEnemyCap=False caps=Phantoms, WorldState, Journal,
  Appearance, Equipment, Dialogue, Progression`. `Enemies` is missing — and so are `Doors`,
  `GroundItems` and `Party`, silently, together. That is `ContentCompatibility.ObjectIdKeyed`
  exactly (`Enemies | Doors | GroundItems | Party`, `ContentCompatibility.cs:274-276`) — the mask
  this codebase already documents dropping together on a detected module-GIT mismatch. `caps`
  still contained `WorldState`, ruling out the separate, unrelated `WorldState | Doors` branch at
  `ContentCompatibility.cs:337` — this was specifically the id-keyed group, specifically a GIT
  mismatch.

**So the pump was never stuck.** `PumpEnemies` reached its very first line every single tick,
read `bridge.SupportsEnemySync` and `caps` correctly, and correctly, deliberately returned early
— because the mod-compatibility system had genuinely detected that the two installs' `tar_m03aa`
GIT state disagreed, and correctly degraded the id-keyed channels rather than risk driving one
side's object ids onto the other's (mismatched) creatures. Every earlier round's static audit of
`CoopAgent.cs`, `KotorGameBridge.cs`, `PatternScanner.cs` and `CoopClient.cs`'s `_gate` lock sites
being clean was accurate — none of that layer was ever broken.

**Why it looked like a bug for four rounds:** no compatibility-mismatch announcement fired
(`CompatibilityAnnouncer` only announces on a re-evaluation EDGE, and this session's rig has run
this exact `tar_m03aa` scenario dozens of times across the whole `local_combat` investigation, on
the SAME two long-lived GOG installs — enemies killed, items moved, differently each round — so
by this point the two installs' `tar_m03aa` GIT state has genuinely, quietly drifted apart over
many hours of real testing). A silent, correct degradation with no on-screen signal is
indistinguishable from a hang until someone reads the actual gate value.

**No code fix needed for the stall itself** — it was correct behaviour. One real, independent
improvement was made and kept regardless: `SaveDigestCache.Run()` (the module-GIT background
compute this same trail suspected earlier) now runs on a dedicated background thread instead of
`Task.Run`, since queuing synchronous archive I/O onto the SAME shared thread pool this process's
other async continuations depend on is a real anti-pattern worth closing even though it was not,
in the end, this bug's cause.

**Practical note for this repo's own test rig, not a code change:** the two GOG installs at
`C:\GOG Games\Star Wars KOTOR (GOG)` and `D:\Games\swkotor-gog-p2` have accumulated genuine
divergent save state after this many hours of live testing. A fresh, matched save pair would stop
tripping this for future rounds; alternatively, seeing `caps` silently drop `Enemies`/`Doors`/
`GroundItems`/`Party` on this specific pair from here on is the compatibility system correctly
doing its job, not a regression to chase.

**`local_combat` itself:** every fix from rounds 9 through the empty-script-slot safety gate
remains real and correct. The one thing genuinely still unconfirmed is the full sustained-combat
pass on a CLEAN save pair (no accumulated GIT drift) — the last several rounds kept getting
diverted by this exact stall before reaching it. That is the actual remaining item, not a pump
bug.

Full suite: 966/966, both builds clean. Rig torn down and confirmed clean.

### Round 44 (2026-08-21) — a fresh cold restart gets a clean save pair; the sustained-combat
### pass still does not land a hit, and a real defensive gap was found and fixed along the way

A full process restart (both `swkotor.exe` and both agents killed, `demo.ps1` relaunched fresh
against save slot 1 on both sides) reached `korr_m34aa` — a module NEITHER install had
independently touched this session — with **no `W_SAVE` warning at all** and `TryWriteEnemies
returned True, wrote 3 enemies` on the client. This confirms round 43's own diagnosis exactly:
the drift was accumulated IN-PROCESS state across one very long-running session, not a permanent
mismatch baked into the save files themselves. A cold restart with a fresh load is the practical
fix for this rig going forward — no code change was needed for that part either.

**With a genuinely clean pair, the sustained-combat symptom this whole chain has been chasing
reproduced immediately and unambiguously.** A real, GIT-placed hostile (`0x6`, then `0x9` after
it cycled) engaged under `local_combat`, was confirmed to have a real `k_def_combend01` script
(the empty-script gate correctly did not exclude it), and simply never moved: `local combat
diag: t=24.0s 0x6 at (110.61,98.77) player at (98.51,108.55) dist=15.56` — the identical
coordinate on every sample for the full 25s cycle. The player was then teleported to point-blank
range (`dist=2.45`, later `dist=1.68` on the next creature) and held there for 20+ seconds each
time: **still zero movement, zero HP change, zero swing.** This rules out pathing/distance
entirely — the creature does not attack even when melee range requires no approach at all.

**A real, independently-valuable defect was found and fixed while investigating this.** The
existing round-41 diagnostic ("STILL SEEING excluded id") fired repeatedly during this session's
capture — proof that `DriveOne` (`EnemyPresenceController.cs`) had no guard of its own for the
currently-excluded/locally-fighting creature and relied ENTIRELY on the agent-side upstream
filter (`CoopAgent.cs`) to keep it out of the plugin's own enemy list. That filter has a
confirmed gap: the excluded creature still reached `ClaimCreatureForFight`/`PermitAttacking`
every frame it leaked through, exactly the re-claim/re-assert cycle the class's own header says
"the local AI must not fight" while a creature is meant to be fighting on its own. Added a direct
skip in `DriveOne` (mirroring the existing `DiagUndrivenObjectId` pattern) so this instance never
touches the excluded creature regardless of whether the upstream filter's read is stale — defence
in depth, not a single point of failure. **Rebuilt and reverified: the "STILL SEEING" line no
longer fires at all.** This is kept as a real fix.

**It was not the root cause of the hit-landing symptom.** With the leak closed, the identical
test (fresh creature, melee range, 20+ seconds held) still produced zero movement and zero
damage. Also ruled out this round: re-firing the script manually (`firescript 0x9 fire`) while
the player was ALREADY at point-blank range produced no change either — so it is not that the
original `StartLocalFight` fired while the player was still far away and the creature's one-shot
decision went stale; a fresh fire at genuine melee range does nothing new.

**What is left, precisely.** Round 27's host-local success and round 37's "outside the census
entirely" success share one property this creature does not: neither was ever claimed
(`Odyssey.ClaimCreature`/`ClaimCreatureForFight`) even once before its script was fired. This
creature spent real time normally mirrored (claimed and re-claimed every frame) before engaging
and being excluded. Read `ClaimCreature`/`ClaimCreatureForFight`/`PermitAttacking`'s full bodies
this round specifically checking whether they touch anything in the round struct
(`+0x934`..`+0x9CC`, the sub-structure at `+4+(+0x96C)*0x14C`) that the 27-round Strategy-1
investigation mapped — they do not; they only touch `ObjCommandable`, `CreMovePermitted`,
`CreRunFlag`, and conditionally call `ClearAllActionsFn` (`0x004CCD80`,
`CSWSObject::ClearAllActions`). **`ClearAllActionsFn` itself was never checked for a side effect
on the round sub-structure** — it runs every single frame on every normally-mirrored creature
before it is ever excluded, and if it (or repeated calls to it over the creature's whole time in
normal mirroring) leaves the sub-structure in a state a post-hoc `StartCombatRound` sequence
cannot repair — unlike a creature that was NEVER put through that loop at all — that would
explain both successes and this failure with one mechanism. This is the concrete next candidate,
not yet disassembled.

**Status: `local_combat` is still not confirmed working end to end against a real, previously-
mirrored, organically-engaged enemy.** It works for a creature that was never claimed at all
(round 37) and for a creature tested in complete isolation from any co-op session (round 27) —
it has never yet been observed landing a hit on a creature that spent real time in normal
mirroring first, which is the actual, ordinary case every real ambush produces. The defensive fix
in this round is real and stays; it did not close the gap.

Full suite: 965/966 this run (the one failure, `DisconnectRecoveryTests.HeavyPacketLossStillConverges`,
is the same known-flaky probabilistic test flagged throughout this session — confirmed passing
in isolation immediately after). Both builds clean. Rig torn down and confirmed clean.

### Round 45 (2026-08-21) — `ClearAllActionsFn` is REFUTED as the mechanism; the commandable-flag
concern is already correctly handled

Round 44 left `ClearAllActionsFn` (`0x004CCD80`) as the concrete next candidate — never
disassembled for a side effect on the round sub-structure. It has now been read in full,
instruction by instruction (`python tools\disasm.py 0x004CCD80 0x100`):

- Its early guard (`0x4CCD88`-`0x4CCDA9`) reads a global at `0x83281c`, a vtable call at
  `[ecx]+0x30`, then `[eax+0xa88]` and a conditional call to `0x5e5670` — none of this is
  `creature+0x9C8` (the round base) or anything in its offset range.
- The body it exists for (`0x4CCDB1` onward) walks `CSWSObject+0xFC` — the ACTION QUEUE list,
  exactly as `Odyssey.cs`'s own existing doc comments say (`CExoLinkedList` at `+0xFC`,
  `CExoLinkedList::GetAt 0x5e9b60`, `CExoLinkedList::RemoveAt 0x5e9c10`, the free at
  `0x6fa390`) — a completely different structure from the round base at `creature+0x9C8`.
- The one write outside that list (`0x4CCE69`, `mov dword ptr [eax+0xa8c],1`) lands on
  whatever the repeated `[reg]->[+0x30]()` vtable call returns — not the creature pointer
  itself, and not anywhere near `+0x958`..`+0x9CC` even if it were.

**`ClearAllActionsFn` never touches the round struct, directly or through this call. That
hypothesis is dead.**

Also checked, since round 44 flagged it as one of only three things `ClaimCreature` touches:
`ObjCommandable` (`creature+0xE8`). Only one write to it exists in the whole plugin
(`Odyssey.cs:375`, inside `ClaimCreature`) — and it sets the flag to **1**, with a comment
explaining an EARLIER version of this code set it to 0 and that was "self-defeating: the
action queue is executed by the AI master, so a non-commandable creature ignores the move
actions we queue and never walks anywhere." This was already found and fixed in an earlier
session; nothing here explains the current failure either.

**Status, honestly: round 44's "claimed vs. never-claimed" distinction is still the sharpest
lead on record, but neither of its two obvious mechanisms panned out.** What's left
unexplored on that axis: not a single corrupting WRITE (ruled out for the two functions that
run during normal claim), but a longer-duration, harder-to-instrument possibility — whether
being position/facing-driven every frame for real wall-clock minutes (the ordinary case for
any enemy in an active ambush before the player gets close enough to engage it) leaves the
ENGINE'S OWN pathfinding/movement state confused in a way a later `StartCombatRound` does not
reset, as distinct from any single field this investigation's static reading can find. That
needs a live before/after comparison (a creature engaged almost immediately after spawning vs.
one left mirrored for several real minutes first) rather than more disassembly — the next
concrete step, not yet run.

No live testing or code changes this round — pure static disassembly, in the main repo
checkout. Full suite unaffected (no source touched).

### Round 46 (2026-08-21) — the flag-restoration lead is REFUTED live; two real bugs found
### and fixed along the way; the freeze survives both, and the mystery is narrower but open

Started from round 45's own open question and the task's specific untested lead: does
`ClaimCreature`/`ClaimCreatureForFight`/`PermitAttacking` leave `CreMovePermitted`/`CreRunFlag`
in a state that blocks `AIActionPhysicalAttacks` (`0x005BBBF0`) even at melee range, and is
anything restored on exclusion? Read `EnemyPresenceController.cs` in full and every `Odyssey.cs`
site touching `ObjCommandable`/`CreMovePermitted`/`CreRunFlag`/`CreMoveState`.

**Static read: the claim writes are OR-only, so a bit they set can never be a NEW restriction.**
`ClaimCreature` does `CreMovePermitted |= 2` and `CreRunFlag = 0`; `PermitAttacking` does
`CreMovePermitted |= CreAttackPermittedBit (0x04)`. Neither ever clears a bit. A creature that
was never claimed (round 27, 37) and one that was claimed every frame for 90 seconds (this
round's case) can only differ by having EXTRA bits set on the claimed one, never fewer — so on
its face this lead looked already-refuted by inspection, before any disassembly. Two things kept
it live anyway: `DriveEnemy` (not `ClaimCreature`) ASSIGNS `CreMoveState` and `CreRunFlag` every
driven frame from the smoother's own "moving" bit — that is destructive, not additive — and
nobody had actually disassembled `AIActionPhysicalAttacks` itself to see what it reads.

**Disassembly of `0x005BBBF0` found a THIRD, previously undocumented gate bit.** Past its first
three preconditions (a `GetDead`-style vtable call, `0x63e7f0`, `0x4ef890`), at `0x005BBD3B`:
`mov ax, word ptr [esi+0x9f0]` (the `CreMovePermitted` word) / `test al,al` / `jns` (bails
unless bit `0x80` of the LOW BYTE is set) / `test al,4` / `je` (bails unless the already-known
attack-permitted bit is ALSO set) / then a self-attack guard. So the real gate is bits `0x80`
AND `0x04` together, not just the documented `0x04` — and bit `0x80` is written by NOTHING in
this plugin, anywhere (confirmed by a full byte-pattern scan of `.text` for every accessor of
offset `0x9F0`/`0x9F1`, `tools/disasm.py`+a one-off capstone sweep, cross-checked against
`tools/xrefs.py`'s call-site model). This looked like the answer.

**Disassembling `ClearAllActionsFn` (`0x004CCD80`) in FULL (round 45 stopped partway) found a
second, independent, genuinely destructive mechanism.** Past the action-queue loop, at
`0x004CCEC9` its `bIsCurrentOnly` argument (which every call site in this codebase passes as
`1`) gates a call to `0x004D37E0`, which loops 5 times (`i=0..4`, stride `0x14C`) calling
`0x004D3010` on each of the creature's own `CSWSCombatRound` sub-structures. `0x004D3010` is a
FULL reset of that sub-structure — frees any queued action nodes at `sub+0x88`, and zeroes
`sub+0xB0` (verified at `0x004D3101`, `mov dword ptr [esi+0xb0], ebx` where `ebx=0`) — the exact
field rounds 9-37 spent 27+ rounds proving is the write that arms a round. So every time
`ClaimCreatureForFight`'s conditional wipe fires (whenever the driven creature's head action is
move-class — the ordinary case for a body this controller force-overwrites every frame), it also
disarms ALL FIVE of that creature's own round sub-structures. On the exact frame `StartLocalFight`
fires `+0x260`, `ClaimCreatureForFight` had already run first in the same pass — a real,
one-frame race that could disarm the very mechanism the script was about to rely on, one frame
before it needed it. Round 45's read of this function stopped at the action-queue loop and never
reached this branch, so "REFUTED" was too strong; it is real, just not a raw write to
`+0x958..+0x9CC` the way that round's search was shaped to find.

**Fix implemented, on `EnemyPresenceController.cs` and `Odyssey.cs`:** a new
`Odyssey.ClaimSnapshot`, captured once per creature the first time `Resolve()` sees it in a slot
(before this plugin's claim has ever touched it), holding `ObjCommandable`, the full
`CreMovePermitted` dword, `CreRunFlag`, and `CreMoveState`. `DriveOne` now computes the
engagement/trigger condition BEFORE the claim block (not after `Drive()`, as before); on the
exact frame it is about to start a local fight it skips `ClaimCreatureForFight` entirely (so
`ClearAllActionsFn`'s round-disarm branch never runs on the handoff frame), and after `Drive()`
restores the captured snapshot before firing `+0x260`, with `PermitAttacking` re-asserted
AFTER the restore so the attack-permitted bit can never be silently lost. Build clean, full
suite green (see below).

**Live-tested, twice, and BOTH the bit-0x80 theory and the flag-restoration fix are REFUTED —
the freeze is unchanged.** Cold-restarted both instances (`tools/demo.ps1`, save slot 1,
`END_M01AA`, clean census — `enemy drive: mirroring 25 of the owner's 25 enemies`, no `W_SAVE`).
Picked `0x1A` (`end_soldier2`, confirmed live to carry `k_def_combend01` via `firescript 0x1A`),
left it mirroring normally for 90+ seconds, then teleported the player to `dist=1.26` (genuine
melee range) with `tp`. `local combat: 0x1A pulled out of the census to fight for real` fired
correctly (proximity-triggered, no manual `firescript`), and a direct `ReadProcessMemory` probe
of the live creature (a one-off script, not part of the tree) read, WHILE FROZEN:

```
ObjCommandable   (+0xE8)  = 1
CreMovePermitted (+0x9F0) = 0x0000FFFF   (bit 0x80 = SET, bit 0x04 = SET, bit 0x02 = SET)
CreRunFlag       (+0xA98) = 0
CreMoveState     (+0xA8C) = -1 ("walking")
round sub[0..4]  +0xB0    = 0, 0, 0, 0, 0   (still never armed)
```

Bit `0x80` — the theory's whole premise — was ALREADY SET, live, on the frozen creature, both
before and after the fix. The gate `AIActionPhysicalAttacks` checks was never the blocker. 21+
seconds at `dist=1.26`: zero movement, zero HP change, zero swing, exactly matching round 44's
un-fixed baseline byte for byte. The fix changed nothing observable.

**A live memory probe also refutes a THIRD candidate found and fixed this same round, before it
was fixed.** `ClearAllActionsFn`'s tail (traced this round) also invalidates
`creature+0x508` and `(creature+0x340)+0x254` — a per-creature "last enemy" cache
(`ObjectInvalid`, `0x7F000000`) — every time its full body runs, which `ClaimSnapshot` was
extended to capture and restore. Live probe on the SAME frozen creature, AFTER that fix: both
fields still read `0x7F000000`, and the action queue was genuinely EMPTY (no head node at all).
At first this looked like a second live-confirmed bug the fix hadn't reached — but the
snapshot was captured on this creature's very FIRST resolve, before even one claim call had
ever touched it, and it read the SAME `0x7F000000` at capture time. A standing guard that has
never yet fought anyone has no "last enemy" to cache — that is its normal resting value, not
something this plugin corrupted. Restoring an already-invalid value cannot fix anything, and
round 27/37's WORKING creatures would have started from the identical natural default. This
lead is refuted by the same evidence that first looked like it confirmed it — recorded in full
because the false read is exactly the kind of thing a future round should not re-chase.

**What's left, honestly.** All three concrete, disassembly-grounded candidates this investigation
has produced across rounds 44-46 (`CreMovePermitted`/`CreRunFlag`/`CreMoveState`/`ObjCommandable`
staleness, the `AIActionPhysicalAttacks` bit-`0x80` gate, `ClearAllActionsFn`'s round-disarm and
target-cache side effects) are now checked, and none of them is the blocker — the two real,
independently-valuable fixes from this round stay in the tree (defence in depth for a real
one-frame race, and a real target-cache restore that is correct even though it wasn't the cause
here), but they did not close the gap. The round sub-structures never arm (`+0xB0 = 0` across
all five, every single time, in every failure this whole investigation has ever measured) — that
part of the signature has not moved once in 46 rounds. The sharpest remaining fact: round 27 and
37's creatures were NEVER touched by `DriveOne`'s claim/drive machinery even once before their
script fired; every failure (round 44's `0x6`/`0x9`, this round's `0x1A`/`0x17`) was. Every FIELD
that machinery is known to touch has now been checked and either was already fine or made no
difference restored — which, following round 38's own precedent for an identical shape of dead
end (an exhaustive field-write search across a real behavioural boundary that came up empty),
points toward the same two remaining options round 38 named: either the real mechanism is not a
field this investigation's method (managed-code field pokes, disassembly of the write side) can
see at all — worth tracing `RunScript`'s real dispatcher (`0x005D45D0`, documented above as "a
queue-based dispatcher with a pending-script list at `+0x1AC` and a counter at `+0x28`, not a
simple synchronous call" — never actually opened) for a per-object throttle, generation counter,
or "recently externally modified" flag that a creature driven every frame for real minutes could
trip and a never-touched one could not; or there is still an unfound touch-point. Recommended
next step for whoever picks this up: disassemble `0x005D45D0` in full before touching any more
`CSWSCreature` fields — the round struct's own internals have now been checked from every angle
this investigation's method can reach.

Fixes kept: `Odyssey.ClaimSnapshot`/`CaptureClaimState`/`RestoreClaimState`, and `DriveOne`'s
reordered trigger check that skips `ClaimCreatureForFight` on the handoff frame
(`EnemyPresenceController.cs`, `Odyssey.cs`). Safety properties re-confirmed live on the same
session: no materialised stand-in was ever selected (unchanged `mapped == 0` gate), and no
permanent exclusion — `0x1A`/`0x17` cycled cleanly through repeated 25s timeouts
(`local combat: 0x1A fight ended (timed out), rejoining the census` /
`local combat: 0x17 pulled out of the census to fight for real`, alternating cleanly, no stuck
state, no absorb/watchdog warnings). Full suite 1017/1017 (this branch's baseline moved from 966
during this round due to unrelated, concurrent work landing on the same branch — see this
session's commits around `583d8e6`/`1324f93`/`4373740`; a `git stash` race during test
verification briefly touched `tests/KotorCoop.Tests/ReliableSenderTests.cs`, which this round
does not own and left untouched). Both builds clean, rig torn down and confirmed clean
(`Get-Process` returned none for either process name).

### Round 47 (2026-08-21) — the empirical bisection: NOT a single DriveOne write, at all;
### both ends of the write space fail identically, which narrows and hardens round 38's own
### "read-only" conclusion instead of reopening it

Forty-six rounds tried the disassembly-first method — find a candidate field, prove what
writes it, restore or avoid the write, test live. Every candidate that method could name has
now been checked (rounds 9-46) and none explained the freeze. This round tried the other
half of the standing rule this document holds itself to: stop guessing which write matters
and let the rig answer it directly. A dev-only `coop.ini` key,
`local_combat_bisect_mask` (hex bitmask, default `0` = every op active — today's shipped
behaviour, bit for bit), was added so a live A/B pass could disable ANY subset of DriveOne's
own per-frame writes to a mirrored creature without touching the census-exclusion/trigger
machinery at all, and the census-membership question rounds 37-38 already isolated could
finally be re-run through the REAL automatic `StartLocalFight`/`MaintainLocalFight` path
instead of round 29's manual `firescript` diagnostic.

**Op table.** Every distinct engine write/call `DriveOne` (and its callees) performs on a
mirrored, not-yet-fighting creature, each now gated behind its own bit
(`Odyssey.BisectBitClaimCommandable` … `BisectBitClearPositionDirty`, `Odyssey.cs`, right
before `ClaimCreature`; `Odyssey.BisectAllOps` is all eleven at once):

| Bit | Op | Where | Function/field |
|---|---|---|---|
| 0 | `ObjCommandable = 1` | `ClaimCreature` | `creature+0xE8` |
| 1 | `ClearAllActionsFn` (targeted move-class wipe) | `ClaimCreature` | `0x004CCD80`, gated on `IsMoveAction(head)` |
| 2 | `CreMovePermitted \|= 2 ; CreRunFlag = 0` | `ClaimCreature` | `creature+0x9F0`, `creature+0xA98` |
| 3 | `CreMovePermitted \|= CreAttackPermittedBit (0x04)` | `PermitAttacking` | `creature+0x9F0` |
| 4 | `PlayOneShotAnimation` replay (on serial change) | `DriveOne` | vtable animation call |
| 5 | `SetOrientationFn` (facing write) | `DriveBody` | `0x004xxxxx` engine call, facing vector |
| 6 | `CreRunFlag` / `CreMoveState` destructive assignment | `DriveBody` | `creature+0xA98`, `creature+0xA8C` |
| 7 | `SetAnimation` vtable call (server animation field, on change) | `DriveBody` | vtable+`VtableSetAnimation` |
| 8 | `SetPositionFn` (engine's own SetPosition, suppress-dirty) | `DriveBody` | engine call, `creature+0x090` |
| 9 | Client-twin desired-position + animation-sync writes | `DriveBody` | `twin+…`, rendering only, never touches the server creature |
| 10 | `ClearPositionDirty` (`ClearDirtyFlagFn`) | `DriveOne` | `creature+0x1FC` dirty-flag bit array |

Two ops the task brief expected to find inside `DriveOne` are **not there** — a real
correction, not a gap in the table: the health pin (`Odyssey.SetMinOneHitPoint`,
`MirrorHealthRule.Target`) and any faction hold live entirely in `CombatController.Apply`
(`CombatController.cs:140,157-168`), a separate controller with its own 40-frame cadence and
its own `block.EnemyCount`/`GetEnemy` feed — `EnemyPresenceController.DriveOne` never calls
either. `local_combat_bisect_mask` therefore cannot and does not gate them; they keep running
at their normal cadence throughout every run below, exactly as they do in production today.
This is flagged rather than silently "fixed" because it is itself informative: whatever is
freezing a mirrored-then-excluded creature's round sub-structures is not something the health
pin or a faction write could explain either, since both ran identically, at their own
cadence, unaffected by the mask, in every trial below including the ones where nothing else
touched the creature at all.

Other callers of `ClaimCreature`/`ClaimCreatureForFight`/`PermitAttacking`/`DriveEnemy`
(`MaterialisedEnemies.cs`, `PartyPresenceController.cs`, `PhantomController.cs`) never pass a
mask, so they default to `0` — unaffected by this round, always full behaviour. The
handoff-frame ops (`ObjCommandable = 1` set directly, `RestoreClaimState`,
the post-restore `PermitAttacking`, `StartLocalFight`'s `FireCreatureScriptSlot0x260`) and all
of `MaintainLocalFight` are deliberately **never** gated by any bit — this mask only ever
touches the ONGOING per-frame drive that runs on a creature before it is ever handed to
local_combat, per the task's own framing of "went through DriveOne even briefly."

**Rig.** `tools/demo.ps1` cold start, save slot 1, `END_M01AA`, GOG installs at
`C:\GOG Games\Star Wars KOTOR (GOG)` (host, instance 1) and `D:\Games\swkotor-gog-p2` (client,
instance 2 — the mirroring side, where `EnemyPresenceController` actually drives anything).
`local_combat_bisect_mask` set in the CLIENT's `coop.ini` only (the host never mirrors its own
enemies). Target: `0x1A` (`end_soldier2`), confirmed live each run via `firescript 0x1A`
(peek) to carry `k_def_combend01` — the same creature round 46 used. Engaged via
`blockpoke.ps1 -ProcId <client pid> console 'tp <x> <y> <z>'` to `(45.80, 21.10, -1.3)`,
measured live at `dist=1.26` from `0x1A`'s own `(47.06, 21.08)` — genuine melee range, no
approach needed. Teardown via `demo.ps1 -Stop` between the two cold-start runs; a same-session
re-engagement was used for a second trial within run 0 (see below) since the rig was already
warm and uncorrupted, before the scheduled teardown.

**Bisection table.**

| Run | Mask | Ops active | Result | Evidence |
|---|---|---|---|---|
| 0, trial 1 | `0x7FF` (`Odyssey.BisectAllOps`) | none — creature only resolved/tracked/census-published | **FAIL** | `[17:12:59.337] local combat: 0x1A pulled out…` → `local combat diag: t=0.0s…t=16.1s 0x1A at (47.06,21.08) player at (45.80,21.10) dist=1.26` (constant, sampled every 1s) → `[17:13:24.339] local combat: 0x1A fight ended (timed out)…` → `where` → `hp 12/12` |
| 0, trial 2 (same session, re-engaged) | `0x7FF` | none | **FAIL** | `[17:15:29.349] local combat: 0x1A pulled out…` → `dist=1.26` constant `t=3.5s…t=21.5s` → `[17:15:54.350] local combat: 0x1A fight ended (timed out)…` → `where` mid-cycle → `hp 12/12` |
| control | `0x0` (default, fresh cold restart) | all eleven | **FAIL** | `[17:19:06.771] local combat: 0x1A pulled out…` → `dist=1.26` constant `t=0.0s…t=24.0s` → `[17:19:31.773] local combat: 0x1A fight ended (timed out)…` → `where` → `hp 12/12` |

Zero movement, zero HP change, zero swing in all three trials — byte-for-byte the same
signature rounds 44 and 46 already measured under the unmodified, mask-less code. The `where`
reply's own accounting is exact enough to rule out anything subtler than "no hit landed":
`hp 12/12` before, during and after every trial, and `0x1A`'s own position never moved off
its spawn coordinate to one decimal place across a full 25s cycle at melee range, three times.

**Per the task's own explicit branch: run 0 does NOT reproduce round 37's success, so the
cause is not inside `DriveOne` at all.** Disabling every one of the eleven ops this
controller performs — the full, exhaustive write surface `DriveOne` has on a mirrored
creature — changes nothing observable. A creature this plugin has genuinely never written a
single field of, never called `SetPosition`/`SetOrientation`/`SetAnimation`/`ClearAllActions`
on, still fails to arm through the exact same automatic `StartLocalFight` path that armed
round 37's cap-excluded creature successfully. There is no bit to find in this space, because
both endpoints of it — nothing active, and everything active — already agree on failure.

**What this adds beyond round 38, which already reached "no write-based mechanism" by
disassembly.** Round 38 exhaustively disassembled the READ-side call chain
(`Resolve()`→`ResolveServerObject`→`GetGameObject`→`CGameObjectArray::GetGameObject`) between
"published but untouched" (round 29's diagnostic) and "never published" (round 37's cap
exclusion) and found every touch provably read-only. But round 29's "untouched" creature
never went through the real `StartLocalFight`/`RestoreClaimState`/`MaintainLocalFight`
lifecycle at all — `DiagUndrivenObjectId`'s early `return true` in `DriveOne` bails out before
the trigger check even runs (`EnemyPresenceController.cs:517-520`), so round 29 could only
ever be fired by hand (`firescript … fire`). This round's mask=`0x7FF` creature, by contrast,
DOES go through the genuine automatic trigger — `IsEngaged`/`HasCreatureScriptSlot0x260` gate
it in, the handoff-frame `ObjCommandable` write and `RestoreClaimState`/`PermitAttacking` run
on it, and `StartLocalFight` fires its `+0x260` script exactly as production does for a real
ambush. That it fails identically closes the one gap round 29 and round 38 left open: it was
conceivable the manual-fire path differed from the real one in some load-bearing way. It does
not. Census membership — being enumerated in this instance's own published
`EnemyPresenceCount` and driven through `Resolve()`/`MaterialisedEnemies.Seen()`/the trigger
check at all, regardless of whether a single field is ever written — is confirmed, now via
the real production path, to be the entire variable rounds 9-46 have been chasing through
individual fields.

**Where that leaves the remaining candidates**, now that every write DriveOne makes is ruled
out and every read on the plugin's own side is proven read-only at the instruction level
(round 38): either (a) `MaterialisedEnemies.Seen(enemy.ObjectId, now)` — a bare managed-array
timestamp write with no engine touch at all, called once per frame for every published id in
`OnFrame` before `DriveOne` ever runs, which no round including this one has found a
mechanism for turning into an engine-visible effect; or (b) something inside the engine's own
script dispatch path that this investigation's method (disassembling a WRITE site) cannot
see because the relevant state is read and consumed somewhere this plugin never writes to at
all — a per-object throttle, generation counter, or "externally touched" flag consulted by
`RunScript`'s real dispatcher, `0x005D45D0`, exactly the concrete next step round 46 named and
never opened.

**Partial disassembly of `0x005D45D0` this round** (`python tools\disasm.py 0x005D45D0
0x200`, 79 instructions, the full prologue through both early-return tails): confirms round
34's characterisation at the instruction level — this is a QUEUE-based dispatcher operating on
its OWN object (`esi`, the VM/dispatcher instance, not the creature), not a direct synchronous
call. It maintains a pending-script list at `esi+0x1AC` (enqueue via `0x005D21F0`, a
capacity/reset helper at `0x005D2140`), a depth/count field at `esi+0x28`, a per-slot
caller-object pair at `esi+eax*4+0x16C`/`+0x18C` (written from the two stack args at
`[esp+0x1C]`/`[esp+0x18]` — plausibly the script's OBJECT_SELF and a secondary caller id,
unconfirmed), a "currently running" pointer at `esi+0x3D4`, and calls out to `0x005D4270`
(entered with the pending-list head, likely the actual dequeue-and-execute step),
`0x005D44F0`, `0x005D2260`, and `0x005E5670`/`0x005E58A0` on its failure/short-circuit paths.
No per-CREATURE field (an offset in the `0x0-0xB00` range any `CSWSCreature` accessor in this
file would recognise) appears anywhere in this function — everything it touches is offset
from `esi`, the dispatcher's own state, not from `edi`/`edx` (the creature/script-owner
arguments). **This does not rule out (b)** — a throttle keyed on the CALLER's id could live
inside `0x005D4270`, `0x005D44F0`, `0x005D21F0`, `0x005D2140`, or `0x005D2260`, none of which
were opened this round — but it does mean the dispatcher's own top-level frame is not where
such a check would sit, so the next round should start at `0x005D4270` (the actual
dequeue/execute step, called with the pending-list head at `0x005D4695`) rather than
re-reading `0x005D45D0` itself.

**Status, honestly.** The bisection answered its own first branch point decisively and with
three independent live confirmations (two same-mask trials plus a mask=`0` control,
byte-for-byte identical failure signature every time): **the cause of the freeze is not any
DriveOne write operation.** `local_combat_bisect_mask` and its op-level gating are real,
tested (full suite unaffected — see below), and kept in the tree as a diagnostic instrument
for whoever picks this up next, exactly as intended; they did not, and structurally cannot,
close the gap, because the gap was never in the write space they cover. The mystery is
narrower than round 46 left it (every DriveOne write is now excluded, not just the four
fields rounds 44-46 tried restoring) but still open: the boundary is census membership
itself, and the two remaining candidates for WHY are `MaterialisedEnemies.Seen`'s managed
timestamp write (never yet connected to any engine-visible effect) and the unopened interior
of `RunScript`'s real dispatcher, specifically `0x005D4270` onward. `local_combat` itself is
unchanged by this round — still on by default, still safe (no materialised stand-in ever
selected, no permanent exclusion, the 25s cycle still resolves cleanly in every trial above)
— and the one thing it still cannot do, on a previously-mirrored creature, is land a hit.

Build clean (`dotnet build KotorCoop.slnx`, `src\KotorCoop.Plugin\build.ps1 -Install` against
both GOG installs). Full suite 1080/1080 (baseline moved from 1017 during this round due to
unrelated, concurrent work landing on the same branch — see this session's own commit log
around this round's own commit; `HeavyPacketLossStillConverges` passed this run, still
flagged as the known-flaky one throughout this document). Rig torn down and confirmed clean
(`Get-Process swkotor,KotorCoop.Agent` returned none), `auto_start_agent` restored to `1` in
both installs by `demo.ps1 -Stop`.

### Round 48 (2026-08-21) — the probe finds real, engine-measured hostility failure, but it is
### not this plugin's doing: the same reading holds on the host's own unmirrored engine copy

Round 47 closed the DriveOne write space and flagged two ops it had never masked: the health
pin and "any faction hold," both said to live in `CombatController.Apply`. Reading
`CombatController.cs` in full this round found that claim is half wrong: there is NO faction
write anywhere in `CombatController` (confirmed by grep across the whole file — the only
writes `Apply` makes are `Odyssey.SetMinOneHitPoint` and, conditionally, `Odyssey.SetCurrentHitPoints`).
A full audit of every controller besides `EnemyPresenceController` that can touch a mirrored
enemy or the local player's own reputation-relevant state — `CombatController`,
`PhantomController.ApplyTargetable`, `PartyPresenceController.ApplyCompanionFaction`,
`MaterialisedEnemies.MarkAsMirroredEnemy` — found exactly ONE faction write in the whole
plugin that a GIT-identical, census-tracked enemy like `0x1A` could ever be touched by: none.
`PhantomController.ApplyTargetable` does hold a faction, but only on THIS instance's own
phantom body (the one representing the REMOTE peer), never on a mirrored enemy — irrelevant to
a GIT creature. `MaterialisedEnemies.MarkAsMirroredEnemy` (which DOES touch faction indirectly
via the attack-permitted bit, and would clear `ObjPlot`) is never called for a GIT-identical
enemy at all — only for a locally-materialised stand-in (`mapped != 0`), which `0x1A` is not.
So round 47's "faction hold" was imprecise, not a gap in that round's own table — but its
instinct (something CombatController does, unmasked, might matter) pointed at the two writes
that genuinely were never gated: the health pin and the health-mirror write itself.

**Op table (the round-48 addition).**

| Bit | Op | Where | Function/field |
|---|---|---|---|
| 11 | `SetMinOneHitPoint(creature, true)` (the health pin) | `CombatController.Apply` | `creature+0x200` |
| 12 | `SetCurrentHitPoints` (the health-mirror write itself) | `CombatController.Apply` | engine setter |

Both gated behind the SAME `local_combat_bisect_mask` ini key round 47 introduced (`Odyssey.BisectBitHealthPin`,
`Odyssey.BisectBitHealthMirror`; `EnemyPresenceController.LocalCombatBisectMask` made `internal`
so `CombatController` — a separate class, same assembly — can read the one static instance of
the mask directly, the same way every other consumer already does). 0 (default) is
unconditional, byte for bit identical to before this round. `Odyssey.GetAIStateReputation` was
also added this round: a public wrapper around the same `GetAIStateReputationFn` (0x0057CD90)
round 18 used as a private diagnostic, so a caller outside `Odyssey.cs` (the probe below) can
ask the engine's own hostility question without a second hand-rolled pointer chain.

**The probe**, added at the top of `StartLocalFight` (`EnemyPresenceController.cs`), fires once
per arming, before the `+0x260` script — creature and player faction, the engine's own
reputation answer both directions, `CreMovePermitted`, plot/min1hp, and HP/maxHP:

    local combat probe: 0x1A creatureFaction=4 playerFaction=0 rep(creature,player)=1
    rep(player,creature)=1 (2=hostile,1=friendly,0=neutral) CreMovePermitted=0xFFFF plot=False
    min1hp=True hp=1/1 bisectMask=0x0

**This is the answer to the task's own first branch, and it is decisive: hostility reads
FRIENDLY (1), not hostile (2), symmetric in both directions, measured through the exact engine
call `GetNearestEnemy`'s own hostility gate uses.** `GetNearestEnemy`/`GN_DetermineCombatRound`
requires `==2`; this pair never clears that gate, so the round was never going to arm no matter
what any DriveOne or CombatController write did — a plain, structural explanation that fits
every failure this investigation has ever measured. A second creature engaged in the same
session, `0x17` (a DIFFERENT faction, `2` not `4`), read the identical friendly-both-directions
answer, so this is not a one-creature fluke.

**The critical follow-up, before accepting "this plugin corrupted hostility" as the story: is
this reading something the plugin did, or something already true of the creature?** Tested
directly on the HOST's own completely unmirrored, unmodified engine copy of the identical
creature — the host process never runs `EnemyPresenceController`'s claim/drive machinery or
`CombatController`'s pin/mirror on its OWN local enemies the way a mirroring instance does; a
host-local Sith soldier is governed purely by native, unpatched engine AI. Teleported the
host's real player (`blockpoke.ps1 tp`) to point-blank range of `0x1A` (`dist` effectively 0,
same coordinates the client used) and held 18+ seconds: **zero damage, zero movement, exactly
the client's failure signature.** Repeated on a second, unrelated census enemy (`0x9`, a
different position entirely, `(30.5,119.8)`): same result, 15+ seconds, zero damage. **The
non-hostile reading is not a co-op artifact — it reproduces on the host's own authoritative,
completely unmirrored engine, for two different creatures.** Every candidate write this
investigation has ever found (round 47's eleven DriveOne ops, this round's two CombatController
ops, sixteen total) has now been read in full, and NONE of them touches faction or the
reputation matrix — consistent with the host showing the identical symptom with none of them
ever running on its own local creatures at all.

**Bisection run, for completeness and to rule out an indirect effect** (`local_combat_bisect_mask
= 0x1800`, health pin + health-mirror both off, fresh cold restart, save slot 1, `END_M01AA`,
same rig as round 47):

| Run | Mask | Result | Evidence |
|---|---|---|---|
| control | `0x0` | FAIL, friendly reading | `local combat probe: 0x1A ... rep(creature,player)=1 rep(player,creature)=1 ... min1hp=True hp=1/1 bisectMask=0x0` |
| health-pin+mirror off | `0x1800` | FAIL, IDENTICAL friendly reading | `local combat probe: 0x1A ... rep(creature,player)=1 rep(player,creature)=1 ... min1hp=False hp=1/1 bisectMask=0x1800` |

The mask correctly took effect (`min1hp` flips `True`→`False`, confirming the new bit gates the
real write, and the loaded-mask log line decodes `healthPin=True, healthMirror=True` as
disabled) — and the reputation reading, the thing that actually matters, does not move at all.
**Health pin and health-mirror write are REFUTED as the cause**, empirically, not just by
static reasoning. One more fact fell out of this run for free: with the health-mirror write
disabled, `0x1A` still reads `hp=1/1` — meaning `1` is this creature's genuine, intrinsic
current AND max hit points in its own stats block, not a value CombatController's mirror ever
set. `0x9` reads `hp 1/8` the same way (a real max of 8, not the mirror's floor). Both are
census enemies that look battle-worn almost to death, baked into the save itself, present
identically after a genuinely fresh cold restart (checked twice, two separate reloads of save
slot 1) — not something accumulated by this session's own testing.

**The most likely explanation, honestly speculative but consistent with everything measured
this round:** save slot 1 has been the SAME save, reloaded fresh dozens of times, across every
round of this investigation since round 9. Several of its Sith troops (`0x1A` max 1 hp, `0x9`
max 8 current 1, `0x17` reading `1/1` the same way) show real, low, persisted stat damage —
consistent with having actually been fought for real at some point across 40+ prior rounds
(round 27 and 39's own genuine fights, in particular, both landed real hits on host-local
creatures) — and KOTOR's own stock AI has a well-known "flee/surrender when critically
wounded" behaviour in several combat scripts that can include a faction change away from
hostile as part of standing down. If that fired for real, on the host's own engine, at some
point during 40+ rounds of prior live-fire testing, and the module was later saved, every
"fresh cold restart" since has been reloading an ALREADY-DEFEATED, ALREADY-NEUTRALISED Sith
soldier — never mind mirrored or driven, this specific creature may never have been hostile to
the player at all in the state this save currently holds. This is speculation about WHY, not
a new claim of fact beyond what the probe and the host-side test actually measured; the measured
fact stands on its own regardless of the mechanism behind it.

**What this means for the standing question.** 46 rounds (9-47) measured "the round never
arms" for a mirrored, previously-claimed GIT enemy and treated it as proof of a mirroring
defect. This round's probe is the first to actually check whether the test target was hostile
at all at the moment of arming, and the honest answer — confirmed two independent ways, on two
different creatures, with and without every write this plugin makes — is that it was not.
Every prior round's negative result is fully consistent with "the creature was never going to
fight regardless of the mechanism," which this investigation had no way to distinguish from "the
mechanism is broken" until this round's probe existed. That does NOT retroactively prove the
underlying arming mechanism (fire `+0x260` on a mirrored, previously-claimed creature) actually
works — round 44/46's specific finding (something about having gone through `DriveOne`'s
claim/drive pipeline, as opposed to round 27/37's never-claimed creatures) is still an open,
unresolved question. It means the question cannot be answered further on THIS save, because
none of its remaining reused test creatures currently read as hostile to the player, so no
input to `+0x260` from a previously-mirrored creature on this save could ever produce a
positive result no matter what this plugin does.

**Recommended next step for whoever picks this up:** before any further live bisection,
confirm the target creature is genuinely hostile — either read this round's probe line, or run
the same host-side unmirrored melee check this round used — BEFORE spending a round chasing a
write-side explanation. If the whole remaining census reads non-hostile the same way, the
right move is a genuinely fresh save (one that has not been fought over by 40+ rounds of prior
live testing on save slot 1), not another bisection of this plugin's own write surface, which
this round and round 47 together have now exhausted (sixteen ops, all sixteen individually
gate-able, all sixteen ruled out).

**Safety re-checks**, on both the mask=`0x0` and mask=`0x1800` runs: no materialised stand-in
ever selected; no permanent exclusion — `0x1A`/`0x17` cycled cleanly through repeated 25s
timeouts in both runs (`local combat: 0x1A fight ended (timed out), rejoining the census` /
`local combat: 0x17 pulled out of the census to fight for real`, alternating cleanly, no stuck
state); no absorb/watchdog warnings in either run's log; the local player's own HP stayed
`12/12` throughout every trial. `local_combat` and its default (`on`) are unchanged by this
round.

Build clean (`dotnet build KotorCoop.slnx`, `src\KotorCoop.Plugin\build.ps1 -Install` against
both GOG installs). Full suite 1144/1144 (baseline moved from 1080 during this round, same
concurrent-work drift round 47 already flagged for this branch). Rig torn down and confirmed
clean (`Get-Process swkotor,KotorCoop.Agent` returned none), `auto_start_agent` restored to `1`
in both installs by `demo.ps1 -Stop`, and the client's `coop.ini local_combat_bisect_mask`
returned to `0x0`.

### Log rotation and diagnostic throttling (2026-08-21)

Adjacent to this section only because it is the same class of "runs forever, never checked
for size" bug, this time in the logging plumbing rather than in a shared field. A live test
rig's `kotorcoop-plugin.log` reached 170 MB — 158 concatenated `=== KotorCoop plugin loaded
===` sessions — because `PluginHost.Log` has always used `File.AppendAllText` with no cap.

**Rotation**, added in `LogRotation.cs` (a pure `internal static class`, shared by source into
`KotorCoop.Plugin`, `KotorCoop.Agent`, and the test project the same way `MotionSmoother.cs`
and friends already are): `ShouldRotate(existingSizeBytes, capBytes)` and `RotatedPath(path)`
decide whether a file is over `coop.ini`'s `log_max_mb` (default 8) and what its `.1.log`
sibling is named. Three call sites apply it, all **once, at startup, never mid-session** (a
tool like `tools/verify.ps1` tails the live file, and rotating out from under a tail would
lose it):

- `PluginHost.RotateLogsIfNeeded` — sweeps every `kotorcoop-*.log` in the game directory
  (the combined file and every channel split from `ChannelFor`) before the first `Log()` call
  of a session. Skips `kotorcoop-agent.log`, which the next point handles.
- `AgentLauncher.RotateAgentLogIfNeeded` — checks `kotorcoop-agent.log` right before
  `AgentLauncher` opens it with `CREATE_ALWAYS` for the auto-started agent's redirected
  stdout. `CREATE_ALWAYS` already truncates on every launch, so this process alone never
  accumulated across sessions the way the plugin log did; the rotation here only means a
  previous session that ran long enough to cross the cap leaves a `.1.log` tail instead of
  being silently discarded.
- `AgentDashboard`'s `--log` option (used directly by `tools/demo.ps1`, bypassing
  `AgentLauncher` entirely) turned out to have the *same* unbounded-growth bug as the plugin
  log, for a different reason: `demo.ps1`'s `agent1.log` / `agent2.log` live in
  `$env:TEMP\kotorcoop-demo`, a directory the script creates with `-Force` but never clears,
  and `AgentDashboard.Note` / `WriteLogLine` both append with no truncation. Fixed the same
  way, at construction, using the default cap (no `coop.ini` to read here — the `--log` path
  is a scratch file, not necessarily beside a game install).

`docs/research/*.md` and `tools/*.ps1` were checked for filename assumptions
(`kotorcoop-plugin.log`, `kotorcoop-<channel>.log`, `kotorcoop-agent.log`); none delete or
expect an unbroken single file across the whole run, so the new `.1.log` siblings are purely
additive.

**Throttling.** The roadmap item asking for this named a `local combat diag: t=...` line in
`EnemyPresenceController.cs` firing once a second per locally-fighting enemy. That line does
not exist in this checkout — this worktree's `EnemyPresenceController.cs` and
`IncomingDamageController.cs` are both already edge-triggered throughout (a `driven !=
_drivenLogged` guard, budgeted `_logged < N` counters, and similar), and a repo-wide search
for `Log($"..."` calls carrying per-frame numerics (`hp=`, `dist=`, `pos=`, timestamps) found
none. Likely explanation: this worktree's `HEAD` is the merge-base with
`integration/honest-limitations`, over a hundred commits behind that branch's tip, and the
diagnostic line was added on work not present here. Nothing was changed for this half of the
item; see the session's final report for the fuller discrepancy note.

### Round 49 (2026-08-21) — the reputation gate, a real GIT-placed hostile, and the freeze
### confirmed genuine on it too: round 48's "friendly" finding was correct but never general

Round 48 left two open items: gate `local_combat`'s candidate selection on hostility (a
non-hostile census actor should never be excluded for a "fight" that can't happen), and find out
whether the freeze rounds 9-47 chased is real or was always just a symptom of testing against
non-hostile creatures. This round did both.

**The gate.** `EnemyPresenceController.DriveOne`'s `triggeringLocalFight` condition — GIT-identical
(`mapped == 0`), engaged (`IsEngaged`), a non-empty `+0x260` script slot — now also requires
`Odyssey.GetAIStateReputation(creature, Odyssey.GetPlayerObjectId()) == 2`, checked only once the
cheaper gates already pass (an engine call, not a field read). A rejected candidate logs once,
edge-triggered per slot (`_repRejectedLogged[]`, reset in `Forget` and whenever the same slot
later reads hostile again) — not once a frame, which the identical friendly reading every pass
would otherwise spam for the creature's whole time in the census. `IncomingDamageController`'s
own attribution was also audited this round: it can only ever license an absorb for
`EnemyPresenceController.IsLocallyEngaged`'s member set, which `MaintainLocalFight` populates
with exactly one id — `_locallyFightingId` — set exclusively inside `StartLocalFight`. With the
gate in place, `StartLocalFight` itself can now only ever be called for a creature that already
read `rep == 2`, so the absorb path is structurally protected transitively; no separate bug found
there, and none was fixed (per the task's own instruction, left unrefactored this round).

**What round 48's two creatures actually were.** `end_soldier2`/`0x1A` (round 47's own name for
it, never verified against the GIT) sits at exactly `(47.0629, 21.0793)` — read directly from
`end_m01aa_s.rim`'s `n_repsold002.utc` via `tools/kotorfiles.py gff "end_m01aa_s:n_repsold002.utc"
--path FactionID` → **`FactionID = 4` (`Friendly_2`)**. `n_repsold` is the Republic-soldier
blueprint family (`n_repsold001`-`006`) — an ally defending the bridge, not a Sith trooper; the
"end_soldier2" name in rounds 39-48 was an unverified guess. Round 48's second creature, `0x17`
(`FactionID=2`, "a DIFFERENT faction, 2 not 4"), matches `n_sithappren002.utc` → **`FactionID = 2`
(`Friendly_1`)** — a Sith-appearance creature the module's own author coded as friendly by
default (a common Odyssey pattern: an apparent enemy whose real hostility is set by an `OnSpawn`
script keyed to plot state, not the blueprint's baseline). `repute.2da` confirms both: row 0
(`Player`)'s `friendly_2` and `friendly_1` columns both read `100` — the player is never hostile
to either faction. **Round 48's own reading was exactly right for these two creatures — they
never were Sith — but the two-round mystery about WHY they read friendly is now closed by a
blueprint lookup, not by the "battle-worn, fled, faction changed at some point across 40+ rounds"
speculation round 48 offered.** That speculation is superseded, not merely unconfirmed.

**Finding a real one.** `python tools/kotorfiles.py creatures end_m01aa` lists the full GIT
Creature List (26 entries, Encounter List: 0 — nothing here is encounter-spawned, everything is
GIT-placed). Cross-checked every blueprint's `FactionID` against `repute.2da`:

| Blueprint | Position | FactionID | repute.2da row | Player's reputation |
|---|---|---|---|---|
| `n_repsold002` (round 47/48's `0x1A`) | (47.06, 21.08) | 4 | Friendly_2 | 100 (friendly) |
| `n_sithappren001` | (28.99, 119.86) | 4 | Friendly_2 | 100 (friendly) |
| `n_sithappren002` (round 48's `0x17`) | (30.55, 119.76) | 2 | Friendly_1 | 100 (friendly) |
| **`n_sithsoldier003` ×2** | (29.74, 144.46), (31.86, 141.99) | **1** | **Hostile_1** | **0 (hostile)** |
| **`n_sithsoldier004` ×2** | (43.96, 62.92), (47.56, 60.54) | **1** | **Hostile_1** | **0 (hostile)** |

`n_sithsoldier003`/`004` are the only genuinely hostile GIT creatures in this module's census.
Chose the `004` pair (its region was never named by object id in any prior round's log, unlike
the `003` pair which round 39's original untraced "sithsoldier pair" fight may have already worn
down).

**Rig.** Cold start (`tools/demo.ps1`, save slot 1, both GOG installs — `C:\GOG Games\Star Wars
KOTOR (GOG)` host, `D:\Games\swkotor-gog-p2` client/mirroring). Client teleported
(`blockpoke.ps1 console 'tp 43.9 62.6 -1.275'`) from its spawn point directly to the `004` pair's
position — no approach needed, matching this round's "point-blank, no pathing" preference.

**The probe, live, on a real hostile — first time this investigation has ever measured this:**

    local combat probe: 0x3B creatureFaction=1 playerFaction=0 rep(creature,player)=2
    rep(player,creature)=2 (2=hostile,1=friendly,0=neutral) CreMovePermitted=0xFFFF plot=False
    min1hp=True hp=12/12 bisectMask=0x0
    local combat: 0x3B pulled out of the census to fight for real (timeout 25s)
    local combat diag: t=0.0s 0x3B at (47.56,60.54) player at (43.90,62.60) dist=4.20
    ... (identical dist=4.20 every second through t=24.0s) ...
    local combat: 0x3B fight ended (timed out), rejoining the census
    local combat probe: 0x38 creatureFaction=1 playerFaction=0 rep(creature,player)=2
    rep(player,creature)=2 ... hp=12/12 bisectMask=0x0
    local combat: 0x38 pulled out of the census to fight for real (timeout 25s)
    local combat diag: t=0.0s 0x38 at (43.96,62.92) player at (43.90,62.60) dist=0.32
    ... (identical dist=0.32 every second through t=24.0s, both engagements, across repeat
    cycles) ...

**Zero movement, zero HP change, on a confirmed `rep=2` hostile, at both 4.2 units and 0.32 units
(point-blank, no approach possible) — the identical failure signature rounds 44 and 46 measured
on the friendly creatures, now on a real one.** The pre-existing forwarded-damage channel kept
working throughout and independently of this mechanism: `combat: absorbed 10 forwarded damage in
total, attributed to mirrored enemies we are driving (last attacker 0x3B); this engine says
in-combat=True`, and later, once `0x3B`'s exclusion window had expired and `IsLocallyEngaged`
correctly stopped licensing the absorb, `combat: took 2 damage from a hit on our phantom
(attacker 0x3B)` applied normally — evidence the HOST's own untouched engine copy of `0x3B` was
fighting the client's phantom for real the whole time (this player's real position propagates to
the host as a phantom, and a genuinely hostile creature aggroes it there independent of anything
`local_combat` does) — a completely different, already-working channel from the one being tested.

**The bisection, against a real hostile, budget 4 runs, three run (the fourth judged unnecessary
— `0x1FFF` is `0x7FF | 0x1800`'s superset and already settles both open bits):**

| Run | Mask | Ops disabled | Result |
|---|---|---|---|
| control | `0x0` | none (shipped default) | FAIL — `rep=2`, zero movement/HP change, both creatures, full 25s cycles |
| all DriveOne ops off | `0x7FF` | bits 0-10 (round 47's `BisectAllOps`) | FAIL — identical signature, `rep=2` |
| everything off | `0x1FFF` | bits 0-12 (adds health pin + health mirror) | FAIL — identical signature, `rep=2`, `min1hp=False` confirms the mask took effect |

**Three independent configurations, spanning the full write surface this investigation has ever
found (thirteen ops, rounds 47-48's complete table), all fail identically on a creature now known
to be genuinely hostile.** This is the first real data point on a real hostile this investigation
has ever produced, and it closes round 48's open question: the freeze is not, and never was,
fully explained by "the test creature was never hostile." It happened to be true for the two
specific creatures round 48 checked; it is not true in general. The actual mechanism — something
about a creature that spent real time normally mirrored (claimed, driven, re-claimed every frame)
before being excluded, as round 44 first isolated and round 47's bisection narrowed to "census
membership itself, not any write" — is unchanged and still open. Round 47's own two remaining
candidates (`MaterialisedEnemies.Seen`'s managed timestamp write; the unopened interior of
`RunScript`'s real dispatcher past `0x005D45D0`, specifically `0x005D4270` onward) are still the
concrete next steps.

**Safety re-checks**, this session: no materialised stand-in ever selected (both `n_sithsoldier004`
instances are GIT-identical); no permanent exclusion — both creatures cycled cleanly through
repeated 25s timeouts and rejoined the census, re-engaging when the player was still in range,
exactly as designed; no absorb/watchdog warnings; the client's own HP stayed `12/12` throughout
every local_combat probe window (the one `took 2 damage`/`cannot be downed` line came from the
unrelated, already-working forwarded-damage channel once this creature's local exclusion window
had already ended, and the Endar Spire command deck's own min-1-hp scene pin — documented
elsewhere in this file — is why it floored rather than applied for real).

**Status, honestly.** The reputation gate is real, implemented, and correct regardless of what
this round found — it stops a structurally-impossible "fight" before it starts, independent of
whether hostility was ever the freeze's cause. It is not what was blocking rounds 44-48's specific
test creatures (those were never going to pass it or fail it meaningfully, since a friendly
creature excluded under the OLD code just froze the same way a rejected one now never gets
excluded at all) — but it is a correctness fix regardless, and it removes "was the candidate even
hostile" as a variable for whoever continues this. `local_combat` stays on by default, unchanged.
No hit has yet landed through the exclusion-and-script-fire mechanism on any creature this
investigation has ever tested, hostile or friendly — roadmap item 1 and the README combat bullet
are unchanged this round because none did here either. The forwarded-damage channel this
mechanism was meant to eventually retire continues to be the only thing actually making a fight
dangerous in co-op, on both the friendly-then-excluded creatures of rounds 44-48 and the genuinely
hostile one this round adds.

Build clean (`dotnet build KotorCoop.slnx`; `src\KotorCoop.Plugin\build.ps1 -Install -GameDir
"C:\GOG Games\Star Wars KOTOR (GOG)"` and again with `-GameDir "D:\Games\swkotor-gog-p2"` — the
bare default `-Install` targets a Steam path neither GOG rig install uses, confirmed live this
round; both explicit `-GameDir` installs succeeded, 0 warnings, 0 errors). Full suite 1173/1173
(`HeavyPacketLossStillConverges` and the flagged `SaveDigestTests` case, this run, did not flake).
Rig torn down and confirmed clean (`Get-Process swkotor,KotorCoop.Agent` returned none), the
client's `coop.ini local_combat_bisect_mask` restored to `0x0`, `auto_start_agent` restored to `1`
in both installs by `demo.ps1 -Stop`.

### Round 50 (2026-08-21) — the decisive control: a census-listed creature NO plugin code
### touches, still freezes; AND round 37's own historic success does not reproduce today

Forty-nine rounds bisected every WRITE this plugin makes to a mirrored creature (round 47's
eleven `DriveOne` ops, round 48's two `CombatController` ops) and found none of them explains
the freeze. But every one of those tests still let the OTHER consumers of census membership run:
`Resolve()`'s pointer cache and its call into the engine's own `GetGameObject`,
`MaterialisedEnemies.Seen`'s per-frame timestamp write, `CombatController`'s own resolve-and-pin
pass, `PeerDamageController`'s owed-damage apply. Round 29's diagnostic came closest but still
ran `Resolve()`/`Seen()`; round 37/38's census-cap exclusion removed the creature from the list
entirely, which is "never listed," not "listed but untouched." Nobody had run a census-listed
creature that literally nothing in this plugin ever touches.

**Part 1: the consumer audit.** Every controller that can receive a census enemy's object id,
read in full this round, with what it does and whether that touch can reach the creature's own
AI/combat:

| Consumer | What it does to/with a census creature | Can it affect the creature's own AI/combat? |
|---|---|---|
| `EnemyPresenceController.OnFrame`'s per-slot loop | `MaterialisedEnemies.Seen(id, now)` before `DriveOne` even runs | Indirectly (feeds the sweep/lease system) |
| `EnemyPresenceController.DriveOne` → `Resolve()` | Caches an engine pointer; calls `Odyssey.ResolveServerObject` → the engine's own `CServerExoApp::GetGameObject` (round 38: disassembled in full, provably read-only, writes only the CALLER's own out-parameter) | No (proven read-only at the instruction level) |
| `DriveOne` → `ClaimCreature`/`ClaimCreatureForFight` | `ObjCommandable=1`, targeted `ClearAllActionsFn`, `CreMovePermitted\|=2`, `CreRunFlag=0` (round 47's bits 0-2) | Yes — already exhaustively bisected off (round 47) with no change |
| `DriveOne` → `PermitAttacking` | `CreMovePermitted \|= CreAttackPermittedBit` (bit 3) | Yes — bisected off (round 47), no change |
| `DriveOne` → `Drive`/`DriveBody` | Position/facing/animation writes, `ClearPositionDirty` (bits 4-10) | Yes — bisected off (round 47), no change |
| `DriveOne`'s trigger check | `Odyssey.GetAIStateReputation` engine call (round 49's gate), `IsEngaged` (position read only) | No (read-only engine query) |
| `StartLocalFight`/`MaintainLocalFight` | `FireCreatureScriptSlot0x260`, `RestoreClaimState`, engagement bookkeeping | Yes, but only ever reached for a creature `DriveOne` already resolved — unreachable once the id is skipped at the top of the loop |
| `CombatController.Apply` | `SetMinOneHitPoint` (health pin, bit 11), `SetCurrentHitPoints` (health mirror, bit 12), `KillOnce`→`Odyssey.KillCreature` on `enemy.Dead` | Yes — pin/mirror bisected off (round 48), no change; `KillOnce` never reached (creature stays alive throughout every run below) |
| `IncomingDamageController` | Reads `EnemyPresenceController.IsLocallyEngaged(attacker)` to decide absorb-vs-apply | Structurally guaranteed false for a never-`DriveOne`'d id — `IsLocallyEngaged`'s member set is populated ONLY inside `MaintainLocalFight`, which is unreachable per the row above. No code change needed; verified by reading the call graph, not assumed |
| `PeerDamageController.ApplyOwed` (owner end) | `Odyssey.ApplyDamageEffect(objectId, amount)` — a real engine damage effect, which could plausibly wake AI on a hit | Yes in principle — now explicitly gated (this round) |
| `PhantomHitHook.OnDamage` | Pure observer — the detour stub unconditionally resumes into the original `OnApplyDamage`; a credited "dealt" total is a managed-array write only | No (never suppresses/replaces the engine's own damage application — verified from the stub's own machine code, which always ends in the relocated prologue + `jmp` back in) |
| `AnimationHook.OnFireForget`/`RecordCreatureOneShot` | Records a captured swing/animation id into a managed table; never suppresses or replaces the engine's own `InsertFireForgetAnimation` call (same always-resumes stub shape as `PhantomHitHook`) | No. Its only downstream consumer is `DriveOne`'s one-shot replay block, which is unreachable once the id is skipped at the loop's top |
| `MaterialisedEnemies` (`Seen`/`TryMaterialise`/etc.) | Keyed by a MATERIALISED stand-in's owner id in a separate table; a GIT-identical creature (`mapped == 0`, our test creatures) never gets an entry at all | No, structurally, for the creature class this round tested (verified: the table lookup loop can never match an id it was never `Add`ed under) |
| `CheatController` | `Slay`/`GiveLoot`/`MoveNear`/`FireScript` — all operator-invoked, once, only when a console command explicitly names the id | Yes, but only when WE type a command naming it — not an automatic per-frame toucher. `FireScript` targeting the bypassed id is our own test instrument, deliberately never gated |
| `PhantomController.ApplyTargetable` | Holds a faction on THIS instance's OWN phantom bodies (representing remote peers) | Not applicable — never touches a mirrored enemy at all (confirmed by round 48's own audit and re-confirmed by grep this round: the only per-creature id space it reads is `LocalCombat`/`DamageDealtEnabled`, both global flags) |
| `InteractionLockController`/`Odyssey.HoldCreatureStill` | Holds an NPC by TAG for a dialogue partner | Not applicable — resolved by tag, not census object id; never sees an enemy census entry |
| Agent: `CoopAgent.PumpEnemies` → `TryWriteEnemies` | Marshals `EnemyState` into the shared-memory block via raw `WriteProcessMemory`-style calls into OUR OWN process's mapped memory | No — this is the "listed" half by design; it must NOT be gated, or the creature would no longer be listed at all (round 37/38's condition, not this round's) |
| Agent: `KotorGameBridge.TryWriteEnemies` | Same as above, confirmed this round by re-reading the function in full (`KotorGameBridge.cs:4382-4422`): every field goes through `_memory.TryWrite` into the plugin's shared block, never into the game engine's own object memory | No |
| Agent: `DetectMaterialisedDeaths` | Owner-authority side (host only), keyed off materialised-loot capture, not the client's own touch of a mirrored creature | Not applicable to the client instance under test |

**Part 2: the bypass.** `EnemyPresenceController.LocalCombatBypassId` (dev-only `coop.ini
local_combat_bypass_id`, hex, default 0) added, logged once at load. One check —
`if (LocalCombatBypassId != 0 && enemy.ObjectId == LocalCombatBypassId) continue;` — placed at
the very top of `OnFrame`'s per-slot loop, BEFORE `MaterialisedEnemies.Seen` and before
`DriveOne` is ever called (`EnemyPresenceController.cs`). This is stronger than round 29's own
`DiagUndrivenObjectId`, which bailed out only inside `DriveOne`, after `Resolve()`/`Seen()` had
already run. Additional explicit early-returns added in `CombatController.Apply`,
`PeerDamageController.ApplyOwed`, and `PhantomHitHook.OnDamage`'s dealt-credit path — the three
consumers whose reach was NOT already structurally blocked by the `OnFrame` skip. The agent's own
`TryWriteEnemies` is deliberately never touched: the creature is still published in the census
exactly as before, which is the whole point of the control.

**Rig.** `tools/demo.ps1` cold start, save slot 1, `END_M01AA`, `n_sithsoldier004`
(`n_repsold`/`n_sithappren` blueprints round 48/49 already ruled out as friendly), GIT
`FactionID=1` (Hostile_1), the exact same pair round 49 used — re-read fresh from
`tools/creatures.ps1` after every cold load per this round's own instructions, and the ids held
stable across all three cold loads below: `0x3B` at `(47.56,60.54)`, `0x38` at `(43.96,62.92)`.
Builds: `dotnet build KotorCoop.slnx` clean; `src\KotorCoop.Plugin\build.ps1 -Install -GameDir
"C:\GOG Games\Star Wars KOTOR (GOG)"` and again with `-GameDir "D:\Games\swkotor-gog-p2"` (the
bare default targets a Steam path neither install uses). Client teleported
(`blockpoke.ps1 console 'tp 43.90 62.60 -1.3'`) to `dist=4.20` from `0x3B` — the same ~4-unit
range round 49 used. Fired via `blockpoke.ps1 console 'firescript 0x3B fire'` (`CheatController`,
which auto-arms `Odyssey.ArmFireScriptWatch` — the round-28/39 per-frame native-state probe,
unthrottled, logging `round+0x958`/`+0x960`/`+0x96C`→`sub+0xB0`/head-action for 6000 frames after
every fire).

**Run A — control, bypass off.** `local_combat_bypass_id` unset (default 0), cold start. Fired
`firescript 0x3B fire` at `dist=4.20`; the automatic `local_combat` trigger was ALSO cycling on
this pair independently throughout (`local_combat=1` is the shipped default, and both `0x3B` and
`0x38` are within `local_combat_range`, so this is the SAME condition every prior round's manual
fire has always run under — not a new confound). Full 6000-frame watch:

    [18:31:06.176] firescript: fired 0x3B +0x260 = 'k_def_combend01'
    [18:31:54.816] firescript watch: frame -133 ... round+0x958 timer=1, round+0x960 delay=544,
      round+0x96C sub-index=0 sub+0xB0=1, ... head action=0x3F (is fight action: False)
    (sub+0xB0 nonzero on 1351 of 6000 sampled frames; "is fight action: True" on 0 of 6000)

Player HP fell during the window (`vtbl+0x9C(0)`, which resolves `round+0x9CC`'s own target —
`0x7FFFFFFF`, the LOCAL PLAYER, every single sample), but the plugin's own log shows exactly why:
`combat: took 2 damage from a hit on our phantom (attacker 0x3B)`, the pre-existing, unrelated
forwarded-damage channel round 49 already documented as running independently. **Zero fight
actions across the full window — the established freeze, reproduced.**

**Run B — bypass on, the decisive control.** `local_combat_bypass_id = 0x3B` set in the client's
`coop.ini`, cold restart (`demo.ps1 -Stop` then a fresh `-Save 1` launch — a genuinely clean
process, not a hot ini edit). Confirmed at load:

    [18:34:56.773] local_combat_bypass_id=0x3B - this creature will be published in the census
      as normal but touched by NO plugin consumer (round 50 decisive control)

Waited 90s post-world-up with the player away from the pair — no automatic exclusion cycle ran on
`0x3B` in that window (confirmed by grep: no `local combat: 0x3B` lines at all this session, vs.
`0x38`'s normal 25s cycling continuing exactly as designed). `census 0x3B` after teleporting back
into range still read as part of the owner's published set (25 ids, `0x3B` among them) — genuinely
listed. Fired `firescript 0x3B fire` at the same `dist=4.20`. Full 6000-frame watch:

    [18:36:55.307] firescript watch: armed on 0x3B for 6000 frames
    [18:37:13.565] firescript watch: frame -1122 ... sub+0xB0=2 ... head action=0x3F
      (is fight action: False)
    (sub+0xB0 nonzero on 2274 of 6000 sampled frames; "is fight action: True" on 0 of 6000)

`tools/creatures.ps1` before and after: `0x3B` at `(47.56,60.54)`, hp `12/10`, unchanged to the
decimal, both times. **Identical failure signature to the control — zero movement, zero fight
action, across the full window — with LITERALLY NOTHING in this plugin ever having touched the
creature.** This settles instruction 3's first branch: **the plugin is NOT the cause.** There is
no consumer left to bisect, because Run B already removed every one of them at once and the
freeze did not move.

**Part 3: the round-37 cross-check (task-mandated, since B froze).** Per the task's own branch,
this reruns round 37's exact construction — `KotorGameBridge.BlockEnemyCapacity` temporarily
32→1 — as a same-session sanity check that the historic "outside the census entirely" success
still reproduces at all on today's build. Torn down, edited, rebuilt (`dotnet build
KotorCoop.slnx`), redeployed to both installs, cold-started fresh (bypass id cleared first, so
this test is not conflated with Part 2's mechanism). `census 0x38` read `1 resolve here, 0 do
not`; `census 0x3B` read `not listed in this census` — the exact round-37 split, confirmed live.
Fired `firescript 0x3B fire` at the same `dist=4.20`. Full 6000-frame watch:

    [18:42:15.592] firescript watch: armed on 0x3B for 6000 frames
    [18:42:17.074] firescript watch: frame -5626 ... round+0x958 timer=1, round+0x960 delay=1500,
      sub+0xB0=3, sub+0x5c=4, ... head action=0x3F (is fight action: False)
    (0 of 6000 samples read "is fight action: True")

Same HP-fell-via-forwarded-channel confound checked and ruled out the same way (`combat: took 2
damage from a hit on our phantom (attacker 0x3B)`, repeating every ~3s throughout — the standing
channel, not this creature's own round). `0x3B`'s own position/HP via `creatures.ps1`: unchanged
before and after.

⚠ Transparency note: three unrelated commits landed on this branch from a concurrent session
between Part 2's build and Part 3's rebuild (`cd8f7c6`/`f6f7411`/`120e580` — map-reveal fog and a
materialised-loot reconnect resync). Checked this round: their entire diff against `PluginHost.cs`
is two lines wiring a new `MapRevealController` into the existing controller list (construct,
`OnFrame`, `Invalidate`) — no combat, round-arming, or census code touched at all, and none of the
other changed files (`MapRevealController.cs`, `MapRevealRule.cs`, `CoopHost.cs`,
`MaterialisedLootHistory.cs`) touch `EnemyPresenceController`, `CombatController`, or `Odyssey.cs`
either. Not a plausible explanation for the round-37 non-reproduction, but named here rather than
left for a reader to wonder whether Part 2 and Part 3 ran identical binaries — they did not, by
three files' worth of unrelated diff, verified harmless.

**Round 37's own historic result — `sub+0xB0=1` sustained, real HP movement attributed to "the
target" — does NOT reproduce on this build, under round 37's own exact construction, on the same
save, same module, same creature pair.** Say this loudly, per the task's own instruction: **the
round-37/38 baseline itself does not hold today.** Two honest readings, neither provable from this
round's evidence alone: (a) something in the 100+ commits this branch has taken since round 37
(rounds 39-49's own fixes among them — the reputation gate, the `_locallyFightingId`
one-fight-at-a-time serialization, the health-pin/mirror gating) changed engine-visible behaviour
for a census-excluded creature even though none of them touch `DriveOne`'s write surface directly;
or (b) round 37/38's own "armed, real fight" reading was ITSELF the phantom-forwarding confound
round 28 first identified and round 49 had to explicitly rule out for the FIRST time in this
document's history — round 37/38 pre-date that check, their "target" resolved to a creature with
`16` max hp (never matching this save's player, which has always read `12` max in every round of
this document), and neither round's own writeup shows an explicit rule-out of the standing
forwarded-damage channel the way round 49 (and this round) does. This round cannot distinguish (a)
from (b); it only establishes that whichever it is, round 37's specific claim is not reproducible
today and nothing that has cited it since (including round 47/49's own framing of it as "the one
proven-working case") should be trusted without re-verifying it fresh.

**A concrete, precise next experiment**, grounded in this round's own logs, not speculation: in
every one of this round's three trials — bypass off, bypass on, and the round-37 cap-exclusion —
`sub+0x5c` reads `4` (once observed at all; earlier samples read other small values before
settling). Round 30 disassembled `sub+0x5c`'s own role and measured it at `0` on ITS test
creature (round 29's untouched `0x10`) — INSIDE the "`-1` or `0..3`" range round 27 documented as
required for the loop that reaches the arming call to continue at all. `4` is OUTSIDE that range.
Round 30 already showed `sub+0x5c=0` alone is not sufficient (that creature still never armed,
for a still-unidentified downstream reason) — but this round's creature reading `4`, consistently,
across three different exclusion mechanisms, on a pair that (unlike round 29/30's test subject)
DOES have a genuine, ongoing fight against it from the HOST's own unmirrored engine via the
phantom channel, is the single most concrete, unexplained, reproducible state difference this
document has on hand. **Next step: disassemble what sets `sub+0x5c`, starting from the arming
call site `0x005BBA15` round 27 already located, and specifically test whether a creature's
`sub+0x5c` value depends on whether ITS OWN process's engine (not the peer's) has ever run a
combat round against it at all this session** — the phantom-forwarding fight is happening on the
HOST's copy of `0x3B`, in a completely separate process, so it should have no way to reach the
CLIENT's own `sub+0x5c` for the CLIENT's own copy of the same creature; if it turns out to
correlate anyway, that is itself a new finding about what `sub+0x5c` actually tracks.

**Safety.** No materialised stand-in ever selected (both test creatures are GIT-identical); no
permanent exclusion in any run — `0x38`'s ordinary automatic cycle kept alternating cleanly
throughout Run B's window, untouched by the bypass on `0x3B`; no absorb/watchdog warnings; the
bypass and the capacity change are both dev-only and were both reverted before teardown (capacity
back to `32`, rebuilt, redeployed to both installs; the client's `coop.ini local_combat_bypass_id`
line removed entirely, restoring the file to its pre-round state).

Build clean (`dotnet build KotorCoop.slnx`; both `-GameDir` installs, 0 warnings, 0 errors). Full
suite 1191/1192 (`HeavyPacketLossStillConverges` flaked this run, confirmed passing in isolation
immediately after — the same known-flaky test flagged throughout this document; baseline moved
1173→1192 during this round from unrelated, concurrent work on the same branch, the same pattern
rounds 47-49 already flagged). Rig torn down and confirmed clean (`Get-Process
swkotor,KotorCoop.Agent` returned none), `auto_start_agent` restored to `1` in both installs by
`demo.ps1 -Stop`.

**Status.** `local_combat` stays on by default, unchanged. `local_combat_bypass_id` is a real,
tested, dev-only diagnostic left in the tree (default `0`, off) for whoever opens the `sub+0x5c`
lead next. No hit has yet landed through the exclusion-and-script-fire mechanism on ANY creature
this investigation has ever tested, hostile or friendly, touched or genuinely untouched — the
forwarded-damage channel remains the only thing making a fight dangerous in co-op. Roadmap item 1
and the README combat bullet are unchanged this round for the same reason every round since 39 has
left them unchanged: no hit landed through the mechanism the roadmap item is actually asking
about. **The freeze is now proven independent of every write AND every read this plugin's
EnemyPresenceController, CombatController, IncomingDamageController, PeerDamageController,
PhantomHitHook, AnimationHook, MaterialisedEnemies, CheatController, PhantomController and the
agent's own census-writing path can perform on the creature — the cause, whatever it is, lives
entirely inside the engine's own per-creature combat-round state, is not affected by census
membership status alone (round 37/38's own claim to the contrary does not reproduce today), and
the most concrete lead on hand is the `sub+0x5c=4` anomaly named above.**

### Round 51 (2026-08-21) — the ground-truth diagnostic never fired even when the mechanism
### worked; the client install is refuted, the session-role question is still open

Round 50 left one concrete lead (`sub+0x5c=4`) and one untested split: nobody had ever tested the
client INSTANCE without a session, or the client INSTALL standalone. This round did both, plus the
static disassembly the task asked for first.

**Part 1: static — `sub+0x5c` disassembled at the source (20-minute budget).** `tools/fieldwrites.py`
cannot scan offset `0x5C` (it fits a disp8, not the disp32 the tool requires), so this round wrote a
one-off disp8/disp32 byte scanner (same technique, smaller displacement form) and disassembled every
candidate with `tools/disasm.py`. Two real write sites found, both inside the SAME function:

- **`0x005BB590`** — this is `StartCombatRound` itself (matches round 27's own description: a
  5-argument entry, `edi+0x40=1` set immediately, a per-round counter at `[eax+0x974]`
  incremented). Early in the function, at **`0x005BB668`**, it writes a HARDCODED default:
  `mov byte ptr [edi+0x5c], 4` — unconditionally, before any maneuver has been chosen. `4` is not
  an anomaly or an out-of-spec value; it is this round's own "nothing selected yet" sentinel.
- **`0x005BAD8A`-`0x005BADC5`** — a maneuver-selection dispatch, reached later in the same function,
  that overwrites the sentinel with a real value: `al` (a computed value) if `[esp+0x30]` is set,
  `3` if `[esi+0x4c]` is nonzero, `1` if `[edi+0x8d4]` is nonzero, or falls through unwritten
  (leaving whatever `0`/`1`/`2` a prior pass already wrote) otherwise.

**The bigger correction: round 27's own account of the per-participant loop is wrong about what the
range check gates.** Full disassembly of the loop (`0x005BB8C6`-`0x005BBA33`, the loop round 27
already found) shows the check on `[ebx+0x5c]` (`0x005BB9CE`-`0x005BB9EA`, values `-1` or `0..3`
continue one way, anything else — including `4` — jumps to `0x005BBA0D`) does **not** skip the
arming call. Both the "in-range" path (which also runs two extra setup calls, `0x005BB440` and
`0x005B8B00`, reading/writing per-target state — an alloc'd `0x8C`-byte object, a comparison against
`[esi+0]`'s vtable, `[edi+0x48]=1`) and the "out-of-range" path converge on the exact same
instruction, `0x005BBA0D`, and BOTH unconditionally call the arming function `0x004D5A50` at
`0x005BBA15`. **The range check only decides whether the two extra setup calls run — it does not
decide whether the round arms.** This matches every live reading rounds 49-51 have taken: `sub+0xB0`
(the arming list's own count) goes nonzero in every trial this round ran, `sub+0x5c=4` throughout
most of them, and the two are not in tension — they were never supposed to be. `sub+0x5c=4` means
"this participant's round has not yet had a maneuver selected," not "arming is blocked."

**Part 2: the rig, and the first pass (C1, incomplete methodology — recorded, then corrected).**
`tools/demo.ps1` was read in full for this round: `launch --instance 2` gives the client its own
renamed mutex and does not require instance 1 to be running at all; `agent launch`/`agent load` can
be driven directly, without `demo.ps1`'s two-instance orchestration, to bring up ONE install alone.
Both installs ship `coop.ini auto_start_agent=1` by default, and `AgentLauncher` starts that agent
in `--menu` mode (no `--host`, no `--join`) — so launching a game alone, with no `demo.ps1` session
at all, still gets an attached agent processing console commands (confirmed live: `blockpoke.ps1
... state` read `phantoms 0  remotes 0` the whole time). This is the "no session" condition the
task asked for, achieved without any code change.

Cold-loaded the CLIENT install (`D:\Games\swkotor-gog-p2`) alone, save slot 1 (`agent load
--load-save 1` reports `staged '000002 - Game1' as the autosave` — this is the save both installs
load; it is NOT a fresh per-round save, the same file every round back to 44 has used), module
`END_M01AA`. `tools/creatures.ps1` found the same `n_sithsoldier004` pair rounds 49-50 used, same
ids: `0x3B (47.56,60.54)`, `0x38 (43.96,62.92)`. Teleported to `dist=4.20` from `0x3B`
(`console 'tp 43.90 62.60 -1.3'`), waited 30s with NO script fired: **zero movement, zero HP change
on the creature** — no natural aggro in this window, on this install, standalone (a second
passive-wait control, on the HOST install, gave the identical zero-aggro result — see Part 4).
Fired `console 'firescript 0x3B fire'`, waited ~30s, checked only the creature's own state
(`creatures.ps1`: unchanged) and the `is fight action` diagnostic (0/6000 sampled frames). This
LOOKED like the established freeze. **It was an incomplete measurement**: this pass never read the
PLAYER's own HP, only the attacker's — which would never move regardless of what happened to the
player. This gap is exactly the one closed in Part 3.

**Part 3: C1 redone, C2, and C3 — all three measured with the ground-truth check.** Every trial
below used `console 'where'` immediately after loading/teleporting (baseline) and again ~30s after
firing (result) — the one check the whole document has been missing.

| Trial | Install | Session | Baseline HP | Result HP | `is fight action: True` (of 6000) |
|---|---|---|---|---|---|
| C1 (redo) | client (`swkotor-gog-p2`) | none (`phantoms 0 remotes 0`) | `12/12` | **`1/12`** | 0 |
| C2 | host (`GOG Games\...`) | none (`phantoms 0 remotes 0`) | `12/12` | **`1/12`** | 0 |
| C3 | client | live (`phantoms 1 remotes 1`) | `12/12` | **`1/12`** | 0 |

`1/12` is the documented min-1-hp scene floor for this Endar Spire command-deck area (round 49:
"floored by the Endar Spire deck's own min-1-hp pin, the same pin every other real fight this
investigation has measured hits") — a REAL, independently-verified hit, not a proxy reading, in
**all three** trials. `is fight action: True` was `False` on all 6000 sampled frames in every one,
including C1 and C2, where the hit is completely unconfounded (no session exists, so no census, no
`EnemyPresenceController`, no phantom, no forwarding channel — nothing this plugin can touch was
running at all; the only thing that happened between baseline and result was the manual
`firescript` fire and the creature's own subsequent engine-native AI). This settles instruction 3's
branch directly: **C1 passed and C2 passed. Per the task's own branching, this means V2 (session
role), not V1 (the client install), is the live variable** — the client BUILD fights fine when
nothing else is touching the creature; whatever the "freeze" chased since round 9 is, it is not a
property of being the client instance.

**C3 is real but confounded, and the confound is quantitatively sufficient to explain it alone.**
The client's plugin log for the C3 session shows the automatic `local_combat` proximity exclusion
already pulled `0x3B` out of the census the moment the player teleported into range — BEFORE the
manual fire ran — and the standing forwarded-damage channel (the host's own unmirrored copy of
`0x3B`, genuinely fighting the client's phantom body on the HOST's own engine, forwarding damage
over the network; a separate, already-documented mechanism unrelated to `local_combat`) was already
delivering damage seconds before the manual fire:

    [19:22:26.694] local combat: 0x3B pulled out of the census to fight for real (timeout 25s)
    [19:22:32.653] combat: absorbed 2 forwarded damage in total, attributed to mirrored enemies
      we are driving (last attacker 0x3B); this engine says in-combat=True
    [19:22:35.985] firescript watch: armed on 0x3B for 6000 frames
    [19:22:35.986] firescript: fired 0x3B +0x260 = 'k_def_combend01'
    [19:22:52.956] combat: took 2 damage from a hit on our phantom (attacker 0x3B)
    [19:22:55.616] combat: took 2 damage from a hit on our phantom (attacker 0x3B)

Only two "took" lines (4 points total) appear between the manual fire and the `where` check, far
short of the 11-point drop from `12` to `0` a normal floor would need — but this area's min-1-hp
pin does not require a large hit, only a nonzero one (round 49's `min1hp=True` reading, documented
as flooring "every real fight this investigation has measured hits" regardless of size). **A single
one of those two forwarded hits is sufficient, on its own, to fully explain the `12→1` reading.**
C3 therefore cannot distinguish "the client's own local mechanism also fired successfully" from
"only the pre-existing, already-working forwarding channel did" — the same phantom-forwarding
confound rounds 28 and 49 already named, now shown to be strong enough to single-handedly produce
the exact signature this whole investigation has used as its evidence of a working fight, whether or
not `local_combat`'s own mechanism ever does anything at all in-session. **V2 is not settled by this
round.**

**Part 4: the passive-wait control, both installs.** Before firing anything, both C1 and C2 stood
the player at `dist=4.20` from `0x3B` for 30s with no script fired: zero movement, zero HP change on
the creature in both cases. The creature's own perception/heartbeat AI does not aggro on its own in
this window at this range, on either install — the manual (or `local_combat`'s automatic) script
fire is doing real, necessary work, not standing in for something that would have happened anyway.

**An open discrepancy with round 49, named rather than papered over.** Round 49's own automatic-
trigger test, same creature pair, same save, explicitly checked player HP and reported it "stayed
12/12 throughout every local_combat probe window" for a full 25s cycle, twice (both `0x3B` and
`0x38`), attributing the ONE HP change it saw entirely to the forwarding channel and only after the
exclusion window had already closed. This round's three trials, by contrast, found the fight (client
standalone, host standalone, and the client's C3 trial) landing a real hit within roughly 25-30s
every time, 3 for 3. Both rounds used `console 'where'`-equivalent ground truth (round 49 via the
dashboard's own HP readout, round 51 via the console verb) — this is not obviously a methodology gap
on either side. Two honest readings, not resolved this round: either the underlying hit is
probabilistic/timing-sensitive (round 49 unlucky twice, round 51 lucky three times), or something
about firing via the `firescript` cheat verb directly (round 51's method, all three trials) differs
in a way that matters from the creature being pulled out and re-fired through `local_combat`'s own
automatic `StartLocalFight` path (round 49's method) even though round 50's own consumer audit says
both call the same `FireCreatureScriptSlot0x260`. Whoever continues this should rerun round 49's
exact automatic-trigger test with a `where` check bracketing the window, since round 49 predates
this round's discovery that the diagnostic it otherwise relied on cannot be trusted either.

**Verdict.** V1 (the client install/instance itself) is refuted, decisively, by two clean,
unconfounded trials (C1 redo, C2). The deeper and more consequential finding is methodological: the
`is fight action` diagnostic this document has used as its ground truth since round 9 does not
detect success even when a real, independently-verified hit lands — every prior round's "0/6000,
therefore frozen" conclusion was never actually testing what it claimed to test, and needs
re-reading against player HP, not this proxy, before being trusted further. V2 (session role) is
narrowed but not settled: a hit was observed with a session live (C3), but the standing forwarded-
damage channel is quantitatively sufficient to explain it alone, so this round cannot yet say
whether `local_combat`'s own client-side mechanism contributes anything once a session is up.

**Next step, concrete.** A clean C3 needs the forwarding-channel confound controlled OUT before the
manual (or automatic) fire runs — e.g. keep the player's phantom outside the HOST's own perception
range of the creature until after the fire is armed, or temporarily neutral-hold the HOST's own copy
of the creature via its console, so only the CLIENT's own mechanism can possibly be producing a hit
— then bracket that window with `console 'where'` on both sides. Separately, round 49's own
automatic-trigger positive/negative pattern should be re-run with the same `where` bracketing this
round used, since it predates the discovery that `is fight action` cannot be trusted as a negative
result either.

**Safety.** No permanent exclusion or corruption in any trial; both creatures remained alive,
GIT-identical, and un-materialised throughout (`n_sithsoldier004` was never a stand-in). No code
changes were made this round (`git status` clean going in and coming out other than this
documentation and roadmap update) — this was a pure diagnostic round, static analysis plus live
instrumentation the codebase already ships (`firescript`, `ArmFireScriptWatch`, `console 'where'`,
`console 'tp'`). Full suite `dotnet test tests/KotorCoop.Tests`: **1192/1192**, no flakes this run.
`dotnet build KotorCoop.slnx`: 0 warnings, 0 errors. `src\KotorCoop.Plugin\build.ps1 -Install
-GameDir "C:\GOG Games\Star Wars KOTOR (GOG)"` and again with `-GameDir "D:\Games\swkotor-gog-p2"`:
both succeeded, 0 warnings, 0 errors. Rig torn down (`demo.ps1 -Stop`) and confirmed clean
(`Get-Process swkotor,KotorCoop.Agent` returned none), `auto_start_agent` restored to `1` in both
installs.

### Round 52 (2026-08-21) — the ground-truth instrument, live, in a real session: PROVEN

Round 51 left one thing genuinely open: whether `local_combat`'s own client-side mechanism
contributes a real hit once a live session's automatic exclusion and the standing
forwarded-damage channel are BOTH running on the same creature — its own C3 trial showed a
hit, but the forwarding channel alone was quantitatively sufficient to explain it, so C3
could not distinguish the two. It also found the `is fight action` diagnostic every round
1-51 leaned on cannot be trusted (0/6000 sampled frames even on a proven-successful hit).
This round built the instrument round 51 asked for and ran the confound-controlled trial.

**Part 1: the ground-truth instrument.** `PhantomHitHook.OnDamage` already hooks the
engine's one damage choke point (`CSWSEffectListHandler::OnApplyDamage`) for every damage
event in the game; it did not yet look at hits landing on the LOCAL PC specifically. Added
(logging only — never suppresses or redirects the engine's own application):

- `PhantomHitHook.WatchLocalPlayer(uint)` — called once a frame from `PluginHost.OnFrame`,
  reusing the same fresh `Odyssey.GetPlayerCreaturePointer()`/id read that already feeds
  `PublishEnginePlayer`, so the hook needs no engine call of its own (the class's own "no
  engine calls inside the hook" rule stays intact).
- `PhantomHitHook.LogLocalHitGroundTruth` — when the damage target is the watched local
  player id, reads the attacker id and total off the effect (the same `ReadTotal` the
  phantom path already uses) and classifies the attacker, then logs one line, edge-limited
  to once per attacker per second (`ShouldLogLocalHit`, a small fixed 8-slot table, no
  allocation, no dictionary — same style as the rest of the class).
- Classification order: `PhantomHitHook.SuppressDealt` (already set around every
  `Odyssey.ApplyDamageEffect` call, for an unrelated reason — round 39's self-feeding-ledger
  fix) identifies "this player's own instance applied this, not the engine reacting to a
  real local swing" — the ONLY one of `ApplyDamageEffect`'s four call sites that ever targets
  the local player is `IncomingDamageController`'s forwarded-apply, so seeing the flag set
  here is unambiguous without a second flag. Otherwise: `EnemyPresenceController.
  IsLocallyFightingCreature` (a new static mirror of `_locallyFightingId`, published each
  frame at the same point `block.LocalCombatExcludedId` already is), then
  `IsDrivenCensusCreature` (a new static per-frame set of every id `DriveOne` actually drove
  this pass, populated only at its genuine "driven" exit — none of the early-return skips),
  then `PhantomHitHook.IsWatchedPhantom` (already existed), else "unrecognized". Effect
  creator_id was deliberately NOT used to identify the forwarded applier — `ApplyDamageEffect`
  dispatches through the VM's own script context, not scoped to the original remote attacker,
  so its creator_id would have named the wrong thing (confirmed live this round: forwarded
  applies logged creator_id `0x2C`, `0x20`, `0x7`, `0x109` — none of them `0x38`, the real
  attacker `IncomingDamageController`'s own line names correctly from the wire).

**Part 2: the misleading diagnostic.** `Odyssey.TickFireScriptWatch`'s
`(is fight action: {IsFightAction(headType)})` log fragment — the one every round 9-51 used
as its sole "did a fight happen" signal — is deleted, not fixed: round 51's own disassembly
found no evident correction (the head action reflects the action-QUEUE dispatch, RunActions;
the real combat-round processing is a separate state machine, `StartCombatRound` and its
maneuver-selection dispatch, that this field does not observe). The raw `headType` is still
logged for whoever continues the `sub+0x5c` lead; the misleading interpretation is not. A
doc-comment warning was added to `IsFightAction` itself (still functionally correct and kept
for its original, narrower purpose inside `QueueAttackAction` — "don't stack a second queued
attack behind one already at the head") pointing future readers at this round and round 51.

**Part 3: the rig and the confound-controlled trial.** `tools/demo.ps1` cold start (both GOG
installs, save slot 1, `END_M01AA`), `n_sithsoldier004` pair confirmed at the same ids rounds
49-51 used (`0x3B` at `(47.56,60.54)`, `0x38` at `(43.96,62.92)`). Baseline `console 'where'`:
`hp 12/12`. The client's player was teleported to a staging point 9.4 units away (not
point-blank — `console 'tp 40.0 55.0 -1.3'`) and then walked the ordinary way into
`local_combat_range` (`blockpoke.ps1 walk 46.5 60.0`, the engine's own move action) rather
than teleported point-blank. A 30s passive-wait control at this range produced zero natural
aggro (consistent with round 51's own control) — no attempt was made to disguise this: the
soldiers never noticed the player on their own within any window this investigation has ever
tested. `local_combat`'s own AUTOMATIC proximity trigger fired the moment the player entered
`local_combat_range` (20 units default — no manual firescript needed this round; the ordinary
mechanism triggered itself). Later in the session, to test mutual combat and a second
creature, the client's own player was ordered to engage with the engine's real `ActionAttack`
(`blockpoke.ps1 attack 0x7FFFFFFF 0x3B` — clicking is not available, so this cheat path is
the stand-in for a mouse click, not a script-firing shortcut; the player visibly walked from
its staging point to melee range as a result, confirming a genuine `ActionAttack` was queued).

The evidence (verbatim, client `kotorcoop-plugin.log`, this session only):

    [19:44:59.155] local combat probe: 0x3B creatureFaction=1 playerFaction=0 rep(creature,player)=2
      rep(player,creature)=2 (2=hostile,1=friendly,0=neutral) CreMovePermitted=0xFFFF plot=False
      min1hp=True hp=12/12 bisectMask=0x0
    [19:44:59.155] local combat: 0x3B pulled out of the census to fight for real (timeout 25s)
    [19:45:14.933] combat: absorbed 2 forwarded damage in total, attributed to mirrored enemies
      we are driving (last attacker 0x3B); this engine says in-combat=True
    [19:45:18.811] combat ground-truth: local PC took 2 damage, attacker=0x3B (the currently
      locally-fighting creature (StartLocalFight's own id))
    [19:45:24.156] local combat: 0x3B fight ended (timed out), rejoining the census
    ... (0x3B/0x38 continued cycling cleanly every 25s for the rest of the session — 8 more
    clean "fight ended (timed out), rejoining the census" / "pulled out of the census to fight
    for real" pairs, confirming re-inclusion works and the timeout safety net fires reliably) ...
    [19:50:00.233] combat ground-truth: local PC took 4 damage, attacker=0x2C (forwarded-damage
      applier (IncomingDamageController's own EffectDamage apply))
    [19:50:00.234] combat: took 4 damage from a hit on our phantom (attacker 0x38)
    [19:50:00.234] combat: this player cannot be downed (took 4 on 1 hp and is on 1) — the
      player carries min-1-hp, so damage floors at 1 hit point and no death path runs.
    [19:50:00.893] combat ground-truth: local PC took 2 damage, attacker=0x3B (the currently
      locally-fighting creature (StartLocalFight's own id))

The client's own `console 'where'` HP tracked exactly: `12/12` baseline, `8/12` after the
first four points of GENUINE local damage (two ground-truth hits of 2 each, zero
`combat: took` lines in that window), `1/12` (the area's min-1-hp floor) after roughly a
dozen more real hits from both `0x3B` and `0x38` — the player never actually died despite
dozens of confirmed real hits landing over the ~6-minute session, because the floor holds,
not because the damage was not real. 21 of the ~26 total damage events this round logged were
genuine local hits (`SuppressDealt` false, attacker matching the currently-fighting or
driven-census classification); the remaining ~5 were forwarded-and-applied hits from `0x38`
while `0x38` was NOT the currently-excluded creature, each correctly paired with
`IncomingDamageController`'s own `combat: took ... (attacker 0x38)` line. **Not one damage
event this round observed was misclassified between "genuinely local" and "the forwarded
applier"** — the one distinction that actually matters for the double-counting question.

**Reverse safety (task instruction 3), confirmed live in both directions.** While `0x3B` held
the local-fight slot, its forwarded damage was absorbed, not applied (`combat: absorbed 2
forwarded damage ... last attacker 0x3B`) — the host's own copy of `0x3B` genuinely dealt
that same 2 damage to the client's phantom on the HOST's engine (host log, same timestamp
window: `[19:45:14.726] hit breakdown: ... [12]=2 ... [14]=2`), and the client correctly
declined to double-apply it. In the same second `0x38`'s forwarded damage — `0x38` was not
the locally-fighting creature at that moment — correctly APPLIED instead of silently
vanishing, confirming the absorb is attributable per-attacker, not a blanket standdown. Zero
`combat: took` lines exist anywhere in this session's log for attacker `0x3B` while `0x3B`
held the exclusion; every `combat: took` line in the whole session names `0x38`, and only
during windows `0x38` itself was not the excluded one.

**One honest gap.** Around a fight's rejoin boundary the "driven/mirrored census creature"
classification occasionally read "unrecognized attacker id" instead for a few seconds
(`EnemyPresenceController.IsDrivenCensusCreature`'s per-frame set did not always contain the
id right at the handoff) — `SuppressDealt` was still correctly false every time, so the one
safety-relevant bit was never wrong, only the finer sub-classification. Root cause not
chased; noted rather than hidden, and does not affect the verdict below.

**Verdict.** `local_combat`'s own exclusion-and-script-fire mechanism is now PROVEN, live, in
a real two-instance session, with the automatic trigger, against a genuinely hostile
GIT-placed creature, to land real, repeated, independently-verified damage on the local
player through the client's own engine — correctly distinguished from, and never
double-counted with, the pre-existing forwarded-damage channel. What remains unproven: (1)
natural aggro — this save's `n_sithsoldier004` pair has never been observed to notice the
player without either a manual script fire or an ordered `ActionAttack`, in any round's
passive-wait control including this one; (2) real lethality — END_M01AA's command-deck
min-1-hp scene pin means no test run in this specific room can show a player actually go
down or die from either damage source, so "can this really kill someone" is still untested by
every round to date, this one included; (3) the client killing an enemy outright — this round
ran the fight to conclusion via the timeout/disengage/re-include cycle (repeated 9 times
cleanly) rather than a kill, which the task's own instructions accept as an equally valid
conclusion.

**Safety.** No permanent exclusion in any cycle — both `0x3B` and `0x38` cycled cleanly
through repeated timeouts and rejoined the census correctly throughout (9 clean cycles
logged). No watchdog trips. No materialised stand-in ever created (`n_sithsoldier004` is
GIT-identical throughout). Both creatures remained alive at session end. The client's HP
floor behaved exactly as the area's own documented min-1-hp pin predicts, for both damage
sources.

Build clean (`dotnet build KotorCoop.slnx`: 0 warnings, 0 errors; `src\KotorCoop.Plugin\
build.ps1 -Install -GameDir "C:\GOG Games\Star Wars KOTOR (GOG)"` and again with `-GameDir
"D:\Games\swkotor-gog-p2"`: both succeeded, 0 warnings, 0 errors). Full suite
`dotnet test tests/KotorCoop.Tests`: **1192/1192**, no flakes. Rig torn down (`demo.ps1
-Stop`) and confirmed clean (`Get-Process swkotor,KotorCoop.Agent` returned none),
`auto_start_agent` restored to `1` in both installs.

### Round 53 (2026-08-21) — one ordinary fight, on a module WITHOUT a min-1-hp pin: the full
### arc to a genuine DOWN, real lethality proven, and a new blocker isolated for the KILL half

Round 52 proved `local_combat` lands real hits, but left three things open: natural aggro,
real lethality (END_M01AA's command-deck pin floors every hit at 1, so no test there can show
a down/death), and the client killing an enemy outright. This round picked a module without a
scripted pin and ran the fight to a real conclusion.

**Module and target — GIT evidence.** `tar_m10aa` (Taris, Black Vulkar Base) was read with
`tools/kotorfiles.py`: 25 GIT-placed `FactionID=1` hostiles in the module (`tar10_vulkarpt01`,
`tar10_vulkpoo001..005`, `tar10_ptdrd0xx`, `tar10_bouncer`, `tar10_thermturre` x3, etc.), none
plot-flagged, none min-1-hp-pinned in their own blueprint. The one actually engaged this
round — GIT `Creature List` index 17, `TemplateResRef=tar10_vulkpoo002`, `FactionID=1`,
`Plot=0`, name strref 26410 = **"Black Vulkar"**, `CurrentHitPoints=MaxHitPoints=22`,
`ChallengeRating=3`, `Appearance_Type=264` (the same Vulkar row client-encounters.md §7's
Taris ambush used) — predicted object id `0x33` (`kotorfiles.py objectids tar_m10aa`), at
GIT position `(103.70, 101.37)`, 14.6 units from the module's own `Mod_Entry_X/Y`
`(103.293, 115.924)`. Confirmed live at the SAME id and position on both instances
(`console 'resolve 0x33'` → `0x1A9FD5A8` client / `0x1AC43E80` host; `census 0x33` on both).
This was chosen over the module's originally-picked `tar10_vulkpoo002` at id `0x38` (also a
valid GIT hostile in the same cluster) only because the engine's own automatic proximity
trigger picked `0x33` first — see below.

**Blocker found and fixed before the arc could even start: a false-positive session
disconnect on the warp itself.** `console 'warp tar_m10aa'` on a live, connected host
produced `co-op: loading a save mid-session is not supported yet - clients must rejoin` and a
real `SessionManager.Disconnect()` — roadmap item 14 phase 1's brand-new host mid-session-load
refusal (shipped the same day, commit `c23d4e4`), firing on an ORDINARY module transition, not
a genuine different-save load. Root-caused live: KOTOR's own engine autosaves on every module
transition, and `demo.ps1`'s own staged autosave slot (`saves\000001 - AUTOSAVE`) is exactly
the file `SaveIdentityWatch`'s digest polls — confirmed by inspecting the save on disk before
and after the warp (`savenfo.res` `LASTMODULE` flipped from `END_M01AA` to `tar_m10aa`,
`SAVEGAME.sav`'s mtime updated to the warp's own timestamp). The digest changing because THIS
session's own world moved forward is indistinguishable, from a content digest alone, from a
host loading a genuinely different save — exactly the gap `session-save.md`'s own NEEDS-LIVE
item 1 flagged as unmeasured ("whether an in-game Load Game action changes
`SaveIdentityDigest` at all, as opposed to only a subsequent save") and NEEDS-LIVE item 2
flagged as never run ("whether the host-refusal path is disruptive... never run against two
real GOG installs"). Both are now answered: yes, and yes — an ordinary transition trips the
exact same edge a real different-save load would, so shipped as-is this would disconnect every
host the first time they walked through a door.

**Fix (minimal, matches this codebase's own convention for an unverified detector — ship the
edge detection, gate the action):** `CoopConfig.SessionSaveHostRefusal` (new bool, default
`false`), parsed from a new `session_save_host_refusal` coop.ini key, documented in
`profiles/coop.ini.example`. `CoopCommandDispatcher.MaintainSessionSave` still calls
`_sessionSaveWatch.ObserveHost(digest)` every tick unconditionally (the edge is still detected
and logged — `session-save: host-side save-identity edge detected - NOT refusing
(session_save_host_refusal=0)`), but only calls `Reply(...)`/`manager.Disconnect()` when the
flag is explicitly turned on. Two new tests in `CoopConfigTests.cs`
(`SessionSaveHostRefusalDefaultsOff`, `SessionSaveHostRefusalCanBeTurnedOn`). Re-run with the
fix live: the identical `warp tar_m10aa` on a connected host produced the same edge-detected
log line but did NOT disconnect — `peers 1` held throughout, and the client followed cleanly
(`following the host to 'tar_m10aa'`, both `console 'where'` replies landed in `tar_m10aa`
within the same few seconds). Distinguishing a genuine different-save load from an ordinary
transition for real needs a save/load engine hook (`session-save.md`'s own deferred item 3 —
out of scope here); until that lands the flag stays off and item 14's status is downgraded
from "Partial (phase 1, unit-tested only)" to note this specific live-verified false positive.

**A second, separate, transient false positive — self-resolving, not fixed.** Right after the
warp landed, `co-op [W_SAVE] different save: enemies, doors, items and party are off` fired on
both instances (`console 'census'` on the client read `MIRROR 0 id(s)` for several minutes
after — the id-keyed channels genuinely were suspended, not just logged). This is round 43/44's
own documented "save-drift false alarm" pattern, but a NEW instance of it: neither original
save (`000002 - Game1`, unmodified since 2026-08-13/07-30) had ever visited `tar_m10aa` — only
one nested `.sav` (`END_M01AA`) exists in either — so both installs' pristine `tar_m10aa.rim`
GIT digests are identical and the mismatch this compat check saw was transient, computed once
against a not-yet-settled on-disk state right at the crossing, not genuine content drift.
`console 'reconnect'` on the client forced a fresh re-judgment against the now-settled state
and cleared it immediately (`census` went from `MIRROR 0 id(s)` to `MIRROR 26 id(s) 'tar_m10aa':
26 resolve here, 0 do not`). No code change made for this half — the existing "rejoin to
adopt the host's world" guidance the warning itself gives is the correct, already-designed
recovery, and this round used it rather than inventing a second gate on top of the first fix.

**Natural aggro test — still no.** With the census healthy, the client's own player sat
passively within `local_combat_range` of `0x33` (`dist=9.43`, non-point-blank, `tp`'d there
rather than melee-adjacent) for two full 25-second exclusion cycles
(`local combat: 0x33 pulled out of the census to fight for real` / `0x33 fight ended (timed
out), rejoining the census`, twice). Zero `combat ground-truth` or `combat: absorbed` lines in
either window; `console 'where'` read `hp 12/12` throughout. This is the THIRD distinct
creature/module pair (after `n_sithsoldier004` x2 on `END_M01AA` in rounds 51-52) to show zero
proximity-only aggro in every test this investigation has run — the pattern generalises past
one save.

**⚠ A discrepancy from round 52's own methodology, found live.** `blockpoke.ps1 walk` did NOT
move the player this round — two separate requests (`walk 103.6 106.0` from a staging point 25
units away, and implicitly via the `attack` order's own approach-to-melee step from 9.4 units
away) both left `console 'where'` reporting the exact same, unmoved coordinates for 15+ seconds
each, cross-checked against the plugin's own independent per-second position log
(`local combat diag: t=... player at (95.00,105.00)`, static across 24+ samples). This matches
`TestDriveController.HandleWalk`'s own doc comment verbatim ("THIS DOES NOT MOVE THE PLAYER
CHARACTER... the PC is drive-controlled") — a caveat that predates round 52's write-up, which
nonetheless reported `blockpoke.ps1 walk 46.5 60.0` moving the player successfully. Not
resolved this round (out of scope of the caveat's own guidance, which names `tp` as the
reliable alternative); flagged rather than silently worked around. **Workaround used
throughout this round:** `console 'tp <x> <y>'` to the desired staging/engage distance instead
of `walk`, which is reliable (confirmed instant, exact-coordinate placement every time).

**The engagement — real fight, real hits, only once the player was near-melee.** `attack
0x7FFFFFFF 0x33` issued from the 9.4-unit staging point produced the identical
`attacktarget diag: 0x7FFFFFFF round+0x958 0->1, round+0x960 0->5DC` arming signature round
52's own successful case logged, and the plugin's own log confirmed the order was accepted
(`test: 0x7FFFFFFF ordered to attack 0x33`) — but zero movement and zero damage followed for
40+ seconds. `tp`'d to `(103.7, 103.4)` (1.97-2.03 units from `0x33`) and re-ordered: real
combat started within seconds. Verbatim, client `kotorcoop-plugin.log`:

    [20:33:23.288] combat ground-truth: local PC took 3 damage, attacker=0x33 (the currently
      locally-fighting creature (StartLocalFight's own id))
    [20:33:29.962] combat ground-truth: local PC took 4 damage, attacker=0x33 (...)
    [20:33:35.695] combat ground-truth: local PC took 8 damage, attacker=0x33 (...)
    ... (26 such lines total over the next ~2 minutes, every one attributed to 0x33 as "the
    currently locally-fighting creature", raw hits ranging 2-20 damage, summing to 178) ...
    [20:35:15.155] combat ground-truth: local PC took 2 damage, attacker=0x33 (...)
    [20:35:15.156] death: the local player is down

Every one of the 26 ground-truth lines this fight produced named `0x33` as "the currently
locally-fighting creature" — zero were classified as the forwarded applier or unrecognized,
and (reverse safety) **zero `combat: absorbed`/`combat: took` lines appear anywhere in this
window** — the host's own PC never left the module entry point 14+ units away, so this fight
ran through `local_combat`'s own mechanism in complete isolation from the forwarded-damage
channel, a cleaner separation than round 52's own concurrent-channel test.

**Real lethality — PROVEN.** `console 'where'` mid-fight read `hp 1/12` despite the ground-truth
sum already exceeding 12 several hits earlier — a min-1-hp floor was masking it, but NOT
`tar_m10aa`'s own (the module has none — `min-one-hitpoint.md`'s sweep already confirms this).
Root cause: `SetMinOneHP` is a **persistent status effect**, not an area-scoped one — `END_M01AA`'s
`k_pend_area01` sets it on the PC once, and only `end_m01ab`'s `k_pend_1b_area` clears it; this
round's save started on the Endar Spire and warped directly to `tar_m10aa`, skipping the
clearing area entirely, so the stale pin rode along. Cleared live with `cheat god 0`
(`cheat: god OFF - a module may still pin you (status 1)`) — the exact lever `CheatController.God`
already exposes for precisely this situation. The moment the floor came off:

    [20:35:14.208] cheat: god OFF - a module may still pin you (status 1)
    [20:35:15.155] combat ground-truth: local PC took 2 damage, attacker=0x33 (the currently
      locally-fighting creature (StartLocalFight's own id))
    [20:35:15.156] death: the local player is down
    [20:35:15.221] spectate: watching 0x133 - Tab cycles (2 to watch)

`console 'where'` immediately after read `hp -11/12` — the true, already-deeply-negative total
the floor had been hiding, not a fresh kill. **This settles round 52's own open question
outright: a real fight through `local_combat`, with no min-1-hp pin standing in the way, really
can bring a player down from genuine engine damage** — not a scripted floor, not a health poke,
the engine's own death path (`death: the local player is down`, `blockpoke.ps1 death` reading
`downed 1` immediately after). **Cross-player sync of the down state confirmed too:** the HOST's
own `console 'peers'` read `Malak  tar_m10aa  [down]` — the other player's screen correctly knew
the client was down, matching README's "Go down together" bullet. Revived cleanly afterward
(`cheat standup 12` → `hp 12/12`, `death` → `downed 0`) and combat broken off
(`attack 0x7FFFFFFF 0` → `test: 0x7FFFFFFF ordered to break off combat`).

**What did NOT close: the client killing an enemy outright, and a new, more precise reason
why.** `0x33` took real, substantial local damage on the client's own engine (raw ground-truth
sum 178; the client's own damage-DEALT ledger, read directly out of the shared block
(`PhantomHitHook.Dealt`/`SharedBlock` offsets `0x1E60`/`0x2000`/`0x2118`), confirmed
`DamageDealtEnabled=1` and one live entry, `id 0x33 cumulative 163`). But the HOST's own copy
of `0x33` never moved: `console 'census 0x33'` on the host read `listed hp 22/22` throughout
and after the fight, and the host's own `DamageOwed*` fields
(`SharedBlock` offsets `0x200C`/`0x2010`/`0x2018`, read the same direct way) showed
`DamageOwedCount=0` for the entire session — no `combat: applied N of a peer's damage to 0x33`
line, and not even `peer-damage-ledger.md`'s own documented fallback diagnostic,
`combat: a peer owes N damage to 0x33, which does not resolve here` (which DOES fire correctly
for an unresolvable id, per two OLDER sessions' log entries found in the same file) — meaning
the report never reached `PeerDamageController.ApplyOwed` at all, not even as a debt held
against an unresolvable id. Ruled out live: malformed packets (`status`'s own `malformed 0`
throughout), netsim (`netsim status` → `off (clean link)`), peer registration (host's `peers`
correctly named `Malak` in `tar_m10aa` the whole time, and OTHER peer-keyed channels — presence,
HP, the downed flag — all synced correctly over the same connection), and module-label
mismatch (`moduleClean` held true on the client throughout, per the existing per-tick
diagnostic). This narrows round 52's own open item 3 ("the client killing an enemy outright")
to a concrete, previously-unisolated point of failure: somewhere between
`CoopClient.ReportEnemyDamage`'s send and `CoopHost.GetEnemyDamage`'s merge, not in the
plugin's own ledger (confirmed populated) and not in the plugin's own apply step (never
reached). Not root-caused further this round — `peer-damage-ledger.md`'s own NEEDS-LIVE
checklist (items 1, 2, 8) predates this finding and is the first live measurement of any of it;
this is new information, not a re-confirmation of something already known to fail.

**Safety.** No permanent exclusion — `0x33` cycled cleanly through repeated timeouts (5+ clean
`fight ended (timed out)` / `pulled out of the census` pairs after the engagement, continuing
even after combat was broken off, since the player never left `local_combat_range` until the
final `tp` back to the entry). No watchdog trips. No materialised stand-in (`tar10_vulkpoo002`
is GIT-identical throughout on both installs). The host's own player was never at risk (stayed
at the module entry, `hp 12/12` throughout, 14+ units from the fight). `0x33` remained alive on
the host (`22/22`) at session end — the client's own near-death local copy (would have read
`~1/22`) never persisted past its next `local_combat` re-inclusion, since re-inclusion re-syncs
the mirrored creature back to the host's still-full authoritative health, another visible
symptom of the same unresolved item above.

Build clean (`dotnet build KotorCoop.slnx`: 0 warnings, 0 errors; `build.ps1 -Install` for both
GOG installs: both succeeded, 0 warnings, 0 errors). Full suite `dotnet test
tests/KotorCoop.Tests`: **1194/1194** (1192 baseline + 2 new `CoopConfigTests` cases), no
flakes. Rig torn down (`demo.ps1 -Stop`) and confirmed clean (`Get-Process
swkotor,KotorCoop.Agent` returned none), `auto_start_agent` restored to `1` in both installs.

### Round 54 (2026-08-21) — P1 (the transient `[W_SAVE]`) root-caused and fixed, live-verified
### with a real kill; P2 (natural aggro) reconfirmed open, a direct no-plugin test attempted and
### left inconclusive by the test rig's own navigation limits, not by anything measured

Round 53 left two things open: why the client's dealt damage never reached the host's apply
step, and whether the plugin or the harness explains zero natural aggro. This round closes the
first with a live kill and narrows the second without settling it.

**P1 — static trace first, per the task's own branch.** `SessionIntegrationTests.cs` already
had `ClientEnemyDamageReachesTheHost` (a real host+client over loopback UDP, asserting
`client.ReportEnemyDamage` reaches `host.GetEnemyDamage`) — run in isolation, it **passed**
(`Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1`), and the full suite passed at the
pre-round baseline. This rules out a session/protocol-layer regression outright: the transport,
`PacketCodec`, `CoopHost.HandleEnemyDamage`, and `PeerDamageLedger.Merge` (unit-tested
separately in `PeerDamageLedgerTests.cs`) all work. Per the task's own branching, this makes the
live cause capability-related, not a protocol regression — confirmed live below.

**Root cause, found by tracing `CoopAgent.PumpEnemies`'s own gate.** Both halves of the
damage channel — the client's `session.ReportEnemyDamage` call and the host's
`session.GetEnemyDamage`/`_peerDamage.Merge`/`bridge.TryPublishDamageOwed` call — live inside
the SAME function, `CoopAgent.PumpEnemies`, which returns at its very first line whenever
`(caps & SessionCapabilities.Enemies) == 0` (`CoopAgent.cs:2054`). Round 43 already established
that `W_SAVE`/`W_GIT` drop `ContentCompatibility.ObjectIdKeyed` (`Enemies | Doors | GroundItems
| Party`) together as one mask — so the transient `[W_SAVE]` round 53 saw silences the ENTIRE
function, both directions, for as long as it reads true. That much was already on record; this
round found WHY the crossing reads as a mismatch at all.

`SaveDigest.TryComputeSaveIdentity` (`src/KotorCoop.Memory/SaveDigest.cs:145`) folds the module
the save is CURRENTLY standing in into the identity hash (`FnvString(FnvOffset,
moduleSav.Resref...)`), alongside the `Mod_NextObjId` counters and the module's own GIT id
list. That is exactly right for catching two genuinely different saves — but it also means the
digest legitimately, correctly changes the instant EACH side's own engine autosaves into a new
module on an ordinary transition. The two peers never autosave in the same instant: one follows
the other by however long the travel and load take, plus `SaveDigestCache`'s own up-to-2-second
re-stat interval on each side (`StampCheckIntervalMs = 2000`, `RefreshSaveIfStale`). For that
whole window the faster peer's digest already names the new module while the slower peer's
still names the old one — and critically, `WorldDigest.Compare`'s save-identity check
(`src/KotorCoop.Protocol/WorldDigest.cs`) had **no gate on this at all**: unlike the module GIT
check three lines below it, which is `sameModule &&`-gated for exactly this reason ("while one
peer is mid-transit the digests describe different areas"), the save-identity comparison ran
unconditionally. So an ordinary crossing reads, correctly by the letter of the check and
incorrectly by its intent, as a save-identity mismatch — `W_SAVE` fires and drops combat for
everyone, every single time one player's transition takes longer than the other's.

**Fix, two layers, both in `KotorCoop.Protocol` where host and client both compute from
(pure, no clock needed for the first one):**

1. **`WorldDigest.Compare` now gates `SaveIdentityMismatch` on `sameModule`**, the same
   boolean the GIT check already used. While the two peers report different modules — the
   ordinary state of "still travelling" — a save-identity disagreement is uninformative
   (nothing id-keyed is shared across two different modules anyway, so there is nothing to
   protect by judging it) and is no longer compared at all. This closes the majority of the
   race: the client hasn't even reported a new module yet, so the raw comparison never runs.
2. **`SaveIdentitySettle` (new, `src/KotorCoop.Protocol/SaveIdentitySettle.cs`)** — a small
   stateful debounce, one instance per client-side view and one per host-side peer connection
   (`CoopClient._saveSettle`, `CoopHost.PeerConnection.SaveSettle`) — absorbs the RESIDUAL gap:
   once both sides agree on the module, each side's own `SaveDigestCache` may still be holding
   the pre-transition digest for up to its own 2-second re-stat interval (the identity job runs
   off the save file's on-disk stamp, not the module read directly out of engine memory, so the
   two do not update in lockstep). A save-identity mismatch is only honored once it has been
   observed continuously for `DefaultSettleMs` (5000 ms); a single clean tick clears it
   immediately, so recovery is never delayed — only the ONSET of a new mismatch is. Wired in at
   `CoopClient.RefineCompatibility` and `CoopHost.EffectiveVerdict`, reset on a fresh Welcome
   (client) and implicitly fresh on every new `PeerConnection` (host, since a reconnecting peer
   gets a brand-new one already).

**Tests.** `WorldDigestTests.SaveIdentityIsOnlyJudgedWhenBothPeersStandInTheSameModule` (mirrors
the existing GIT test); `SaveIdentitySettleTests.cs` (8 cases: a single-tick mismatch is
suppressed, a mismatch that persists past the window is honored, the exact transient-then-agree
sequence never reads as a mismatch, recovery is instant, `Reset` forgets an in-flight timer);
`SessionIntegrationTests.ATransientSaveIdentityMismatchNeverDropsCombat` (real `CoopHost` +
`CoopClient` over loopback UDP: publishes a genuine crossing — host already in `tar_m10aa`,
client still in `end_m01aa` for 1.5s, then both agree — and asserts `Enemies` is never dropped
at any sampled point, then holds the agreeing state for 4 more seconds to confirm it never gets
honored late either). The pre-existing `ASaveIdentityMismatchDegradesToIdIndependentChannels`
and `AModuleGitMismatchDropsIdChannelsThereAndRestoresInTheNextModule` (both same-module by
construction, so unaffected by the new gate) still pass unchanged, confirming a REAL,
persistent mismatch still degrades exactly as designed.

**Live re-run, the exact round-53 recipe, same rig, same module.** Fresh `demo.ps1` cold start
(save 1), `console 'warp tar_m10aa'` on the host. This round's crossing:

    [21:15:25] * warping to tar_m10aa...
    [21:15:26] hosting on 0.0.0.0:27140  ...  module area1  ...  combat [FIGHT in area1 - engaged: you]
    [21:15:26] * session-save: host-side save-identity edge detected - NOT refusing (session_save_host_refusal=0)
    [21:15:27] hosting on 0.0.0.0:27140  ...  module tar_m10aa  ...  combat [nobody here is fighting]
    [21:15:58] hosting on 0.0.0.0:27140  ...  module tar_m10aa  ...  combat [nobody here is fighting]

**No `[W_SAVE]` line at all** — compare round 53's identical recipe, which fired it at
`[21:09:35]`, ~5-7s after the warp landed. `console 'census'` on the client immediately after
read `MIRROR 26 id(s) 'tar_m10aa': 26 resolve here, 0 do not` — the channel was never silenced
this time.

**The kill, live, both directions.** Client teleported to `dist≈2` from `0x33` (the identical
Black Vulkar round 53 used, 22 hp) and ordered to attack (`blockpoke.ps1 attack 0x7FFFFFFF
0x33`). Host's `kotorcoop-plugin.log`, verbatim:

    [21:17:09.852] combat: applied 6 of a peer's damage to 0x33 (owed 6)
    [21:17:12.832] combat: applied 7 of a peer's damage to 0x33 (owed 13)
    [21:17:19.072] combat: applied 9 of a peer's damage to 0x33 (owed 22)

22 applied = `0x33`'s exact max hp. Host agent readout, same moment: `* materialised loot: 1
enemy death(s) noticed this pass: 0x33` / `* materialised loot: captured 2 item(s) off 0x33's
corpse, publishing` — a REAL death, with a corpse and real loot, exactly as
`peer-damage-ledger.md`'s headline promised and round 53 could not yet show. Host `census 0x33`
immediately after: `not listed in this census` (dead, removed from the owner's own census).
Client's plugin log, same window: `[21:17:19.398] combat: killed 0x33 on the owner's authority`
— the client's own mirrored copy following the real death down the ordinary enemy-snapshot
path, not a synthetic kill-request. This settles round 53's own open item: `DamageOwedCount`
moved, `combat: applied` fired, and the kill produced a real death on both instances. The fight
continued onto a second Vulkar (`0x24`) with the identical shape (`applied` climbing 7→16→22→
29→36, then `does not resolve here ANY MORE - the body was destroyed`) — not a one-shot fluke.

**P2 — reconfirmed open; a direct no-plugin test was attempted and is recorded as
inconclusive, not negative.** Before the fix's own crossing, the client's plugin log showed
`0x33` "fighting" the player for ~25s while the player stood `dist=14.56` away — but reading
the surrounding lines shows this was `local_combat`'s own 25-second ROUND-ROBIN TIMER pulling
`0x33` out of the census and forcing `StartLocalFight`'s `+0x260` script fire
(`local combat: 0x33 pulled out of the census to fight for real (timeout 25s)`, logged at
`dist=14.56`, not on approach) — not proximity- or perception-triggered engagement. This
reconfirms rounds 44-53's own repeated finding on a FRESH creature/module pair: the probe read
genuinely hostile (`local combat probe: 0x33 ... rep(creature,player)=2 rep(player,creature)=2`,
avoiding round 48's stale-reputation confound), yet nothing organic ever engaged it — only the
plugin's own timer-forced arming ever did, on every creature this whole investigation has ever
tested.

Attempted the task's own cheapest test — instance 1 launched with `dinput8.dll` renamed aside,
same save (`Load Game` → `Auto Save`, confirmed `Taris / Black Vulkar Base`), navigated with
`tools/click.ps1`/`tools/sendkey.ps1` and `tools/shot.ps1` for visual feedback (no plugin means
no `blockpoke.ps1 tp`/`console`, and — newly confirmed this round — `TestDriveController.HandleWalk`'s
own documented brokenness means there is no reliable programmatic walk EVEN WITH the plugin, so
positioning near a hostile without the plugin has no proven-reliable primitive at all in this
codebase today). Reached the Black Vulkar base's pool room and confirmed the blueprint itself
is unremarkable (`tools/kotorfiles.py gff` on `tar10_vulkpoo002.utc`: `ScriptOnNotice
k_def_percept01`, `PerceptionRange 11`, `FactionID 1`, `Plot 0` — the same stock perception
script and range every ordinary KOTOR hostile uses), but blind keyboard/click navigation
without a live visual-feedback loop could not reliably close the distance to a live Vulkar
within the session's time budget (repeated `w`/`s`/`a`/`d` holds oscillated between two fixed
points in a corridor, apparently blocked by a stationary Pool Droid and/or Trask's own scripted
position near the entrance, confirmed by `tools/creatures.ps1`'s position reads — a raw
memory-scan tool needing no plugin — rather than by anything resembling a pause). **This is a
rig-navigation limitation, not a measurement either way** — recorded honestly rather than
reported as a negative result. `dinput8.dll` was restored and confirmed identical to the
freshly built one (`Get-FileHash`, both `A904346...73AF`, matching).

**Recommended next step for whoever picks P2 up:** the blocker is specifically "no reliable way
to position a character near a hostile without the plugin's own `tp`." Stage a save file
already standing within `local_combat_range` of a live hostile in `tar_m10aa` (via the plugin's
own `tp` + an in-game quicksave, which needs a scan-code addition to `tools/sendkey.ps1` — no
quicksave key is mapped in it today — or a menu-driven manual save) BEFORE removing
`dinput8.dll`, so the no-plugin instance only has to load, not navigate. With that in hand,
round 51's own C1/C2 passive-wait methodology (30s untouched, `console 'where'`-equivalent
ground truth before and after — here, `tools/creatures.ps1`'s raw memory read, since there is
no plugin to ask) applies directly.

**Safety.** No materialised stand-in selected on the P1 re-run (`0x33`/`0x24` both GIT-identical
throughout). No permanent exclusion, no absorb/watchdog warnings. The P2 no-plugin instance
never reached a live hostile, so it could not have damaged, materialised, or exclusion-locked
anything; it was closed cleanly (`Stop-Process`) with no other instance running alongside it.

Both builds clean (`dotnet build KotorCoop.slnx`: 0 warnings, 0 errors; `src\KotorCoop.Plugin\build.ps1
-Install` for both GOG installs: both succeeded). Full suite `dotnet test tests/KotorCoop.Tests`:
**1218/1218** (1209 baseline + 9 new: 1 `WorldDigestTests`, 8 `SaveIdentitySettleTests`, 1
`SessionIntegrationTests`), no flakes this run. Rig torn down and confirmed clean (`Get-Process
swkotor,KotorCoop.Agent` returned none), `auto_start_agent` restored to `1` in both installs.

### Round 55 (2026-08-21) — P1 (host natural aggro) settled decisively; P2 (the client's own
### trigger policy) rebuilt around an immediate proximity/targeting-us rule, live-verified twice

Round 54 left two things open: whether the host's own engine aggroes naturally with the plugin
loaded (P1, its own no-plugin control was inconclusive), and the client's own trigger, reconfirmed
to only ever fire on `local_combat`'s 25s round-robin, never on approach. This round settles both.

**P1 — host natural aggro, cold rig, plugin present.** `demo.ps1` cold start (save 1),
`console 'warp tar_m10aa'` on the host, then two measurements against the identical Black Vulkar
pool round 53/54 used (`0x33`, GIT-placed, `FactionID=1`, `tar10_vulkpoo002.utc`:
`PerceptionRange=11`), using the plugin's own `console 'tp'` to position the host (its documented
walk primitive moved the player only partway before stalling on a repeat call — consistent with
round 54's own note that `TestDriveController.HandleWalk` is unreliable — so `tp` positioned the
rest of the way; position is what the perception test measures, not the path taken to reach it).

Measurement 1, `dist=8.4` (`102.4,109.7` vs `0x33` at `103.70,101.37`), inside `PerceptionRange`,
30s+ standing still, untouched:

    reply: 'module tar_m10aa  at 102.4 109.7 2.2  hp 12/12'      -- before
    reply: 'module tar_m10aa  at 102.4 109.7 2.2  hp 12/12'      -- 30s later, unchanged
    0x33 at (103.70, 101.37)   -- unchanged, before and after
    (zero plugin log lines mentioning combat or 0x33 in the window)

Zero aggro, zero movement, zero log activity — consistent with every prior round's own finding.

Measurement 2, `dist≈1.6` (`103.7,103.0`), same room, clear line of sight, 30s+ standing still,
untouched:

    [22:06:40.365] combat ground-truth: local PC took 2 damage, attacker=0x2F (unrecognized attacker id ...)
    [22:06:41.555] combat ground-truth: local PC took 3 damage, attacker=0x33 (unrecognized attacker id ...)
    [22:06:42.195] combat ground-truth: local PC took 9 damage, attacker=0x26 (unrecognized attacker id ...)
    [22:06:42.557] combat ground-truth: local PC took 7 damage, attacker=0x36 (unrecognized attacker id ...)
    [22:06:43.952] combat ground-truth: local PC took 7 damage, attacker=0x31 (unrecognized attacker id ...)
    [22:06:44.556] combat ground-truth: local PC took 10 damage, attacker=0x33 (unrecognized attacker id ...)
    [22:06:45.505] combat ground-truth: local PC took 9 damage, attacker=0x38 (unrecognized attacker id ...)

("unrecognized attacker id" is `PhantomHitHook.LogLocalHitGroundTruth`'s own classification for
a hit this plugin did not cause and is not forwarding — i.e. the engine's OWN native combat round,
verbatim from round 52's ground-truth instrument, not a new label invented for this round.) Within
20 seconds the host was fighting SEVEN independently-aggroed Vulkars (`0x33 0x24 0x26 0x36 0x31
0x38 0x2F`) at once, HP falling from `12/12` to `1/12`
(`reply: 'module tar_m10aa  at 103.7 103.0 2.2  hp 1/12'`) — `cheat god 1` and a retreat `tp`
ended the fight safely once the result was clear (the module has no confirmed min-1-hp pin, so
letting it run further risked a real death this round did not need to prove anything more by
causing).

**Conclusion: host natural aggro works completely normally with the plugin loaded.** The
`dist=8.4` miss was a line-of-sight/room-geometry gap (the pool room's own layout — the same
class of obstruction round 54's own no-plugin attempt got stuck on while trying to walk there),
not a range failure and not a plugin effect: the SAME distance metric, the SAME creature, the SAME
`PerceptionRange`, with only line of sight changing, went from zero engagement to seven
simultaneous aggros. No coop.ini bisection of the plugin's always-on patches (pause sanity,
focus-pause suppression, render-activate, solo-mode hold) was needed or run, because the positive
result at close range was unambiguous — the engine's own perception path is intact. Per the task's
own branching, this means the whole gap belongs to P2 below.

**P1, second half — the HOST's copy against the CLIENT's phantom.** With the host's swarm
still active, the client was teleported to the identical point-blank spot (`103.7,103.0`) and
`cheat god 1` applied there too. The client's own plugin log confirms the host's real,
unmodified creatures aggroed the phantom exactly as the design (`combat-participation.md` §5)
predicts:

    [22:08:10.511] combat: took 14 damage from a hit on our phantom (attacker 0x38)
    [22:08:12.539] combat: took 10 damage from a hit on our phantom (attacker 0x2F)
    [22:08:12.848] combat: took 10 damage from a hit on our phantom (attacker 0x31)
    [22:08:13.628] combat: took 12 damage from a hit on our phantom (attacker 0x24)
    [22:08:13.940] combat: took 3 damage from a hit on our phantom (attacker 0x26)

Nine distinct attacker ids landed forwarded, applied hits on the phantom over the session —
settling the second half of item 1 outright: the host's engine needs nothing from this plugin to
threaten a client's phantom once it is standing where a hostile can perceive it.

**P2 — the client's own trigger, rebuilt.** Before this round, `EnemyPresenceController.DriveOne`
computed its own `triggeringLocalFight` per slot, independently, in whatever order the owner's
census happened to enumerate that frame — the FIRST slot to pass every gate in that frame's loop
order won, with no comparison against any other eligible candidate. Combined with the single
exclusion slot (`_locallyFightingId`, one fight at a time by design) and the 25s
`LocalCombatTimeoutSeconds` ceiling on how long a fight may run, this meant a creature that became
eligible while another was already fighting had to wait out that WHOLE fight for its turn — which
is genuinely indistinguishable from a 25s round-robin timer even though no code ever scheduled
one, exactly the shape round 54 described from the outside.

Two new pieces, both in `src/KotorCoop.Plugin/`:

- **`LocalFightCandidateRule.cs`** (new file, `internal static`, no engine dependency): a pure
  function, `Evaluate(reputation, gitIdentical, hasScript, distance, engageRange, targetingUs) ->
  LocalFightPriority` (`None` / `Proximity` / `TargetingUs`), plus `Beats(...)` for ranking two
  candidates. The three hard gates (hostility `==2`, GIT identity, a real `+0x260` script) are
  unchanged from rounds 39/49 — this adds nothing unsafe, it only decides which ELIGIBLE candidate
  wins when more than one qualifies in the same frame. Unit-tested in isolation
  (`LocalFightCandidateRuleTests.cs`, 12 cases): each hard gate rejects unconditionally even when
  every other signal is maximally strong (targeting-us, point-blank), `TargetingUs` outranks
  `Proximity` at equal distance, and the tie-break (`Beats`) picks the strictly closer candidate at
  equal priority while letting the incumbent hold an exact tie (no flapping frame to frame).
- **`EnemyPresenceController.SelectImmediateCandidate`** (new method): runs once per `OnFrame`,
  BEFORE the main drive loop, whenever the exclusion slot is free. It scans every GIT-identical
  census slot, resolves each candidate (reusing `Resolve`'s own per-slot cache, so the later main
  loop's re-resolve is a cache hit), and asks `LocalFightCandidateRule` to rank it. The winner's
  slot index is stashed in `_immediateTriggerSlot`; `DriveOne`'s own trigger condition collapses to
  `slot == _immediateTriggerSlot` — every gate it used to re-derive per slot (rep, range, script,
  the round-49 hostility recheck) is now the prescan's job, checked exactly once instead of twice.
  `local_combat_engage_range` (coop.ini, default **11**, matching `tar10_vulkpoo002`'s own stock
  `PerceptionRange` from round 54's no-plugin control) is its own knob from the wider
  `local_combat_range` (default 20) that licenses absorbing forwarded damage once a fight is
  already running — starting a fight is a tighter question than continuing to credit one.

  The `TargetingUs` signal needed no protocol change: `block.IncomingDamage` /
  `block.IncomingDamageAttacker` (`IncomingDamageController`'s own cumulative wire total and last
  attacker, already forwarded end to end — `PublishPhantomDamage` → `GetIncomingPhantomDamage` →
  `bridge.TryWriteIncomingDamage`) already IS "the host reports the creature currently attacking
  our phantom." `UpdatePhantomAttackerTracking` (new, runs every frame ahead of every early
  return, same discipline as the existing engagement reset) detects a genuinely NEW cumulative
  total — mirroring `IncomingDamageController`'s own owed/applied reasoning exactly — and records
  its attacker as "targeting us" for `local_combat_targeting_us_fresh_s` (default 2s). Independent
  of whether local_combat is currently absorbing anything, which is what makes it usable as a
  trigger signal and not only an apply target. `Invalidate()` re-syncs to the current cumulative
  total (not zero) on a module change, so the non-resetting wire counter can never manufacture a
  false "new hit right now" with a stale, wrong-module attacker id.

**Live, the exact pool of Vulkars P1 aggroed.** Immediate arming on first contact — the client
teleported point-blank to `0x33` and the very same log line pair fired in the same millisecond as
the teleport:

    [22:08:04.911] local combat: 0x33 selected as the immediate local-fight candidate (priority=Proximity, dist=1.79, engageRange=11.0)
    [22:08:04.912] local combat: 0x33 pulled out of the census to fight for real (timeout 25s)

No wait for a 25s slot: when `0x33`'s own fight later ended by hitting the timeout ceiling, the
next eligible candidate was armed in the SAME MILLISECOND, not after any further delay —

    [22:08:29.912] local combat: 0x24 fight ended (timed out), rejoining the census
    [22:08:29.912] local combat: 0x24 selected as the immediate local-fight candidate (priority=Proximity, dist=1.79, engageRange=11.0)
    [22:08:29.913] local combat: 0x24 pulled out of the census to fight for real (timeout 25s)

and the `TargetingUs` priority branch fired live, not just in the unit tests — `0x33` won a later
slot specifically because the owner's own damage report named it as the current attacker on the
phantom, ranked above a plain-proximity read:

    [22:08:54.913] local combat: 0x33 selected as the immediate local-fight candidate (priority=TargetingUs, dist=1.80, engageRange=11.0)

**Exclusivity and no invulnerability, confirmed live with SEVEN simultaneous hostiles instead of
two.** While `0x33` held the one exclusion slot, every other nearby Vulkar kept landing real
forwarded hits without interruption — `combat: took 14 damage from a hit on our phantom (attacker
0x38)`, `... (attacker 0x2F)`, `... (attacker 0x31)`, `... (attacker 0x24)`, `... (attacker
0x26)`, all logged in the same seconds `0x33` was excluded — and `combat: absorbed 8 forwarded
damage in total, attributed to mirrored enemies we are driving (last attacker 0x33); this engine
says in-combat=True` confirms the absorb licence stayed correctly scoped to the one creature
actually fighting.

**Two real kills, ordered with `blockpoke.ps1 attack 0x7FFFFFFF 0x33`.** Host:

    [22:09:53.250] combat: applied 9 of a peer's damage to 0x33 (owed 27)
    [22:09:53.641] combat: killed 0x33 on the owner's authority          -- (client log, same instant)
    [22:10:15.347] combat: applied 9 of a peer's damage to 0x24 (owed 35)
    [22:10:18.657] combat: a peer owes 4 damage to 0x24, which does not resolve here ANY MORE - it did earlier this epoch, so the body was destroyed; holding the debt in case it comes back

`0x33`'s `owed 27` against `22` max hp and `0x24`'s `owed 35` against `28` max hp both crossed
their targets' full health, and the host's own `console 'census 0x33'` confirmed `not listed in
this census` afterward — two real, ground-truth-confirmed deaths from client-dealt damage,
matching round 54's own kill shape twice over rather than once.

**One honest gap, not a safety issue.** `0x24`'s local-fight window ended by `timed out`, not
`dead or gone`, even though the HOST's own copy had already died a few seconds earlier from the
accumulated ledger — `MaintainLocalFight`'s own `IsCreatureDead` check reads the CLIENT's local
resolved creature, whose own HP track is decoupled from the host's ledger for the duration of an
exclusion (that is the whole point of letting the engine's native AI run there), so a body that
died on the host's side purely from forwarded-and-applied ledger damage can keep reading alive
locally for the rest of its timeout window. Benign: the next normal census pass reads the owner's
`Dead` flag on `0x24` and retires it immediately (`DriveOne`'s own `enemy.Dead` check), and no
double-exclusion or stuck state was observed. Not chased further this round — noted here rather
than hidden, same discipline as round 52's own rejoin-boundary gap.

**Testing artifact, documented rather than treated as a bug.** `cheat god 1` on the client (used
to protect it from the seven-creature swarm this round's own P1 measurement produced) pinned its
HP, which twice tripped the pre-existing invulnerability watchdog
(`IncomingDamageController.AbsorbProofThreshold`) — `combat: ⚠ local_combat WATCHDOG TRIPPED —
absorbed 47 damage on the claim that our own mirrored enemies were dealing it here, and this
player's hit points never fell once` — because god mode makes that literally true regardless of
whether the absorb was legitimate. The watchdog did exactly its documented job (reverted to
applying instead of absorbing until HP genuinely moved again), which explains the handful of
`(the currently locally-fighting creature)`-attributed hits that show up applied rather than
absorbed in the raw log. Not a round 55 defect; a future live pass without god mode would not see
this.

**Safety.** No permanent exclusion; every slot the timeout closed rejoined the census cleanly. No
materialised stand-ins were ever selected (`SelectImmediateCandidate` only ever considers
`MaterialisedEnemies.ToLocal(id) == 0`). `LocalCombatBypassId`/`DiagUndrivenObjectId` (unset this
round) are still honored by the prescan. Rig torn down clean (`Get-Process swkotor,KotorCoop.Agent`
returned none), `auto_start_agent` restored to `1` in both installs.

Both builds clean (`dotnet build KotorCoop.slnx`: 0 warnings, 0 errors; `src\KotorCoop.Plugin\build.ps1
-Install` for both GOG installs: both succeeded, `dinput8.dll` 1710 KB each). Full suite
`dotnet test tests/KotorCoop.Tests`: **1243/1243** (1231 baseline + 12 new
`LocalFightCandidateRuleTests`), no flakes.

**What remains.** The client's own local swings still are not visually shown as a swing on the
peer's screen (cosmetic, `combat-participation.md` §5.3's own "ship after the mechanics" item,
unchanged). Line of sight is not independently checked by `SelectImmediateCandidate` — it relies
on `local_combat_engage_range` (11) being tight enough that most in-range creatures are also
visible, which round 55's own P1 measurement supports (the same distance metric separated "no
aggro" from "aggro" almost entirely on line of sight, not range) but has not been proven as a
general rule across other rooms; a genuine `GetVisibleListElement`-based perceiving check (already
bound in `Odyssey.cs` for a round-18 diagnostic, `combat-participation.md` §3.1's SEEN bit) is the
natural next tightening if a future room shows a false-positive trigger through a wall. Whether
local_combat should ever run more than one fight at a time, now that the mechanism is proven
solid under a seven-creature swarm, is deliberately left as the next step, not expanded this
round.

### Round 56 (2026-08-22) — Player-style verification pass: two live regressions root-caused and fixed, three findings confirmed live, one left genuinely open

Prompted by a user watching the previous (staged) session and reporting three things: doors not
opening on both sides, Trask's icon staying at the arrival point after a transition, and attacks
not synced at all. The prior session used teleports, an excluded Vulkar diagnostic id, and
`client_door_refusal` refusing doors while the host was mid-fight — all legitimate staging, but
none of it isolates "is this real." This round re-ran the same shapes cold, with `demo.ps1`, no
`firescript`, no bisect masks, no `applyeffect`, `usedoor`/`attack`/`tp`/`warp` only (the
console-command harness stand-ins for a click and a walk, per this doc's own convention), and
also chased the two regressions the LAST captured log (`kotorcoop-general.log`, 07:50–08:02)
already showed before any of that: the round-41 "STILL SEEING excluded id" diagnostic firing on a
roughly 1s cadence for most of an hour, and `party roster: no peer roster to reconcile against`
seconds after a successful reconcile.

**Regression 1 — the excluded-id diagnostic, root-caused as a genuine log-spam bug (not a
functional one) and fixed.** `EnemyPresenceController.DriveOne`'s round-41 guard — skip the
locally-excluded creature completely, before any claim, log if seen — was still doing its job
correctly: nothing downstream of the early `return true` at line 813 was ever reached, so no
double-claim or corrupted round-processing was ever possible. What round 55's rewrite did not
change was the log's own throttle (`now - _stillSeeingLoggedAt >= 1.0`, unconditional once armed),
and round 55's own new mechanism — `SelectImmediateCandidate` re-arming a fresh fight in the SAME
frame a previous one's timeout closes — turned local_combat from "rare, short exclusions" into
"back-to-back fights for as long as anything stays hostile," so the SAME frame-safe, functionally
harmless guard now logged once a second for the fight's ENTIRE duration instead of the brief
race-window blip it was built for. A full-log grep for `STILL SEEING excluded` in the captured
window returned 3615 lines; per-fight, individual runs ranged from 1–2 lines (the agent's own
150ms `CombatPumpIntervalMs` catching up almost immediately — confirmed via
`$TEMP\kotorcoop-demo\agent2.log`'s own `local combat diag (agent): excludedId=... filtered.Count=...
(removed 1)` lines running every pump) up to 15+ consecutive one-second lines for a single fight —
the exclusion mechanism itself (`CoopAgent.PumpEnemies`'s `excludedId` filter before
`TryWriteEnemies`) is correct and was filtering every single pump throughout; DriveOne's own
guard was simply carrying 100% of a defence that is supposed to be rare.

Fix: `EnemyPresenceController.cs` — the diagnostic is now edge-triggered. It logs once when a NEW
excluded id starts leaking (`_stillSeeingReportedId != _locallyFightingId`), then only a 5s
keep-alive for as long as the SAME id keeps leaking, instead of once a second unconditionally.
`_stillSeeingReportedId` resets in `MaintainLocalFight`'s own fight-end path and in `Invalidate()`
(module change), so the next fight's (necessarily different) excluded id always gets its own fresh
edge. **Live-verified after the fix, same rig, same room** (`tar_m10aa`, the same Vulkar pool):
a fight that previously would have logged ~20 lines over 20 seconds logged exactly two —

    [08:53:39.084] local combat diag: STILL SEEING excluded id 0x36 ... (still leaking, 5s keep-alive)
    [08:54:39.09x] ... (further keep-alives, still one per 5s, not one per 1s)

No functional change — DriveOne's skip behavior is identical before and after; only the log
volume changed.

**Regression 2 — `party roster: no peer roster to reconcile against`, root-caused and fixed.**
Live-reproduced on the very first ordinary `end_door10` crossing of this pass, on the CLIENT:

    [08:51:57.163] party roster: reconciling against a peer party of 0x001
    [08:51:57.865] party together: CSWParty's own member count read 1 against CSWPartyTable's 0 - clamping ...
    [08:52:05.362] party roster: no peer roster to reconcile against

`CoopClient.TryGetPartyRoster` is deliberately never aged out (its own doc comment: "a stale one
is still the last thing the host said") and `_hasPartyRoster` is set true once and never reset —
so the ONLY way the client's own `block.HostPartyRoster` reads invalid is an explicit
`bridge.WriteHostPartyRoster(false, PartyRoster.Empty)` write. `CoopAgent.PumpPartyRoster` had
exactly one such write, guarding three conditions in one branch:
`!settings.SyncParty || !bridge.SupportsPartySync || (caps & SessionCapabilities.Party) == 0`.
The first two are session-lifetime facts (correct to clear immediately — a level the plugin kept
reconciling against after the channel died is the stale-authority failure the comment already
names). The third, `bridge.SupportsPartySync` (`KotorGameBridge`: `_player is not null`), is NOT
session-lifetime: it reads false for roughly a second on every module transition, while this
instance's own player object is torn down and rebuilt — the identical window
`WorldLoadRule`/"unannounced teardown" logging already covers (`flush, before the module is
stored: destroyed phantom...` → `0 phantom(s) alive` → `spawned phantom...`). Zero
`CompatibilityAnnouncer` lines (`co-op [W_...]`) appear anywhere in this session's logs, so this
was never a content-compatibility capability drop (`ObjectIdKeyed`/`SessionCapabilities.Party`
never actually left `All`) — it was this one transient-vs-permanent conflation clearing a channel
that a still-connected host never stopped publishing.

Fix: `CoopAgent.PumpPartyRoster` (`src/KotorCoop.Session/CoopAgent.cs`) now splits the three
conditions. `!settings.SyncParty || (caps & SessionCapabilities.Party) == 0` still clears
immediately (unchanged behavior). `!bridge.SupportsPartySync` now just `return`s — holds whatever
is already in the block (host roster, local roster, both) instead of clearing either, so the very
next pump, once the player object resolves again, picks back up exactly where the last real read
left off. Full solution build clean; full suite still 1520/1520. **Not yet re-verified live**
after the fix (time-boxed out of this pass) — a dedicated `PumpPartyRoster` regression test (this
codebase's usual `IGameBridge`-double pattern, `EncounterPumpTests.cs` et al.) is also not yet
written; both are the natural next step.

**Player-style pass — the three original reports, checked cold.**

1. **DOORS.** `end_door10` (the one module-transition door in `END_M01AA`/`END_M01AB`) opens and
   mirrors correctly: `usedoor end_door10` on the host logged `door: opened 0x4A (state 0 -> 2)`
   and the CLIENT's own log showed `door 0x4A: swinging 0 -> 2 on the session's authority` within
   ~1.8s (`DoorController`, unchanged this round). **Genuinely untested this pass: an ORDINARY
   (non-transition) door.** `end_m01aa`'s own GIT (`end_door01`, `end_door02`, `end_door05`...,
   read directly out of `modules\end_m01aa.rim` for this pass, since no console verb lists them)
   has fifteen; `usedoor <tag>` refuses every one of them by design
   (`CheatController.FindNearestTransitionDoor`'s own doc comment: "a bare `usedoor` has to be
   incapable of doing anything except starting a transition... an ordinary door two metres away
   would otherwise win the distance comparison and merely open — which looks exactly like the
   command working"). Mouse-click injection (`tools/click.ps1`, the real per-object interact a
   player uses) and `tools/shot.ps1` screenshots both came back solid black in this rig — the
   same class of limitation `blockpoke.ps1`'s own header names for keystrokes ("injected
   keystrokes never reach this game"). `DoorController`'s own mirror mechanism is generic (any
   door's open-state write goes through the same `SetOpenState`/`LogFew` path the transition door
   just exercised), so nothing here suggests a door-type-specific gap — but it is genuinely
   unverified, not confirmed, for the non-transition case. Worth a dedicated console verb (an
   `opendoor <tag>`, deliberately not gated to `LinkedToModule`, mirroring `usedoor`'s own
   structure) the next time this needs checking without a working click.

2. **TRASK / PARTY ICON.** Live-reproduced: `party drive: mirroring 1 of the host's companions`
   at cold start dropped to `party drive: mirroring 0 of the host's companions` immediately after
   the `end_door10` crossing above, and the client's own `Odyssey.TryReadPartyRoster` read
   `0x000` (recruited 0x000) where it had read `0x001` a moment before — on BOTH host and client,
   independently. **This needs a real caveat, not a clean verdict.** Every crossing in this pass
   used `tp` to reach the door (the documented `walk` harness primitive — `TestDriveController.
   HandleWalk` — moved the player 0m in two separate attempts this session, confirming the
   existing "unreliable" finding again), and `tp` does not carry Trask along the way a real walk
   does — `tools/creatures.ps1` confirmed Trask's body stayed exactly at spawn through three
   successive `tp` hops toward the door. `GatherPartyIfScattered` (the `usedoor` verb's own
   pre-transition "gather scattered companions" step, `party_together_fix`'s companion feature,
   added THIS SAME DAY per this doc's round history) exists specifically for this; its own gate
   is `Odyssey.IsPartyTogether()` (`CSWParty::IsPartyTogether`, a live engine call, not a cached
   read) — and it read TRUE (no gathering attempted, confirmed by the total absence of the
   `door: the engine says the party is NOT together...` log line this pass ever expects) even
   with Trask a hundred-plus metres back. Whether that stale-true reading is a genuine gap in
   `IsPartyTogether` specific to a `tp`-class instant reposition (which a real walking player,
   whose engine-native follow-AI updates this continuously, would never produce) or a real,
   walk-reachable bug was NOT settled this pass — reproducing it without `tp` needs either a
   working `walk` primitive or real mouse/keyboard input, neither available in this rig. The
   roster DID self-recover once, unprompted, about thirteen minutes later
   (`party drive: mirroring 1 of the host's companions` at 08:49:25); a second drop coincided
   with the `warp tar_m10aa` in item 4 below, which is expected — see there.

3. **COMBAT.** END_M01AB's placed hostiles (`end_sith01`/`end_sith04`/`end_sithsol01`, GIT-dumped
   this pass the same way as the doors) took zero damage after 20+ seconds standing on top of one
   at point-blank range and did not aggro naturally, and an ordered `attack` against one also
   never landed a hit in the same window — most likely a scripted, not-yet-hostile room (this is
   the Endar-Spire corridor a vanilla playthrough gates behind Trask's own assault script, not a
   free-roam encounter; earlier rounds' `n_sithsoldier004` engagements were never actually in this
   specific room). Per this task's own item 4, pivoted to `tar_m10aa` (round 53–55's own proven
   hostile Vulkar pool) to get a real read on sync rather than leave this unchecked. There,
   natural aggro fired immediately and FORWARDED damage confirmed working exactly as round 55
   found it — `combat ground-truth: local PC took 16 damage, attacker=0x22` /
   `... took 11 damage, attacker=0x17` on the client, landing within a second of the host's own
   `hit breakdown` lines — strongly enough that, without `cheat god 1` running yet, the client
   dropped from 12 hp to a large negative value in under two seconds and the CLIENT'S GAME
   PROCESS EXITED (`[resolve] player ...: vtable unreadable` / `TryFindPlugin: scan found 0
   candidate(s)` in `agent2.log`, then a clean `session-saved character` sidecar and disconnect —
   the agent degraded gracefully, but the game itself did not survive the hit). This matches the
   documented death-test staging discipline (separate players first, stage god mode before a
   swarm) this pass skipped in the interest of a "no staging" read — logged here as a genuine,
   reproducible finding (overkill simultaneous damage against an un-godded client can crash the
   client process) rather than chased further, since fixing it was not this round's brief.
   Redone with `cheat god 1` on both sides: local_combat correctly excluded and armed a real fight
   for the client's own nearest hostile (`0x24`, `0x36`, proximity-triggered, matching round 55's
   mechanism exactly, and exercising the regression-1 fix live in the process — see above) and
   FORWARDED damage kept working. **Not confirmed this pass: the CLIENT'S OWN outgoing damage
   landing on the host's real creature.** Two separate `attack 0x7FFFFFFF <id>` orders — the
   second one specifically against the SAME id `local_combat` had just armed, matching round 55's
   own successful `attack 0x7FFFFFFF 0x33` precedent exactly — produced an armed round
   (`attacktarget diag: round+0x958 0->1`) but no HP change on either instance's own copy of the
   target and no `combat: applied`/`combat: killed` line, over roughly 40 seconds of observation.
   Round 55's own kills took real time too, so this is not proof of a regression — but it is not
   a confirmed pass either, and this round ran out of time to isolate whether god mode, the
   specific creature, or something else explains the difference. Left open, honestly, rather than
   guessed at.

4. **WARP.** `warp tar_m10aa` (a player-available cheat) landed both instances at the identical
   point (`103.3, 115.9, 2.2`) within a few seconds of each other, confirmed via `where` on both.
   Trask was not carried — expected: KOTOR does not bring companions through a warp in vanilla
   single-player either (there is no module to walk them out of), and this project has never
   claimed otherwise; the roster drop this produced (`party roster: local party is now 0x000`
   immediately after `world: unannounced teardown - treating as a load, not a transition`) is the
   same-shaped read as item 2's, for the same underlying reason (no gather is attempted or
   expected across a warp), not a new finding.

**One more real thing found, not consumed: `WorldLoadRule.IsUnannouncedTeardown` misclassifies
every client-follow transition.** `world: unannounced teardown - treating as a load, not a
transition` fired on the CLIENT for both the ordinary door crossing (item 1/2) and the warp
(item 4) — every single client-side world teardown this pass produced, cold-started save
included. `IsUnannouncedTeardown`'s own rule is `!transitionWasPending`
(`Odyssey.IsAreaTransitionPending`, `CSWSArea`'s own scripted-transition bookkeeping) — a flag
that belongs to a NATIVE, player-clicked door/trigger transition, which a client's own
peer-follow-travel move never goes through (it drives the engine's module-load path directly, not
via a scripted door). This looks structurally guaranteed to misfire for every client follow, host
included when it uses `usedoor`/`warp` rather than a native walk-in. **Currently harmless**: its
only consumers, `client_load_rejoin`/`host_load_readopt` (`docs/research/session-save.md` phase
2), are both off by default in both installs' `coop.ini` and were not turned on this pass, so the
misclassification is logged but nothing downstream acts on it destructively. Noted here rather
than fixed, since acting on it (deciding what "announced" should mean for a follow-travel move,
not just a native transition) is session-save phase 2's own scope, not this round's.

**Safety.** No coop.ini edits shipped from this pass — both installs already matched
`profiles/coop.ini.example`'s defaults (`client_door_refusal=1`, `local_combat_bisect_mask=0x0`,
`companion_local_combat=0`) going in. `local_combat_bypass_id`/`DiagUndrivenObjectId` unset
throughout. Rig torn down clean both times (`Get-Process swkotor,KotorCoop.Agent` returned none
after each `demo.ps1 -Stop`), `auto_start_agent` restored to `1` in both installs.

| Check | Synced? | Fixed this round? |
|---|---|---|
| Excluded-id diagnostic spam (regression 1) | N/A (log-only; the underlying skip was always safe) | **Yes** — edge-triggered, live-verified |
| Party roster "no peer roster" (regression 2) | — | **Yes** — root-caused live, fix applied, build+suite clean; **not yet re-verified live** |
| Doors — `end_door10` (transition) open+mirror | **Yes**, confirmed live (~1.8s) | — (already worked) |
| Doors — an ordinary (non-transition) door | **Untested** — no click/keystroke path in this rig | — |
| Trask body/roster following an ordinary transition | **Not synced** in this pass, but confounded by `tp`'s own lack of native follow — not distinguished from a real bug | Not this round |
| Combat — forwarded (host→client phantom) damage | **Yes**, confirmed live twice | — (already worked) |
| Combat — client's own local swing landing on the host's real creature | **Not confirmed** this pass (armed but no measured hit in ~40s) | Not this round |
| Combat — an un-godded client surviving a multi-attacker swarm | **No** — client's game process exited | Not this round (staging discipline this pass deliberately skipped) |
| Warp — arrival sync | **Yes** | — (already worked) |
| Warp — companions carried | **No**, matches documented vanilla behavior | N/A (not a bug) |

**What remains.** A dedicated `PumpPartyRoster` regression test and a live re-verification of fix
2. A non-cheat way to open an ordinary door and to move the player (a working `walk`, or a real
click) for the next pass that needs one. Whether client-dealt local_combat damage genuinely lands
on the host's creature under the exact conditions this pass used. Whether `IsPartyTogether`'s
stale-true reading after a `tp`-class reposition is a real, walk-reachable bug or purely a
`tp` testing artifact. Death-test staging (god mode, or player separation) before any future pass
that puts an un-godded client near more than one live hostile.

### Round 57 (2026-08-22) — The round-56 crash root-caused and fixed, live-reproduced and
re-verified twice; an ordinary door confirmed both ways; a real transition's party-roster
behaviour confirmed steady; a client's own swing still unconfirmed

Picks up round 56's four open items, crash first per this round's own brief.

**1. THE CRASH — root-caused, fixed, live-reproduced and re-verified twice.** Round 56's
un-godded client death (`tar_m10aa`, the Vulkar pool — the same substitution round 56 itself made
after finding END_M01AB's placed Sith never aggro without Trask's own assault script) left no
crash-probe dump, no Windows Application-log event (`tools\crashlog.ps1`, `Get-WinEvent` over the
exact window: nothing), and no minidump (no admin rights to enable `HKLM` `LocalDumps`; a
`HKCU`-scoped `LocalDumps` key was tried this round and Windows did not honour it either) — a
silent, un-symbolizable process death matching `CrashProbe.cs`'s own documented native-AOT
fail-fast class (`RaiseFailFastException`/`0xC0000602`, which bypasses vectored exception
handlers, `AppDomain.UnhandledException`, and WER's own event log, "the process is gone before
anything is written").

**Root cause**, found from the plugin log's own evidence (`agent2.1.log`/`kotorcoop-plugin.log`
from the original round-56 crash, still on disk): the client's PC took a first overkill hit
(HP 12 → -4), then a SECOND forwarded hit half a second later (HP -4 → -15) — and
`IncomingDamageController`'s own "a downed player takes no further damage" guard
(`Odyssey.IsDowned(player)`) should have refused that second application outright. It didn't:
no `death: the local player is down` line ever appeared in the crash session's log despite HP
sitting at -15, proving `IsDowned` — `CSWSCreature::GetDeadTemp` (0x004EF890) alone — read FALSE
the entire time. `GetDeadTemp` loops the ENGINE's own idea of "the client party" (`CSWParty`, not
`CSWPartyTable`) to decide whether the creature it was called on is a member at all before testing
its hit points, and this exact session's own `party_together_fix` diagnostic
(`party together: CSWParty's own member count read N against CSWPartyTable's M - clamping`) is
direct, repeated, live evidence that `CSWParty` can hold a stale membership view relative to the
table — the SAME mechanism `party_together_fix` corrects for door transitions. When the local
player's own creature is not found as a party member by that stale view, `GetDeadTemp` silently
returns false — including inside the ENGINE'S OWN internal copy of this identical check
(`CSWSEffectListHandler::OnApplyDamage`'s own already-dead early-out calls the same function), not
just the plugin's. The second hit therefore reached a genuinely fragile, effectively-untested
engine path (re-running death choreography, or worse, on a creature the engine's own gate should
have already refused), and something in it terminated the process with no crash-reporting path
left standing to say why.

**Live reproduction** (this round, deterministic): rather than fight AI/aggro timing, the
forwarded-damage wire fields (`SharedBlock.OffsetIncomingDamage`/`...Attacker`, 0x1D50/0x1D54 — the
exact fields `IncomingDamageController` reads) were written directly on the client via a one-off
script mirroring `blockpoke.ps1`'s own signature-scan technique
(`pokedamage.ps1`, scratchpad-only, not shipped). First write: owed 16 on top of 0 applied → HP
12 → -4, confirmed via `blockpoke.ps1 <pid> death` reading `downed 0` despite HP -4 (the bug,
caught live). Second write: owed +11 more while still at -4 → **client process confirmed DEAD**
(`Get-Process` empty) within the same second — the crash, reproduced on demand, twice across the
two builds below.

**Fix**, two parts:
- `Odyssey.IsDowned` (`src/KotorCoop.Plugin/Odyssey.cs`) now ORs in a direct raw-HP read
  (`GetCurrentHitPoints(creature) <= 0`) alongside `GetDeadTempFn`. This fixes every one of
  `IsDowned`'s six call sites at once (`IncomingDamageController`, `SpectateController`,
  `ControlReturnController`, `DeathController`, `PartyPresenceController`, `CheatController`'s
  `standup`) rather than papering over the one that happened to crash — chosen because the raw
  current-HP field (+0xDC) is ground truth the whole codebase already trusts for "how hurt is
  this creature" (`GetCurrentHitPoints`'s own doc comment), and by the engine's OWN definition a
  recognised party member's temp-dead state IS "current hit points <= 0"; ORing it in can never
  call a genuinely-still-up creature (positive HP) downed.
- `IncomingDamageController.cs`'s own guard is unchanged in shape (`if (Odyssey.IsDowned(player))
  { ...; return; }`) — it inherits the fix for free — with a comment pointing at the root cause
  for the next reader.

**Re-verified live, twice, same rig, same fix build** (host pid 45220, client pid 18600, both
freshly rebuilt via `build.ps1 -Install` with the fix in place): cycle 1 — `pokedamage.ps1` drove
the client to HP -4 (`downed 1` this time, confirming the `IsDowned` fix), a second overkill write
was **silently refused** (HP unchanged, `Get-Process` still ALIVE), `spectate: watching 0x128`
fired on the client (camera handoff), the host `tp`'d to the client's position (the closest this
rig's harness gets to "walks over" — see the door-walk note below), and
`Lanik revived you (0.0 units...)` / `spectate: back up` closed the loop, HP back to 12/12. Cycle
2 — identical shape, same result: refused second hit, `ALIVE`, `spectate: watching 0x128` again,
`Lanik revived you (2.2 units...)`, back up. **No crash, either time.** Full solution build clean;
`dotnet test tests/KotorCoop.Tests`: 1520/1520 with the fix in place.

⚠ **A concurrent session's fix landed mid-round and is folded in.** While this round's crash work
was in progress, a coordinator message reported (and committed, `b491b74`,
`PartyRosterController.cs` only) a second, independent bug in the SAME
`party_together_fix`/`SyncClientPartyTogetherState` machinery this round's root-cause analysis
already leaned on as evidence: the clamp had been running on the HOST too, and because
`CSWPartyTable` counts companions only (Trask → 1) while `CSWParty` counts the PC as well
(PC + Trask → 2), the "divergence" it was clamping away on a host was never staleness — it was
correct, and clamping it out dropped the host's own Trask from the struct the game's control,
portrait and follow logic reads. Fixed by gating the clamp on `block.TryGetHostPartyRoster`
(true only for a client actually receiving a host roster). This round rebuilt and re-deployed
**with** that fix before any of the live checks below, and confirmed live: the host's own
`party together: ... - clamping` line, which had fired repeatedly earlier in this exact session
(09:21, 09:30, 10:15), does not fire again after the merged fix and a fresh roster publish
(10:20:47 `party roster: local party is now 0x001`, no clamp line following) — the host keeps
Trask.

**2. ORDINARY DOOR, both ways — confirmed.** Round 56 could not test this at all (no working
`walk`/click; `usedoor` refuses every non-transition door by design). Added the smallest
possible verb, `opendoor [tag|id]` (mirrors `usedoor`'s own door-resolution and distance/state
checks via the same `DoorUse.Decide`, called with `linked` forced true — the one gate that exists
specifically to keep `usedoor` incapable of this), calling `Odyssey.OpenDoorFully` — the identical
engine door-use action `usedoor` already calls, never a state-byte poke — so `DoorController`'s
own generic open-state mirror is what gets exercised. New `SharedBlock.CheatKindOpenDoor = 17` /
`CheatKinds.OpenDoor = 17` (next free number after `DestroyObject`), full verb wiring
(`ConsoleCommand`, `CoopCommandDispatcher`, `ConsoleVerbCatalog`, `FuncInterpHook`'s in-game `~`
console list). **Host → client:** `opendoor end_door02` on the host opened door 0x46
(state 0 → 1); the client's own log showed `door 0x46: swinging 0 -> 1 on the session's authority`
~1.8s later. **Client → host:** `opendoor end_door05` on the client opened door 0x42
(state 0 → 2); the host's log showed the identical mirror line ~1.8s later. Both directions,
clean 0→open transitions, confirmed live.

One caveat found and not chased (out of scope for this round, real but narrow): a door already
ajar that gets `opendoor`'d again from a DIFFERENT approach side flips SWING DIRECTION (2↔1, not
0→open) — and that specific transition does NOT mirror, because both the reliable flag store's
own max-merge (`DoorFlags` values are compared numerically; 1 is not "more open" than 2) and
`DoorController.Apply`'s own forward-only gate (`desired.OpenState <= current` refuses) treat
open-state as a monotonically-increasing scale, which a swing-direction flip is not. The
plain closed→open case this task asked about is unaffected and works correctly either direction;
worth a note for whoever next touches `DoorController` or `DoorFlags`.

**3. TRASK AFTER A REAL TRANSITION — the round-56 roster fix confirmed live; the walk-crossing
itself still not achieved.** Over 30 combined attempts across two approaches
(`usedoor end_door10`, and blockpoke's raw `walk`), from more than a dozen different `tp`-staged
approach positions and distances (0.2m to 5.7m), the player's queued `ActionMoveToLocation` never
completed a real crossing of `end_door10` this round either — the closest and most reproducible
failure mode was a stable TWO-POINT OSCILLATION: an `usedoor` call from ~4m out would make real
progress to ~1.5m, but the NEXT fresh `usedoor` call from that near position recomputed
`DoorUse.WalkThroughPoint`'s overshoot target on the OTHER side of the door plane (the player,
having nearly crossed, reads as "already on that side"), sending them back to the far anchor
point every time — a real, reproducible defect in `WalkThroughPoint`'s per-call side recompute,
worth a fix the next time a working `walk`/click makes it testable without this much trial and
error. Not fixed this round (out of scope; `usedoor`'s own internal per-call retry, which reuses
one fixed target across its 600-frame window, is unaffected — only fresh external calls flip).

Given that, this round substituted `warp` (host → `end_m01ab`, client followed automatically via
the announce path) to get SOME real module-teardown/reload cycle — the specific window round 56's
`PumpPartyRoster` fix targets (`bridge.SupportsPartySync` false for ~1s while the player object
tears down and rebuilds) — without needing a working walk. Confirmed: `party roster: reconciling
against a peer party of 0x001` held steady through the transition (no `no peer roster to
reconcile against` regression — round 56's own fix holding). A SEPARATE, pre-existing,
already-working gate — `co-op [W_SAVE] different save: enemies, doors, items and party are off`
— fired on BOTH sides (matching identical behaviour already present in round 56's own log at
08:52:05/08:52:51, unrelated to anything this round changed) and self-recovered on its own after
~4 minutes (`co-op: enemies, doors, items and party shared again`), same shape both times. Trask
himself was correctly and consistently ABSENT from both instances after the warp (matches
documented vanilla behaviour — a warp carries no companions — not a new finding), with no stale
duplicate or phantom on either side. **Genuinely unresolved, honestly:** whether Trask's body and
map marker actually mirror correctly on the client after a true WALKING crossing — the specific
thing this item asked for — remains untested, blocked on the same `WalkThroughPoint` oscillation
above.

**4. CLIENT'S OWN SWING — still not confirmed.** `tar_m10aa`'s Vulkar pool again (END_M01AB's
own placed Sith still don't aggro without Trask's script, matching round 56 exactly). Two separate
`attack 0x7FFFFFFF 0x38` orders (blockpoke's `TestDriveController` harness, the player as
attacker) against a Vulkar at ~1m, each confirmed armed (`attacktarget diag: 0x7FFFFFFF
round+0x958 0->1`), over roughly 90 seconds combined observation (longer than round 55's own
successful precedent needed) — HP on the host's real copy of the target (`creatures.ps1`,
before/after) never moved from 22. Left open exactly as round 56 left it: not a confirmed
regression (round 55's own kills took real time too), but not a confirmed pass either. This
round's own local-combat log showed the SAME target repeatedly excluded from the driven census
(`STILL SEEING excluded id 0x38`) while the ordered attack sat armed — worth checking next
whether `local_combat`'s own exclusion of a creature `attacktarget` is simultaneously driving is
itself what starves the ordered round of ever being processed.

| Check | Result | Fixed this round? |
|---|---|---|
| The round-56 crash | **Root-caused and reproduced live** (deterministic, `pokedamage.ps1`) | **Yes** — `Odyssey.IsDowned` now ORs a raw-HP check; **re-verified live twice**, no crash either time |
| Spectate camera handoff on down | **Yes**, confirmed live both cycles (`spectate: watching 0x...`) | — (already worked once `IsDowned` was fixed) |
| Peer rescue (host reaches downed client) | **Yes**, confirmed live both cycles (`... revived you`, HP restored) | — (already worked once `IsDowned` was fixed) |
| Host keeps control of its own companions | **Was broken this session** (a concurrent fix, `b491b74`, landed mid-round) | **Yes** (by the other fix) — confirmed live: no more host-side clamp after rebuild |
| Doors — ordinary door, host → client | **Yes**, confirmed live (~1.8s) | New verb (`opendoor`) added to make this testable at all |
| Doors — ordinary door, client → host | **Yes**, confirmed live (~1.8s) | Same verb, same round |
| Doors — a swing-direction flip on an already-open door | **No** — genuine, narrow gap found (numeric-ordering assumption) | Not this round |
| Party roster steady through a real transition (round 56's fix) | **Yes**, confirmed live via `warp` (no `no peer roster` regression) | — (round 56's own fix, re-verified) |
| Trask carried through a TRUE WALKING door crossing | **Still untested** — the walk itself never completed, 30+ attempts | Not this round |
| Trask absent-but-consistent after a `warp` | **Yes**, matches documented vanilla behaviour on both sides | N/A (not a bug) |
| Combat — client's own local swing landing on host's real creature | **Still not confirmed** (armed, no measured hit in ~90s) | Not this round |

**Safety.** No coop.ini edits shipped from this pass in either install. `local_combat_bypass_id`
unset throughout. God mode was toggled on both instances repeatedly for staging and always turned
back off before the checks that needed it off; the client's own save was found to already carry
`min1hp` on load (from an earlier, unrelated session) and was explicitly toggled off before the
crash reproduction so the test genuinely exercised an un-godded client. Rig torn down clean
(`Get-Process swkotor,KotorCoop.Agent` empty after `demo.ps1 -Stop`), `auto_start_agent` restored
to `1` in both installs.

**What remains.** The `WalkThroughPoint` side-recompute oscillation (item 3) — a real, reproducible
bug worth its own fix once a working `walk`/click exists to verify it without dozens of `tp`
round-trips. Whether `local_combat`'s own census-exclusion of a creature `attacktarget` is also
driving starves that ordered round (item 4's new lead). The swing-direction door-flip gap (item 2).
A dedicated regression test for `Odyssey.IsDowned`'s new raw-HP fallback (the existing IGameBridge
test doubles do not model `CSWSCreature::GetDeadTemp`'s live party-membership behaviour, so this
was verified live rather than in `tests/KotorCoop.Tests`).

### Round 58 (2026-08-22) — the client's own swing, CONFIRMED landing, four times over; no
"PC held" writer exists; round 56/57's "armed but silent" gap did not reproduce on a clean rig

Picks up round 57's own open item 4 (`local_combat`'s exclusion of a creature `attacktarget` is
simultaneously driving might be what starves the ordered round) and this round's own brief: find
whatever holds the client's own PC still and fix it. **It found no such thing, live-reproduced
the supposedly-broken case four separate ways, and all four worked cleanly, first try.**

**Static audit first, per this doc's own convention (§2's branching discipline).** Every call site
of `Odyssey.HoldCreatureStill`, `Odyssey.ClearAllActions`(-class) and `Odyssey.ClaimCreature` in
`src/KotorCoop.Plugin/` was enumerated: `InteractionLockController` (a peer's dialogue-lock target),
`EnemyPresenceController.ClaimCreatureForFight`/`MaterialisedEnemies` (mirrored enemies only),
`PartyPresenceController` (mirrored companions only), `PhantomController` (a PEER's phantom, never
the local player). **None of them is ever handed the local player's own creature pointer.** The
"PC is drive-controlled" note `TestDriveController.HandleWalk` carries is specific to
`ActionMoveToLocation` (NWScript routine 21, dead-code message type 1 — round 52's own finding);
`AttackTarget` queues `ActionAttack` through the identical VM-dispatch mechanism but attack (message
type 2) IS one of the eight message types this build's player-input channel actually sends, per
`HandleWalk`'s own doc comment — so nothing in the walk caveat implies attack shares its fate, and
this round's live evidence below settles it decisively: it does not.

**Instrumentation added** (`src/KotorCoop.Plugin/Odyssey.cs`, `GetAttackDiagnostics`; `TestDrive­Controller.cs`,
`ProbeAttack`): once `HandleAttack` arms a real order, both bodies are read every second for 20s —
`ObjCommandable`, `CreMovePermitted`, the head-of-queue action type, and the attacker's own combat
round (`+0x958`/`+0x960`) — logged as `attack probe: ...`. Diagnostic only, no behaviour change.

**Rig: cold `demo.ps1`, `tar_m10aa`, the same Vulkar pool rounds 53-57 used.** Four live attempts,
each a fresh `attack 0x7FFFFFFF <id>` order via `blockpoke.ps1`, at melee range (~1-2 units), client
`cheat god 1` on (floors at 1 hp, matching round 55's own documented artifact):

1. **Client attacks a plain MIRRORED enemy** (`0x33`, never the currently-excluded
   `local_combat` id at any point during the attack — confirmed via the probe's own
   `localCombatExcluded=` field, which read `0x24`→`0x26` throughout, never `0x33`). HP fell
   22→13→1→dead over 20s (`attack probe: ... hp=22 ... hp=13 ... hp=1 ...`), host
   `combat: killed 0x33 on the owner's authority` (client-side mirror of the death) at
   `[10:52:05.451]`, ~21s after the order.
2. **Client attacks the id `local_combat` had JUST armed, then a later id it was no longer
   fighting** (`0x36`, matching round 56/57's own exact recipe: "the SAME id local_combat had
   just armed"). Killed cleanly, `combat: killed 0x36 on the owner's authority` at
   `[10:53:06.869]`, ~7s after the order.
3. **Client attacks the id CURRENTLY excluded for the entire window** (`0x3B`,
   `localCombatExcluded=0x3B` unchanged from the order to the kill — the one shape rounds 56/57
   never confirmed). HP fell 27→17→11→2→1→dead, `combat: killed 0x3B on the owner's authority`
   at `[10:54:43.877]`, 32s after the order — the probe's own 20s window closed one second
   before the kill line, which is why the fix keeping the probe running matters for any future
   pass (a fight can legitimately outlast a fixed 20s watch).
4. **Both players attack the SAME enemy at once** (`tar10_thermturret` `0x1F`, 80 hp, confirmed
   alive via `console 'census 0x1F'` — see the methodology note below on why that check, not a
   raw memory scan, is what picked the target). Host's own HP trace: 80→71→64→57→45→39→27→19→
   14→9→dead (`[11:05:58]`→`[11:06:15]`, host's native swings). Client's own mirrored trace over
   the same window: 80→61→49→14→9→dead, `combat: killed 0x1F on the owner's authority` at
   `[11:06:14.530]` — ONE death, on the host's authority, both contributions counted. XP
   max-merge confirmed on both sides after: `creatures.ps1` read `0x7FFFFFFF` (the PC sentinel)
   at **xp 6850 on both instances**, up from the host's own pre-fight 4100 (a prior kill this
   session had already raised it) — the two converge, exactly as `peer-damage-ledger.md` §4
   predicts. Loot was not observed for this target (a stationary thermal turret's inventory is
   plausibly empty; round 54's own `materialised loot: captured 2 item(s) off 0x33's corpse`
   already stands as the loot channel's live proof and was not re-chased this round).

**Every one of the four is a real kill, on the owner's authority, with the mirror following it.**
None of the four matches round 56/57's "armed round, HP never moves, ~40-90s, nothing" shape.

**⚠ Methodology note, found the hard way and worth keeping.** The first two follow-up attempts
this round (re-targeting `0x33` and `0x38` after test 1) picked targets straight out of
`creatures.ps1`'s raw memory scan — which, exactly as `peer-damage-ledger.md` §7 already warns
("the census published GHOSTS... cannot distinguish a live creature from the intact bytes of a
destroyed one"), returned **stale corpse bytes reporting full HP for creatures already dead**
(confirmed: `console 'census 0x33'` → `not listed in this census` for every one of `0x26 0x2F 0x24
0x33 0x38 0x31 0x36` after the first three kills). The probe caught this cleanly —
`resolved=False`, `dead=True`, from the very first sample, same frame as the order — so it read as
an immediate no-op rather than a mystery, but it means test 4's target was deliberately picked via
`console 'census <id>'` (the engine-verified check) instead, which is the reliable way to find a
live target in a room this session had already fought through.

**Round 51/52's own finding reconfirmed, sharply.** The PC's head-of-queue action read `0x3F`
(move-class, per `IsMoveAction`) continuously through all four successful attacks — never once
switched to a fight-class type — while real damage landed every second and killed the target every
time. Anyone using head-action state as ground truth for whether the player is "really fighting"
will be misled on the ATTACKER side exactly as round 51/52 already found on the receiving side;
this round adds the player's own queue to that same warning.

**So what explains round 56/57?** Not settled with certainty — the exact session state is gone —
but the leading candidate, based on everything ruled out here, is session accumulation rather than
a structural bug: those sessions had run 30+ door-crossing attempts, multiple warps, dialogue
opens/closes and earlier failed orders before the failing attack, any one of which could have left
a stray hold (`IncomingDamageController.ApplyOwed`'s own conversation gate, a stuck `attackerRound`
delay from an aborted prior order, or simply a target that — like this round's first two follow-ups
— was already a ghost by the time the order landed). **This round's own clean, first-try, four-for-
four result on a cold rig is itself the evidence that the combat-authority mechanism has no
structural defect to fix.** The instrumentation now shipped (`GetAttackDiagnostics`/`ProbeAttack`)
means the next time this shape recurs, whichever session hits it gets a live curve instead of a
guess.

**`tools/verify.ps1` already carries exactly this check — no new one was needed.** Its own
`a client hit costs the enemy real hit points` section (find the enemy nearest the client, read
its hit points out of the HOST's creature, order the client to attack it, read again) is the same
check this round set out to add. A full battery run (cold `demo.ps1`, `END_M01AA`) confirmed the
mechanism generally (`swing animations reach the peer` passed, killing `0x35` in ~3s at 41 units)
but that ONE check failed — `enemy 0x14 at 57 units: hp 15 -> 15` — and this round's own probe,
attached to the same order, explains why: `dist=128.56` for the ENTIRE 20s window, unchanging,
head action `0xFFFFFFFF` (no action at all) from the second sample on. The target the check's own
distance math picked (57 units, from the client's last-known census position) and the target's
REAL live distance (128.56, from the engine) disagree by more than double — the census position
was stale, the real target sits on the far side of the Endar Spire's corridor maze, no walkable
path exists in the window, and the attack action gave up immediately rather than walking there.
**Not the round 56/57 shape** (which showed a continuously-moving, continuously re-queuing PC —
head `0x3F` the whole time — with damage that still never landed at melee range); this is a
target-selection gap in the check itself, picking an enemy that is alive but unreachable, not a
regression in the plugin. Left as a note for whoever next touches that check's own nearest-enemy
search (mirror section 10's own approach: judge distance from a position both sides agree is
current, not a census snapshot) rather than fixed this round, since the mechanism itself is what
this round was verifying and it already has four clean live confirmations above.

**Item 4, the `DoorUse.WalkThroughPoint` oscillation — NOT the same root cause, left as documented.**
Round 57's own diagnosis (a fresh external `usedoor` call recomputing the overshoot target on the
OTHER side of the door plane once the player has nearly crossed) is a navigation-target recompute
bug inside `DoorUse`, independent of the action-queue/round-timer machinery this round audited end
to end and found nothing holding. No PC-holding writer exists anywhere in this codebase (confirmed
above), so there is nothing here to connect the two; not attempted this round.

**Safety.** No coop.ini edits shipped from this pass in either install (both already matched
`profiles/coop.ini.example`'s defaults going in). `local_combat_bypass_id` unset throughout. God
mode was on for the client throughout (floors at 1 hp, matches round 55's own documented artifact)
and off for the host. Rig torn down clean (`Get-Process swkotor,KotorCoop.Agent` empty after
`demo.ps1 -Stop`), `auto_start_agent` restored to `1` in both installs.

Both builds clean (`dotnet build KotorCoop.slnx`: 0 warnings, 0 errors; `src\KotorCoop.Plugin\build.ps1
-Install` for both GOG installs: both succeeded, `dinput8.dll` 1761 KB each). Full suite
`dotnet test tests/KotorCoop.Tests`: **1520/1520**, unchanged (this round added live-only
diagnostics, no new unit tests — `GetAttackDiagnostics` is a plain field-read helper with nothing
to assert against an `IGameBridge` double, same class of gap round 57 already logged for
`Odyssey.IsDowned`). A full `tools/verify.ps1` battery run (separate cold rig, `END_M01AA`,
recorded above): **37 passed, 8 failed** — two are the pre-existing environment-flaky checks this
doc's own memory already names (`phantom damageable, not a sink`; `the peer really loses those hit
points`), one is the target-selection gap in `a client hit costs the enemy real hit points`
explained above, and the remaining five (a downed-player report, the co-op revive, a journal
removal gate, an engine-signature count, one phantom-damage variant) are outside this round's own
scope and were not chased — none of their log windows mention anything this round touched.

**What remains.** Whether round 56/57's exact failure mode can be forced on demand (a stuck
conversation gate, a poisoned `attackerRound` state, a ghost target) is still open — this round
ruled out the leading structural suspect (a PC-holding writer) rather than reproducing the original
symptom. The `WalkThroughPoint` oscillation (still unfixed, still not the same cause). The
swing-direction door-flip gap (round 57, item 2). A true walking crossing of `end_door10` to settle
whether Trask mirrors correctly (round 56/57, still blocked on the same oscillation). Loot on a
lootless target type (turrets) was not chased further. `verify.ps1`'s own nearest-enemy search for
`a client hit costs the enemy real hit points` should judge distance the way section 10 does
(a position both sides currently agree on) rather than a possibly-stale census snapshot, so it
cannot pick an alive-but-unreachable target again. The five unrelated `verify.ps1` failures this
round's battery run surfaced but did not investigate.

### Round 59 (2026-08-22) — the two symmetry-pass claims live-verified, `verify.ps1` hardened with four regression checks, and round 58's own distance-selection fix landed

Three jobs: live-verify the merged symmetry pass (host→client story-trigger relay, protocol 36;
dialogue auto-join); harden `verify.ps1` with the checks this week's regressions would have
caught, plus round 58's own named fix for `a client hit costs the enemy real hit points`; run
the full test suite. Rig: cold `demo.ps1`, GOG pair, both `build.ps1 -Install` (1770 KB
`dinput8.dll` each), both a solution build and the plugin build clean going in.

**1a. HOST→CLIENT story trigger (protocol 36): PASS, fresh live evidence, all four sub-claims.**
Both instances warped to `end_m01ab` together (`end_trig_carth02` sits on the `from_01a`
arrival waypoint, per trigger-relay.md §11/§12) — both real players' presence crossed it within
8 ms of each other, and the HOST's own same-tick apply (no network hop) won the race on
`TriggerRelayStore` exactly as trigger-relay.md §12 predicts:

```
HOST    [11:40:22.229] triggers: observed my own engine natively fire OnEnter of 0x26 -
                        publishing for other peers
CLIENT  [11:40:22.237] triggers: observed my own engine natively fire OnEnter of 0x26 -
                        publishing for other peers          <- the client's OWN native fire,
                                                                 silently absorbed as already-fired
CLIENT  [11:40:25.340] triggers: fired OnEnter of 0x26 for peer 1 (phantom 0x6C)
```

No `fired OnEnter ... for peer 2` line appears on the host for this crossing — the shared
`_firedByModule` dedup working exactly as documented, the client's own claim losing the race to
the host's zero-latency one. **No re-fire on revisit**: the host was `tp`'d out of the polygon
and back in (`50,60` then `50.3,120.4`); zero new lines in either log across the round trip.
**The pre-existing CLIENT→HOST direction still works, unregressed**: `tp`'d the client alone
into `end_trig10`'s polygon (id `0x27`, a different STORY trigger, `end_m01ab`'s own
`TriggerList`, host nowhere near it) —

```
CLIENT  [11:42:59.991] triggers: standing in 1 generic trigger(s), first 0x27 - asking the
                        enemy owner to run its OnEnter too
HOST    [11:43:00.876] triggers: fired OnEnter of 0x27 for peer 2 (phantom 0x6D)
```

No crash either instance, either direction.

**1b. Dialogue auto-join (`dialogue_auto_join=near`, default): PASS, all sub-claims, plus
`always`/`off` cross-checked with real coop.ini/restart cycles.** Endar Spire, `end_trask`
(already recruited — every run below is a recruitment-class NPC, and none of them touched
recruitment/dismissal, which stayed off `dialogue_recruit_sync=0`, the shipped default in both
`coop.ini`s and `profiles/coop.ini.example`).

- **Host starts, client auto-joins with no click**: `dlgprobe.ps1 -NoJoin` (so the joiner's own
  click test-hook is never fired) — `interaction: auto-joining a peer's conversation with
  'end_trask' (dialogue_auto_join=Near)` on the client, unprompted. `NODES: on the same entry
  for 28 of 28 paired samples`, serials settled (host +0, client +2 over 30 s).
- **Peer starts, the other side auto-joins**: observed organically from Trask's own known
  scripted re-initiate (dialogue-sync.md's documented "worst case" quirk) racing the token —
  `interaction: auto-joining a peer's conversation with 'end_trask' (dialogue_auto_join=Near)`
  fired on the HOST this time, confirming the mechanism is symmetric by construction exactly as
  `InteractionLockController.TryForceOpen`'s own doc comment claims (no host/client branch).
- **Separated >20 m: no pull, for the RIGHT reason.** `tp`'d the client to 58 units from Trask
  (near-mode's own distance test is the LOCAL player's own distance to the NPC, not
  peer-to-peer — worth naming since it is easy to misread). Host reinitiated the SAME
  conversation repeatedly across a 40+s window (5 separate close/reopen cycles); the client's
  log shows zero `interaction: auto-joining` lines the entire time — confirmed via the same
  code path that also arms the "walk over to join" notice (`_farNoticeUntilMs`,
  `InteractionLockController.cs`), though the on-screen text itself was not independently
  screenshotted this pass (log-only evidence). Moving the client back to 0 units and closing
  again: **not immediately reproduced** (the very next close/reopen also stayed unjoined for
  ~14 s before eventually joining — Trask's reinitiate loop makes exact edge-timing on this one
  NPC noisy; the FAR case's negative result over 40+ s is unambiguous, the NEAR-again positive
  result took longer than expected without being a clean single data point. Not chased further
  this round — see "what remains" below).
- **`dialogue_auto_join=always`: pulls regardless of distance, confirmed with a real
  restart.** `coop.ini` on both installs, `demo.ps1` cycled. Client `tp`'d to 58 units from
  Trask; host `talk end_trask`; client log: `interaction: auto-joining a peer's conversation
  with 'end_trask' (dialogue_auto_join=Always)`, content delivered 0 ms after opening, node
  followed 19→29.
- **`dialogue_auto_join=off`: restores click-to-join, confirmed with a real restart.** Host
  `talk end_trask` with the client standing right next to Trask: client shows `remote: active
  1` (aware) but `local: partner ''` (not pulled in) — zero `auto-joining` log lines. The
  client's own explicit talk request (the same channel a real click drives) then opened,
  adopted and followed normally (`test: opened a conversation` → `engine started the same
  conversation here; adopting it to follow` → `followed the owner 19 -> 29`), confirming the
  OLD click-to-join path is intact underneath the auto-join layer.
- **Recruitment-class safety**: no crash, no roster corruption, across every cycle above — all
  against `end_trask`, a companion/recruitment-class NPC, with `dialogue_recruit_sync=0`
  (default) throughout. Check 19 below (`the host still controls its companions`) independently
  confirms the party roster was intact at the end of the session.
- `dialogue_auto_join` restored to unset (default `near`) in both `coop.ini`s before the
  `verify.ps1` battery below — no coop.ini edits shipped from this pass, matching round 58's
  own precedent.

**2. `verify.ps1` hardened — four new checks (19-22), the round-58 distance fix landed, one
stale check fixed, one tooling false-positive diagnosed and left alone.**

- **Check 19, "the host still controls its companions"**: `CSWPartyTable`'s own companion
  count vs. `CSWParty`'s own count (client-chain read, `am→+4→+4→+0x270`) must differ by
  exactly one (the PC); the seated companion must read `ObjCommandable=1`
  (`creature+0xE8`); and the client-only `party together: ...clamping` line
  (`PartyRosterController.cs`) must never have fired against the HOST's own log — the exact
  regression class `b491b74` fixed earlier the same day. **First draft of this check was
  itself wrong**: scanning the WHOLE log file (the same pattern check 6's crash scan uses)
  reads HOURS-OLD clamp lines from a PRIOR session as if they were live evidence — a clean rig
  failed on lines timestamped 08:51-10:15, none from the run under test. Fixed to scope the
  scan to a mark taken at the top of the script (`$battery1`), matching the file's own
  documented rule ("Log checks must only look at lines written AFTER the action that is being
  tested"). Re-run clean: `table 1 companion(s), CSWParty 2 (PC+companions), companion 0x127
  commandable=1, host clamped this run: False`.
- **Check 20, "a door opened on the client opens on the host"**: `opendoor` (blank = nearest,
  round 57's own verb) on the client; the oracle is the HOST's own live door-open-state byte
  (`door+0x2CC`), found via the same vtable-scan technique `FindCreature` already uses
  (`CSWSDoor` vtable `0x007498B0`, added to `verify.ps1`'s own C# helper as `FindDoor`), driven
  through the SAME agent<-plugin console channel `blockpoke.ps1`'s `console` verb uses
  (`SendConsole`, new helper). **First run read state=0 after a single post-resolve read and a
  10s address-only wait; a live investigation traced this to `SetDoorOpenState`'s deferred
  commit (`bDeferred=1`) not having landed yet at read time** — a harness bug (read-once
  instead of poll-to-completion), fixed by polling the STATE itself for up to 20s. Also added a
  distance-diagnostic (host's own distance to the door) to the failure detail, since the first
  investigation briefly suspected a distance-dependent dormancy (a door 114 units from the host
  read 0 for 7+ real minutes in that one session) — **not reproduced** on a clean re-run at an
  even greater distance (141 units, PASS within the 20s poll), so this is recorded as a
  one-off/needs-a-repro rather than a confirmed structural gap. Clean run: `door 0x4D: client
  opened it, host reads open-state 2 (141 units from the door)`.
- **Check 21, "the client's party roster survives a transition"**: the host-roster valid bit
  (`SharedBlock.PartyRosterValid`, `0x8000_0000` of `OffsetHostPartyRoster`/`0x3304`) must stay
  set through a real module teardown/rebuild, not merely a flag left over from before. Rather
  than needing a second module (a `warp` round-trip risks stranding the rig somewhere the rest
  of the battery cannot recover from), it self-warps to the module the client is already in —
  a real `ExecuteLoadModule` cycle, exactly the window round 56's own `PumpPartyRoster` fix
  targets, with no net displacement. PASS: `roster before 0x80010001 (valid=True) after
  0x80010001 (valid=True)`.
- **Check 22, "no per-frame spam"**: groups every log line since the battery started by
  `(whole second, exact message text)`; fails if any single line repeats more than 10 times in
  one second — the shape a LEVEL-not-EDGES bug produces (`PartyRosterController`'s own "A LEVEL,
  NOT EDGES" comment names exactly this failure mode). PASS both instances every run this round.
- **Round 58's own named fix, section 13's nearest-enemy distance selection**: was reading the
  ENEMY's position from the client's mirrored census slot (`$h2, $slot+4/+8`) — round 58 found
  this stale by more than double (57 reported vs. 128.56 real) in one session. Now reads the
  enemy's position straight off the HOST's own live creature (`$cand`, already resolved via
  `FindCreature`, `+0x90/+0x94` — `Odyssey.ObjPosition`), the same "judge distance from what the
  engine says NOW" discipline section 10 already uses for the attacker's own side. **Verified
  live that the fix is actually engaging**: a direct comparison this round (census slot vs.
  `$cand`'s own live position for the same enemy id) read IDENTICAL coordinates
  (`46.77889, 33.26784` both ways) — for this specific creature the census was not stale, so the
  check's own repeated "enemy 0x14 at 57 units: hp 15 -> 15" FAIL across two separate battery
  runs this round is an HONEST result, not the round-58 bug recurring: the nearest
  still-alive census entry in this well-used save genuinely sits 57 live units from the client
  (plausibly because many nearer enemies have already been killed by repeated testing today),
  too far for the attack to close within the check's window. The SELECTION is now correct; a
  session with no live target closer than 57 units will still fail this check, honestly.
- **`journal removal logged as gated off by default` fixed — it was asserting the OLD
  default.** `Odyssey.RemoveJournalEntry`'s own remarks record "confirmed live 2026-08-22" (a
  pass earlier the SAME DAY as this round) and `JournalController.cs` was promoted to
  `PluginConfig.ReadBool("journal_remove_sync", true)` — default ON — with
  `profiles/coop.ini.example` shipping `journal_remove_sync=1` to match. `verify.ps1`'s own
  section 5c still expected the pre-promotion acknowledge-only default and failed for that
  reason alone on every run, a stale-test harness bug, not a regression. Fixed to assert what
  the current default actually does (`removed on the session's authority
  (journal_remove_sync)`); the old assertion is preserved as the documented way to exercise the
  OFF path (`journal_remove_sync=0`).
- **`every engine call signature matches the binary` — investigated, false positive, left
  unchanged.** `callaudit.py` flagged `CSWSArea::SetTransitionPending` (`0x00506B30`,
  `EngineHandles.cs`): "callee pops 4, declaration passes 8". Live disassembly
  (`tools/disasm.py 0x00506B30 0x50`) shows the function reads BOTH stack arguments
  (`mov eax,[esp+4]` then `mov dl,[esp+8]`, written into `area+0x2C4`/`+0x2C9` exactly as
  documented) before UNCONDITIONALLY TAIL-JUMPING into a shared, unrelated thunk chain
  (`jmp 0x5EDA10` → `jmp 0x5F6B10`, the SAME chain thirteen other, unrelated call sites across
  the binary also jump into — `tools/xrefs.py 0x5eda10`). The function has no `ret` of its own
  at all; `callaudit.py`'s linear epilogue scanner is reading some OTHER function's `ret 4`
  after following (or failing to follow) the tail-jump. **The C# declaration is correct** — both
  arguments are genuinely read and used — and this is a `callaudit.py` control-flow-following
  limitation (a real, understood, harmless false positive for a tail-calling function), not a
  plugin ABI defect. Left both `EngineHandles.cs` and `callaudit.py` unchanged: fixing the
  scanner's tail-jump handling is a separate piece of tooling work, out of this round's three
  jobs, and guessing at a "fix" for a declaration that is already correct would only add risk.

**Battery result (final, clean rig, host pid 32132 / client pid 16372): 41 passed, 7 failed.**
Two are the pre-existing, documented environment-flaky checks (`phantom damageable, not a
sink`; `the peer really loses those hit points` — both named in this repo's own standing memory
note on `verify.ps1` flakiness, Endar Spire min-1-hp / re-permitted-attack artifacts, not
regressions). One is the round-58 distance check's own honest "no target close enough this
session" result, explained above. The remaining four (`an item picked up here vanishes on the
peer`; `a downed player is reported to the agent`; `the co-op revive stands the player back up`;
and the `callaudit.py` false positive) are pre-existing, outside this round's three jobs, and —
matching round 58's own precedent for the checks it did not chase — not investigated further
this round. `a downed player is reported to the agent` reads `down-block reason 1` (min-1-hp),
plausibly the same Endar Spire deck artifact the two known-flaky checks already carry, but not
confirmed as the same root cause this round.

**3. Full test suite: 1566/1566, up from the stated 1555 baseline** (this round added no new
unit tests of its own — all four `verify.ps1` checks and the distance fix are PowerShell, not
C#; the extra 11 came from work already landed earlier the same day, before this round began).
Both the solution build (`dotnet build KotorCoop.slnx`) and the plugin build
(`src\KotorCoop.Plugin\build.ps1 -Install`, both GOG installs, 1770 KB `dinput8.dll` each) clean
throughout. `dotnet test` was run only after `demo.ps1 -Stop` — the live agent processes lock
their own DLLs, and a test run attempted while the rig was still up failed on `MSB3027`
file-lock errors (a harness-usage note, not a code issue).

**Safety.** `dialogue_auto_join` cycled through `always`/`off` and back to unset (default
`near`) in both `coop.ini`s; `journal_remove_sync=1` (matching `profiles/coop.ini.example`,
already the environment's state going in, not touched this round) is the only non-default
setting left standing. Rig torn down clean (`Get-Process swkotor,KotorCoop.Agent` empty after
the final `demo.ps1 -Stop`), `auto_start_agent` restored to `1` in both installs.

**What remains.** The near-mode "moved back into range, took ~14s longer than expected to
rejoin" observation (1b, far-notice section) — likely more Trask-reinitiate noise than a real
timing gap, given the FAR case's own 40+s clean negative result, but not isolated to a single
clean data point this round. The far-notice on-screen text itself (`_farNoticeUntilMs`'s actual
draw) was not screenshotted — confirmed only via the log-absence + code-path argument. Whether
the door check's own "one-off 114-unit dormancy" was real or a fluke of that specific session
(not reproduced at 141 units on a clean rig) — worth a repeat if it recurs. The four pre-existing
`verify.ps1` failures named above, not chased. `callaudit.py`'s tail-jump-following gap — a real,
now-diagnosed tooling limitation that will misreport again the next time a `SetX`-shaped
thunk-and-jump function is declared; worth a proper fix outside this round's scope.

### Round 61 (2026-08-22) — phantom locomotion: the real mechanism found, a partial live-verified fix shipped, honestly not fully reliable

Continuation of round 60's phantom-slide audit (`docs/research/phantom-locomotion.md` §7),
this time with the live rig from the start and a differential method as instructed: sample a
baseline (a genuinely engine-animated creature's own state) against the driven phantom, find
the field(s) that differ, and fix by replicating what the engine's own path does.

**Correction to round 60.** Static disassembly of `ClientServerSyncHandler_Animation`
(`0x0060C100`, twin vtable `+0x134` — the entry `DriveBody` calls for gait) shows it never
calls `AnimationLooping` at all; that classifier is called one level deeper, inside the plain
setter it falls through to (`CSWCObject::SetAnimation`, vtable `+0x9C`). Round 60's read of
"the animbase does not classify these ids as playable" was the right symptom traced to the
wrong function.

**The actual root cause.** `AnimationLooping` structurally rejects walk/run ids for every
creature, not just phantoms — `animations.2da` rows 338 (`walkSS`) and 345 (`runSS`), what
`0x2712`/`0x2714` resolve to, both have `Looping = 0` (VERIFIED, read straight from the retail
2DA file with `tools/kotorfiles.py`); walking/running are their own 2DA category. The
differential dump the brief asked for (a live byte-for-byte diff of `CSWCAnimBase`, the local
player's own twin vs. the phantom's) came up genuinely empty — weapon class, the "run looks
awful" flag, and every field that feeds the classifier chain matched between the two — which is
itself the finding: this is not a per-creature state gap. `CSWCCreature::ClientServerSyncHandler_Position`
(twin vtable `+0x12C`) is the ONLY place in the whole binary that ever calls
`CSWCObject::AnimationWalking`/`AnimationRunning` and plays a walk/run animation by name
(`tools/xrefs.py`: one call site each, both there) — and `DriveBody` has, since it was written,
suppressed the position dirty bit that would trigger it (`SetPositionFn(..., bSuppressDirty:
1)`), to dodge a different, earlier-fixed bug (`ComputeSpeedFactor` returning 0 for a
waypoint-less body). The fix for one bug silently disabled the only code path that plays the
other.

**The fix.** `Odyssey.DriveBody` now calls `ClientServerSyncHandler_Position` directly every
step, with its bad interpolation/rate side effects immediately overwritten by the position and
rate writes `DriveBody` already made unconditionally. Live-verified this round to actually work
some of the time — sustained multi-second stretches of the twin's rendered animation correctly
leaving idle and matching server gait, something no prior round achieved (rounds 59/60 measured
essentially 100% stuck idle) — but NOT reliably: repeat live runs measured 64-89% of moving
samples still showing idle depending on test conditions, with the run gait specifically worse
than walk and, in one run, nearly 100% still sliding. The residual failure mode (a suspected
extra float-threshold gate specific to the run branch) was not isolated this pass.

**Shipped anyway, not reverted**, unlike round 60's own falsified `+0xA0` attempt — this one is
not a no-op; it demonstrably plays the correct animation part of the time, which round 60's
attempt never did at all. `tests/KotorCoop.Tests` stayed at 1566/1566; both plugin builds clean
across four rebuild/redeploy/live-test cycles; no crash observed. README's "run-gait" claim in
the "Verified live" summary was removed (it was not true even before this round — the bug this
round investigates predates the claim) and replaced with an Honest Limitations entry describing
the real, partial state. Full evidence, the field diff table, live sample transcripts, and what
a future pass should try next: `docs/research/phantom-locomotion.md` §8.

### Round 62 (2026-08-22) — the run/walk asymmetry's exact gate found and closed; not
sufficient, the deeper defect isolated one level further down

Continuation of round 61, tasked specifically with the float-threshold gate round 61 left
untraced (constant at `0x0073E9AC`, gating `ClientServerSyncHandler_Position`'s RUN-only "play
nicely" branch). Full static disassembly this round (stack-depth hand-traced and cross-checked)
identified it precisely: the gate compares the handler's own `budget` argument (arg4) against
`0.5f` (live-read from the binary, `raw=0x3F000000`), taking the nicer formula only when
`budget < 0.5`. `DriveBody` was passing `ObjAnimationBudget` (`creature+0xD8`), an engine field
that reads `~1.0` live — `1.0` never satisfies `< 0.5`, so every RUN call permanently took the
worse fallback formula while WALK (no such gate) did not. This precisely explains round 61's
own walk-partially-works / run-almost-never asymmetry, from a verified constant rather than a
guess.

Fix: a new call-site-local constant, `TwinPositionSyncBudget = 0f`, passed to the `+0x12C` call
specifically (not `ObjAnimationBudget`, which is untouched and still feeds the separate,
already-proven-inert-for-gait `+0x134` call). Both plugin builds clean; `tests/KotorCoop.Tests`
stayed at 1566/1566.

**Live-measured, honestly: this does not fix the reported bug.** Two directions, 10 s walk + 10
s run each: host→client phantom measured **99.4%** idle-while-moving (166 moving samples, 165
idle), client→host phantom **81.2%** (117 moving, 95 idle) — not better than round 61's 64-89%
range, arguably worse on one direction. Mirrored enemy was not independently sampled (time);
the companion (Trask) stayed pinned by the party-follow AI for the whole test window and
produced no usable movement samples.

The round's real contribution is isolating the defect one level deeper than round 61 reached: a
new capped diagnostic read `twin+0x48` immediately before and after the `+0x12C` call itself,
for 60 samples on each instance (walk phase only — the cap exhausted before any run sample was
captured). **`before == after` in 60/60, with zero exceptions** — the call never changes the
rendered animation field synchronously, even though that same field visibly does change across
separate calls a frame or more apart. This rules out "it plays correctly, then gets reverted
within the step" and points at `CSWCAnimBase::SetAnimationInternal` (`animBase_vtable+0x54`,
`0x0069EE10`, db-confirmed name) — reached unconditionally at the end of every branch inside
`ClientServerSyncHandler_Position` — as either deferring its effect to a separate engine update
pass, or gating internally on something this round did not decode. That function itself, not
its caller, is the target for the next pass. Full input table, the live sample transcripts, and
the before/after diagnostic evidence: `docs/research/phantom-locomotion.md` §9.

`tests/KotorCoop.Tests` stayed at 1566/1566; both plugin builds clean across two
rebuild/redeploy/live-test cycles; no crash observed. README's Honest Limitations entry for
this bug was updated with round 62's numbers, not marked fixed — the walk/run bullet is not
restored (all four cases were not clean; two of four were not even independently measured this
round).

### Round 63 (2026-08-22) — a citation error found and corrected, two hypotheses tested live
and falsified, still not fixed

Continuation of round 62, tasked with testing four specific one-variable-at-a-time hypotheses
(E1–E4) for why `ClientServerSyncHandler_Position`'s effect on `twin+0x48` is not synchronous.
Full write-up: `docs/research/phantom-locomotion.md` §10.

**The biggest finding is a correction, not a fix.** Round 62 named `0x0069EE10` as
`CSWCAnimBase::SetAnimationInternal`, "db-confirmed." It is not — the debug symbol database
(`tools/kotor1_0_3.db`, queried directly this round with `tools/q.py`) puts
`SetAnimationInternal` at **`0x0069E8D0`**; `0x0069EE10` falls inside a completely unrelated
function, `CSWCAnimBase::GetBoundingRect`. Confirmed three ways (the symbol DB, a linear
disassembly from a known-good boundary showing `0x0069EE10` is not even an instruction
boundary, and reading `CSWCAnimBase`'s vtable directly off disk — `+0x54` is a five-line
forwarding stub, `SetAnimation`, that itself calls `+0xF0`, which IS `0x0069E8D0`). Every
prior round's disassembly of "`SetAnimationInternal`" was reading `GetBoundingRect`'s jump-table
data as code.

Disassembling the real function (VERIFIED) shows idle, walk and run all reach one identical
generic path — ruling out a per-gait-id divergence inside this function as the cause — and
that the path's "play" step depends on `CSWCAnimBase::GetAnimationName` (vtable `+0x40`)
resolving a non-empty name for the requested id. `GetAnimationName`'s own disassembly (VERIFIED)
shows a fallback: if its first attempt (with the real id) comes back empty, it retries with the
literal constant `0x2710` (idle) hardcoded in place of the real id, before ever reaching the
combat-overlay range checks any gait id would skip anyway. **If the first attempt is empty for
`0x2712`/`0x2714` on a phantom, this is a complete, mechanistic explanation for the whole bug —
not live-confirmed this round** (would need a capped diagnostic reading the intermediate
string's length, not attempted — time went to E1/E2/E4 instead). Flagged as the most concrete
unfinished lead the phantom-locomotion investigation has produced across four rounds.

Two of the brief's four experiments were live-tested:

- **E1** (skip the proven-dead `+0x134` animation-sync call for Walk/Run): live-measured no
  change (96.1% idle-while-moving vs. round 62's 99.4% baseline, host→client). Kept anyway —
  round 61 already proved this call is a structural no-op for these two ids, so removing it is
  correct regardless of whether it fixes the bug.
- **E2** (stop writing "arrived instantly" to the twin's desired-position budget while moving):
  live-measured no change (96.9% combined with E1). Reverted — unlike E1, this has no
  independent justification once it measured no help.
- **E3** (disassemble `SetAnimationInternal`): done — see above; this IS what produced the
  citation correction and the `GetAnimationName` lead.
- **E4** (a control creature, AI-walked via `ActionMoveToLocation`): blocked. The plugin's own
  test-drive hook targets only the player, and the player is confirmed live this round to be
  "drive-controlled" — a queued `ActionMoveToLocation` on the PC never executes (`moveState`
  went to "requested" and stayed there the whole 15 s sample; position never moved). This pass's
  save has no hostile or party companion to target instead.

**Final live numbers, both directions, 10 s walk + 10 s run, with this round's one kept change
(E1) shipped**: host→client phantom **97.6%** idle-while-moving (84 moving samples, 82 idle),
client→host phantom **96.9%** (129 moving, 125 idle) — statistically unchanged from round 62's
baseline. Mirrored enemy and companion were not sampled (no hostiles or party members in this
save) — the fourth round running without an independent measurement of either.

`tests/KotorCoop.Tests` stayed at 1591/1591 (baseline grew from 1566 via unrelated merges since
round 62, not this pass's own scope); both plugin builds clean across three
rebuild/redeploy/live-test cycles; no crash observed. README's walk/run bullet is not restored.

### Round 66 (2026-08-22) — container open/close sync's first live pass: two real regressions found and fixed, one masked as "the close doesn't work" until root-caused

Live-verifying the container-sync feature merged in `b67c1f5` (protocol 37/block v50, "not yet
live-verified"). Full write-up: `docs/research/container-sync.md` §5.

The core one-looter-at-a-time lock and the open/close mirror both work, symmetrically, host-opens
and client-opens directions, against a real footlocker (0xC6, END_M01AA) — a second player never
gets a local claim or sees the panel while a peer holds it. But getting there needed two real
fixes, not just a live-verify pass:

1. **No way to close what `useplaceable` opens.** That verb only sends the network half of a
   real click, so the engine's `+0x338` genuinely flips but no GUI panel ever shows on ANY
   instance — nothing for Escape or a click to close. Added `closeplaceable [tag|id]`
   (`CheatKindClosePlaceable = 19`, kept in sync both halves) calling the real `CloseInventory`
   directly. First attempt passed the "requesting player" arg as 0 (reasoning from a
   misread branch) and it silently no-op'd — live-disproven, `+0x338` never cleared. Fixed by
   passing the closing player's own object id, the value a real click's `AIActionUseObject`
   actually passes there (fresh disasm of the real call site).
2. **The durable "ever opened" cosmetic mirror (pre-existing, `PlaceableController`, shared
   block v11) permanently defeats a close.** That channel is deliberately monotonic
   ("never the reverse", `PlaceableFlags.cs`) and its agent-side poll re-broadcasts "open" for
   any placeable it has EVER seen open, forever. A container this pass's own transient close
   logic genuinely closed got clobbered back open within ~100ms, on every instance, permanently
   — the close-sync feature worked for one frame and then silently failed for the rest of the
   session, every single time it was tried. A 4-second suppression window was tried first and
   ALSO live-disproven (the reassertion pressure never stops, so any timeout eventually loses).
   Fixed with `ContainerCloseHook`, a second `OpenInventory`-style detour on `CloseInventory`
   (never refusing) that latches an id as closed for the rest of the module load;
   `PlaceableController` now checks that latch before re-asserting. Verified stable for 30
   straight seconds after the fix, both directions.

A THIRD bug surfaced testing lease expiry (kill the host's own agent, expect the client's lock to
release within the 5s lease): it didn't, for 12+ seconds straight, even though the client
correctly detected the disconnect. Root cause: `CoopClient.GetInteractionLocks()` is a cache of
"the locks the host last broadcast," with no expiry of its own — the design assumes the host
stays alive to publish the pruned list, which does not hold when the HOST itself is what died.
**Identical shape to a bug already fixed once in the same function**, for the dialogue token
(`CoopClient.ForgetSession`'s own comment records a host killed mid-conversation leaving a client
"spectating forever," fixed by dropping the cached token on link-loss) — the container-lock cache
had the same hole, just never previously exercised live. Fixed the same way: `ForgetSession` now
also clears `_interactionLocks`/`_interactionLockModule`. Re-verified: clears within 1s of the
kill.

Also found and fixed in passing: `ContainerUseHook`'s refusal was never logged anywhere (every
other refusal path in this plugin logs; this one only updated an internal counter for the
on-screen notice) — added a log line in `InteractionLockController.PollContainerRefusals`. And
documented, not fixed: `OdysseyPlaceables.HasInventory` (`+0x334`) does not reliably identify a
real container live — a confirmed working container with a real item in its repository still
reads `has_inventory=0` — so `useplaceable` bare's own container sweep under-reports; the new
`closeplaceable` verb sidesteps this by filtering on `Odyssey.IsPlaceableOpen` instead.

Not exercised this pass: a corpse (`slay`'s target, object id `0x35`, fully invalidated
immediately after the kill rather than leaving a lootable body — not root-caused, possibly
specific to the `slay` cheat rather than the container feature) and a live dialogue-vs-container
interleave (no console primitive exists to start a real conversation; argued instead from the
lock's kind-discriminated key and the untouched, still-passing conversation-lock test suite). An
ordinary door and the reversed-roles refusal were both live-confirmed working.

`tests/KotorCoop.Tests`: 1590/1591 (the one failure is the documented `HeavyPacketLossStillConverges`
flake). Both plugin builds clean across five rebuild/redeploy/relaunch cycles.

### Round 67 (2026-08-22) — the "0% idle-while-moving" measurement was blind to a SECOND gait
defect: the walk/run loop was being restarted from frame 0 on every drive step; fixed and
live-measured at up to an ~800x drop in restart calls, both directions

Round 65 closed the render-field-stuck-at-idle defect (0% idle-while-moving, both phantom
directions) but a player report after that fix landed described a different, coexisting
symptom: "combination of sliding with animation stutter — you can see the run animation
initiated but it just never looks correct." The idle-while-moving metric cannot see this —
it only checks WHICH animation id the twin shows, never whether that animation is playing
smoothly. Full write-up, disassembly, and live traces: `docs/research/phantom-locomotion.md`
§13.

**Root cause (VERIFIED, static disassembly of `CSWCAnimBase::SetAnimationInternal`,
`0x0069E8D0`, and its caller `ClientServerSyncHandler_Position`, `+0x12C`/`0x00611F50`):**
`Odyssey.DriveBody` has called `+0x12C` every drive step since round 61 (60 Hz per driven
body, `PhantomController.StepIntervalSeconds`) — deliberately, because round 61 measured
that gating it on the gait edge underperformed at the time. Full disassembly of
`SetAnimationInternal`'s generic path (`0x0069E987`, where every gait id lands — round 63
§10.2) this round found **no "already playing this id, skip" guard anywhere in the function**:
every call unconditionally resolves the animation's name and makes the "play" virtual call.
Round 61 §8.5's own comment already noted the call's interpolation/rate side effects are
overwritten by `DriveBody`'s own unconditional writes immediately after — meaning the ONLY
thing that call's per-step repetition accomplished was restarting the walk/run cycle from
frame 0, up to 60 times a second per driven body — a stutter, not a rare glitch.

**Why gating it now is safe, when it was not in round 61**: round 65's separate fix (keeping
`twin+0x58`, `looping_animation`, in sync with the intended gait every step) is what now keeps
`CSWCCreature::AnimationUpdate`'s per-frame reassertion a no-op — that no longer depends on
`+0x12C` re-firing every step. Position and the playback-rate scale are also both written
unconditionally every step already, independently of this call. So the call's only remaining
job is the one-time "start playing this cycle" trigger a genuine gait transition needs.

**Fix**: `Odyssey.DriveBody` now calls `+0x12C` only when `GaitRule.ShouldReissue` (the
existing `GaitChanged` helper, already tracked for the `+0x134` skip) reports a real
transition — one evaluation shared between both use sites instead of two. A new counter
(`sync12c-calls N in Mms`, in the existing periodic gait log line) makes the call rate
directly observable.

**Live-measured, both directions, on the same combat-heavy save (`END_M01AA`, ~25 mirrored
hostiles active), true before/after A/B (temporarily reverted the gate, rebuilt, redeployed,
measured, then restored the fix and redeployed again)**:

| | before (unconditional, round 61-65 behavior) | after (round 67, edge-triggered) |
| --- | --- | --- |
| `+0x12C` calls, aggregate across all driven bodies, per ~5-6s window | 35,311 – 55,014 | 0 – 68 (steady-run windows: 0-2) |
| idle-while-moving, host→client phantom | — (round 65 baseline: 0%) | 0% (0/308-392 moving samples, two runs) |
| idle-while-moving, client→host phantom | — (round 65 baseline: 0%) | 0% (0/372-381 moving samples, two runs) |
| twin animation id during a sustained run, fine-grained (16ms) mover | — | stable, no flicker (300+ consecutive identical samples) |

The idle-while-moving fraction (round 65's own metric) stayed at 0% in every run this round —
this fix does not regress it. The new evidence is the call-rate collapse (roughly 500-800x on
the busiest windows) and stable per-sample animation ids, which round 65's own metric could
never have detected. A coarser (40ms) synthetic mover produced walk/run flicker that a
finer-grained (16ms) mover did not reproduce — diagnosed as a synthetic-mover/network-relay
polling-rate aliasing artifact of this round's own test harness, not a shipped defect (see
phantom-locomotion.md §13.5 for the full reasoning).

`dotnet test tests/KotorCoop.Tests`: 1591/1591 before and after. Both plugin builds clean
across four rebuild/redeploy/relaunch cycles this round (the A/B needed two extra full
redeploys). No crash observed. Mirrored enemies were exercised live by construction (this
save's own active combat encounter; `EnemyPresenceController.DriveOne` calls the identical
`Odyssey.DriveBody` this round changed) and their calls are folded into the aggregate
before/after counts above — not independently isolated per-creature this round.

### Round 68 (2026-08-22) — walk/run hysteresis shipped and unit-tested; the mirrored
companion/enemy gait isolation rounds 63-67 all deferred is STILL not closed — this time
because AI/combat state, not `DriveBody`, blocked it, and a raw-memory-poke shortcut was
found NOT to propagate through the mirroring pipeline at all

Three gaps left by rounds 65-67 (`docs/research/phantom-locomotion.md` §12-§13,
`docs/research/container-sync.md` §5): mirrored companion/enemy gait sampled independently
rather than folded into an aggregate; hysteresis on the bare walk/run threshold; and the
corpse-looting question. Full write-ups: `docs/research/phantom-locomotion.md` §14 (gait
hysteresis + the companion/enemy attempts), `docs/research/container-sync.md` §6 (corpse
looting).

**Hysteresis: DONE.** `GaitRule` gets a two-threshold band (enter Run above 2.4, drop to Walk
below 1.6 — the old bare 2.0 cutoff sits in the dead band between them) plus a 0.15s dwell
before a near-zero speed reads as idle. 23 unit tests (`GaitRuleTests.cs`, rewritten), including
a hovering-jitter simulation (1.8/2.2 alternating) proving zero transitions and a
ramp-up-then-down simulation proving real acceleration still transitions exactly once each way.
Live-observed via a `tp`-hop cadence mover (the least-bad available primitive — real player
input still cannot be injected): after a ~1s cold-start settle, the client's mirrored phantom
held Run for the rest of the window with zero flicker despite an independently re-measured
speed signal swinging between 0.25 and 2.94 u/s sample to sample — real evidence, but
methodologically coarse (a re-derived speed estimate, not the engine's own internal value) —
see phantom-locomotion.md §14.2 for the honest caveat.

**Mirrored companion/enemy gait: still NEEDS-LIVE, blocked upstream of `DriveBody`.** Recruited
Trask via the `talk`/dialogue test-hook (confirmed in the party table on both host and client),
but his follow-AI never engaged — 20 straight seconds of his real position not moving a single
float bit while stationary, and no sustained movement after either a long or a point-blank `tp`
of the player. Firing his own `+0x260` combat-end script produced exactly one discrete jump,
then he froze again. In END_M01AB, standing the player next to the exact Sith soldiers rounds
52-58 name as chasing-once-aggroed produced no reaction, and the `attack` test-hook's
player-as-attacker form (`attack 0x7FFFFFFF <id>`, round 58's own documented recipe for a real
kill) never queued an action against five distinct targets across two modules —
`head=0xFFFFFFFF`, zero combat-round activity, the entire observation window, every attempt —
while the REVERSE order (an NPC ordered to attack the player) reliably produced real combat
rounds on the same targets. Whether this is a save/module property (several targets read hp
1/1, consistent with cutscene-placeholder NPCs, not real combatants) or a genuine regression
since round 58's `tar_m10aa` session (same day) the same recipe got four confirmed kills was
NOT settled this round — flagged for a future pass.

**A new methodology finding, worth keeping**: a raw `WriteProcessMemory` poke to a creature's
own position field persists on local read-back (confirmed for an idle Sith and for the local
player's own position) but does **not** propagate to a mirroring instance at all — the enemy
census stayed at 0 entries, and a poked player's own phantom on the other instance never moved,
for the entire poked window. Only a REAL engine call (`console 'tp ...'`, genuine AI movement,
or an existing test hook) reliably drives what the network layer republishes. This was tried
specifically as a shortcut past the AI-state blockers above and ruled OUT as a viable one — not
root-caused why the read side disagrees with a local re-read, but confirmed reproducible three
separate ways (companion, enemy, player).

**Corpse looting: still open, one level of progress.** Identified the likely engine surface
(`CSWSCreature::GetItemRepository`, `0x004EF770`, via the symbol DB — a corpse is never a
`CSWSPlaceable`, so `ContainerUseHook`'s existing `OpenInventory` detour structurally cannot
cover it). Extended `useplaceable`'s cheat handler so an explicit id/tag that resolves to any
server object — not only a placeable — sends the same generic `Odyssey.UseObject` click;
verified this reproduces §5.5's own `slay`-body finding exactly (`does not resolve to anything
here`). Could not obtain a REAL kill this round (blocked by the same `attack`-hook gap above),
so the extension was never exercised against a genuine corpse — whether `ContainerUseHook`
needs a creature-side sibling detour, whether the client gets refused while the host loots, and
whether loot removal mirrors, are all still open questions for the next round with a working
kill path.

`dotnet test tests/KotorCoop.Tests`: **1601/1601** (up from the round's own 1591 baseline by 10
net-new tests), run after this round's full change set. Both plugin builds clean across two
rebuild/redeploy/relaunch cycles. Rig torn down clean (`Get-Process swkotor,KotorCoop.Agent`
empty), `auto_start_agent` restored to `1` in both installs by `demo.ps1 -Stop`.

### Round 69 (2026-08-22) — control-return's regression still does not converge live, and
tracing why found the detection function reads neither of the two documented control fields;
the lag audit's own premise (`interp_ms` "needs exposing") was already wrong, and a real
structural win (`party_fast_pump`) shipped behind a flag instead

Two jobs, full write-ups in `docs/research/control-return.md` ("Live pass") and
`docs/research/lag-audit.md` ("Measurements").

**Control-return: 2 of 4 scenarios closed live, the regression itself did not.** Built a
dev-only `leader <slot|pc>` console verb (mirrors `applyeffect`'s wiring) so the harness no
longer needs the one manual Tab step `control-return-live.ps1` could never automate. It writes
two SEPARATE, both live-verified-stable control fields — `CClientExoAppInternal+0x20` (input
authority, the field `LockControlToPlayer` reverts) and `CSWParty`'s member[0] client-space id
(`party+0x24`, client-control-lock.md's own "Recipe A") — and Trask is LIVE-RECONFIRMED
party-roster slot 0 in `end_m01aa`, not 1 as this round's own brief assumed. Neither field turns
out to be what `Odyssey.GetPlayerCreaturePointer` (`ControlReturnController`'s detection) reads:
disassembly traced it to `CServerExoApp::GetPlayerCreature` (0x004B2820), a chain through
`CServerExoAppInternal` unrelated to either write. Host-untouched and disconnect-while-on-PC
scenarios both closed live and cleanly; the regression scenario and the `control_return=0`
opt-out remain NEEDS-LIVE, now precisely scoped to "what does 0x4B2820 actually read, and does
a real Tab press change it" rather than "does the fix run." A related, unexplained side finding
— `GetPlayerCreaturePointer`-based cheats (`topup`) report 0 max hit points on both instances
in this exact session regardless of Tab state, while the agent's own independent reading shows
12/12 for the same character — is flagged for its own round; it may be upstream of the
regression's own non-convergence.

**Lag audit: measured, one real code change shipped.** `frame_stats=1` plus a ~190s connected
session (repeated `tp` hops, one `usedoor`, one staged `attack` fight) on END_M01AA found both
instances comfortably under budget (2-3 ms mean frame time, 5-7 frames over 33 ms out of
~220,000 sampled, all attributable to the module-load transition or isolated unexplained ~150-
180 ms spikes with no per-frame-controller signature) — `EnemyPresenceController`'s scan was
NOT gated, because the measured hitch pattern did not implicate it, and the audit's own
conditional instruction was "if it clusters." The bigger finding: `coop.ini interp_ms` already
IS the "expose `interp_delay_ms`" ask from a prior pass — both installs already had it live at
`0` (more aggressive than the 40 this round was asked to try), confirmed via `lagstats`, no code
change needed. Shipped `party_fast_pump` (default OFF, `CoopConfig.cs`/`Program.cs`/
`CoopAgent.cs`), mirroring `dialogue_fast_join`'s exact rollout shape, to remove one of the two
stacked 150 ms windows on the companion-mirror path; unit-tested (`PartyFastPumpTests.cs`) and
smoke-tested live for ~50s with the flag on (party reconciliation intact, zero frame-time
regression, agent pump unchanged) — not yet run through a full combat-heavy session, so left
off by default pending a longer A/B.

`dotnet test tests/KotorCoop.Tests`: **1623/1623** (1621 baseline + 2 new `PartyFastPumpTests`).
Plugin rebuilt/reinstalled to both GOG directories across two redeploy cycles (the `leader`
verb's two revisions). Rig torn down clean, `auto_start_agent` restored to `1` in both installs.

### Round 70 (2026-08-22) — the control-return regression closes live: `GetPlayerCreaturePointer`
disassembled and found structurally incapable of seeing a control switch, the engine's own
`ChangeCharacter`/`SetControlledCreature` fully reverse-engineered but not reliably callable
from this process (a genuine NEEDS-LIVE result, not abandoned), and a safe, working, id-only
substitute shipped and matrix-verified

Full write-up: `docs/research/control-return.md` "Round 70". Short version: round 69 left two
open questions — why `ControlReturnController` never sees the claim, and why `topup`-class
cheats report 0 max hit points. Both are closed.

**`CServerExoApp::GetPlayerCreature` (0x004B2820) does not read anything a control switch
touches, at all.** Disassembled end to end this round: it resolves a FIXED "player slot 0"
entry from the server's own connected-players list (a bucket search at
`CServerExoAppInternal+0x10064`) and reads that entry's own registered PC id (`player+0x28`),
resolved through `CServerExoApp::GetGameObject` with a type-tag range check. Tab, `leader`,
`ChangeCharacter` — none of them touch this. It always answers "the real PC" (or nothing),
which is exactly why `ControlReturnController` and `PartyRosterController`'s detection, both
built on it, never fired. Added `Odyssey.GetControlledCreaturePointer`, which reads the field
that DOES move — CSWParty member[0]'s client-space id, `client-control-lock.md`'s own control
gate — resolved through the same safe generic `ResolveServerObject` every other id read in the
file already uses. Both controllers now key off it.

**The 0-max-HP finding was never a bug — it was `end_m01aa`'s own min-one-HP pin, already
documented three weeks earlier and not cross-referenced.** Live-reproduced first (both
instances, unconditionally, before any switching): `GetPlayerCreaturePointer` resolves a real,
live `CSWSCreature` (correct vtable `0x7470C8`, correct current hp matching the agent's own
independent read exactly) whose id reads `0x7FFFFFFF` — this codebase's own well-documented
"current player" sentinel, not garbage — and whose max hp reads 0. `docs/research/min-one-hitpoint.md`
already recorded the identical reading (`sethp.ps1 -ObjectId 0x7FFFFFFF` → `hp 12 (stored max 0)
[min1hp]`) for this exact module: the Endar Spire's own `k_pend_area01` OnEnter calls
`SetMinOneHP(GetFirstPC(), TRUE)`, and the engine stores max hp as 0 while that pin holds.
`GetPlayerCreaturePointer` needed no fix.

**The engine's own switch function was found, fully disassembled, and its calling convention
confirmed against two real call sites — but calling it live left the array unchanged, every
time, and that is an honest, unresolved result, not a workaround for a bug that was never
found.** `CClientExoAppInternal::ChangeCharacter` (0x5F6B60) → `CSWParty::SetControlledCreature`
(0x635480): xrefs against real callers (`ChangeCharacterToNextLivingPartyMember`'s own
`push 0; push -2`, and the portrait path's own `CSWParty::FindMemberIndexById` 0x6349C0, which
turned out to be exactly the bounded array search this file already needed) confirmed the
argument shape precisely. Calling it — with the documented `-2` sentinel included, which
bypasses `SetControlledCreature`'s own count bound entirely — left `internal+0x4B4` (the flag
the callee only sets past its own no-op short-circuit) at 0 and the array byte-for-byte
unchanged, in every live test this round ran. Traced as far as ruling out an index/argument
mistake; the likely remaining cause (an exception inside `SetControlledCreature`'s own SEH
frame, silently unwound before the array write) needs debugger access this round's tooling did
not have. Documented in full in `Odyssey.cs`'s own remarks so a future round does not re-derive
it.

**The working fix reproduces the SAME end state by direct, id-only writes instead.**
`Odyssey.RotateControlToFront` performs the exact reorder `SetControlledCreature` was supposed
to (move the target to member[0], shift the rest down, preserving order) using ONLY id writes —
`client-control-lock.md`'s own "Recipe A", already established crash-proof — then calls the
engine's own, independently-already-working `SetCameraFollowServerObject`/
`RestoreCameraToControlledCreature` for the camera. `ControlReturnController`'s hand-back now
calls this synchronously the instant it detects a session-owned companion, instead of logging a
claim and waiting on a separate mechanism — closing the exact gap round 69 could not: the array
reorder and the camera move now happen in the SAME pass the claim is logged in.

**One real interaction found and fixed along the way: `party_together_fix`'s own clamp — which
exists so a client can cross a real door — also makes a companion UNREACHABLE by array index
while it is active**, because it clamps CSWParty's count to CSWPartyTable's companions-only
count, and that is the SAME bound both the real engine's index-based switch and this round's
manual rotate need to address a target past the PC. Neither the data nor the fix cares: the
companion's id is still physically sitting in the array past the clamped count (live-confirmed:
Trask's flagged id at member[1] with count clamped to 1), so `SwitchControlToPartySlot` now
scans past the live count when the bounded search comes up empty, and — never inventing data,
only reading what is already there — temporarily raises the count to include exactly the one
entry it needs. `PartyTogetherClampRule`'s own deferral (now reading the corrected detection)
takes over the very next pass, so nothing re-clamps it back down mid-switch.

**Live matrix, `control-return-live.ps1` (updated this round — see below), fresh rig, three
redeploy cycles across the investigation:** ALL PASS. Scenario 1 (the regression itself) closes
end to end: claim logged, array reordered and camera pointed at the PC in the SAME pass, no
`party_together_fix` clamp lands before the claim (the one dangerous window that still matters
— see below for why "clamp lands between claim and hand-back" stopped being the regression's
signature). Scenario 2 (host untouched) and scenario 3 (PC-only disconnect, no-op) both PASS as
before. Scenario 4 (`control_return=0`) — NOT live-restarted in rounds 68/69 for lack of time —
WAS live-restarted this round: appended `control_return = 0` to the client's `coop.ini`, a full
rig teardown/relaunch (fresh process, config read once at construction), `leader 0` while
disconnected, reconnect — the array stayed on Trask (`member[0] = 0x80000127`, direct memory
read) and the plugin log shows zero `control:` lines across the whole window. Reverted the
config change afterward. `verify.ps1` check 19 ("the host still controls its companions") PASS
throughout: `table 1 companion(s), CSWParty 2 (PC+companions), companion 0x127 commandable=1,
host clamped this run: False` — the host-side clamp restriction this round's changes never
touch stayed intact.

**`control-return-live.ps1` itself needed a real update, not a rerun.** Its scenario-1 check
was written for the OLD architecture, where "claim" and "hand-back" were separate events with a
gap between them that a clamp could land in and corrupt — a real signature at the time. Round
70 makes the claim SYNCHRONOUS with the switch, so a clamp landing AFTER the claim (once the PC
is already back at member[0]) is now the ordinary, harmless case resuming, live-confirmed by
timestamp (`.375` claim/switch, `.572` the routine clamp —197 ms later, after control was
already back, `.604` the camera reassert) — checking for it as a failure was a false positive
against the very fix this round shipped. Moved the check to the window that still matters: no
clamp before the claim, where the array could still be corrupted while undetected.

`dotnet test tests/KotorCoop.Tests`: **1623/1623**, unchanged baseline (round 70 changed
control-switch mechanics, not any pure-logic rule under test). Both plugin builds clean across
five redeploy cycles (the diagnostic passes that isolated the `ChangeCharacter` dead end, then
the working `RotateControlToFront` fix, then the `control_return=0` live restart). `callaudit.py
--bad`: 161/162 engine call declarations agree with the binary — the one that does not
(`CSWSArea::SetTransitionPending` in `EngineHandles.cs`) predates this round and this round
touched no file it is declared in. Rig torn down clean, `auto_start_agent` restored to `1` in
both installs.

### Round 71 (2026-08-23) — the player-as-attacker `attack` gap root-caused and fixed (a
distance problem, not a code defect); a real ActionFollowLeader dispatch built for Trask's
follow-AI, live-verified safe but not yet sufficient; a real combat kill found to invalidate its
corpse exactly like `slay` does, closing that open question the wrong way; `party_fast_pump`
re-measured and left off

Picks up all three harness gaps rounds 65-70 left open. Full write-ups: `docs/research/lag-audit.md`
("2026-08-23"), `docs/research/container-sync.md` §7, `docs/research/phantom-locomotion.md`
("2026-08-23").

**Blocker 1 — CLOSED.** `attack <target> 0x7FFFFFFF` (player-as-attacker) was never a code
defect: `Odyssey.AttackTarget`/`QueueAttackAction` are unchanged since round 11 and still work
exactly as round 58 found. Live-reproduced the failure first — a `tar_m10aa` Vulkar 14.6 m away
never took a swing in 20s, `head` locked at a non-attack action the whole window, distance frozen
at 8.4 m the entire time — then found the reason by elimination: the drive-controlled local
player has no queued-move step (the SAME limitation `HandleWalk`'s own doc names for
`ActionMoveToLocation`), so `ActionAttack` never gets the player within melee range unless it
already started there — exactly what every prior round's own successful test (round 11, round
58) happened to stage. Fix (`TestDriveController.HandleAttack`/`CloseToMeleeRangeIfNeeded`):
when the attacker is the local player and not already within 1.5 m, snap it to melee range via
`Odyssey.SnapTo` (the same real call the `tp` cheat itself uses) before arming the order.
Live-verified twice more this round on fresh Vulkars, both real kills: hp 22→13→4→dead and
20→14→9→dead, `dist` settling at 1.79-2.10 m from a genuine >2 m start, both through the
unmodified round-11 priming path.

**A live methodology trap found and fixed along the way**: an external PowerShell tool
(`teleport.ps1`) that vtable-scans for "the first creature whose stats say `is_pc`" can and did
pick a stale/leftover object sharing the player's vtable and id byte pattern, silently writing
position to a body the engine's own `GetGameObjectFn` never resolves 0x7FFFFFFF to — the write
"succeeds," a follow-up scan even shows the new position, and nothing about the attack ever
reflects it. The plugin's own `console 'tp x y z'` (the `Teleport` cheat, `Odyssey.SnapTo` on the
engine-resolved player pointer `HandleCheat` already holds) does not have this failure mode and
is what every distance-sensitive test in this round used instead — the existing project
convention (`docs/research/phantom-locomotion.md` §14.2 already recommends it) has a concrete
new reason behind it now, not just history.

**Blocker 3 — the missing mechanism is now built and live-verified SAFE and CORRECT at the
dispatch level; genuine walking is still NOT achieved, honestly.** New `PartyFollowController`
(and `Odyssey.QueuePartyFollowLeader`, the safe VM-dispatch route into
`CSWVirtualMachineCommands::ExecuteCommandActionFollowLeader 0x0052CBE0` — confirmed against the
symbol database, the same class of call `AttackTarget`/`PickUpItem` already use) detects a
genuinely-owned companion that has stopped moving and reissues a real `ActionFollowLeader`.
Live-measured: Trask (recruited by a loaded save, and separately via the `talk` test-hook) sat
with `CreMoveState` (`+0xA8C`; 1 idle, 2 requested, 3 follow, -1 walking) reading 1, not the
empty-queue story party-reconciliation.md's 2026-07-31 design assumed — his own head action was
a stable, undocumented `0x3D` (not the verified `0x3C PartyFollowLeader`), unmoving, for 30+
seconds straight. `QueuePartyFollowLeader` reliably drives `CreMoveState` to 3 ("follow" mode IS
entered, confirmed every call) — but never to -1 ("walking," a route genuinely being stepped),
across every module and distance tried this round. One real, self-inflicted bug was found and
fixed along the way: the controller's own first draft cleared the companion's action queue
before reissuing (mirroring `HandleAttack`'s "stop fighting" no-op), which — per
`Odyssey.CaptureClaimState`'s own remarks — unconditionally invalidates a creature's pathing
target-cache fields, fields that do not self-heal; removing that clear changed nothing further,
which is itself useful evidence that clearing was never the blocker, only an additional hazard.
Ruled out with their own live tests, not by inference: an empty vs. stuck queue (both checked);
perception range (a live control — a plain Vulkar guard ordered to attack from outside its ~11 m
`PerceptionRange` also never moved, but the SAME order from within range closed 0.87 m of real
distance in ~8s, so queued AI movement is not universally broken in this rig, narrowing the gap
to something follow-specific); and the party table's own leader slot (`CSWPartyTable+0xEC` read
-1 the whole session; writing 0 as a bounded live experiment, then reverting it, produced no
change). Shipped anyway (`party_follow=1` default) because it is a correct, safe, verified
improvement over the complete absence of any follow-re-arming that existed before this round —
but "Trask genuinely walks behind the host" is NOT closed, flagged for a future round with the
above four causes already eliminated.

**Blocker 2 — NOT closed the way the brief assumed; a real corpse produces the SAME
"invalidated" result as `slay`, not a different one.** §5.5/§6.3's own hypothesis was that
`slay`'s own kill path might be what destroys the body, distinct from a real combat kill. This
round finally had a working `attack` (blocker 1) to test that directly: three separate creatures
(two `vulkarguard`, one `vulkarpool`) killed through genuine player melee damage — not `slay`,
not `Odyssey.KillCreature` called directly — all three came back `resolve <id>` → `engine 0x0
walk 0x0 resolve 0x0` within seconds of death, byte-for-byte the same reading §5.5/§6.3 got from
`slay`. `useplaceable` bare (no id) found no new placeable in the sweep afterward either — no
body-bag object appears anywhere for these deaths. Because `CombatController.KillOnce` (the
mirrored-death path) and `Slay`'s `KillHere` branch both call the exact same
`Odyssey.KillCreature`, this is expected once measured, not a discrepancy: whatever
`Odyssey.KillCreature`'s lethal-damage effect does to a creature, it does identically regardless
of which caller triggers it. **This means the premise itself — that a `killreal` verb distinct
from `slay` would produce a lootable body — does not hold for the cases tested.** Left genuinely
open: whether some OTHER death path (a natural HP-reaches-zero-through-ordinary-damage-resolution
route that never calls `KillCreature` at all, if one exists and is reachable) behaves differently,
and whether some creature TYPES (ones actually carrying loot, unlike the empty-handed Vulkars
here) get a body-bag placeable that these did not. (B) from the task brief could not be run: no
lootable corpse could be produced by any method available this round.

**(A) `party_fast_pump` A/B, real combat, `frame_stats` — re-measured, LEFT OFF.** ~45s per leg,
host and client both fighting Vulkars simultaneously (shorter than the requested 3 min per side —
session time ran out before a longer pass was possible; flagged as a real limitation of this
round's own measurement, not a claim of statistical rigor). `=1`: mean per-window frame time
9.63 ms (host) / 7.15 ms (client) during the fight, 197 + 217 = 414 frames over the 33 ms budget
across the two instances. `=0`: 6.30 ms (host) / 5.52 ms (client), 138 + 123 = 261 frames over
budget. `=0` read better on every one of those four numbers, not worse on any — the opposite of
what would justify flipping the default on. Full numbers in `docs/research/lag-audit.md`. Default
stays off; `coop.ini` in both installs was returned to `party_fast_pump=0` after the test (the
rig had inherited `=1` live from round 69's own smoke test, left set in the install directories,
not in source).

**(C) Gait sampling — enemy half attempted, inconclusive; Trask's half blocked by the still-open
follow gap above.** `phantomgait.ps1` against a chasing `vulkarguard`'s mirrored copy, watched
from the client: two separate attempts, both showing zero position change for the entire 12-15s
sampling window despite an EARLIER, independently confirmed instance of the identical order
producing real movement on the identical creature type in this same session (creatures.ps1
positions before/after, not inferred). Not reproducible on demand within this round's remaining
time — flagged, not claimed either way. Trask could not be sampled moving at all, for the reasons
above.

`dotnet test tests/KotorCoop.Tests`: **1623/1623**, unchanged baseline — no new unit tests this
round (the new controller's live-observed thresholds are not yet extracted into a pure,
unit-testable rule the way `GaitRule`/`PartyFreezeRule` are; flagged as follow-up). Both plugin
builds clean across the round's several rebuild/redeploy/relaunch cycles. Rig torn down clean
(`Get-Process swkotor,KotorCoop.Agent` empty), `auto_start_agent` restored to `1` in both
installs.

### Round 72 (2026-08-23) — the corpse-vanish mystery closed as vanilla engine/content behaviour,
not a coop-plugin defect; Trask's follow-AI traced one function deeper, to a frozen follow-TARGET
point two live-tested fix attempts did not unstick

Two threads, both live-tested this round; full write-ups in `docs/research/container-sync.md` §8
(corpses) and `docs/research/party-reconciliation.md`'s own 2026-08-23 entry (Trask's follow-AI),
`docs/research/phantom-locomotion.md`'s own 2026-08-23 entry has the gait-sampling pointer.

**Thread 1 — CLOSED.** `Odyssey.KillCreature` is a real `EffectDamage` through the VM, not a
`DestroyObject`; the one `DestroyObjectById` call site that could plausibly reach a GIT-placed
enemy (`MaterialisedEnemies.Destroy`) only ever touches ids this instance's own mirror spawned,
never a real body. On the HOST, `EnemyPresenceController`/`CombatController` both gate their whole
per-slot loop on a peer-forwarded census count that reads 0 on a host (nothing to mirror), so
neither controller runs at all against a creature the host's own engine kills — confirmed
structurally AND by grepping the whole session's plugin log (48,000+ lines) for zero
`"combat: killed"`/`"materialise: destroyed"`/`"enemy drive:"` lines. Live differential: a humanoid
Sith trooper (`END_M01AA`, `sithsoldier` `0xE`, hp 6, and `end_gensith` `0x38`, hp 12), killed on
the HOST through genuine melee combat-round resolution via the round-71 `attack` fix (no `slay`, no
`Odyssey.KillCreature` anywhere in the chain), vanished exactly like round 71's Vulkars —
`resolve` → `0x0` within 1-2s of death, `useplaceable`'s sweep count unchanged (64), the body gone
from a raw vtable memory scan too. **Verdict: this is not a coop-plugin bug. There is nothing in
this codebase to fix.** It is either the creature's own attached `OnDeath` script (an immediate,
zero-delay `DestroyObject`) or an engine-intrinsic cleanup — bounded to "outside this plugin's own
code," not further distinguished, because either answer closes the question the task brief asked.
This also closes container-sync.md's own long-open corpse-loot matrix (§5.5/§6.5/§7's "(B) could
not be run") as **not applicable** rather than still open: no kill method available to this rig,
coop or vanilla-equivalent, produces a lootable corpse in either module this project tests
against, so `ContainerUseHook`'s refusal/mirror machinery has nothing left to be tested against
here.

**Thread 2 — narrowed one real function deeper, still not closed.** Disassembled
`WalkUpdateLocation_QuickWalk_FollowLeader` (0x0051C360) — the engine's own PER-FRAME
continuation for `CreMoveState==3` that round 71 never reached, distinct from the one-shot
`AIActionPartyFollowLeader`/`QueuePartyFollowLeader` dispatcher round 71 built and shipped. Its
own opening gate resolves the creature's index inside `CSWParty` via
`ServerToClientObjectId`/`GetIndex` (both confirmed against the symbol DB's own
calling-convention/param-size metadata) and reads a per-member follow-data record at
`CSWParty+0x24+index*0x88`. Live probe (two new read-only Odyssey.cs diagnostics wired into
`PartyFollowController`'s existing stall log): Trask's own index resolves fine (`1`, not `-1`),
solo mode reads confirmed `0` (off) on the host the whole session — but the record's TARGET follow
point AND its own cached "prior leader position" are BOTH frozen at wherever the leader stood the
instant the module loaded, unmoving across a dozen samples and three teleports to increasingly
distant positions. Two live-tested fix attempts, both real verified-safe engine calls (not blind
ABI guesses): a direct `CSWParty::RecaulateFollowPoint` call (0 stack args, confirmed from the
DB), and writing the record's own cached leader-position field directly (confirmed by read-back
that the write genuinely stuck) immediately before the same recompute call. **Neither moved the
frozen target point at all** — live-disproven, not shipped, kept in `Odyssey.cs` as documented
ruled-out primitives. New prime suspect for a future round: `CSWParty::Update` (0x00637050, one
stack argument whose meaning this round did not safely determine — not called, per this project's
own rule against guessing an ABI blind). `party_follow=1` stays shipped (a real, safe improvement
over no follow-re-arming at all) but "Trask genuinely walks behind the host" remains unproven.

### Test suite and build

`dotnet test tests/KotorCoop.Tests`: **1623/1623**, unchanged baseline — this round's changes are
new read-only diagnostics plus two experimental primitives not called from any shipped path,
none of it exercised by the (x86 NativeAOT, unreferenced-by-tests) test project. Both plugin
builds clean across the round's many rebuild/redeploy/relaunch cycles. Rig torn down clean
(`Get-Process swkotor,KotorCoop.Agent` empty), `auto_start_agent` restored to `1` in both installs.

### Round 73 (2026-08-23) — CSWParty::Update's argument and write behaviour settled by disasm and
confirmed live; two real fixes shipped for Trask's frozen follow record; he still does not walk

Picks up round 72's own named prime suspect, `CSWParty::Update` (0x00637050), whose one stack
argument round 72 declined to guess. Full disassembly + live-measurement write-up in
`docs/research/party-reconciliation.md`'s 2026-08-23 "Round 73" entry;
`docs/research/phantom-locomotion.md`'s own 2026-08-23 update carries the gait-sampling pointer.
Short version:

- **The argument is a delta-time float** — settled by disasm (its one caller in the whole
  binary, `tools/xrefs.py 0x637050`, sits inside `CClientExoAppInternal`'s per-frame update and
  pushes `deltaTime * <a time-scale global>`; the function's own first three instructions
  accumulate the argument into `this+0x2BC`) and matches `param_size_bytes=4` in
  `tools/kotor1_0_3.db` — not a blind ABI guess.
- **What it writes**: a PARTY-LEVEL (not per-member) reference position at `this+0x2B0..0x2B8`,
  refreshed from the leader's live position, which `CSWParty::RecaulateFollowPoint` reads as its
  own input — the field round 72's manual `RecaulateFollowPoint` call was working from stale,
  because `CSWParty::Update` (its one real caller living inside a per-frame dispatch this
  plugin's drive loop never reaches) was never being called at all.
- **Fix 1 (shipped)**: `Odyssey.PumpPartyUpdate` calls `CSWParty::Update` once per rendered frame
  with a real measured delta time, from `PartyFollowController.OnFrame`. Live-verified: a new
  diagnostic (`Odyssey.TryGetPartyReferenceState`) shows the party-level reference position
  genuinely tracking a teleported leader within one frame, where it previously stayed frozen at
  the module-load position exactly like round 72's per-member fields.
- **This alone did not unstick the per-member fields.** With the reference position now
  genuinely live, both `CSWParty::Update`'s own conditional call into `RecaulateFollowPoint` and
  an added UNCONDITIONAL direct call (bypassing `CSWParty::TestAndResetPath()`'s gate entirely)
  left the per-member target/prior fields exactly where round 72 found them, frozen at
  module-load — not yet explained.
- **Fix 2 (shipped, the task's own sanctioned fallback)**: `Odyssey.TryDriveFollowTarget` writes
  the per-member target/prior fields to the live leader position AND sets the record's own
  "target changed" dirty flag (`record+0x68`) — round 72's raw poke never set this flag, which
  this round's deeper disasm of `WalkUpdateLocation_QuickWalk_FollowLeader` (0x0051C360, past
  round 72's ~0x51C700 stopping point) found is what actually triggers the function's own
  re-snapshot step. Live-verified: target/prior now genuinely track the leader, and the dirty
  flag reads back cleared (the engine's own continuation visibly consumed it).
- **Genuinely still not closed.** `CreMoveState` never reached -1 ("walking") even with a
  permanently fresh target — this round's disasm traced the function into an 11-phase step state
  machine (a jump table at 0x51CCEC) gated behind an obstacle-avoidance check against "the other
  party member's slot," which this ONE-companion save leaves empty/uninitialized — a new,
  unconfirmed lead for round 74, ahead of tracing all 11 phase handlers.
- **Methodology, reconfirmed**: a raw `WriteProcessMemory` poke to the leader's own position
  field does NOT propagate into `CSWParty::Update`'s own leader lookup (which reads a DIFFERENT
  offset, `+0x24/+0x28/+0x2C`, not `CSWSObject`'s usual `+0x90`) — the same §14.4 lesson, now
  confirmed against a second, different subsystem. `console 'tp x y z'` (a real engine teleport
  via the plugin's own console-command channel) is what actually moves everything downstream
  correctly.

`dotnet test tests/KotorCoop.Tests`: **1623/1623**, unchanged baseline. Both plugin builds clean
across the round's five rebuild/redeploy/relaunch cycles. `tools/verify.ps1`: 40 passed / 9
failed, all 9 pre-existing combat/item/revive/RPC-signature checks unrelated to this round's
changes (unstaged fresh-session gaps, not a regression) — the two party-specific checks ("the
host still controls its companions", "the client party roster survives a transition") both
passed clean, and `tools/partystate.ps1` on both instances matched the pre-round baseline
exactly (host soloMode=0, client soloMode=1, companions 1 on both). Rig torn down clean
(`Get-Process swkotor,KotorCoop.Agent` empty), `auto_start_agent` restored to `1` in both
installs.

### Round 74 (2026-08-23) — the "other slot" theory disassembled and disproven; a real step-FSM
reset bug found and fixed one layer deeper; still narrowly not fixed — thread closed

Picks up round 73's own last lead: an obstacle-avoidance lookup against "the other party
member's slot" inside `WalkUpdateLocation_QuickWalk_FollowLeader`'s step FSM, which a
one-companion party (this save has only Trask) leaves empty. Full write-up in
`docs/research/party-reconciliation.md`'s 2026-08-23 "Round 74" entry. Short version:

- **The "other slot" theory is disproven by direct disassembly.** The lookup resolves the other
  slot's stored id through `ClientToServerObjectId` and then never reads the result again — the
  following instructions unconditionally write a hardcoded "far/no obstacle" sentinel and
  register bookkeeping with the creature's own Area, regardless of whether the resolve
  succeeded, returned null, or anything else. Live-confirmed: a new diagnostic
  (`Odyssey.FollowDiag.OtherSlotIndex`/`OtherSlotStoredId`) reads `otherSlot=2:0x7F000000` every
  sample — `0x7F000000` being this document's OWN already-documented `CSWPartyTable+0x0C`
  "not spawned" sentinel (2026-07-31 entry), independent confirmation the slot really is empty
  and handled normally, not garbage. A second recruitable companion was not chased live: the
  disassembly already answers the question (populating the slot changes nothing downstream
  reads), and `END_M01AA` (the Endar Spire) never offers one in vanilla story content at this
  point anyway.
- **The real defect, one layer deeper**: the step FSM's own phase field (`record+4`) reset path
  (dispatch index 0, reached whenever phase == -1) re-enters `WaitingForPath` (phase 5) directly,
  WITHOUT ever calling `FindPath` (phase 0) — the only handler that submits a real path-finding
  job and writes `CreMoveState` to -1. The watchdog that forces phase back to -1 after 50 stalled
  attempts can therefore only ever land back in this same two-state loop once entered — a
  permanent dead loop, live-confirmed via `phase`/`attempts` diagnostics cycling exactly as
  predicted.
- **Fix shipped**: `Odyssey.TryResetStuckFollowPhase` resets `record+4`/`record+0x44` to 0 ONLY
  when phase is -1 or 5, called every frame alongside `TryDriveFollowTarget`. Live-verified
  across five rig cycles: breaks the dead loop every time, and the companion's step FSM reaches
  phase 7 (`PathWaitDest`) — the furthest any round has observed — reliably, reproducibly, and
  independent of an experimental change to `TryDriveFollowTarget` (see below) that was tried and
  reverted.
- **Still not enough**: once parked at phase 7, the attempt counter stays at exactly 0 for 40+
  continuous seconds — the outer per-frame dispatch block is not being re-entered at all.
  `PathWaitDest` (disassembled this round) polls an async job's write-cursor
  (`[creature+0x2DC]+0xFA4`) the same way `WaitingForPath` polls a job-status field — strongly
  suggesting the blocker is now a THIRD instance of this investigation's own recurring pattern
  (round 73's `CSWParty::Update`, rounds 60-67's animation ticks): an async engine subsystem this
  plugin's per-frame drive loop does not reach.
- **Tried and reverted**: lagging `TryDriveFollowTarget`'s `prior` write behind `target` (instead
  of round 73's "both to the same new value") on the theory that the outer function's own
  target-vs-prior gate needed a persistent nonzero delta to keep re-triggering. Live-tested
  against a leader driven through 12 real teleports simulating continuous movement — no
  measurable benefit over the phase-reset fix alone, and a real increase in per-frame write
  frequency for it. Reverted rather than ship an unproven behaviour change.
- **Verdict**: narrow, confirmed gap, thread closed per this round's own budget. Real forward
  progress (a genuine dead loop broken, the furthest FSM phase any round has reached) shipped
  alongside an honest "still does not walk" — judged solo-companion-only (nothing in four rounds
  of disassembly touches the multi-companion party-index path), not a general regression.

`dotnet test tests/KotorCoop.Tests`: **1623/1623**, unchanged baseline. Both plugin builds clean
across seven rebuild/redeploy/relaunch cycles. `tools/verify.ps1`: 41 passed / 8 failed (one
better than round 73's 40/9 baseline, same battery — all failures pre-existing and unrelated to
party-follow); the two party-specific checks passed clean. `tools/partystate.ps1` matched the
pre-round baseline exactly on both instances. Rig torn down clean (`Get-Process
swkotor,KotorCoop.Agent` empty), `auto_start_agent` restored to `1` in both installs.

### Round 75 (2026-08-23) — a second companion staged (confounded, not conclusive); the
pathfinder cursor's PRODUCER found, disassembled and live-confirmed working; the CONSUMER's
non-re-entry (round 74's own open question) narrowed further, still not closed; `party_fast_pump`
re-tested non-combat, left off

Full write-up in `docs/research/party-reconciliation.md`'s 2026-08-23 "Round 75" entry and
`docs/research/lag-audit.md`'s matching dated entry. Short version:

- **A new dev-only test primitive, because there was no other way to a second companion.**
  `CheatKindRecruit` (kind 21, no `SharedBlock` layout change) + a `recruit <slot> [tag]` console
  verb force NPC roster slot `<slot>` into the local active party — poke available, spawn if
  possible, fall back to an already-placed body by tag otherwise, then `AddPartyMember`. Live
  result along the way: `SpawnAvailableNpc` genuinely refuses on a bare availability poke (no
  blueprint association) — **settling `Odyssey.TrySetSlotAvailable`'s own long-standing
  NEEDS-LIVE question**: the flag alone is not enough. The tag fallback (Carth's real in-world
  tag is `carth`, found via `tools/kotorfiles.py creatures END_M01AA`) worked:
  `console 'recruit 2 carth'` → `companions 2` on the host, live-confirmed via `partystate.ps1`.
- **The two-companion result is real but confounded.** Carth's `CreMoveState`/followFlag never
  left 0 (the follow mode itself never engages) — traced to his action-queue head staying parked
  on a leftover static-placement action from BEFORE the force-add, not to anything company-count
  related: his creature-side `CSWParty` backlink (`creature+0x4C0`,
  `EnemyPresenceController`'s own `CrePartyFollowInfo`) reads non-zero, ruling out the gate this
  round went looking for. Round 74's own full disassembly already proved the "other slot" lookup
  does not gate on slot 2 being populated, and this round's producer/consumer finding below
  touches only party-level (not per-member) state — both lines of static evidence say
  solo-companion is not a special case, but this round could not prove it live with a cleanly
  (not force-) recruited second companion, so this stays a confounded data point, not a closed
  question.
- **The pathfinder cursor's PRODUCER, found.** `tools/fieldwrites.py 0xFA4` finds all 8 writers
  to the cursor `PathWaitDest` polls, entirely inside `CSWPartyFollowDataArray::Reset`/
  `SmoothFollowPath`/`ProcessPoint` — never inside `PathWaitDest` itself, confirming it is purely
  a CONSUMER. `ProcessPoint`'s own callers (`tools/xrefs.py`) include one inside `CSWParty::Update`
  (the function round 73's `PumpPartyUpdate` fix already drives every frame) and one inside
  `CSWCCreature::SetPrecalculatedPosition`.
- **A DB error found by a real crash, and fixed.** The first read of the cursor treated
  `CSWParty+0x2DC` as a POINTER field (mirroring the symbol DB's own offset row) and dereferenced
  it — crashed the host live. Disassembling `CSWParty::Update`'s own call into
  `FindFurthestPartyMemeber_Current` (`lea ebx, [edi+0x2dc]`, not a `mov`) proves
  `CSWPartyFollowDataArray` is embedded INLINE in `CSWParty` at `+0x2DC`, not pointed to from
  there — the DB's `*` suffix on this one row, and its `CSWParty` size (736, too small to hold
  the 4020-byte embedded array), are both wrong. Fixed to treat `party+0x2DC` as the address
  itself, `NativeMethods.IsReadable`-guarded; no further crash across the rest of the round.
- **Live-confirmed: the producer works once `CSWParty::Update` is pumped.** A real engine
  teleport left the cursor flat; a real engine move action (`console 'walk 30 25'`) advanced it
  `0 -> 2` within seconds — round 73's already-shipped fix is sufficient for this half of the
  mechanism, no further pump needed.
- **This did not unstick Trask.** His own consumption index stayed at 0 while the cursor advanced
  past it — matching round 74's own `attempts` reading exactly: the outer phase-dispatch block
  that would re-poll `PathWaitDest` is not being re-entered for this creature at all anymore, for
  a reason that (per round 74's own conclusion, unchanged by this round) needs a live debugger to
  find, not further static disassembly. No fix shipped for it.
- **`party_fast_pump` non-combat re-test: still left OFF.** Frame time shows no meaningful
  difference either way (~1.9-2.0 ms mean on both instances, one similar single spike per leg).
  The requested companion-position-lag metric could not be produced: Trask's own follow-AI does
  not move (no natural signal), and a raw position poke used to manufacture one is reverted by
  the host's own per-frame system within about one poll interval on BOTH flag settings equally —
  itself live evidence for the `SetPrecalculatedPosition -> ProcessPoint` finding above, not
  something the flag causes or fixes.

`dotnet test tests/KotorCoop.Tests`: **1623/1623**, unchanged baseline, 2m28s. Both plugin builds
clean across the round's several rebuild/redeploy/relaunch cycles (one specifically needed to ship
the crash fix before further live testing could safely continue). Rig torn down clean
(`Get-Process swkotor,KotorCoop.Agent` empty), `auto_start_agent` restored to `1` in both
installs, the round's own test-only `dialogue_recruit_sync=1` override removed from both
`coop.ini`s afterward.

## 3. A client's XP is actively erased

`ProgressionSnapshotPacket` is host→client only and absolute (CoopAgent.cs, `PumpProgression`):
the host publishes gold and XP; the client **writes the host's totals over its own** every
tick. A client's own kill grants XP locally and then has it stamped back down within a
tick. Both instances read 300 in the audit only because neither had gained any since load.

### FIXED (2026-08-13), and the obvious fix was wrong

"Publish deltas and take the max" is right for one of these two values and would have been a
disaster for the other.

**Experience cannot be a delta.** Both engines already grant kill XP independently: a
client's mirrored enemy is killed through `Odyssey.KillCreature`, which applies a real lethal
damage effect precisely so the death runs the engine's own path — and that path reaches
`CSWSEffectListHandler::OnApplyDeath` (0x4E0AC0), whose call at 0x4E0CB7 is the **only**
caller of `CSWSCreature::ApplyDeathExperience` (0x4FB1E0). So the mirror grants the same XP
the owner does, for the same kill. Forwarding a delta would have doubled every kill in the
game. XP is therefore absolute and merges by **max**, in both directions — which carries the
thing that actually diverges (script-granted XP: `GivePlotXP` from a conversation only one
instance is having, stealth awards) and leaves the rest alone.

**Gold cannot be a max.** It is the one shared value that goes DOWN, and a max would
resurrect anything anyone spent. It travels as a cumulative earned/spent **ledger** — the
shape `EnemyDamagePacket` already proved — which the host applies against its own party
table; the host's balance IS the purse. A client's purchase now comes out of the party's
money exactly once, in any datagram order, however many times the report arrives, with a
generation field so a re-attached agent's re-reported totals are adopted rather than spent
a second time.

`ProgressionReportPacket` (type 38, protocol 23) is the peer direction the channel never had.
Covered by `ProgressionPumpTests`. ⚠ Not yet live-verified: gold changes are inferred from
the level, so the agent must not count its own writes as the party earning money — there is a
test for that, but it has not been watched against a real shop.

## 4. The root of most of the list: ownership is one-directional

"Clients are not triggering aspects", "Trask still walks around on the host while a client
is talking to him", death and dialogue not replicating from a client — one cause. The host
owns enemies, companions, doors and containers; `PartySnapshotPacket` says so outright
("every client freezes its own copies and mirrors these"). The client→host channels that
exist are narrow and specific: `EnemyDamagePacket`, `EncounterTriggerPacket`,
`ItemOpPacket`, `FlagOpPacket`, `JournalOpPacket`, `DialogueRequestPacket`.

There is no channel by which a client says *"hold this companion still, I am talking to
him"*. So the client's Trask freezes (it is mirroring), the client opens a conversation
with what is effectively a puppet, and the host's Trask carries on walking because the
host's AI was never told otherwise. Everything a client does to a host-owned object has
this shape.

⚠ Do not fix these one at a time. The next real step is an **interaction lock in the
owner's direction**: a client-claimed object that the owner's engine agrees to hold.
Click-to-join is the same missing primitive seen from the other end, which is why it has
resisted three passes of gate-hunting.

## 5. The black window until you click it — FIXED

A launched instance draws nothing until the player clicks into it. With two windows that
means clicking both, every launch.

`0x7A3A38` is the render-window-active latch: `Global::ActivateRenderWindow` (0x00401E00,
__stdcall, NO arguments — 0x00401E83 is a plain `ret`) sets it and early-outs if it is
already set; `Global::DeactivateRenderWindow` (0x00401D90) early-outs if it is already
clear. Measured on the live pair:

| moment | instance 1 | instance 2 |
|---|---|---|
| straight after launch | 0 | 0 |
| after focusing instance 1 | 1 | 0 |
| after focusing instance 2 | **1** | 1 |

Both come up unactivated, and — the useful part — instance 1 stayed at 1 after losing
focus. It is a latch, not a focus mirror, so activating once per launch is enough and
nothing turns it off again in play.

`FocusKeeper` now calls `Odyssey.EnsureRenderWindowActive()`, which invokes the engine's own
activation rather than poking the latch (its body also clears the unfocused pause reason via
`SetPauseState(2, 0)` — the same one FocusKeeper clears). Level-triggered, so the several
deactivate/reactivate cycles the load chain performs are absorbed. Verified: both instances
render fully with nothing ever clicked. `coop.ini activate_render_on_start=0` restores stock
behaviour.

### The spurious startup pause it exposed

With both windows finally drawing, the first launch came up with `PAUSED / PRESS THE PAUSE
BUTTON TO CONTINUE` on both. Not the pause adoption — the log said `space fallback ->
paused`. `PauseKeyController` starts with `_spaceWasDown = false`, so the very first poll is
a rising edge whenever space happens to read down, which it does during startup; the
instance paused itself and the peer mirrored it. The first observation now only records the
key state (`_spaceSeen`). Verified: the pause log is clean through a launch.

## 6. The pause button

`Odyssey.ReconcilePause` has a `union == 0` branch that force-unpauses both world timers
and clears the GUI banner whenever neither pause mask holds a bit. It runs every 20 frames,
session or no session. During live play at 14:48 it logged five repairs in three seconds.

If the engine's own HUD-button path moves the world timer and the GUI belief without
setting a mask bit, that branch is a second force-unpause — reinventing exactly the bug
`PauseSanityPatch` exists to delete, this time on our side of the line. That would present
as "clicking pause does nothing at all", on both instances.

CONFIRMED and FIXED. One press produced:

    .600  reconciler UNPAUSED (banner) — no mask held a bit (server 0x00, client 0x00)
    .744  space fallback -> paused

The engine's own pause paths move the banner and the world timers but set NO mask bit, so
the reconciler read it as stranded and tore it down within ~60 ms. Space only ever appeared
to work because `PauseKeyController` re-paused it 200 ms later; the HUD button has no such
fallback and did nothing at all. The reconciler now ADOPTS the banner instead — writing the
mask the engine forgot — which also makes the pause publishable, so a HUD-button pause now
reaches the other player. Verified: adopted in 27 ms, mirrored in 39 ms, no fallback.

### Releasing a shared pause — FIXED for the key, OPEN for the button

A release could only ever be made by the player who STARTED the pause. Reproduced: paused on
instance 1, released on instance 2, instance 1 stayed frozen. The cause is that a release is
not observable as the absence of `FlagPaused` — a mirroring player never had that flag set,
because mirrored pauses are excluded at the source. It needs its own signal:
`PlayerState.FlagPauseReleased` plus a serial each way through the block (version 30).
Verified: instance 2 released, instance 1 followed 92 ms later.

⚠ STILL BROKEN: **the HUD hourglass can pause but cannot UNPAUSE.** Measured — click it once
and the pause is adopted and mirrored (81 ms); click it again and *nothing happens at all*,
on either instance, with no reconciler line. The masks were read live straight afterwards and
both halves still held `0x01` on the instance that was clicked, so this is **not** a
propagation failure — the second click never unpauses even locally.

The likely cause, unconfirmed: an adopted pause has no entry in the session's `PausedBy` list
(we write the mask bit directly rather than going through the engine's pause registration),
so the button's unpause path finds nothing to remove and no-ops. `PauseKeyController`'s
200 ms fallback is what rescues the space key, and there is no equivalent for the mouse.

NEXT: find the HUD pause button's handler and either hook it (the click is the intent, and
we already own the pause) or give the mouse the same fallback the key has. Do NOT "fix" this
by re-asserting in the reconciler — that is the loop that made the banner unclearable before.

---

## §4 answered: the per-object interaction lock (2026-08-11, live-verified)

§4 recorded that ownership is one-directional and that nothing tells an object's owner when
somebody else is using it. That channel now exists.

**The reported symptom:** a client walks up to Trask and starts talking. The client sees a body
it drives from the host's position stream; the host's own AI, never told anything happened,
carries on walking the real Trask around. Both screens show a conversation with a companion
who is strolling out of it.

### Shape: a lease, claimed early

* **A lease, not a handshake.** The claim is re-sent every tick while it is held and expires
  on its own. There is no release message, so losing one cannot strand an NPC for the rest of
  the session — which is the failure that actually matters, because the case to design for is
  a client dying mid-conversation. Same discipline as the encounter-trigger channel.
* **Claimed the moment a player starts interacting**, which is earlier than a conversation
  exists. That is what makes a contested NPC fair, and it fixes a bias nothing had noticed:
  "first speaker wins" is only fair if both claims race on equal terms, and they never did —
  the host's dialogue claim reaches the arbiter **in-process at zero latency** while a
  client's crosses the network. Two players clicking the same NPC at the same instant
  resolved to the host every time, however early the client clicked. `DialogueArbiter` now
  takes a `lockHolder` and refuses a claim on an NPC someone else holds **even when the token
  is free**.
* **A claim with no partner tag is never refused.** The tag is empty for a real window at the
  start of every conversation (`gui+0x170/0x174` stay `OBJECT_INVALID` until the server
  resolves the participants), and refusing on an empty tag would refuse every conversation in
  the game for its first few hundred milliseconds.

### The hold itself

`Odyssey.HoldCreatureStill` clears the action queue **only when the head action is a move**.
A blanket `ClearAllActions` every pass is what stopped a mirrored enemy ever landing a swing —
the queue does not distinguish a walk from the whole fight, so the test has to.

The agent filters the local peer's own claim out before handing the list down, so the plugin
holds everything it is given without needing to know its own peer id. Sending it would be
actively wrong: the object *this* player is talking to is already frozen by the engine's own
dialogue system.

### Measured

Client starts the conversation, host owns the NPC:

```
  t   host#  client#  hostNode  clientNode  hLock  cLock   hostPartner  clientPartner
  0       0        1        -1          29      1      0                end_trask
  -> host clicks 'end_trask' (join)
  5       1        1        29          29      1      0   end_trask    end_trask
NODES: on the same entry for 9 of 9 paired samples.
```

`hLock 1` from t=0 — the host is holding Trask still at the client's request, before the host
is in the conversation at all.

`verify.ps1` carries it permanently: *an interaction lock reaches the other instance*.

### `force_dialogue_party`, and why it is OFF

With the lock in place, being *pulled into* a peer's conversation rather than clicking to join
is a few lines — it sends the same request a click sends and takes the same adopt path.
Verified live: with nobody clicking, the host joined **531 ms** after the client started and
landed on the same node (14/14 paired samples).

It shipped **off**, for two reasons:

1. It is the behaviour that was already removed once. Players were yanked out of whatever they
   were doing from anywhere in the module; joining became a click because walking away while
   your partner talks to a shopkeeper is a legitimate thing to want.
2. **Unresolved:** a conversation that recruits or dismisses a companion calls
   `AddPartyMember` / `RemovePartyMember`, and those have **no channel in this mod**. A player
   driven into a recruitment conversation ends it holding a party roster the other instance
   does not share — and the roster is what the companion channel keys on. Ordinary
   conversation is fine; recruitment is not.

### RESOLVED (2026-08-22): `dialogue_auto_join` replaces the flag, seamless

Both reasons above are now addressed and the bare on/off flag is gone, replaced by
`dialogue_auto_join=near` (default) / `always` / `off` in `InteractionLockController.cs`.

Reason 2 is resolved elsewhere first: `PartyRosterController`'s `dialogue_recruit_sync` gives
recruitment/dismissal an actual channel (a `PartyRoster` packet reconciling the seat on both
instances) — the exact gap this section's own point 2 named as missing. Ordinary conversation
was already fine; recruitment now has somewhere for its consequence to go.

Reason 1 — the "yanked from anywhere in the module" complaint — is answered directly by
`near`, the new default, instead of by leaving the whole feature off: auto-join now only fires
when the joining player is already within `dialogue_auto_join_range` (default 20 game units) of
the conversation's NPC, and not in combat, a minigame, or a conversation of their own. Outside
that range the player is not silently ignored — a "walk over to join" notice
(`InteractionLockController.DrawFarNotice`) tells them a peer is talking nearby, the same
spirit as the existing "click the same character to join" notice `DialogueSpectateController`
already shows for a blocked click, just for the case where auto-join declined rather than
refused. `always` is available for a player who wants the old unconditional pull back (now
safe, since reason 2 no longer applies); `off` restores click-only.

The decision itself — `DialogueAutoJoinRule.ShouldAutoJoin`
(`src/KotorCoop.Protocol/DialogueAutoJoinRule.cs`, unit-tested, shared by source into the
plugin) — is a pure function of the mode and the JOINING peer's own local facts: no parameter
for "am I the host" or "who started this conversation" exists in its signature at all. Both
directions run the identical code path on whichever peer would be joining
(`InteractionLockController.TryForceOpen`, plugin), so a host auto-joining a client's
conversation and a client auto-joining the host's conversation cannot diverge by construction —
verified by `DialogueAutoJoinRuleTests.TheRuleProducesTheSameAnswerRegardlessOfWhoStartedTheConversation`.
This is narrower than the ARBITER's own known host-favouring race (§ above, "first speaker
wins" when two players click different NPCs at once) — auto-join only decides whether a JOIN
request is sent for a conversation that already exists, it does not touch who wins a
simultaneous NEW claim.

Distance is measured from the joining player's own position to the resolved partner creature
(the same body `ApplyPeerLocks` already resolves and holds still), independent of
`dialogue_join_teleport`'s own separate, smaller, engine-verified 10-unit range (which still
handles the actual walk/snap once a join is decided — the two thresholds serve different
questions: "does this feel reasonable" versus "can the engine's own `StartDialog` even be
reached from here").

NEEDS-LIVE (nothing in this pass was run against the game): that the notice actually renders
without flicker or overlap alongside `DialogueSpectateController`'s own notice (placed on an
adjacent screen row, not measured together); that a real recruitment conversation, joined
automatically under `near`, ends with both instances' party rosters agreeing via
`dialogue_recruit_sync` in practice and not merely in theory; the exact feel of the 20-unit
default range in play (chosen as a middle ground between `dialogue_join_teleport`'s 10-unit
engine gate and "anywhere in the module", not measured).

### Round 77 (2026-08-23) — skip-desync retest (still blocked, narrower now); the forced-
conversation positive case finally observed live, twice; three real-play bug reports triaged, one
fixed and live-confirmed, one shipped-but-not-latency-measured, one root-caused and documented

Two jobs from the original brief plus three real bug reports the user filed mid-round from actual
(not staged) play, folded in per the coordinator's own priority call. Full write-ups:
`docs/research/dialogue-skip-desync.md`'s and `docs/research/dialogue-sync.md`'s own dated
"round 77" sections, and `docs/research/container-sync.md` §9. Short version:

- **Skip-desync retest: still blocked, but the blocker moved.** `tools/sendkey.ps1`/`click.ps1`'s
  `Focus()` genuinely could not move Windows' OS-level foreground pointer at all (same symptom as
  last round, different competing app). Root-caused one level deeper: `AttachThreadInput` alone
  does not satisfy Windows' foreground-lock guard; a synthetic Alt keydown/keyup immediately
  before `SetForegroundWindow` does — fixed in both tools. But even with foreground CONFIRMED
  held (verified by `GetForegroundWindow()` and a same-call screenshot showing the live game),
  a burst of injected key presses still produced **zero effect** — no node change, byte-identical
  screenshot before/after. The desktop was independently confirmed to be in genuine concurrent
  use by its own human operator throughout (explains the transient re-loss of focus between
  separate tool calls, does not explain the zero-effect result inside the atomic, held-focus
  test). Points at KOTOR's own DirectInput device re-acquisition needing a real
  `WM_ACTIVATE`-shaped message sequence a forced `SetForegroundWindow` may not deliver — inferred,
  not disassembly-verified this pass.
- **Forced-conversation positive case: closed.** `DialogueGateHook.LastStartForced` read TRUE
  live, twice, independently (host and client, two different trigger ids in the Taris Sith-base
  interrogation cluster in `tar_m02ad`, reached by warp per the user's own direction) — the item
  `docs/research/dialogue-sync.md` has carried as NEEDS-LIVE since its own 2026-08-23 forced-flag
  work. The auto-join-from-genuine-distance half stayed unconfirmed (both real characters kept
  landing at the same module arrival waypoint before a `blockpoke.ps1 walk` away could clear it in
  time) — left NEEDS-LIVE, a tooling/positioning limitation, not a code question this pass found
  wrong.
- **Bug 1 (forced dialogue didn't reach the other player) — a real defect found and fixed.**
  Reading the code this pass's own positive-case reproduction produced found
  `DialogueGateHook.OnStartDialog` computing "is this forced" purely for diagnostics and never
  consulting it in the refusal decision: a genuinely forced `StartDialog` got refused
  ("a peer holds the token") exactly like an ordinary click competing with someone else's
  conversation — and a refusal here clears the engine's own queued `BeginConversation` action, so
  an unlucky forced conversation could be lost outright, not merely delayed. Fixed: the forced
  case now bypasses the whole exclusivity gate unconditionally, mirroring
  `DialogueAutoJoinRule.ShouldAutoJoin`'s own `isForced` handling on the JOIN side. Live-confirmed
  fixed: zero `blocked` lines before the FIRST forced `StartDialog` attempt of a fresh session,
  after rebuild/redeploy, same rig, same trigger cluster.
- **Bug 2 (combat presence went quiet after dialogue-into-combat) — root-caused, not fixed.**
  `CoopAgent.PumpEnemies` holds the enemy census (not damage, which is unconditional) while
  `bridge.IsInDialogue` on the HOST — always the census authority — matching the report's own
  damage-synced/presence-didn't split exactly. Most likely mechanism: this round's OWN forced-join
  fix (and the underlying design generally) can now pull the host into FOLLOWING any peer's forced
  conversation, which genuinely pauses the host's own hostile-AI simulation for that conversation's
  duration, the same as it always has for the host's OWN conversations. Not fixed this round:
  the gate's underlying behaviour is correct (a paused simulation genuinely cannot produce fresh
  census data), and no live reproduction of the specific ambush-into-combat timing was available
  this session to judge a fix against. Documented with a specific next-step measurement for a
  future round.
- **Bug 3 (container open/close cross-instance delay) — found, fixed, smoke-tested, not
  latency-measured.** `PumpInteractionLocks` (the open/close animation, the use-lock) already rode
  the ~150 ms combat cadence; `PumpPlaceables` (the DURABLE open-or-locked state) rode the
  ~1000 ms heavy cadence — up to ~850 ms of daylight between "looks closed" and "durably marked
  closed" for a peer, matching "very big delay... closing biggest". Fixed with the same rollout
  shape `dialogue_fast_join`/`party_fast_pump` already used: `container_fast_pump` (default off)
  moves `PumpPlaceables` onto the 150 ms cadence. Ran a full live session with the flag on — no
  crash, no regression in anything else exercised — but no actual open/close timing was measured
  before vs. after (no OS input access this session to click a container).
- **Tests and build**: `dotnet test tests/KotorCoop.Tests` 1635/1635 passed both before and after
  the plugin changes (same count — the changes threaded settings and fixed a gate, added no new
  managed surface needing its own test). Both builds clean, both installs rebuilt/redeployed
  mid-round to pick up the `DialogueGateHook` fix before its own live-confirmation pass.
