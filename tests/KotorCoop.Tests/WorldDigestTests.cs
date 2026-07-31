using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure in-world comparison both peers run on the exchanged <see cref="WorldDigest"/>s.
/// The properties that matter: unknown (0/empty) never counts as a mismatch, the module
/// GIT is only judged when both peers stand in the same module, and a module change that
/// lands on agreeing GITs clears the mismatch — which is what restores the id-keyed
/// channels per module.
/// </summary>
public class WorldDigestTests
{
    private static WorldDigest Digest(
        uint globals = 0xAAAA, uint save = 0xBBBB, string module = "end_m01aa", uint git = 0xCCCC) =>
        new(globals, save, module, git);

    [Fact]
    public void IdenticalDigestsMismatchNothing()
    {
        WorldDigestComparison m = WorldDigest.Compare(Digest(), Digest());

        Assert.False(m.Any);
    }

    [Fact]
    public void EachResolvedFieldMismatchesIndependently()
    {
        Assert.True(WorldDigest.Compare(Digest(globals: 1), Digest(globals: 2)).GlobalsCatalogueMismatch);
        Assert.True(WorldDigest.Compare(Digest(save: 1), Digest(save: 2)).SaveIdentityMismatch);
        Assert.True(WorldDigest.Compare(Digest(git: 1), Digest(git: 2)).ModuleGitMismatch);

        // And none of them bleeds into the others.
        WorldDigestComparison m = WorldDigest.Compare(Digest(git: 1), Digest(git: 2));
        Assert.False(m.GlobalsCatalogueMismatch);
        Assert.False(m.SaveIdentityMismatch);
    }

    [Fact]
    public void AnUnresolvedFieldOnEitherSideIsNeverAMismatch()
    {
        // 0 means "could not be computed" (unreadable save, no plugin, mid-resolve); per
        // the Unknown-is-not-mismatch rule that must never degrade anything.
        Assert.False(WorldDigest.Compare(Digest(save: 0), Digest(save: 2)).SaveIdentityMismatch);
        Assert.False(WorldDigest.Compare(Digest(save: 1), Digest(save: 0)).SaveIdentityMismatch);
        Assert.False(WorldDigest.Compare(Digest(git: 0), Digest(git: 2)).ModuleGitMismatch);
        Assert.False(WorldDigest.Compare(Digest(globals: 0), Digest(globals: 2)).GlobalsCatalogueMismatch);
        Assert.False(WorldDigest.Compare(WorldDigest.Empty, Digest()).Any);
    }

    [Fact]
    public void ModuleGitIsOnlyJudgedWhenBothPeersStandInTheSameModule()
    {
        // Mid-transit the two digests describe different areas; comparing them would flag
        // every ordinary module change as a mod difference.
        WorldDigestComparison apart = WorldDigest.Compare(
            Digest(module: "end_m01aa", git: 1), Digest(module: "tar_m02aa", git: 2));
        Assert.False(apart.ModuleGitMismatch);

        // Module names compare case-insensitively, like every resref.
        WorldDigestComparison together = WorldDigest.Compare(
            Digest(module: "END_M01AA", git: 1), Digest(module: "end_m01aa", git: 2));
        Assert.True(together.ModuleGitMismatch);

        // An empty module (not yet readable) is unknown, not "somewhere else".
        Assert.False(WorldDigest.Compare(
            Digest(module: "", git: 1), Digest(module: "", git: 2)).ModuleGitMismatch);
    }

    [Fact]
    public void WalkingIntoAnAgreeingModuleClearsTheGitMismatch()
    {
        // The per-module story end to end: a modded module mismatches while both stand in
        // it, and the next module — whose GITs agree — compares clean again. The session
        // recomputes its verdict from the latest digests, so this is exactly what turns
        // the enemy/door/item channels back on.
        WorldDigest localModded = Digest(module: "tar_m03aa", git: 111);
        WorldDigest remoteModded = Digest(module: "tar_m03aa", git: 222);
        Assert.True(WorldDigest.Compare(localModded, remoteModded).ModuleGitMismatch);

        WorldDigest localNext = localModded with { Module = "tar_m04aa", ModuleGit = 333 };
        WorldDigest remoteNext = remoteModded with { Module = "tar_m04aa", ModuleGit = 333 };
        Assert.False(WorldDigest.Compare(localNext, remoteNext).Any);
    }

    [Fact]
    public void DefaultConstructedDigestIsSafeDespiteItsNullModule()
    {
        // A default struct (never through the codec) carries a null Module; Compare must
        // read that as unresolved rather than throw.
        WorldDigestComparison m = WorldDigest.Compare(default, Digest());

        Assert.False(m.Any);
        Assert.True(default(WorldDigest).IsEmpty);
    }
}
