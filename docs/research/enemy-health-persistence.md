# Enemy health baseline persistence

## Current boundary

Enemy health scaling is disabled by default (100%). With an explicit non-100 setting, the
native `EnemyScalingController` scales each eligible hostile once per world epoch and its
`ScaledObjectLatch` retains `(objectId, original maximum, applied percent)` for that epoch.
The latch has a 256-entry capacity and is cleared on world invalidation. It has no export or
import consumer yet.

`EnemyScalingBaselineStore` already writes a bounded sidecar atomically against the exact
`SAVEGAME.sav` SHA-256 and a selected save key, then verifies the archive did not change during
the write. Selection requires the same save key/hash, module and object ID, tag/template, and a
stored maximum equal to the recorded baseline scaled by the recorded percentage. The agent's
load-provenance mailbox captures the requested slot/name or exact direct-directory name and the
request-time `WorldLoadSerial`; a later serial edge can distinguish successful world replacement
from a request alone. These pieces are currently separate: no code connects latch data to the
sidecar or binds an imported record set to the native controller.

This task adds only an in-memory collection foundation in
`EnemyScalingBaselineCollector`. Native-export records deliberately contain only object ID,
original maximum, and applied percentage. Module, tag, and template must be joined from the exact
saved archive; they are not inferred from a live object's fields. Exports are page-bounded and
qualified by exact load session, module generation, and export generation. A complete newer
module snapshot replaces that module's previous snapshot while other visited-module snapshots
remain. Incomplete or conflicting current exports prevent a frozen save snapshot until a newer
complete export arrives. Capacity overflow and a zero/unknown session fail closed. A complete
zero-record export is distinct from no export and can be frozen.

This is not connected to save capture, load restoration, the native block, or `EnemyScalingController`.
It does not make reloaded scaling idempotent and is not a claim that the feature is complete.

## Smallest safe production wiring still required

1. **Export and accumulate.** Add a versioned bounded shared-block page for the native latch.
   The agent must call `ExpectModule` at each observed module generation, consume every page,
   and retain completed snapshots across visited modules. A latch overflow, missing page, or
   generation conflict must prevent saving the collection as complete. The current block has
   only 0x58 unused bytes after the v74 load-provenance mailbox, so a record page needs a planned
   block growth/version change or another explicitly owned transport region.
2. **Capture after save completion.** Observe the native SaveGame request's exact slot/name,
   wait until that archive is demonstrably settled, and freeze the collector only after every
   expected module has a complete snapshot. `SaveIdentityDigest` is not a save-completion signal
   for HP changes. Parse the exact archive to join raw latch records with saved module/object
   identity, tag, template, and stored maximum; reject missing/ambiguous joins. Then write with
   `EnemyScalingBaselineStore.TryWriteForArchive`.
3. **Bind a successful load.** Correlate captured load provenance with a later load-serial edge,
   establish the exact active archive path/key, and require the archive hash and live save identity
   to match before import. A request without a successful transition is not enough. Transfer the
   selected module's records completely to the plugin before enabling health writes.
4. **Fail closed and preserve health.** For a loaded epoch, missing, stale, mismatched, or partial
   sidecar state must leave enemy health at 100% for that epoch. For a matched record, verify the
   current stored maximum equals `ScaleMaximum(baseMaximum, recordedPercent)`. If the configured
   percentage changed, compute the new maximum from the original maximum and preserve the current
   health ratio against the old stored maximum; never multiply the already-scaled maximum. Fresh
   world startup needs a separately proven classification before it can bypass this loaded-save
   gate.

Tests for the collection foundation cover exact-session reset, out-of-order pages, current versus
stale generations, complete empty snapshots, per-module replacement, preservation of other
modules, malformed/conflicting exports, capacity overflow, and immutable frozen copies. Separate
integration tests remain necessary for archive joins, stable-save timing, exact load binding, and
the native readiness gate.
