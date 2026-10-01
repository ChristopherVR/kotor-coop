# Manaan round trip, live staging (2026-10-01)

Build main `b486c4f1` (plugin built 17:26), private game4 (host) / game5 (joiner), a copy of `campaign_seafloor`. Evidence: `artifacts/manaan-roundtrip-live-20261001-1729/` (run1-5 logs, shots, `joiner-crash.bin`, `run4-joiner-crash.bin`).

Staged: `tp` inside modules, `setglobal MAN_SHORTCUT true` (station door `28d`), the Rift pressure puzzle solved by hand (journal entry plus 1500 XP), `god on`. Real doors and replies for transitions. Not done: the Selkath/survivor puzzle in `28ac`, the star map, the control-station airlocks (`28cn`, locked). The Rift was reached through the real `28d` door and left through the shortcut door `0x7` with `crossdoor` (`usedoor 0x7` is refused).

| Point | Result |
| --- | --- |
| Joiner parked at the station | **Fail.** Refused as a follower (`not following ... single-seat minigame track`), but in 3 of 5 host-led airlock attempts the joiner's own copy of the airlock conversation ran `announce: scripted transition ... (Script) -> 'manm28ab'` and both players ended up underwater. In the other 2 it stayed behind with no "waiting" message and no input lock (synthetic W moved it 5 to 8 units); the host plugin logged no solo flag for Manaan. |
| Release on return | Partial. Both ended in `manm28aa` with one phantom each. The `28bw` return was clean twice. After the Rift shortcut return the joiner's screen was black with no HUD and a frozen camera in 4 of 6 screenshots, it was reset to the station spawn, and `W_GIT module manm28aa's object mapping differs` appeared after every Rift return. |
| No crash or duplicate bodies (host-led) | Pass. The effect-removal crash did not recur (8 `effect fence: retired` lines); the host never crashed. |
| Joiner-led trip | **Fail.** Not refused: the joiner's own script went to `manm28ab` alone. Crash 1 (18:00:33): AV `0x63D4B3` in `CSWCObject::GetServerObject` from `CSWGuiDialogComputer::UpdateSkills`, the joiner's own airlock dialogue. Crash 2 (18:05:27): pulled back to the station 8 s later, AV `0x654B68` in `CSWCMessage::HandleServerToPlayerItemScript_Add`. |
| Host disconnect/reconnect mid-trip | Pass. The joiner reconnected as peer 2 and was not stranded; the host was released via the shortcut. |

Leads: the parking lock (`MinigameLockController`, flag set agent-side by `MinigameModuleRule`) never engaged because the host plugin raised no flag, and a joiner that watches the host's airlock conversation executes its transition script locally. `0x63D4B3` is the same retail `GetServerObject` stale-pointer family recorded in transition-crashes.md.

## Retest after fixes (2026-10-01, 18:15 build)

Root causes: (1) `MinigameLockRule.IsRacing` required the minigame flag AND a track module, and the Manaan envirosuit modules (`manm28ab`, `manm28ad`) never set the flag, so the parking lock never engaged; (2) the joiner auto-joined the host's `man28_airlock` conversation and ran its transition script. Fixes: a peer in either solo module now locks without the flag (away peers never lock; a local player in one is never locked), and `man28_airlock` is never auto-joined (`DialogueAutoJoinRule.NeverAutoJoin`).

Result (`artifacts/manaan-roundtrip-live-20261001-1815/`): parking 3 of 3 host-led airlocks plus the Rift, joiner moved 0 units, `minigame lock: Lanik started a minigame - holding control until it ends`, notice "is in a solo section - waiting"; released on every return (`finished - control is yours again`, one phantom each); the black screen after the Rift shortcut return did not recur (a post-load "Item(s) Received" popup needed one click); host disconnect in the Rift: joiner reconnected, stayed held until the host returned. Not repeated: joiner-led trip (two original-engine crashes remain), star map, control-station route.

## Joiner airlock gate (2026-10-01, `artifacts/manaan-joiner-gate-20261001-2330/`)

`DialogueGateHook` now refuses a session joiner starting `man28_airlock` (`JoinerIsClient` from the shared-block session role; notice "Only the host can start this - wait for them"; log `dialogue: refused a joiner starting a solo-section conversation`). Live: host-led airlock to `manm28ab` with the joiner parked and the release on return passed; the host starting the airlock conversation with the joiner idle passed (panel shown). The refusal itself is **inconclusive**: the joiner's `usedoor 28aw` and `startdialog 28aw man28_airlock` produced no conversation and no crash, but also no `engine reached StartDialog` line, notice or refusal log (the joiner was under Selkath attack and never walked); a real click on the door was not tried. The lock that appeared not to release after the first load came from the fixture itself starting the joiner inside `manm28ab` (it followed the host out at 23:39:30), not a stuck lock.
