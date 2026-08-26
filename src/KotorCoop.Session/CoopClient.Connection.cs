using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient connection lifecycle: the Hello handshake and Welcome, dropping back to it
/// on link loss or a half-open snapshot absence (ForgetSession), the reliable-sender
/// backoff reset and forced world resync that follows, Bye on shutdown, and the round-
/// trip ping. See CoopClient.cs for the class's core fields, the tick loop and the
/// packet-dispatch registry.
/// </summary>
public sealed partial class CoopClient
{
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
        //
        // ⚠ SAME HOLE, ONE LAYER DOWN — see docs/research/container-sync.md.
        // `GetInteractionLocks()` is "the locks the host last broadcast" — a client-side
        // CACHE, not something with its own expiry, on the theory that the host's own
        // InteractionLockStore lease (see that class's remarks) prunes it and the next
        // broadcast carries the drop. That theory only holds while the HOST is still alive
        // to compute and send that next broadcast; killing the host itself (not just the
        // claimant) leaves nobody left to ever publish the empty list, so this client's
        // cache — exactly like the dialogue token above — stays frozen holding a peer that
        // no longer exists, forever. Dropped here for the same reason and the same way.
        lock (_gate)
        {
            _dialogue = DialogueState.Idle;
            _interactionLocks = [];
            _interactionLockModule = string.Empty;
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

    private void HandleWelcome(IPEndPoint from, ReadOnlySpan<byte> span, long now)
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
            _dialogueGrantSender.ResetBackoff();

            // ...and ask for the full world back, rather than trusting three version counters
            // that an outage can leave equal-but-different. See _resyncOnConnect.
            _resyncOnConnect = true;
            _lastFlagRequestMs = 0;
            _lastItemRequestMs = 0;
            _lastJournalRequestMs = 0;
            _lastLootRequestMs = 0;

            // Judge compatibility from our side too, from the host's digest in the Welcome.
            // The host already refused an incompatible client; this gives the client the
            // same capability set (so both ends gate the same channels) and the same
            // warnings to show. Computed identically, so the two agree without negotiating.
            ContentCompatibility.RequireIdenticalSave = _settings.RequireIdenticalSave;
            _installVerdict = ContentCompatibility.Compare(_settings.Digest, welcome.Digest);
            _compatibility = _installVerdict;
            // A fresh Welcome means a fresh crossing to judge — any settle timer left running
            // from before this (re)connect named a peer/world this client is not comparing
            // against any more.
            _saveSettle.Reset();
            RefineCompatibility();
        }
    }

    private void HandleWorldSnapshot(IPEndPoint from, ReadOnlySpan<byte> span, long now)
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
        // request is only disarmed once all four senders are quiet, because a forced resync
        // while our own ops are still in flight is exactly the overwrite the pending gates
        // below exist to prevent — so it waits its turn rather than being spent unused.
        bool forced = _resyncOnConnect;
        if (forced
            && _flagSender.PendingCount == 0
            && _itemSender.PendingCount == 0
            && _journalSender.PendingCount == 0
            && _lootSender.PendingCount == 0)
        {
            _resyncOnConnect = false;
        }

        // The version tripwire: 4 bytes per tick tells us whether our world flags still
        // match the host's, and the full set only crosses the wire when they do not.
        //
        // The DIGEST alongside it catches what the counter cannot: two stores that diverged
        // by the same number of local changes tie on version and would never trigger a
        // resync on that alone (docs/research/disconnect-reconnect.md §4.1). The digest
        // depends on content, not change count, so it still disagrees when the counter does
        // not.
        //
        // Gated on our own ops being acked, exactly like items and journal below and for the
        // same reason: until they land, the two sides are SUPPOSED to differ, and asking for
        // a snapshot in that window used to be how a client's own change got overwritten.
        if ((forced || snapshot.FlagVersion != Flags.Version || snapshot.FlagDigest != Flags.Digest)
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

        // Same tripwire (version + digest) for ground items, but only once our own ops have
        // all been acked: resyncing while a drop is still in flight would erase it locally,
        // respawn it on the next snapshot, and flicker the item in and out of the world.
        if ((forced || snapshot.ItemVersion != Items.Version || snapshot.ItemDigest != Items.Digest)
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

        // Journal tripwire (version + digest), gated on our own ops being acked for the same
        // reason as items: the merge itself is monotonic, but the version numbers only settle
        // once in-flight ops have landed.
        if ((forced
                || snapshot.JournalVersion != Journal.Version
                || snapshot.JournalDigest != Journal.Digest)
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

        // Container loot's own tripwire — NEW. Before this the channel had no version at all
        // (docs/research/disconnect-reconnect.md §4.8): a removal was announced once,
        // reliably, and never mentioned again, so a straggler that missed it under sustained
        // loss had nothing behind it but the retransmit itself. There is no LootSnapshotPacket
        // to ask for in reply — the host's "full resync" for this channel already IS
        // re-offering every removal through the reliable per-op sender
        // (CoopHost.ResyncContainerLoot), so the request just has to reach it.
        if ((forced || snapshot.LootVersion != _lootStore.Version || snapshot.LootDigest != _lootStore.Digest)
            && _lootSender.PendingCount == 0
            && now - _lastLootRequestMs >= LootResyncIntervalMs)
        {
            _lastLootRequestMs = now;
            int length = PacketCodec.Write(
                _replyBuffer,
                new LootSyncRequestPacket(_localPeerId, _lootStore.Version, _lootStore.Digest));
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _replyBuffer.AsSpan(0, length));
            }
        }
    }

    public void Dispose() => _channel.Dispose();

    private void HandleReject(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadReject(span) is { } reject)
        {
            _rejectReason = reject.Reason;
        }
    }

    private void HandlePong(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPong(span) is { } pong)
        {
            // Exponential moving average; a single sample is too jittery to show.
            double sample = now - pong.OriginalSentTimeMs;
            _roundTripMs = _roundTripMs == 0 ? sample : (_roundTripMs * 0.8) + (sample * 0.2);
        }
    }
}
