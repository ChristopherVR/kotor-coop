# Courtyard, dialogue, auto-follow and console shutdown regressions

## Played-session evidence

The September 26 session logs in the two game installations, plus the agent logs
in `%TEMP%/kotorcoop-demo`, show these concrete failures:

- At 14:20:27 the host started a forced conversation with `dan14aa_bay`. The
  listener tried to teleport to `(191.9,119.6,8.4)`; the walkmesh refused it.
  The listener logged a join request but never reached `StartDialog`.
- The shipped `c_khounda001.utc` has tag `dan14aa_bay` and an empty `Conversation`
  field. The actual scene is `dan14_cut01`; naming only the actor cannot replay
  this scripted conversation.
- At 14:20:20 the listener acknowledged node 28, then its own queued entry sent
  it back to node 0. The same owner serial was considered already applied.
- At 14:22:02 the joiner owned the conversation while its raw dialogue flag
  flickered off. Auto-follow resumed and sent a walk toward the host; host loss
  was not declared until 14:22:06.
- Some earlier sessions have simultaneous heartbeat/frame stalls. Expensive
  dialogue-partner heap scans contribute to game-thread stalls. The logs do not
  establish one cause for every reported focus/movement freeze.

## Changes

The StartDialog hook copies its caller-owned resource-name override while the
string is alive. Protocol 75 carries it through requests, arbitration and
snapshots; shared-block version 81 publishes local/remote names at
`0x8600`/`0x8614`, with total size `0x8700`. Both peers require matching updates.

Forced joining uses the engine's ActionStartConversation VM routine with the
actual resource name and ignore-start-range argument. It does not require a
walkmesh-valid teleport beside an invisible scene actor. An empty override still
uses the actor's default conversation. Ordinary joins now report failure when
neither teleport destination is valid.

Dialogue partners resolve through the server's live object table rather than a
private-heap sweep. Cached object IDs are resolved before touching cached
pointers, and tags are rechecked. A missing partner retries after 250 ms.

Followers clear obsolete pending entries before driving the owner's node, and
reassert it if their local engine subsequently drifts despite an acknowledged
serial. Auto-follow holds for any active conversation token, remembers a
successful join across UI gaps, and stops issuing movement when the link is lost
or silent for more than one second. The plugin rechecks automatic movement when
it executes, closing the command-delivery race with conversation entry.

The foreground game recovers a missed input-activation latch. This is a recovery
for the focus-switch report; reproducing the player's complete freeze still
requires a real focus-switch play check.

The agent disposes the session on exit and writes a timestamped closure reason
after the dashboard restores the original terminal screen. That restoration is
why the old console ended with its startup lines; it was not evidence of a
restart.

## Verification

- Fast suite: 5,057 tests passed, including scene-name packet round trips,
  arbitration, pump publication across dialogue gaps, node drift and link loss.
- Native x86 plugin built with warnings treated as errors.
- Two isolated game directories used copied saves and separate logs; original
  saves, configuration and played-session logs were untouched.
- An explicit `dan14_cut01` start reached both engines. The auto-follow listener
  delivered content 15 ms after opening and followed the owner's next node.
- A second run exercised forced joining by injecting the remote forced flag
  while replaying that scene. The listener delivered content after 16 ms,
  followed the next node, removed the scene actor and fought the spawned
  creatures. This verifies the join path, not the original trigger traversal.
- The first live run exposed a cached-pointer access violation after actor
  destruction. Symbolication named `InteractionLockController.Resolve`; storing
  the ID separately and resolving it before dereferencing fixed the repeat run.
- Both agents' captured stdout ended with a timestamped
  `Co-op agent closed: the game exited.` line. All temporary game/agent processes
  were stopped after the checks.
- The final matching plugin and agent were installed into the GOG game directory
  and `D:/Games/swkotor-gog-p2`; their hashes match the built artifacts. The normal
  installer preserved both existing `coop.ini` files.

Remaining live checks are the original courtyard trigger during normal movement,
long reply-heavy conversations with fast skips, host departure during a
joiner-owned conversation, and rapid focus switching between visible windows.
