using System.Buffers.Binary;
using System.Text;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The arena boss fight (<c>chungus</c>; docs/research/chungus-boss.md): the host's round, what
/// goes over the wire, how his powers are chosen and what they do, his blueprint and install,
/// and the files that ship. Nothing here touches a game or a socket.
/// </summary>
public sealed class ChungusBossTests : IDisposable
{
    private const uint Host = 1;
    private const uint Aria = 2;
    private const uint Bao = 3;
    private const string Home = "tar_m02aa";
    private const string Arena = PvpDuelRound.ArenaModule;

    private readonly string _temp = Path.Combine(Path.GetTempPath(), "kotorcoop-chungus-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_temp))
        {
            Directory.Delete(_temp, recursive: true);
        }
    }

    // ---- the round --------------------------------------------------------------------------

    private static PvpParticipant Well(string module = Home, bool bossReady = true, int hp = 100, bool busy = false) =>
        new(Present: true, Aware: true, Busy: busy, Module: module, Health: hp, MaxHealth: 100, Downed: false,
            ArenaReady: true, BossReady: bossReady);

    private static BossRound Round() => new() { HostPeerId = Host };

    private static List<(uint, PvpParticipant)> Party(PvpParticipant? host = null, PvpParticipant? aria = null) =>
        [(Host, host ?? Well()), (Aria, aria ?? Well())];

    private static Func<uint, PvpParticipant> World(PvpParticipant host, PvpParticipant aria) =>
        id => id switch { Host => host, Aria => aria, _ => default };

    private static PvpNoticeCode[] Codes(BossRound round) => [.. round.DrainNotices().Select(n => n.Code)];

    private static BossRound Live(out long now)
    {
        BossRound round = Round();
        round.Start(Host, Party(), 0, duelBusy: false, joinersMayStart: false);
        Assert.Equal(PvpPhase.Entering, round.Phase);
        Func<uint, PvpParticipant> inArena = World(Well(Arena), Well(Arena));
        round.Tick(1_000, inArena);
        round.Tick(1_000 + PvpDuelRound.ArenaSettleMs, inArena);
        Assert.Equal(PvpPhase.Countdown, round.Phase);
        now = 1_000 + PvpDuelRound.ArenaSettleMs + BossRound.CountdownMs;
        round.Tick(now, inArena);
        Assert.Equal(PvpPhase.Live, round.Phase);
        round.DrainNotices();
        return round;
    }

    [Fact]
    public void TheHostStartsItAndEveryoneIsToldAtOnce()
    {
        BossRound round = Round();

        round.Start(Host, Party(), 0, duelBusy: false, joinersMayStart: false);

        Assert.Equal(PvpPhase.Entering, round.Phase);
        Assert.True(round.RoundId >= BossRound.FirstRoundId);
        Assert.Equal([Host, Aria], round.Players.Order());
        PvpNotice said = Assert.Single(round.DrainNotices());
        Assert.Equal((0u, PvpNoticeCode.BossEntering, Host), (said.ForPeer, said.Code, said.Other));
    }

    [Fact]
    public void AJoinerMayOnlyStartItWhenTheHostAllowsArenaFights()
    {
        BossRound refused = Round();
        refused.Start(Aria, Party(), 0, duelBusy: false, joinersMayStart: false);
        Assert.Equal(PvpPhase.Idle, refused.Phase);
        Assert.Equal([PvpNoticeCode.BossHostOnly], Codes(refused));

        BossRound allowed = Round();
        allowed.Start(Aria, Party(), 0, duelBusy: false, joinersMayStart: true);
        Assert.Equal(PvpPhase.Entering, allowed.Phase);
    }

    [Theory]
    [InlineData("busy")]
    [InlineData("apart")]
    [InlineData("notinstalled")]
    [InlineData("inarena")]
    public void NobodyGoesUnlessEveryoneCan(string problem)
    {
        PvpParticipant aria = problem switch
        {
            "busy" => Well(busy: true),
            "apart" => Well("danm13"),
            "notinstalled" => Well(bossReady: false),
            _ => Well(Arena),
        };
        PvpParticipant host = problem == "inarena" ? Well(Arena) : Well();
        BossRound round = Round();

        round.Start(Host, Party(host, aria), 0, duelBusy: false, joinersMayStart: false);

        Assert.Equal(PvpPhase.Idle, round.Phase);
        PvpNotice said = Assert.Single(round.DrainNotices());
        Assert.Equal(PvpNoticeCode.BossNotReady, said.Code);
        Assert.Equal(Host, said.ForPeer);
    }

    [Fact]
    public void ADuelUnderWayKeepsTheBossOut()
    {
        BossRound round = Round();

        round.Start(Host, Party(), 0, duelBusy: true, joinersMayStart: false);

        Assert.Equal(PvpPhase.Idle, round.Phase);
        Assert.Equal([PvpNoticeCode.DuelInProgress], Codes(round));
    }

    [Fact]
    public void TheCountdownWaitsForEveryoneToArriveAndSettle()
    {
        BossRound round = Round();
        round.Start(Host, Party(), 0, duelBusy: false, joinersMayStart: false);
        round.DrainNotices();

        // The host is in, the joiner is still loading (its state is blank for a while).
        round.Tick(2_000, World(Well(Arena), default));
        round.Tick(9_000, World(Well(Arena), default));
        Assert.Equal(PvpPhase.Entering, round.Phase);

        round.Tick(10_000, World(Well(Arena), Well(Arena)));
        round.Tick(10_000 + PvpDuelRound.ArenaSettleMs - 1, World(Well(Arena), Well(Arena)));
        Assert.Equal(PvpPhase.Entering, round.Phase);
        round.Tick(10_000 + PvpDuelRound.ArenaSettleMs, World(Well(Arena), Well(Arena)));

        Assert.Equal(PvpPhase.Countdown, round.Phase);
        Assert.Equal([PvpNoticeCode.BossCountdown], Codes(round));
    }

    [Fact]
    public void BeatingTheBossWinsAndTakesEveryoneHome()
    {
        BossRound round = Live(out long now);

        round.BossDown();
        round.Tick(now + 100, World(Well(Arena), Well(Arena)));

        Assert.Equal(PvpPhase.Leaving, round.Phase);
        Assert.Equal([PvpNoticeCode.BossWon, PvpNoticeCode.ArenaReturning], Codes(round));

        round.Tick(now + 5_000, World(Well(), Well()));
        round.Tick(now + 5_000 + PvpDuelRound.ArenaSettleMs, World(Well(), Well()));
        Assert.Equal(PvpPhase.Idle, round.Phase);
        Assert.Contains("beaten", round.LastEnding);
    }

    [Fact]
    public void TheFightIsLostOnlyWhenEveryPlayerStaysDown()
    {
        BossRound round = Live(out long now);

        // One player on the floor of the pin is not a loss.
        round.Tick(now + 1_000, World(Well(Arena, hp: 1), Well(Arena)));
        round.Tick(now + 9_000, World(Well(Arena, hp: 1), Well(Arena)));
        Assert.Equal(PvpPhase.Live, round.Phase);

        // Both down, but one is healed inside the hold: still on.
        round.Tick(now + 10_000, World(Well(Arena, hp: 1), Well(Arena, hp: 1)));
        round.Tick(now + 11_000, World(Well(Arena, hp: 40), Well(Arena, hp: 1)));
        round.Tick(now + 10_000 + BossRound.AllDownHoldMs, World(Well(Arena, hp: 40), Well(Arena, hp: 1)));
        Assert.Equal(PvpPhase.Live, round.Phase);

        // Both down for the whole hold.
        round.Tick(now + 20_000, World(Well(Arena, hp: 1), Well(Arena, hp: 1)));
        round.Tick(now + 20_000 + BossRound.AllDownHoldMs, World(Well(Arena, hp: 1), Well(Arena, hp: 1)));
        Assert.Equal(PvpPhase.Leaving, round.Phase);
        Assert.Contains(PvpNoticeCode.BossLost, Codes(round));
    }

    [Fact]
    public void AnyPlayerCanCallItOffAndABystanderCannot()
    {
        BossRound round = Live(out _);

        Assert.False(round.Yield(Bao));
        Assert.Equal(PvpPhase.Live, round.Phase);

        Assert.True(round.Yield(Aria));
        Assert.Equal(PvpPhase.Leaving, round.Phase);
        PvpNotice[] said = [.. round.DrainNotices()];
        Assert.Equal((PvpNoticeCode.BossCancelled, Aria), (said[0].Code, said[0].Other));
        Assert.False(round.Yield(Aria));   // nothing left to give up on the way home
    }

    [Fact]
    public void TheHostLeavingTheArenaEndsIt()
    {
        BossRound round = Live(out long now);

        round.Tick(now + 1_000, World(Well(), Well(Arena)));

        Assert.Equal(PvpPhase.Leaving, round.Phase);
        Assert.Contains(PvpNoticeCode.BossCancelled, Codes(round));
    }

    [Fact]
    public void AJoinerWhoDropsIsLeftOutAndTheRestFightOn()
    {
        BossRound round = Live(out long now);

        round.Tick(now + 1_000, World(Well(Arena), default));
        round.Tick(now + 1_000 + PvpDuelRound.ArenaAbsentGraceMs, World(Well(Arena), default));

        Assert.Equal(PvpPhase.Live, round.Phase);
        Assert.Equal([Host], round.Players);
    }

    [Fact]
    public void ItDoesNotRunForEver()
    {
        BossRound round = Live(out long now);

        round.Tick(now + BossRound.FightLimitMs, World(Well(Arena), Well(Arena)));

        Assert.Equal(PvpPhase.Leaving, round.Phase);
        Assert.Contains(PvpNoticeCode.BossTimedOut, Codes(round));
    }

    [Fact]
    public void PowersAreOnlySaidWhileTheFightIsOn()
    {
        BossRound idle = Round();
        idle.Announce(new BossCue(BossPower.Snap, 50, 0));
        Assert.Empty(idle.DrainNotices());

        BossRound round = Live(out _);
        BossCue cue = new(BossPower.Blast, 30, (ushort)Aria);
        round.Announce(cue);

        PvpNotice said = Assert.Single(round.DrainNotices());
        Assert.Equal((0u, PvpNoticeCode.BossPower), (said.ForPeer, said.Code));
        Assert.Equal(cue, BossCue.Unpack(said.Other));
    }

    // ---- what each game does with the round -------------------------------------------------

    [Fact]
    public void EveryPlayerIsPinnedButNobodysStandInTurnsHostile()
    {
        PvpView live = new(PvpPhase.Live, BossRound.FirstRoundId, Host, 0, Arena: true, Boss: true);

        // Level 1 (pinned), never level 2 (the duel's "the other player is the enemy"), and
        // without the pvp setting: it is a cheat, not a duel.
        Assert.Equal((1u, 0u), PvpPumpRule.Levels(live, Host, enabled: false));
        Assert.Equal((1u, 0u), PvpPumpRule.Levels(live, Bao, enabled: false));
        Assert.Equal((0u, 0u), PvpPumpRule.Levels(live with { Phase = PvpPhase.Entering }, Aria, enabled: true));
        Assert.Equal((0u, 0u), PvpPumpRule.Levels(live with { Phase = PvpPhase.Leaving }, Aria, enabled: true));

        Assert.False(PvpPumpRule.SaveAllowed(live, Aria));
        Assert.False(PvpPumpRule.SaveAllowed(live with { Phase = PvpPhase.Leaving }, Aria));
        Assert.False(PvpPumpRule.CountsTowardWipe(live, Aria));
    }

    [Fact]
    public void ThePartyLinesUpWithoutStandingOnEachOtherAndFacesTheBoss()
    {
        PvpView view = new(PvpPhase.Countdown, BossRound.FirstRoundId, Host, 0, Arena: true, Boss: true);
        List<(float X, float Y, float Z)> spots = [.. Enumerable.Range(0, 8).Select(slot => ArenaFlow.SpawnOf(view, Host, slot))];

        for (int i = 0; i < spots.Count; i++)
        {
            // Inside the ring's floor (x 184-216, y 84-118), on the east half.
            Assert.InRange(spots[i].X, 200f, 216f);
            Assert.InRange(spots[i].Y, 86f, 116f);
            for (int j = i + 1; j < spots.Count; j++)
            {
                float dx = spots[i].X - spots[j].X;
                float dy = spots[i].Y - spots[j].Y;
                Assert.True((dx * dx) + (dy * dy) > 3f * 3f, $"slots {i} and {j} are on top of each other");
            }
        }

        (float bx, float by, _) = ArenaFlow.BossSpot;
        Assert.True(bx < 195f && Math.Abs(by - 100f) < 2f);
    }

    // ---- the wire ---------------------------------------------------------------------------

    [Fact]
    public void TheBossFlagRidesWithTheArenaFlagAndAnOlderPacketReadsAsNeither()
    {
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        PvpStatePacket sent = new(
            7, PvpPhase.Live, BossRound.FirstRoundId, Host, 0,
            [new PvpNotice(4, 0, PvpNoticeCode.BossPower, new BossCue(BossPower.Gizka, 0, 3).Pack())],
            Arena: true, Boss: true);

        int length = PacketCodec.Write(buffer, sent);
        PvpStatePacket? read = PacketCodec.ReadPvpState(buffer.AsSpan(0, length));

        Assert.NotNull(read);
        Assert.True(read.Arena);
        Assert.True(read.Boss);
        Assert.Equal(sent.Notices, read.Notices);

        PvpStatePacket? older = PacketCodec.ReadPvpState(buffer.AsSpan(0, length - 1));
        Assert.NotNull(older);
        Assert.False(older.Arena || older.Boss);

        int plain = PacketCodec.Write(buffer, sent with { Boss = false });
        Assert.False(PacketCodec.ReadPvpState(buffer.AsSpan(0, plain))!.Boss);
    }

    [Fact]
    public void TheChungusRequestRoundTripsAndTheOnePastItIsRejected()
    {
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        PvpIntentPacket sent = new(77, 5, 99, PvpOp.Chungus, 0);
        int length = PacketCodec.Write(buffer, sent);

        Assert.Equal(sent, PacketCodec.ReadPvpIntent(buffer.AsSpan(0, length)));

        buffer[length - 5] = (byte)PvpOp.Chungus + 1;
        Assert.Null(PacketCodec.ReadPvpIntent(buffer.AsSpan(0, length)));
    }

    [Fact]
    public void ACuePacksIntoOneWord()
    {
        BossCue cue = new(BossPower.Drain, 15, 513);

        Assert.Equal(cue, BossCue.Unpack(cue.Pack()));
        Assert.True(cue.Hits(513));
        Assert.False(cue.Hits(2));
        Assert.True(new BossCue(BossPower.Snap, 50, 0).Hits(2));
        Assert.False(BossCue.Unpack(0).IsKnown);
        Assert.False(BossCue.Unpack(200).IsKnown);
    }

    // ---- his head ---------------------------------------------------------------------------

    [Fact]
    public void HeWaitsThenUsesAPowerEverySoOften()
    {
        BossBrain brain = new(1);
        uint[] players = [Host, Aria];

        Assert.Null(brain.Next(0, 100, players));
        Assert.Null(brain.Next(BossBrain.FirstPowerDelayMs - 1, 100, players));
        BossCue? first = brain.Next(BossBrain.FirstPowerDelayMs, 100, players);
        Assert.NotNull(first);
        Assert.NotEqual(BossPower.Snap, first.Value.Power);

        // Not again at once, and by ten seconds later certainly.
        Assert.Null(brain.Next(BossBrain.FirstPowerDelayMs + 4_000, 100, players));
        Assert.NotNull(brain.Next(BossBrain.FirstPowerDelayMs + 10_000, 100, players));
    }

    [Fact]
    public void TheSnapComesWhenHeIsHurtAndAgainWhenHeIsNearlyDone()
    {
        BossBrain brain = new(7);
        uint[] players = [Host, Aria, Bao];
        long now = 0;
        brain.Next(now, 100, players);

        List<(int Health, BossPower Power)> used = [];
        foreach (int health in (int[])[100, 90, 58, 50, 40, 24, 10, 5])
        {
            now += 20_000;
            used.Add((health, brain.Next(now, health, players)!.Value.Power));
        }

        Assert.Equal(BossPower.Snap, used.Single(u => u.Health == 58).Power);
        Assert.Equal(BossPower.Snap, used.Single(u => u.Health == 24).Power);
        Assert.Equal(2, used.Count(u => u.Power == BossPower.Snap));
    }

    [Fact]
    public void AnAimedPowerNamesAPlayerAndTheOthersNameNobody()
    {
        BossBrain brain = new(3);
        uint[] players = [Host, Aria];
        long now = 0;
        brain.Next(now, 100, players);

        for (int i = 0; i < 40; i++)
        {
            now += 20_000;
            BossCue cue = brain.Next(now, 100, players)!.Value;
            Assert.True(cue.IsKnown);
            Assert.Equal(BossPowers.Strength(cue.Power), cue.Strength);
            if (BossPowers.Targeted(cue.Power))
            {
                Assert.Contains((uint)cue.TargetPeer, players);
            }
            else
            {
                Assert.Equal(0, cue.TargetPeer);
            }
        }
    }

    [Fact]
    public void WithHisHealthUnknownTheSnapStillComes()
    {
        BossBrain brain = new(11);
        uint[] players = [Host];
        long now = 0;
        brain.Next(now, -1, players);

        List<BossPower> used = [];
        for (int i = 0; i < 10; i++)
        {
            now += 20_000;
            used.Add(brain.Next(now, -1, players)!.Value.Power);
        }

        Assert.Equal(BossPower.Snap, used[2]);
        Assert.Equal(2, used.Count(p => p == BossPower.Snap));
    }

    // ---- what a power does, as the plugin plays it ------------------------------------------

    [Fact]
    public void ThePluginsNumbersAreTheWiresNumbers()
    {
        Assert.Equal((int)BossPower.Snap, ChungusRule.Snap);
        Assert.Equal((int)BossPower.Blast, ChungusRule.Blast);
        Assert.Equal((int)BossPower.Lift, ChungusRule.Lift);
        Assert.Equal((int)BossPower.Gizka, ChungusRule.Gizka);
        Assert.Equal((int)BossPower.Fright, ChungusRule.Fright);
        Assert.Equal((int)BossPower.Drain, ChungusRule.Drain);
        Assert.Equal((int)BossPower.Slow, ChungusRule.Slow);
        Assert.Equal((int)BossPower.Entrance, ChungusRule.Entrance);
        Assert.Equal((int)BossPower.Defeat, ChungusRule.Defeat);

        Assert.Equal(ChungusCheat.OpSpawn, ChungusRule.OpSpawn);
        Assert.Equal(ChungusCheat.OpWindUp, ChungusRule.OpWindUp);
        Assert.Equal(ChungusCheat.OpLand, ChungusRule.OpLand);
        Assert.Equal(ChungusCheat.OpDespawn, ChungusRule.OpDespawn);
        Assert.Equal(ChungusCheat.OpAnimation, ChungusRule.OpAnimation);
        Assert.Equal(ChungusCheat.OpSound, ChungusRule.OpSound);
        Assert.Equal(ChungusCheat.FlagHitsLocalPlayer, ChungusRule.FlagHitsLocalPlayer);
        Assert.Equal(ChungusCheat.FlagHost, ChungusRule.FlagHost);
        Assert.Equal(ChungusBlueprint.ResRef, ChungusRule.Template);
        Assert.Equal(KotorCoop.Memory.CheatKinds.Chungus, SharedBlockChungusKind);

        uint word = ChungusCheat.Word(new BossCue(BossPower.Blast, 30, 2), hitsLocalPlayer: true, isHost: false);
        Assert.Equal(ChungusRule.Blast, ChungusRule.PowerOf(word));
        Assert.Equal(30, ChungusRule.StrengthOf(word));
        Assert.NotEqual(0u, word & ChungusRule.FlagHitsLocalPlayer);
        Assert.Equal(0u, word & ChungusRule.FlagHost);
    }

    /// <summary>SharedBlock.CheatKindChungus: the plugin's SharedBlock cannot be linked here (it
    /// is the whole block layout), so the number is read out of its source.</summary>
    private static uint SharedBlockChungusKind
    {
        get
        {
            string source = File.ReadAllText(FindRepoFile("src", "KotorCoop.Plugin", "SharedBlock.cs"));
            const string marker = "public const uint CheatKindChungus = ";
            int at = source.IndexOf(marker, StringComparison.Ordinal);
            Assert.True(at >= 0, "SharedBlock.CheatKindChungus not found");
            int end = source.IndexOf(';', at);
            return uint.Parse(source.AsSpan(at + marker.Length, end - at - marker.Length));
        }
    }

    [Fact]
    public void TheSnapTakesHalfOfWhatIsLeftAndNeverTheLastPoint()
    {
        Assert.Equal(60, ChungusRule.Landing(ChungusRule.Snap, 50, 120, 200).Damage);
        Assert.Equal(1, ChungusRule.Landing(ChungusRule.Snap, 50, 3, 200).Damage);
        Assert.Equal(0, ChungusRule.Landing(ChungusRule.Snap, 50, 1, 200).Damage);
    }

    [Fact]
    public void EveryPowerHasAnAnimationASoundThatShipsAndSomethingToDo()
    {
        HashSet<string> shipped = [.. Directory.GetFiles(Path.GetDirectoryName(FindShipped("k_coop_chungus.mdl"))!, "k_coop_ch*.wav")
            .Select(path => Path.GetFileNameWithoutExtension(path))];
        HashSet<string> animations = [.. ReadAnimationNames(File.ReadAllBytes(FindShipped("k_coop_chungus.mdl")))];

        // The engine's creature rows of animations.2da (10000 + row), by the name it then looks for.
        Dictionary<ushort, string> rows = new()
        {
            [10260] = "cvictory", [10263] = "ctaunt", [10293] = "castout1", [10294] = "castout2",
            [10295] = "castout3", [10296] = "powerup",
        };

        for (int power = ChungusRule.Snap; power <= ChungusRule.Slow; power++)
        {
            ushort animation = ChungusRule.WindUpAnimation(power);
            Assert.True(rows.TryGetValue(animation, out string? name), $"power {power} has no animation");
            Assert.Contains(name, animations);

            BossLanding landing = ChungusRule.Landing(power, BossPowers.Strength((BossPower)power), 100, 200);
            Assert.True(landing.Damage > 0 || landing.Effect != 0, $"power {power} does nothing");
            Assert.True(BossPowers.LandDelayMs((BossPower)power) > 0);
        }

        for (int power = ChungusRule.Snap; power <= ChungusRule.Defeat; power++)
        {
            foreach (string sound in (string[])[ChungusRule.WindUpSound(power), ChungusRule.LandSound(power)])
            {
                Assert.True(sound.Length == 0 || shipped.Contains(sound), $"power {power}: sound '{sound}' does not ship");
                Assert.True(sound.Length <= 16);
            }
        }
    }

    // ---- words ------------------------------------------------------------------------------

    [Fact]
    public void EveryBossLineIsWritten()
    {
        foreach (PvpNoticeCode code in Enum.GetValues<PvpNoticeCode>().Where(c => c >= PvpNoticeCode.BossEntering && c != PvpNoticeCode.BossPower))
        {
            Assert.NotEqual(string.Empty, PvpText.Format(code, "Aria"));
            Assert.NotEqual(string.Empty, PvpText.Format(code, "Aria", aboutSelf: true));
        }

        foreach (BossPower power in Enum.GetValues<BossPower>().Where(p => p != BossPower.None))
        {
            BossCue cue = new(power, 0, 2);
            Assert.NotEqual(string.Empty, PvpText.FormatBossPower(cue, "Aria", aimedAtReader: false));
            Assert.NotEqual(string.Empty, PvpText.FormatBossPower(cue, "Aria", aimedAtReader: true));
        }

        Assert.Contains("Aria", PvpText.FormatBossPower(new BossCue(BossPower.Gizka, 0, 2), "Aria", aimedAtReader: false));
        Assert.DoesNotContain("Aria", PvpText.FormatBossPower(new BossCue(BossPower.Gizka, 0, 2), "Aria", aimedAtReader: true));
    }

    [Theory]
    [InlineData("chungus", null, 0)]
    [InlineData("chungus spawn", "spawn", 0)]
    [InlineData("chungus remove", "remove", 0)]
    [InlineData("chungus anim 10263", "anim", 10263)]
    [InlineData("chungus power 1", "power", 1)]
    [InlineData("chungus sound k_coop_chsnap", "sound", 0)]
    public void TheVerbAndItsStagingForms(string line, string? piece, int amount)
    {
        CoopCommand parsed = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.Chungus, parsed.Kind);
        Assert.Equal(piece, parsed.Address);
        Assert.Equal(amount, parsed.Amount);
    }

    [Theory]
    [InlineData("chungus now")]
    [InlineData("chungus anim")]
    [InlineData("chungus anim many")]
    [InlineData("chungus sound a_name_that_is_far_too_long")]
    public void AnythingElseAfterTheVerbIsExplained(string line)
    {
        CoopCommand parsed = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.Invalid, parsed.Kind);
        Assert.Equal(PvpText.BossUsageLine, parsed.Error);
    }

    // ---- blueprint and install --------------------------------------------------------------

    [Fact]
    public void TheBlueprintIsAHostileCreatureThatWearsTheGivenRow()
    {
        Gff utc = Gff.Parse(ChungusBlueprint.Build(509));

        Assert.Equal("UTC ", utc.FileType);
        Assert.True(utc.Root.TryGetResRef("TemplateResRef", out string resref));
        Assert.Equal("k_coop_chungus", resref);
        Assert.True(utc.Root.TryGetWord("Appearance_Type", out ushort row));
        Assert.Equal(509, row);
        Assert.True(utc.Root.TryGetWord("FactionID", out ushort faction));
        Assert.Equal(1, faction);

        // A plot creature is never mirrored to joiners, and a pinned one could not be beaten.
        Assert.True(utc.Root.TryGetByte("Plot", out byte plot));
        Assert.Equal(0, plot);
        Assert.True(utc.Root.TryGetByte("Min1HP", out byte pinned));
        Assert.Equal(0, pinned);

        Assert.True(utc.Root.TryGetResRef("ScriptHeartbeat", out string heartbeat));
        Assert.Equal("k_def_heartbt01", heartbeat);
        Assert.Equal(ChungusBlueprint.Build(509), ChungusBlueprint.Build(509));
        Assert.NotEqual(ChungusBlueprint.Build(509), ChungusBlueprint.Build(510));
    }

    private string WriteGame(params string[] extraRowLabels)
    {
        string overrideDirectory = Directory.CreateDirectory(Path.Combine(_temp, "Override")).FullName;
        TwoDaWriter table = new(["label", "race", "racetex", "modeltype", "walkdist", "rundist", "hitradius", "sizecategory"]);
        table.AddRow("0", ["P_MAL_A", "", "", "B", "1.6", "5.4", "0.25", "3"]);
        table.AddRow("1", ["Creature_Gizka", "C_Gizka", "", "S", "1.3", "2.6", "0.2", "3"]);
        foreach (string label in extraRowLabels)
        {
            table.AddRow(table.RowCount.ToString(), [label, "C_Other", "", "S", "1", "2", "0.5", "3"]);
        }

        File.WriteAllBytes(Path.Combine(overrideDirectory, "appearance.2da"), table.ToBytes());
        foreach (string shipped in ChungusInstaller.ShippedFiles)
        {
            File.WriteAllBytes(Path.Combine(overrideDirectory, shipped), [1, 2, 3]);
        }

        return overrideDirectory;
    }

    [Fact]
    public void InstallAddsHisRowKeepsEveryOtherAndWritesTheBlueprintForIt()
    {
        string overrideDirectory = WriteGame();

        ChungusInstallResult result = ChungusInstaller.Install(_temp);

        Assert.Equal(ChungusInstallOutcome.Installed, result.Outcome);
        Assert.Equal(2, result.AppearanceRow);
        TwoDa table = TwoDa.Parse(File.ReadAllBytes(Path.Combine(overrideDirectory, "appearance.2da")));
        Assert.Equal(3, table.RowCount);
        Assert.Equal("Creature_Gizka", table.Get(1, "label"));
        Assert.Equal("C_Gizka", table.Get(1, "race"));
        Assert.Equal(ChungusInstaller.AppearanceLabel, table.Get(2, "label"));
        Assert.Equal("k_coop_chungus", table.Get(2, "race"));
        Assert.Equal("S", table.Get(2, "modeltype"));
        Assert.Equal("4", table.Get(2, "sizecategory"));

        Gff utc = Gff.Parse(File.ReadAllBytes(Path.Combine(overrideDirectory, ChungusBlueprint.FileName)));
        Assert.True(utc.Root.TryGetWord("Appearance_Type", out ushort row));
        Assert.Equal(2, row);

        // Again: nothing to do, nothing rewritten.
        byte[] before = File.ReadAllBytes(Path.Combine(overrideDirectory, "appearance.2da"));
        ChungusInstallResult again = ChungusInstaller.Install(_temp);
        Assert.Equal(ChungusInstallOutcome.UpToDate, again.Outcome);
        Assert.Equal(2, again.AppearanceRow);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(overrideDirectory, "appearance.2da")));
    }

    [Fact]
    public void InstallSaysWhyWhenHisFilesAreNotThere()
    {
        string overrideDirectory = WriteGame();
        File.Delete(Path.Combine(overrideDirectory, "k_coop_chungus.mdl"));

        ChungusInstallResult result = ChungusInstaller.Install(_temp);

        Assert.Equal(ChungusInstallOutcome.Unavailable, result.Outcome);
        Assert.Contains("k_coop_chungus.mdl", result.Message);
        Assert.False(File.Exists(Path.Combine(overrideDirectory, ChungusBlueprint.FileName)));
    }

    [Fact]
    public void UninstallTakesHisRowOutAndLeavesTheRest()
    {
        string overrideDirectory = WriteGame();
        ChungusInstaller.Install(_temp);

        ChungusInstaller.Uninstall(_temp);

        TwoDa table = TwoDa.Parse(File.ReadAllBytes(Path.Combine(overrideDirectory, "appearance.2da")));
        Assert.Equal(2, table.RowCount);
        Assert.Equal("Creature_Gizka", table.Get(1, "label"));
    }

    [Fact]
    public void UninstallLeavesTheTableAloneWhenAnotherModAddedRowsAfterHis()
    {
        string overrideDirectory = WriteGame();
        ChungusInstaller.Install(_temp);
        string path = Path.Combine(overrideDirectory, "appearance.2da");
        TwoDaWriter later = new(TwoDa.Parse(File.ReadAllBytes(path)));
        later.AddRow("3", ["Another_Mod", "C_Mod", "", "S", "1", "2", "0.5", "3"]);
        File.WriteAllBytes(path, later.ToBytes());
        byte[] before = File.ReadAllBytes(path);

        string said = ChungusInstaller.Uninstall(_temp);

        Assert.Contains("left alone", said);
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    // ---- the files that ship ----------------------------------------------------------------

    /// <summary>The animations a single-model creature is asked for by the engine (the set the
    /// game's own c_gizka carries) plus the ones his powers and the status effects play.</summary>
    private static readonly string[] RequiredAnimations =
    [
        "cpause1", "cpause2", "creadyr", "cwalk", "crun", "cwalkinj", "g0a1", "g0a2", "cdodgeg", "cdamages",
        "cdie", "cdead", "ctaunt", "cvictory", "tlknorm", "chturnl", "chturnr",
        "castout1", "castout2", "castout3", "powerup", "choke", "horror", "sleep", "paralyzed", "whirlwind",
        "ckdbck", "ckdbcklp", "cgustandb", "cspasm",
    ];

    [Fact]
    public void TheShippedModelIsAWellFormedCreatureModel()
    {
        byte[] mdl = File.ReadAllBytes(FindShipped("k_coop_chungus.mdl"));
        byte[] mdx = File.ReadAllBytes(FindShipped("k_coop_chungus.mdx"));

        // File header: 0, the size of the rest of the .mdl, the size of the .mdx.
        Assert.Equal(0u, U32(mdl, 0));
        Assert.Equal((uint)mdl.Length - 12, U32(mdl, 4));
        Assert.Equal((uint)mdx.Length, U32(mdl, 8));

        // The K1 PC model header: the two engine addresses the loader checks, the name, a
        // character model.
        Assert.Equal(4273776u, U32(mdl, 12));
        Assert.Equal(4216096u, U32(mdl, 16));
        Assert.Equal("k_coop_chungus", CString(mdl, 20, 32));
        Assert.Equal(2, mdl[12 + 76]);
        Assert.Equal(4, mdl[12 + 80]);
        Assert.Equal((uint)mdx.Length, U32(mdl, 12 + 176));

        // Every offset stays inside the file.
        uint root = U32(mdl, 12 + 40);
        uint nodeCount = U32(mdl, 12 + 44);
        Assert.InRange(root, 196u, (uint)mdl.Length - 12 - 80);
        Assert.InRange(nodeCount, 10u, 100u);

        string[] animations = ReadAnimationNames(mdl);
        Assert.All(RequiredAnimations, name => Assert.Contains(name, animations));
        Assert.Equal(animations.Length, animations.Distinct().Count());
    }

    [Fact]
    public void TheShippedTextureAndSoundsAreInFormatsTheGameReads()
    {
        byte[] tga = File.ReadAllBytes(FindShipped("k_coop_chungus.tga"));
        Assert.Equal(2, tga[2]);                                             // uncompressed true colour
        int width = BinaryPrimitives.ReadUInt16LittleEndian(tga.AsSpan(12));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(tga.AsSpan(14));
        Assert.Equal((512, 512), (width, height));                           // a power of two
        Assert.Equal(24, tga[16]);
        Assert.Equal(18 + (width * height * 3), tga.Length);

        string directory = Path.GetDirectoryName(FindShipped("k_coop_chungus.mdl"))!;
        string[] sounds = Directory.GetFiles(directory, "k_coop_ch*.wav");
        Assert.True(sounds.Length >= 8);
        foreach (string path in sounds)
        {
            byte[] wav = File.ReadAllBytes(path);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
            Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20)));    // PCM
            Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22)));    // mono
            Assert.Equal(16, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34)));   // 16 bit
            Assert.True(Path.GetFileNameWithoutExtension(path).Length <= 16, path);
        }
    }

    [Fact]
    public void EveryShippedBossFileHasThePrefixTheInstallerCopies()
    {
        foreach (string shipped in ChungusInstaller.ShippedFiles)
        {
            Assert.StartsWith("k_coop_", shipped);
            Assert.True(File.Exists(FindShipped(shipped)));
        }
    }

    private static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));

    private static string CString(byte[] data, int offset, int max)
    {
        int length = 0;
        while (length < max && data[offset + length] != 0)
        {
            length++;
        }

        return Encoding.ASCII.GetString(data, offset, length);
    }

    /// <summary>The names in the model's animation table (offsets are from byte 12).</summary>
    private static string[] ReadAnimationNames(byte[] mdl)
    {
        uint table = U32(mdl, 12 + 88);
        uint count = U32(mdl, 12 + 92);
        string[] names = new string[count];
        for (int i = 0; i < count; i++)
        {
            uint animation = U32(mdl, 12 + (int)table + (4 * i));
            Assert.Equal(5, mdl[12 + (int)animation + 76]);   // an animation header
            names[i] = CString(mdl, 12 + (int)animation + 8, 32);
        }

        return names;
    }

    private static string FindShipped(string fileName) => FindRepoFile("profiles", "override", fileName);

    private static string FindRepoFile(params string[] parts)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string candidate = Path.Combine([directory.FullName, .. parts]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        Assert.Fail($"not found in the repository: {Path.Combine(parts)}");
        return string.Empty;
    }
}
