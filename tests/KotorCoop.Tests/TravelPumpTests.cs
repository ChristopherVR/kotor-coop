using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The follow-the-host rule: a client travels to whatever module the host is in, the host
/// never follows anyone, and the request is debounced so a module load in progress is not
/// interrupted by a duplicate. Pure decision logic — the engine side is the plugin's
/// TravelController, which cannot run outside the game.
/// </summary>
public class TravelPumpTests
{
    private const string HostModule = "tar_m02aa";
    private const string ClientModule = "end_m01aa";

    private sealed class TravelBridge : IGameBridge
    {
        public List<(string Module, string Waypoint)> Requests { get; } = [];

        public bool InDialogue { get; set; }

        public PlayerState Local { get; set; } = new(Vector3f.Zero, 0f, ClientModule, 20, 0);

        public string Description => "travel-test";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsTravel => true;

        public bool IsInDialogue => InDialogue;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = Local;
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        /// <summary>Set to simulate THIS instance's own engine having committed to a
        /// transition — the lead-swap "both announce simultaneously" tests use this to arm
        /// the host-side safety gate without needing a real plugin.</summary>
        public (uint Serial, string Module, string Waypoint)? OwnAnnounce { get; set; }

        public bool TryReadTransitionAnnounce(out uint serial, out string module, out string waypoint)
        {
            if (OwnAnnounce is { } announce)
            {
                (serial, module, waypoint) = announce;
                return true;
            }

            serial = 0;
            module = string.Empty;
            waypoint = string.Empty;
            return false;
        }

        /// <summary>The movies attached to each travel request, so the cinematic parity
        /// channel can be asserted on the thing that actually reaches the plugin.</summary>
        public List<string[]> RequestMovies { get; } = [];

        public bool TryRequestTravel(string module, string waypoint = "", IReadOnlyList<string>? movies = null)
        {
            Requests.Add((module, waypoint));
            RequestMovies.Add(movies is null ? [] : [.. movies]);
            return true;
        }

        /// <summary>Every value <c>PumpTravel</c> wrote through
        /// <see cref="TryArmClientDoorRefusal"/>, in order — asserts both that it is written
        /// EVERY tick (a stale "armed" must not outlive whatever justified it) and what it
        /// carried.</summary>
        public List<bool> DoorRefusalArmedCalls { get; } = [];

        /// <summary>Every REASON <c>PumpTravel</c> wrote alongside the armed bit above, in
        /// order — the same shape, for tests that care WHY (host-must-lead vs. host-busy).</summary>
        public List<ClientDoorRefusalReason> DoorRefusalReasonCalls { get; } = [];

        public bool TryArmClientDoorRefusal(ClientDoorRefusalReason reason)
        {
            DoorRefusalArmedCalls.Add(reason != ClientDoorRefusalReason.None);
            DoorRefusalReasonCalls.Add(reason);
            return true;
        }

        /// <summary>Every on-screen line posted through the fast path's
        /// <see cref="IGameBridge.PostChatLine"/> call.</summary>
        public List<string> ChatLines { get; } = [];

        public bool PostChatLine(string line)
        {
            ChatLines.Add(line);
            return true;
        }

        public void Dispose()
        {
        }
    }

    private sealed class StubSession : ICoopSession
    {
        /// <summary>Announcements waiting to be handed to the agent, as if they had just
        /// arrived off the wire.</summary>
        public List<TransitionMoviePacket> IncomingMovies { get; } = [];

        public IReadOnlyList<TransitionMoviePacket> DrainTransitionMovies()
        {
            List<TransitionMoviePacket> ready = [.. IncomingMovies];
            IncomingMovies.Clear();
            return ready;
        }

        /// <summary>Transition-announce packets waiting to be handed to the agent, as if they
        /// had just arrived off the wire.</summary>
        public List<TransitionAnnouncePacket> IncomingAnnounces { get; } = [];

        public IReadOnlyList<TransitionAnnouncePacket> DrainTransitionAnnounce()
        {
            List<TransitionAnnouncePacket> ready = [.. IncomingAnnounces];
            IncomingAnnounces.Clear();
            return ready;
        }

        public List<RemotePeer> Peers { get; } = [];

        public bool EnemyAuthority { get; set; }

        public uint LocalPeerId => EnemyAuthority ? CoopHost.HostPeerId : 2;

        public bool IsConnected => true;

        public string LocalName => "test";

        public EventFlagStore Flags { get; } = new();

        public GroundItemStore Items { get; } = new();

        public bool IsEnemyAuthority => EnemyAuthority;

        public SessionStats GetStats() => new();

        public void PublishLocalState(in PlayerState state)
        {
        }

        public IReadOnlyList<RemotePeer> GetRemotePeers() => Peers;

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

        public JournalStore Journal { get; } = new();

        public void PublishWorldDigest(in WorldDigest digest) { }

        public void PublishParty(System.Collections.Generic.IReadOnlyList<PartyMemberState> members) { }

        public System.Collections.Generic.IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

        public void PublishDialogue(
            bool active, string speakerName, string line,
            string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
            int nodeIndex = -1, uint nodeSerial = 0, bool forced = false) { }

        public DialogueState GetDialogue() => DialogueState.Idle;

        public void PublishProgression(uint gold, uint experience) { }

        public bool TryGetProgression(out uint gold, out uint experience)
        {
            gold = 0;
            experience = 0;
            return false;
        }

        public void SendChat(string text) { }

        public System.Collections.Generic.IReadOnlyList<ChatMessage> DrainChat() => [];

        public void SetJournalEntry(string questTag, int state) => Journal.Merge(questTag, state);

        public SessionCapabilities Capabilities { get; set; } = SessionCapabilities.All;

        public CompatibilityVerdict? Compatibility => null;

        public Task RunAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private static (CoopAgent Agent, TravelBridge Bridge, StubSession Session) Build(
        bool follow = true, int retryMs = 15_000, int settleMs = 0, int combatPumpMs = 150,
        bool transitionAnnounce = false, bool clientDoorRefusal = false,
        FollowLeadMode followLeadMode = FollowLeadMode.Host)
    {
        TravelBridge bridge = new();
        StubSession session = new();
        CoopAgent agent = new(bridge, session, new AgentSettings
        {
            FollowHostModule = follow,
            FollowRetryMs = retryMs,

            // The cinematic pump shares the combat cadence. Tests that need it to run on
            // EVERY pump pass 0; the rest keep the real interval.
            CombatPumpIntervalMs = combatPumpMs,

            // These tests are about the DEBOUNCE. The post-arrival settle window is a
            // separate rule with its own test below, and leaving it on here would mean
            // every case had to wait out a wall clock nothing else in the suite depends on.
            ModuleSettleMs = settleMs,

            TransitionAnnounceEnabled = transitionAnnounce,
            ClientDoorRefusalEnabled = clientDoorRefusal,

            // Host-only by default here so every pre-existing test in this file (all written
            // before lead-swap existed) keeps meaning exactly what it meant before. The
            // lead-swap section below opts each of ITS OWN tests into FollowLeadMode.Any
            // explicitly — coop.ini's own real-world default is the opposite (Any), set in
            // CoopConfig/Program.cs, not here.
            FollowLeadMode = followLeadMode,

            // Deterministic and distinguishable from the raw resref in the fast-path tests,
            // without needing a real game directory.
            ModuleDisplayName = m => $"Pretty({m})",
        });

        return (agent, bridge, session);
    }

    private static RemotePeer HostPeer(string module) =>
        new(CoopHost.HostPeerId, "host", new PlayerState(Vector3f.Zero, 0f, module, 20, 0));

    private static RemotePeer ClientPeer(uint peerId, string name, string module, uint flags = 0) =>
        new(peerId, name, new PlayerState(Vector3f.Zero, 0f, module, 20, 0, Flags: flags));

    [Fact]
    public void ClientFollowsTheHostIntoAnotherModule()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();

        (string module, string waypoint) = Assert.Single(bridge.Requests);
        Assert.Equal(HostModule, module);
        Assert.Equal(string.Empty, waypoint);
    }

    [Fact]
    public void RepeatedPumpsDoNotRepeatTheRequestWhileTheLoadIsInFlight()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();
        agent.PumpOnce();
        agent.PumpOnce();

        Assert.Single(bridge.Requests);
    }

    [Fact]
    public void SameModuleMeansNoRequest()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer(ClientModule));

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void TheHostNeverFollowsAnyone()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.EnemyAuthority = true;
        session.Peers.Add(new RemotePeer(2, "client",
            new PlayerState(Vector3f.Zero, 0f, HostModule, 20, 0)));

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void StuntModulesAreNeverFollowedInto()
    {
        // Stunt modules stage cutscenes; the host comes back out on its own.
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer("STUNT_06"));

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void FallbackAreaNamesAreNeverFollowedInto()
    {
        // "area1F" is the bridge's stand-in when the host's module name was unreadable
        // (a transition in flight); it names nothing on disk. A real module that merely
        // starts with "area" would be fine — only the pure hex-suffix form is held back.
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer("area1F"));

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void NoRequestWhileTheLocalPlayerIsInDialogue()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        bridge.InDialogue = true;
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void FollowCanBeDisabled()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build(follow: false);
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    [Fact]
    public void ArrivingResetsTheDebounceSoTheNextSeparationActsImmediately()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();
        Assert.Single(bridge.Requests);

        // The client arrives: local module now matches the host's.
        bridge.Local = new PlayerState(Vector3f.Zero, 0f, HostModule, 20, 0);
        agent.PumpOnce();

        // The host moves on again; the follow must fire without waiting out the retry.
        session.Peers.Clear();
        session.Peers.Add(HostPeer("dan_m13aa"));
        agent.PumpOnce();

        Assert.Equal(2, bridge.Requests.Count);
        Assert.Equal("dan_m13aa", bridge.Requests[1].Module);
    }

    [Fact]
    public void ArrivingSomewhereHoldsTheNextFollowUntilTheWorldHasSettled()
    {
        // The crash guard: leaving a module moments after arriving in it killed the client
        // in the engine's creature-add handler, which dereferences CClientExoApp::GetModule
        // unchecked while the previous world's object messages are still being serviced.
        // A client that walks through its own door is brought straight back by the follow,
        // so this pair is ordinary play rather than a contrived case.
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build(settleMs: 60_000);
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();
        Assert.Single(bridge.Requests); // the FIRST follow is not held: we never transitioned

        // We arrive somewhere of our own — a different module from both.
        bridge.Local = new PlayerState(Vector3f.Zero, 0f, "dan_m13aa", 20, 0);
        agent.PumpOnce();

        // The host is still elsewhere, so the follow wants to fire immediately. It must not.
        agent.PumpOnce();
        Assert.Single(bridge.Requests);
    }

    [Fact]
    public void NoFollowWhileOurOwnModuleNameIsTheMidTransitionFallback()
    {
        // "areaXX" means the module name could not be read, which happens for exactly one
        // reason: we are mid-transition. Comparing that placeholder against the host's real
        // module reads as separation and fires a SECOND transition into the middle of the
        // first — which is what killed the client in the engine's creature-add handler.
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer(HostModule));
        bridge.Local = new PlayerState(Vector3f.Zero, 0f, "area1F", 20, 0);

        agent.PumpOnce();

        Assert.Empty(bridge.Requests);

        // Once the real name resolves, the follow behaves normally.
        bridge.Local = new PlayerState(Vector3f.Zero, 0f, ClientModule, 20, 0);
        agent.PumpOnce();

        Assert.Single(bridge.Requests);
        Assert.Equal(HostModule, bridge.Requests[0].Module);
    }

    [Fact]
    public void TheSettleWindowDoesNotHoldTheFirstFollowOfASession()
    {
        // Starting a session already in a different module from the host is not a transition,
        // and making the player wait for one that never happened would look like the follow
        // was broken.
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build(settleMs: 60_000);
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();

        Assert.Single(bridge.Requests);
        Assert.Equal(HostModule, bridge.Requests[0].Module);
    }

    [Fact]
    public void ADifferentTargetBypassesTheDebounce()
    {
        // The host changed its mind mid-flight (double transition). The new destination
        // must not wait out the previous request's retry window.
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();

        session.Peers.Clear();
        session.Peers.Add(HostPeer("dan_m13aa"));
        agent.PumpOnce();

        Assert.Equal(2, bridge.Requests.Count);
        Assert.Equal("dan_m13aa", bridge.Requests[1].Module);
    }

    // ---- the cinematic that rides with the follow ----------------------------------

    private static TransitionMoviePacket Announcement(uint serial, params string[] movies) =>
        new(CoopHost.HostPeerId, "end_m01ab", serial, movies);

    /// <summary>An ordinary follow carries no movies, and must not invent any.</summary>
    [Fact]
    public void AFollowWithNothingAnnouncedCarriesNoMovies()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build();
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();

        Assert.Empty(Assert.Single(bridge.RequestMovies));
    }

    [Fact]
    public void AnAnnouncedMovieRidesWithTheFollow()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build(combatPumpMs: 0);
        session.IncomingMovies.Add(Announcement(1, "01c"));
        session.Peers.Add(HostPeer(HostModule));

        agent.PumpOnce();

        Assert.Equal(["01c"], Assert.Single(bridge.RequestMovies));
    }

    /// <summary>
    /// The case the accumulation exists for. Endar Spire → Taris is TWO transitions through
    /// the stunt module STUNT_00, and the follow logic correctly refuses to enter a `stunt*`
    /// module — so the follower declines the first (movie <c>01c</c>) and finally warps to
    /// tar_m02af (movie <c>01f</c>). The transition it actually makes must carry both, in
    /// order, or the player who followed sees half the story.
    /// </summary>
    [Fact]
    public void MoviesAnnouncedWhileTheFollowIsDeclinedAreAccumulated()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build(combatPumpMs: 0);

        // The host disappears into the stunt module, announcing 01c on the way.
        session.IncomingMovies.Add(Announcement(1, "01c"));
        session.Peers.Add(HostPeer("STUNT_00"));
        agent.PumpOnce();

        Assert.Empty(bridge.Requests); // correctly refused to follow

        // Its scene ends and it announces 01f on the way to Taris.
        session.IncomingMovies.Add(Announcement(2, "01f"));
        session.Peers.Clear();
        session.Peers.Add(HostPeer("tar_m02af"));
        agent.PumpOnce();

        Assert.Equal("tar_m02af", Assert.Single(bridge.Requests).Module);
        Assert.Equal(["01c", "01f"], Assert.Single(bridge.RequestMovies));
    }

    /// <summary>Spent, not kept: the same names must never ride a second, unrelated
    /// transition.</summary>
    [Fact]
    public void TheAccumulationIsSpentByTheFollowThatCarriesIt()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build(combatPumpMs: 0);
        session.IncomingMovies.Add(Announcement(1, "01c"));
        session.Peers.Add(HostPeer(HostModule));
        agent.PumpOnce();

        session.Peers.Clear();
        session.Peers.Add(HostPeer("dan_m13aa"));
        agent.PumpOnce();

        Assert.Equal(2, bridge.RequestMovies.Count);
        Assert.Equal(["01c"], bridge.RequestMovies[0]);
        Assert.Empty(bridge.RequestMovies[1]);
    }

    /// <summary>A repeated announcement is the SENDER's redundancy against a dropped
    /// datagram, not five cinematics.</summary>
    [Fact]
    public void ARepeatedAnnouncementIsAccumulatedOnce()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build(combatPumpMs: 0);

        session.IncomingMovies.Add(Announcement(1, "01c"));
        session.IncomingMovies.Add(Announcement(1, "01c"));
        session.IncomingMovies.Add(Announcement(1, "01c"));
        session.Peers.Add(HostPeer("STUNT_00"));
        agent.PumpOnce();

        session.Peers.Clear();
        session.Peers.Add(HostPeer(HostModule));
        agent.PumpOnce();

        Assert.Equal(["01c"], Assert.Single(bridge.RequestMovies));
    }

    /// <summary>
    /// Arriving where the host already is drops the accumulation. Holding it would attach
    /// somebody else's film to whatever this player did next, minutes later — the same "left
    /// armed" hazard the staging arm expires to avoid.
    /// </summary>
    [Fact]
    public void BeingWithTheHostDropsTheAccumulation()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build(combatPumpMs: 0);

        session.IncomingMovies.Add(Announcement(1, "01c"));
        session.Peers.Add(HostPeer("STUNT_00"));
        agent.PumpOnce();

        // The host comes back to us: nothing pending any more.
        session.Peers.Clear();
        session.Peers.Add(HostPeer(ClientModule));
        agent.PumpOnce();

        // And now it leaves again, with no cinematic of its own.
        session.Peers.Clear();
        session.Peers.Add(HostPeer(HostModule));
        agent.PumpOnce();

        Assert.Empty(Assert.Single(bridge.RequestMovies));
    }

    /// <summary>
    /// The race the accumulation guard exists for: the host announces a movie the instant its
    /// engine accepts it onto the transition queue, which is BEFORE its main thread blocks to
    /// play it — so its published module has not changed yet, and for as long as the stall
    /// lasts (the whole length of the movie) every pump still reads "together". None of that
    /// is a real reunion, so none of it may drop the movie.
    /// </summary>
    [Fact]
    public void AnnouncingWhileTheHostStillReadsAsTogetherDoesNotDropTheMovie()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build(combatPumpMs: 0);

        // Announced, but the host's published module has not caught up to its own departure -
        // it still reads as standing right where we are.
        session.IncomingMovies.Add(Announcement(1, "01c"));
        session.Peers.Add(HostPeer(ClientModule));
        agent.PumpOnce();

        // Several more pumps while its main thread is still blocked playing the movie: the
        // published module never changes, so "together" keeps reading true throughout.
        agent.PumpOnce();
        agent.PumpOnce();

        // The stall ends and the host's real module finally lands.
        session.Peers.Clear();
        session.Peers.Add(HostPeer(HostModule));
        agent.PumpOnce();

        Assert.Equal(["01c"], Assert.Single(bridge.RequestMovies));
    }

    /// <summary>The engine's own queue takes six; a hoard past that is capped rather than
    /// sent, because AddMoveToModuleMovie would refuse the extras anyway.</summary>
    [Fact]
    public void TheAccumulationIsCappedAtTheEnginesOwnSix()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build(combatPumpMs: 0);

        for (uint i = 1; i <= 9; i++)
        {
            session.IncomingMovies.Add(Announcement(i, $"mov{i}"));
        }

        session.Peers.Add(HostPeer("STUNT_00"));
        agent.PumpOnce();

        session.Peers.Clear();
        session.Peers.Add(HostPeer(HostModule));
        agent.PumpOnce();

        string[] carried = Assert.Single(bridge.RequestMovies);
        Assert.Equal(TransitionMoviePacket.MaxMovies, carried.Length);
        Assert.Equal(["mov1", "mov2", "mov3", "mov4", "mov5", "mov6"], carried);
    }

    // ---- announced transitions: the fast path ---------------------------------------

    private static TransitionAnnouncePacket Announce(uint serial, string module, string waypoint = "") =>
        new(CoopHost.HostPeerId, module, waypoint, serial);

    /// <summary>
    /// The whole point of the feature: an announce fires the follow immediately, even though
    /// the settle window (still open from this player's own last transition) would otherwise
    /// hold it — and even with NO host peer entry at all, simulating the exact window the
    /// design targets: the host's main thread is blocked inside a movie and has published
    /// nothing new yet.
    /// </summary>
    [Fact]
    public void AnAnnouncedTransitionBypassesTheSettleDebounceAndNeedsNoHostPeerEntry()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(settleMs: 60_000, transitionAnnounce: true, combatPumpMs: 0);

        // The FIRST follow, unrelated to what this test is about.
        session.Peers.Add(HostPeer(HostModule));
        agent.PumpOnce();
        Assert.Single(bridge.Requests);

        // We arrive at the host's module — a real transition just completed, so the settle
        // clock starts (ModuleSettleMs guards the world that was JUST loaded, exactly here).
        bridge.Local = new PlayerState(Vector3f.Zero, 0f, HostModule, 20, 0);
        agent.PumpOnce();

        // The host is already moving on again — but its main thread is blocked inside a
        // movie, its published module has not caught up, and it is absent from the peer list
        // entirely here on purpose (nothing to compare "together" against). Only the announce
        // says where it is actually headed.
        session.IncomingAnnounces.Add(Announce(2, "korr_m34aa", "from_door"));
        agent.PumpOnce();

        Assert.Equal(2, bridge.Requests.Count);
        Assert.Equal("korr_m34aa", bridge.Requests[1].Module);
        Assert.Equal("from_door", bridge.Requests[1].Waypoint);
    }

    /// <summary>The gate itself: with the feature OFF (the default), a queued announce is
    /// simply never consumed, and behaviour matches every pre-existing test in this file.</summary>
    [Fact]
    public void WithTheFeatureDisabledAnAnnouncementIsIgnored()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(settleMs: 60_000, transitionAnnounce: false, combatPumpMs: 0);

        session.IncomingAnnounces.Add(Announce(1, HostModule));
        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    /// <summary>The stunt gate applies to an announced destination exactly like it does to the
    /// host's published module — an announcement is earlier information, not different rules.</summary>
    [Fact]
    public void AStuntModuleAnnouncementIsNeverFollowedEvenViaTheFastPath()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(transitionAnnounce: true, combatPumpMs: 0);

        session.IncomingAnnounces.Add(Announce(1, "STUNT_00"));
        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    /// <summary>An announce naming our own current module is spent without ever warping —
    /// there is nothing to race ahead of.</summary>
    [Fact]
    public void AnAnnouncementNamingOurOwnModuleWarpsNowhere()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(transitionAnnounce: true, combatPumpMs: 0);

        session.IncomingAnnounces.Add(Announce(1, ClientModule));
        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    /// <summary>The movies accumulated from a declined stunt-module follow ride the fast-path
    /// warp exactly like they ride the slow one.</summary>
    [Fact]
    public void MoviesAccumulatedSoFarRideTheAnnouncedFastPathToo()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(transitionAnnounce: true, combatPumpMs: 0);

        session.IncomingMovies.Add(new TransitionMoviePacket(CoopHost.HostPeerId, "end_m01ab", 1, ["01c"]));
        session.Peers.Add(HostPeer("STUNT_00"));
        agent.PumpOnce();
        Assert.Empty(bridge.Requests); // correctly refused to follow into the stunt module

        session.Peers.Clear();
        session.IncomingAnnounces.Add(Announce(2, "tar_m02af"));
        agent.PumpOnce();

        Assert.Equal("tar_m02af", Assert.Single(bridge.Requests).Module);
        Assert.Equal(["01c"], Assert.Single(bridge.RequestMovies));
    }

    /// <summary>The on-screen line (task 2a): posted through the bridge's chat overlay,
    /// pretty-printed via the injected display-name hook, naming the host by its peer name.</summary>
    [Fact]
    public void TheFastPathPostsAnOnScreenLine()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(transitionAnnounce: true, combatPumpMs: 0);

        session.Peers.Add(HostPeer(HostModule)); // supplies the display name, "host"
        session.IncomingAnnounces.Add(Announce(1, "tar_m03aa"));
        agent.PumpOnce();

        Assert.Equal("following host to Pretty(tar_m03aa)", Assert.Single(bridge.ChatLines));
    }

    /// <summary>A repeated announce (the sender's own redundancy against a dropped datagram)
    /// must not fire the warp twice.</summary>
    [Fact]
    public void ARepeatedAnnouncementDoesNotRepeatTheFastPathWarp()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(transitionAnnounce: true, combatPumpMs: 0, retryMs: 60_000);

        session.IncomingAnnounces.Add(Announce(1, "tar_m03aa"));
        agent.PumpOnce();
        Assert.Single(bridge.Requests);

        session.IncomingAnnounces.Add(Announce(1, "tar_m03aa"));
        agent.PumpOnce();
        Assert.Single(bridge.Requests);
    }

    // ---- the client-door-refusal arm ---------------------------------------------------

    [Fact]
    public void TheRefusalArmIsWrittenEveryTickWhenEligible()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(clientDoorRefusal: true, combatPumpMs: 0);

        session.Peers.Add(HostPeer(HostModule));
        agent.PumpOnce();

        Assert.Equal([true], bridge.DoorRefusalArmedCalls);
    }

    [Fact]
    public void TheHostNeverArmsItsOwnDoorRefusal()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(clientDoorRefusal: true, combatPumpMs: 0);

        session.EnemyAuthority = true;
        agent.PumpOnce();

        Assert.Equal([false], bridge.DoorRefusalArmedCalls);
    }

    [Fact]
    public void TheRefusalArmIsOffByDefault()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) = Build(combatPumpMs: 0);

        session.Peers.Add(HostPeer(HostModule));
        agent.PumpOnce();

        Assert.Equal([false], bridge.DoorRefusalArmedCalls);
    }

    /// <summary>The accidental-success case: the host announcing (or queuing movies for) a
    /// transition of its own this same tick disarms the refusal for whatever door it just used.</summary>
    [Fact]
    public void ARecentHostTransitionSignalDisarmsTheRefusalTheSameTick()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(clientDoorRefusal: true, combatPumpMs: 0);

        session.IncomingMovies.Add(new TransitionMoviePacket(CoopHost.HostPeerId, "end_m01ab", 1, ["01c"]));
        session.Peers.Add(HostPeer("STUNT_00")); // declined follow; irrelevant to the arm
        agent.PumpOnce();

        Assert.Equal([false], bridge.DoorRefusalArmedCalls);
    }

    // ---- lead-swap (2026-08-22): the host follows a client's announce ------------------

    private const uint ClientPeerId = 2;

    private static TransitionAnnouncePacket ClientAnnounce(uint serial, string module, string waypoint = "") =>
        new(ClientPeerId, module, waypoint, serial);

    /// <summary>The everyday case: running AS THE HOST, follow_lead=any, a client's announce
    /// with nothing in the way. The fast path fires exactly like it does for a client following
    /// the host, just in the other direction.</summary>
    [Fact]
    public void HostFollowsAnEligibleClientAnnounceUnderAnyMode()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(transitionAnnounce: true, followLeadMode: FollowLeadMode.Any, combatPumpMs: 0);
        session.EnemyAuthority = true; // this instance IS the host

        session.IncomingAnnounces.Add(ClientAnnounce(1, "tar_m03aa", "from_door"));
        agent.PumpOnce();

        (string module, string waypoint) = Assert.Single(bridge.Requests);
        Assert.Equal("tar_m03aa", module);
        Assert.Equal("from_door", waypoint);
    }

    /// <summary>follow_lead=host (the default in this file's own <c>Build</c>): "the host
    /// never follows anyone" restored exactly, even with a perfectly usable client announce
    /// sitting right there.</summary>
    [Fact]
    public void HostIgnoresAClientAnnounceUnderHostOnlyMode()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(transitionAnnounce: true, followLeadMode: FollowLeadMode.Host, combatPumpMs: 0);
        session.EnemyAuthority = true;

        session.IncomingAnnounces.Add(ClientAnnounce(1, "tar_m03aa"));
        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    /// <summary>The safety gate: the host's own module is still settling (it just arrived
    /// somewhere), so a client's announce is held rather than acted on — and NOT dropped: once
    /// the settle window elapses, the SAME pending announce still fires.</summary>
    [Fact]
    public void HostHoldsAClientAnnounceWhileItsOwnModuleIsSettling()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(transitionAnnounce: true, followLeadMode: FollowLeadMode.Any, settleMs: 60_000, combatPumpMs: 0);
        session.EnemyAuthority = true;

        // The host arrives somewhere of its own first - a real transition, so the settle
        // clock starts.
        session.Peers.Add(ClientPeer(ClientPeerId, "client", "end_m01aa"));
        agent.PumpOnce();
        bridge.Local = new PlayerState(Vector3f.Zero, 0f, "dan_m13aa", 20, 0);
        agent.PumpOnce();

        // A client announces while the host is still settling.
        session.IncomingAnnounces.Add(ClientAnnounce(1, "tar_m03aa"));
        agent.PumpOnce();
        Assert.Empty(bridge.Requests); // held, not acted on - the same shape
                                        // ArrivingSomewhereHoldsTheNextFollowUntilTheWorldHasSettled
                                        // already uses for the client-side settle window. Whether
                                        // a held announce is KEPT (retried once free) rather than
                                        // dropped is pinned at the pure-rule level -
                                        // LeadFollowRuleTests has no wall-clock seam to advance
                                        // SessionClock and prove the later retry end-to-end here.
    }

    /// <summary>The safety gate again, for real combat: the host's own PlayerState carries
    /// FlagInCombat, and the client's announce is held rather than acted on.</summary>
    [Fact]
    public void HostDoesNotFollowAClientAnnounceWhileInRealCombat()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(transitionAnnounce: true, followLeadMode: FollowLeadMode.Any, combatPumpMs: 0);
        session.EnemyAuthority = true;
        bridge.Local = new PlayerState(
            Vector3f.Zero, 0f, ClientModule, 20, 0, Flags: PlayerState.FlagInCombat);

        session.IncomingAnnounces.Add(ClientAnnounce(1, "tar_m03aa"));
        agent.PumpOnce();

        Assert.Empty(bridge.Requests);
    }

    /// <summary>Both announce simultaneously -&gt; host wins: the host's own outbound
    /// transition-announce serial changing THIS tick (its own engine just committed to a
    /// crossing) closes the safety gate, so a client's announce arriving in the same window is
    /// never acted on and the host's own crossing proceeds untouched.</summary>
    [Fact]
    public void BothAnnouncingSimultaneouslyTheHostsOwnTransitionWins()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(transitionAnnounce: true, followLeadMode: FollowLeadMode.Any, combatPumpMs: 0);
        session.EnemyAuthority = true;
        bridge.OwnAnnounce = (1, "korr_m34aa", "from_door");

        session.IncomingAnnounces.Add(ClientAnnounce(1, "tar_m03aa"));
        agent.PumpOnce();

        // The host's OWN transition (its outbound announce read this tick) wins: it never
        // requests travel to the client's destination.
        Assert.Empty(bridge.Requests);
    }

    /// <summary>The on-screen line names the LEADING CLIENT, not "the host", when the host is
    /// the one following.</summary>
    [Fact]
    public void TheOnScreenLineNamesTheLeadingClientWhenTheHostFollows()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(transitionAnnounce: true, followLeadMode: FollowLeadMode.Any, combatPumpMs: 0);
        session.EnemyAuthority = true;
        session.Peers.Add(ClientPeer(ClientPeerId, "partner", ClientModule));

        session.IncomingAnnounces.Add(ClientAnnounce(1, "tar_m03aa"));
        agent.PumpOnce();

        Assert.Equal("following partner to Pretty(tar_m03aa)", Assert.Single(bridge.ChatLines));
    }

    // ---- lead-swap: the refusal reason ---------------------------------------------------

    /// <summary>follow_lead=any with a free host: no refusal at all, which is what lets the
    /// client's crossing become an announce the host follows.</summary>
    [Fact]
    public void NoRefusalUnderAnyModeWithAFreeHost()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(clientDoorRefusal: true, followLeadMode: FollowLeadMode.Any, combatPumpMs: 0);

        session.Peers.Add(HostPeer(HostModule));
        agent.PumpOnce();

        Assert.Equal([ClientDoorRefusalReason.None], bridge.DoorRefusalReasonCalls);
    }

    /// <summary>follow_lead=any with the host in dialogue (read off the host's own RemotePeer):
    /// refused with the transient HostBusy reason, not HostMustLead.</summary>
    [Fact]
    public void RefusalReasonIsHostBusyWhenTheHostIsInDialogueUnderAnyMode()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(clientDoorRefusal: true, followLeadMode: FollowLeadMode.Any, combatPumpMs: 0);

        session.Peers.Add(new RemotePeer(CoopHost.HostPeerId, "host",
            new PlayerState(Vector3f.Zero, 0f, HostModule, 20, 0, Flags: PlayerState.FlagInDialogue)));
        agent.PumpOnce();

        Assert.Equal([ClientDoorRefusalReason.HostBusy], bridge.DoorRefusalReasonCalls);
    }

    /// <summary>follow_lead=any with the host in real combat: same transient reason.</summary>
    [Fact]
    public void RefusalReasonIsHostBusyWhenTheHostIsInCombatUnderAnyMode()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(clientDoorRefusal: true, followLeadMode: FollowLeadMode.Any, combatPumpMs: 0);

        session.Peers.Add(new RemotePeer(CoopHost.HostPeerId, "host",
            new PlayerState(Vector3f.Zero, 0f, HostModule, 20, 0, Flags: PlayerState.FlagInCombat)));
        agent.PumpOnce();

        Assert.Equal([ClientDoorRefusalReason.HostBusy], bridge.DoorRefusalReasonCalls);
    }

    /// <summary>follow_lead=host (today's restored behaviour): always HostMustLead, even with
    /// a perfectly free host — the client never leads under this mode at all.</summary>
    [Fact]
    public void RefusalReasonIsHostMustLeadUnderHostOnlyModeEvenWithAFreeHost()
    {
        (CoopAgent agent, TravelBridge bridge, StubSession session) =
            Build(clientDoorRefusal: true, followLeadMode: FollowLeadMode.Host, combatPumpMs: 0);

        session.Peers.Add(HostPeer(HostModule));
        agent.PumpOnce();

        Assert.Equal([ClientDoorRefusalReason.HostMustLead], bridge.DoorRefusalReasonCalls);
    }
}
