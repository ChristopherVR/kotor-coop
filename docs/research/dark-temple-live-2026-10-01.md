# Dark-side temple recruitment, live staging (2026-10-01)

Build main `b9bf4294`, private game4 (host) / game5 (joiner). Evidence: `artifacts/dark-temple-live-20261001-1620/` (RESULTS.md, shots, run1-run5 logs, party/creature dumps). Staged with `god`, `tp`, `startdialog 0x2 unk44_evilbast`, `selectreply`, `hurt 200 0x2`, `spawncreature p_bastilla001`; fixture `000035 - dark_handoff_ready`.

| Point | Result |
| --- | --- |
| Bastila joins the host's party through the shipped script | Fails on the fixture as-is (no object tagged `bastila`; the save holds `bastilae`). Passes once a `bastila` object exists (host `party roster: local party is now 0x001 (recruited 0x005)`). A tag byte-patch made the dialogue owner the new party member and hung the host at node 63, a patch artefact. |
| Joiner mirror | Pass: `party roster: spawned NPC slot 0 as 0x71`, one body at the host's position, no duplicate. Oddities: the joiner's body is listed with tag `end_trask`, and a host creature `end_trask` 0x96 appears after the recruit script in every run and mirrors as a phantom (suspect `AddAvailableNPCByTemplate` slots 15..19 vs the 0..8 roster table, see dark-side-branch.md; not investigated). |
| Former companions turn hostile (host-led) | Pass: Jolee and Juhani removed from both rosters, kills agree. A liveness kill burst of about 20 lines repeated for 0x5F. |
| Joiner-led dialogue | **Fail:** node scripts ran only on the joiner, the host's Bastila stayed neutral, the host's conversation restarted from node 150, the joiner fought a local hostile Bastila and looped `auto-joining a peer's conversation` every 4 s (34 times). Companion faction flips are not a synced channel (roadmap item 43). |
| Reload / reconnect | Partial: a joiner reconnect keeps one Bastila; a host `save` + `loadgame` keeps her, but the joiner did not re-adopt after the host's mid-session load in two attempts (W_GIT/W_SAVE, stale Jolee/Juhani present). |
| Crash | One host crash from our own `faction 0x71 me` dev verb with a joiner's id (`Odyssey.GetCreatureFaction`); fixed by validating the object is a readable creature before reading its faction. No crash from the scenario itself. |
