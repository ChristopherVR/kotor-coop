using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost peer-connection lifecycle: the Hello handshake and reconnection (including
/// retired-identity reclaim), per-tick state updates from a peer, liveness pings, and
/// Bye/timeout departure. See CoopHost.cs for the class's core fields, the tick loop and
/// the packet-dispatch registry.
/// </summary>
public sealed partial class CoopHost
{
    /// <summary>
    /// Answers a Hello whose version byte does not match ours. Reached only through
    /// <see cref="Handle"/>'s lenient fallback (<see cref="PacketCodec.PeekTypeAnyVersion"/>),
    /// because the strict path a real Hello would otherwise take (<see cref="HandleHello"/>)
    /// never sees this datagram — <see cref="PacketCodec.PeekType"/> already folded it into
    /// <see cref="PacketType.None"/>. The Reject is written in OUR current wire format
    /// (<see cref="ProtocolConstants.Version"/>); an older client cannot strictly parse it
    /// either, which is exactly why <see cref="CoopClient"/>'s own Handle has the matching
    /// lenient fallback — see its remarks.
    /// </summary>
    private void HandleVersionMismatch(IPEndPoint from, byte peerVersion)
    {
        _channel.NoteVersionMismatch();

        // The host's own side of this: before 2026-09-18 the joiner got a typed Reject and the
        // host printed nothing (status-engineering.md's E_PROTO "known gap"). Queued, not
        // announced here — the agent drains it through the same trio W_CFG lines use.
        _rejectedJoins.NoteVersionMismatch(from, peerVersion, ProtocolConstants.Version, SessionClock.NowMs);

        int rejectLength = PacketCodec.Write(
            _replyBuffer,
            new RejectPacket($"E_PROTO: host is v{ProtocolConstants.Version}, you are v{peerVersion}"));
        if (rejectLength > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, rejectLength));
        }
    }

    /// <summary>
    /// One best-effort datagram to every peer still on the roster — see
    /// <see cref="PacketType.HostBye"/>'s own doc comment. Called once, after the tick loop has
    /// already stopped (<see cref="RunAsync"/>), so there is no concurrent <see cref="Tick"/>
    /// contending for <see cref="_sendBuffer"/> and nothing to gain from retrying: a client
    /// that misses this still notices the silence within <see cref="SessionSettings.PeerTimeoutMs"/>
    /// regardless.
    /// </summary>
    private void BroadcastHostBye()
    {
        List<PeerConnection> recipients;
        lock (_gate)
        {
            recipients = [.. _peers.Values];
        }

        if (recipients.Count == 0)
        {
            return;
        }

        int length = PacketCodec.Write(_sendBuffer, new HostByePacket(_sessionId));
        if (length == 0)
        {
            return;
        }

        foreach (PeerConnection peer in recipients)
        {
            _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
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
            _rejectedJoins.NoteWrongPassword(from, now);

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
        ContentCompatibility.RequireIdenticalSave = _settings.RequireIdenticalSave;
        CompatibilityVerdict verdict = ContentCompatibility.Compare(_settings.Digest, hello.Digest);
        _compatibility = verdict;

        // Behaviour-flag negotiation (docs/roadmap-seamless.md Tier 1 item 7): unlike content,
        // never refuses or degrades — a peer's coop.ini/CLI differing from this host's is
        // never a reason to reject the join. Computed here (rather than only once the peer is
        // admitted) so a rejected Hello still leaves a comparison to look at on the dashboard.
        _behaviourComparison = BehaviourCompatibility.Compare(_settings.Behaviour, hello.Behaviour);

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
        bool freshlyAdmitted = false;

        lock (_gate)
        {
            // A nonce is the identity. Matching a new nonce by source endpoint can hand a
            // stranger the departed player's id and all cumulative ledgers keyed by it.
            PeerConnection? existing = _peers.Values.FirstOrDefault(p => p.Nonce == hello.ClientNonce);

            if (existing is not null)
            {
                if (!_enemyDamageGenerations.Bind(existing.Id, hello.DamageIncarnation)
                    || !_companionDamageGenerations.Bind(existing.Id, hello.DamageIncarnation)) return;
                existing.DamageIncarnation = hello.DamageIncarnation;
                existing.LastSeenMs = now;
                existing.EndPoint = from;
                existing.InstallVerdict = verdict;
                assignedId = existing.Id;
            }
            else
            {
                // A new nonce on an endpoint still held by a live peer proves that endpoint
                // was reissued. Retire the old peer immediately instead of keeping a ghost
                // until the normal timeout; the old nonce remains reclaimable.
                if (_peers.Values.FirstOrDefault(p => p.EndPoint.Equals(from)) is { } replaced)
                {
                    ForgetPeer(replaced.Id, now);
                }

                if (_peers.Count + 1 >= ProtocolConstants.MaxPeers)
                {
                    int rejectLength = PacketCodec.Write(_replyBuffer, new RejectPacket("session full"));
                    if (rejectLength > 0)
                    {
                        _channel.Send(from, _replyBuffer.AsSpan(0, rejectLength));
                    }

                    return;
                }

                // ⚠ A blackout longer than the peer timeout expires the slot, so by the time
                // the link is back this is a peer with no live entry — and it MUST NOT be
                // dealt a fresh id. Reclaim the retired identity if this nonce ever held one.
                string name;
                if (_retired.Remove(hello.ClientNonce, out RetiredPeer? previous))
                {
                    assignedId = previous.Id;

                    // The id must be the old one; the NAME should be whatever they are
                    // called now, falling back to what they were called before.
                    name = string.IsNullOrWhiteSpace(hello.PlayerName) ? previous.Name : hello.PlayerName;
                }
                else
                {
                    assignedId = _nextPeerId++;
                    name = string.IsNullOrWhiteSpace(hello.PlayerName) ? $"peer{assignedId}" : hello.PlayerName;
                }

                if (!_enemyDamageGenerations.Bind(assignedId, hello.DamageIncarnation)
                    || !_companionDamageGenerations.Bind(assignedId, hello.DamageIncarnation)) return;
                PeerConnection created = new()
                {
                    Id = assignedId,
                    EndPoint = from,
                    Name = name,
                    Nonce = hello.ClientNonce,
                    DamageIncarnation = hello.DamageIncarnation,
                    LastSeenMs = now,
                    InstallVerdict = verdict,
                    PartyPoolLoadQuarantined = _hasObservedHostLoad,
                    PartyPoolReleaseWhenReady = _hasObservedHostLoad,
                };
                _peers[assignedId] = created;

                // Version 67 (docs/research/session-save.md): a bare disconnect/reconnect
                // that carried no load edge either side is the SAME lineage this host already
                // judged agreed, not a fresh "never agreed" start — see SaveLineage's own doc
                // for why a fresh instance otherwise re-honours a save-only mismatch it
                // already knows is harmless. Trusted only when this is PROVABLY the same live
                // host process (the client's remembered session id still matches _sessionId —
                // a restarted host mints a new one) and this host's own world has not moved
                // since (LoadSerial unchanged); the CLIENT's own LoadSerial is re-checked the
                // ordinary way, by SaveLineage.Apply's load-edge guard, the first time a real
                // digest comparison runs.
                if (hello.LineageHostSessionId != 0 && hello.LineageHostSessionId == _sessionId
                    && _localWorld.LoadSerial == hello.LineageHostLoad)
                {
                    created.SaveLineage.SeedAgreement(hello.LineageHostLoad, hello.LineageClientLoad);
                }

                freshlyAdmitted = true;
            }
        }

        if (freshlyAdmitted)
        {
            // A peer that has been away long enough to lose its live entry has also been away
            // long enough to miss container-loot removals — the one world channel with no
            // version tripwire to notice a gap, because a removal is announced once, reliably,
            // and then never mentioned again. Re-offer everything known so the chests it slept
            // through are empty in its copy too. Enqueue dedups by (container, item) against
            // ops already in flight, and every recipient dedups on the pair regardless of who
            // announced it, so re-offering costs the peers that already have them nothing.
            ResyncContainerLoot(now);

            // Anything still unacked backed off while there was nobody to send it to; a peer
            // arriving is the moment to try again rather than 4 seconds later.
            _lootSender.ResetBackoff();

            // Same reasoning, for materialised-enemy death loot. Item 11(b) closed this pass:
            // ResyncMaterialisedLoot re-offers the bounded history alongside the ordinary
            // in-flight resend, so an op that had already fully retired before this peer
            // reconnected is not lost the way it used to be — see that method's own doc.
            ResyncMaterialisedLoot(now);
            _materialisedLootSender.ResetBackoff();

            // Same speed-up, same narrow gap: a dialogue grant still in flight when this peer
            // reconnects goes out again immediately rather than waiting out a backoff, but one
            // already fully retired before it came back is not resent.
            _dialogueGrantSender.ResetBackoff();
            _classGrantSender.ResetBackoff();

            // A Jedi choice that already fully retired (every peer present at the time acked
            // it) before this peer connected would otherwise never reach them — the same
            // "announced once, never mentioned again" gap ResyncContainerLoot's own remarks
            // describe. ResyncJediChoice re-announces it as a brand-new, unacked op.
            ResyncJediChoice(now);
            _placedMineRosterSentMs = 0; // the (re)admitted peer needs the mine roster now

            // Same reasoning again, for the shared party item pool: a peer away long enough to
            // lose its live entry has also missed however many add/remove ops happened while
            // it was gone, and this channel — like container loot — has no version tripwire of
            // its own to notice the gap. ResyncPartyPool re-offers every stack as an ABSOLUTE
            // fact (PartyPoolOpKind.Set), not a replay of history, so it is safe even if this
            // peer's own store still holds a stale nonzero total for some key.
            ResyncPartyPool(now);
            _partyPoolSender.ResetBackoff();

            // Same reasoning again, for merchant store stock: a peer away long enough to lose
            // its live entry has also missed however many buy/sell ops happened while it was
            // gone. ResyncStore re-offers every (store, stack) entry as an ABSOLUTE fact
            // (StoreOpKind.Set), safe even if this peer's own store still holds a stale total.
            ResyncStore(now);
            _storeSender.ResetBackoff();

            // A returning client's FIRST liveness packet is a full snapshot (every cycle
            // already is one), so all this needs is to not make them wait out the ordinary
            // 2-second interval for it — see BroadcastCreatureLiveness.
            _forceLivenessBroadcast = true;

            // v2 (party_pool_sync=2/store_sync=2): a rejoining/newly-admitted peer's very first
            // snapshot cycle IS the complete resync, by construction — see
            // docs/research/shared-inventory-v2.md §2.7. Forcing it out immediately, exactly
            // like the liveness channel above, avoids waiting out the ordinary broadcast
            // interval for it.
            _forcePartyPoolV2Broadcast = true;
            _forceStoreV2Broadcast = true;
        }

        WelcomePacket welcome = new(
            assignedId, _sessionId, (byte)_settings.TickRate, ProtocolConstants.MaxPeers, _settings.Digest,
            _settings.Behaviour);
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

    private void HandlePing(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPing(span) is not { } ping)
        {
            _channel.NoteMalformed();
            return;
        }

        // ⚠ ONLY A KNOWN PEER GETS AN ANSWER — the same rule every other peer-keyed handler
        // applies, and here it is what lets a forgotten client find out that it has been
        // forgotten. A client's whole liveness signal is "am I hearing anything", and after
        // the host expires a peer it sends that peer nothing: no snapshot, no roster, no
        // dialogue token. Except a Pong. Answering a stranger's ping refreshed the ghost's
        // clock once a second forever, so a client whose upstream had failed and recovered
        // sat there fully connected in its own opinion, streaming an avatar under a peer id
        // the host discards, visible to nobody. Silence is the message.
        lock (_gate)
        {
            if (!_peers.ContainsKey(ping.PeerId))
            {
                return;
            }
        }

        int length = PacketCodec.Write(_replyBuffer, new PongPacket(ping.SentTimeMs, now));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    /// <summary>
    /// Drops one peer from the roster and from everything keyed on its id, retiring its
    /// identity first so a return is a rejoin rather than a stranger arriving. Caller holds
    /// <see cref="_gate"/>.
    ///
    /// One helper rather than two hand-written lists: the timeout path and the
    /// stale-endpoint path in <see cref="HandleHello"/> must forget exactly the same set, and
    /// a per-field teardown written twice is a per-field teardown that will eventually miss a
    /// field.
    /// </summary>
    private void ForgetPeer(uint id, long nowMs)
    {
        // Retire before removing: the identity has to survive the connection, or the same
        // player comes back as a stranger (see RetiredPeer).
        if (_peers.TryGetValue(id, out PeerConnection? gone))
        {
            Retire(gone, nowMs);
        }

        _peers.Remove(id);
        _buffer.Remove(id);
        ReleaseDialogueIfOwnedBy(id);
        ForgetPhantomDamageFor(id);
        _encounterTriggers.Forget(id);
        _triggerRelay.Forget(id);
        _interactionLocks.Forget(id);
    }

    /// <summary>
    /// Remembers a departed peer's identity against its nonce. Caller holds <see cref="_gate"/>.
    ///
    /// The oldest is dropped once the table is full, which is the only way an identity is ever
    /// forgotten — a peer whose identity has aged out that far comes back as a new player, and
    /// the cumulative channels treat it as one (a new peer id is first-sight everywhere, not a
    /// continuation), so the failure mode of the bound is "loses a contribution", never
    /// "counts one twice".
    /// </summary>
    private void Retire(PeerConnection peer, long nowMs)
    {
        _retired[peer.Nonce] = new RetiredPeer(peer.Id, peer.Name, nowMs);

        while (_retired.Count > MaxRetiredPeers)
        {
            uint oldest = 0;
            long oldestAt = long.MaxValue;
            foreach ((uint nonce, RetiredPeer retired) in _retired)
            {
                if (retired.RetiredAtMs < oldestAt)
                {
                    oldestAt = retired.RetiredAtMs;
                    oldest = nonce;
                }
            }

            _retired.Remove(oldest);
        }
    }

    private void HandleBye(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadBye(span) is not { } bye)
        {
            return;
        }

        lock (_gate)
        {
            // ⚠ A POLITE DEPARTURE RETIRES THE IDENTITY EXACTLY LIKE A TIMEOUT DOES. This was
            // contested — the argument against is that the session has announced "so-and-so
            // left" and everyone tore down accordingly, so resurrecting the id resurrects
            // state that was deliberately discarded. That premise does not hold here, and the
            // binary says why.
            //
            // WHAT A DEPARTING PLAYER ACTUALLY TEARS DOWN is on THEIR machine:
            // SessionManager.Disconnect disposes their session and calls ClearCoopState. On
            // THIS host nothing keyed on their peer id is discarded by the announcement — and
            // two of the tables that matter are never discarded at all, by design:
            // _phantomDamageIn (rows are kept deliberately; dropping one walks a monotonic
            // total backwards) and CoopAgent's PeerDamageLedger, which is scoped to the MODULE
            // and knows nothing about sessions.
            //
            // AND THE DEPARTING PLAYER'S OWN COUNTERS SURVIVE TOO. The running total of damage
            // they have dealt lives in the plugin's DamageDealtTable, and ClearCoopState does
            // not touch it (it clears the presence counts, the dialogue flag and the encounter
            // request — nothing damage-related). The only thing that clears it is
            // PluginHost.InvalidateAll, which fires on world unload, module change and a
            // flushed transition — never on a disconnect. Its GENERATION is bumped in the same
            // place, so it does not move either.
            //
            // So a player who quits to the menu and rejoins in the same module reports the SAME
            // cumulative total under the SAME generation. Deal them a fresh peer id and the
            // ledger finds no prior row for it, treats all of it as new, and hands the enemy
            // that player's entire historic contribution a second time — with rule 3 unable to
            // help, because the generation is exactly what did not change. The third-party
            // phantom-damage tables double in the same way, and there the victim is a player's
            // own hit points.
            //
            // The invariant is therefore not "clean exit versus dropped link". It is: AN
            // IDENTITY MUST LIVE AS LONG AS THE COUNTERS KEYED ON IT, and those are scoped to
            // the module, not to the connection. When a departure really does reset them — a
            // module change, a restarted agent, a new game — the reset is already announced by
            // the generation/epoch machinery, and the returning player carries a NEW nonce
            // anyway (a fresh CoopClient mints one), so nothing is reclaimed and every
            // cumulative channel correctly treats them as first sight. Which is also why this
            // line is nearly unreachable in production: only a client object that SURVIVED can
            // present a nonce that has said Bye, and that is precisely the client whose
            // counters survived with it. See ByeIdentityTests.
            if (_peers.TryGetValue(bye.PeerId, out PeerConnection? leaving))
            {
                Retire(leaving, SessionClock.NowMs);
            }

            _peers.Remove(bye.PeerId);
            ReleaseDialogueIfOwnedBy(bye.PeerId);
            ForgetPhantomDamageFor(bye.PeerId);
        }

        // Outside the lock: the store has its own. A departing peer must not keep an NPC
        // held — the lease would expire it anyway, but a player should not have to wait out
        // a timeout to talk to someone the last player left mid-sentence.
        _interactionLocks.Forget(bye.PeerId);

        _buffer.Remove(bye.PeerId);
        _encounterTriggers.Forget(bye.PeerId);
        _triggerRelay.Forget(bye.PeerId);
    }
}
