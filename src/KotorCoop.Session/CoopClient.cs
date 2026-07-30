using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// The joining end of a session. Sends its own avatar to the host every tick, receives
/// the combined world back, and repairs world flag divergence by version comparison
/// rather than by streaming flags continuously.
/// </summary>
public sealed class CoopClient : ICoopSession
{
    private const int HelloIntervalMs = 500;
    private const int PingIntervalMs = 1000;
    private const int FlagResyncIntervalMs = 500;
    private const int ItemResyncIntervalMs = 500;
    private const int JournalResyncIntervalMs = 500;

    private readonly SessionSettings _settings;
    private readonly IPEndPoint _hostEndPoint;
    private readonly UdpChannel _channel;
    private readonly SnapshotBuffer _buffer;
    private readonly ReliableFlagSender _flagSender = new();
    private readonly ReliableItemSender _itemSender = new();
    private readonly ReliableJournalSender _journalSender = new();
    private readonly Lock _gate = new();
    private readonly uint _nonce;

    private readonly byte[] _sendBuffer = new byte[ProtocolConstants.MaxPacketSize];
    private readonly byte[] _replyBuffer = new byte[ProtocolConstants.MaxPacketSize];

    private PlayerState _localState = PlayerState.Empty;
    private uint _localPeerId;
    private uint _sequence;
    private long _lastHelloMs;
    private long _lastPingMs;
    private long _lastFlagRequestMs;
    private long _lastItemRequestMs;
    private long _lastJournalRequestMs;
    private uint _nextItemSequence = 1;
    private long _lastInboundMs;
    private double _roundTripMs;
    private string _rejectReason = string.Empty;
    private CompatibilityVerdict? _installVerdict;
    private CompatibilityVerdict? _compatibility;
    private uint _localWorldGlobals;
    private uint _hostWorldGlobals;
    private long _lastWorldDigestMs;

    public SessionCapabilities Capabilities => _compatibility?.Allowed ?? SessionCapabilities.All;

    public CompatibilityVerdict? Compatibility => _compatibility;

    public void PublishWorldDigest(uint globalsCatalogue)
    {
        if (globalsCatalogue == _localWorldGlobals)
        {
            return;
        }

        _localWorldGlobals = globalsCatalogue;
        RefineCompatibility();
    }

    /// <summary>
    /// Recomputes the client's verdict from the install compare plus the in-world
    /// globals-catalogue match. Both digests must be resolved before a difference counts.
    /// </summary>
    private void RefineCompatibility()
    {
        if (_installVerdict is not { } install)
        {
            return;
        }

        bool globalsMismatch = _localWorldGlobals != 0 && _hostWorldGlobals != 0
            && _localWorldGlobals != _hostWorldGlobals;

        _compatibility = ContentCompatibility.ApplyWorldMismatch(
            install,
            globalsCatalogueMismatch: globalsMismatch,
            saveIdentityMismatch: false,
            moduleGitMismatch: false);
    }

    public CoopClient(SessionSettings settings, IPEndPoint hostEndPoint)
    {
        _settings = settings;
        _hostEndPoint = hostEndPoint;

        // Bind an ephemeral port: two clients on one machine must not fight over 27015.
        _channel = new UdpChannel(0);
        _buffer = new SnapshotBuffer(settings.InterpolationDelayMs);
        _nonce = (uint)Random.Shared.Next(1, int.MaxValue);
    }

    public uint LocalPeerId => _localPeerId;

    public bool IsConnected => _localPeerId != 0;

    public string LocalName => _settings.PlayerName;

    public EventFlagStore Flags { get; } = new();

    public GroundItemStore Items { get; } = new();

    public uint DropItem(string resref, string module, Vector3f position)
    {
        if (_localPeerId == 0)
        {
            return 0;
        }

        uint itemId;
        lock (_gate)
        {
            itemId = (_localPeerId << 16) | (_nextItemSequence++ & 0xFFFF);
        }

        GroundItem item = new(itemId, resref, module, position);

        // Applied locally straight away so the dropping player is not waiting on a round
        // trip; the host's authoritative version will confirm or correct it.
        if (!Items.Drop(item))
        {
            return 0;
        }

        SendItemOp(_itemSender.Enqueue(_localPeerId, ItemOpKind.Drop, item, SessionClock.NowMs));
        return itemId;
    }

    /// <summary>A client never owns enemy state; it mirrors the host's.</summary>
    public bool IsEnemyAuthority => false;

    public void PublishEnemies(IReadOnlyList<EnemyState> enemies)
    {
        // Only the host is authoritative, so a client's own view is deliberately dropped.
    }

    public IReadOnlyList<EnemyState> GetEnemies()
    {
        lock (_gate)
        {
            return _enemies;
        }
    }

    private IReadOnlyList<EnemyState> _enemies = [];

    public void ReportEnemies(string module, IReadOnlyList<EnemyState> enemies)
    {
        if (_localPeerId == 0 || enemies.Count == 0)
        {
            return;
        }

        // Unreliable and re-sent every tick, like the state update it travels beside: a
        // lost report costs one tick of staleness on the host's floor, nothing more.
        int length = PacketCodec.Write(_sendBuffer, new EnemyReportPacket(_localPeerId, module, enemies));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    public IReadOnlyList<EnemyState> GetEnemyReports(string module) => [];

    public JournalStore Journal { get; } = new();

    public void SetJournalEntry(string questTag, int state)
    {
        // Applied locally straight away — the local player just watched the quest
        // advance; the host's version confirms and redistributes it.
        if (!Journal.Merge(questTag, state))
        {
            return;
        }

        if (_localPeerId == 0)
        {
            return;
        }

        SendJournalOp(_journalSender.Enqueue(_localPeerId, questTag, state, SessionClock.NowMs));
    }

    public void RemoveItem(uint itemId)
    {
        if (!Items.Remove(itemId) || _localPeerId == 0)
        {
            return;
        }

        GroundItem tombstone = new(itemId, string.Empty, string.Empty, Vector3f.Zero);
        SendItemOp(_itemSender.Enqueue(_localPeerId, ItemOpKind.Remove, tombstone, SessionClock.NowMs));
    }

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
        // Applied locally straight away so the local player is not waiting on a round
        // trip; the host's authoritative version will confirm or correct it.
        Flags.Set(flagId, value);

        if (_localPeerId == 0)
        {
            return;
        }

        FlagOpPacket op = _flagSender.Enqueue(_localPeerId, flagId, value, SessionClock.NowMs);
        SendFlagOp(op);
    }

    public SessionStats GetStats()
    {
        long now = SessionClock.NowMs;

        return new SessionStats
        {
            Connected = IsConnected,
            LocalPeerId = _localPeerId,
            PeerCount = _buffer.PeerCount,
            RoundTripMs = _roundTripMs,
            PacketsSent = _channel.PacketsSent,
            PacketsReceived = _channel.PacketsReceived,
            PacketsDropped = _channel.MalformedPackets,
            BytesSent = _channel.BytesSent,
            BytesReceived = _channel.BytesReceived,
            LastInboundAgeMs = _lastInboundMs == 0 ? 0 : now - _lastInboundMs,
            FlagVersion = Flags.Version,
            PendingFlagOps = _flagSender.PendingCount,
            Status = _rejectReason.Length > 0
                ? $"rejected by host: {_rejectReason}"
                : IsConnected
                    ? $"connected to {_hostEndPoint} as peer {_localPeerId}"
                    : $"connecting to {_hostEndPoint}...",
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

        SendBye();
        await receiveLoop.ConfigureAwait(false);
    }

    private void Tick()
    {
        long now = SessionClock.NowMs;

        if (!IsConnected)
        {
            if (now - _lastHelloMs >= HelloIntervalMs && _rejectReason.Length == 0)
            {
                _lastHelloMs = now;
                SendHello();
            }

            return;
        }

        SendStateUpdate(now);

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

        // Report our in-world digest so the host can judge the globals match. Stable, so a
        // low rate is plenty; only once our catalogue has resolved.
        if (_localWorldGlobals != 0 && now - _lastWorldDigestMs >= 1000)
        {
            _lastWorldDigestMs = now;
            int length = PacketCodec.Write(_sendBuffer, new WorldDigestPacket(_localPeerId, _localWorldGlobals));
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }

        _buffer.PruneStale(now, _settings.PeerTimeoutMs);
    }

    private void SendHello()
    {
        string module;
        lock (_gate)
        {
            module = _localState.Module;
        }

        int length = PacketCodec.Write(
            _sendBuffer, new HelloPacket(_settings.PlayerName, module, _nonce, _settings.Digest));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    private void SendStateUpdate(long now)
    {
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

    private void SendFlagOp(FlagOpPacket op)
    {
        int length = PacketCodec.Write(_sendBuffer, op);
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    private void SendItemOp(ItemOpPacket op)
    {
        int length = PacketCodec.Write(_sendBuffer, op);
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    private void SendJournalOp(JournalOpPacket op)
    {
        int length = PacketCodec.Write(_sendBuffer, op);
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    private void SendBye()
    {
        if (_localPeerId == 0)
        {
            return;
        }

        int length = PacketCodec.Write(_sendBuffer, new ByePacket(_localPeerId));
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

    private void Handle(in Datagram datagram)
    {
        // Ignore anything that did not come from the host we joined.
        if (!datagram.From.Address.Equals(_hostEndPoint.Address) || datagram.From.Port != _hostEndPoint.Port)
        {
            return;
        }

        ReadOnlySpan<byte> span = datagram.Span;
        long now = SessionClock.NowMs;
        _lastInboundMs = now;

        switch (PacketCodec.PeekType(span))
        {
            case PacketType.Welcome:
                HandleWelcome(span);
                break;

            case PacketType.Reject:
                if (PacketCodec.ReadReject(span) is { } reject)
                {
                    _rejectReason = reject.Reason;
                }

                break;

            case PacketType.WorldSnapshot:
                HandleWorldSnapshot(span, now);
                break;

            case PacketType.FlagAck:
                if (PacketCodec.ReadFlagAck(span) is { } ack)
                {
                    _flagSender.Acknowledge(ack.OpSequence);
                }

                break;

            case PacketType.FlagSnapshot:
                if (PacketCodec.ReadFlagSnapshot(span) is { } flagSnapshot)
                {
                    Flags.ReplaceAll(flagSnapshot.Flags, flagSnapshot.Version);
                }

                break;

            case PacketType.ItemAck:
                if (PacketCodec.ReadItemAck(span) is { } itemAck)
                {
                    _itemSender.Acknowledge(itemAck.OpSequence);
                }

                break;

            case PacketType.JournalAck:
                if (PacketCodec.ReadJournalAck(span) is { } journalAck)
                {
                    _journalSender.Acknowledge(journalAck.OpSequence);
                }

                break;

            case PacketType.JournalSnapshot:
                if (PacketCodec.ReadJournalSnapshot(span) is { } journalSnapshot)
                {
                    // Merge, never replace: journal states are monotonic, and a snapshot
                    // must not lower an entry a local advance just raised.
                    Journal.MergeSnapshot(journalSnapshot.Entries, journalSnapshot.Version);
                }

                break;

            case PacketType.EnemySnapshot:
                if (PacketCodec.ReadEnemySnapshot(span) is { } enemySnapshot)
                {
                    lock (_gate)
                    {
                        _enemies = enemySnapshot.Enemies;
                    }
                }

                break;

            case PacketType.WorldDigest:
                if (PacketCodec.ReadWorldDigest(span) is { } hostDigest)
                {
                    _hostWorldGlobals = hostDigest.GlobalsCatalogue;
                    RefineCompatibility();
                }

                break;

            case PacketType.ItemSnapshot:
                if (PacketCodec.ReadItemSnapshot(span) is { } itemSnapshot)
                {
                    Items.ReplaceAll(itemSnapshot.Items, itemSnapshot.Version);
                }

                break;

            case PacketType.Pong:
                if (PacketCodec.ReadPong(span) is { } pong)
                {
                    // Exponential moving average; a single sample is too jittery to show.
                    double sample = now - pong.OriginalSentTimeMs;
                    _roundTripMs = _roundTripMs == 0 ? sample : (_roundTripMs * 0.8) + (sample * 0.2);
                }

                break;

            case PacketType.None:
                _channel.NoteMalformed();
                break;

            default:
                break;
        }
    }

    private void HandleWelcome(ReadOnlySpan<byte> span)
    {
        if (PacketCodec.ReadWelcome(span) is not { } welcome)
        {
            _channel.NoteMalformed();
            return;
        }

        if (_localPeerId == 0)
        {
            _localPeerId = welcome.AssignedPeerId;

            // Judge compatibility from our side too, from the host's digest in the Welcome.
            // The host already refused an incompatible client; this gives the client the
            // same capability set (so both ends gate the same channels) and the same
            // warnings to show. Computed identically, so the two agree without negotiating.
            ContentCompatibility.AllowDifferentSave = _settings.AllowDifferentSave;
            _installVerdict = ContentCompatibility.Compare(_settings.Digest, welcome.Digest);
            _compatibility = _installVerdict;
            RefineCompatibility();
        }
    }

    private void HandleWorldSnapshot(ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadWorldSnapshot(span) is not { } snapshot)
        {
            _channel.NoteMalformed();
            return;
        }

        foreach (SnapshotEntry entry in snapshot.Entries)
        {
            if (entry.PeerId == _localPeerId)
            {
                continue;
            }

            _buffer.Push(entry.PeerId, entry.Name, entry.State, now);
        }

        // The version tripwire: 4 bytes per tick tells us whether our world flags still
        // match the host's, and the full set only crosses the wire when they do not.
        if (snapshot.FlagVersion != Flags.Version && now - _lastFlagRequestMs >= FlagResyncIntervalMs)
        {
            _lastFlagRequestMs = now;
            int length = PacketCodec.Write(_replyBuffer, new FlagSyncRequestPacket(_localPeerId, Flags.Version));
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _replyBuffer.AsSpan(0, length));
            }
        }

        // Same tripwire for ground items, but only once our own ops have all been acked:
        // resyncing while a drop is still in flight would erase it locally, respawn it on
        // the next snapshot, and flicker the item in and out of the world.
        if (snapshot.ItemVersion != Items.Version
            && _itemSender.PendingCount == 0
            && now - _lastItemRequestMs >= ItemResyncIntervalMs)
        {
            _lastItemRequestMs = now;
            int length = PacketCodec.Write(_replyBuffer, new ItemSyncRequestPacket(_localPeerId, Items.Version));
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _replyBuffer.AsSpan(0, length));
            }
        }

        // Journal tripwire, gated on our own ops being acked for the same reason as
        // items: the merge itself is monotonic, but the version numbers only settle once
        // in-flight ops have landed.
        if (snapshot.JournalVersion != Journal.Version
            && _journalSender.PendingCount == 0
            && now - _lastJournalRequestMs >= JournalResyncIntervalMs)
        {
            _lastJournalRequestMs = now;
            int length = PacketCodec.Write(
                _replyBuffer, new JournalSyncRequestPacket(_localPeerId, Journal.Version));
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _replyBuffer.AsSpan(0, length));
            }
        }
    }

    public void Dispose() => _channel.Dispose();
}
