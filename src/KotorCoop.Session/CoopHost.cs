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

        /// <summary>The peer's in-world digest (globals catalogue, save identity, current
        /// module + GIT), every field 0/empty until it reports one.</summary>
        public WorldDigest World = WorldDigest.Empty;
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

    private readonly ReliableLootSender _lootSender = new();

    /// <summary>
    /// The host looted an item from a shared container; broadcast the removal reliably so it
    /// vanishes from every client's copy too. Enqueue-only — the tick loop, which owns the
    /// send buffer, does the actual broadcasting and retransmits.
    /// </summary>
    public void PublishContainerLoot(uint containerId, uint itemId) =>
        _lootSender.Enqueue(HostPeerId, containerId, itemId, SessionClock.NowMs);

    /// <summary>The host is the enemy authority: its fights are the ones that count.</summary>
    public bool IsEnemyAuthority => true;

    public void PublishEnemies(string module, IReadOnlyList<EnemyState> enemies)
    {
        lock (_gate)
        {
            _enemyModule = module;
            _enemies = enemies;
        }
    }

    public IReadOnlyList<EnemyState> GetEnemies(string module) => [];

    private IReadOnlyList<EnemyState> _enemies = [];

    /// <summary>The module the published ids were read in; it travels with them so a peer
    /// somewhere else can tell that they mean nothing to it.</summary>
    private string _enemyModule = string.Empty;

    private IReadOnlyList<PartyMemberState> _party = [];

    public void PublishParty(IReadOnlyList<PartyMemberState> members)
    {
        lock (_gate)
        {
            _party = members;
        }
    }

    public IReadOnlyList<PartyMemberState> GetPartyMembers() => [];

    private uint _gold;
    private uint _experience;
    private bool _hasProgression;

    public void PublishProgression(uint gold, uint experience)
    {
        lock (_gate)
        {
            _gold = gold;
            _experience = experience;
            _hasProgression = true;
        }
    }

    /// <summary>The host is the source of progression; it never mirrors a peer's.</summary>
    public bool TryGetProgression(out uint gold, out uint experience)
    {
        gold = 0;
        experience = 0;
        return false;
    }

    /// <summary>The single conversation token; the host is the sole arbiter.</summary>
    private DialogueState _dialogue = DialogueState.Idle;

    public void PublishDialogue(
        bool active, string speakerName, string line,
        string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null)
    {
        lock (_gate)
        {
            // The host's own claim goes through the same arbiter as a client's, so ownership
            // and the serial are decided in exactly one place.
            _dialogue = DialogueArbiter.Apply(
                _dialogue, HostPeerId, active, speakerName, line, partnerTag, replySerial, replyIndex, pickLog);
        }
    }

    public DialogueState GetDialogue()
    {
        lock (_gate)
        {
            return _dialogue;
        }
    }

    /// <summary>Drops the token if the peer that held it has gone. Called on disconnect.</summary>
    private void ReleaseDialogueIfOwnedBy(uint peerId)
    {
        if (_dialogue.IsOwnedBy(peerId))
        {
            _dialogue = new DialogueState(false, 0, _dialogue.Serial + 1, string.Empty, string.Empty);
        }
    }

    /// <summary>Chat received from clients, for the host's own display.</summary>
    private readonly Queue<ChatMessage> _chatInbox = new();

    /// <summary>Chat to send from the tick loop (own lines and relays), with who to skip.</summary>
    private readonly Queue<(ChatPacket Packet, uint ExcludePeerId)> _chatOutbox = new();

    public void SendChat(string text)
    {
        text = text?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return;
        }

        // Queued, not sent here: SendChat is called from the agent thread, but the send
        // buffer belongs to the tick loop. The host's own line is echoed locally by the
        // caller, so every client is a recipient (exclude only the host itself).
        lock (_gate)
        {
            _chatOutbox.Enqueue((new ChatPacket(HostPeerId, LocalName, text), HostPeerId));
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

    /// <summary>Flushes queued chat from the tick loop, so the send buffer is single-threaded.</summary>
    private void FlushChat(List<PeerConnection> recipients)
    {
        while (true)
        {
            (ChatPacket Packet, uint ExcludePeerId) item;
            lock (_gate)
            {
                if (_chatOutbox.Count == 0)
                {
                    return;
                }

                item = _chatOutbox.Dequeue();
            }

            int length = PacketCodec.Write(_sendBuffer, item.Packet);
            if (length == 0)
            {
                continue;
            }

            foreach (PeerConnection peer in recipients)
            {
                if (peer.Id != item.ExcludePeerId)
                {
                    _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
                }
            }
        }
    }

    private void HandleChat(ReadOnlySpan<byte> span)
    {
        if (PacketCodec.ReadChat(span) is not { } chat)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            // Only a known peer may chat, and it cannot spoof another's id.
            if (!_peers.ContainsKey(chat.FromPeerId))
            {
                return;
            }

            _chatInbox.Enqueue(new ChatMessage(chat.FromPeerId, chat.Name, chat.Text));
            // Relay to the others in the star; the sender already sees its own line.
            _chatOutbox.Enqueue((chat, chat.FromPeerId));
        }
    }

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

    // ---- phantom damage (combat participation) -----------------------------------
    //
    // Running totals, keyed by the peer the damage is FOR. Sent to that peer every tick;
    // when the addressee is the host itself, kept here for the pump to read. Cumulative
    // and monotonic, so an out-of-order or repeated datagram can never double-apply.

    /// <summary>
    /// Keyed by (who it is FOR, who DEALT it). ⚠ The source half is not decoration: a total
    /// is per-sender, so folding two senders into one key means whichever wrote last wins and
    /// the other's damage is thrown away every tick. The host both originates reports (its own
    /// game hit a phantom) and relays them (a client's game did), so with three players two
    /// sources for the same target is the ordinary case.
    /// </summary>
    private readonly Dictionary<(uint Target, uint Source), (uint Damage, uint Attacker)> _phantomDamageOut = [];

    /// <summary>Running totals owed to the local player, per attacking peer. See
    /// <see cref="GetIncomingPhantomDamage"/> for why this cannot be one number.</summary>
    private readonly Dictionary<uint, (uint Damage, uint Attacker)> _phantomDamageIn = [];

    private uint _lastIncomingAttacker;

    public void PublishPhantomDamage(uint targetPeerId, uint cumulativeDamage, uint attackerId)
    {
        if (targetPeerId == 0 || targetPeerId == LocalPeerId)
        {
            return;
        }

        lock (_gate)
        {
            _phantomDamageOut[(targetPeerId, LocalPeerId)] = (cumulativeDamage, attackerId);
        }
    }

    /// <summary>
    /// The total this player owes, summed across everyone who has hit their phantom.
    ///
    /// ⚠ Each entry is a per-sender RUNNING TOTAL, so they must be added, not maxed. The
    /// old single-slot version kept only the largest total it had seen: once one peer had
    /// dealt 40, a second peer's reports of 5, 10, 15 were all smaller and every one of them
    /// was discarded - that player's damage simply never landed. Invisible with two players,
    /// because there is only ever one source.
    ///
    /// The sum stays monotonic (every term is), which is what the plugin's applied-so-far
    /// bookkeeping needs. Entries are therefore NOT dropped when a peer leaves: removing one
    /// would walk the total backwards.
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

    /// <summary>Records one sender's running total, ignoring a reordered datagram that would
    /// walk that sender's figure backwards.</summary>
    private void AcceptIncomingPhantomDamage(uint sourcePeerId, uint cumulative, uint attacker)
    {
        if (_phantomDamageIn.TryGetValue(sourcePeerId, out (uint Damage, uint Attacker) seen)
            && cumulative <= seen.Damage)
        {
            return;
        }

        _phantomDamageIn[sourcePeerId] = (cumulative, attacker);

        if (attacker != 0)
        {
            _lastIncomingAttacker = attacker;
        }
    }

    /// <summary>Drops a departed peer from the outbound table. The inbound side keeps its
    /// entry on purpose - see <see cref="GetIncomingPhantomDamage"/>.</summary>
    private void ForgetPhantomDamageFor(uint peerId)
    {
        List<(uint Target, uint Source)>? dead = null;
        foreach ((uint Target, uint Source) key in _phantomDamageOut.Keys)
        {
            if (key.Target == peerId || key.Source == peerId)
            {
                (dead ??= []).Add(key);
            }
        }

        if (dead is null)
        {
            return;
        }

        foreach ((uint Target, uint Source) key in dead)
        {
            _phantomDamageOut.Remove(key);
        }
    }

    /// <summary>A client reported damage its game did to someone's phantom. If it is for
    /// the host, keep it; otherwise it is relayed to the addressee on the next broadcast.</summary>
    private void HandlePhantomDamage(ReadOnlySpan<byte> span)
    {
        if (PacketCodec.ReadPhantomDamage(span) is not { } packet)
        {
            return;
        }

        lock (_gate)
        {
            if (packet.TargetPeerId == LocalPeerId)
            {
                AcceptIncomingPhantomDamage(packet.FromPeerId, packet.CumulativeDamage, packet.AttackerId);
            }
            else if (packet.TargetPeerId != 0)
            {
                // Relayed. The ORIGINATOR is kept as the key and re-sent as the From field,
                // so the addressee can tell two attackers apart; stamping the host's own id
                // on it would merge every relayed report into one running total.
                _phantomDamageOut[(packet.TargetPeerId, packet.FromPeerId)] =
                    (packet.CumulativeDamage, packet.AttackerId);
            }
        }
    }

    private void BroadcastPhantomDamage(List<PeerConnection> recipients)
    {
        List<KeyValuePair<(uint Target, uint Source), (uint Damage, uint Attacker)>> owed;
        lock (_gate)
        {
            if (_phantomDamageOut.Count == 0)
            {
                return;
            }

            owed = [.. _phantomDamageOut];
        }

        // One datagram per (addressee, source) pair rather than per addressee: each carries
        // that source's own running total, which is what the receiver sums.
        foreach (PeerConnection peer in recipients)
        {
            foreach ((( uint target, uint source), (uint damage, uint attacker)) in owed)
            {
                if (target != peer.Id || damage == 0)
                {
                    continue;
                }

                int length = PacketCodec.Write(
                    _sendBuffer, new PhantomDamagePacket(source, peer.Id, damage, attacker));
                if (length > 0)
                {
                    _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
                }
            }
        }
    }

    public JournalStore Journal { get; } = new();

    public void SetJournalEntry(string questTag, int state)
    {
        // Host-authoritative, like flags: merge locally, clients pick it up through the
        // journal version in the next world snapshot.
        Journal.Merge(questTag, state);
    }

    private CompatibilityVerdict? _compatibility;
    private WorldDigest _localWorld = WorldDigest.Empty;

    public void PublishWorldDigest(in WorldDigest digest) => _localWorld = digest;

    /// <summary>
    /// This peer's effective capabilities: its install verdict, refined by the in-world
    /// comparison (globals catalogue, save identity, and the GIT of the module both are
    /// standing in). Per the Unknown-is-not-mismatch rule, a field must be resolved on
    /// BOTH sides before a difference counts — and because the module fields follow the
    /// players, walking into a module whose GITs agree restores the id-keyed channels a
    /// previous module's mismatch dropped.
    /// </summary>
    private CompatibilityVerdict EffectiveVerdict(PeerConnection peer)
    {
        WorldDigestComparison mismatch = WorldDigest.Compare(_localWorld, peer.World);

        return ContentCompatibility.ApplyWorldMismatch(
            peer.InstallVerdict,
            mismatch.GlobalsCatalogueMismatch,
            mismatch.SaveIdentityMismatch,
            mismatch.ModuleGitMismatch);
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

    /// <summary>Latched so an over-full lobby writes one line, not one per tick.</summary>
    private bool _snapshotOverflowLogged;

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
                    ReleaseDialogueIfOwnedBy(id);
                    ForgetPhantomDamageFor(id);
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
            // ⚠ Say so, and carry on. A full lobby with long names, long module names and
            // equipped gear overruns MaxPacketSize, and the old early return took every other
            // channel down with it - enemies, party, dialogue, progression, loot, damage,
            // chat and the stale-peer prune all stopped, silently, for as long as the lobby
            // stayed full. Losing player positions for a tick is bad; losing the entire
            // session's state machine and not logging it is worse.
            if (!_snapshotOverflowLogged)
            {
                _snapshotOverflowLogged = true;
                Console.Error.WriteLine(
                    $"[coop] world snapshot for {snapshot.Entries.Count} peers does not fit in "
                    + "one packet; positions are not being sent. Shorter player names help.");
            }
        }
        else
        {
            _snapshotOverflowLogged = false;

            foreach (PeerConnection peer in recipients)
            {
                _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
            }
        }

        BroadcastEnemies(now, recipients);
        BroadcastParty(now, recipients);
        BroadcastDialogue(now, recipients);
        BroadcastProgression(now, recipients);
        BroadcastContainerLoot(now, recipients);
        BroadcastPhantomDamage(recipients);
        BroadcastWorldDigest(now, recipients);
        FlushChat(recipients);
        _buffer.PruneStale(now, _settings.PeerTimeoutMs);
    }

    /// <summary>
    /// Retransmits container-loot removals to every client until each has acked. Reliable
    /// rather than snapshot-broadcast: a removal is a one-shot fact, not a level in a state
    /// that the next tick re-sends anyway, so it must survive a dropped packet. An op is
    /// retired once every currently-connected client has confirmed it.
    /// </summary>
    private void BroadcastContainerLoot(long now, List<PeerConnection> recipients)
    {
        List<uint> recipientIds = [.. recipients.Select(p => p.Id)];

        foreach (ContainerLootPacket op in _lootSender.CollectDue(recipientIds, now))
        {
            int length = PacketCodec.Write(_sendBuffer, op);
            if (length == 0)
            {
                continue;
            }

            foreach (PeerConnection peer in recipients)
            {
                _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
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
        string module;
        lock (_gate)
        {
            enemies = _enemies;
            module = _enemyModule;
        }

        if (enemies.Count == 0 || recipients.Count == 0)
        {
            return;
        }

        int length = PacketCodec.Write(_sendBuffer, new EnemySnapshotPacket(now, module, enemies));
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
    /// Sends the authoritative party companions. Unreliable and repeated every tick, like
    /// the enemy snapshot: the host owns companion movement through its own engine AI, and
    /// each client mirrors this onto its frozen copies.
    /// </summary>
    private void BroadcastParty(long now, List<PeerConnection> recipients)
    {
        IReadOnlyList<PartyMemberState> party;
        lock (_gate)
        {
            party = _party;
        }

        if (party.Count == 0 || recipients.Count == 0)
        {
            return;
        }

        int length = PacketCodec.Write(_sendBuffer, new PartySnapshotPacket(now, party));
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
    /// Broadcasts the absolute shared progression each tick once the host has published it.
    /// Like the enemy/party snapshots: a lost packet costs one tick and the next carries the
    /// whole truth, and absolute values make replays idempotent.
    /// </summary>
    private void BroadcastProgression(long now, List<PeerConnection> recipients)
    {
        uint gold;
        uint experience;
        lock (_gate)
        {
            if (!_hasProgression)
            {
                return;
            }

            gold = _gold;
            experience = _experience;
        }

        if (recipients.Count == 0)
        {
            return;
        }

        int length = PacketCodec.Write(_sendBuffer, new ProgressionSnapshotPacket(now, gold, experience));
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
    /// Broadcasts the conversation token every tick, unconditionally (even when idle), so a
    /// client that joined or reconnected mid-conversation converges on the current owner and
    /// a released token is not missed. One tiny packet per tick is negligible.
    /// </summary>
    private void BroadcastDialogue(long now, List<PeerConnection> recipients)
    {
        if (recipients.Count == 0)
        {
            return;
        }

        DialogueState state;
        lock (_gate)
        {
            state = _dialogue;
        }

        int length = PacketCodec.Write(_sendBuffer, new DialogueSnapshotPacket(now, state));
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
    /// Sends the host's in-world digest so each client can judge the world-time match
    /// (globals, save, module GIT) from its side too. Cheap and mostly stable — only the
    /// module fields move, and only on a module change — so a low rate is plenty; sent
    /// only once at least one field has resolved.
    /// </summary>
    private long _lastWorldDigestMs;

    private void BroadcastWorldDigest(long now, List<PeerConnection> recipients)
    {
        WorldDigest world = _localWorld;
        if (world.IsEmpty || recipients.Count == 0 || now - _lastWorldDigestMs < 1000)
        {
            return;
        }

        _lastWorldDigestMs = now;
        int length = PacketCodec.Write(_sendBuffer, new WorldDigestPacket(HostPeerId, world));
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

            case PacketType.ContainerLootAck:
                if (PacketCodec.ReadContainerLootAck(span) is { } lootAck)
                {
                    _lootSender.Acknowledge(lootAck.PeerId, lootAck.OpSequence);
                }

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

            case PacketType.PhantomDamage:
                HandlePhantomDamage(span);
                break;

            case PacketType.DialogueRequest:
                HandleDialogueRequest(span);
                break;

            case PacketType.Chat:
                HandleChat(span);
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
                peer.World = report.Digest;
                _compatibility = EffectiveVerdict(peer);
            }
        }
    }

    private void HandleDialogueRequest(ReadOnlySpan<byte> span)
    {
        if (PacketCodec.ReadDialogueRequest(span) is not { } request)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            // Only a known peer may move the token, and only for its own peer id.
            if (_peers.ContainsKey(request.PeerId))
            {
                _dialogue = DialogueArbiter.Apply(
                    _dialogue, request.PeerId, request.Active, request.SpeakerName, request.Line,
                    request.PartnerTag, request.ReplySerial, request.ReplyIndex, request.PickLog);
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
            ReleaseDialogueIfOwnedBy(bye.PeerId);
            ForgetPhantomDamageFor(bye.PeerId);
        }

        _buffer.Remove(bye.PeerId);
    }

    public void Dispose() => _channel.Dispose();
}
