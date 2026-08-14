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
    private uint _nextItemSequence = 1;
    private long _lastInboundMs;
    private double _roundTripMs;
    private string _rejectReason = string.Empty;
    private bool _reconnecting;

    /// <summary>
    /// Set on every Welcome, cleared once the three world stores have been re-requested.
    ///
    /// ⚠ THE VERSION TRIPWIRE ALONE CANNOT BE TRUSTED ACROSS AN OUTAGE. Each store's version
    /// is a count of local changes, not a fingerprint of its contents, so two sets that
    /// diverged by the same NUMBER of changes compare equal and no resync is ever requested —
    /// permanent silent divergence. Ordinarily the client's own ops bump the host and break
    /// the tie, but an outage is exactly the window in which both ends change independently,
    /// and a client change the host already holds at that value bumps nothing. So a
    /// reconnection asks for all three snapshots unconditionally instead of waiting to be
    /// told it needs them.
    /// </summary>
    private bool _resyncOnConnect;

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

        _compatibility = ContentCompatibility.ApplyWorldMismatch(
            install,
            mismatch.GlobalsCatalogueMismatch,
            mismatch.SaveIdentityMismatch,
            mismatch.ModuleGitMismatch);
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

    /// <summary>This client's transport fault injector. Arming it partitions this one player
    /// and leaves the rest of the session running, which is the ordinary "one player's
    /// router blipped" case.</summary>
    public NetworkSim Network => _channel.Sim;

    public bool IsConnected => _localPeerId != 0;

    public string LocalName => _settings.PlayerName;

    public EventFlagStore Flags { get; } = new();

    public GroundItemStore Items { get; } = new();

    public uint DropItem(string resref, string module, Vector3f position)
    {
        if (_sessionPeerId == 0)
        {
            return 0;
        }

        uint itemId;
        lock (_gate)
        {
            itemId = (_sessionPeerId << 16) | (_nextItemSequence++ & 0xFFFF);
        }

        GroundItem item = new(itemId, resref, module, position);

        // Applied locally straight away so the dropping player is not waiting on a round
        // trip; the host's authoritative version will confirm or correct it.
        if (!Items.Drop(item))
        {
            return 0;
        }

        ItemOpPacket op = _itemSender.Enqueue(_sessionPeerId, ItemOpKind.Drop, item, SessionClock.NowMs);

        if (IsConnected)
        {
            SendItemOp(op);
        }

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

        // Queued under the last admitted identity so a chest emptied during an outage still
        // reaches the host afterwards — see SetFlag.
        _lootSender.Enqueue(_sessionPeerId, containerId, itemId, SessionClock.NowMs);
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

        // Queued while disconnected too — see SetFlag.
        if (_sessionPeerId == 0)
        {
            return;
        }

        JournalOpPacket op = _journalSender.Enqueue(_sessionPeerId, questTag, state, SessionClock.NowMs);

        if (IsConnected)
        {
            SendJournalOp(op);
        }
    }

    public void RemoveItem(uint itemId)
    {
        if (!Items.Remove(itemId) || _sessionPeerId == 0)
        {
            return;
        }

        GroundItem tombstone = new(itemId, string.Empty, string.Empty, Vector3f.Zero);
        ItemOpPacket op = _itemSender.Enqueue(_sessionPeerId, ItemOpKind.Remove, tombstone, SessionClock.NowMs);

        if (IsConnected)
        {
            SendItemOp(op);
        }
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

        // ⚠ QUEUED EVEN WHILE THE LINK IS DOWN. The gate used to be _localPeerId, which is
        // zeroed the moment the host goes quiet — so every world change the player made
        // during an outage was applied locally and never queued at all, and the reliable
        // channel that exists to survive exactly this had nothing in it to deliver.
        if (_sessionPeerId == 0)
        {
            return;
        }

        FlagOpPacket op = _flagSender.Enqueue(_sessionPeerId, flagId, value, SessionClock.NowMs);

        if (IsConnected)
        {
            SendFlagOp(op);
        }
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
            ForgetSession();
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
    private void ForgetSession()
    {
        _localPeerId = 0;
        _rejectReason = string.Empty;
        _reconnecting = true;
        _lastInboundMs = 0;
        _absentFromSnapshotSinceMs = 0;

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
        PacketType type = PacketCodec.PeekType(span);

        // ⚠ NOT EVERY PACKET PROVES WE ARE STILL IN THE SESSION. The liveness clock is the
        // only thing that notices a dropped link, and a Pong or an op ack is sent by the host
        // to anyone who asks — member or not. So a client the host has expired (a one-way
        // outage: our upstream failed, its broadcasts kept arriving) could keep its own clock
        // fresh with its own pings, and never work out that it had become a ghost. Only
        // traffic the host sends to MEMBERS counts: the snapshots, the levels, the handshake.
        if (type is not (PacketType.Pong or PacketType.FlagAck or PacketType.ItemAck
            or PacketType.JournalAck or PacketType.ContainerLootAck))
        {
            _lastInboundMs = now;
        }

        switch (type)
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
                    // Merge, never replace: world flags are monotonic, and a snapshot must
                    // not lower (or delete) one a local advance has just raised. The journal
                    // has always done it this way; flags used to overwrite, which is how a
                    // change made during an outage disappeared on reconnect.
                    Flags.MergeSnapshot(flagSnapshot.Flags, flagSnapshot.Version);
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
            _sessionPeerId = welcome.AssignedPeerId;
            _reconnecting = false;

            // The link is back. Everything still unacked backed off while it was down; make
            // it all due now so the world catches up in one tick instead of over the next few
            // seconds. Nothing is re-created here — the ops kept their sequence numbers, and
            // the host dedups on (origin, sequence), so a re-send the host already applied is
            // recognised and merely re-acked.
            _flagSender.ResetBackoff();
            _itemSender.ResetBackoff();
            _journalSender.ResetBackoff();
            _lootSender.ResetBackoff();

            // ...and ask for the full world back, rather than trusting three version counters
            // that an outage can leave equal-but-different. See _resyncOnConnect.
            _resyncOnConnect = true;
            _lastFlagRequestMs = 0;
            _lastItemRequestMs = 0;
            _lastJournalRequestMs = 0;

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

        HashSet<uint> present = [];
        bool listed = false;

        foreach (SnapshotEntry entry in snapshot.Entries)
        {
            if (entry.PeerId == _localPeerId)
            {
                listed = true;
                continue;
            }

            present.Add(entry.PeerId);
            _buffer.Push(entry.PeerId, entry.Name, entry.State, now);
        }

        // Half-open link: the host has forgotten us but we can still hear it. See
        // _absentFromSnapshotSinceMs. Given a grace period rather than acted on immediately,
        // because a snapshot can also arrive in the window between our Welcome and the host's
        // next tick.
        if (listed)
        {
            _absentFromSnapshotSinceMs = 0;
        }
        else if (_localPeerId != 0)
        {
            if (_absentFromSnapshotSinceMs == 0)
            {
                _absentFromSnapshotSinceMs = now;
            }
            else if (now - _absentFromSnapshotSinceMs > _settings.PeerTimeoutMs)
            {
                ForgetSession();
                return;
            }
        }

        // The snapshot is the complete roster, so anyone missing from it has left — drop them
        // now rather than holding a phantom for another peer timeout. That window is what put
        // two copies of the same player on screen when one of them reconnected during it.
        _buffer.RetainOnly(present);

        // A reconnection asks for everything once, regardless of what the versions say. The
        // request is only disarmed once all three senders are quiet, because a forced resync
        // while our own ops are still in flight is exactly the overwrite the pending gates
        // below exist to prevent — so it waits its turn rather than being spent unused.
        bool forced = _resyncOnConnect;
        if (forced
            && _flagSender.PendingCount == 0
            && _itemSender.PendingCount == 0
            && _journalSender.PendingCount == 0)
        {
            _resyncOnConnect = false;
        }

        // The version tripwire: 4 bytes per tick tells us whether our world flags still
        // match the host's, and the full set only crosses the wire when they do not.
        //
        // Gated on our own ops being acked, exactly like items and journal below and for the
        // same reason: until they land, the two sides are SUPPOSED to differ, and asking for
        // a snapshot in that window used to be how a client's own change got overwritten.
        if ((forced || snapshot.FlagVersion != Flags.Version)
            && _flagSender.PendingCount == 0
            && now - _lastFlagRequestMs >= FlagResyncIntervalMs)
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
        if ((forced || snapshot.ItemVersion != Items.Version)
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
        if ((forced || snapshot.JournalVersion != Journal.Version)
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
