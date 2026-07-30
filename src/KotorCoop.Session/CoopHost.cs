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
public sealed class CoopHost : ICoopSession
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

        /// <summary>The peer's global-variable catalogue digest, 0 until it reports one.</summary>
        public uint WorldGlobals;
    }

    public const uint HostPeerId = 1;

    private readonly SessionSettings _settings;
    private readonly UdpChannel _channel;
    private readonly SnapshotBuffer _buffer;
    private readonly Dictionary<uint, PeerConnection> _peers = [];
    private readonly Lock _gate = new();
    private readonly uint _sessionId;

    // Two buffers, because Tick and the receive loop run on different tasks and would
    // otherwise interleave writes into the same array.
    private readonly byte[] _sendBuffer = new byte[ProtocolConstants.MaxPacketSize];
    private readonly byte[] _replyBuffer = new byte[ProtocolConstants.MaxPacketSize];

    private PlayerState _localState = PlayerState.Empty;
    private uint _nextPeerId = HostPeerId + 1;
    private long _lastInboundMs;

    public CoopHost(SessionSettings settings)
    {
        _settings = settings;
        _channel = new UdpChannel(settings.Port);
        _buffer = new SnapshotBuffer(settings.InterpolationDelayMs);
        _sessionId = (uint)Random.Shared.Next(1, int.MaxValue);
    }

    public uint LocalPeerId => HostPeerId;

    public bool IsConnected => true;

    public string LocalName => _settings.PlayerName;

    public EventFlagStore Flags { get; } = new();

    public GroundItemStore Items { get; } = new();

    public IPEndPoint ListenEndPoint => _channel.LocalEndPoint;

    private uint _nextItemSequence = 1;

    public void PublishLocalState(in PlayerState state)
    {
        lock (_gate)
        {
            _localState = state;
        }
    }

    public IReadOnlyList<RemotePeer> GetRemotePeers() => _buffer.Sample(SessionClock.NowMs);

    public void SetFlag(uint flagId, sbyte value)
    {
        // The host is authoritative, so its own changes apply immediately. Clients pick
        // them up through the version tripwire in the next world snapshot.
        Flags.Set(flagId, value);
    }

    public uint DropItem(string resref, string module, Vector3f position)
    {
        uint itemId;
        lock (_gate)
        {
            itemId = (HostPeerId << 16) | (_nextItemSequence++ & 0xFFFF);
        }

        return Items.Drop(new GroundItem(itemId, resref, module, position)) ? itemId : 0;
    }

    public void RemoveItem(uint itemId) => Items.Remove(itemId);

    /// <summary>The host is the enemy authority: its fights are the ones that count.</summary>
    public bool IsEnemyAuthority => true;

    public void PublishEnemies(IReadOnlyList<EnemyState> enemies)
    {
        lock (_gate)
        {
            _enemies = enemies;
        }
    }

    public IReadOnlyList<EnemyState> GetEnemies() => [];

    private IReadOnlyList<EnemyState> _enemies = [];

    /// <summary>Latest enemy report per client, so a client's damage can be merged in.</summary>
    private sealed record EnemyReport(string Module, IReadOnlyList<EnemyState> Enemies, long ReceivedMs);

    private readonly Dictionary<uint, EnemyReport> _enemyReports = [];

    /// <summary>Reports older than this are stale — the client left, or changed module
    /// and its ids name strangers now.</summary>
    private const int EnemyReportFreshMs = 2000;

    public void ReportEnemies(string module, IReadOnlyList<EnemyState> enemies)
    {
        // The host does not report to itself; its own engine is the authority.
    }

    public IReadOnlyList<EnemyState> GetEnemyReports(string module)
    {
        long now = SessionClock.NowMs;
        List<EnemyState> merged = [];

        lock (_gate)
        {
            foreach (EnemyReport report in _enemyReports.Values)
            {
                if (now - report.ReceivedMs > EnemyReportFreshMs
                    || !string.Equals(report.Module, module, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                merged.AddRange(report.Enemies);
            }
        }

        return merged;
    }

    public JournalStore Journal { get; } = new();

    public void SetJournalEntry(string questTag, int state)
    {
        // Host-authoritative, like flags: merge locally, clients pick it up through the
        // journal version in the next world snapshot.
        Journal.Merge(questTag, state);
    }

    private CompatibilityVerdict? _compatibility;
    private uint _localWorldGlobals;

    public void PublishWorldDigest(uint globalsCatalogue) => _localWorldGlobals = globalsCatalogue;

    /// <summary>
    /// This peer's effective capabilities: its install verdict, refined by whether its
    /// in-world globals catalogue matches the host's. Both digests must be resolved
    /// (non-zero) before a difference counts — an unresolved catalogue is "unknown", not a
    /// mismatch.
    /// </summary>
    private CompatibilityVerdict EffectiveVerdict(PeerConnection peer)
    {
        bool globalsMismatch = _localWorldGlobals != 0 && peer.WorldGlobals != 0
            && _localWorldGlobals != peer.WorldGlobals;

        return ContentCompatibility.ApplyWorldMismatch(
            peer.InstallVerdict,
            globalsCatalogueMismatch: globalsMismatch,
            saveIdentityMismatch: false,
            moduleGitMismatch: false);
    }

    /// <summary>
    /// The narrowest capability set across all admitted peers. One peer running a mod that
    /// drops a channel drops it for the whole session — the host cannot publish enemy
    /// state to a client that would apply it to the wrong creatures.
    /// </summary>
    public SessionCapabilities Capabilities
    {
        get
        {
            lock (_gate)
            {
                SessionCapabilities caps = SessionCapabilities.All;
                foreach (PeerConnection peer in _peers.Values)
                {
                    caps &= EffectiveVerdict(peer).Allowed;
                }

                return caps;
            }
        }
    }

    public CompatibilityVerdict? Compatibility => _compatibility;

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
            LocalPeerId = HostPeerId,
            PeerCount = peerCount,
            PacketsSent = _channel.PacketsSent,
            PacketsReceived = _channel.PacketsReceived,
            PacketsDropped = _channel.MalformedPackets,
            BytesSent = _channel.BytesSent,
            BytesReceived = _channel.BytesReceived,
            LastInboundAgeMs = _lastInboundMs == 0 ? 0 : SessionClock.NowMs - _lastInboundMs,
            FlagVersion = Flags.Version,
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

        await receiveLoop.ConfigureAwait(false);
    }

    private void Tick()
    {
        long now = SessionClock.NowMs;

        List<SnapshotEntry> entries;
        List<PeerConnection> recipients;

        lock (_gate)
        {
            List<uint>? expired = null;
            foreach ((uint id, PeerConnection peer) in _peers)
            {
                if (now - peer.LastSeenMs > _settings.PeerTimeoutMs)
                {
                    (expired ??= []).Add(id);
                }
            }

            if (expired is not null)
            {
                foreach (uint id in expired)
                {
                    _peers.Remove(id);
                    _buffer.Remove(id);
                }
            }

            entries = new List<SnapshotEntry>(_peers.Count + 1)
            {
                new(HostPeerId, _settings.PlayerName, _localState),
            };

            foreach (PeerConnection peer in _peers.Values)
            {
                entries.Add(new SnapshotEntry(peer.Id, peer.Name, peer.LastState));
            }

            recipients = [.. _peers.Values];
        }

        WorldSnapshotPacket snapshot = new(now, Flags.Version, Items.Version, Journal.Version, entries);
        int length = PacketCodec.Write(_sendBuffer, snapshot);
        if (length == 0)
        {
            return;
        }

        foreach (PeerConnection peer in recipients)
        {
            _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
        }

        BroadcastEnemies(now, recipients);
        BroadcastWorldDigest(now, recipients);
        _buffer.PruneStale(now, _settings.PeerTimeoutMs);
    }

    /// <summary>
    /// Sends the authoritative enemy list. Unreliable and repeated every tick, like the
    /// world snapshot: a lost packet costs one tick of staleness, and the next one carries
    /// the full truth anyway. Death is a level in that state, not an event, so it cannot
    /// be missed the way a one-shot "it died" message could.
    /// </summary>
    private void BroadcastEnemies(long now, List<PeerConnection> recipients)
    {
        IReadOnlyList<EnemyState> enemies;
        lock (_gate)
        {
            enemies = _enemies;
        }

        if (enemies.Count == 0 || recipients.Count == 0)
        {
            return;
        }

        int length = PacketCodec.Write(_sendBuffer, new EnemySnapshotPacket(now, enemies));
        if (length == 0)
        {
            return;
        }

        foreach (PeerConnection peer in recipients)
        {
            _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    /// <summary>
    /// Sends the host's in-world digest so each client can judge the globals-catalogue
    /// match from its side too. Cheap and stable, so a low rate is plenty; sent only once
    /// the host's catalogue has resolved.
    /// </summary>
    private long _lastWorldDigestMs;

    private void BroadcastWorldDigest(long now, List<PeerConnection> recipients)
    {
        uint globals = _localWorldGlobals;
        if (globals == 0 || recipients.Count == 0 || now - _lastWorldDigestMs < 1000)
        {
            return;
        }

        _lastWorldDigestMs = now;
        int length = PacketCodec.Write(_sendBuffer, new WorldDigestPacket(HostPeerId, globals));
        if (length == 0)
        {
            return;
        }

        foreach (PeerConnection peer in recipients)
        {
            _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
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
                // A malformed datagram must never take the host down.
                _channel.NoteMalformed();
            }
        }
    }

    private void Handle(in Datagram datagram)
    {
        ReadOnlySpan<byte> span = datagram.Span;
        PacketType type = PacketCodec.PeekType(span);
        long now = SessionClock.NowMs;
        _lastInboundMs = now;

        switch (type)
        {
            case PacketType.Hello:
                HandleHello(datagram.From, span, now);
                break;

            case PacketType.StateUpdate:
                HandleStateUpdate(datagram.From, span, now);
                break;

            case PacketType.FlagOp:
                HandleFlagOp(datagram.From, span);
                break;

            case PacketType.FlagSyncRequest:
                HandleFlagSyncRequest(datagram.From, span);
                break;

            case PacketType.ItemOp:
                HandleItemOp(datagram.From, span);
                break;

            case PacketType.ItemSyncRequest:
                HandleItemSyncRequest(datagram.From, span);
                break;

            case PacketType.JournalOp:
                HandleJournalOp(datagram.From, span);
                break;

            case PacketType.JournalSyncRequest:
                HandleJournalSyncRequest(datagram.From, span);
                break;

            case PacketType.EnemyReport:
                HandleEnemyReport(span, now);
                break;

            case PacketType.WorldDigest:
                HandleWorldDigest(span);
                break;

            case PacketType.Ping:
                HandlePing(datagram.From, span, now);
                break;

            case PacketType.Bye:
                HandleBye(span);
                break;

            case PacketType.None:
                _channel.NoteMalformed();
                break;

            default:
                break;
        }
    }

    private void HandleHello(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadHello(span) is not { } hello)
        {
            _channel.NoteMalformed();
            return;
        }

        // Password gate first — the cheapest reason to turn a stranger away, and the one
        // the player most wants a clear answer to. An open session (no password) matches
        // any client, including one that supplies a password.
        uint required = SessionPassword.Hash(_settings.Password);
        if (required != SessionPassword.None && hello.PasswordHash != required)
        {
            int rejectLength = PacketCodec.Write(_replyBuffer, new RejectPacket("E_PASSWORD: wrong password"));
            if (rejectLength > 0)
            {
                _channel.Send(from, _replyBuffer.AsSpan(0, rejectLength));
            }

            return;
        }

        // Content compatibility is judged before admission. A refusal (different exe, or a
        // different save when that is not allowed) is sent as a typed Reject so the client
        // stops retrying and can tell the player why, rather than hanging on a silent host.
        ContentCompatibility.AllowDifferentSave = _settings.AllowDifferentSave;
        CompatibilityVerdict verdict = ContentCompatibility.Compare(_settings.Digest, hello.Digest);
        _compatibility = verdict;

        if (!verdict.Admitted)
        {
            int rejectLength = PacketCodec.Write(_replyBuffer, new RejectPacket(verdict.Summary));
            if (rejectLength > 0)
            {
                _channel.Send(from, _replyBuffer.AsSpan(0, rejectLength));
            }

            return;
        }

        uint assignedId;

        lock (_gate)
        {
            // Match a returning peer by its stable nonce first, then by endpoint. The nonce
            // is what makes reconnection seamless: a client that dropped and came back —
            // possibly from a new ephemeral port — reclaims its existing slot (same id,
            // same name, its phantom never doubled) instead of joining as a stranger while
            // its old slot lingers until timeout. Hello is also retransmitted until Welcome
            // lands, so a plain repeat must return the same id too.
            PeerConnection? existing =
                _peers.Values.FirstOrDefault(p => p.Nonce == hello.ClientNonce)
                ?? _peers.Values.FirstOrDefault(p => p.EndPoint.Equals(from));

            if (existing is not null)
            {
                existing.LastSeenMs = now;
                existing.EndPoint = from;
                existing.InstallVerdict = verdict;
                assignedId = existing.Id;
            }
            else
            {
                if (_peers.Count + 1 >= ProtocolConstants.MaxPeers)
                {
                    int rejectLength = PacketCodec.Write(_replyBuffer, new RejectPacket("session full"));
                    if (rejectLength > 0)
                    {
                        _channel.Send(from, _replyBuffer.AsSpan(0, rejectLength));
                    }

                    return;
                }

                assignedId = _nextPeerId++;
                _peers[assignedId] = new PeerConnection
                {
                    Id = assignedId,
                    EndPoint = from,
                    Name = string.IsNullOrWhiteSpace(hello.PlayerName) ? $"peer{assignedId}" : hello.PlayerName,
                    Nonce = hello.ClientNonce,
                    LastSeenMs = now,
                    InstallVerdict = verdict,
                };
            }
        }

        WelcomePacket welcome = new(
            assignedId, _sessionId, (byte)_settings.TickRate, ProtocolConstants.MaxPeers, _settings.Digest);
        int length = PacketCodec.Write(_replyBuffer, welcome);
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    private void HandleStateUpdate(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadStateUpdate(span) is not { } update)
        {
            _channel.NoteMalformed();
            return;
        }

        string name;

        lock (_gate)
        {
            if (!_peers.TryGetValue(update.PeerId, out PeerConnection? peer))
            {
                return;
            }

            // Endpoints can change (NAT rebinding); trust the latest source address.
            peer.EndPoint = from;
            peer.LastState = update.State;
            peer.LastSeenMs = now;
            name = peer.Name;
        }

        _buffer.Push(update.PeerId, name, update.State, now);
    }

    private void HandleFlagOp(IPEndPoint from, ReadOnlySpan<byte> span)
    {
        if (PacketCodec.ReadFlagOp(span) is not { } op)
        {
            _channel.NoteMalformed();
            return;
        }

        Flags.ApplyOp(op);

        // Ack unconditionally: a duplicate op means the previous ack was lost.
        int length = PacketCodec.Write(_replyBuffer, new FlagAckPacket(op.OpSequence));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    private void HandleFlagSyncRequest(IPEndPoint from, ReadOnlySpan<byte> span)
    {
        if (PacketCodec.ReadFlagSyncRequest(span) is null)
        {
            _channel.NoteMalformed();
            return;
        }

        FlagSnapshotPacket snapshot = new(Flags.Version, Flags.Snapshot());
        int length = PacketCodec.Write(_replyBuffer, snapshot);
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    private void HandleItemOp(IPEndPoint from, ReadOnlySpan<byte> span)
    {
        if (PacketCodec.ReadItemOp(span) is not { } op)
        {
            _channel.NoteMalformed();
            return;
        }

        Items.ApplyOp(op);

        // Ack unconditionally: a duplicate op means the previous ack was lost.
        int length = PacketCodec.Write(_replyBuffer, new ItemAckPacket(op.OpSequence));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    private void HandleItemSyncRequest(IPEndPoint from, ReadOnlySpan<byte> span)
    {
        if (PacketCodec.ReadItemSyncRequest(span) is null)
        {
            _channel.NoteMalformed();
            return;
        }

        ItemSnapshotPacket snapshot = new(Items.Version, Items.Snapshot());
        int length = PacketCodec.Write(_replyBuffer, snapshot);
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    private void HandleJournalOp(IPEndPoint from, ReadOnlySpan<byte> span)
    {
        if (PacketCodec.ReadJournalOp(span) is not { } op)
        {
            _channel.NoteMalformed();
            return;
        }

        Journal.Merge(op.QuestTag, op.State);

        // Ack unconditionally: a duplicate op means the previous ack was lost.
        int length = PacketCodec.Write(_replyBuffer, new JournalAckPacket(op.OpSequence));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    private void HandleJournalSyncRequest(IPEndPoint from, ReadOnlySpan<byte> span)
    {
        if (PacketCodec.ReadJournalSyncRequest(span) is null)
        {
            _channel.NoteMalformed();
            return;
        }

        // A journal longer than one datagram goes out as several packets, each carrying
        // the full version. If a part is lost the client's version still matches and the
        // gap survives until the next journal change bumps the version — the same
        // truncation trade the flag snapshot already makes, acceptable because ops are
        // the primary path and snapshots only repair.
        List<KeyValuePair<string, int>> entries = Journal.Snapshot();
        uint version = Journal.Version;

        for (int start = 0; ; start += ProtocolConstants.MaxJournalEntriesPerPacket)
        {
            List<KeyValuePair<string, int>> part =
                [.. entries.Skip(start).Take(ProtocolConstants.MaxJournalEntriesPerPacket)];

            int length = PacketCodec.Write(_replyBuffer, new JournalSnapshotPacket(version, part));
            if (length > 0)
            {
                _channel.Send(from, _replyBuffer.AsSpan(0, length));
            }

            if (start + ProtocolConstants.MaxJournalEntriesPerPacket >= entries.Count)
            {
                break;
            }
        }
    }

    private void HandleWorldDigest(ReadOnlySpan<byte> span)
    {
        if (PacketCodec.ReadWorldDigest(span) is not { } report)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (_peers.TryGetValue(report.PeerId, out PeerConnection? peer))
            {
                peer.WorldGlobals = report.GlobalsCatalogue;
                _compatibility = EffectiveVerdict(peer);
            }
        }
    }

    private void HandleEnemyReport(ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadEnemyReport(span) is not { } report)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (!_peers.ContainsKey(report.PeerId))
            {
                return;
            }

            _enemyReports[report.PeerId] = new EnemyReport(report.Module, report.Enemies, now);
        }
    }

    private void HandlePing(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPing(span) is not { } ping)
        {
            _channel.NoteMalformed();
            return;
        }

        int length = PacketCodec.Write(_replyBuffer, new PongPacket(ping.SentTimeMs, now));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    private void HandleBye(ReadOnlySpan<byte> span)
    {
        if (PacketCodec.ReadBye(span) is not { } bye)
        {
            return;
        }

        lock (_gate)
        {
            _peers.Remove(bye.PeerId);
        }

        _buffer.Remove(bye.PeerId);
    }

    public void Dispose() => _channel.Dispose();
}
