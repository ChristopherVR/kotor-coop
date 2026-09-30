using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class ControlledSceneRecoveryRuleTests
{
    private static PlayerState Local(uint flags = 0, int health = 12) =>
        new(new Vector3f(21, 44, 9), 0, "lev_m40aa", health, 0, MaxHealth: 12, Flags: flags);

    [Fact]
    public void AbandonedDialogueAfterKnownControlledHostLoadCanCarryCheckpoint() =>
        Assert.True(ControlledSceneRecoveryRule.MayUseLivingCheckpoint(RejoinTrigger.HostReload,
            true, true, Local(PlayerState.FlagInDialogue), false));

    [Theory]
    [InlineData(RejoinTrigger.SaveMismatch)]
    [InlineData(RejoinTrigger.ManualWorldRefresh)]
    [InlineData(RejoinTrigger.SoloReturn)]
    [InlineData(RejoinTrigger.OwnLoad)]
    public void OtherTriggersStillRequireTheirExistingCapturePolicy(RejoinTrigger trigger) =>
        Assert.False(ControlledSceneRecoveryRule.MayUseLivingCheckpoint(trigger, true, true, Local(), false));

    [Fact]
    public void OrdinaryBusyDialogueCannotUseAnOlderCheckpoint() =>
        Assert.False(ControlledSceneRecoveryRule.MayUseLivingCheckpoint(RejoinTrigger.HostReload,
            false, true, Local(PlayerState.FlagInDialogue), false));

    [Fact]
    public void NativeControlledActorAndMissingPlayerCannotBeMistakenForOwnCharacter()
    {
        Assert.False(ControlledSceneRecoveryRule.MayUseLivingCheckpoint(RejoinTrigger.HostReload, true, true, Local(), true));
        Assert.False(ControlledSceneRecoveryRule.MayUseLivingCheckpoint(RejoinTrigger.HostReload, true, false, Local(), false));
    }

    [Theory]
    [InlineData(PlayerState.FlagAway)]
    [InlineData(PlayerState.FlagSoloSegment)]
    [InlineData(PlayerState.FlagDowned)]
    [InlineData(PlayerState.FlagPartyWiped)]
    public void UnresolvedOrNonlivingPlayerDoesNotSelectControlledAbortPolicy(uint flags) =>
        Assert.False(ControlledSceneRecoveryRule.MayUseLivingCheckpoint(RejoinTrigger.HostReload, true, true, Local(flags), false));

    [Fact]
    public void ZeroHealthCannotUseControlledAbortPolicy() =>
        Assert.False(ControlledSceneRecoveryRule.MayUseLivingCheckpoint(RejoinTrigger.HostReload, true, true, Local(health: 0), false));
}
