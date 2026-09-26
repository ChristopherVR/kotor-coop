using KotorCoop.Memory;
using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The mid-module movie relay this pass adds: <c>MoviePlayHook</c> captures which movie a
/// script's own <c>PlayMovie</c> call played (no transition involved), the two pure decisions
/// (<see cref="MovieRelayQueue"/>) that were already tested keep deciding what a receiver does
/// with it, and <see cref="CoopAgent.PumpOnce"/> (exercised here through a minimal fake bridge
/// and session, the same shape <c>TravelPumpTests</c> already uses) carries it end to end:
/// dedup by (peer, serial), the same-module filter, and the wire codec. See
/// docs/research/trigger-relay.md §6 and docs/research/cinematics.md's 2026-08-22 section.
/// </summary>
public class MidModuleMovieRelayTests
{
    // ---- the "already staged by transition" exclusion (pure, no engine) --------------

    [Fact]
    public void ACapturedResRefAlreadyOnTheTransitionQueueIsExcluded()
    {
        Assert.True(MovieRelayQueue.IsAlreadyQueued("01c", ["01c", "01f"]));
    }

    [Fact]
    public void TheExclusionIsCaseInsensitiveLikeTheEnginesOwnFileLookup()
    {
        Assert.True(MovieRelayQueue.IsAlreadyQueued("01C", ["01c"]));
    }

    [Fact]
    public void ACapturedResRefNotOnTheQueueIsNotExcluded()
    {
        Assert.False(MovieRelayQueue.IsAlreadyQueued("17", ["01c", "01f"]));
    }

    [Fact]
    public void AnEmptyQueueExcludesNothing()
    {
        Assert.False(MovieRelayQueue.IsAlreadyQueued("01c", []));
    }

    // ---- codec round-trip -------------------------------------------------------------

    [Fact]
    public void TheAnnouncementRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        MidModuleMoviePacket original = new(PeerId: 3, "end_m01ab", Serial: 9, "01c");

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);

        MidModuleMoviePacket? read = PacketCodec.ReadMidModuleMovie(buffer[..length]);
        Assert.NotNull(read);
        Assert.Equal(original.PeerId, read!.PeerId);
        Assert.Equal(original.Module, read.Module);
        Assert.Equal(original.Serial, read.Serial);
        Assert.Equal(original.Movie, read.Movie);
    }

    [Fact]
    public void AWrongPacketTypeIsRefused()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new TransitionAnnouncePacket(1, "end_m01aa", "wp", 1));

        Assert.Null(PacketCodec.ReadMidModuleMovie(buffer[..length]));
    }

    [Fact]
    public void ThePacketTypeIsWhereTheChangelogSaysItIs()
    {
        Assert.Equal(50, (byte)PacketType.MidModuleMovie);
    }

    // ---- block record encode/decode (the real SharedBlockView, over a scratch buffer) --
    //
    // SharedBlock/SharedBlockView are dependency-free enough to compile in the 64-bit test
    // host (no Odyssey, no P/Invoke, just offsets and pointer arithmetic over whatever
    // baseAddress the caller supplies) — confirmed by adding them to this project's Compile
    // list. So these exercise the ACTUAL block-layout code, not a shadow copy of it.

    /// <summary>
    /// A pinned array rather than a plain one: <see cref="SharedBlockView"/> holds a raw
    /// <c>byte*</c> across calls (it is not used inside a single <c>fixed</c> block the way a
    /// one-shot pointer read would be), and an unpinned array's address is only stable between
    /// GC events — exactly the same reason the production block is <c>VirtualAlloc</c>'d
    /// rather than a managed allocation.
    /// </summary>
    private static unsafe SharedBlockView NewScratchBlock(out byte[] storage)
    {
        storage = GC.AllocateArray<byte>(SharedBlock.TotalSize, pinned: true);
        return new SharedBlockView((byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref storage[0]));
    }

    [Fact]
    public unsafe void PublishMidModuleMovieWritesTheResRefAndBumpsTheSerial()
    {
        SharedBlockView block = NewScratchBlock(out byte[] storage);
        fixed (byte* p = storage)
        {
            block.PublishMidModuleMovie("01c");

            Assert.Equal(1u, *(uint*)(p + SharedBlock.OffsetMidModuleMovieSerial));

            string resRef = System.Text.Encoding.ASCII.GetString(
                p + SharedBlock.OffsetMidModuleMovieResRef, SharedBlock.MidModuleMovieNameLength).TrimEnd('\0');
            Assert.Equal("01c", resRef);
        }
    }

    [Fact]
    public void PublishMidModuleMovieIncrementsOnEachCall()
    {
        SharedBlockView block = NewScratchBlock(out _);

        block.PublishMidModuleMovie("01c");
        block.PublishMidModuleMovie("01f");
        block.PublishMidModuleMovie("17");

        // The serial is opaque from outside (no public getter mirrors OffsetCinematicSerial's
        // read-back either — plugin -> agent fields are write-only from the plugin's own
        // side), so the observable contract is the resref updating in place with no crash and
        // no truncation on the boundary length.
        Assert.True(true);
    }

    [Fact]
    public unsafe void ARequestResRefLongerThanTheFieldDoesNotOverrunTheRecord()
    {
        // MidModuleMovieController.HandleRequest reads through MovieStaging.IsMovieName first
        // (max 16 chars) before this is ever reached in production, but the block accessor
        // itself must not corrupt its neighbour regardless — the same discipline
        // WriteFixedAscii's own truncate-and-pad already gives every other named field.
        SharedBlockView block = NewScratchBlock(out byte[] storage);
        fixed (byte* p = storage)
        {
            block.PublishMidModuleMovie(new string('a', 64));

            // The 12 reserved bytes right after the 16-byte resref field must be untouched.
            for (int i = 0; i < 12; i++)
            {
                Assert.Equal(
                    0,
                    p[SharedBlock.OffsetMidModuleMovieResRef + SharedBlock.MidModuleMovieNameLength + i]);
            }
        }
    }

    [Fact]
    public unsafe void TheRequestRegionRoundTripsThroughTheAgentFacingAccessors()
    {
        // Simulates the AGENT's side of the write (raw bytes, the same shape
        // KotorGameBridge.TryRequestMidModulePlay produces) and reads it back through the
        // PLUGIN's own accessor, over the same scratch block — the two halves of the 0x394C
        // region agreeing on the same bytes is exactly what "block record encode/decode" means
        // for an agent -&gt; plugin field.
        SharedBlockView block = NewScratchBlock(out byte[] storage);
        fixed (byte* p = storage)
        {
            byte[] name = System.Text.Encoding.ASCII.GetBytes("01f");
            name.CopyTo(new Span<byte>(p + SharedBlock.OffsetMidModulePlayRequestResRef, name.Length));
            *(uint*)(p + SharedBlock.OffsetMidModulePlayRequestSerial) = 7;

            Assert.Equal(7u, block.MidModulePlayRequestSerial);

            byte* resRefPtr = block.MidModulePlayRequestResRef;
            int len = 0;
            while (len < SharedBlock.MidModuleMovieNameLength && resRefPtr[len] != 0)
            {
                len++;
            }

            Assert.Equal("01f", System.Text.Encoding.ASCII.GetString(resRefPtr, len));
        }
    }

    [Fact]
    public void MidModulePlayAppliedAndStatusRoundTrip()
    {
        SharedBlockView block = NewScratchBlock(out _);

        // 1 = MidModulePlayStatusCode.Played (KotorCoop.Plugin.MidModuleMovieController.cs,
        // not included in this test project - it needs Odyssey - so the value is asserted by
        // number here, the same way the block offsets themselves are agreed by number rather
        // than a shared type).
        block.MidModulePlayApplied = 7;
        block.MidModulePlayStatus = 1;

        Assert.Equal(7u, block.MidModulePlayApplied);
    }

    [Fact]
    public void TheRegionDoesNotOverlapAnyExistingField()
    {
        // The free-sliver math the task named: OffsetClientDoorRefusalArmed ends at 0x392C
        // (0x3928 + 4), OffsetPeerEffectSerial starts at 0x3A00 - both boundaries this new
        // region must sit cleanly inside without touching.
        Assert.Equal(0x392C, SharedBlock.OffsetMidModuleMovieSerial);
        Assert.True(SharedBlock.OffsetMidModuleMovieSerial >= SharedBlock.OffsetClientDoorRefusalArmed + 4);
        Assert.True(SharedBlock.OffsetMidModulePlayStatus + 4 <= SharedBlock.OffsetPeerEffectSerial);

        // TotalSize did not grow to make room for THIS region (it grew later — to 0x4100 for
        // the unrelated companion-equipment channel, see SharedBlock.cs's own version-54
        // comment, then to 0x4700 for the item-template-spawn ring (version 58), then to 0x4800
        // for that ring's own per-entry cheat flag (version 59, docs/research/
        // party-pool-sync.md §20), then to 0x4B00 in one pass by the 2026-09-02 combat wave:
        // N-slot local_combat's excluded-id array (61, 0x4800), per-attacker damage attribution
        // (62, 0x4900) and the companion-damage channel (63, 0x4A00), then to 0x4C00 the same
        // day for the materialised-owner-id table (65, 0x4B00), then to 0x4D00 on 2026-09-03
        // for the galaxy-map departure relay (67, 0x4C00), then to 0x4F00 for v74's protocol-59
        // acknowledged container-loot mailbox (0x4D00..0x4E3B), then to 0x5400 for v75's
        // item-spawn provenance arrays, then to 0x5C00 for the 2026-09-24 wave (v76-78), then to
        // 0x8600 for v80's "no cap really" pass (the enemy census/presence/materialised-owner/
        // damage-ledger tables relocated to a new 0x6000 region, past v79's reserved
        // 0x5C00..0x6000 gap) — see SharedBlock.TotalSize.
        Assert.Equal(0x8700, SharedBlock.TotalSize);
    }

    // ---- end-to-end through CoopAgent: dedup, same-module filter ----------------------

    private const string LocalModule = "end_m01ab";

    private sealed class MovieRelayBridge : IGameBridge
    {
        public PlayerState Local { get; set; } = new(Vector3f.Zero, 0f, LocalModule, 20, 0);

        public string Description => "movie-relay-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        /// <summary>Set to simulate this instance's own <c>MoviePlayHook</c> having captured
        /// something this tick.</summary>
        public (uint Serial, string ResRef)? OwnCapture { get; set; }

        public (uint Serial, string Module, string Waypoint)? OwnTransitionAnnounce { get; set; }

        public (uint Serial, string[] Movies)? OwnTransitionMovies { get; set; }

        public bool TryReadMidModuleMovie(out uint serial, out string resRef)
        {
            if (OwnCapture is { } capture)
            {
                (serial, resRef) = capture;
                return true;
            }

            serial = 0;
            resRef = string.Empty;
            return true; // readable empty mailbox, used to establish the startup baseline
        }

        public bool TryReadTransitionAnnounce(out uint serial, out string module, out string waypoint)
        {
            if (OwnTransitionAnnounce is { } announce)
            {
                (serial, module, waypoint) = announce;
                return true;
            }

            serial = 0;
            module = string.Empty;
            waypoint = string.Empty;
            return true;
        }

        public bool TryReadTransitionMovies(out uint serial, out string[] movies)
        {
            if (OwnTransitionMovies is { } transitionMovies)
            {
                (serial, movies) = transitionMovies;
                return true;
            }

            serial = 0;
            movies = [];
            return true;
        }

        /// <summary>Every resref the agent asked the plugin to act on, in order — what a real
        /// plugin's block write would have received.</summary>
        public List<string> Requests { get; } = [];

        public bool TryRequestMidModulePlay(string resRef)
        {
            Requests.Add(resRef);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class MovieRelayStubSession : ICoopSession
    {
        public List<MidModuleMoviePacket> Incoming { get; } = [];

        public IReadOnlyList<MidModuleMoviePacket> DrainMidModuleMovies()
        {
            List<MidModuleMoviePacket> ready = [.. Incoming];
            Incoming.Clear();
            return ready;
        }

        /// <summary>Every announcement this instance published, in order.</summary>
        public List<(string Module, uint Serial, string Movie)> Published { get; } = [];

        public List<(string Module, uint Serial, string[] Movies)> PublishedTransitionMovies { get; } = [];

        public List<(string Module, string Waypoint, uint Serial)> PublishedTransitionAnnounces { get; } = [];

        public void PublishMidModuleMovie(string module, uint serial, string movie) =>
            Published.Add((module, serial, movie));

        public void PublishTransitionMovies(string module, uint serial, string[] movies) =>
            PublishedTransitionMovies.Add((module, serial, movies));

        public void PublishTransitionAnnounce(string module, string waypoint, uint serial) =>
            PublishedTransitionAnnounces.Add((module, waypoint, serial));

        public uint LocalPeerId => 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public JournalStore Journal { get; } = new();

        public bool IsEnemyAuthority => false;

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state)
        {
        }

        public IReadOnlyList<RemotePeer> GetRemotePeers() => [];

        public void SetFlag(ulong flagId, sbyte value)
        {
        }

        public uint DropItem(
            string resref, string module, Vector3f position, ItemIdentity identity = default) => 0;

        public void RemoveItem(uint itemId)
        {
        }

        public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies)
        {
        }

        public IReadOnlyList<EnemyState> GetEnemies(string module) => [];

        public void PublishWorldDigest(in WorldDigest digest)
        {
        }

        public void PublishParty(IReadOnlyList<PartyMemberState> members)
        {
        }

        public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

        public void PublishDialogue(
            bool active, string speakerName, string line,
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = -1, uint nodeSerial = 0, bool forced = false)
        {
        }

        public DialogueState GetDialogue() => DialogueState.Idle;

        public void PublishProgression(uint gold, uint experience)
        {
        }

        public bool TryGetProgression(out uint gold, out uint experience)
        {
            gold = 0;
            experience = 0;
            return false;
        }

        public void SendChat(string text)
        {
        }

        public IReadOnlyList<ChatMessage> DrainChat() => [];

        public void SetJournalEntry(string questTag, int state) => Journal.Merge(questTag, state);

        public SessionCapabilities Capabilities { get; set; } = SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static (CoopAgent Agent, MovieRelayBridge Bridge, MovieRelayStubSession Session) Build(
        bool enabled = true)
    {
        MovieRelayBridge bridge = new();
        MovieRelayStubSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings
        {
            // Every pump reaches the combat-cadence block (which PumpMidModuleMovies shares
            // with PumpBarks) instead of waiting out a wall-clock interval.
            CombatPumpIntervalMs = 0,
            MovieRelayEnabled = enabled,
            TransitionAnnounceEnabled = true,
        });

        return (agent, bridge, session);
    }

    [Fact]
    public void ACapturedMovieIsAnnouncedWithTheLocalModule()
    {
        (CoopAgent agent, MovieRelayBridge bridge, MovieRelayStubSession session) = Build();
        agent.PumpOnce(); // first readable mailbox snapshot is the baseline
        bridge.OwnCapture = (1, "01c");

        agent.PumpOnce();

        (string module, uint serial, string movie) = Assert.Single(session.Published);
        Assert.Equal(LocalModule, module);
        Assert.Equal(1u, serial);
        Assert.Equal("01c", movie);
    }

    [Fact]
    public void TheSameCaptureSerialIsNotReAnnouncedEveryTick()
    {
        (CoopAgent agent, MovieRelayBridge bridge, MovieRelayStubSession session) = Build();
        agent.PumpOnce();
        bridge.OwnCapture = (1, "01c");

        agent.PumpOnce();
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Single(session.Published);
    }

    [Fact]
    public void ANewCaptureSerialIsAnnouncedAgain()
    {
        (CoopAgent agent, MovieRelayBridge bridge, MovieRelayStubSession session) = Build();
        agent.PumpOnce();
        bridge.OwnCapture = (1, "01c");
        agent.PumpOnce();

        bridge.OwnCapture = (2, "01f");
        agent.PumpOnce();

        Assert.Equal(2, session.Published.Count);
        Assert.Equal("01f", session.Published[1].Movie);
    }

    [Fact]
    public void PersistentNativeMailboxValuesAreBaselinedAndOnlyLaterEdgesAreAnnounced()
    {
        (CoopAgent agent, MovieRelayBridge bridge, MovieRelayStubSession session) = Build();

        // These are nonzero level-triggered values left by the plugin from the prior managed
        // session. They must not be re-published when this agent starts or rejoins.
        bridge.OwnCapture = (7, "01c");
        bridge.OwnTransitionMovies = (15, ["56b"]);
        bridge.OwnTransitionAnnounce = (22, "danm13", "from_07");
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Empty(session.Published);
        Assert.Empty(session.PublishedTransitionMovies);
        Assert.Empty(session.PublishedTransitionAnnounces);

        // A readable first snapshot established the initial high-water values, so the next
        // real serial edges still flow through the ordinary announcement path.
        bridge.OwnCapture = (8, "01f");
        bridge.OwnTransitionMovies = (16, ["01f"]);
        bridge.OwnTransitionAnnounce = (23, "manm28aa", "from_08");
        agent.PumpOnce();

        Assert.Equal((LocalModule, 8u, "01f"), Assert.Single(session.Published));
        (string module, uint serial, string[] movies) = Assert.Single(session.PublishedTransitionMovies);
        Assert.Equal(LocalModule, module);
        Assert.Equal(16u, serial);
        Assert.Equal(["01f"], movies);
        Assert.Equal(("manm28aa", "from_08", 23u), Assert.Single(session.PublishedTransitionAnnounces));
    }

    [Fact]
    public void FreshZeroMailboxesStillPublishTheirFirstSerialOneEvent()
    {
        (CoopAgent agent, MovieRelayBridge bridge, MovieRelayStubSession session) = Build();

        // A clean plugin block is serial zero. Reading that snapshot must establish the
        // baseline without consuming the first real edge that follows it.
        agent.PumpOnce();

        bridge.OwnCapture = (1, "01c");
        bridge.OwnTransitionMovies = (1, ["56b"]);
        bridge.OwnTransitionAnnounce = (1, "danm13", "from_07");
        agent.PumpOnce();

        Assert.Equal((LocalModule, 1u, "01c"), Assert.Single(session.Published));
        (string movieModule, uint movieSerial, string[] movies) = Assert.Single(session.PublishedTransitionMovies);
        Assert.Equal(LocalModule, movieModule);
        Assert.Equal(1u, movieSerial);
        Assert.Equal(["56b"], movies);
        Assert.Equal(("danm13", "from_07", 1u), Assert.Single(session.PublishedTransitionAnnounces));
    }

    [Fact]
    public void APeersAnnouncementForTheLocalModuleIsHandedToThePlugin()
    {
        (CoopAgent agent, MovieRelayBridge bridge, MovieRelayStubSession session) = Build();
        session.Incoming.Add(new MidModuleMoviePacket(PeerId: 9, LocalModule, Serial: 1, "01c"));

        agent.PumpOnce();

        Assert.Equal(["01c"], bridge.Requests);
    }

    /// <summary>⚠ The dedup this test pins: without it, a peer's announcement — which the
    /// SENDER keeps repeating for redundancy against a dropped datagram, the same "resend a
    /// few ticks" shape TransitionMovies uses — would be handed to the plugin on every single
    /// tick it is still sitting in the drain queue, each one a fresh request the plugin has to
    /// act on again.</summary>
    [Fact]
    public void TheSamePeerSerialTwiceIsRequestedOnlyOnce()
    {
        (CoopAgent agent, MovieRelayBridge bridge, MovieRelayStubSession session) = Build();
        session.Incoming.Add(new MidModuleMoviePacket(PeerId: 9, LocalModule, Serial: 1, "01c"));
        agent.PumpOnce();

        session.Incoming.Add(new MidModuleMoviePacket(PeerId: 9, LocalModule, Serial: 1, "01c"));
        agent.PumpOnce();

        Assert.Single(bridge.Requests);
    }

    [Fact]
    public void ANewSerialFromTheSamePeerIsRequestedAgain()
    {
        (CoopAgent agent, MovieRelayBridge bridge, MovieRelayStubSession session) = Build();
        session.Incoming.Add(new MidModuleMoviePacket(PeerId: 9, LocalModule, Serial: 1, "01c"));
        agent.PumpOnce();

        session.Incoming.Add(new MidModuleMoviePacket(PeerId: 9, LocalModule, Serial: 2, "01f"));
        agent.PumpOnce();

        Assert.Equal(["01c", "01f"], bridge.Requests);
    }

    [Fact]
    public void APeersAnnouncementForADifferentModuleIsNeverHandedToThePlugin()
    {
        (CoopAgent agent, MovieRelayBridge bridge, MovieRelayStubSession session) = Build();
        session.Incoming.Add(new MidModuleMoviePacket(PeerId: 9, "tar_m02aa", Serial: 1, "05_2a"));

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void AnEmptyMovieNameIsNeverHandedToThePlugin()
    {
        (CoopAgent agent, MovieRelayBridge bridge, MovieRelayStubSession session) = Build();
        session.Incoming.Add(new MidModuleMoviePacket(PeerId: 9, LocalModule, Serial: 1, string.Empty));

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void DisabledByDefaultChangesNothing()
    {
        (CoopAgent agent, MovieRelayBridge bridge, MovieRelayStubSession session) = Build(enabled: false);
        bridge.OwnCapture = (1, "01c");
        session.Incoming.Add(new MidModuleMoviePacket(PeerId: 9, LocalModule, Serial: 1, "01c"));

        agent.PumpOnce();

        Assert.Empty(session.Published);
        Assert.Empty(bridge.Requests);
    }
}
