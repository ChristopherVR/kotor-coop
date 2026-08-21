using KotorCoop.Agent;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The staging instrument for docs/research/effect-sync.md's live list, which had never been
/// run: no reachable test module has a Force-using hostile, and until <c>applyeffect</c> existed
/// there was no way to apply a non-damage effect on demand at all. This covers the three things
/// a live pass has to trust without a human watching: the parser's validation, that a valid line
/// actually reaches the plugin with the right fields, and that <c>effects</c>' read-only
/// formatter reports the pipeline honestly.
/// </summary>
public class EffectVerbsTests
{
    /// <summary>A game that records the cheats filed with it. Deliberately NOT a
    /// <c>KotorGameBridge</c> — <c>effects</c> reads the block rings only through that concrete
    /// type, so a plain <see cref="IGameBridge"/> fake is what exercises the "no game to read"
    /// refusal.</summary>
    private sealed class EffectBridge : IGameBridge
    {
        public readonly List<CheatRequest> Requests = [];

        public string Description => "effect-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsCheats => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public List<EnemyState> ReadEnemies(IReadOnlyList<Vector3f> otherPlayers) => [];

        public bool TryRequestCheat(CheatRequest request, out uint serial)
        {
            Requests.Add(request);
            serial = (uint)Requests.Count;
            return true;
        }

        public bool TryReadCheatAnswer(out uint applied, out int status, out string detail)
        {
            applied = 0;
            status = 0;
            detail = string.Empty;
            return false;
        }

        public void Dispose()
        {
        }
    }

    private static (CoopCommandDispatcher Dispatcher, EffectBridge Bridge, SessionManager Manager) NewDispatcher()
    {
        EffectBridge bridge = new();
        SessionManager manager = new(bridge, new AgentSettings { PollRate = 30 });
        CoopCommandDispatcher dispatcher = new(
            manager, bridge, 27140,
            password => new SessionSettings { PlayerName = "Tester", Port = 0, TickRate = 30, Password = password });

        return (dispatcher, bridge, manager);
    }

    private static void Type(CoopCommandDispatcher dispatcher, string line) =>
        dispatcher.Dispatch(ConsoleCommand.Parse(line));

    // ---- parsing / validation --------------------------------------------------------

    [Theory]
    [InlineData("stun", PeerEffectKind.Stun)]
    [InlineData("PARALYZE", PeerEffectKind.Paralyze)]
    [InlineData("Knockdown", PeerEffectKind.Knockdown)]
    [InlineData("slow", PeerEffectKind.Slow)]
    [InlineData("frightened", PeerEffectKind.Frightened)]
    [InlineData("sleep", PeerEffectKind.Sleep)]
    [InlineData("choke", PeerEffectKind.Choke)]
    [InlineData("forcepush", PeerEffectKind.ForcePush)]
    [InlineData("whirlwind", PeerEffectKind.Whirlwind)]
    public void EveryAllowlistKindParsesCaseInsensitively(string word, PeerEffectKind expected)
    {
        CoopCommand command = ConsoleCommand.Parse($"applyeffect {word}");

        Assert.Equal(CoopCommandKind.ApplyEffect, command.Kind);
        Assert.Equal((int)expected, command.Amount);
        Assert.Null(command.Address);
    }

    /// <summary>A bare line names no kind at all, and "none" is not a kind a player can ask
    /// for — both must refuse rather than silently doing nothing.</summary>
    [Theory]
    [InlineData("applyeffect")]
    [InlineData("applyeffect none")]
    [InlineData("applyeffect zap")]
    [InlineData("applyeffect stun 0x35 4000 60 extra")]
    public void AnUnknownOrMalformedLineIsInvalid(string line)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.Invalid, command.Kind);
        Assert.NotEqual(string.Empty, command.Error);
    }

    [Fact]
    public void ABareKindGetsTheDefaultDurationAndZeroMagnitude()
    {
        CoopCommand command = ConsoleCommand.Parse("applyeffect stun");

        Assert.Equal(CoopCommandKind.ApplyEffect, command.Kind);
        Assert.Equal(6000u, command.EffectDurationMs);
        Assert.Equal(0u, command.EffectMagnitude);
        Assert.Null(command.Address);
    }

    [Fact]
    public void AnExplicitTargetDurationAndMagnitudeAreCarried()
    {
        CoopCommand command = ConsoleCommand.Parse("applyeffect slow 0x35 4000 60");

        Assert.Equal(CoopCommandKind.ApplyEffect, command.Kind);
        Assert.Equal("0x35", command.Address);
        Assert.Equal(4000u, command.EffectDurationMs);
        Assert.Equal(60u, command.EffectMagnitude);
    }

    /// <summary>`me`/`self` resolve to the player's own object id — always 0x7FFFFFFF on every
    /// instance — so a constructor can be smoke-tested locally without knowing your own id.</summary>
    [Theory]
    [InlineData("applyeffect choke me")]
    [InlineData("applyeffect choke self")]
    public void SelfWordsResolveToThePlayerObjectId(string line)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.ApplyEffect, command.Kind);
        Assert.Equal("0x7FFFFFFF", command.Address);
    }

    [Fact]
    public void ATargetThatIsNotAnObjectIdIsInvalid()
    {
        CoopCommand command = ConsoleCommand.Parse("applyeffect stun not-an-id");

        Assert.Equal(CoopCommandKind.Invalid, command.Kind);
    }

    [Theory]
    [InlineData("applyeffect stun 0x35 nope")]
    [InlineData("applyeffect stun 0x35 4000 nope")]
    public void ANonNumericDurationOrMagnitudeIsInvalid(string line)
    {
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse(line).Kind);
    }

    [Fact]
    public void EffectsTakesNoArguments()
    {
        Assert.Equal(CoopCommandKind.Effects, ConsoleCommand.Parse("effects").Kind);

        CoopCommand withArgs = ConsoleCommand.Parse("effects 3");
        Assert.Equal(CoopCommandKind.Invalid, withArgs.Kind);
    }

    // ---- dispatcher routing -----------------------------------------------------------

    [Fact]
    public void ABlankTargetReachesThePluginAsZero()
    {
        (CoopCommandDispatcher dispatcher, EffectBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, "applyeffect stun");

            CheatRequest request = Assert.Single(bridge.Requests);
            Assert.Equal(CheatKinds.ApplyEffect, request.Kind);
            Assert.Equal(0u, request.Target);
            Assert.Equal((int)PeerEffectKind.Stun, request.Amount);
            Assert.Equal(6000u, request.Charges);
            Assert.Equal(0u, request.MaxCharges);
        }
    }

    [Fact]
    public void AnExplicitTargetDurationAndMagnitudeReachThePlugin()
    {
        (CoopCommandDispatcher dispatcher, EffectBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, "applyeffect slow 0x35 4000 60");

            CheatRequest request = Assert.Single(bridge.Requests);
            Assert.Equal(CheatKinds.ApplyEffect, request.Kind);
            Assert.Equal(0x35u, request.Target);
            Assert.Equal((int)PeerEffectKind.Slow, request.Amount);
            Assert.Equal(4000u, request.Charges);
            Assert.Equal(60u, request.MaxCharges);
        }
    }

    /// <summary>Knockdown/Paralyze/Sleep now have real apply-side constructors
    /// (effect-sync.md §7) — pinned as an explicit dispatcher-routing check for the three
    /// kinds this pass added, not just the generic parser theory above.</summary>
    [Theory]
    [InlineData("applyeffect knockdown", PeerEffectKind.Knockdown)]
    [InlineData("applyeffect paralyze", PeerEffectKind.Paralyze)]
    [InlineData("applyeffect sleep", PeerEffectKind.Sleep)]
    public void KnockdownParalyzeAndSleepReachThePluginWithTheRightKind(string line, PeerEffectKind expected)
    {
        (CoopCommandDispatcher dispatcher, EffectBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, line);

            CheatRequest request = Assert.Single(bridge.Requests);
            Assert.Equal(CheatKinds.ApplyEffect, request.Kind);
            Assert.Equal((int)expected, request.Amount);
        }
    }

    [Fact]
    public void MeResolvesToThePlayerObjectIdAtTheDispatcher()
    {
        (CoopCommandDispatcher dispatcher, EffectBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, "applyeffect choke me");

            CheatRequest request = Assert.Single(bridge.Requests);
            Assert.Equal(0x7FFFFFFFu, request.Target);
        }
    }

    /// <summary>A line the parser refused must never become an engine call.</summary>
    [Theory]
    [InlineData("applyeffect")]
    [InlineData("applyeffect zap")]
    [InlineData("applyeffect stun not-an-id")]
    public void ARefusedLineNeverReachesThePlugin(string line)
    {
        (CoopCommandDispatcher dispatcher, EffectBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            Type(dispatcher, line);
            Assert.Empty(bridge.Requests);
        }
    }

    /// <summary><c>effects</c> needs a real game to read the block rings from, and says so
    /// rather than silently printing nothing — the same rule <c>items</c>/<c>item</c> follow.</summary>
    [Fact]
    public void EffectsWithoutAGameRefusesOutLoud()
    {
        (CoopCommandDispatcher dispatcher, EffectBridge bridge, SessionManager manager) = NewDispatcher();
        using (manager)
        {
            List<string> logged = [];
            dispatcher.Log = logged.Add;

            Type(dispatcher, "effects");

            Assert.Contains(logged, line => line.Contains("--kotor", StringComparison.Ordinal));
            Assert.Empty(bridge.Requests);
        }
    }

    // ---- the `effects` formatter (PeerEffectLog) --------------------------------------

    [Fact]
    public void NewCapturesAndAppliesAreBothRecorded()
    {
        PeerEffectLog log = new();
        DateTime t0 = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);

        log.Observe(
            captured: [new PeerEffectRecord(0x35, (uint)PeerEffectKind.Stun, 6000, 0, 1)],
            applied: [new PeerEffectRecord(0x2, (uint)PeerEffectKind.Slow, 4000, 60, 1)],
            now: t0);

        Assert.Equal(2, log.Recent.Count);
        Assert.Contains(log.Recent, e => e.Category == "captured/forwarded" && e.SubjectId == 0x35);
        Assert.Contains(log.Recent, e => e.Category == "applied" && e.SubjectId == 0x2);
    }

    /// <summary>A slot re-read with the SAME serial (nothing new written since the last poll)
    /// must not be recorded twice.</summary>
    [Fact]
    public void ARepeatedSerialIsNotRecordedAgain()
    {
        PeerEffectLog log = new();
        DateTime t0 = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
        PeerEffectRecord record = new(0x35, (uint)PeerEffectKind.Stun, 6000, 0, 1);

        log.Observe([record], [], t0);
        log.Observe([record], [], t0.AddSeconds(1));

        Assert.Single(log.Recent);
    }

    [Fact]
    public void OnlyTheLastEightAreKept()
    {
        PeerEffectLog log = new();
        DateTime t0 = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);

        for (int i = 1; i <= 12; i++)
        {
            log.Observe(
                captured: [new PeerEffectRecord(0x35, (uint)PeerEffectKind.Stun, 6000, 0, (uint)i)],
                applied: [],
                now: t0.AddSeconds(i));
        }

        Assert.Equal(PeerEffectLog.Capacity, log.Recent.Count);

        // Newest first, and the oldest four (serials 1-4) fell off the end.
        Assert.Equal(12u, log.Recent[0].Serial);
        Assert.DoesNotContain(log.Recent, e => e.Serial <= 4);
    }

    /// <summary>
    /// A module change resets the capturing hook's own sequence counter to zero, so the ring's
    /// highest serial can legitimately drop below what was already remembered. That must be
    /// read as a reset, not as "nothing new" — the failure mode this pins is `effects` going
    /// silent for a whole fresh ring's worth of captures after every transition.
    /// </summary>
    [Fact]
    public void ASerialDropIsTreatedAsAResetNotAsStaleData()
    {
        PeerEffectLog log = new();
        DateTime t0 = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);

        log.Observe(
            captured: [new PeerEffectRecord(0x35, (uint)PeerEffectKind.Stun, 6000, 0, 12)],
            applied: [],
            now: t0);

        // A fresh module: the hook's sequence restarted at 1.
        log.Observe(
            captured: [new PeerEffectRecord(0x40, (uint)PeerEffectKind.Choke, 6000, 0, 1)],
            applied: [],
            now: t0.AddSeconds(5));

        Assert.Contains(log.Recent, e => e.SubjectId == 0x40 && e.Serial == 1);
    }

    [Fact]
    public void FormatNamesTheKindAndReportsHowLongAgo()
    {
        DateTime observedAt = new(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
        PeerEffectLog.Entry entry = new(
            "captured/forwarded", 0x35, (uint)PeerEffectKind.Whirlwind, 6000, 0, 7, observedAt);

        string line = PeerEffectLog.Format(entry, observedAt.AddSeconds(3.2));

        Assert.Contains("whirlwind", line, StringComparison.Ordinal);
        Assert.Contains("0x35", line, StringComparison.Ordinal);
        Assert.Contains("serial 7", line, StringComparison.Ordinal);
        Assert.Contains("3.2s ago", line, StringComparison.Ordinal);
    }

    /// <summary>A kind number outside the allowlist (a capture-only sub-code the sender did not
    /// map, or simple corruption) is reported rather than crashing the formatter.</summary>
    [Fact]
    public void AnUnknownKindNumberFormatsWithoutThrowing()
    {
        DateTime now = DateTime.UtcNow;
        PeerEffectLog.Entry entry = new("applied", 0x2, 200, 0, 0, 1, now);

        string line = PeerEffectLog.Format(entry, now);

        Assert.Contains("kind200", line, StringComparison.Ordinal);
    }
}
