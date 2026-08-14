using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The co-op half of the staging cheats: WHO is allowed to kill WHAT, and where the kill
/// goes when the answer is "not you".
///
/// These rules are the reason the cheats exist at all. The poke itself is one engine call;
/// getting the authority wrong does not fail loudly, it produces a corpse in one game and a
/// living enemy in the other, and that surfaces days later as "the combat mirror is broken".
/// </summary>
public class CheatAuthorityTests
{
    private const uint Target = 0x2A;

    /// <summary>The owner of a fight kills its own enemy outright.</summary>
    [Fact]
    public void TheEnemyOwnerKillsLocally()
    {
        Assert.Equal(
            SlayVerdict.KillHere,
            CheatAuthority.ForSlay(
                targetResolved: true, targetIsLocalPlayer: false, targetIsPhantom: false,
                targetIsCompanion: false, targetIsPlot: false, mirrorsEnemies: false));
    }

    /// <summary>
    /// A mirroring instance does NOT kill locally. Its copy is pinned at one hit point and
    /// the owner's next pass would stand any local corpse back up, so the kill is spent
    /// through the damage ledger and the owner's engine produces the real death.
    /// </summary>
    [Fact]
    public void AMirroringInstanceForwardsTheKillToTheOwner()
    {
        Assert.Equal(
            SlayVerdict.ForwardToOwner,
            CheatAuthority.ForSlay(
                targetResolved: true, targetIsLocalPlayer: false, targetIsPhantom: false,
                targetIsCompanion: false, targetIsPlot: false, mirrorsEnemies: true));
    }

    [Fact]
    public void AnUnresolvedTargetIsRefusedBeforeAnythingElseIsAsked()
    {
        Assert.Equal(
            SlayVerdict.RefuseMissing,
            CheatAuthority.ForSlay(
                targetResolved: false, targetIsLocalPlayer: true, targetIsPhantom: true,
                targetIsCompanion: true, targetIsPlot: true, mirrorsEnemies: true));
    }

    [Fact]
    public void YourOwnCharacterIsNeverASlayTarget()
    {
        Assert.Equal(
            SlayVerdict.RefusePlayer,
            CheatAuthority.ForSlay(
                targetResolved: true, targetIsLocalPlayer: true, targetIsPhantom: false,
                targetIsCompanion: false, targetIsPlot: false, mirrorsEnemies: false));
    }

    /// <summary>
    /// A phantom is a peer's body rendered here on their behalf. Killing it would produce a
    /// corpse only this instance can see, and its owner is still walking around.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APeersPhantomIsRefusedOnEitherSide(bool mirrorsEnemies)
    {
        Assert.Equal(
            SlayVerdict.RefusePhantom,
            CheatAuthority.ForSlay(
                targetResolved: true, targetIsLocalPlayer: false, targetIsPhantom: true,
                targetIsCompanion: false, targetIsPlot: false, mirrorsEnemies: mirrorsEnemies));
    }

    /// <summary>Companions are the host's, mirrored by every client — killing one from
    /// either side is a desync, not a death.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACompanionIsRefusedOnEitherSide(bool mirrorsEnemies)
    {
        Assert.Equal(
            SlayVerdict.RefuseCompanion,
            CheatAuthority.ForSlay(
                targetResolved: true, targetIsLocalPlayer: false, targetIsPhantom: false,
                targetIsCompanion: true, targetIsPlot: false, mirrorsEnemies: mirrorsEnemies));
    }

    /// <summary>The module decides who is un-killable — the same rule the combat mirror
    /// applies to a plot-flagged cutscene actor.</summary>
    [Fact]
    public void APlotCreatureIsRefused()
    {
        Assert.Equal(
            SlayVerdict.RefusePlot,
            CheatAuthority.ForSlay(
                targetResolved: true, targetIsLocalPlayer: false, targetIsPhantom: false,
                targetIsCompanion: false, targetIsPlot: true, mirrorsEnemies: false));
    }

    /// <summary>A person check outranks the plot check: "that is your partner" is a better
    /// answer than "the module says no".</summary>
    [Fact]
    public void APhantomThatIsAlsoPlotFlaggedReadsAsAPhantom()
    {
        Assert.Equal(
            SlayVerdict.RefusePhantom,
            CheatAuthority.ForSlay(
                targetResolved: true, targetIsLocalPlayer: false, targetIsPhantom: true,
                targetIsCompanion: false, targetIsPlot: true, mirrorsEnemies: false));
    }

    /// <summary>Every verdict names what it saw. A guard that fails safe and says nothing is
    /// indistinguishable from a guard that is broken.</summary>
    [Fact]
    public void EveryVerdictHasAnExplanation()
    {
        // Enumerated rather than listed, so a verdict added later cannot be forgotten here.
        foreach (SlayVerdict verdict in Enum.GetValues<SlayVerdict>())
        {
            string explanation = CheatAuthority.Explain(verdict, Target);
            Assert.False(string.IsNullOrWhiteSpace(explanation), $"{verdict} has no explanation");
            Assert.NotEqual("unhandled verdict", explanation);
        }
    }

    /// <summary>
    /// The forwarded amount has to clear any base-game creature's hit points and stay well
    /// under the ledger's saturating ceiling — a total that wrapped would read as a RESET on
    /// the owner's side and hand the peer's whole fight back a second time.
    /// </summary>
    [Fact]
    public void TheForwardedKillAmountIsLethalButNotNearTheLedgerCeiling()
    {
        Assert.True(CheatAuthority.ForwardedKillDamage > 1000);
        Assert.True(CheatAuthority.ForwardedKillDamage < uint.MaxValue / 1000);
    }

    /// <summary>The ledger really does accept it: one forwarded kill, one saturating add,
    /// no drop.</summary>
    [Fact]
    public void TheDamageLedgerAcceptsAForwardedKill()
    {
        DamageDealtTable ledger = new(4);

        Assert.True(ledger.Add(Target, CheatAuthority.ForwardedKillDamage));
        Assert.Equal(0u, ledger.Dropped);
        Assert.Equal(CheatAuthority.ForwardedKillDamage, ledger.TotalAt(0));
    }
}
