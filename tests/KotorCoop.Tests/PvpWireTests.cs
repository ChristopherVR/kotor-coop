using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Everything about duels that is not the round itself or the plugin's pin (docs/research/pvp-plan.md):
/// the setting and how it is negotiated, the two packets, the console words, the plain-language
/// lines, the agent's decisions, and the faction rule for the opponent's body.
/// </summary>
public class PvpWireTests
{
    // ---- pvp = off | duel -----------------------------------------------------------------

    [Fact]
    public void DuelsAreOffUnlessCoopIniSaysDuel()
    {
        Assert.False(BehaviourDigest.Default.Has(BehaviourFlags.Pvp));
        Assert.Equal(1u << 22, (uint)BehaviourFlags.Pvp);
        Assert.False(new CoopConfig().Pvp);
    }

    [Theory]
    [InlineData("duel", true)]
    [InlineData("DUEL", true)]
    [InlineData("off", false)]
    [InlineData("arena", true)]    // arena implies duels
    [InlineData("nonsense", false)] // anything unknown stays off
    [InlineData("", false)]
    public void TheIniValueIsReadTheSameWayEverywhere(string value, bool expected)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, $"pvp = {value}\n");

            CoopConfig? config = CoopConfig.TryLoad(path);

            Assert.NotNull(config);
            Assert.Equal(expected, config!.Pvp);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ADigestCarriesTheBit()
    {
        BehaviourDigest on = Digest(pvp: true);
        BehaviourDigest off = Digest(pvp: false);

        Assert.True(on.Has(BehaviourFlags.Pvp));
        Assert.False(off.Has(BehaviourFlags.Pvp));
        Assert.NotEqual(on.Hash, off.Hash);
    }

    [Fact]
    public void ADifferenceIsHostAuthoritative()
    {
        BehaviourComparisonResult result = BehaviourCompatibility.Compare(Digest(pvp: true), Digest(pvp: false));

        BehaviourDifference diff = Assert.Single(result.Differences);
        Assert.Equal("pvp", diff.Key);
        Assert.Equal("1", diff.HostValue);
        Assert.True(diff.HostAuthoritative);
    }

    [Fact]
    public void AgreeingPeersHaveNothingToSay()
    {
        Assert.True(BehaviourCompatibility.Compare(Digest(pvp: true), Digest(pvp: true)).IsEmpty);
    }

    private static BehaviourDigest Digest(bool pvp) => BehaviourDigest.Compute(
        localCombat: false, companionLocalCombat: false, partyFastPump: false, partySync: true,
        clientDoorRefusal: true, transitionAnnounce: true, clientLoadRejoin: true, hostLoadReadopt: true,
        effectSync: true, triggerRelay: true, clientEncounters: true, dialogueItemSync: true,
        dialogueRecruitSync: true, scriptItemSync: true, movieRelay: true, livenessSync: true,
        mapRevealSync: true, requireIdenticalSave: false, interpolationMs: 100, followLead: "any",
        pvp: pvp);

    // ---- the packets ----------------------------------------------------------------------

    [Fact]
    public void AnIntentRoundTrips()
    {
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        PvpIntentPacket sent = new(77, 5, 123456, PvpOp.Challenge, 9);

        int length = PacketCodec.Write(buffer, sent);
        PvpIntentPacket? read = PacketCodec.ReadPvpIntent(buffer.AsSpan(0, length));

        Assert.Equal(sent, read);
    }

    [Fact]
    public void AnIntentWithAnUnknownOpIsRejected()
    {
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new PvpIntentPacket(1, 2, 3, PvpOp.Accept, 0));
        buffer[length - 5] = 99; // the op byte sits just before the 4-byte target

        Assert.Null(PacketCodec.ReadPvpIntent(buffer.AsSpan(0, length)));
    }

    [Fact]
    public void AStateWithItsNoticesRoundTrips()
    {
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        PvpStatePacket sent = new(
            77, PvpPhase.Live, 4, 1, 5,
            [new PvpNotice(10, 5, PvpNoticeCode.Started, 1), new PvpNotice(11, 1, PvpNoticeCode.Started, 5)]);

        int length = PacketCodec.Write(buffer, sent);
        PvpStatePacket? read = PacketCodec.ReadPvpState(buffer.AsSpan(0, length));

        Assert.NotNull(read);
        Assert.Equal((77u, PvpPhase.Live, 4u, 1u, 5u), (read!.HostSessionId, read.Phase, read.RoundId, read.Challenger, read.Target));
        Assert.Equal(sent.Notices, read.Notices);
    }

    [Fact]
    public void AFullStateFitsOneDatagram()
    {
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        List<PvpNotice> notices = [.. Enumerable.Range(1, PvpStatePacket.MaxNotices)
            .Select(i => new PvpNotice((uint)i, 2, PvpNoticeCode.Won, 3))];

        Assert.True(PacketCodec.Write(buffer, new PvpStatePacket(1, PvpPhase.Idle, 0, 0, 0, notices)) > 0);
    }

    [Fact]
    public void AStateWithAnImpossibleNoticeCodeIsRejected()
    {
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(
            buffer, new PvpStatePacket(1, PvpPhase.Idle, 0, 0, 0, [new PvpNotice(1, 2, PvpNoticeCode.Won, 3)]));
        buffer[length - 6] = 200; // the code byte sits before the 4-byte "other" and the arena flag

        Assert.Null(PacketCodec.ReadPvpState(buffer.AsSpan(0, length)));
    }

    [Fact]
    public void EveryNoticeCodeIsOnTheWireAndHasAUniqueValue()
    {
        PvpNoticeCode[] codes = [.. Enum.GetValues<PvpNoticeCode>().Where(c => c != PvpNoticeCode.None)];

        Assert.Equal(codes.Length, codes.Select(c => (byte)c).Distinct().Count());
        Assert.Equal((byte)codes.Length, (byte)codes.Max());
    }

    // ---- the words a player types ---------------------------------------------------------

    [Theory]
    [InlineData("duel Aria", CoopCommandKind.Duel, "Aria")]
    [InlineData("duel off", CoopCommandKind.Duel, "off")]
    [InlineData("duel on", CoopCommandKind.Duel, "on")]
    [InlineData("duel #1", CoopCommandKind.Duel, "#1")]
    [InlineData("coop duel Aria", CoopCommandKind.Duel, "Aria")]
    [InlineData("accept", CoopCommandKind.DuelAccept, null)]
    [InlineData("decline", CoopCommandKind.DuelDecline, null)]
    [InlineData("yield", CoopCommandKind.DuelYield, null)]
    public void TheDuelVerbsParse(string line, CoopCommandKind kind, string? address)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(kind, command.Kind);
        Assert.Equal(address, command.Address);
    }

    [Fact]
    public void DuelWithNoNameTellsThePlayerWhatToType()
    {
        CoopCommand command = ConsoleCommand.Parse("duel");

        Assert.Equal(CoopCommandKind.Invalid, command.Kind);
        Assert.Contains("accept", command.Error);
    }

    [Fact]
    public void TheVerbsCompleteAndAreInterceptedAtTheInGameConsole()
    {
        foreach (string verb in new[] { "duel", "accept", "decline", "yield" })
        {
            Assert.Contains(ConsoleVerbCatalog.Entries, entry => entry.Verb == verb);
            Assert.NotEqual(CoopCommandKind.Unknown, ConsoleCommand.Parse(verb + " x").Kind);
        }
    }

    // ---- the lines ------------------------------------------------------------------------

    [Fact]
    public void EveryNoticeCodeHasAPlainLineThatNamesThePlayer()
    {
        // A boss power's line depends on which power it was (PvpText.FormatBossPower; ChungusBossTests).
        foreach (PvpNoticeCode code in Enum.GetValues<PvpNoticeCode>()
            .Where(c => c is not (PvpNoticeCode.None or PvpNoticeCode.BossPower)))
        {
            string line = PvpText.Format(code, "Aria");

            Assert.False(string.IsNullOrWhiteSpace(line), code.ToString());
            Assert.DoesNotContain("peer", line, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("Aria", PvpText.Format(PvpNoticeCode.Challenged, "Aria"));
        Assert.Contains("accept", PvpText.Format(PvpNoticeCode.Challenged, "Aria"));
        Assert.Contains("30 seconds", PvpText.Format(PvpNoticeCode.Challenged, "Aria"));
    }

    [Fact]
    public void TheProductLinesAreWordForWord()
    {
        Assert.Equal(
            "You challenged Aria to a duel. Waiting for an answer.",
            PvpText.Format(PvpNoticeCode.ChallengeSent, "Aria"));
        Assert.Equal("Aria said no. Nothing happened.", PvpText.Format(PvpNoticeCode.Declined, "Aria"));
        Assert.Equal("Aria did not answer. Nothing happened.", PvpText.Format(PvpNoticeCode.TimedOut, "Aria"));
        Assert.Equal(
            "Duel with Aria starts in 5. Type yield at any time to stop.",
            PvpText.Format(PvpNoticeCode.Countdown, "Aria"));
        Assert.Equal(
            "The host has not turned on duels. Ask them, or see the pvp setting.",
            PvpText.Format(PvpNoticeCode.NotEnabled, "Aria"));
    }

    [Fact]
    public void ALossTellsThePlayerNothingWasLostAndTheyAreBack()
    {
        string lost = PvpText.Format(PvpNoticeCode.Lost, "Aria");

        Assert.StartsWith("Aria won the duel.", lost);
        Assert.Contains("Nothing was lost", lost);
    }

    // ---- the agent's side -----------------------------------------------------------------

    private static PvpView Round(PvpPhase phase, uint challenger = 1, uint target = 2) =>
        new(phase, 1, challenger, target);

    [Fact]
    public void ThePluginIsToldTheDuelPhaseOnlyForADuellist()
    {
        Assert.Equal((0u, 0u), PvpPumpRule.Levels(Round(PvpPhase.Idle, 0, 0), 1, enabled: true));
        Assert.Equal((0u, 0u), PvpPumpRule.Levels(Round(PvpPhase.Challenged), 1, enabled: true));
        Assert.Equal((1u, 2u), PvpPumpRule.Levels(Round(PvpPhase.Countdown), 1, enabled: true));
        Assert.Equal((2u, 1u), PvpPumpRule.Levels(Round(PvpPhase.Live), 2, enabled: true));
        Assert.Equal((0u, 0u), PvpPumpRule.Levels(Round(PvpPhase.Live), 3, enabled: true));
        Assert.Equal((0u, 0u), PvpPumpRule.Levels(Round(PvpPhase.Live), 1, enabled: false));
    }

    [Fact]
    public void ADuelDefeatNeverCountsTowardAWipe()
    {
        PvpView live = Round(PvpPhase.Live, 1, 2);

        Assert.False(PvpPumpRule.CountsTowardWipe(live, 2));
        Assert.False(PvpPumpRule.LocalMayWipe(live, 1));
        Assert.True(PvpPumpRule.CountsTowardWipe(live, 3));
        Assert.True(PvpPumpRule.LocalMayWipe(live, 3));
        Assert.True(PvpPumpRule.LocalMayWipe(Round(PvpPhase.Idle, 0, 0), 1));
    }

    [Fact]
    public void ADuellistGetsNoMenuCushionAndNoSave()
    {
        PvpView live = Round(PvpPhase.Live, 1, 2);

        Assert.False(PvpPumpRule.MenuGuardAllowed(live, 1));
        Assert.False(PvpPumpRule.SaveAllowed(live, 2));
        Assert.True(PvpPumpRule.MenuGuardAllowed(live, 3));
        Assert.True(PvpPumpRule.SaveAllowed(live, 3));
        Assert.True(PvpPumpRule.SaveAllowed(Round(PvpPhase.Challenged), 1));
    }

    // ---- the opponent's body ----------------------------------------------------------------

    [Fact]
    public void TheOpponentTurnsHostileOnlyInALiveDuel()
    {
        Assert.True(PhantomTargetRule.WantsPvpHostile(
            pvpLive: true, isOpponent: true, alive: true, stealthSync: true, stealthed: false));
        Assert.False(PhantomTargetRule.WantsPvpHostile(
            pvpLive: false, isOpponent: true, alive: true, stealthSync: true, stealthed: false));
        Assert.False(PhantomTargetRule.WantsPvpHostile(
            pvpLive: true, isOpponent: false, alive: true, stealthSync: true, stealthed: false));
        Assert.False(PhantomTargetRule.WantsPvpHostile(
            pvpLive: true, isOpponent: true, alive: false, stealthSync: true, stealthed: false));
    }

    [Fact]
    public void AStealthedOpponentStaysHiddenWhileStealthSyncIsOn()
    {
        Assert.False(PhantomTargetRule.WantsPvpHostile(
            pvpLive: true, isOpponent: true, alive: true, stealthSync: true, stealthed: true));
        Assert.True(PhantomTargetRule.WantsPvpHostile(
            pvpLive: true, isOpponent: true, alive: true, stealthSync: false, stealthed: true));
    }

    [Fact]
    public void ThePvpFactionOverridesEverythingElseAndOnlyThen()
    {
        Assert.Equal(PhantomTargetRule.Kind.PvpHostile, PhantomTargetRule.FactionFor(false, true, pvpHostile: true));
        Assert.Equal(PhantomTargetRule.Kind.PvpHostile, PhantomTargetRule.FactionFor(true, false, pvpHostile: true));

        // Unchanged outside a duel.
        Assert.Equal(PhantomTargetRule.Kind.Neutral, PhantomTargetRule.FactionFor(false, true));
        Assert.Equal(PhantomTargetRule.Kind.Friendly, PhantomTargetRule.FactionFor(true, true));
        Assert.Equal(PhantomTargetRule.Kind.Player, PhantomTargetRule.FactionFor(true, false));
    }
}
