# Dantooine Jedi path, live (2026-10-01)

Build: main `2c08d408`, current plugin and a freshly published agent, private game4 (host) / game5 (joiner). Evidence: `artifacts/dantooine-jedi-live-20261001-1455/` (RESULTS.md, shots, run logs).

Staging: both players started as non-Jedi Scoundrels. `warp danm13` (once per run), `setglobal DAN_DORAK_DONE true`, `startdialog dan13_dorak` / `dan13_zhar`, `tp` to the workbench, `selectreply` through the questionnaire. No `addclass`, `scriptgive` or forced class grant.

| Scenario | Result | Decisive evidence |
| --- | --- | --- |
| Host-led Dorak | Pass (two runs) | Host picked Blue: `class-grant: a script added class 3`. Joiner: `jedi-choice: request #1 - showing the class-choice prompt`, then picked Consular (class 4), later Sentinel (class 5). `stats`: host `[2@1,3@0]`, joiner `[2@1,4@0]` / `[2@1,5@0]`. |
| Joiner-led Dorak | Pass | Joiner took Yellow (class 5); the host's own screen showed the prompt and picked Consular (class 4). |
| Zhar saber | Pass | Host-led: host built blue `g_w_lghtsbr01`, joiner logged `first lightsaber: giving ... 'g_w_lghtsbr04'` (yellow, Sentinel). Joiner-led: joiner built yellow, host received `g_w_lghtsbr03` (green, Consular). One saber each in the pool, unequipped; a joiner reconnect produced no second give. |

No crash and no new error lines (only the known first-chance 0x406D1388).

Caveats: the prompt choice was made with `selectreply`, not by clicking the rows; one log line said the prompt "never opened locally within 5000 ms" while the prompt was visibly up and answered (cosmetic); a host Zhar conversation stalled at node 101 in run 1 after manual `closegui`/`popmodal` calls and did not recur in runs 2 and 3; one launch failed to adopt the host world until relaunched (known flaky launch); saber colour was read from item listings and logs, not save files; the joiner's agent listed the host's saber as "held by 0x14B", probably the host's equipment mirror, not confirmed; the natural prerequisites for Dorak were skipped.
