# Joiner re-adoption after a host mid-session load (2026-10-01)

Build main `58406a9c`, private game4 (host) / game5 (joiner), fresh agent. Evidence: `artifacts/readopt-live-20261001-1915/` (times.txt, per-test logs, sliced failure logs under `evidence/`). Triggered by a dark-temple run where the joiner did not re-adopt twice (`artifacts/dark-temple-live-20261001-1620`).

| Test | Result |
| --- | --- |
| A: Taris fixture, host `save` + `loadgame`, same module, x3 | Pass: 17, 19, 20 s from `loadgame` to the joiner's "rejoined". Joiner saves 5 to 8 s after the rejoin were absorbed by the same-world lineage rule. |
| B1: host loads an older slot of the same module | Rejoin passed in 20 s, then W_SAVE was raised on both sides at 19:30:06, five seconds after the joiner's own periodic Co-op save, and sync stayed off for over 2 minutes until the next host load. Not a clean repro. |
| B2/E2: host loads the Endar Spire `END_M01AA` save | Fail, twice. The joiner followed the module transition instead of adopting; the automatic rejoin refused every 3 to 4 s (`the game never answered that - is a world loaded?`, `current-character save refused or world changed; connection retained`), 30 times over 110 s. The joiner plugin logged `frame hook not fired`: the main loop was parked (opening cinematic). Loading back into Taris afterwards took 67 s. |
| C: dark-side fixture, same module, x3 | Pass: 12, 10, 11 s. |
| D: joiner `disconnect`/`reconnect`, then host `save` + `loadgame` | Pass in 13 s; the earlier failure did not reproduce. |

Refusal location: `AutomaticRejoinCapture.cs:43` (90 s timeout) and `:58` (`!ReturnSaveIsCurrent(save) || save.Refused`) gate the host-reload rejoin on the joiner producing a fresh character save. The B1 stall is probably the lineage seed on reconnect (`CoopClient.Connection.cs:138-140`, `SessionManager.cs:804-808`) marking lineage agreed with stale load serials, so `PersistentSaveMismatchRule` (`!worldsProvenSame` only) never re-adopts; inferred from code, not from a log line. No code was changed: the common path works and the failing cases are not reproducible on demand.
