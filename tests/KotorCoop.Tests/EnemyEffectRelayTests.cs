using System.Net;
using System.Text.RegularExpressions;
using KotorCoop.Memory;
using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The client-cast enemy-effect relay (docs/research/enemy-effects.md, coop.ini
/// <c>enemy_effect_sync</c>): the wire codec and its numbering, the forwardable-kind vocabulary
/// (and the three copies of it agreeing), the reliable sender, the plugin's throttle and apply
/// rules, the mailbox packing (plugin copy == agent copy), the block-layout agreement between
/// the two halves, and a real host/client loopback of the reliable report.
/// </summary>
public class EnemyEffectRelayTests
{
    // ---- codec ------------------------------------------------------------------------

    [Fact]
    public void TheEffectRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        EnemyEffectPacket original = new(3, 9, "tar_m03aa", 0x14, PeerEffectKind.Stunned, 4_500, 0);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);

        Assert.Equal(original, PacketCodec.ReadEnemyEffect(buffer[..length]));
    }

    [Fact]
    public void TheAckRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        EnemyEffectAckPacket original = new(PeerId: 1, OriginPeerId: 3, OpSequence: 9);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);

        Assert.Equal(original, PacketCodec.ReadEnemyEffectAck(buffer[..length]));
    }

    [Fact]
    public void AReadClampsTheDurationAndMagnitude()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(
            buffer, new EnemyEffectPacket(3, 1, "m", 5, PeerEffectKind.Slow, uint.MaxValue, uint.MaxValue));

        EnemyEffectPacket? read = PacketCodec.ReadEnemyEffect(buffer[..length]);
        Assert.NotNull(read);
        Assert.Equal(EnemyEffectRules.MaxDurationMs, read.DurationMs);
        Assert.Equal(EnemyEffectRules.MaxMagnitude, read.Magnitude);

        length = PacketCodec.Write(buffer, new EnemyEffectPacket(3, 2, "m", 5, PeerEffectKind.Stun, 0, 0));
        Assert.Equal(EnemyEffectRules.DefaultDurationMs, PacketCodec.ReadEnemyEffect(buffer[..length])!.DurationMs);
    }

    [Theory]
    [InlineData(PeerEffectKind.Heal)]
    [InlineData(PeerEffectKind.TemporaryHitpoints)]
    [InlineData(PeerEffectKind.Poison)]
    [InlineData(PeerEffectKind.Disguise)]
    [InlineData(PeerEffectKind.None)]
    public void AKindTheRelayDoesNotForwardIsRefusedOnRead(PeerEffectKind kind)
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new EnemyEffectPacket(3, 1, "m", 5, kind, 1000, 0));

        Assert.Null(PacketCodec.ReadEnemyEffect(buffer[..length]));
    }

    [Fact]
    public void AnEmptyEnemyIdOrAWrongPacketTypeIsRefused()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new EnemyEffectPacket(3, 1, "m", EnemyId: 0, PeerEffectKind.Stun, 1000, 0));
        Assert.Null(PacketCodec.ReadEnemyEffect(buffer[..length]));

        length = PacketCodec.Write(buffer, new EnemyEffectAckPacket(1, 2, 3));
        Assert.Null(PacketCodec.ReadEnemyEffect(buffer[..length]));
        Assert.Null(PacketCodec.ReadEnemyEffectAck(buffer[..PacketCodec.Write(buffer, new EnemyEffectPacket(1, 1, "m", 5, PeerEffectKind.Stun, 1, 0))]));
    }

    [Fact]
    public void ATruncatedPacketIsRefused()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new EnemyEffectPacket(3, 1, "m", 5, PeerEffectKind.Stun, 1000, 0));

        Assert.Null(PacketCodec.ReadEnemyEffect(buffer[..(length - 3)]));
    }

    [Fact]
    public void ThePacketTypesAndVersionAreWhereTheChangelogSaysTheyAre()
    {
        Assert.Equal(84, (byte)PacketType.EnemyEffect);
        Assert.Equal(85, (byte)PacketType.EnemyEffectAck);
        Assert.True(ProtocolConstants.Version >= 58, "the EnemyEffect packets arrived with protocol 58");
    }

    // ---- vocabulary: three copies of one list ------------------------------------------

    [Fact]
    public void TheForwardableSetIsTheTwelveCombatKindsAndOnlyThose()
    {
        PeerEffectKind[] forwardable =
        [
            PeerEffectKind.Stun, PeerEffectKind.Paralyze, PeerEffectKind.Knockdown, PeerEffectKind.Slow,
            PeerEffectKind.Frightened, PeerEffectKind.Sleep, PeerEffectKind.Choke, PeerEffectKind.ForcePush,
            PeerEffectKind.Whirlwind, PeerEffectKind.Confused, PeerEffectKind.Fear, PeerEffectKind.Stunned,
        ];

        for (uint kind = 0; kind <= 40; kind++)
        {
            bool expected = forwardable.Contains((PeerEffectKind)kind);
            Assert.Equal(expected, EnemyEffectRules.IsForwardable((PeerEffectKind)kind));
            Assert.Equal(expected, EnemyEffectRules.IsForwardable(kind));

            // The plugin's copy, which cannot reference Protocol, must agree exactly.
            Assert.Equal(expected, EnemyEffectMailboxRule.IsForwardable(kind));
        }
    }

    [Fact]
    public void EveryForwardableKindHasAnApplyConstructorAndNoneNeedsItsOwnGate()
    {
        for (uint kind = 1; kind <= 16; kind++)
        {
            PeerEffectKind k = (PeerEffectKind)kind;
            if (EnemyEffectRules.IsForwardable(k))
            {
                Assert.True(PeerEffectRules.HasApplyConstructor(k), $"{k} has no apply-side constructor");
                Assert.False(PeerEffectRules.NeedsOwnApplyGate(k), $"{k} needs poison_sync/disguise_sync");
            }
        }
    }

    [Fact]
    public void ThePluginCopiesOfTheKindValuesMatchTheProtocolEnum()
    {
        Assert.Equal((uint)PeerEffectKind.Stun, PeerEffectKindValues.Stun);
        Assert.Equal((uint)PeerEffectKind.Whirlwind, PeerEffectKindValues.Whirlwind);
        Assert.Equal((uint)PeerEffectKind.Confused, PeerEffectKindValues.Confused);
        Assert.Equal((uint)PeerEffectKind.Stunned, PeerEffectKindValues.Stunned);
        Assert.Equal((uint)PeerEffectKind.Poison, PeerEffectKindValues.Poison);
    }

    // ---- reliable sender ----------------------------------------------------------------

    [Fact]
    public void AReportIsNumberedAndRetransmittedUntilTheHostAcks()
    {
        EnemyEffectSender sender = new(retransmitIntervalMs: 100, maxIntervalMs: 100);

        EnemyEffectPacket? packet = sender.EnqueueLocal(1, "tar_m03aa", 20, PeerEffectKind.Stunned, 3000, 0, nowMs: 0);
        Assert.NotNull(packet);
        Assert.Equal(1u, packet.OpSequence);

        Assert.Single(sender.CollectDue(0));
        Assert.Empty(sender.CollectDue(50)); // inside the backoff
        Assert.Single(sender.CollectDue(200));

        sender.Acknowledge(1, 1);
        Assert.Empty(sender.CollectDue(400));
        Assert.Equal(0, sender.PendingCount);
    }

    [Fact]
    public void ABuffAKindOrAnEmptyIdIsRefusedAtEnqueue()
    {
        EnemyEffectSender sender = new();
        Assert.Null(sender.EnqueueLocal(1, "m", 20, PeerEffectKind.Heal, 3000, 0, 0));
        Assert.Null(sender.EnqueueLocal(1, "m", 20, PeerEffectKind.Poison, 3000, 0, 0));
        Assert.Null(sender.EnqueueLocal(1, "m", 0, PeerEffectKind.Stun, 3000, 0, 0));
        Assert.Equal(0, sender.PendingCount);
    }

    [Fact]
    public void AnUnackedReportGoesStaleAndTheTableIsBounded()
    {
        EnemyEffectSender sender = new(maxPending: 3, maxAgeMs: 1000);

        Assert.NotNull(sender.EnqueueLocal(1, "m", 1, PeerEffectKind.Stun, 1000, 0, 0));
        Assert.NotNull(sender.EnqueueLocal(1, "m", 2, PeerEffectKind.Stun, 1000, 0, 0));
        Assert.NotNull(sender.EnqueueLocal(1, "m", 3, PeerEffectKind.Stun, 1000, 0, 0));
        Assert.Null(sender.EnqueueLocal(1, "m", 4, PeerEffectKind.Stun, 1000, 0, 0)); // full

        Assert.Equal(3, sender.CollectDue(0).Count);

        // Well past its useful life: retired, not retransmitted for ever.
        Assert.Empty(sender.CollectDue(5000));
        Assert.Equal(0, sender.PendingCount);
    }

    // ---- throttle: a spam of casts cannot flood the host ----------------------------

    [Fact]
    public void TheSameEnemyAndKindWaitsOutTheRepeatCooldown()
    {
        EnemyEffectThrottleRule rule = new();
        uint stun = PeerEffectKindValues.Stunned;

        Assert.True(rule.TryAdmit(20, stun, 0));
        Assert.False(rule.TryAdmit(20, stun, 500));
        Assert.False(rule.TryAdmit(20, stun, EnemyEffectThrottleRule.MinRepeatMs - 1));
        Assert.True(rule.TryAdmit(20, stun, EnemyEffectThrottleRule.MinRepeatMs));
    }

    [Fact]
    public void ADifferentKindOrEnemyIsNotHeldByAnotherOnesCooldown()
    {
        EnemyEffectThrottleRule rule = new();

        Assert.True(rule.TryAdmit(20, PeerEffectKindValues.Stunned, 0));
        Assert.True(rule.TryAdmit(20, PeerEffectKindValues.Slow, 0));
        Assert.True(rule.TryAdmit(21, PeerEffectKindValues.Stunned, 0));
    }

    [Fact]
    public void OneEnemyIsCappedPerWindowSoAStunlockSpamCannotFloodTheHost()
    {
        EnemyEffectThrottleRule rule = new();
        uint[] kinds = [1, 2, 3, 4, 5, 6, 7, 8, 9, 12];

        int admitted = 0;
        for (int i = 0; i < kinds.Length; i++)
        {
            if (rule.TryAdmit(20, kinds[i], nowMs: i)) // distinct kinds, all inside one window
            {
                admitted++;
            }
        }

        Assert.Equal(EnemyEffectThrottleRule.MaxPerWindow, admitted);

        // A new window opens the enemy again.
        Assert.True(rule.TryAdmit(20, 1, EnemyEffectThrottleRule.WindowMs + 10));
    }

    [Fact]
    public void ARefusedBurstDoesNotExtendItsOwnCooldown()
    {
        EnemyEffectThrottleRule rule = new();

        Assert.True(rule.TryAdmit(20, 1, 0));
        for (long t = 100; t < 1000; t += 100)
        {
            Assert.False(rule.TryAdmit(20, 1, t));
        }

        Assert.True(rule.TryAdmit(20, 1, 1000)); // measured from the ADMITTED cast, not the last try
    }

    [Fact]
    public void ManyDistinctEnemiesNeverThrowAndClearForgetsThem()
    {
        EnemyEffectThrottleRule rule = new();
        for (uint enemy = 1; enemy <= 200; enemy++)
        {
            Assert.True(rule.TryAdmit(enemy, 1, enemy));
        }

        rule.Clear();
        Assert.True(rule.TryAdmit(200, 1, 201)); // would be inside the cooldown had it been kept
        Assert.False(rule.TryAdmit(0, 1, 500));  // an empty id is never admitted
    }

    // ---- apply: what the host does with a report --------------------------------------

    [Fact]
    public void ALiveForeignCreatureOfAForwardableKindIsApplied()
    {
        Assert.Equal(
            EnemyEffectApplyVerdict.Apply,
            EnemyEffectApplyRule.Decide(PeerEffectKindValues.Stunned, true, true, true, false, true));
    }

    [Fact]
    public void TheHostRefusesEverythingItShouldNotTouch()
    {
        uint stun = PeerEffectKindValues.Stunned;

        Assert.Equal(EnemyEffectApplyVerdict.RefuseKind,
            EnemyEffectApplyRule.Decide(PeerEffectKindValues.Heal, true, true, true, false, true));
        Assert.Equal(EnemyEffectApplyVerdict.RefuseKind,
            EnemyEffectApplyRule.Decide(PeerEffectKindValues.Poison, true, true, true, false, true));
        Assert.Equal(EnemyEffectApplyVerdict.RefuseUnresolved,
            EnemyEffectApplyRule.Decide(stun, false, false, false, false, true));
        Assert.Equal(EnemyEffectApplyVerdict.RefuseNotCreature,
            EnemyEffectApplyRule.Decide(stun, true, false, false, false, true));
        Assert.Equal(EnemyEffectApplyVerdict.RefuseOurBody,
            EnemyEffectApplyRule.Decide(stun, true, true, true, isOurBody: true, true));
        Assert.Equal(EnemyEffectApplyVerdict.RefuseDead,
            EnemyEffectApplyRule.Decide(stun, true, true, alive: false, false, true));
        Assert.Equal(EnemyEffectApplyVerdict.RefuseThrottled,
            EnemyEffectApplyRule.Decide(stun, true, true, true, false, throttleAdmits: false));
    }

    [Fact]
    public void ANameOfOneOfTheHostsOwnBodiesIsRefusedBeforeItCouldBeStunned()
    {
        // A modified client naming the host's own character must lose even when the body is
        // alive, a creature and inside the throttle.
        Assert.Equal(
            EnemyEffectApplyVerdict.RefuseOurBody,
            EnemyEffectApplyRule.Decide(PeerEffectKindValues.Paralyze, true, true, true, isOurBody: true, throttleAdmits: true));
    }

    // ---- mailbox packing: plugin copy == agent copy ----------------------------------

    [Fact]
    public void TheMailboxPackingRoundTripsAndThePluginCopyAgreesWithTheAgentCopy()
    {
        uint[] durations = [0, 1, 99, 100, 101, 6_000, 59_950, 60_000];
        uint[] magnitudes = [0, 1, 50, 100, 0xFFFF];

        foreach (uint duration in durations)
        {
            foreach (uint magnitude in magnitudes)
            {
                uint agentArgs = EnemyEffectMailbox.PackArgs(duration, magnitude);
                Assert.Equal(agentArgs, EnemyEffectMailboxRule.PackArgs(duration, magnitude));
                Assert.Equal(EnemyEffectMailbox.DurationMsOf(agentArgs), EnemyEffectMailboxRule.DurationMsOf(agentArgs));
                Assert.Equal(magnitude, EnemyEffectMailbox.MagnitudeOf(agentArgs));

                // Rounded UP to the 100 ms unit, never below what was captured.
                Assert.True(EnemyEffectMailbox.DurationMsOf(agentArgs) >= duration);
                Assert.True(EnemyEffectMailbox.DurationMsOf(agentArgs) - duration < 100);
            }
        }

        foreach (uint serial in new uint[] { 1, 2, 255, 256, 0xFFFFFE, 0xFFFFFF })
        {
            foreach (uint kind in new uint[] { 1, 9, 12, 14 })
            {
                uint head = EnemyEffectMailbox.PackHead(serial, kind);
                Assert.Equal(head, EnemyEffectMailboxRule.PackHead(serial, kind));
                Assert.Equal(serial, EnemyEffectMailbox.SerialOf(head));
                Assert.Equal(serial, EnemyEffectMailboxRule.SerialOf(head));
                Assert.Equal(kind, EnemyEffectMailbox.KindOf(head));
                Assert.Equal(kind, EnemyEffectMailboxRule.KindOf(head));
            }
        }
    }

    [Fact]
    public void TheSerialWrapsAroundZeroAndAgreesBetweenTheCopies()
    {
        Assert.Equal(1u, EnemyEffectMailbox.NextSerial(0));
        Assert.Equal(2u, EnemyEffectMailbox.NextSerial(1));
        Assert.Equal(1u, EnemyEffectMailbox.NextSerial(EnemyEffectMailbox.SerialMask));

        foreach (uint serial in new uint[] { 0, 1, 0xFFFFFE, 0xFFFFFF })
        {
            Assert.Equal(EnemyEffectMailbox.NextSerial(serial), EnemyEffectMailboxRule.NextSerial(serial));
        }
    }

    [Fact]
    public void ThePluginsDurationClampMatchesTheProtocolsSoTheAgentNeverSeesAnOutOfRangeReport()
    {
        foreach (uint duration in new uint[] { 0, 1, 6_000, 60_000, 60_001, uint.MaxValue })
        {
            Assert.Equal(EnemyEffectRules.ClampDurationMs(duration), EnemyEffectMailboxRule.ClampDurationMs(duration));
        }
    }

    // ---- block layout: both halves agree --------------------------------------------

    [Fact]
    public void TheEnemyEffectRegionIsDeclaredIdenticallyInBothHalvesAndFitsItsSlice()
    {
        string root = RepositoryRoot();
        string plugin = File.ReadAllText(Path.Combine(root, "src", "KotorCoop.Plugin", "SharedBlock.EnemyEffect.cs"));
        string bridge = File.ReadAllText(Path.Combine(root, "src", "KotorCoop.Memory", "KotorGameBridge.PluginBlock.cs"));

        string[] names = ["OutEnemy", "OutHead", "OutArgs", "InEnemy", "InHead", "InArgs", "OutAck", "InApplied"];
        HashSet<int> seen = [];
        foreach (string name in names)
        {
            Match p = Regex.Match(plugin, $@"OffsetEnemyEffect{name}\s*=\s*0x([0-9A-Fa-f]+);");
            Match b = Regex.Match(bridge, $@"BlockEnemyEffect{name}\s*=\s*0x([0-9A-Fa-f]+);");
            Assert.True(p.Success, $"plugin half lacks OffsetEnemyEffect{name}");
            Assert.True(b.Success, $"agent half lacks BlockEnemyEffect{name}");

            int offset = Convert.ToInt32(p.Groups[1].Value, 16);
            Assert.Equal(offset, Convert.ToInt32(b.Groups[1].Value, 16));
            Assert.True(offset >= 0x4C60 && offset + 4 <= 0x4C80, $"{name} at 0x{offset:X} is outside this stream's 0x4C60..0x4C80");
            Assert.True(seen.Add(offset), $"{name} at 0x{offset:X} overlaps another field");
        }

        Assert.True(0x4C80 <= SharedBlock.TotalSize);
    }

    // ---- host/client loopback ---------------------------------------------------------

    [Fact]
    public async Task AClientsEffectReachesTheHostReliablyAndOnlyTheHost()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient malak = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30, DamageIncarnation = 100 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));
        using CoopClient bastila = new(
            new SessionSettings { PlayerName = "Bastila", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(30));
        Task hostTask = host.RunAsync(cts.Token);
        Task malakTask = malak.RunAsync(cts.Token);
        Task bastilaTask = bastila.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "tar_m03aa", 100, 0);
        void Publish()
        {
            host.PublishWorldDigest(new WorldDigest(1, 1, state.Module, 1, NativeWorldEpoch: 3));
            malak.PublishWorldDigest(new WorldDigest(1, 1, state.Module, 1, NativeWorldEpoch: 5));
            bastila.PublishWorldDigest(new WorldDigest(1, 1, state.Module, 1, NativeWorldEpoch: 6));
            host.PublishLocalState(state);
            malak.PublishLocalState(state);
            bastila.PublishLocalState(state);
        }

        Assert.True(await WaitForAsync(() => malak.IsConnected && bastila.IsConnected && malak.RemoteWorldDigest.NativeWorldEpoch == 3, TimeSpan.FromSeconds(15), Publish));

        EnemyEffectCaptureWorld source = malak.EnemyEffectCaptureSource;
        malak.PublishEnemyEffect("tar_m03aa", 0x14, PeerEffectKind.Stunned, 4_000, 0, 45, 5, 0, source.HostSession, source.HostLoad, source.HostEpoch, 100);

        List<EnemyEffectPacket> atHost = [];
        Assert.True(
            await WaitForAsync(
                () =>
                {
                    atHost.AddRange(host.DrainEnemyEffects());
                    return atHost.Count > 0;
                },
                TimeSpan.FromSeconds(15),
                Publish),
            "the host never received the enemy effect");

        EnemyEffectPacket received = Assert.Single(atHost);
        Assert.Equal(malak.LocalPeerId, received.OriginPeerId);
        Assert.Equal(0x14u, received.EnemyId);
        Assert.Equal(PeerEffectKind.Stunned, received.Kind);
        Assert.Equal("tar_m03aa", received.Module);

        // Retransmits racing the ack are deduplicated: keep pumping and nothing more arrives.
        await WaitForAsync(() => false, TimeSpan.FromSeconds(1.5), Publish);
        Assert.Empty(host.DrainEnemyEffects());

        // Exercise the actual UDP admission path: known peer ID is insufficient without its endpoint.
        byte[] effectBytes = new byte[ProtocolConstants.MaxPacketSize];
        using (UdpChannel stranger = new(0))
        {
            EnemyEffectPacket spoof = received with { OpSequence = 46 };
            int spoofLength = PacketCodec.Write(effectBytes, spoof);
            stranger.Send(new IPEndPoint(IPAddress.Loopback,host.ListenEndPoint.Port), effectBytes.AsSpan(0,spoofLength));
            await Task.Delay(150);
            Assert.Empty(host.DrainEnemyEffects());
        }
        UdpChannel admitted = (UdpChannel)typeof(CoopClient).GetField("_channel",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(malak)!;
        // A managed sender restart retry of the same native capture cannot apply twice.
        admitted.Send(new IPEndPoint(IPAddress.Loopback,host.ListenEndPoint.Port), effectBytes.AsSpan(0,PacketCodec.Write(effectBytes,received)));
        await Task.Delay(150);
        Assert.Empty(host.DrainEnemyEffects());
        // Same-module replacement recycles EnemyId 0x14. An old-namespace report from the
        // genuine admitted endpoint is rejected; the fresh namespace is admitted once.
        host.BeginEnemyDamageWorld("tar_m03aa",1,4);
        Assert.False(host.EnemyEffectWorldCurrent(received));
        admitted.Send(new IPEndPoint(IPAddress.Loopback,host.ListenEndPoint.Port),effectBytes.AsSpan(0,PacketCodec.Write(effectBytes,received with {OpSequence=46})));
        await Task.Delay(150);
        Assert.Empty(host.DrainEnemyEffects());
        EnemyEffectPacket fresh=received with {OpSequence=46,HostLoadSerial=1,HostNativeEpoch=4};
        admitted.Send(new IPEndPoint(IPAddress.Loopback,host.ListenEndPoint.Port),effectBytes.AsSpan(0,PacketCodec.Write(effectBytes,fresh)));
        Assert.True(await WaitForAsync(()=>{atHost.Clear();atHost.AddRange(host.DrainEnemyEffects());return atHost.Count>0;},TimeSpan.FromSeconds(2)));
        Assert.Equal(fresh,Assert.Single(atHost));
        // Further queued reports are discarded again if the world flips before drain.
        EnemyEffectPacket queued=fresh with {OpSequence=47};
        admitted.Send(new IPEndPoint(IPAddress.Loopback,host.ListenEndPoint.Port),effectBytes.AsSpan(0,PacketCodec.Write(effectBytes,queued)));
        await Task.Delay(150);
        host.BeginEnemyDamageWorld("tar_m03aa",2,5);
        Assert.Empty(host.DrainEnemyEffects());

        host.PublishWorldDigest(new WorldDigest(1,1,"tar_m03aa",1,LoadSerial:2,NativeWorldEpoch:5));
        Assert.True(await WaitForAsync(()=>malak.RemoteWorldDigest.NativeWorldEpoch==5,TimeSpan.FromSeconds(2)));
        // Empty capture-source publication never rebrands this old local world with the
        // new host tuple: only a real local epoch advance can establish a fresh pairing.
        Assert.Equal(default,malak.EnemyEffectCaptureSource);
        malak.PublishEnemyEffect("tar_m03aa",0x14,PeerEffectKind.Stunned,4000,0,48,5,0,source.HostSession,source.HostLoad,source.HostEpoch,100);
        await Task.Delay(150);
        Assert.Empty(host.DrainEnemyEffects());

        // Nothing is relayed onward: the other client learns of it through the enemy census.
        Assert.Empty(bastila.DrainEnemyEffects());

        // The host has nothing to report about itself.
        host.PublishEnemyEffect("tar_m03aa", 0x14, PeerEffectKind.Stunned, 4_000, 0);
        Assert.Empty(host.DrainEnemyEffects());

        cts.Cancel();
        await Task.WhenAll(hostTask, malakTask, bastilaTask).WaitAsync(TimeSpan.FromSeconds(10)).ContinueWith(_ => { });
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout, Action? each = null)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            each?.Invoke();

            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KotorCoop.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}
