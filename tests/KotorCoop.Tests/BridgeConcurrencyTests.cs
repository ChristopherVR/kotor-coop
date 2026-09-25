using System.Collections.Concurrent;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The live bug (2026-09-25, docs/research/disconnect-reconnect.md's bridge-concurrency
/// section): a joiner agent's pump began failing EVERY tick with
/// <c>InvalidOperationException</c> ("A concurrent update was performed on this collection
/// and corrupted its state") after a module transition, because <see cref="KotorGameBridge"/>
/// is shared by the pump, the command dispatcher's own poll loop, the join flow, the server-
/// discovery beacon and <c>SessionManager</c>'s rejoin task — none of which coordinated with
/// each other — and several of its caches
/// (<c>_creatureCache</c>/<c>_liveCache</c>/<c>_scanCache</c>, <c>_itemTags</c>/
/// <c>_itemAddresses</c>, the door/placeable/container "shadow" dictionaries, ...) are plain
/// Dictionary/List fields, not concurrent collections.
///
/// <see cref="KotorGameBridge.Sync"/> (see its remarks, and <see cref="IGameBridge.Sync"/>'s)
/// is the fix: every method that touches one of those caches takes it internally, so every
/// caller — including a test that does not know which methods are the hot ones — is
/// automatically serialized. These tests hammer the bridge from many threads at once and
/// assert two things a corrupted collection would violate: no exception escapes, and
/// <see cref="KotorGameBridge.Sync"/> is a single stable per-instance object that actually
/// provides mutual exclusion (not, say, a fresh object handed out on every access, which
/// would make every <c>lock (bridge.Sync)</c> in the codebase a no-op).
///
/// Attaches to the TEST PROCESS ITSELF, the same trick
/// <see cref="PartyPoolAmbientSuppressionTests"/> already uses: nothing here needs a real
/// KOTOR process, only a live, readable <see cref="ProcessMemory"/> handle for
/// <see cref="KotorGameBridge"/>'s scans to run against (finding nothing KOTOR-shaped in THIS
/// process is fine and expected — the race is in the bookkeeping around the scan, not in what
/// the scan finds).
/// </summary>
public class BridgeConcurrencyTests : IDisposable
{
    private readonly ProcessMemory _memory = ProcessMemory.Attach(Environment.ProcessId);
    private readonly KotorGameBridge _bridge;

    public BridgeConcurrencyTests()
    {
        _bridge = new KotorGameBridge(_memory, ownsMemory: false);
    }

    public void Dispose()
    {
        _bridge.Dispose();
        _memory.Dispose();
    }

    /// <summary>
    /// Proves <see cref="KotorGameBridge.Sync"/> is a stable identity that gives real mutual
    /// exclusion: many threads race to enter a <c>lock (_bridge.Sync)</c> block and increment/
    /// decrement a counter inside it — if <c>Sync</c> ever returned a fresh object (or a
    /// different object per call), more than one thread would observe the counter above 1.
    /// </summary>
    [Fact]
    public void SyncIsAStableLockThatActuallySerializesEntry()
    {
        const int threads = 16;
        const int iterationsPerThread = 500;

        int inside = 0;
        int maxObservedInside = 0;
        object maxGate = new();
        ConcurrentBag<Exception> failures = [];

        Parallel.For(0, threads, _ =>
        {
            try
            {
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    lock (_bridge.Sync)
                    {
                        int now = Interlocked.Increment(ref inside);
                        lock (maxGate)
                        {
                            if (now > maxObservedInside)
                            {
                                maxObservedInside = now;
                            }
                        }

                        Interlocked.Decrement(ref inside);
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        });

        Assert.Empty(failures);
        Assert.Equal(1, maxObservedInside);
    }

    /// <summary>
    /// The actual regression test: hammers the exact entry points the live bug's log named
    /// (<c>TryResolve</c> -&gt; <c>EnumerateCreatures</c>, logging "adopted the engine's own
    /// creature") plus every other method this pass found sharing the same class of hazard
    /// (<see cref="KotorGameBridge.ReadPartyPortraits"/>, <c>TryReadLocalState</c>,
    /// <c>ReadEnemies</c>, <c>ClearCoopState</c>, <c>ForgetWorldShadows</c>,
    /// <c>ResetCaches</c>) from many threads at once, simultaneously. Before the fix this
    /// reproduced <c>InvalidOperationException</c> from the shared Dictionary/List caches
    /// within a handful of iterations; after it, none of these may throw anything.
    /// </summary>
    [Fact]
    public void HammeringEveryHotspotEntryPointFromManyThreadsNeverThrows()
    {
        const int threads = 12;
        const int iterationsPerThread = 30;

        ConcurrentBag<Exception> failures = [];

        Parallel.For(0, threads, threadIndex =>
        {
            try
            {
                for (int i = 0; i < iterationsPerThread; i++)
                {
                    // Every thread runs a different mix so the interleaving is not lock-step —
                    // lock-step callers can accidentally never actually race.
                    switch ((threadIndex + i) % 6)
                    {
                        case 0:
                            _bridge.TryResolve(out _);
                            break;
                        case 1:
                            _bridge.EnumerateCreatures();
                            break;
                        case 2:
                            _bridge.TryReadLocalState(out _);
                            break;
                        case 3:
                            _bridge.ReadPartyPortraits();
                            break;
                        case 4:
                            _bridge.ReadEnemies([]);
                            break;
                        default:
                            _bridge.ResetCaches();
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        });

        Assert.Empty(failures);
    }

    /// <summary>
    /// <c>ClearCoopState</c>/<c>ForgetWorldShadows</c> CLEAR several of the same caches the
    /// hotspot readers above populate — the disconnect/rejoin shape, not just the resolve
    /// shape. Run concurrently with readers on other threads to exercise a Clear() racing a
    /// TryGetValue()/indexer write, the other classic Dictionary corruption pattern (distinct
    /// from two writers racing each other).
    /// </summary>
    [Fact]
    public void ClearingCoopStateWhileOtherThreadsAreReadingNeverThrows()
    {
        const int readerThreads = 8;
        const int clearerThreads = 4;
        const int iterationsPerThread = 30;

        ConcurrentBag<Exception> failures = [];

        Parallel.Invoke(
            () => Parallel.For(0, readerThreads, readerIndex =>
            {
                _ = readerIndex;
                try
                {
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        _bridge.EnumerateCreatures();
                        _bridge.ReadPartyPortraits();
                        _bridge.TryReadLocalState(out PlayerState state);
                        _ = state;
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }),
            () => Parallel.For(0, clearerThreads, clearerIndex =>
            {
                _ = clearerIndex;
                try
                {
                    for (int i = 0; i < iterationsPerThread; i++)
                    {
                        _bridge.ClearCoopState();
                        _bridge.ForgetWorldShadows();
                    }
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }));

        Assert.Empty(failures);
    }
}
