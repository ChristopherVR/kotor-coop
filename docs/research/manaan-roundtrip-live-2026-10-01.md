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
