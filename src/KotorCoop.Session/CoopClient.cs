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
    private readonly ContainerLootStore _lootStore = new();
    private readonly ReliableLootSender _lootSender = new();

    /// <summary>
    /// The loot sender's one recipient: the host, which applies the removal, re-attributes
    /// it under its own id, and relays it to the other clients (see the host's
    /// HandleContainerLoot). The shared sender retires an op once every listed recipient has
    /// acked, so a client's list is just this.
    /// </summary>
    private static readonly uint[] LootRecipients = [CoopHost.HostPeerId];
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
    private bool _reconnecting;
    private CompatibilityVerdict? _installVerdict;
    private CompatibilityVerdict? _compatibility;
    private WorldDigest _localWorld = WorldDigest.Empty;
    private WorldDigest _hostWorld = WorldDigest.Empty;
    private long _lastWorldDigestMs;

    public SessionCapabilities Capabilities => _compatibility?.Allowed ?? SessionCapabilities.All;

    public CompatibilityVerdict? Compatibility => _compatibility;

    public void PublishWorldDigest(in WorldDigest digest)
    {
        if (digest == _localWorld)
        {
            return;
        }

        _localWorld = digest;
        RefineCompatibility();
    }

    /// <summary>
    /// Recomputes the client's verdict from the install compare plus the in-world
    /// comparison (globals catalogue, save identity, current module's GIT). Each field
    /// must be resolved on both sides before a difference counts, and the module fields
    /// track the players — so this re-judges itself on every module change, restoring
    /// channels wherever the two GITs agree again.
    /// </summary>
    private void RefineCompatibility()
    {
        if (_installVerdict is not { } install)
        {
            return;
        }

        WorldDigestComparison mismatch = WorldDigest.Compare(_localWorld, _hostWorld);

        _compatibility = ContentCompatibility.ApplyWorldMismatch(install, mismatch);
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

    public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies)
    {
        // Only the host is authoritative, so a client's own view is deliberately dropped.
    }

    public IReadOnlyList<EnemyState> GetEnemies(string module)
    {
        lock (_gate)
        {
            // Ids from another module are not stale, they are WRONG: the same numbers name
            // different creatures here. Dropping the snapshot leaves our own copies alone
            // until we are back in the same place.
            return _enemyModule.Length == 0
                || module.Length == 0
                || string.Equals(_enemyModule, module, StringComparison.OrdinalIgnoreCase)
                    ? _enemies
                    : [];
        }
    }

    private IReadOnlyList<EnemyState> _enemies = [];

    private string _enemyModule = string.Empty;

    private IReadOnlyList<PartyMemberState> _party = [];

    public void PublishParty(IReadOnlyList<PartyMemberState> members)
    {
        // Only the host is authoritative for the party; a client's own view is dropped.
    }

    public IReadOnlyList<PartyMemberState> GetPartyMembers()
    {
        lock (_gate)
        {
            return _party;
        }
    }

    /// <summary>The host's roster, as last received, and whether one ever has been.</summary>
    private PartyRoster _partyRoster = PartyRoster.Empty;

    private bool _hasPartyRoster;

    public void PublishPartyRoster(PartyRoster roster)
    {
        // Only the host is authoritative for the roster; a client's own view is dropped, the
        // same way its party snapshot is.
    }

    /// <summary>
    /// ⚠ THE BOOL IS THE SAFETY, NOT A CONVENIENCE. An unheard-from roster and a genuinely
    /// empty one are the same two zero masks, and applying the second by mistake dismisses
    /// every companion the player has. So "no level yet" is reported as false and the plugin
    /// is handed nothing at all — a client with no level applies nothing, which is exactly
    /// what a client that has not been told anything should do.
    ///
    /// It is deliberately NOT aged out. A roster is save state, not a position sample: a
    /// stale one is still the last thing the host said about a table that only changes when
    /// somebody recruits or dismisses. Losing the connection is handled by the connection
    /// (the agent stops writing the block); losing a few datagrams is not a reason to stop
    /// reconciling.
    /// </summary>
    public bool TryGetPartyRoster(out PartyRoster roster)
    {
        lock (_gate)
        {
            roster = _partyRoster;
            return _hasPartyRoster;
        }
    }

    /// <summary>The host's authoritative conversation token, as last received.</summary>
    private DialogueState _dialogue = DialogueState.Idle;

    /// <summary>This client's local conversation status, sent to the host each tick.</summary>
    private bool _localDialogueActive;
    private string _localDialogueSpeaker = string.Empty;
    private string _localDialogueLine = string.Empty;
    private string _localDialoguePartnerTag = string.Empty;
    private uint _localDialogueReplySerial;
    private int _localDialogueReplyIndex;
    private byte[]? _localDialoguePickLog;
    private int _localDialogueNode = DialogueState.NoNode;
    private uint _localDialogueNodeSerial;

    public void PublishDialogue(
        bool active, string speakerName, string line,
        string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
        int nodeIndex = DialogueState.NoNode, uint nodeSerial = 0)
    {
        lock (_gate)
        {
            _localDialogueActive = active;
            _localDialogueSpeaker = speakerName ?? string.Empty;
            _localDialogueLine = line ?? string.Empty;
            _localDialoguePartnerTag = partnerTag ?? string.Empty;
            _localDialogueReplySerial = replySerial;
            _localDialogueReplyIndex = replyIndex;
            _localDialoguePickLog = pickLog;
            _localDialogueNode = nodeIndex;
            _localDialogueNodeSerial = nodeSerial;
        }
    }

    public DialogueState GetDialogue()
    {
        lock (_gate)
        {
            return _dialogue;
        }
    }

    private uint _gold;
    private uint _experience;
    private bool _hasProgression;

    /// <summary>Only the host is authoritative for progression; a client's own view is dropped.</summary>
    public void PublishProgression(uint gold, uint experience)
    {
    }

    public bool TryGetProgression(out uint gold, out uint experience)
    {
        lock (_gate)
        {
            gold = _gold;
            experience = _experience;
            return _hasProgression;
        }
    }

    private readonly Queue<ChatMessage> _chatInbox = new();
    private readonly Queue<string> _chatOutbox = new();

    public void SendChat(string text)
    {
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        // Queued for the tick loop, which owns the send buffer.
        lock (_gate)
        {
            _chatOutbox.Enqueue(text);
        }
    }

    public IReadOnlyList<ChatMessage> DrainChat()
    {
        lock (_gate)
        {
            if (_chatInbox.Count == 0)
            {
                return [];
            }

            ChatMessage[] drained = [.. _chatInbox];
            _chatInbox.Clear();
            return drained;
        }
    }

    // ---- NPC barks ----------------------------------------------------------------
    //
    // A client both sends the barks its own instance produced and applies the ones the host
    // relays from everyone else. Unreliable: a lost bark costs a line, not consistency.

    private readonly Queue<BarkPacket> _barkOutbox = new();
    private readonly Queue<BarkPacket> _barkInbox = new();

    public void PublishBark(string module, uint speaker, byte[] text, byte[] soundResRef, uint flags, uint serial)
    {
        lock (_gate)
        {
            _barkOutbox.Enqueue(new BarkPacket(_localPeerId, module, speaker, text, soundResRef, flags, serial));
        }
    }

    public IReadOnlyList<BarkPacket> DrainBarks(string module)
    {
        lock (_gate)
        {
            if (_barkInbox.Count == 0)
            {
                return [];
            }

            List<BarkPacket> ready = [];
            while (_barkInbox.Count > 0)
            {
                BarkPacket bark = _barkInbox.Dequeue();

                // Ids are per module load, so a bark from elsewhere names nothing here.
                if (string.Equals(bark.Module, module, StringComparison.OrdinalIgnoreCase))
                {
                    ready.Add(bark);
                }
            }

            return ready;
        }
    }

    private void FlushBarks()
    {
        while (true)
        {
            BarkPacket bark;
            lock (_gate)
            {
                if (_barkOutbox.Count == 0)
                {
                    return;
                }

                bark = _barkOutbox.Dequeue();
            }

            int length = PacketCodec.Write(_sendBuffer, bark);
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    private void FlushChat()
    {
        while (true)
        {
            string text;
            lock (_gate)
            {
                if (_chatOutbox.Count == 0)
                {
                    return;
                }

                text = _chatOutbox.Dequeue();
            }

            int length = PacketCodec.Write(
                _sendBuffer, new ChatPacket(_localPeerId, _settings.PlayerName, text));
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    public void ReportEnemyDamage(string module, uint generation, IReadOnlyList<EnemyDamageEntry> damage)
    {
        if (_localPeerId == 0 || damage.Count == 0)
        {
            return;
        }

        // Unreliable and re-sent every tick, like the state update it travels beside. Safe
        // precisely because the totals are cumulative: a lost datagram costs one tick of
        // staleness rather than a hit, and a duplicate applies nothing.
        int length = PacketCodec.Write(
            _sendBuffer, new EnemyDamagePacket(_localPeerId, module, generation, damage));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    public IReadOnlyList<PeerEnemyDamage> GetEnemyDamage(string module) => [];

    public void ReportProgression(uint generation, uint goldEarned, uint goldSpent, uint experience)
    {
        if (_localPeerId == 0)
        {
            return;
        }

        // Sent every tick even when nothing changed, and unreliable, for the same reason the
        // damage ledger is: the totals are cumulative, so a lost datagram costs a tick of
        // staleness rather than a purchase, and a repeat applies nothing.
        int length = PacketCodec.Write(
            _sendBuffer,
            new ProgressionReportPacket(_localPeerId, generation, goldEarned, goldSpent, experience));

        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    /// <summary>The host merges reports; a client only sends its own.</summary>
    public IReadOnlyList<PeerProgression> GetProgressionReports() => [];

    /// <summary>
    /// This player emptied a container here. A container is a private copy in each process, so
    /// unless the host is told, its copy still holds the item and whoever opens it there takes
    /// the same loot a second time — the dupe this whole channel exists to prevent.
    ///
    /// Reliable, like the host's own removals: a removal is a one-shot fact, not a level the
    /// next tick re-sends. Enqueue-only; the tick loop owns the send buffer.
    ///
    /// Gated on the store, which is what stops the host's removals bouncing back: applying one
    /// empties a container here, this instance's scanner then reports the same item as looted,
    /// and the store recognises it and swallows the announcement.
    /// </summary>
    public void PublishContainerLoot(uint containerId, uint itemId)
    {
        if (!_lootStore.Add(new ContainerLootRemoval(containerId, itemId)))
        {
            return;
        }

        _lootSender.Enqueue(_localPeerId, containerId, itemId, SessionClock.NowMs);
    }

    // ---- phantom damage (combat participation) -----------------------------------

    private readonly Dictionary<uint, (uint Damage, uint Attacker)> _phantomDamageOut = [];

    /// <summary>Running totals owed to the local player, keyed by the peer that dealt them.
    /// See <see cref="GetIncomingPhantomDamage"/>: with three players two different peers can
    /// be hitting this player's phantom, and their totals add.</summary>
    private readonly Dictionary<uint, (uint Damage, uint Attacker)> _phantomDamageIn = [];

    private uint _lastIncomingAttacker;

    /// <summary>Queued for the next tick's send; the host relays it to the addressee (or
    /// keeps it, when the host is the one being hit).</summary>
    public void PublishPhantomDamage(uint targetPeerId, uint cumulativeDamage, uint attackerId)
    {
        if (targetPeerId == 0 || targetPeerId == _localPeerId)
        {
            return;
        }

        lock (_gate)
        {
            _phantomDamageOut[targetPeerId] = (cumulativeDamage, attackerId);
        }
    }

    /// <summary>
    /// The total this player owes, summed across every peer that has hit their phantom.
    ///
    /// ⚠ Per-sender running totals ADD; they do not max. Keeping one number meant a second
    /// attacker whose total was below the leader's was ignored outright, forever. The sum is
    /// still monotonic, which is what the plugin's applied-so-far counter needs, so entries
    /// are never removed - dropping one would walk the total backwards.
    /// </summary>
    public (uint CumulativeDamage, uint AttackerId) GetIncomingPhantomDamage()
    {
        lock (_gate)
        {
            ulong total = 0;
            foreach ((uint damage, uint _) in _phantomDamageIn.Values)
            {
                total += damage;
            }

            return ((uint)Math.Min(total, uint.MaxValue), _lastIncomingAttacker);
        }
    }

    private void HandlePhantomDamage(ReadOnlySpan<byte> span)
    {
        if (PacketCodec.ReadPhantomDamage(span) is not { } packet
            || packet.TargetPeerId != _localPeerId)
        {
            return;
        }

        lock (_gate)
        {
            // Monotonic PER SENDER, so a reordered datagram cannot walk that sender's total
            // backwards and make the plugin re-apply the difference. The host relays another
            // client's report with the ORIGINATOR in FromPeerId, so this key really does
            // separate the attackers rather than lumping everything under the host.
            if (_phantomDamageIn.TryGetValue(packet.FromPeerId, out (uint Damage, uint Attacker) seen)
                && packet.CumulativeDamage <= seen.Damage)
            {
                return;
            }

            _phantomDamageIn[packet.FromPeerId] = (packet.CumulativeDamage, packet.AttackerId);

            if (packet.AttackerId != 0)
            {
                _lastIncomingAttacker = packet.AttackerId;
            }
        }
    }

    private void SendPhantomDamage()
    {
        List<KeyValuePair<uint, (uint Damage, uint Attacker)>> owed;
        lock (_gate)
        {
            if (_phantomDamageOut.Count == 0 || _localPeerId == 0)
            {
                return;
            }

            owed = [.. _phantomDamageOut];
        }

        foreach ((uint targetPeerId, (uint damage, uint attacker)) in owed)
        {
            if (damage == 0)
            {
                continue;
            }

            int length = PacketCodec.Write(
                _sendBuffer, new PhantomDamagePacket(_localPeerId, targetPeerId, damage, attacker));
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    // ---- encounter triggers -------------------------------------------------------

    private string _encounterModule = string.Empty;
    private uint[] _encounterIds = [];

    /// <summary>
    /// The set the agent last read out of the plugin. Held rather than queued: this is a
    /// level, so the newest set is the only one worth sending and an older one is never
    /// worth retransmitting.
    /// </summary>
    public void PublishEncounterTriggers(string module, IReadOnlyList<uint> encounterIds)
    {
        lock (_gate)
        {
            _encounterModule = module ?? string.Empty;
            _encounterIds = [.. encounterIds.Take(ProtocolConstants.MaxEncounterTriggers)];
        }
    }

    private void SendEncounterTriggers()
    {
        string module;
        uint[] ids;

        lock (_gate)
        {
            if (_encounterIds.Length == 0 || _localPeerId == 0)
            {
                // Nothing to say. Deliberately silent rather than sending an empty set every
                // tick: the host ages a peer's last report out on its own, so "I left" needs
                // no datagram and standing in open ground costs no traffic.
                return;
            }

            module = _encounterModule;
            ids = _encounterIds;
        }

        int length = PacketCodec.Write(
            _sendBuffer, new EncounterTriggerPacket(_localPeerId, module, ids));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    /// <summary>The container-loot removals received from the host, for the agent to apply.</summary>
    public IReadOnlyList<ContainerLootRemoval> ContainerLootRemovals => _lootStore.Snapshot();

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
                    : _reconnecting
                        ? $"reconnecting to {_hostEndPoint}..."
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

        // Link-loss detection: once connected, silence longer than the peer timeout means
        // the host is gone (crashed, left, or a network drop). Fall back to the handshake
        // so the SAME client — same nonce — rejoins automatically when the host returns,
        // and the host restores its slot rather than leaving a ghost. This is what makes
        // "the host restarted" or "the router blipped" recoverable in-game.
        if (IsConnected && _lastInboundMs != 0 && now - _lastInboundMs > _settings.PeerTimeoutMs)
        {
            _localPeerId = 0;
            _rejectReason = string.Empty;
            _reconnecting = true;
            _lastInboundMs = 0;

            // ⚠ DROP THE CONVERSATION TOKEN. It belongs to a session we are no longer in,
            // and it is level state the agent re-publishes every tick — so a host that
            // CRASHES mid-conversation (rather than leaving cleanly) left this client
            // spectating forever with its own replies disabled. Measured live: the host
            // was killed mid-conversation and 20 s later the client still read
            // "remote: active 1". The explicit-disconnect path was already covered; this
            // is the same hole on the timeout path. The host re-publishes a fresh token
            // on reconnect, so clearing costs nothing.
            lock (_gate)
            {
                _dialogue = DialogueState.Idle;
            }
        }

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
        SendDialogueRequest();
        SendInteractionClaim();
        SendPhantomDamage();
        SendEncounterTriggers();
        FlushChat();
        FlushBarks();

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

    private void SendHello()
    {
        string module;
        lock (_gate)
        {
            module = _localState.Module;
        }

        int length = PacketCodec.Write(
            _sendBuffer,
            new HelloPacket(
                _settings.PlayerName, module, _nonce, _settings.Digest,
                SessionPassword.Hash(_settings.Password)));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    /// <summary>
    /// Sends this client's conversation status every tick, idle or not — same "state, not
    /// events" rule as the avatar update, so a released token is never lost to a dropped
    /// packet. The host's arbiter turns it into token grants/updates/releases.
    /// </summary>
    // ---- per-object interaction locks -------------------------------------------------

    private string _interactionModule = string.Empty;
    private string _interactionTag = string.Empty;
    private InteractionKind _interactionKind;
    private bool _interactionActive;

    private IReadOnlyList<InteractionLock> _interactionLocks = [];
    private string _interactionLockModule = string.Empty;

    public void PublishInteractionClaim(string module, string tag, InteractionKind kind, bool active)
    {
        lock (_gate)
        {
            _interactionModule = module ?? string.Empty;
            _interactionTag = tag ?? string.Empty;
            _interactionKind = kind;
            _interactionActive = active;
        }
    }

    /// <summary>
    /// The locks the host last broadcast, or empty when they describe a DIFFERENT module.
    ///
    /// The module check is not belt-and-braces: a lock names a TAG, tags are only unique
    /// inside a module, and a client that has walked through a door is briefly holding the
    /// previous area's set. Applying those would hold whatever happens to share a tag here.
    /// </summary>
    public IReadOnlyList<InteractionLock> GetInteractionLocks()
    {
        lock (_gate)
        {
            return string.Equals(_interactionLockModule, _interactionModule, StringComparison.OrdinalIgnoreCase)
                ? _interactionLocks
                : [];
        }
    }

    /// <summary>
    /// Re-sent every tick while a claim is held, never as an edge — the host's store expires
    /// a lease that stops being refreshed, so this IS the release: stop asking and the NPC is
    /// free within one lease. A client that crashes mid-conversation therefore cannot strand
    /// anybody, which no message-based release can promise.
    /// </summary>
    private void SendInteractionClaim()
    {
        string module;
        string tag;
        InteractionKind kind;
        bool active;
        lock (_gate)
        {
            module = _interactionModule;
            tag = _interactionTag;
            kind = _interactionKind;
            active = _interactionActive;
        }

        if (tag.Length == 0)
        {
            return;
        }

        int length = PacketCodec.Write(
            _sendBuffer, new InteractionClaimPacket(_localPeerId, module, tag, kind, active));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    private void SendDialogueRequest()
    {
        bool active;
        string speaker;
        string line;
        string partnerTag;
        uint replySerial;
        int replyIndex;
        byte[]? pickLog;
        int nodeIndex;
        uint nodeSerial;
        lock (_gate)
        {
            active = _localDialogueActive;
            speaker = _localDialogueSpeaker;
            line = _localDialogueLine;
            partnerTag = _localDialoguePartnerTag;
            replySerial = _localDialogueReplySerial;
            replyIndex = _localDialogueReplyIndex;
            pickLog = _localDialoguePickLog;
            nodeIndex = _localDialogueNode;
            nodeSerial = _localDialogueNodeSerial;
        }

        int length = PacketCodec.Write(
            _sendBuffer,
            new DialogueRequestPacket(
                _localPeerId, active, speaker, line, partnerTag, replySerial, replyIndex, pickLog,
                nodeIndex, nodeSerial));
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

    private void SendContainerLoot(ContainerLootPacket op)
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

            case PacketType.ContainerLootAck:
                if (PacketCodec.ReadContainerLootAck(span) is { } lootAck)
                {
                    // The ack names its sender (the host) so the shared per-recipient
                    // sender can mark exactly that recipient done.
                    _lootSender.Acknowledge(lootAck.PeerId, lootAck.OpSequence);
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
                        _enemyModule = enemySnapshot.Module;
                    }
                }

                break;

            case PacketType.WorldDigest:
                if (PacketCodec.ReadWorldDigest(span) is { } hostDigest)
                {
                    _hostWorld = hostDigest.Digest;
                    RefineCompatibility();
                }

                break;

            case PacketType.PartySnapshot:
                if (PacketCodec.ReadPartySnapshot(span) is { } party)
                {
                    lock (_gate)
                    {
                        _party = party.Members;
                    }
                }

                break;

            case PacketType.PartyRoster:
                if (PacketCodec.ReadPartyRoster(span) is { } rosterSnapshot)
                {
                    lock (_gate)
                    {
                        // A LEVEL, replaced wholesale — the host sends the complete roster
                        // every tick, so there is no "companion left the party" edge to miss.
                        _partyRoster = rosterSnapshot.Roster;
                        _hasPartyRoster = true;
                    }
                }

                break;

            case PacketType.DialogueSnapshot:
                if (PacketCodec.ReadDialogueSnapshot(span) is { } dialogue)
                {
                    lock (_gate)
                    {
                        _dialogue = dialogue.State;
                    }
                }

                break;

            case PacketType.PhantomDamage:
                HandlePhantomDamage(span);
                break;

            case PacketType.Bark:
                if (PacketCodec.ReadBark(span) is { } bark)
                {
                    lock (_gate)
                    {
                        _barkInbox.Enqueue(bark);
                    }
                }

                break;

            case PacketType.InteractionLock:
                if (PacketCodec.ReadInteractionLock(span) is { } interaction)
                {
                    lock (_gate)
                    {
                        // A LEVEL, replaced wholesale. The host sends the complete live set
                        // every tick, so a lock that has gone simply is not in the next one —
                        // there is no release to miss.
                        _interactionLocks = interaction.Locks;
                        _interactionLockModule = interaction.Module ?? string.Empty;
                    }
                }

                break;

            case PacketType.Chat:
                if (PacketCodec.ReadChat(span) is { } chat)
                {
                    lock (_gate)
                    {
                        _chatInbox.Enqueue(new ChatMessage(chat.FromPeerId, chat.Name, chat.Text));
                    }
                }

                break;

            case PacketType.ProgressionSnapshot:
                if (PacketCodec.ReadProgressionSnapshot(span) is { } progression)
                {
                    lock (_gate)
                    {
                        _gold = progression.Gold;
                        _experience = progression.Experience;
                        _hasProgression = true;
                    }
                }

                break;

            case PacketType.ItemSnapshot:
                if (PacketCodec.ReadItemSnapshot(span) is { } itemSnapshot)
                {
                    Items.ReplaceAll(itemSnapshot.Items, itemSnapshot.Version);
                }

                break;

            case PacketType.ContainerLoot:
                HandleContainerLoot(span);
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
            _reconnecting = false;

            // Judge compatibility from our side too, from the host's digest in the Welcome.
            // The host already refused an incompatible client; this gives the client the
            // same capability set (so both ends gate the same channels) and the same
            // warnings to show. Computed identically, so the two agree without negotiating.
            ContentCompatibility.RequireIdenticalSave = _settings.RequireIdenticalSave;
            _installVerdict = ContentCompatibility.Compare(_settings.Digest, welcome.Digest);
            _compatibility = _installVerdict;
            RefineCompatibility();
        }
    }

    /// <summary>
    /// Records a container-loot removal from the host and acks it. Acked unconditionally — a
    /// duplicate op means our previous ack was lost — and the store dedups by op sequence, so
    /// applying the removal twice is impossible even though the ack repeats.
    /// </summary>
    private void HandleContainerLoot(ReadOnlySpan<byte> span)
    {
        if (PacketCodec.ReadContainerLoot(span) is not { } op)
        {
            _channel.NoteMalformed();
            return;
        }

        _lootStore.ApplyOp(op);

        int length = PacketCodec.Write(
            _replyBuffer, new ContainerLootAckPacket(_localPeerId, op.OpSequence));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _replyBuffer.AsSpan(0, length));
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
