using KotorCoop.Agent;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PostAdoptionGitMismatchNoticeTests
{
    [Fact]
    public void WarnsOnceWhenAdoptedModuleMapsStillDifferAfterSettle()
    {
        PostAdoptionGitMismatchNotice watch = new();
        watch.Arm("lev_m40aa", 1_000, managerGeneration: 4);

        Assert.False(watch.Observe("lev_m40aa", 11, "lev_m40aa", 22, 4, 5_999));
        Assert.True(watch.Observe("LEV_M40AA", 11, "lev_m40aa", 22, 4, 6_000));
        Assert.False(watch.Observe("lev_m40aa", 11, "lev_m40aa", 22, 4, 7_000));
    }

    [Fact]
    public void DoesNotWarnForUnknownOrDifferentModuleAndClearsOnAgreement()
    {
        PostAdoptionGitMismatchNotice watch = new();
        watch.Arm("lev_m40aa", 0, managerGeneration: 1);

        Assert.False(watch.Observe("lev_m40aa", 0, "lev_m40aa", 22, 1, 6_000));
        Assert.False(watch.Observe("lev_m40aa", 11, "danm13", 22, 1, 6_000));
        Assert.False(watch.Observe("lev_m40aa", 11, "lev_m40aa", 11, 1, 6_000));
        Assert.False(watch.Observe("lev_m40aa", 11, "lev_m40aa", 22, 1, 7_000));
    }

    [Fact]
    public void ExpiresIfAdoptedModuleDoesNotSettleAndResetDisarms()
    {
        PostAdoptionGitMismatchNotice watch = new();
        watch.Arm("lev_m40aa", 10, managerGeneration: 1);

        Assert.False(watch.Observe("danm13", 11, "danm13", 22, 1, 60_009));
        Assert.False(watch.Observe("lev_m40aa", 11, "lev_m40aa", 22, 1, 60_010));

        watch.Arm("lev_m40aa", 70_000, managerGeneration: 2);
        watch.Reset();
        Assert.False(watch.Observe("lev_m40aa", 11, "lev_m40aa", 22, 2, 80_000));
    }

    [Fact]
    public void DoesNotWarnAfterBothPeersLeaveAdoptedModuleAndReturn()
    {
        PostAdoptionGitMismatchNotice watch = new();
        watch.Arm("lev_m40aa", 0, managerGeneration: 8);

        Assert.False(watch.Observe("lev_m40aa", 11, "lev_m40aa", 22, 8, 1_000));
        Assert.False(watch.Observe("danm13", 11, "danm13", 22, 8, 2_000));
        Assert.False(watch.Observe("lev_m40aa", 11, "lev_m40aa", 22, 8, 6_000));
    }

    [Fact]
    public void NewManagerGenerationInvalidatesLateAdoptionNotice()
    {
        PostAdoptionGitMismatchNotice watch = new();
        watch.Arm("lev_m40aa", 0, managerGeneration: 8);

        Assert.False(watch.Observe("lev_m40aa", 11, "lev_m40aa", 22, 9, 6_000));
        Assert.False(watch.Observe("lev_m40aa", 11, "lev_m40aa", 22, 8, 7_000));
    }
}
