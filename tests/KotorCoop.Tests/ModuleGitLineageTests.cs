using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary><see cref="ModuleGitLineage"/>: a GIT digest difference between two peers that have
/// provably loaded the same save is a baseline artifact, remembered per digest pair.</summary>
public sealed class ModuleGitLineageTests
{
    private static WorldDigest D(uint identity, uint git, string module = "danm14aa") =>
        WorldDigest.Empty with { SaveIdentity = identity, ModuleGit = git, Module = module, GlobalsCatalogue = 1 };

    /// <summary>Identity 7 is the save that was adopted in these tests.</summary>
    private static WorldDigestComparison Run(ModuleGitLineage lineage, WorldDigest local, WorldDigest remote) =>
        lineage.Apply(WorldDigest.Compare(local, remote), local, remote, identity => identity == 7);

    [Fact]
    public void ADifferentSaveKeepsTheGitMismatch()
    {
        Assert.True(Run(new ModuleGitLineage(), D(1, 10), D(2, 20)).ModuleGitMismatch);
    }

    [Fact]
    public void EqualIdentitiesWithoutAnAdoptionKeepTheGitMismatch()
    {
        // A mod edited this module's placements on one install: same save, different mapping.
        Assert.True(Run(new ModuleGitLineage(), D(5, 10), D(5, 20)).ModuleGitMismatch);
    }

    [Fact]
    public void AgreeingIdentitiesAbsorbTheGitPair()
    {
        ModuleGitLineage lineage = new();
        WorldDigestComparison c = Run(lineage, D(7, 10), D(7, 20));

        Assert.False(c.ModuleGitMismatch);
        Assert.True(lineage.Absorbing);
    }

    [Fact]
    public void TheSamePairStaysAbsorbedAfterAnOrdinarySaveDriftsTheIdentities()
    {
        ModuleGitLineage lineage = new();
        Run(lineage, D(7, 10), D(7, 20));

        Assert.False(Run(lineage, D(8, 10), D(9, 20)).ModuleGitMismatch);
    }

    [Fact]
    public void ANewBaselinePairIsJudgedAfresh()
    {
        ModuleGitLineage lineage = new();
        Run(lineage, D(7, 10), D(7, 20));

        Assert.True(Run(lineage, D(8, 11), D(9, 20)).ModuleGitMismatch);
    }

    [Fact]
    public void AnotherModuleIsJudgedAfresh()
    {
        ModuleGitLineage lineage = new();
        Run(lineage, D(7, 10), D(7, 20));

        Assert.True(Run(lineage, D(8, 10, "danm15"), D(9, 20, "danm15")).ModuleGitMismatch);
    }
}
