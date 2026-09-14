# Performance and memory-safety review — 2026-09-15

This pass focuses on the shared memory-access layer, background signature scans,
journal mutations, DirectInput wrapping, and the final-node party-follow repair paths.
It is a source review with synthetic measurements and Windows memory tests, not a
live gameplay profile or a guarantee that all native crashes are prevented.

## Changes

| Finding | Improvement |
| --- | --- |
| Signature scans allocated a buffer of up to roughly 1 MiB for every scanned region. | Rent buffers from `ArrayPool<byte>` and return them in `finally`, including cancellation and read failures. Buffers remain local to each call, preserving concurrent scanner use. |
| Pattern search examined every byte in a scalar loop. | Search the fixed literal prefix with `Span.IndexOf`, then verify wildcard suffixes. Preserve overlapping matches and matches across scan chunks. |
| Cancellation was checked only between regions, allowing a large region to delay cancellation. | Also check cancellation between chunks. Reject empty literal patterns and handle overflowing match indices without indexing outside the span. |
| Generic memory operations used `Marshal.SizeOf<T>()`, which describes marshalled layout rather than the actual unmanaged value representation. | Read/write a byte span over the value itself. A `bool` now touches one byte, and a `char` touches two; neither overwrites adjacent fields or fails because a neighbouring page is inaccessible. Failed reads still return a default output value. |
| Fixed-width string operations allocated temporary byte arrays for ordinary short fields. | Use bounded stack buffers, explicitly clearing write buffers to retain zero padding and full-width ResRef support. Larger writes retain a heap-buffer fallback. |
| Failure to detect target bitness silently selected 64-bit pointers; attach failures could leak the process handle. | Refuse attachment if bitness detection fails, and close the acquired handle on initialization failure. |
| Plugin readability checks validated only the starting address. | Validate complete ranges across region boundaries, distinguish writable memory, and reject guarded, uncommitted, inaccessible and overflowing ranges. Default field checks cover four bytes. |
| Journal guards trusted the first part of the array and could call native code before checking later tags. | Share one validator for add/update/remove, checking the header, backing capacity, every active tag's bounded range, and its null terminator before mutation. This also protects the second traversal in remove-then-add. |
| DirectInput wrapping read whole GUIDs/vtables after checking only their starts. | Check the full GUID/vtable ranges and the output slots before replacing interface pointers. |
| Final-node repairs wrote vectors and records after checking individual readable addresses. | Require writable record/vector ranges and reject invalid trail cursors before calculating a node address. |

## Measurements

Run `dotnet run --project tools/MemoryBench -c Release` to reproduce the included
microbenchmark. The baseline preserves the previous scalar search and array-backed
ASCII reader. Search results are checked for equality before timing. Values below
are medians of five warmed runs on this machine, .NET 10.0.12 x64, for a four-byte
literal signature in a 32 MiB buffer.

| Buffer | Previous search | Revised search | Ratio |
| --- | ---: | ---: | ---: |
| Zero-filled | 6.84 ms | 1.21 ms | 5.66× |
| Seeded random bytes | 7.74 ms | 1.22 ms | 6.34× |
| Repeated first-byte near misses | 34.44 ms | 1.21 ms | 28.55× |

A 16-byte ASCII read containing `end_m01aa` allocated **80 → 40 bytes per call**;
the remaining allocation is the returned string. These figures isolate local search
and allocation costs. They do not include an entire game-memory sweep, network latency,
controller scheduling, or game frame times. Wildcard patterns and other CPUs may differ.

The initial single-byte vector search regressed on dense near misses. The final
implementation searches the literal prefix together, resolving that measured regression.

## Validation and remaining limits

The managed solution and the NativeAOT win-x86 plugin built successfully with warnings
treated as errors. The full suite passed **3,500 tests** in 9 minutes 12 seconds,
excluding `HeavyPacketLossStillConverges` as in CI. The final focused validation after
the literal-prefix refinement and journal-validator extraction covered 103 memory, scanner, journal,
and external-target tests, all passing. An isolated output directory needed a copy of
`profiles/mockgame.json` for the existing profile-location test; its rerun passed.

Regression tests use dedicated unmanaged allocations and real Win32 memory APIs.
They cover exact-width writes, neighbouring-byte preservation, zero padding,
chunk-boundary matches, scan limits/cancellation, read-only and guard pages,
cross-page ranges, missing string terminators, and malformed later journal entries.
Full address-space scans remain in the existing separate-process tests; tests do not
scan the test host's GC heap.

Page permissions are intentionally not cached: a transition can invalidate them.
Readability cannot establish object identity or keep an allocation alive after a check.
The existing game-thread and object-liveness requirements therefore still apply.
Raw engine calls outside the reviewed paths remain candidates for further audits.
The raw external process-handle lifetime, especially disposal while a background scan
is running, also merits a separate concurrency review.

Before treating this as gameplay-verified, exercise quest add/advance/remove,
keyboard/gamepad input, companion following, and normal door transitions in the live
two-instance rig. Compare existing `perf_trace` scan and frame-time telemetry under the
same module, party, and inventory load. The plugin was built locally; this pass does
not install it into game directories or change feature defaults.
