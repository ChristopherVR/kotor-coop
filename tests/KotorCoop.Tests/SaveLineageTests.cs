using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The lineage half of the save-identity guard (2026-09-03): the engine's own transition
/// autosave rewrites each side's digested save with that side's own counters, so after a real
/// door crossing the two identities disagree for good even though both peers are still in the
/// same world — see <see cref="SaveLineage"/>'s doc comment and docs/research/cinematics.md
/// §8.3c for the live crossing that took a needless rejoin.
/// </summary>
public class SaveLineageTests
{
    private static WorldDigest Digest(uint identity, uint load = 0, string module = "end_m01ab") =>
        new(GlobalsCatalogue: 1, SaveIdentity: identity, Module: module, ModuleGit: 7, LoadSerial: load);

    private static WorldDigestComparison Judge(SaveLineage lineage, in WorldDigest local, in WorldDigest remote) =>
        lineage.Apply(WorldDigest.Compare(local, remote), local, remote);

    [Fact]
    public void APeerThatNeverAgreedIsJudgedOnTheRawComparison()
    {
        // A joining peer on a different save: the case the identity exists for.
        SaveLineage lineage = new();

        WorldDigestComparison result = Judge(lineage, Digest(0xAAAA), Digest(0xBBBB));

        Assert.True(result.SaveIdentityMismatch);
        Assert.False(lineage.HasAgreed);
    }

    [Fact]
    public void ASaveOnEitherSideAfterAgreementIsNotADifferentSave()
    {
        // The live crossing: both agree in end_m01aa, cross a real door, each engine autosaves
        // its own counters, and the identities disagree in end_m01ab with no load anywhere.
        SaveLineage lineage = new();

        Assert.False(Judge(lineage, Digest(0x1111, module: "end_m01aa"), Digest(0x1111, module: "end_m01aa")).SaveIdentityMismatch);
        Assert.True(lineage.HasAgreed);

        WorldDigestComparison afterTheDoor = Judge(lineage, Digest(0x2222), Digest(0x3333));

        Assert.False(afterTheDoor.SaveIdentityMismatch);
        Assert.True(lineage.Absorbing);
        // A module-name flicker mid-transition (Compare() not judging) does not end the episode ...
        Judge(lineage, Digest(0x2222, module: "end_m01ab"), Digest(0x3333, module: "end_m01aa"));
        Assert.True(lineage.Absorbing);
        // ... only a recorded agreement does (the client adopted the host's save again).
        Judge(lineage, Digest(0x3333), Digest(0x3333));
        Assert.False(lineage.Absorbing);
        // Everything else passes through: the module both stand in is still named.
        Assert.Equal("end_m01ab", afterTheDoor.Module);
    }

    [Fact]
    public void ALoadOnTheLocalSideSinceAgreementIsHonored()
    {
        SaveLineage lineage = new();
        Judge(lineage, Digest(0x1111, load: 3), Digest(0x1111, load: 5));

        // This side's plugin counted an unannounced teardown: a Load Game, which really can
        // put a different world here.
        Assert.True(Judge(lineage, Digest(0x2222, load: 4), Digest(0x1111, load: 5)).SaveIdentityMismatch);
    }

    [Fact]
    public void ALoadOnTheRemoteSideSinceAgreementIsHonored()
    {
        SaveLineage lineage = new();
        Judge(lineage, Digest(0x1111, load: 3), Digest(0x1111, load: 5));

        Assert.True(Judge(lineage, Digest(0x1111, load: 3), Digest(0x2222, load: 6)).SaveIdentityMismatch);
    }

    [Fact]
    public void AgreementIsReRecordedSoALaterLoadIsJudgedAgainstTheNewestMatch()
    {
        SaveLineage lineage = new();
        Judge(lineage, Digest(0x1111, load: 0), Digest(0x1111, load: 0));

        // The remote loads (serial 0 -> 1) and the two agree again after the rejoin ...
        Assert.False(Judge(lineage, Digest(0x1111, load: 0), Digest(0x1111, load: 1)).SaveIdentityMismatch);

        // ... so a transition autosave AFTER that is a save against the new checkpoint, not a
        // stale "the remote loaded once" verdict.
        Assert.False(Judge(lineage, Digest(0x2222, load: 0), Digest(0x3333, load: 1)).SaveIdentityMismatch);

        // And a further load is still caught.
        Assert.True(Judge(lineage, Digest(0x2222, load: 0), Digest(0x4444, load: 2)).SaveIdentityMismatch);
    }

    [Fact]
    public void AnUnresolvedOrApartComparisonIsNotAnAgreement()
    {
        SaveLineage lineage = new();

        // One side unresolved: Compare() says no mismatch, but nothing has been proven.
        Judge(lineage, Digest(0x1111), Digest(0));
        Assert.False(lineage.HasAgreed);

        // Same identity but different modules: Compare() does not judge it, so neither do we.
        Judge(lineage, Digest(0x1111, module: "end_m01aa"), Digest(0x1111, module: "end_m01ab"));
        Assert.False(lineage.HasAgreed);

        Assert.True(Judge(lineage, Digest(0x1111), Digest(0x2222)).SaveIdentityMismatch);
    }

    [Fact]
    public void OtherMismatchesPassThroughUntouched()
    {
        SaveLineage lineage = new();
        Judge(lineage, Digest(0x1111), Digest(0x1111));

        WorldDigest local = new(GlobalsCatalogue: 1, SaveIdentity: 0x2222, Module: "end_m01ab", ModuleGit: 7);
        WorldDigest remote = new(GlobalsCatalogue: 2, SaveIdentity: 0x3333, Module: "end_m01ab", ModuleGit: 8);

        WorldDigestComparison result = Judge(lineage, local, remote);

        Assert.True(result.GlobalsCatalogueMismatch);
        Assert.True(result.ModuleGitMismatch);
        Assert.False(result.SaveIdentityMismatch);
    }
}
