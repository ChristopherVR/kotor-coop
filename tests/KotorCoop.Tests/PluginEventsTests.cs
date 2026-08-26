using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// PluginEvents is the typed notification hub PluginHost and a handful of controllers raise
/// on the game thread. The one property worth a test is the guard-wrapping: a subscriber that
/// throws must not stop the rest of the invocation list, and must not let the exception
/// escape the raise — an escaping managed exception inside an [UnmanagedCallersOnly] frame
/// fail-fasts the whole game (see PluginEvents' own class comment). Everything else about the
/// hub is "call the delegate", which the compiler already checks.
///
/// PluginEvents is dependency-free (no Odyssey/SharedBlockView reference), so it compiles
/// straight into this test project like the plugin's other pure-logic files — see
/// KotorCoop.Tests.csproj's own remarks on that ItemGroup.
/// </summary>
public class PluginEventsTests
{
    /// <summary>
    /// Every test clears every subscription and the log sink before and after it runs: the
    /// events are static, so a handler left behind by one test would otherwise fire — and
    /// possibly throw against disposed test state — on a completely unrelated test.
    /// </summary>
    public PluginEventsTests()
    {
        PluginEvents.ResetForTests();
    }

    [Fact]
    public void RaisingWithNoSubscribersDoesNothing()
    {
        // Must not throw for lack of anyone listening - every raise site calls this
        // unconditionally, whether or not anything has subscribed yet.
        Exception? escaped = Record.Exception(PluginEvents.RaiseWorldLoadObserved);

        Assert.Null(escaped);
    }

    [Fact]
    public void SubscriberIsInvokedOnRaise()
    {
        int calls = 0;
        PluginEvents.WorldLoadObserved += () => calls++;

        PluginEvents.RaiseWorldLoadObserved();

        Assert.Equal(1, calls);
    }

    [Fact]
    public void ThrowingSubscriberDoesNotStopLaterSubscribersOrEscape()
    {
        bool secondRan = false;
        List<string> failures = [];
        PluginEvents.LogFailure = failures.Add;

        PluginEvents.ModuleTransitionStarted += () => throw new InvalidOperationException("boom");
        PluginEvents.ModuleTransitionStarted += () => secondRan = true;

        Exception? escaped = Record.Exception(PluginEvents.RaiseModuleTransitionStarted);

        Assert.Null(escaped);
        Assert.True(secondRan);
        Assert.Single(failures);
        Assert.Contains("ModuleTransitionStarted", failures[0]);
        Assert.Contains("boom", failures[0]);
    }

    [Fact]
    public void MissingLogFailureSinkDropsTheFailureRatherThanThrowing()
    {
        // PluginEvents.LogFailure is null until PluginHost.EnsureStarted wires it up (and
        // stays null in a bare test host like this one) - a subscriber throwing before that
        // point must still not escape.
        PluginEvents.LogFailure = null;
        PluginEvents.DialogueStarted += () => throw new InvalidOperationException("boom");

        Exception? escaped = Record.Exception(PluginEvents.RaiseDialogueStarted);

        Assert.Null(escaped);
    }

    [Fact]
    public void TwoArgEventPassesBothArgumentsThroughUnchanged()
    {
        (bool engaged, uint ownerId) seen = default;
        PluginEvents.LocalCombatEngagementChanged += (engaged, ownerId) => seen = (engaged, ownerId);

        PluginEvents.RaiseLocalCombatEngagementChanged(true, 0x1234);

        Assert.True(seen.engaged);
        Assert.Equal(0x1234u, seen.ownerId);
    }

    [Fact]
    public void PeerSlotEventsCarrySlotAndObjectId()
    {
        (int slot, uint id) spawned = default;
        (int slot, uint id) destroyed = default;
        PluginEvents.PeerSlotSpawned += (slot, id) => spawned = (slot, id);
        PluginEvents.PeerSlotDestroyed += (slot, id) => destroyed = (slot, id);

        PluginEvents.RaisePeerSlotSpawned(2, 0xABCD);
        PluginEvents.RaisePeerSlotDestroyed(2, 0xABCD);

        Assert.Equal((2, 0xABCDu), spawned);
        Assert.Equal((2, 0xABCDu), destroyed);
    }

    [Fact]
    public void EveryZeroArgEventCanBeRaisedWithoutThrowing()
    {
        int calls = 0;
        void OnEvent() => calls++;

        PluginEvents.WorldLoadObserved += OnEvent;
        PluginEvents.ModuleTransitionStarted += OnEvent;
        PluginEvents.ServerModuleGone += OnEvent;
        PluginEvents.DialogueStarted += OnEvent;
        PluginEvents.DialogueEnded += OnEvent;
        PluginEvents.SessionConnected += OnEvent;
        PluginEvents.SessionReleased += OnEvent;

        PluginEvents.RaiseWorldLoadObserved();
        PluginEvents.RaiseModuleTransitionStarted();
        PluginEvents.RaiseServerModuleGone();
        PluginEvents.RaiseDialogueStarted();
        PluginEvents.RaiseDialogueEnded();
        PluginEvents.RaiseSessionConnected();
        PluginEvents.RaiseSessionReleased();

        Assert.Equal(7, calls);
    }
}
