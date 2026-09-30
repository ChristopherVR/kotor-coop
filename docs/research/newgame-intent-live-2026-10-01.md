# Accepted New Game and first-join checks

The accepted New Game witness distinguishes a player's deliberate new game from wipe
recovery. ABI 95 also stamps load requests with the current witness generation: a queued
load from before a newer accepted New Game is refused before either native load phase.
Explicit manual intent does not override this generation fence.

## Live evidence

- **Stock GUI after a real wipe, ABI 94:** the client selected New Game through the actual
  game menu. The accepted hook advanced generation to 2 and the Agent disconnected that
  client in the same second. Its new Endar character had 9 HP and survived a later host
  reload. The prior character checkpoint's bytes and timestamp were unchanged. This is
  an ABI 94 live result; the matching ABI 95 stock-GUI repeat was not performed.
- **Native console and load fence, ABI 95:** an isolated console New Game completed with
  generation 2. Controlled old-generation automatic and manual load payloads both returned
  refusal status 10 without changing the world. A subsequently issued actual manual load
  stamped generation 2 and succeeded. The negative fixture intentionally supplied an old
  token; it does not claim a naturally timed network-stage interruption.
- **First join, ABI 95 plus Agent commit `e69d7bc`:** an uninterrupted two-player
  `join_mode=newcharacter` session completed automatic New Game, class selection and
  acceptance. Native capture saved 559,983 bytes; the Agent wrote the chosen character
  sidecar, adopted the host's 907,375-byte world, completed native load serial 1 and
  reconnected as peer 2. The host remained class 2 with 6 HP; the client remained its
  chosen class 1 with 9 HP. No fresh-intent disconnect fired during creation.

The first-join run had no QuickSave directories before or after, so it proves no new
QuickSave was introduced, rather than preservation of an existing QuickSave. Its own
character sidecar was intentionally replaced by the newly chosen character.

## Resolved first-join stall

Three earlier attempts using the pre-fix Agent stalled before accepted New Game, inside
native class-preview rendering. Session `Command=1` and an armed capture hook both worked
immediately without an Agent. A bounded control admitted the actual client and queued
creation while its game thread was paused, stopped only that owned Agent, then resumed:
the constructor completed immediately. The updated Agent then passed the uninterrupted
full sequence above.

`e69d7bc` reuses an attached block only after validating its full ABI signature. Coherent
native player-zero, unavailable running module and unchanged epoch observations return
world-not-ready without rescanning committed memory or adopting freed PC-shaped objects.
It preserves the discovery fallback for external mode and invalid attachments. This
addresses the measured scan pressure; the exact OpenGL driver wait mechanism is not proven.
The worldless, known-load and concurrency regression selection passed 22 tests.

Artifacts: `artifacts/newgame-intent-v94/live` for the stock GUI check;
`artifacts/newgame-intent-v95/game3` and `session-control` for isolated checks;
`artifacts/newgame-intent-v95/rig-third/control2-result.json` for the admitted-state control;
and `artifacts/newgame-intent-v95/worldless-firstjoin` for the uninterrupted capture,
adoption, native class reads and logs. Only owned PID/start-time pairs were stopped;
private game configurations and original native DLLs were restored. Normal new saves and
the newly chosen character checkpoint remain.
