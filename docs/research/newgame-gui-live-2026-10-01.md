# Stock GUI New Game intent and cancelled adoption, Native96

2026-10-01, immutable Native96/wire85 and Agent from `7cf5706`. Private game1/game2
only, port39147; game PIDs56572/65524. The matching binaries and complete test results
are recorded in [the effect relay report](enemy-effects-live-2026-10-01.md).

**Passed:** actual stock-menu New Game acceptance during an abandoned asynchronous
OwnLoad adoption, chosen character creation, unchanged previous character checkpoint,
and no hijack after a restarted active host reloaded its checkpoint.

**Not established:** a clean full wipe. The host hit a native access violation before
the client menu check. This run must not be cited as clean wipe recovery or as a fix
for that crash.

## Actual damage and crash qualification

The unchanged Endar baseline loaded at native6/6. Controlled placement and actual
attacks against original Taris GIT hostiles produced logged NPC damage but stopped
at native1 HP, with no down event. The inherited tutorial min-one-HP protection makes
that fixture unsuitable for an NPC-driven wipe; no protection flag was altered.

Both then actually loaded private unchanged copies of original
`000022 - campaign_manaan_return_ready`, yielding native12/12 in `manm28aa`. A completed
dedicated client save preceded combat. Controlled placement and actual combat with
original Selkath produced client nativeHP-11/12, downed1, DownedSerial1 and
GameOverSerial1. The host remained far enough away to prevent nearby rescue before
its own original Selkath encounter.

The host exited at09:25:42. Preserved crash.bin reports access violation at/EIP
`0x4FED74`, serial2, ESP`0x1AFC8C`; image stack includes `0x6FB81D`, `0x6FB822`,
`0x71B6A6`, `0x4B0DC5`, `0x4BAC78`, `0x8B720D`, `0x564216`. This agrees with the
separate Manaan queued-effect-removal investigation. No repair or synthetic down
substitution was introduced in this run.

## Accepted stock intent and chosen PC

The client reached the actual stock main menu. At09:25:58 its abandoned OwnLoad
adoption was still attempting the UDP fallback after the dead host's TCP port failed.
At09:27:21 actual HWND-local buffered input clicked the stock **New Game** button.
The verified native intent generation advanced0 to2; the Agent logged:

> new game selected - left co-op so character creation can continue; connect again when you are ready

The previous character sidecar and metadata SHA256, lengths and file timestamps were
unchanged immediately after this intent and after the later host reload. The preserved
sidecar's actual player was Soldier0@1 plus Sentinel5@0 with saved HP fields0/0/12:
the verified GOG serializer oracle gives effective living HP12.

All subsequent character creation used the stock GUI through native buffered clicks:
male Scoundrel, Quick Character, portrait confirmation, default name **Dandon Star**,
and Play. Actual native new world `END_M01AA` appeared at15.4/20.1/-1.3, with6 HP.
Read-only current-server-array/vtable/ID checks proved the new local creature had a
single class2@1, distinguishing it from the previous0@1/5@0 checkpoint. The Agent
reported no session. There was no injected intent-generation value or automatic
character-creation substitute.

## Later active host reload

Only the owned host was restarted (new PID16948, new recorded start time and exact
game path). After actual load of the same original Manaan checkpoint and starting its
matching host Agent, a further active-session named `loadgame GUI Wipe Manaan`
completed. Native host was in `manm28aa` with HP12 and classes0@1/5@0. The client
remained in `END_M01AA` with HP6, the same current typed new body, class2@1,
intent generation2, and no session. No co-op world adoption resumed.

## Evidence and restoration

`artifacts/newgame-gui-live-v96` contains stock-menu/chargen screenshots, coherent
intent and current-PC JSON, all checkpoint hash/time comparisons, original source
copy checks, host restart/load logs, copied final native/Agent logs and saves, and
the exact crash record plus pre-restart host native log.

Cleanup validated every owned PID/start-time/path, including the restarted host.
Both private copies passed exact original SHA256, size and file-mtime restoration
for configuration, native DLL, saves and mutable runtime files. No raw HP, FP,
class, quest/global or protection writes were used. No additional companion
equipment suppression claim is made. GPU slots were released after cleanup.
