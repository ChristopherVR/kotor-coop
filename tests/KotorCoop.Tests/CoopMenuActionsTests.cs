using KotorCoop.Plugin;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class CoopMenuActionsTests
{
    [Fact]
    public void EveryMenuActionDispatchesAKnownCommandAndHasAStableNonAddressIdentity()
    {
        HashSet<string> markers = [];
        for (int i = 0; i < CoopMenuActions.Count; i++)
        {
            Assert.False(string.IsNullOrWhiteSpace(CoopMenuActions.Caption(i)));
            CoopCommand command = ConsoleCommand.Parse(CoopMenuActions.Command(i));
            Assert.DoesNotContain(command.Kind, new[] { CoopCommandKind.None, CoopCommandKind.Unknown, CoopCommandKind.Invalid });
            Assert.True(markers.Add(CoopMenuActions.Marker(i)));
            Assert.Equal(i, CoopMenuActions.FindMarker(CoopMenuActions.Marker(i)));
        }
        Assert.Equal(-1, CoopMenuActions.FindMarker("192.168.1.2:27140"));
        Assert.Equal(-1, CoopMenuActions.FindMarker(null));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(14)]
    public void ScrollingKeepsEveryActionOnScreenWithAnEmptyOrFullServerList(int total)
    {
        for (int selected = 0; selected < total; selected++)
        {
            int start = CoopMenuActions.WindowStart(total, selected);
            Assert.InRange(selected, start, start + CoopMenuActions.VisibleRows - 1);
            Assert.InRange(start, 0, total - CoopMenuActions.VisibleRows);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    [InlineData(int.MaxValue)]
    public void OutOfRangeSelectionsCannotStartASession(int index)
    {
        Assert.Equal(string.Empty, CoopMenuActions.Command(index));
        Assert.Equal(CoopCommandKind.None, ConsoleCommand.Parse(CoopMenuActions.Command(index)).Kind);
    }

    [Fact]
    public void RepeatedIdenticalDiscoveryUpdatesKeepTheSelectedHost()
    {
        string[] hosts = ["192.168.1.2:27140", "192.168.1.3:27140"];
        for (int update = 0; update < 100; update++)
            Assert.Equal((1, true), CoopMenuActions.ResolveSelection(hosts[1], -1, hosts));
    }

    [Fact]
    public void HostSelectionFollowsItsAddressAcrossReordering()
    {
        Assert.Equal((0, true), CoopMenuActions.ResolveSelection("192.168.1.3:27140", -1,
            ["192.168.1.3:27140", "192.168.1.2:27140"]));
    }

    [Fact]
    public void EveryActionStaysSelectedWhenHostsAppearDisappearOrReorder()
    {
        for (int action = 0; action < CoopMenuActions.Count; action++)
        {
            Assert.Equal((action, true), CoopMenuActions.ResolveSelection(null, action, []));
            Assert.Equal((action + 2, true), CoopMenuActions.ResolveSelection(null, action, ["a:1", "b:1"]));
            Assert.Equal((action + 2, true), CoopMenuActions.ResolveSelection(null, action, ["b:1", "a:1"]));
            Assert.Equal((action, true), CoopMenuActions.ResolveSelection(null, action, []));
        }
    }

    [Fact]
    public void ADisappearedHostRequiresFreshConfirmationOfTheFallback()
    {
        Assert.Equal((0, false), CoopMenuActions.ResolveSelection("gone:1", -1, ["other:1"]));
        Assert.Equal((0, false), CoopMenuActions.ResolveSelection("gone:1", -1, []));
    }
}
