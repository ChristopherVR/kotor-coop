using KotorCoop.Agent;
using Terminal.Gui.App;
using Terminal.Gui.Views;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Regression coverage for the 2026-09-26 join-flow hang: after the Terminal.Gui rework
/// (ed537f9), a live join stalled forever right after "pre-disconnect quicksave did not land
/// within the wait ..." — the tail of <c>CoopCommandDispatcher.CaptureCharacterOnDisconnect</c>,
/// called from <c>SessionManager.Disconnect</c> while it holds <c>SessionManager</c>'s own
/// <c>_gate</c> lock for the whole call (by design — see that lock's own doc comment: "The last
/// chance to read anything off Current/Agent while it is still alive").
///
/// <b>THE CONFIRMED MECHANISM</b> (found by running the FIRST test below against the real
/// library, then tracing exactly why it failed): Terminal.Gui's own
/// <c>TimedEvents.RunTimers()</c> — the method the main loop actually calls once per
/// iteration — wraps the WHOLE due-callback execution in <c>lock (_timeoutsLockToken)</c>:
/// <code>
/// public void RunTimers()
/// {
///     lock (_timeoutsLockToken)
///     {
///         if (_timeouts.Count > 0) { RunTimersImpl(); }
///     }
/// }
/// </code>
/// <c>RunTimersImpl</c>'s OWN inner loop does briefly release the lock around each callback
/// (its comment even says "Execute the callback outside the lock"), but that inner release is
/// irrelevant once the OUTER <c>RunTimers()</c> call already holds the SAME lock for its
/// entire duration — a `lock` is reentrant only for the SAME thread, so from every OTHER
/// thread's point of view <c>_timeoutsLockToken</c> is held for as long as the due callback
/// takes. <c>IApplication.Invoke</c>, called from a different thread, needs that exact lock to
/// enqueue (<c>TimedEvents.Add</c> → <c>AddTimeout</c> → <c>lock (_timeoutsLockToken)</c>).
///
/// Combine that with what <see cref="AgentDashboard"/>'s OLD periodic header refresh did:
/// its <c>Invoke</c>-dispatched callback read <c>manager.Agent</c>, which takes
/// <c>SessionManager</c>'s own <c>_gate</c> — a lock the join-flow thread legitimately holds
/// for several seconds at a time inside <c>SessionManager.Disconnect</c>. The result is a
/// genuine, permanent AB-BA deadlock:
/// <list type="bullet">
/// <item>the join-flow thread HOLDS <c>_gate</c> (via <c>Disconnect</c>) and WANTS
/// <c>_timeoutsLockToken</c> (via its own <c>Note()</c> call, reporting progress through
/// <c>Invoke</c>);</item>
/// <item>the Terminal.Gui main-loop thread HOLDS <c>_timeoutsLockToken</c> (via
/// <c>RunTimers()</c>, running the header-refresh callback) and WANTS <c>_gate</c> (via
/// <c>manager.Agent</c> inside that same callback).</item>
/// </list>
/// Neither thread can ever proceed — exactly matching the live symptom: nothing logged past
/// "pre-disconnect quicksave did not land ..." (that line's FILE write already happened,
/// before the fatal <c>Invoke</c> call; see <see cref="AgentDashboard.Note"/>'s own ordering).
///
/// <b>THE FIX</b> (<c>AgentDashboard.RunTuiOnDedicatedThread</c>/<c>TickOnTimerThread</c> in
/// src/KotorCoop.Agent): never let an <c>Invoke</c>-dispatched callback touch anything that
/// can block on a contended lock. Gather data (e.g. <c>manager.Agent</c>) on the CALLING
/// (non-UI) thread first; the <c>Invoke</c> callback only ever assigns already-computed text,
/// so it can never be the slow callback that jams <c>_timeoutsLockToken</c> for anyone else.
/// Also runs the whole Terminal.Gui lifecycle on a dedicated thread rather than an ordinary
/// <c>Task.Run</c> (so <c>Init()</c> and <c>Run()</c> share a thread, and the pump never
/// permanently occupies a shared ThreadPool worker).
///
/// These tests exercise the underlying mechanism with a REAL <see cref="IApplication"/>
/// (proving the library's actual behaviour, not a hand-written stand-in for it) rather than
/// AgentDashboard's own private internals, so they hold regardless of how that class's fields
/// are named. Each uses a bounded wait and fails FAST (a few seconds) rather than hanging the
/// whole suite if the regression comes back.
/// </summary>
public class AgentDashboardThreadingTests
{
    [Fact]
    public async Task ABlockingInvokeDispatchedCallbackAlsoBlocksEveryOtherThreadsInvokeCall()
    {
        // Documents the hazard itself (see this class's own doc comment for the full
        // mechanism): while ONE Invoke-dispatched callback is still running on the main loop
        // thread, a SECOND thread's own Invoke call is genuinely blocked, not merely queued —
        // this is what makes "never let a callback block" the only correct fix, rather than
        // "call Invoke differently".
        using ManualResetEventSlim busyCallbackStarted = new(false);
        using ManualResetEventSlim releaseBusyCallback = new(false);

        await RunOnDedicatedTerminalGuiThreadAsync(async app =>
        {
            // Stands in for the OLD buggy shape: an Invoke-dispatched callback that blocks
            // the UI thread on a lock a background thread is holding (manager.Agent's
            // SessionManager._gate, in production).
            app.Invoke(() =>
            {
                busyCallbackStarted.Set();
                releaseBusyCallback.Wait(TimeSpan.FromSeconds(10));
            });

            Assert.True(
                busyCallbackStarted.Wait(TimeSpan.FromSeconds(5)),
                "the busy callback never started - the main loop is not draining queued Invoke calls at all");

            Task otherInvoke = Task.Run(() => app.Invoke(() => { }));

            // The second call must still be pending shortly after being issued - proves it is
            // genuinely BLOCKED on the first callback, not just slow to get scheduled.
            await Task.Delay(TimeSpan.FromMilliseconds(300)).ConfigureAwait(false);
            Assert.False(
                otherInvoke.IsCompleted,
                "a second thread's Invoke() completed WHILE the first callback was still blocking the "
                + "main loop - the coupling this test documents (and AgentDashboard's own fix defends "
                + "against) no longer holds in this version of Terminal.Gui. Safe to relax the fix, but "
                + "re-verify with a live join first, and update this class's own doc comment.");

            releaseBusyCallback.Set();

            Assert.True(
                otherInvoke.Wait(TimeSpan.FromSeconds(10)),
                "the second Invoke() call never completed even after releasing the first callback");
        });
    }

    [Fact]
    public async Task NoteEquivalentCallsFromWorkerThreadsCompletePromptlyWhileAnUnrelatedLockIsHeld()
    {
        // The positive proof of the actual fix: several worker threads calling something
        // shaped exactly like AgentDashboard.Note (append, then marshal a UI update via
        // Invoke) stay fast even while ANOTHER thread holds an unrelated lock for a while -
        // as long as nothing INSIDE an Invoke-dispatched callback ever needs that lock (the
        // one thing the previous test proves is fatal), the deadlock this whole file documents
        // cannot occur. Mirrors CaptureCharacterOnDisconnect (holds SessionManager._gate)
        // running concurrently with Note() calls from the join-flow thread itself and from
        // ConsoleCaptureWriter's own captured-output callback on other threads.
        object sessionManagerGateStandIn = new();
        List<string> transcript = [];
        object transcriptGate = new();

        await RunOnDedicatedTerminalGuiThreadAsync(async app =>
        {
            void NoteEquivalent(string line)
            {
                lock (transcriptGate)
                {
                    transcript.Add(line);
                }

                // The fire-and-forget marshal AgentDashboard.Note performs via app.Invoke -
                // note this callback (unlike the previous test's) touches NOTHING that could
                // ever be contended, which is exactly the discipline the fix requires.
                app.Invoke(() => { });
            }

            ConsoleCaptureWriter writer = new(NoteEquivalent);

            // Holds the SessionManager._gate stand-in for a bit, exactly like
            // SessionManager.Disconnect holding it across CaptureCharacterOnDisconnect's own
            // blocking save-landed poll (CharacterCaptureCadenceRule.SaveWaitMs = 5s in
            // production; shortened here so the test stays fast). Deliberately never touched
            // by NoteEquivalent/the Invoke callback above - that is the whole point.
            Task lockHolder = Task.Run(() =>
            {
                lock (sessionManagerGateStandIn)
                {
                    Thread.Sleep(1500);
                }
            });

            await Task.Delay(200).ConfigureAwait(false); // let lockHolder actually take the lock first

            // Several worker threads writing concurrently - the pump thread, the dispatcher
            // tick thread, and a captured stray Console.WriteLine could all be doing this at
            // once in production.
            Task[] writers = [.. Enumerable.Range(0, 8).Select(i => Task.Run(() => writer.WriteLine($"line {i}")))];

            bool allCompleted = Task.WaitAll(writers, TimeSpan.FromSeconds(5));
            await lockHolder.ConfigureAwait(false);

            Assert.True(
                allCompleted,
                "ConsoleCaptureWriter.WriteLine (Note's own capture path) blocked while an unrelated "
                + "lock was held elsewhere and the UI loop was up - this is the 2026-09-26 regression shape");

            lock (transcriptGate)
            {
                Assert.Equal(8, transcript.Count);
            }
        });
    }

    /// <summary>
    /// Runs <paramref name="body"/> against a REAL, actively-pumping <see cref="IApplication"/>
    /// on a dedicated thread — <c>Init()</c> and <c>Run()</c> share that one thread, the same
    /// shape <c>AgentDashboard.RunTuiOnDedicatedThread</c> uses (and needs: see its own doc
    /// comment on why <c>IApplication.MainThreadId</c>, stamped once in <c>Init()</c> and never
    /// reassigned by <c>Run()</c>, requires this) — then requests it to stop and joins the
    /// thread. <paramref name="body"/> runs on the ORIGINAL calling thread (this method's own
    /// caller), concurrently with the dedicated thread's <c>app.Run(window)</c> call, so
    /// <c>app.Invoke</c> calls made from inside it are exercising a genuinely live main loop.
    /// </summary>
    private static async Task RunOnDedicatedTerminalGuiThreadAsync(Func<IApplication, Task> body)
    {
        TaskCompletionSource<IApplication> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Thread uiThread = new(() =>
        {
            IApplication app = Application.Create().Init();
            Window window = new();
            ready.SetResult(app);

            try
            {
                app.Run(window);
            }
            finally
            {
                app.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "AgentDashboardThreadingTests-UI",
        };
        uiThread.Start();

        IApplication app = await ready.Task.ConfigureAwait(false);

        // Let Run() actually start pumping before the test body issues its first Invoke.
        await Task.Delay(TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);

        try
        {
            await body(app).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                app.Invoke(() => app.RequestStop());
            }
            catch
            {
                // Best-effort: the body may already have stopped it, or it may be wedged -
                // either way, join with a bound below rather than hanging the test forever.
            }

            Assert.True(uiThread.Join(TimeSpan.FromSeconds(10)), "the dedicated UI thread never exited after RequestStop");
        }
    }
}
