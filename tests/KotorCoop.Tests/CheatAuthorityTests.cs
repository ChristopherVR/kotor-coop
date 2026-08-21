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

    // ---- id spaces: the same number means different creatures on the two instances ----

    /// <summary>
    /// The case that made a client's <c>slay</c> do nothing at all: the console hands down an
    /// id read from the OWNER's census, the mirror holds a materialised stand-in for it, and
    /// resolving the owner's number here finds nothing. The body to resolve is the stand-in;
    /// the debt is still keyed by the owner's number, because that is the only one the owner
    /// can spend it against.
    /// </summary>
    [Fact]
    public void AnOwnerIdWithAStandInResolvesLocallyAndStillPaysTheOwner()
    {
        (uint local, uint owner) = CheatAuthority.MirrorIds(
            requested: 0x1A, mappedLocal: 0x135, mappedOwner: 0);

        Assert.Equal(0x135u, local);
        Assert.Equal(0x1Au, owner);
    }

    /// <summary>The other direction: somebody typed one of OUR stand-in ids, which means
    /// nothing to the owner, so the debt is translated back into their space.</summary>
    [Fact]
    public void AStandInIdIsForwardedUnderTheOwnersNumber()
    {
        (uint local, uint owner) = CheatAuthority.MirrorIds(
            requested: 0x135, mappedLocal: 0, mappedOwner: 0x1A);

        Assert.Equal(0x135u, local);
        Assert.Equal(0x1Au, owner);
    }

    /// <summary>With no mapping either way the ordinary GIT-identity assumption stands — the
    /// same placed creature carries the same id in both engines, which is what the whole
    /// enemy channel keys on.</summary>
    [Fact]
    public void AnUnmappedIdIsAssumedIdenticalOnBothSides()
    {
        (uint local, uint owner) = CheatAuthority.MirrorIds(
            requested: 0x35, mappedLocal: 0, mappedOwner: 0);

        Assert.Equal(0x35u, local);
        Assert.Equal(0x35u, owner);
    }

    /// <summary>
    /// A number that is BOTH an owner id we hold a body for and one of our own stand-in ids
    /// is read as the owner's. That precedence is not arbitrary: every id the bare
    /// <c>slay</c> sweep produces on a mirroring instance comes out of the owner's census, so
    /// the owner reading is the one that is nearly always meant.
    /// </summary>
    [Fact]
    public void TheOwnerReadingWinsWhenANumberIsAmbiguous()
    {
        (uint local, uint owner) = CheatAuthority.MirrorIds(
            requested: 0x40, mappedLocal: 0x200, mappedOwner: 0x99);

        Assert.Equal(0x200u, local);
        Assert.Equal(0x40u, owner);
    }

    // ---- destroyobject: ForDestroy mirrors ForSlay's person/companion/plot rules, but with
    // a hard refusal instead of a forward (docs/research/creature-liveness.md's own "never
    // resurrects" rule means there is no destroy-ledger to forward through). ----------------

    [Fact]
    public void TheOwnerDestroysLocally()
    {
        Assert.Equal(
            DestroyVerdict.DestroyHere,
            CheatAuthority.ForDestroy(
                targetResolved: true, targetIsLocalPlayer: false, targetIsPhantom: false,
                targetIsCompanion: false, targetIsPlot: false, mirrorsEnemies: false));
    }

    /// <summary>Unlike <c>slay</c>, a mirroring instance is refused outright — there is no
    /// destroy-ledger, so nothing here can be forwarded to the owner.</summary>
    [Fact]
    public void AMirroringInstanceIsRefusedOutrightNotForwarded()
    {
        Assert.Equal(
            DestroyVerdict.RefuseNotOwner,
            CheatAuthority.ForDestroy(
                targetResolved: true, targetIsLocalPlayer: false, targetIsPhantom: false,
                targetIsCompanion: false, targetIsPlot: false, mirrorsEnemies: true));
    }

    [Fact]
    public void AnUnresolvedDestroyTargetIsRefusedBeforeAnythingElseIsAsked()
    {
        Assert.Equal(
            DestroyVerdict.RefuseMissing,
            CheatAuthority.ForDestroy(
                targetResolved: false, targetIsLocalPlayer: true, targetIsPhantom: true,
                targetIsCompanion: true, targetIsPlot: true, mirrorsEnemies: true));
    }

    [Fact]
    public void YourOwnCharacterIsNeverADestroyTarget()
    {
        Assert.Equal(
            DestroyVerdict.RefusePlayer,
            CheatAuthority.ForDestroy(
                targetResolved: true, targetIsLocalPlayer: true, targetIsPhantom: false,
                targetIsCompanion: false, targetIsPlot: false, mirrorsEnemies: false));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void APeersPhantomIsNeverADestroyTargetOnEitherSide(bool mirrorsEnemies)
    {
        Assert.Equal(
            DestroyVerdict.RefusePhantom,
            CheatAuthority.ForDestroy(
                targetResolved: true, targetIsLocalPlayer: false, targetIsPhantom: true,
                targetIsCompanion: false, targetIsPlot: false, mirrorsEnemies: mirrorsEnemies));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACompanionIsNeverADestroyTargetOnEitherSide(bool mirrorsEnemies)
    {
        Assert.Equal(
            DestroyVerdict.RefuseCompanion,
            CheatAuthority.ForDestroy(
                targetResolved: true, targetIsLocalPlayer: false, targetIsPhantom: false,
                targetIsCompanion: true, targetIsPlot: false, mirrorsEnemies: mirrorsEnemies));
    }

    [Fact]
    public void APlotCreatureIsRefusedFromDestroyToo()
    {
        Assert.Equal(
            DestroyVerdict.RefusePlot,
            CheatAuthority.ForDestroy(
                targetResolved: true, targetIsLocalPlayer: false, targetIsPhantom: false,
                targetIsCompanion: false, targetIsPlot: true, mirrorsEnemies: false));
    }

    [Fact]
    public void EveryDestroyVerdictHasAnExplanation()
    {
        foreach (DestroyVerdict verdict in Enum.GetValues<DestroyVerdict>())
        {
            string explanation = CheatAuthority.Explain(verdict, Target);
            Assert.False(string.IsNullOrWhiteSpace(explanation), $"{verdict} has no explanation");
            Assert.NotEqual("unhandled verdict", explanation);
        }
    }
}
