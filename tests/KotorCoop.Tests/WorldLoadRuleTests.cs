using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="WorldLoadRule.IsUnannouncedTeardown"/> is the whole save-vs-load classification
/// primitive PluginHost feeds into <see cref="SharedBlock.OffsetWorldLoadSerial"/>: an ordinary
/// module transition is always preceded by <c>Odyssey.IsAreaTransitionPending</c> going true; a
/// Load Game action (main menu or the in-game pause menu) is not. Deliberately a one-line rule
/// so this assumption is exactly what is pinned here, not buried inside PluginHost's unsafe
/// engine-pointer code.
/// </summary>
public sealed class WorldLoadRuleTests
{
    [Fact]
    public void AnAnnouncedTeardownIsAnOrdinaryTransitionNotALoad()
    {
        Assert.False(WorldLoadRule.IsUnannouncedTeardown(transitionWasPending: true));
    }

    [Fact]
    public void AnUnannouncedTeardownIsTreatedAsALoad()
    {
        Assert.True(WorldLoadRule.IsUnannouncedTeardown(transitionWasPending: false));
    }
}
