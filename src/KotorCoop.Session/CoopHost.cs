using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// The authoritative end of a session. The host plays too: it is peer 1, and its own
/// avatar goes into the same snapshot everyone else's does.
///
/// Topology is a star relay rather than a mesh. Every client sends only to the host, and
/// the host broadcasts the combined world. That costs one extra hop of latency between
/// two clients but means exactly one machine decides what is true, which is the same
/// trade FromSoftware's netcode makes and the reason Seamless Co-op saves host progress.
/// </summary>
public sealed partial class CoopHost : ICoopSession
{
    private sealed class PeerConnection
    {
        public required uint Id;
        public required IPEndPoint EndPoint;
        public required string Name;

        /// <summary>The client's stable nonce, so a peer that reconnects from a new port
        /// reclaims this slot instead of appearing as a second player.</summary>
        public required uint Nonce;

        public PlayerState LastState = PlayerState.Empty;
        public long LastSeenMs;

        /// <summary>The install-time verdict from this peer's Hello digest.</summary>
        public CompatibilityVerdict InstallVerdict = new(RefusalReason.None, SessionCapabilities.All, []);

        /// <summary>The peer's in-world digest (globals catalogue, save identity, current
        /// module + GIT), every field 0/empty until it reports one.</summary>
        public WorldDigest World = WorldDigest.Empty;

        /// <summary>Absorbs the transient save-identity mismatch an ordinary module
        /// transition produces while this peer and the host cross at slightly different
        /// times — see <see cref="SaveIdentitySettle"/>. One per peer: each crossing is its
        /// own timer, and a peer that reconnects gets a fresh <see cref="PeerConnection"/>
        /// and therefore a fresh one too.</summary>
        public readonly SaveIdentitySettle SaveSettle = new();

        /// <summary>Tells this peer's same-world saves (the engine's own transition autosave)
        /// apart from a genuinely different save — see <see cref="Protocol.SaveLineage"/>.
        /// One per peer for the same reason as <see cref="SaveSettle"/>.</summary>
        public readonly SaveLineage SaveLineage = new();
    }

    /// <summary>
    /// A peer that has gone — timed out or said Bye — remembered by its nonce so that if it
    /// comes back it comes back as ITSELF.
    ///
    /// ⚠ THIS IS THE LOAD-BEARING PIECE OF RECONNECTION. The nonce match in HandleHello only
    /// ever searched the LIVE table, so it worked for a blip shorter than the peer timeout and
    /// for nothing else. A blackout longer than five seconds expired the slot, the returning
    /// Hello found no match, and the same player rejoined as a brand new peer id — which is
    /// not cosmetic, because peer id is the key of every cumulative channel in the session:
    /// the damage ledger re-banked that peer's whole running total against the enemy it was
    /// fighting (rule 3's "a new generation resets history" cannot help — the GENERATION was
    /// unchanged, only the identity moved), the incoming phantom-damage table added a second
    /// entry on top of the first and handed the local player every hit they had ever taken a
    /// second time, the progression ledger adopted the returning peer's totals as first-sight
    /// and silently dropped whatever it earned during the outage, and every other client kept
    /// the old id's phantom standing there for another full timeout while the new one spawned
    /// beside it.
    ///
    /// THE RULE, stated once so it is not re-litigated at each call site: an identity must
    /// live as long as the CUMULATIVE COUNTERS KEYED ON IT, and those are scoped to the module
    /// (the plugin's damage table, the ledger's epoch), not to the connection. So both
    /// departures retire — a timeout and a clean Bye alike — and the case where the counters
    /// really do restart takes care of itself, because a peer whose agent was rebuilt arrives
    /// with a new nonce and is correctly treated as first sight. <see cref="HandleBye"/> has
    /// the full argument and the binary evidence behind it.
    /// </summary>
    private sealed record RetiredPeer(uint Id, string Name, long RetiredAtMs);

    public const uint HostPeerId = 1;

    /// <summary>How many departed identities to hold. Generous: an identity is 20-odd bytes
    /// and forgetting one is what produces the duplicate-phantom bug above, so the bound is
    /// here to stop unbounded growth over a long session, not to expire anybody.</summary>
    private const int MaxRetiredPeers = ProtocolConstants.MaxPeers * 8;

    private readonly SessionSettings _settings;
    private readonly UdpChannel _channel;
    private readonly SnapshotBuffer _buffer;
    private readonly Dictionary<uint, PeerConnection> _peers = [];

    /// <summary>Departed peers by client nonce, so a rejoin reclaims its own id.</summary>
    private readonly Dictionary<uint, RetiredPeer> _retired = [];
    private readonly Lock _gate = new();
    private readonly uint _sessionId;

    // Two buffers, because Tick and the receive loop run on different tasks and would
    // otherwise interleave writes into the same array.
    private readonly byte[] _sendBuffer = new byte[ProtocolConstants.MaxPacketSize];
    private readonly byte[] _replyBuffer = new byte[ProtocolConstants.MaxPacketSize];

    private PlayerState _localState = PlayerState.Empty;

    /// <summary>
    /// Whether the host has ever published an avatar, gating its own snapshot entry.
    ///
    /// ⚠ NOT A MICRO-OPTIMISATION. <see cref="PlayerState.Empty"/> is finite, so it passes
    /// every plausibility check downstream and reads as a living, standing player at the
    /// origin. A host still at the main menu therefore advertised itself as an ALIVE PEER to
    /// everyone connected — and the party-wipe rule needs every player down, so with a
    /// permanently-standing ghost in the roster a client's whole party could never count as
    /// wiped and its game-over stayed suppressed forever. "I have nothing true to say yet" and
    /// "I am unhurt at (0, 0, 0)" are not the same sentence; the roster now only carries the
    /// first once it becomes the second.
    /// </summary>
    private bool _hasLocalState;

    private uint _nextPeerId = HostPeerId + 1;
    private long _lastInboundMs;

    /// <summary>Real gaps between successive world-snapshot broadcasts — what <c>lagstats</c>
    /// shows so a live round can see the actual send cadence, not just the configured tick
    /// rate. See docs/research/lag-audit.md.</summary>
    private readonly IntervalTracker _sendIntervals = new();

    /// <summary>Real gaps between successive inbound packets from any peer.</summary>
    private readonly IntervalTracker _recvIntervals = new();

    /// <summary>
    /// This host's public IP:port as observed via STUN (roadmap item 24), or null until
    /// discovered — or forever, when <see cref="SessionSettings.StunServers"/> is empty. See
    /// <see cref="PublicAddressDiscovery"/>.
    /// </summary>
    public IPEndPoint? PublicEndPoint => _publicAddress?.PublicEndPoint;

    /// <summary>Whether a UPnP router mapping was found for this session's port. See
    /// <see cref="UpnpMappingService"/>; <see cref="UpnpMappingStatus.Disabled"/> means
    /// <see cref="SessionSettings.UpnpEnabled"/> was off.</summary>
    public UpnpMappingStatus UpnpStatus => _upnp?.Status ?? UpnpMappingStatus.Disabled;

    private readonly PublicAddressDiscovery? _publicAddress;
    private readonly UpnpMappingService? _upnp;

    public CoopHost(SessionSettings settings)
    {
        _settings = settings;
        _channel = new UdpChannel(settings.Port);
        _buffer = new SnapshotBuffer(
            settings.InterpolationDelayMs, 1000 / settings.TickRate, settings.DejitterEnabled);
        _sessionId = (uint)Random.Shared.Next(1, int.MaxValue);

        // Roadmap item 24: hosting over the internet without a relay. Both are best-effort and
        // additive — neither can make a session fail to start, only fail to be reachable from
        // outside the LAN, which is exactly the pre-existing behaviour when both are disabled.
        if (settings.StunServers.Count > 0)
        {
            _publicAddress = new PublicAddressDiscovery(_channel, settings.StunServers);
            _publicAddress.Start();
        }

        if (settings.UpnpEnabled)
        {
            _upnp = new UpnpMappingService(settings.Port);
            _upnp.Start();
        }

        _handlers = new Dictionary<PacketType, PacketHandler>
        {
            [PacketType.Hello] = HandleHello,
            [PacketType.StateUpdate] = HandleStateUpdate,
            [PacketType.FlagOp] = HandleFlagOp,
            [PacketType.FlagSyncRequest] = HandleFlagSyncRequest,
            [PacketType.ItemOp] = HandleItemOp,
            [PacketType.ItemSyncRequest] = HandleItemSyncRequest,
            [PacketType.ContainerLoot] = HandleContainerLoot,
            [PacketType.ContainerLootAck] = HandleContainerLootAck,
            [PacketType.LootSyncRequest] = HandleLootSyncRequest,
            [PacketType.PartyPoolOp] = HandlePartyPoolOp,
            [PacketType.PartyPoolAck] = HandlePartyPoolAck,
            [PacketType.PartyPoolSyncRequest] = HandlePartyPoolSyncRequest,
            [PacketType.StoreOp] = HandleStoreOp,
            [PacketType.StoreAck] = HandleStoreAck,
            [PacketType.StoreSyncRequest] = HandleStoreSyncRequest,
            [PacketType.PartyPoolIntentV2] = HandlePartyPoolIntentV2,
            [PacketType.StoreIntentV2] = HandleStoreIntentV2,
            [PacketType.MaterialisedLootAck] = HandleMaterialisedLootAck,
            [PacketType.JournalOp] = HandleJournalOp,
            [PacketType.JournalSyncRequest] = HandleJournalSyncRequest,
            [PacketType.EnemyDamage] = HandleEnemyDamage,
            [PacketType.CompanionDamage] = HandleCompanionDamage,
            [PacketType.ProgressionReport] = HandleProgressionReport,
            [PacketType.CompanionXpReport] = HandleCompanionXpReport,
            [PacketType.WorldDigest] = HandleWorldDigest,
            [PacketType.PhantomDamage] = HandlePhantomDamage,
            [PacketType.PeerEffect] = HandlePeerEffect,
            [PacketType.EncounterTrigger] = HandleEncounterTrigger,
            [PacketType.TriggerRelay] = HandleTriggerRelay,
            [PacketType.MidModuleMovie] = HandleMidModuleMovie,
            [PacketType.GalaxyDeparture] = HandleGalaxyDeparture,
            [PacketType.DialogueRequest] = HandleDialogueRequest,
            [PacketType.Chat] = HandleChat,
            [PacketType.Bark] = HandleBark,
            [PacketType.TransitionMovies] = HandleTransitionMovies,
            [PacketType.TransitionAnnounce] = HandleTransitionAnnounce,
            [PacketType.InteractionClaim] = HandleInteractionClaim,
            [PacketType.DialogueGrant] = HandleDialogueGrant,
            [PacketType.DialogueGrantAck] = HandleDialogueGrantAck,
            [PacketType.Ping] = HandlePing,
            [PacketType.Bye] = HandleBye,
            [PacketType.WorldTransferRequest] = HandleWorldTransferRequest,
            [PacketType.WorldTransferChunkStatus] = HandleWorldTransferChunkStatus,
            [PacketType.WorldTransferComplete] = HandleWorldTransferComplete,
            [PacketType.None] = HandleNone,
        };
    }

    public uint LocalPeerId => HostPeerId;

    public bool IsConnected => true;

    public string LocalName => _settings.PlayerName;

    public IPEndPoint ListenEndPoint => _channel.LocalEndPoint;

    /// <summary>The host's transport fault injector. Arming it partitions the host from
    /// every client at once, which is the "the server's uplink died" case.</summary>
    public NetworkSim Network => _channel.Sim;

    public void PublishLocalState(in PlayerState state)
    {
        lock (_gate)
        {
            _localState = state;
            _hasLocalState = true;
        }
    }

    public IReadOnlyList<RemotePeer> GetRemotePeers() => _buffer.Sample(SessionClock.NowMs);

    /// <summary>Latched so an over-full lobby writes one line, not one per tick.</summary>
    private bool _snapshotOverflowLogged;

    /// <summary>Total <see cref="WorldSnapshotPacket"/> pages actually written and sent, across
    /// every tick this host has run — see <see cref="SessionStats.WorldSnapshotPagesSent"/>.
    /// A healthy single-page lobby increments this by one per tick, same as before pagination
    /// existed; a lobby spanning several pages increments it by more, which is the visible
    /// signal that pagination is doing real work rather than sitting dormant.</summary>
    private long _worldSnapshotPagesSent;

    public SessionStats GetStats()
    {
        int peerCount;
        lock (_gate)
        {
            peerCount = _peers.Count;
        }

        return new SessionStats
        {
            Connected = true,
            Reconnecting = false, // a host never reconnects to itself; see SessionStats.Reconnecting
            LocalPeerId = HostPeerId,
            PeerCount = peerCount,
            PacketsSent = _channel.PacketsSent,
            PacketsReceived = _channel.PacketsReceived,
            PacketsDropped = _channel.MalformedPackets,
            VersionMismatches = _channel.VersionMismatchPackets,
            BytesSent = _channel.BytesSent,
            BytesReceived = _channel.BytesReceived,
            LastInboundAgeMs = _lastInboundMs == 0 ? 0 : SessionClock.NowMs - _lastInboundMs,
            FlagVersion = Flags.Version,
            LivenessAnomalies = _livenessAnomalyCount,
            PhantomInterpolationDelayMs = _settings.InterpolationDelayMs,
            RecentSendIntervalsMs = _sendIntervals.Snapshot(),
            RecentRecvIntervalsMs = _recvIntervals.Snapshot(),
            WorldSnapshotPagesSent = _worldSnapshotPagesSent,
            Status = peerCount == 0
                ? $"hosting on {_channel.LocalEndPoint} (waiting for players)"
                : $"hosting on {_channel.LocalEndPoint}",
        };
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Task receiveLoop = ReceiveLoopAsync(cancellationToken);

        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(1000.0 / _settings.TickRate));

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                Tick();
            }
        }
        catch (OperationCanceledException)
        {
        }

        // A clean shutdown (not a crash — nothing runs the tick loop's own catch above on a
        // crash) tells every currently-connected client right away, rather than leaving them to
        // notice only after PeerTimeoutMs of silence. See PacketType.HostBye's own doc comment.
        BroadcastHostBye();

        await receiveLoop.ConfigureAwait(false);
    }

    private void Tick()
    {
        long now = SessionClock.NowMs;

        List<SnapshotEntry> entries;
        List<PeerConnection> recipients;

        // Peers whose world-time verdict came back a REFUSAL rather than a degradation, with
        // the reason to tell them. Filled under the lock, told outside it.
        List<(IPEndPoint EndPoint, string Reason)>? evicted = null;

        lock (_gate)
        {
            List<uint>? expired = null;
            foreach ((uint id, PeerConnection peer) in _peers)
            {
                if (now - peer.LastSeenMs > _settings.PeerTimeoutMs)
                {
                    (expired ??= []).Add(id);
                    continue;
                }

                // ⚠ A refusal has to be CARRIED OUT to mean anything. The world-time compare
                // can only refuse under RequireIdenticalSave, and before this it did so by
                // narrowing this peer's capabilities to nothing — which, because the session
                // takes the narrowest set across all peers, silently switched every channel
                // off for EVERYONE while the refused peer stayed connected and never learned
                // why. Now they are told, in the same typed form the Hello path uses, and
                // dropped; the rest of the lobby is untouched.
                CompatibilityVerdict verdict = EffectiveVerdict(peer);
                if (!verdict.Admitted)
                {
                    (expired ??= []).Add(id);
                    (evicted ??= []).Add((peer.EndPoint, verdict.Summary));
                }
            }

            if (expired is not null)
            {
                foreach (uint id in expired)
                {
                    ForgetPeer(id, now);
                }
            }

            entries = new List<SnapshotEntry>(_peers.Count + 1);

            // The host advertises itself only once it has an avatar to advertise — see
            // _hasLocalState.
            if (_hasLocalState)
            {
                entries.Add(new SnapshotEntry(HostPeerId, _settings.PlayerName, _localState));
            }

            foreach (PeerConnection peer in _peers.Values)
            {
                entries.Add(new SnapshotEntry(peer.Id, peer.Name, peer.LastState));
            }

            recipients = [.. _peers.Values];
        }

        if (evicted is not null)
        {
            foreach ((IPEndPoint endPoint, string reason) in evicted)
            {
                int rejectLength = PacketCodec.Write(_sendBuffer, new RejectPacket(reason));
                if (rejectLength > 0)
                {
                    _channel.Send(endPoint, _sendBuffer.AsSpan(0, rejectLength));
                }
            }
        }

        _sendIntervals.Record(now);

        // ⚠ Page rather than drop-whole. A full lobby with long names, long module names and
        // equipped gear used to overrun MaxPacketSize outright, and the old early return took
        // every other channel down with it - enemies, party, dialogue, progression, loot,
        // damage, chat and the stale-peer prune all stopped, silently, for as long as the
        // lobby stayed full. Losing player positions for a tick is bad; losing the entire
        // session's state machine and not logging it is worse. Pagination
        // (WorldSnapshotPaging, the same fix already shipped for CreatureLiveness's own wider
        // publish) means that failure mode can only recur if even MaxPages datagrams cannot
        // hold the roster - bounded and logged below, rather than unbounded and silent.
        List<WorldSnapshotPacket> pages = WorldSnapshotPaging.Paginate(
            now, Flags.Version, Items.Version, Journal.Version, _lootStore.Version,
            Flags.Digest, Items.Digest, Journal.Digest, _lootStore.Digest, entries,
            out int droppedEntries);

        bool anyPageFailedToFit = false;
        foreach (WorldSnapshotPacket page in pages)
        {
            int length = PacketCodec.Write(_sendBuffer, page);
            if (length == 0)
            {
                // A single page that still does not fit - should not happen given
                // MaxSnapshotEntriesPerPage's own headroom, but fail safe exactly like the
                // old whole-snapshot guard: skip just this page and keep every other channel
                // (and every other page) running.
                anyPageFailedToFit = true;
                continue;
            }

            _worldSnapshotPagesSent++;

            foreach (PeerConnection peer in recipients)
            {
                _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
            }
        }

        if (droppedEntries > 0 || anyPageFailedToFit)
        {
            if (!_snapshotOverflowLogged)
            {
                _snapshotOverflowLogged = true;
                Console.Error.WriteLine(
                    $"[coop] world snapshot for {entries.Count} peers needs more than "
                    + $"{WorldSnapshotPacket.MaxPages} pages ({droppedEntries} peer entries "
                    + "dropped this tick); shorter player names help.");
            }
        }
        else
        {
            _snapshotOverflowLogged = false;
        }

        BroadcastEnemies(now, recipients);
        BroadcastCreatureLiveness(now, recipients);
        BroadcastTriggerRelay(recipients);
        BroadcastParty(now, recipients);
        BroadcastPartyRoster(now, recipients);
        BroadcastDialogue(now, recipients);
        BroadcastProgression(now, recipients);
        BroadcastCompanionExperience(now, recipients);
        BroadcastContainerLoot(now, recipients);
        BroadcastMaterialisedLoot(now, recipients);
        BroadcastPartyPool(now, recipients);
        BroadcastStore(now, recipients);
        BroadcastPartyPoolV2(now, recipients);
        BroadcastStoreV2(now, recipients);
        BroadcastDialogueGrant(now, recipients);
        BroadcastPhantomDamage(recipients);
        BroadcastPeerEffects(recipients);
        BroadcastBarks(recipients);
        BroadcastTransitionMovies(recipients);
        BroadcastTransitionAnnounce(recipients);
        BroadcastMidModuleMovies(recipients);
        BroadcastGalaxyDepartures(recipients);
        BroadcastInteractionLocks(recipients);
        BroadcastWorldDigest(now, recipients);
        FlushChat(recipients);
        PumpWorldTransferUdp(now);
        _buffer.PruneStale(now, _settings.PeerTimeoutMs);
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
                // A malformed datagram must never take the host down.
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
        ReadOnlySpan<byte> span = datagram.Span;
        PacketType type = PacketCodec.PeekType(span);
        long now = SessionClock.NowMs;
        _lastInboundMs = now;
        _recvIntervals.Record(now);

        if (type == PacketType.None)
        {
            // The strict header check (PacketReader.ReadHeader, what PeekType uses) folds a
            // version-mismatched datagram into None indistinguishably from garbage — so a
            // player on a different co-op build sent a perfectly well-formed Hello and got
            // nothing back, forever, until their own 15 s "no reply from host" timeout fired.
            // PeekTypeAnyVersion recovers the type and version WITHOUT trusting the body (a
            // version we do not speak may not lay its fields out the same way), so all this
            // may safely do is answer with a typed Reject — never parse the rest of it.
            PacketType looseType = PacketCodec.PeekTypeAnyVersion(span, out byte peerVersion);
            if (looseType == PacketType.Hello && peerVersion != ProtocolConstants.Version)
            {
                HandleVersionMismatch(datagram.From, peerVersion);
                return;
            }
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

    public void Dispose()
    {
        // Both best-effort and independent of the channel: UpnpMappingService's own Dispose
        // deletes the router mapping (a network call) before the channel it mapped a port for
        // goes away, and PublicAddressDiscovery has no mapping to clean up, only a loop to stop.
        _publicAddress?.Dispose();
        _upnp?.Dispose();
        _channel.Dispose();
    }
}
