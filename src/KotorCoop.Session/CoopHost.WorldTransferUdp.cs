using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — the UDP fallback for <c>auto_adopt_world</c>'s world-transfer
/// (<see cref="PacketType.WorldTransferRequest"/> et al.), multiplexed onto the ordinary
/// session socket rather than a second listener (a second UDP socket cannot share this one's
/// port the way <see cref="WorldTransfer"/>'s TCP listener already does). See
/// <see cref="WorldTransferUdpSender"/> and docs/research/network-topology.md's UDP
/// world-transfer section.
///
/// A world-transfer requester is deliberately NOT a connected session peer — see
/// <see cref="WorldAdoption"/>: the client's own co-op socket is closed BEFORE it fetches, so
/// every method here works from the raw <see cref="IPEndPoint"/> a datagram arrived from, never
/// from <see cref="PeerConnection"/>.
/// </summary>
public sealed partial class CoopHost
{
    /// <summary>Matches <see cref="WorldTransfer"/>'s own <c>TimeoutMs</c> — how long an
    /// in-flight transfer may sit with no status report before this host gives up on it and
    /// frees its payload buffer.</summary>
    private const int WorldTransferUdpTimeoutMs = 60_000;

    /// <summary>Chunks sent per transfer per tick. At the default 30 Hz tick rate that is up to
    /// 1,440 chunks/second (~1.4 MB/s of 1 KB chunks) — comfortably under a typical residential
    /// uplink, and a full <see cref="ProtocolConstants.MaxWorldTransferChunks"/>-chunk transfer's
    /// first pass takes about90 seconds at the128 MiB limit.</summary>
    private const int WorldTransferChunksPerTick = 48;

    private Func<(IReadOnlyList<WorldFile> World, uint SaveIdentity)>? _worldTransferProvider;
    private uint _worldTransferPasswordHash;

    /// <summary>At most one in-flight transfer per requesting endpoint — a client only ever has
    /// one adoption fetch running at a time. Guarded by <see cref="_gate"/>: read and written
    /// from both the receive-loop thread (the Handle* methods below) and the tick thread
    /// (<see cref="PumpWorldTransferUdp"/>), exactly like <see cref="_peers"/> is.</summary>
    private readonly Dictionary<IPEndPoint, WorldTransferUdpSender> _worldTransferSenders = [];

    /// <summary>
    /// Wires up what a UDP world-transfer request should serve — the SAME provider and password
    /// hash <see cref="WorldTransfer.ServeAsync(Func{(IReadOnlyList{WorldFile},uint)},int,uint,Action{string}?,CancellationToken)"/>'s
    /// TCP listener already uses, from the same call site
    /// (<c>CoopCommandDispatcher.MaintainWorldServing</c>). Idempotent — safe to call on every
    /// tick, which is how that method is itself driven — and safe to leave unset: a request
    /// arriving with no provider configured is answered <see cref="WorldTransferUdpStatus.NoWorld"/>,
    /// the same as a host with nothing to serve.
    /// </summary>
    public void SetWorldTransferProvider(
        Func<(IReadOnlyList<WorldFile> World, uint SaveIdentity)> provider, uint passwordHash)
    {
        _worldTransferProvider = provider;
        _worldTransferPasswordHash = passwordHash;
    }

    private void HandleWorldTransferRequest(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadWorldTransferRequest(span) is not { } request)
        {
            _channel.NoteMalformed();
            return;
        }

        if (_worldTransferProvider is not { } provider)
        {
            SendWorldTransferHeader(from, request.RequestId, WorldTransferUdpStatus.NoWorld, 0, 0, 0);
            return;
        }

        if (request.PasswordHash != _worldTransferPasswordHash)
        {
            SendWorldTransferHeader(from, request.RequestId, WorldTransferUdpStatus.BadPassword, 0, 0, 0);
            return;
        }

        (IReadOnlyList<WorldFile> world, uint saveIdentity) = provider();
        if (world.Count == 0)
        {
            SendWorldTransferHeader(from, request.RequestId, WorldTransferUdpStatus.NoWorld, 0, 0, 0);
            return;
        }

        WorldTransferUdpSender sender;
        lock (_gate)
        {
            // A fresh RequestId (a genuinely new attempt, or a retry after this host restarted
            // mid-transfer) gets a fresh sender; a repeat of the SAME RequestId re-answers from
            // the existing one rather than re-reading and re-serialising the save directory
            // again (which would also risk a different SaveIdentity mid-transfer if the host's
            // own world changed in between).
            if (!_worldTransferSenders.TryGetValue(from, out WorldTransferUdpSender? existing)
                || existing.RequestId != request.RequestId)
            {
                long estimatedBytes = 2 + world.Sum(file => 6L + file.Name.Length + file.Data.Length);
                long retainedBytes = _worldTransferSenders.Where(pair => !pair.Key.Equals(from))
                    .Sum(pair => (long)pair.Value.TotalBytes);
                if ((!_worldTransferSenders.ContainsKey(from) && _worldTransferSenders.Count >= ProtocolConstants.MaxPeers)
                    || retainedBytes + estimatedBytes > 2L * ProtocolConstants.MaxWorldTransferBytes)
                {
                    SendWorldTransferHeader(from, request.RequestId, WorldTransferUdpStatus.NoWorld, 0, 0, 0);
                    return;
                }
                try
                {
                    byte[] payload = WorldTransfer.SerializePayload(world);
                    existing = new WorldTransferUdpSender(request.RequestId, payload, saveIdentity, now);
                }
                catch (InvalidDataException)
                {
                    SendWorldTransferHeader(from, request.RequestId, WorldTransferUdpStatus.NoWorld, 0, 0, 0);
                    return;
                }
                _worldTransferSenders[from] = existing;
            }

            sender = existing;
        }

        SendWorldTransferHeader(
            from, sender.RequestId, WorldTransferUdpStatus.Ok, sender.SaveIdentity,
            (uint)sender.TotalBytes, (uint)sender.TotalChunks);
    }

    private void SendWorldTransferHeader(
        IPEndPoint to, uint requestId, WorldTransferUdpStatus status, uint saveIdentity,
        uint totalBytes, uint totalChunks)
    {
        int length = PacketCodec.Write(
            _replyBuffer,
            new WorldTransferHeaderPacket(requestId, status, saveIdentity, totalBytes, totalChunks));
        if (length > 0)
        {
            _channel.Send(to, _replyBuffer.AsSpan(0, length));
        }
    }

    private void HandleWorldTransferChunkStatus(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadWorldTransferChunkStatus(span) is not { } status)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (_worldTransferSenders.TryGetValue(from, out WorldTransferUdpSender? sender)
                && sender.RequestId == status.RequestId)
            {
                sender.NoteStatus(status.MissingBitmap, now, status.FirstChunk);
            }
        }
    }

    private void HandleWorldTransferComplete(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadWorldTransferComplete(span) is not { } complete)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (_worldTransferSenders.TryGetValue(from, out WorldTransferUdpSender? sender)
                && sender.RequestId == complete.RequestId)
            {
                _worldTransferSenders.Remove(from);
            }
        }
    }

    /// <summary>
    /// Sends due chunks for every in-flight UDP world-transfer, and drops one that has gone
    /// quiet for <see cref="WorldTransferUdpTimeoutMs"/> — the client either finished
    /// (<see cref="HandleWorldTransferComplete"/> already removed it, the common case) or gave
    /// up, and either way the payload buffer is not worth holding forever. Called from
    /// <see cref="Tick"/> unconditionally, regardless of the connected-peer list — see this
    /// class's own remarks for why a requester is never one of <see cref="_peers"/>.
    /// </summary>
    private void PumpWorldTransferUdp(long now)
    {
        List<(IPEndPoint Endpoint, WorldTransferUdpSender Sender)> active;
        lock (_gate)
        {
            if (_worldTransferSenders.Count == 0)
            {
                return;
            }

            List<IPEndPoint>? stale = null;
            foreach ((IPEndPoint endpoint, WorldTransferUdpSender sender) in _worldTransferSenders)
            {
                if (now - sender.LastActivityMs > WorldTransferUdpTimeoutMs)
                {
                    (stale ??= []).Add(endpoint);
                }
            }

            if (stale is not null)
            {
                foreach (IPEndPoint endpoint in stale)
                {
                    _worldTransferSenders.Remove(endpoint);
                }
            }

            active = [.. _worldTransferSenders.Select(kv => (kv.Key, kv.Value))];
        }

        foreach ((IPEndPoint endpoint, WorldTransferUdpSender sender) in active)
        {
            foreach (int chunkIndex in sender.CollectDue(WorldTransferChunksPerTick))
            {
                int length = PacketCodec.Write(
                    _sendBuffer,
                    new WorldTransferChunkPacket(
                        sender.RequestId, (uint)chunkIndex, sender.Chunk(chunkIndex).ToArray()));

                if (length > 0)
                {
                    _channel.Send(endpoint, _sendBuffer.AsSpan(0, length));
                }
            }
        }
    }
}
