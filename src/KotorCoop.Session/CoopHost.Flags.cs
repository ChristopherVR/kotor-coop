using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — World event-flag replication: the host is authoritative, so a client's op is
/// applied and acked immediately; clients pick up host-side changes through the
/// flag version/digest carried in every world snapshot.
/// </summary>
public sealed partial class CoopHost
{
    public EventFlagStore Flags { get; } = new();
    public uint FlagLoadRevision { get; private set; }
    private uint _flagNativeEpoch;
    private string _flagReadyModule = string.Empty;

    private void ObserveLoadedFlagWorld(in WorldDigest digest)
    {
        if (digest.Module.Length == 0 || digest.NativeWorldEpoch == 0) return;
        if (_flagNativeEpoch != 0 && digest.NativeWorldEpoch != _flagNativeEpoch
            && string.Equals(digest.Module, _flagReadyModule, StringComparison.OrdinalIgnoreCase))
            ResetFlagsForLoadedSave();
        _flagNativeEpoch = digest.NativeWorldEpoch;
        _flagReadyModule = digest.Module;
    }

    private void ResetFlagsForLoadedSave()
    {
        // A checkpoint replaces object state, including other visited modules.
        Flags.Prune(KotorCoop.Memory.ObjectKeyedFlags.IsObjectKeyed);
        FlagLoadRevision++;
        _flagNativeEpoch = 0;
    }

    public void SetFlag(ulong flagId, sbyte value)
    {
        // The host is authoritative, so its own changes apply immediately. Clients pick
        // them up through the version tripwire in the next world snapshot.
        lock (_gate) Flags.Set(flagId, value, HostPeerId);
    }

    private void HandleFlagOp(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadFlagOp(span) is not { } op)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (op.HostSessionId != _sessionId || op.HostLoadSerial != _localWorld.LoadSerial || op.WorldRevision != FlagLoadRevision
                || !_peers.TryGetValue(op.OriginPeerId, out PeerConnection? peer)
                || !peer.EndPoint.Equals(from)) return;
            Flags.ApplyOp(op);
        }

        // Ack unconditionally: a duplicate op means the previous ack was lost.
        int length = PacketCodec.Write(_replyBuffer, new FlagAckPacket(op.OpSequence));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    private void HandleFlagSyncRequest(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadFlagSyncRequest(span) is null)
        {
            _channel.NoteMalformed();
            return;
        }

        // ⚠ PAGED, AND IT HAS TO BE. This reply is the ONLY way a host-side change reaches a
        // client that did not watch it happen — there are no host→client flag ops (see
        // SetFlag's own comment above) — and doors and placeables share this store with
        // globals, which is never pruned. Sending one datagram meant everything past the first
        // 106 entries of the session was undeliverable forever: measured live as a door OPEN on
        // the host and CLOSED on the client, with the client re-requesting the same truncated
        // head every 500 ms because the digest covers the whole store.
        //
        // Same shape as CoopHost.Journal's own paging, and the same loss model: each page
        // carries the full version, so a lost page leaves a gap the client cannot detect until
        // the next change bumps the version. That is acceptable for the journal because ops are
        // its primary path; here it is merely much better than the alternative, and a client
        // whose digest still disagrees re-requests on its own 500 ms timer anyway — which now
        // actually converges instead of asking the same unanswerable question forever.
        lock (_gate)
        {
        List<FlagSnapshotEntry> entries = Flags.SnapshotEntries();
        uint version = Flags.Version;

        for (int start = 0; ; start += ProtocolConstants.MaxFlagEntriesPerPacket)
        {
            List<FlagSnapshotEntry> page =
                [.. entries.Skip(start).Take(ProtocolConstants.MaxFlagEntriesPerPacket)];

            int length = PacketCodec.Write(_replyBuffer, new FlagSnapshotPacket(version, page, _sessionId, _localWorld.LoadSerial, FlagLoadRevision));
            if (length > 0)
            {
                _channel.Send(from, _replyBuffer.AsSpan(0, length));
            }

            if (start + ProtocolConstants.MaxFlagEntriesPerPacket >= entries.Count)
            {
                break;
            }
        }
        }
    }
}
