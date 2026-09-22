# Runtime difficulty agreement

The GUI difficulty setting is per instance and can change during a session. Co-op now samples
the live setting and reports a warning when two peers disagree. It never changes a player's
setting, rejects a join, or disables a synchronization channel.

## Verified engine facts

`CSWSCreature::DoDamage` reads the byte at `CClientExoApp::GetClientOptions() + 4` for the PC
and party members. That is the in-game slider (indices 0..2), persisted as `Difficulty Level`
in `swkotor.ini`; the GUI writes it in `CSWGuiInGameGameplay::OnDifficultyLeft`
(`docs/research/min-one-hitpoint.md` §3). The retail difficulty table uses it to select the
party/PC damage multiplier. Enemy damage skips that multiplier, so the mirrored-enemy damage
path remains symmetric (`docs/research/peer-damage-ledger.md` §1.2–§2).

This is separate from `CServerInfo + 0x108`, the server difficulty column used by
`CServerExoApp::GetDifficultyOption`. Retail initializes that index to 2 (normal); it is not
the local slider and must not stand in for it. In particular, the `MinHP1` probe is a separate
server-table check and does not establish GUI-setting agreement.

## Sampling and agreement

The native plugin calls the verified `CClientExoApp::GetClientOptions` entry at `0x005ED700`
from the main-loop hook and reads `+4`. It accepts only a plausible client/options pointer and
a byte in 0..2. It writes the value and then marks it known in the plugin-agent SharedBlock
tail at `0x4E40..0x4E42`; the agent reads the known byte on both sides of the value to avoid a
torn sample. If any guard fails, the setting remains unknown. No INI parsing or memory writes
are used. The plugin's fixed native addresses target the supported retail executable; the
sample itself is additionally guarded by client/options pointer checks and the valid value
range, matching the codebase's existing native-read practice.

`WorldDigest.DifficultyLevel` is nullable so the valid Easy value 0 cannot be confused with an
unavailable read. Protocol version 62 appends it to the world-digest packet; protocol 61 peers
are rejected by the existing version handshake instead of misreading the added byte. Both
peers compare known values independent of module. A difference produces `W_DIFFICULTY`, says
that PC/party damage scaling may differ, and drops no capabilities. Unknown on either side is
not a mismatch. If settings agree and later diverge again, the warning can be emitted again.

## Verification status

Static native evidence is documented in `min-one-hitpoint.md` and the peer-damage ledger. The
runtime path is covered by protocol round-trip, unknown-value, mismatch, warning-only, and
re-announcement tests. No live game sampling or setting changes were performed for this change.
