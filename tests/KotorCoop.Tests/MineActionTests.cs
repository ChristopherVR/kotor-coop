using KotorCoop.Plugin;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class MineActionTests
{
    [Theory]
    [InlineData("recover|tar_m10aa", true, "tar_m10aa")]
    [InlineData("disarm|M13AA", false, "M13AA")]
    public void ParsesModuleBoundNativeActionRequest(string text, bool recover, string module)
    {
        Assert.True(MineActionRule.TryParseRequest(text, out bool parsedRecover, out string parsedModule));
        Assert.Equal(recover, parsedRecover);
        Assert.Equal(module, parsedModule);
    }

    [Theory]
    [InlineData("")]
    [InlineData("recover")]
    [InlineData("recover|")]
    [InlineData("flag|tar_m10aa")]
    [InlineData("recover|tar_m10aa|extra")]
    [InlineData("recover|abcdefghijklmnopq")]
    public void RefusesMalformedOrOversizedModuleBoundRequest(string text) =>
        Assert.False(MineActionRule.TryParseRequest(text, out _, out _));

    [Fact]
    public void NativeActionIsFencedByBothModuleAndWorldLoadSerial()
    {
        Assert.Null(MineActionRule.ScopeRefusal("tar_m10aa", 7, "TAR_M10AA", 7));
        Assert.Contains("module", MineActionRule.ScopeRefusal("tar_m10aa", 7, "m13aa", 7));
        Assert.Contains("world load", MineActionRule.ScopeRefusal("tar_m10aa", 6, "tar_m10aa", 7));
    }

    [Theory]
    [InlineData("mineaction recover 0x71", "recover", 0x71)]
    [InlineData("mineaction DISARM #AB", "disarm", 0xAB)]
    public void ParsesOperationAndDeterministicGitId(string line, string action, int objectId)
    {
        CoopCommand parsed = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.MineAction, parsed.Kind);
        Assert.Equal(action, parsed.Address);
        Assert.Equal(objectId, parsed.Amount);
    }

    [Theory]
    [InlineData("mineaction")]
    [InlineData("mineaction recover")]
    [InlineData("mineaction recover 0x71 extra")]
    [InlineData("mineaction flag 0x71")]
    [InlineData("mineaction recover 71")]
    [InlineData("mineaction recover 0x0")]
    [InlineData("mineaction recover 0x10000")]
    [InlineData("mineaction recover -1")]
    public void RejectsMalformedOrNonGitTargets(string line) =>
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse(line).Kind);

    [Fact]
    public void AllowsOnlyResolvedCurrentGitMineAndLocalControlledActor()
    {
        MineActionRule.Facts valid = new(
            ServerTargetFound: true,
            ServerTargetIsTrigger: true,
            TargetIsMine: true,
            TargetIsGit: true,
            TargetIsCurrentArea: true,
            Disarmable: true,
            ClientTargetMatches: true,
            LocalActorMatches: true);

        Assert.Null(MineActionRule.Refusal(valid));
        Assert.Equal("server mine id does not resolve", MineActionRule.Refusal(valid with { ServerTargetFound = false }));
        Assert.Equal("target is not a trigger", MineActionRule.Refusal(valid with { ServerTargetIsTrigger = false }));
        Assert.Equal("target is not a floor mine", MineActionRule.Refusal(valid with { TargetIsMine = false }));
        Assert.Equal("party-placed mines are not supported by this diagnostic", MineActionRule.Refusal(valid with { TargetIsGit = false }));
        Assert.Equal("mine is not in the current area", MineActionRule.Refusal(valid with { TargetIsCurrentArea = false }));
        Assert.Equal("mine is not disarmable", MineActionRule.Refusal(valid with { Disarmable = false }));
        Assert.Equal("client-side trigger identity did not match the server mine", MineActionRule.Refusal(valid with { ClientTargetMatches = false }));
        Assert.Equal("controlled client creature did not match the local player identity", MineActionRule.Refusal(valid with { LocalActorMatches = false }));
    }
}
