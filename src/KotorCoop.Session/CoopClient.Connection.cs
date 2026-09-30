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

        // Whatever the host last said about being away belongs to the session we just left;
        // a silence-declared loss (or the fresh Welcome that ends it) owns _hostLost from here.
        _hostAwayNow = false;
        _hostAwaySinceMs = 0;
        _hostLostByAway = false;

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
                SessionPassword.Hash(_settings.Password), _settings.Behaviour,
                _lineageSeed?.HostSessionId ?? 0, _lineageSeed?.HostLoadAtAgreement ?? 0,
                _lineageSeed?.LocalLoadAtAgreement ?? 0, _settings.DamageIncarnation));
        if (length > 0)
        {
            // DEBUG/TEST-ONLY (coop.ini debug_hello_version, default 0/off — see
            // SessionSettings.DebugHelloVersionOverride's own doc). Patches only the header's
            // version byte (magic[2] + version[1] + type[1], PacketWriter.WriteHeader's layout)
            // on THIS ONE datagram, so a rig that otherwise runs an identical build on both
            // sides can still exercise CoopHost.HandleVersionMismatch's typed E_PROTO Reject.
            // Nothing else about this Hello changes, and every packet sent after it — state
            // updates, pings, Bye — goes out through the ordinary PacketCodec.Write path
            // untouched.
            if (_settings.DebugHelloVersionOverride != 0)
            {
                _sendBuffer[2] = _settings.DebugHelloVersionOverride;
            }

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
            RestoreItemContinuation(welcome.SessionId, welcome.AssignedPeerId);
            _localPeerId = welcome.AssignedPeerId;
            _sessionPeerId = welcome.AssignedPeerId;
            if (_hostSessionId != welcome.SessionId)
            {
                lock (_gate)
                {
                    _hostWorld = WorldDigest.Empty;
                    _flagWorldRevision = 0;
                    Flags.Prune(KotorCoop.Memory.ObjectKeyedFlags.IsObjectKeyed);
                    _flagSender.Clear();
                    _livenessById.Clear();
                    _livenessHasGeneration = false;
                }
            }
            _hostSessionId = welcome.SessionId;
            _reconnecting = false;

            // The lineage seed only applies when this Welcome is provably the SAME host
            // process the seed was recorded against — a restarted host mints a new SessionId,
            // and a stale seed against a genuinely different host must never absorb a real
            // mismatch. Deliberately unconditional beyond that check: SaveLineage.Apply's own
            // load-edge guard re-verifies both LoadSerials the first time a real digest
            // comparison runs, so a seed whose checkpoint has gone stale in some other way is
            // simply never honoured rather than needing a second check here.
            if (_lineageSeed is { } seed && seed.HostSessionId == welcome.SessionId)
            {
                _saveLineage.SeedAgreement(seed.LocalLoadAtAgreement, seed.HostLoadAtAgreement);
            }

            // The host is back (or this is the first join, where it was already false). Either
            // way CoopAgent.PumpOnce reads this falling edge and resumes driving the game
            // normally — see HostLost's own doc comment for the full round trip.
            _hostLost = false;
            _hostLostByAway = false;

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
            _classGrantSender.ResetBackoff();
            _placedMineIntents.ResetBackoff();
            ResetPlacedMineRoster();

            // ...and ask for the full world back, rather than trusting three version counters
            // that an outage can leave equal-but-different. Each channel owns its force flag,
            // because only the item snapshot replaces local state and must wait for its own
            // pending operations.
            _forceFlagResync = true;
            _forceItemResync = true;
            _forceJournalResync = true;
            _forceLootResync = true;
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

            // Behaviour-flag negotiation (docs/roadmap-seamless.md Tier 1 item 7): compare
            // coop.ini/CLI flags against the host's own Welcome digest, warn on every
            // difference, and adopt the host's value for the channels it drives.
            ApplyBehaviourComparison(welcome.Behaviour);

            // Adopt the host's tick rate rather than blindly running our own — see
            // WelcomePacket.TickRate's doc for why the two settings can legitimately
            // disagree (a host that raised or lowered its rate; a client whose coop.ini
            // still names the old default). Clamped: a single wire byte is otherwise
            // unbounded, and 0 would divide by zero building the tick timer's period.
            int hostTickRate = Math.Clamp((int)welcome.TickRate, MinTickRate, MaxTickRate);
            if (hostTickRate != _settings.TickRate)
            {
                Console.Error.WriteLine(
                    $"[coop] adopting host tick rate {hostTickRate} Hz (local setting was "
                    + $"{_settings.TickRate} Hz)");
            }

            _effectiveTickRate = hostTickRate;

            // The buffer quantises inter-arrival gaps to the sender's send period, so it has
            // to learn the same rate the tick timer just adopted — it was built in the
            // constructor, before any host had announced one, against this install's own
            // coop.ini. Getting this wrong does not break playback (the rule's drift bound
            // re-pegs to raw arrivals), it just stops the regularisation helping. See
            // SnapshotTimingRule.
            _buffer.SendPeriodMs = 1000 / hostTickRate;
        }
    }

    /// <summary>Accumulates a tick's <see cref="WorldSnapshotPacket"/> page(s) as they arrive —
    /// see its own remarks for why a page's positions apply the instant it lands, independent
    /// of any sibling page of the same tick.</summary>
    private readonly WorldSnapshotAssembler _worldSnapshotAssembler = new();

    private void HandleWorldSnapshot(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadWorldSnapshot(span) is not { } snapshot)
        {
            _channel.NoteMalformed();
            return;
        }

        // This page's own entries are complete and ready to apply right now — never gated on
        // whether a sibling page of the same tick has (or has not) arrived. See
        // WorldSnapshotAssembler's and WorldSnapshotPacket's remarks.
        foreach (SnapshotEntry entry in _worldSnapshotAssembler.Apply(snapshot, _localPeerId))
        {
            if (entry.PeerId == _localPeerId)
            {
                continue;
            }

            // The host's own entry carries its AwayRule verdict — the raw wire flag, read here
            // rather than from the interpolated buffer so a parked host is seen the tick it says
            // so. Tick() turns a sustained reading into host loss (HostAwayRule).
            if (entry.PeerId == CoopHost.HostPeerId)
            {
                _hostAwayNow = entry.State.IsAway;
            }

            _buffer.Push(entry.PeerId, entry.Name, entry.State, now);
        }

        // Half-open link: the host has forgotten us but we can still hear it. See
        // _absentFromSnapshotSinceMs. Given a grace period rather than acted on immediately,
        // because a snapshot can also arrive in the window between our Welcome and the host's
        // next tick.
        if (_worldSnapshotAssembler.SelfListed)
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
        //
        // Only run once every page of THIS tick has been accounted for — pruning off a
        // partial picture would wrongly evict a peer whose entry simply lives on a page that
        // has not arrived yet. A page that never arrives (lost) just means this tick's prune
        // is skipped; the very next tick starts a fresh, independent cycle, so nothing stalls
        // waiting for it.
        if (_worldSnapshotAssembler.TickComplete)
        {
            _buffer.RetainOnly(_worldSnapshotAssembler.PresentPeerIds);
        }

        // The flag/item/journal/loot tripwires below ride on page 0 only — see
        // WorldSnapshotPacket's remarks — so a later page carries nothing meaningful in these
        // fields and none of the resync logic below runs for it. If page 0 itself is lost,
        // the check simply waits for next tick's fresh page 0.
        if (snapshot.PageIndex != 0)
        {
            return;
        }

        // A reconnection asks for everything once, regardless of what the versions say. Each
        // channel spends its own force flag when its request actually goes out, so unrelated
        // pending work cannot starve a channel's recovery.

        // The version tripwire: 4 bytes per tick tells us whether our world flags still
        // match the host's, and the full set only crosses the wire when they do not.
        //
        // The DIGEST alongside it catches what the counter cannot: two stores that diverged
        // by the same number of local changes tie on version and would never trigger a
        // resync on that alone (docs/research/disconnect-reconnect.md §4.1). The digest
        // depends on content, not change count, so it still disagrees when the counter does
        // not.
        //
        // Flags merge per entry by (Serial, OriginPeerId), so a snapshot cannot overwrite a
        // local operation that the host has not heard yet. Do not gate this request on our
        // own pending sender.
        if (WorldResyncRule.ShouldRequest(
                _forceFlagResync,
                snapshot.FlagVersion != Flags.Version || snapshot.FlagDigest != Flags.Digest,
                _flagSender.PendingCount > 0,
                snapshotReplacesLocal: false,
                now,
                _lastFlagRequestMs,
                FlagResyncIntervalMs))
        {
            _lastFlagRequestMs = now;
            _forceFlagResync = false;
            int length = PacketCodec.Write(_replyBuffer, new FlagSyncRequestPacket(_localPeerId, Flags.Version));
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _replyBuffer.AsSpan(0, length));
            }
        }

        // Ground item snapshots replace the local store, so this is the one channel that must
        // wait for its own pending operations before requesting.
        if (WorldResyncRule.ShouldRequest(
                _forceItemResync,
                snapshot.ItemVersion != Items.Version || snapshot.ItemDigest != Items.Digest,
                _itemSender.PendingCount > 0,
                snapshotReplacesLocal: true,
                now,
                _lastItemRequestMs,
                ItemResyncIntervalMs))
        {
            _lastItemRequestMs = now;
            _forceItemResync = false;
            int length = PacketCodec.Write(_replyBuffer, new ItemSyncRequestPacket(_localPeerId, Items.Version));
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _replyBuffer.AsSpan(0, length));
            }
        }

        // Journal entries merge by (Serial, OriginPeerId), so a request is safe while our own
        // operations are still in flight.
        if (WorldResyncRule.ShouldRequest(
                _forceJournalResync,
                snapshot.JournalVersion != Journal.Version || snapshot.JournalDigest != Journal.Digest,
                _journalSender.PendingCount > 0,
                snapshotReplacesLocal: false,
                now,
                _lastJournalRequestMs,
                JournalResyncIntervalMs))
        {
            _lastJournalRequestMs = now;
            _forceJournalResync = false;
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
        if (WorldResyncRule.ShouldRequest(
                _forceLootResync,
                snapshot.LootVersion != _lootStore.Version || snapshot.LootDigest != _lootStore.Digest,
                _lootSender.PendingCount > 0,
                snapshotReplacesLocal: false,
                now,
                _lastLootRequestMs,
                LootResyncIntervalMs))
        {
            _lastLootRequestMs = now;
            _forceLootResync = false;
            int length = PacketCodec.Write(
                _replyBuffer,
                new LootSyncRequestPacket(
                    _localPeerId, _lootStore.Version, _lootStore.Digest, _lootStore.Module));
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

    /// <summary>
    /// A Reject from a host running a DIFFERENT protocol version. The strict header check
    /// (<see cref="PacketCodec.PeekType"/>, what every ordinary handler dispatches on)
    /// already folded this datagram into <see cref="PacketType.None"/> — a host on a
    /// mismatched build wrote its Reject in ITS current wire format
    /// (<see cref="CoopHost.HandleVersionMismatch"/>), which our strict reader cannot parse
    /// any more reliably than genuine garbage. Recover just the type and version with
    /// <see cref="PacketCodec.PeekTypeAnyVersion"/> — NEVER the body, whose layout for a
    /// version we do not speak is not something we can trust — and surface the mismatch
    /// through the exact same field <see cref="HandleReject"/> uses, so it reaches the
    /// player the same way an ordinary Reject does (<see cref="GetStats"/>'s Status line).
    /// Returns false for anything else, so the caller falls through to the ordinary
    /// malformed-datagram handling.
    /// </summary>
    private bool TryHandleVersionMismatchReject(ReadOnlySpan<byte> span)
    {
        PacketType looseType = PacketCodec.PeekTypeAnyVersion(span, out byte hostVersion);
        if (looseType != PacketType.Reject || hostVersion == ProtocolConstants.Version)
        {
            return false;
        }

        _channel.NoteVersionMismatch();
        _rejectReason = $"protocol mismatch: host is v{hostVersion}, you are v{ProtocolConstants.Version}";
        return true;
    }

    /// <summary>
    /// The host announced a clean shutdown (<see cref="PacketType.HostBye"/>) — declare host
    /// loss immediately, subject to the same <see cref="SessionSettings.HostLossReleaseEnabled"/>
    /// gate the silence-based trigger uses, and drop back to the handshake right away rather
    /// than waiting out <see cref="SessionSettings.PeerTimeoutMs"/> of silence first. The
    /// reconnect itself is NOT gated on the toggle — a clean host restart is worth rejoining
    /// promptly either way, the toggle only decides whether locks release early.
    /// </summary>
    private void HandleHostBye(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadHostBye(span) is null)
        {
            _channel.NoteMalformed();
            return;
        }

        if (!IsConnected)
        {
            return;
        }

        if (_settings.HostLossReleaseEnabled)
        {
            _hostLost = true;
        }

        ForgetSession();
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
