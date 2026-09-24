using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// The joining end of a session. Sends its own avatar to the host every tick, receives
/// the combined world back, and repairs world flag divergence by version comparison
/// rather than by streaming flags continuously.
/// </summary>
public sealed partial class CoopClient : ICoopSession
{
    private const int HelloIntervalMs = 500;
    private const int PingIntervalMs = 1000;
    private const int FlagResyncIntervalMs = 500;
    private const int ItemResyncIntervalMs = 500;
    private const int JournalResyncIntervalMs = 500;
    private const int LootResyncIntervalMs = 500;

    /// <summary>Sane bounds for a host-announced <see cref="WelcomePacket.TickRate"/>. A
    /// single wire byte is otherwise unbounded (0 would divide by zero building the tick
    /// timer's period, and a huge value would busy-loop), so a value outside this range is
    /// clamped rather than trusted outright — see <see cref="HandleWelcome"/>.</summary>
    private const int MinTickRate = 5;
    private const int MaxTickRate = 60;

    private readonly SessionSettings _settings;
    private readonly IPEndPoint _hostEndPoint;
    private readonly UdpChannel _channel;
    private readonly SnapshotBuffer _buffer;

    private readonly Lock _gate = new();
    private readonly uint _nonce;

    /// <summary>⚠ OWNED BY THE TICK THREAD (<see cref="Tick"/> and everything it calls), and by
    /// nothing else. See <see cref="_apiBuffer"/>.</summary>
    private readonly byte[] _sendBuffer = new byte[ProtocolConstants.MaxPacketSize];

    /// <summary>⚠ OWNED BY THE RECEIVE-LOOP THREAD (every <c>Handle*</c> method and the replies
    /// they send). See <see cref="_apiBuffer"/>.</summary>
    private readonly byte[] _replyBuffer = new byte[ProtocolConstants.MaxPacketSize];

    /// <summary>
    /// The encode buffer for packets sent from a CALLER's thread rather than from this
    /// client's own tick or receive loop — <see cref="SetFlag"/>, <see cref="DropItem"/>,
    /// <see cref="SetJournalEntry"/>, <see cref="ReportEnemyDamage"/> and friends, every one
    /// of which <c>CoopAgent.PumpOnce</c> calls on the agent pump thread while
    /// <see cref="Tick"/> is encoding its own packets on another.
    ///
    /// ⚠ THIS IS NOT DEFENSIVE. Two threads encoding into one array produce a datagram whose
    /// header comes from one packet and whose body comes from another: it still carries the
    /// magic and the version, so the far end parses it as whatever type won the header byte
    /// and either rejects it as malformed or — worse — accepts a plausible-looking wrong
    /// value. It was first found as a container-lock request the host dropped as malformed
    /// (<see cref="RequestContainerLock"/>, which fixed it for that one method with a private
    /// buffer) and again as a peer's damage total arriving lower than it had ever been, which
    /// a ledger reads as a fresh fight and banks twice. Guarded by <see cref="_apiGate"/>
    /// rather than allocated per call because the busiest of these runs every tick.
    /// </summary>
    private readonly byte[] _apiBuffer = new byte[ProtocolConstants.MaxPacketSize];

    /// <summary>Guards <see cref="_apiBuffer"/>. Never taken while <see cref="_gate"/> is
    /// held — it is the innermost lock in this class, held only across one encode and one
    /// socket send.</summary>
    private readonly Lock _apiGate = new();

    /// <summary>Encodes and sends one packet to the host from a caller's thread — see
    /// <see cref="_apiBuffer"/>. The tick thread's own retransmits route through here too
    /// (the reliable senders' <c>Send*Op</c> helpers are called from both), which costs an
    /// uncontended lock and removes the "which thread is this?" question entirely.</summary>
    private void SendFromCaller<T>(T packet, Func<byte[], T, int> write)
    {
        lock (_apiGate)
        {
            int length = write(_apiBuffer, packet);
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _apiBuffer.AsSpan(0, length));
            }
        }
    }

    private PlayerState _localState = PlayerState.Empty;
    private uint _localPeerId;

    /// <summary>
    /// The id this client was last admitted under. Unlike <see cref="_localPeerId"/> it is
    /// NOT cleared when the link drops, and that is the point: everything the player does
    /// during an outage — a door opened, a quest advanced, an item picked up — still has to be
    /// queued under an identity so it can be delivered when the link returns. Gated on
    /// "have we ever been admitted", because the ops are keyed by (origin, sequence) and an
    /// origin of zero would collide with the next peer to make one up.
    ///
    /// Safe now that a returning peer reclaims its own id (CoopHost's retired table); before
    /// that, the host would have dealt a new id and these ops would arrive under the old one.
    /// The host does not key world state on the origin — it is a dedup key — so even then
    /// nothing would have been misapplied, but the identity is now genuinely stable.
    /// </summary>
    private uint _sessionPeerId;
    private uint _sequence;
    private long _lastHelloMs;
    private long _lastPingMs;
    private long _lastFlagRequestMs;
    private long _lastItemRequestMs;
    private long _lastJournalRequestMs;
    private long _lastLootRequestMs;
    private uint _nextItemSequence = 1;
    private long _lastInboundMs;
    private double _roundTripMs;
    private string _rejectReason = string.Empty;
    private bool _reconnecting;

    /// <summary>Backing field for <see cref="HostLost"/> — see that property's doc comment.
    /// Set from <see cref="HostLossRule.ShouldDeclare"/> in <see cref="Tick"/> and from
    /// <see cref="HandleHostBye"/>; cleared in <see cref="HandleWelcome"/> the moment a fresh
    /// admission lands.</summary>
    private bool _hostLost;

    /// <summary>The third host-loss trigger's bookkeeping — see <see cref="HostAwayRule"/>.
    /// <see cref="_hostAwayNow"/> is the host's own <see cref="PlayerState.FlagAway"/> as of
    /// the last world snapshot that carried its entry (receive-loop thread → volatile, read
    /// by <see cref="Tick"/>); <see cref="_hostAwaySinceMs"/> is when that first went true, 0
    /// while it is false; <see cref="_hostLostByAway"/> records that <see cref="_hostLost"/> was
    /// raised by THIS trigger, so only this trigger's falling edge (the host coming back into
    /// the world, no fresh Welcome involved) is allowed to lower it again.</summary>
    private volatile bool _hostAwayNow;
    private long _hostAwaySinceMs;
    private bool _hostLostByAway;

    /// <inheritdoc cref="IPeerSession.HostAway"/>
    public bool HostAway => _hostLostByAway;

    /// <summary>Real gaps between successive <see cref="SendStateUpdate"/> calls — what
    /// <c>lagstats</c> shows so a live round can see the actual send cadence, not just the
    /// configured tick rate. See docs/research/lag-audit.md.</summary>
    private readonly IntervalTracker _sendIntervals = new();

    /// <summary>Real gaps between successive liveness-bearing inbound packets — the same
    /// traffic <see cref="_lastInboundMs"/> already tracks the age of.</summary>
    private readonly IntervalTracker _recvIntervals = new();

    /// <summary>
    /// Set on every Welcome, cleared once all four world stores have been re-requested.
    ///
    /// ⚠ THE VERSION TRIPWIRE ALONE CANNOT BE TRUSTED ACROSS AN OUTAGE. Each store's version
    /// is a count of local changes, not a fingerprint of its contents, so two sets that
    /// diverged by the same NUMBER of changes compare equal and no resync is ever requested —
    /// permanent silent divergence. Ordinarily the client's own ops bump the host and break
    /// the tie, but an outage is exactly the window in which both ends change independently,
    /// and a client change the host already holds at that value bumps nothing. So a
    /// reconnection asks for everything unconditionally instead of waiting to be told it
    /// needs it.
    ///
    /// The DIGEST each store now carries beside its version (<see cref="EntryDigest"/>) closes
    /// the same hole for the case that matters more — a tie reached WHILE connected, not just
    /// across an outage — but this forced flag stays: an outage is still the one window where
    /// bumping-and-breaking-the-tie cannot be relied on at all, on either side.
    /// </summary>
    /// <remarks>
    /// ⚠ ONE FLAG PER CHANNEL, SPENT WHERE THE REQUEST GOES OUT. There used to be a single
    /// <c>_resyncOnConnect</c> disarmed only once all four reliable senders were quiet, on the
    /// theory that a forced resync should "wait its turn" rather than be spent unused. Three
    /// of the four channels have nothing to wait for (their snapshots merge rather than
    /// replace — see <see cref="WorldResyncRule"/>), so all that rule did was tie every
    /// channel's forced ask to the slowest sender on the link.
    /// </remarks>
    private bool _forceFlagResync;

    /// <inheritdoc cref="_forceFlagResync"/>
    private bool _forceItemResync;

    /// <inheritdoc cref="_forceFlagResync"/>
    private bool _forceJournalResync;

    /// <inheritdoc cref="_forceFlagResync"/>
    private bool _forceLootResync;

    /// <summary>
    /// The tick rate actually driving <see cref="RunAsync"/>'s send/poll loop right now.
    /// Starts at the local <see cref="SessionSettings.TickRate"/> (nothing has told us
    /// otherwise yet) and is overwritten in <see cref="HandleWelcome"/> once the host
    /// announces its own — see <see cref="WelcomePacket.TickRate"/>'s doc for why the two can
    /// disagree. <c>volatile</c>: written on the receive-loop thread, read every tick by
    /// <see cref="RunAsync"/>'s own thread.
    /// </summary>
    private volatile int _effectiveTickRate;

    /// <summary>The tick rate this client is actually running at — the local setting until a
    /// Welcome arrives, the host's announced rate (clamped) after. Exposed for
    /// <c>lagstats</c>-style readouts and tests; drives <see cref="RunAsync"/>'s send/poll
    /// cadence, not merely reported alongside it.</summary>
    public int EffectiveTickRate => _effectiveTickRate;

    /// <summary>
    /// When the host's world snapshot stopped listing this client, or 0 while it still does.
    ///
    /// ⚠ THE ONE-WAY OUTAGE. Silence is the only disconnect signal this client had, and a
    /// half-open link produces none: if only the UPSTREAM direction fails (a NAT rebinding, a
    /// firewall that drops one way, an ISP fault), the host stops hearing us and expires the
    /// slot after five seconds while its broadcasts keep arriving here perfectly. The link
    /// then recovers, and this client goes on sending state updates for a peer id the host no
    /// longer knows — every one of which the host drops on the floor. It receives the world,
    /// renders everyone, and is invisible to all of them, permanently, with nothing in its own
    /// state that says anything is wrong. The host's snapshot is a complete roster, so "I am
    /// not in it" is the missing signal, and it is already on the wire.
    /// </summary>
    private long _absentFromSnapshotSinceMs;

    public CoopClient(SessionSettings settings, IPEndPoint hostEndPoint)
    {
        _settings = settings;
        _hostEndPoint = hostEndPoint;
        _effectiveTickRate = settings.TickRate;

        // Bind an ephemeral port: two clients on one machine must not fight over 27015.
        _channel = new UdpChannel(0);
        _buffer = new SnapshotBuffer(
            settings.InterpolationDelayMs, 1000 / settings.TickRate, settings.DejitterEnabled);
        // The identity the host keys our peer slot on. A configured one survives the process,
        // so a relaunch reclaims the slot instead of joining as a stranger; 0 keeps the old
        // per-session behaviour. See PeerIdentity.
        _nonce = settings.PeerIdentity != 0 ? settings.PeerIdentity : PeerIdentity.Mint();
        _handlers = new Dictionary<PacketType, PacketHandler>
        {
            [PacketType.Welcome] = HandleWelcome,
            [PacketType.Reject] = HandleReject,
            [PacketType.WorldSnapshot] = HandleWorldSnapshot,
            [PacketType.FlagAck] = HandleFlagAck,
            [PacketType.FlagSnapshot] = HandleFlagSnapshot,
            [PacketType.ItemAck] = HandleItemAck,
            [PacketType.JournalAck] = HandleJournalAck,
            [PacketType.ContainerLootAck] = HandleContainerLootAck,
            [PacketType.DialogueGrant] = HandleDialogueGrant,
            [PacketType.DialogueGrantAck] = HandleDialogueGrantAck,
            [PacketType.ClassGrant] = HandleClassGrant,
            [PacketType.ClassGrantAck] = HandleClassGrantAck,
            [PacketType.PlacedMineIntentAck] = HandlePlacedMineIntentAck,
            [PacketType.PlacedMineRoster] = HandlePlacedMineRoster,
            [PacketType.EnemyEffectAck] = HandleEnemyEffectAck,
            [PacketType.JournalSnapshot] = HandleJournalSnapshot,
            [PacketType.EnemySnapshot] = HandleEnemySnapshot,
            [PacketType.WorldDigest] = HandleWorldDigest,
            [PacketType.PartySnapshot] = HandlePartySnapshot,
            [PacketType.PartyRoster] = HandlePartyRoster,
            [PacketType.DialogueSnapshot] = HandleDialogueSnapshot,
            [PacketType.TriggerRelay] = HandleTriggerRelay,
            [PacketType.PhantomDamage] = HandlePhantomDamage,
            [PacketType.PeerEffect] = HandlePeerEffect,
            [PacketType.Bark] = HandleBark,
            [PacketType.TransitionMovies] = HandleTransitionMovies,
            [PacketType.TransitionAnnounce] = HandleTransitionAnnounce,
            [PacketType.MidModuleMovie] = HandleMidModuleMovie,
            [PacketType.GalaxyDeparture] = HandleGalaxyDeparture,
            [PacketType.InteractionLock] = HandleInteractionLock,
            [PacketType.LockResponse] = HandleLockResponse,
            [PacketType.Chat] = HandleChat,
            [PacketType.ProgressionSnapshot] = HandleProgressionSnapshot,
            [PacketType.CompanionXpSnapshot] = HandleCompanionXpSnapshot,
            [PacketType.ItemSnapshot] = HandleItemSnapshot,
            [PacketType.ContainerLoot] = HandleContainerLoot,
            [PacketType.MaterialisedLoot] = HandleMaterialisedLoot,
            [PacketType.PartyPoolOp] = HandlePartyPoolOp,
            [PacketType.PartyPoolAck] = HandlePartyPoolAck,
            [PacketType.StoreOp] = HandleStoreOp,
            [PacketType.StoreAck] = HandleStoreAck,
            [PacketType.CreatureLiveness] = HandleCreatureLiveness,
            [PacketType.PartyPoolSnapshotV2] = HandlePartyPoolSnapshotV2,
            [PacketType.PartyPoolIntentAckV2] = HandlePartyPoolIntentAckV2,
            [PacketType.StoreSnapshotV2] = HandleStoreSnapshotV2,
            [PacketType.StoreIntentAckV2] = HandleStoreIntentAckV2,
            [PacketType.Pong] = HandlePong,
            [PacketType.HostBye] = HandleHostBye,
            [PacketType.None] = HandleNone,
        };
    }

    public uint LocalPeerId => _localPeerId;

    /// <summary>This client's transport fault injector. Arming it partitions this one player
    /// and leaves the rest of the session running, which is the ordinary "one player's
    /// router blipped" case.</summary>
    public NetworkSim Network => _channel.Sim;

    public bool IsConnected => _localPeerId != 0;

    /// <inheritdoc cref="IPeerSession.HostLost"/>
    public bool HostLost => _hostLost;

    public string LocalName => _settings.PlayerName;

    public void PublishLocalState(in PlayerState state)
    {
        lock (_gate)
        {
            _localState = state;
        }
    }

    public IReadOnlyList<RemotePeer> GetRemotePeers() => _buffer.Sample(SessionClock.NowMs);

    public SessionStats GetStats()
    {
        long now = SessionClock.NowMs;

        return new SessionStats
        {
            Connected = IsConnected,
            Reconnecting = _reconnecting,
            LocalPeerId = _localPeerId,
            PeerCount = _buffer.PeerCount,
            RoundTripMs = _roundTripMs,
            PacketsSent = _channel.PacketsSent,
            PacketsReceived = _channel.PacketsReceived,
            PacketsDropped = _channel.MalformedPackets,
            VersionMismatches = _channel.VersionMismatchPackets,
            BytesSent = _channel.BytesSent,
            BytesReceived = _channel.BytesReceived,
            LastInboundAgeMs = _lastInboundMs == 0 ? 0 : now - _lastInboundMs,
            FlagVersion = Flags.Version,
            PendingFlagOps = _flagSender.PendingCount,
            LivenessAnomalies = _livenessAnomalyCount,
            PhantomInterpolationDelayMs = _settings.InterpolationDelayMs,
            RecentSendIntervalsMs = _sendIntervals.Snapshot(),
            RecentRecvIntervalsMs = _recvIntervals.Snapshot(),
            Status = _rejectReason.Length > 0
                ? $"rejected by host: {_rejectReason}"
                : IsConnected
                    ? $"connected to {_hostEndPoint} as peer {_localPeerId}"
                    : _reconnecting
                        ? $"reconnecting to {_hostEndPoint}..."
                        : $"connecting to {_hostEndPoint}...",
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Task receiveLoop = ReceiveLoopAsync(cancellationToken);

        try
        {
            // Owned entirely by this method/thread — HandleWelcome (receive-loop thread)
            // only ever writes _effectiveTickRate, never touches the timer itself.
            // PeriodicTimer's period is fixed at construction, so adopting a different
            // host-announced rate means swapping the instance, not mutating one in place.
            int runningTickRate = _effectiveTickRate;
            PeriodicTimer timer = new(TimeSpan.FromMilliseconds(1000.0 / runningTickRate));
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    Tick();

                    if (_effectiveTickRate != runningTickRate)
                    {
                        runningTickRate = _effectiveTickRate;
                        timer.Dispose();
                        timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / runningTickRate));
                    }
                }
            }
            finally
            {
                timer.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }

        SendBye();
        await receiveLoop.ConfigureAwait(false);
    }

    private void Tick()
    {
        long now = SessionClock.NowMs;

        // Link-loss detection: once connected, silence longer than the peer timeout means
        // the host is gone (crashed, left, or a network drop). Fall back to the handshake
        // so the SAME client — same nonce — rejoins automatically when the host returns,
        // and the host restores its slot rather than leaving a ghost. This is what makes
        // "the host restarted" or "the router blipped" recoverable in-game.
        if (IsConnected && _lastInboundMs != 0)
        {
            long silenceMs = now - _lastInboundMs;

            // Checked BEFORE ForgetSession, which is what a timeout of the same duration hits
            // in this very call otherwise: ForgetSession zeroes _lastInboundMs, and evaluating
            // in the other order would read a silence of 0 on the tick that matters most. With
            // the default HostLossTimeoutMs == PeerTimeoutMs the two fire in the same tick —
            // declare-then-forget — which is deliberate: host loss is declared in the exact
            // tick the client gives up and starts re-Helloing, not later.
            if (HostLossRule.ShouldDeclare(
                _settings.HostLossReleaseEnabled, _hostLost, silenceMs, _settings.HostLossTimeoutMs))
            {
                _hostLost = true;
            }

            if (silenceMs > _settings.PeerTimeoutMs)
            {
                ForgetSession();
            }
        }

        // The host is still talking, but says it is not in the world (main menu, load screen).
        // Sustained long enough, that is host loss too — the SAME edge the silence path raises,
        // so the agent's release pass is the one already proven live — and the host's flag
        // clearing is the falling edge that resumes mirroring. See HostAwayRule.
        if (IsConnected)
        {
            bool hostAwayNow = _hostAwayNow;
            _hostAwaySinceMs = HostAwayRule.Track(_hostAwaySinceMs, hostAwayNow, now);

            if (HostAwayRule.ShouldDeclare(
                _settings.HostLossReleaseEnabled, _settings.HostAwayTimeoutMs, _hostLost, _hostAwaySinceMs, now))
            {
                _hostLost = true;
                _hostLostByAway = true;
            }
            else if (HostAwayRule.ShouldRestore(_hostLostByAway, hostAwayNow))
            {
                _hostLost = false;
                _hostLostByAway = false;
            }
        }

        if (!IsConnected)
        {
            if (now - _lastHelloMs >= HelloIntervalMs && _rejectReason.Length == 0)
            {
                _lastHelloMs = now;
                SendHello();
            }

            // ⚠ Prune while DOWN as well as while up. This used to sit at the bottom of the
            // tick, past this early return, so a client whose host vanished kept every peer
            // it had ever seen frozen at their last position for as long as the outage lasted
            // — and the agent goes on writing those into the game, so the other player stood
            // there like a statue rather than disappearing.
            _buffer.PruneStale(now, _settings.PeerTimeoutMs);
            return;
        }

        SendStateUpdate(now);
        SendDialogueRequest();
        SendInteractionClaim();
        SendPhantomDamage();
        SendPeerEffects();
        SendEncounterTriggers();
        SendTriggerEntries();
        FlushChat();
        FlushPartyPoolResyncRequest();
        FlushBarks();
        FlushTransitionMovies();
        FlushTransitionAnnounce();
        FlushMidModuleMovies();
        FlushGalaxyDepartures();
        FlushClassGrants(now);
        FlushPlacedMineIntents(now);
        FlushEnemyEffects(now);

        if (now - _lastPingMs >= PingIntervalMs)
        {
            _lastPingMs = now;
            SendPing(now);
        }

        foreach (FlagOpPacket op in _flagSender.CollectDue(now))
        {
            SendFlagOp(op);
        }

        foreach (ItemOpPacket op in _itemSender.CollectDue(now))
        {
            SendItemOp(op);
        }

        foreach (JournalOpPacket op in _journalSender.CollectDue(now))
        {
            SendJournalOp(op);
        }

        // One recipient — the host — which relays onward to the other clients and acks
        // with its own peer id (CoopHost.HandleContainerLoot acks unconditionally).
        foreach (ContainerLootPacket op in _lootSender.CollectDue(LootRecipients, now))
        {
            int lootLength = PacketCodec.Write(_sendBuffer, op);
            if (lootLength > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, lootLength));
            }
        }

        // Same one-recipient shape as ContainerLoot above.
        foreach (DialogueGrantPacket op in _dialogueGrantSender.CollectDue(LootRecipients, now))
        {
            int grantLength = PacketCodec.Write(_sendBuffer, op);
            if (grantLength > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, grantLength));
            }
        }

        // Same one-recipient shape as ContainerLoot above.
        foreach (PartyPoolOpPacket op in _partyPoolSender.CollectDue(LootRecipients, now))
        {
            int poolLength = PacketCodec.Write(_sendBuffer, op);
            if (poolLength > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, poolLength));
            }
        }

        // Same one-recipient shape as ContainerLoot above.
        foreach (StoreOpPacket op in _storeSender.CollectDue(LootRecipients, now))
        {
            int storeLength = PacketCodec.Write(_sendBuffer, op);
            if (storeLength > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, storeLength));
            }
        }

        RetransmitPartyPoolIntentsV2(now);
        RetransmitStoreIntentsV2(now);

        // Report our in-world digest so the host can judge the world-time match (globals,
        // save, module GIT). Mostly stable — only the module fields move, on a module
        // change — so a low rate is plenty; only once at least one field has resolved.
        if (!_localWorld.IsEmpty && now - _lastWorldDigestMs >= 1000)
        {
            _lastWorldDigestMs = now;
            int length = PacketCodec.Write(_sendBuffer, new WorldDigestPacket(_localPeerId, _localWorld));
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }

        _buffer.PruneStale(now, _settings.PeerTimeoutMs);
    }

    /// <summary>
    /// Drops back to the handshake, keeping everything that is not about this connection.
    ///
    /// Reached two ways: the host went silent (the ordinary drop), or the host's roster stops
    /// listing us while its traffic keeps arriving (the half-open link). Both mean "we are no
    /// longer a member of this session", and the cure for both is to say Hello again with the
    /// SAME nonce, which is what makes the host hand back the same peer id and everything
    /// keyed on it — cumulative damage, the progression ledger, the phantom slot — continue
    /// rather than start again.
    ///
    /// What is deliberately NOT reset: the reliable senders (their ops are world truth still
    /// owed to the host), the stores (a client's world does not un-happen because a router
    /// blinked), and the roster/progression levels (stale, but the last thing the host said,
    /// and applying nothing is worse than applying slightly old truth).
    /// </summary>

    private void SendStateUpdate(long now)
    {
        _sendIntervals.Record(now);

        PlayerState state;
        lock (_gate)
        {
            state = _localState;
        }

        StateUpdatePacket packet = new(_localPeerId, ++_sequence, now, state);
        int length = PacketCodec.Write(_sendBuffer, packet);
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    private void SendPing(long now)
    {
        int length = PacketCodec.Write(_sendBuffer, new PingPacket(_localPeerId, now));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Datagram? datagram = await _channel.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            if (datagram is not { } received)
            {
                return;
            }

            try
            {
                Handle(received);
            }
            catch (Exception)
            {
                _channel.NoteMalformed();
            }
        }
    }

    /// <summary>One dispatch target per inbound <see cref="PacketType"/>, built once in
    /// the constructor and never touched again on the hot path — replaces the old switch
    /// on <see cref="PacketType"/> with a single dictionary lookup. Every case the old
    /// switch handled is registered here (see each feature's own file); an unregistered
    /// (unknown/garbled) type is silently ignored, exactly like the switch's <c>default</c>
    /// arm was.</summary>
    private delegate void PacketHandler(IPEndPoint from, ReadOnlySpan<byte> span, long now);

    private readonly Dictionary<PacketType, PacketHandler> _handlers;

    private void Handle(in Datagram datagram)
    {
        // Ignore anything that did not come from the host we joined.
        if (!datagram.From.Address.Equals(_hostEndPoint.Address) || datagram.From.Port != _hostEndPoint.Port)
        {
            return;
        }

        ReadOnlySpan<byte> span = datagram.Span;
        long now = SessionClock.NowMs;
        PacketType type = PacketCodec.PeekType(span);

        if (type == PacketType.None && TryHandleVersionMismatchReject(span))
        {
            return;
        }

        // ⚠ NOT EVERY PACKET PROVES WE ARE STILL IN THE SESSION. The liveness clock is the
        // only thing that notices a dropped link, and a Pong or an op ack is sent by the host
        // to anyone who asks — member or not. So a client the host has expired (a one-way
        // outage: our upstream failed, its broadcasts kept arriving) could keep its own clock
        // fresh with its own pings, and never work out that it had become a ghost. Only
        // traffic the host sends to MEMBERS counts: the snapshots, the levels, the handshake.
        if (type is not (PacketType.Pong or PacketType.FlagAck or PacketType.ItemAck
            or PacketType.JournalAck or PacketType.ContainerLootAck or PacketType.DialogueGrantAck
            or PacketType.ClassGrantAck or PacketType.EnemyEffectAck or PacketType.PlacedMineIntentAck
            or PacketType.PartyPoolAck or PacketType.StoreAck))
        {
            _lastInboundMs = now;
            _recvIntervals.Record(now);
        }

        if (_handlers.TryGetValue(type, out PacketHandler? handler))
        {
            handler(datagram.From, span, now);
        }
    }

    private void HandleNone(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        _channel.NoteMalformed();
    }
}
