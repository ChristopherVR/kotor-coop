namespace KotorCoop.Plugin;

/// <summary>
/// A small typed event hub for cross-controller notifications, so PluginHost.OnFrame's
/// fixed schedule can stay the single source of truth for ORDER while a controller that only
/// cares about an edge (a world load, a dialogue starting, a peer slot spawning...) can
/// subscribe instead of re-deriving the same edge itself.
///
/// Every event here is raised SYNCHRONOUSLY, on the game thread, from the exact point the
/// underlying condition is already detected today — this hub adds a notification, it does not
/// add a new detection. See PluginHost.Frame.cs, PluginHost.SessionLifecycle.cs,
/// EnemyPresenceController.cs and PhantomController.Lifecycle.cs for the raise sites.
///
/// Dependency-free by design (no Odyssey/SharedBlockView reference): this file is compiled
/// directly into the test project, the same way the plugin's other pure-logic files are (see
/// tests/KotorCoop.Tests/KotorCoop.Tests.csproj). <see cref="LogFailure"/> is how a raise
/// reports a subscriber's exception without calling PluginHost.Log directly.
/// </summary>
internal static class PluginEvents
{
    /// <summary>
    /// Set once by PluginHost at startup so a swallowed subscriber exception still reaches the
    /// log. Left null in a test host (or before startup runs): a failure is then dropped
    /// silently rather than thrown, which is still correct — see <see cref="Raise"/>.
    /// </summary>
    public static Action<string>? LogFailure;

    /// <summary>The world-load edge PluginHost.OnFrame's epoch gate already computes
    /// (WorldLoadRule.IsUnannouncedTeardown) — an unannounced teardown, judged to be a Load
    /// Game rather than a real transition.</summary>
    public static event Action? WorldLoadObserved;

    /// <summary>The first frame PluginHost.OnFrame notices a real door/trigger area transition
    /// pending (Odyssey.IsAreaTransitionPending() just went true).</summary>
    public static event Action? ModuleTransitionStarted;

    /// <summary>The frame PluginHost.OnFrame closes the client's world because the server
    /// module is already gone (Odyssey.CloseClientWorldIfServerModuleGone()).</summary>
    public static event Action? ServerModuleGone;

    /// <summary>DialogueSpectateController's debounced local-conversation flag just went
    /// false-&gt;true.</summary>
    public static event Action? DialogueStarted;

    /// <summary>DialogueSpectateController's debounced local-conversation flag just went
    /// true-&gt;false (past the entry-gap linger window, a real end).</summary>
    public static event Action? DialogueEnded;

    /// <summary>EnemyPresenceController's "one creature pulled out of the census to fight the
    /// local player for real" state just flipped. <c>engaged</c> is the new state;
    /// <c>ownerObjectId</c> is the owner-space id of the creature that started or ended the
    /// fight.</summary>
    public static event Action<bool, uint>? LocalCombatEngagementChanged;

    /// <summary>PhantomController spawned a peer's body into the given slot.</summary>
    public static event Action<int, uint>? PeerSlotSpawned;

    /// <summary>PhantomController's slot no longer holds a body — either the destroy
    /// succeeded, or (see PhantomController.Lifecycle.cs's own remarks) it was written off
    /// after MaxDestroyAttempts refusals and left standing. Either way the SLOT is free.</summary>
    public static event Action<int, uint>? PeerSlotDestroyed;

    /// <summary>PluginHost.PumpSessionLifecycle saw <c>block.Command</c> rise to
    /// CommandSpawnAndDrive: a co-op session is now driving this instance.</summary>
    public static event Action? SessionConnected;

    /// <summary>PluginHost.PumpSessionLifecycle saw <c>block.Command</c> fall away: the session
    /// is gone and every controller that held engine/GUI state is releasing it.</summary>
    public static event Action? SessionReleased;

    public static void RaiseWorldLoadObserved() =>
        Raise(WorldLoadObserved, nameof(WorldLoadObserved));

    public static void RaiseModuleTransitionStarted() =>
        Raise(ModuleTransitionStarted, nameof(ModuleTransitionStarted));

    public static void RaiseServerModuleGone() =>
        Raise(ServerModuleGone, nameof(ServerModuleGone));

    public static void RaiseDialogueStarted() =>
        Raise(DialogueStarted, nameof(DialogueStarted));

    public static void RaiseDialogueEnded() =>
        Raise(DialogueEnded, nameof(DialogueEnded));

    public static void RaiseLocalCombatEngagementChanged(bool engaged, uint ownerObjectId) =>
        Raise(LocalCombatEngagementChanged, nameof(LocalCombatEngagementChanged), engaged, ownerObjectId);

    public static void RaisePeerSlotSpawned(int slot, uint objectId) =>
        Raise(PeerSlotSpawned, nameof(PeerSlotSpawned), slot, objectId);

    public static void RaisePeerSlotDestroyed(int slot, uint objectId) =>
        Raise(PeerSlotDestroyed, nameof(PeerSlotDestroyed), slot, objectId);

    public static void RaiseSessionConnected() =>
        Raise(SessionConnected, nameof(SessionConnected));

    public static void RaiseSessionReleased() =>
        Raise(SessionReleased, nameof(SessionReleased));

    /// <summary>
    /// TEST-ONLY: clears every subscription and the log sink. Real code never needs this —
    /// subscriptions live for the process's lifetime — but a shared static hub used from many
    /// independent unit tests needs a way to guarantee one test's handler cannot fire (and
    /// possibly throw against already-disposed test state) during a later, unrelated test.
    /// An event can only be assigned to from inside its declaring type, which is why this
    /// lives here rather than in the test project.
    /// </summary>
    internal static void ResetForTests()
    {
        WorldLoadObserved = null;
        ModuleTransitionStarted = null;
        ServerModuleGone = null;
        DialogueStarted = null;
        DialogueEnded = null;
        LocalCombatEngagementChanged = null;
        PeerSlotSpawned = null;
        PeerSlotDestroyed = null;
        SessionConnected = null;
        SessionReleased = null;
        LogFailure = null;
    }

    // ---- guarded dispatch ---------------------------------------------------------------
    //
    // A subscriber that throws must never escape into the [UnmanagedCallersOnly] frame hook
    // that (transitively) raised this — that fail-fasts the whole game (see the plugin's own
    // crash-probe doc comments). Delegate.GetInvocationList runs each handler in isolation, so
    // one throwing subscriber does not stop the rest from being called. Plain concrete
    // Action/Action&lt;T,T&gt; delegate types throughout — no reflection, no generics-over-
    // unknown-types, so nothing here gives NativeAOT's trimmer anything to remove.

    private static void Raise(Action? handlers, string eventName)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate d in handlers.GetInvocationList())
        {
            try
            {
                ((Action)d)();
            }
            catch (Exception ex)
            {
                LogFailure?.Invoke($"PluginEvents.{eventName} subscriber threw: {ex}");
            }
        }
    }

    private static void Raise<T1, T2>(Action<T1, T2>? handlers, string eventName, T1 arg1, T2 arg2)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate d in handlers.GetInvocationList())
        {
            try
            {
                ((Action<T1, T2>)d)(arg1, arg2);
            }
            catch (Exception ex)
            {
                LogFailure?.Invoke($"PluginEvents.{eventName} subscriber threw: {ex}");
            }
        }
    }
}
