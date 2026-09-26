namespace KotorCoop.Protocol;

public static class ProtocolConstants
{
    /// <summary>Two byte magic ('K','C') so stray traffic on the port is discarded cheaply.</summary>
    public const ushort Magic = 0x4B43;

    /// <summary>
    /// Bumped on any wire-format change. Mismatched peers are rejected at Hello. One line per
    /// version below — see the cited docs/research/*.md file for the full rationale behind any
    /// entry that has one; the rest are self-contained.
    ///
    /// Version 3: PlayerState flags dword, journal channel, client enemy reports.
    /// Version 4: content digest on Hello/Welcome for mod-compatibility handshaking.
    /// Version 5: session password hash on Hello; party-member snapshot channel.
    /// Version 6: dialogue ownership token (snapshot + request channels).
    /// Version 7: text chat channel.
    /// Version 8: shared progression (party gold + PC experience).
    /// Version 9: world digest carries save identity + per-module GIT digest.
    /// Version 10: dialogue token carries the partner tag and the owner's reply picks, so a
    /// spectator runs the same conversation in the game's own UI instead of reading a
    /// transcript.
    /// Version 11: dialogue token carries the owner's full pick LOG so a late joiner catches up.
    /// Version 12: enemy snapshot carries presence (position, facing, animation, one-shots) so
    /// a mirroring instance drives its copies to match the owner's fight.
    /// Version 13: enemy snapshot includes PLOT (cutscene) creatures, presence-only.
    /// Version 14: enemy snapshot carries the MODULE its ids were read in, so a peer elsewhere
    /// drops it instead of misapplying it to local creatures sharing those ids.
    /// Version 15: peer slots are STICKY per peer id, and a relayed phantom-damage report
    /// carries the true ORIGINATOR's peer id rather than the relaying host's.
    /// Version 16: EncounterTrigger — a client reports the encounters it stands inside; the
    /// host's engine (never the client's, whose own encounters stay disarmed) does the spawn.
    /// Version 17: the peer direction reports DAMAGE DEALT (EnemyDamage 33) instead of sampled
    /// health, which the owner's own mirror could overwrite before it was ever read.
    /// Version 18: EnemyState carries appearance/faction so a mirror can MATERIALISE an enemy
    /// that exists only in the owner's instance (an encounter or script spawn).
    /// Version 19: Bark (34) — one-line NPC bubbles, which never touch the dialogue system.
    /// Version 20: InteractionClaim (35) / InteractionLock (36) — a per-object LEASE the
    /// object's owner honours, so a client's conversation with a host-owned NPC stops the
    /// host's own AI from walking it away mid-scene.
    /// Version 21: DialogueState carries the owner's NODE index; a follower is driven onto that
    /// entry rather than replaying the reply SLOT, which two instances' independent scripts can
    /// resolve to different branches.
    /// Version 22: PartyRoster (37) — the host's nine companion slots as two masks, so a
    /// recruit/dismiss conversation on one instance keeps both rosters in sync.
    /// Version 23: ProgressionReport (38) — the peer's contribution: gold as a cumulative
    /// earned/spent LEDGER (a balance cannot be merged), XP as an absolute merged by max (both
    /// engines already grant kill XP independently, so a delta would double-count).
    /// Version 24: a ground item carries per-instance IDENTITY (stack/charges/upgrades/flags)
    /// instead of respawning as a pristine blueprint copy on pickup.
    /// Version 25: TransitionMovies (40) — pre-announces a transition's movie queue so a
    /// follower's own (otherwise-empty) queue plays them too.
    /// Version 26: PlayerState carries FlagInMinigame so a peer's instance parks the phantom and
    /// stops applying forwarded damage during a swoop race/turret sequence. See
    /// docs/research/minigames.md.
    /// Version 27: the world snapshot carries a content DIGEST per store (flag/item/journal/
    /// loot) beside its version counter, catching a same-count-but-different-content tie a bare
    /// counter misses. See docs/research/disconnect-reconnect.md §4.1/§4.8.
    /// Version 28: PeerEffect (44) — non-damage combat effects on a phantom, forwarded to the
    /// real character. Off by default until live-verified. See docs/research/effect-sync.md.
    /// Version 29: DialogueGrant (45) / DialogueGrantAck (46) — relays a reward node's item/
    /// companion grant to the spectating side, whose own HandleReply never ran. See
    /// docs/research/dialogue-sync.md.
    /// Version 30: CreatureLiveness (47) — every creature the host knows about, not just the
    /// combat census, so a mirror can reconcile a body left alive on one side and gone on the
    /// other. Off by default until live-verified. See docs/research/creature-liveness.md.
    /// Version 31: flag/journal ops carry a per-key Lamport Serial (plus a journal Removed bit)
    /// so a legitimate DECREASE is no longer refused forever by max-merge. See
    /// docs/research/quest-sync.md.
    /// Version 32: TriggerRelay (48) — a client reports the generic triggers it stands in; the
    /// host fires them so its own object-id counter stays in lockstep. Off by default until
    /// live-verified. See docs/research/trigger-relay.md.
    /// Version 33: TransitionAnnounce (49) — destination + waypoint announced before a
    /// transition's load starts, so a follower's own warp overlaps instead of trailing. See
    /// docs/research/transition-smoothness.md.
    /// Version 34: MidModuleMovie (50) — a script's own <c>PlayMovie</c> with no transition,
    /// relayed the same way TransitionMovies is. Off by default until live-verified. See
    /// docs/research/trigger-relay.md §6.
    /// Version 35: WorldDigestPacket gains LoadSerial, so a receiver can classify a peer's
    /// SaveIdentity change as a save or a load. Off by default until live-verified. See
    /// docs/research/session-save.md.
    /// Version 36: <see cref="TriggerRelayPacket"/> gains <c>NativeFiredIds</c>/
    /// <c>NativeFiredOriginPeerIds</c> — the reverse direction, so the HOST's own native trigger
    /// fires reach clients too, not just a client's presence reaching the host. See
    /// docs/research/trigger-relay.md.
    /// Version 37: <see cref="InteractionKind.Container"/> — a container/corpse joins the
    /// interaction lock, keyed by <c>TargetId</c> rather than <c>Tag</c> since placeables repeat
    /// tags across instances. See docs/research/container-sync.md.
    /// Version 38: <see cref="DialogueState"/> and <see cref="DialogueRequestPacket"/> gain
    /// <c>Forced</c>, so <c>dialogue_auto_join</c> can tell an engine-forced conversation (an
    /// ambush, a scripted cutscene) from an ordinary player-clicked chat. See
    /// docs/research/dialogue-sync.md's Item 1.
    /// Version 39: <see cref="PlayerState.FlagAway"/> — set on a player's last known state once
    /// <c>CoopAgent</c> has failed to read them for a sustained streak (main menu, load screen,
    /// character screen), so a peer's instance parks their phantom instead of leaving a frozen
    /// statue behind and the session can announce "away"/"back". Rides the existing
    /// <c>Flags</c> dword, so nothing in <see cref="PacketCodec"/> changes shape. See
    /// docs/research/disconnect-reconnect.md.
    /// Version 40: <see cref="HelloPacket"/>/<see cref="WelcomePacket"/> gain a <see
    /// cref="BehaviourDigest"/> — coop.ini/CLI behaviour flags (local_combat, party_sync,
    /// follow_lead, interp_ms and 16 more) are now negotiated the way game content already
    /// is: a difference always warns (<c>W_CFG</c>); a difference on a host-authoritative
    /// channel additionally makes the client adopt the host's value at runtime. See
    /// docs/research/mod-compatibility.md's "Behaviour-flag negotiation" section and
    /// docs/roadmap-seamless.md Tier 1 item 7.
    ///
    /// Version 41: companion-state sync — <see cref="PacketType.CompanionXpSnapshot"/> (51) /
    /// <see cref="PacketType.CompanionXpReport"/> (52) are new packet types, and
    /// <see cref="PartyMemberState"/> gained three equip fields on the existing
    /// <see cref="PacketType.PartySnapshot"/> shape. See docs/research/companion-state-sync.md.
    ///
    /// Version 42: <see cref="PacketType.PartyPoolOp"/> (53) / <see cref="PacketType.PartyPoolAck"/>
    /// (54) / <see cref="PacketType.PartyPoolSyncRequest"/> (55) — the shared party item-pool
    /// channel (<c>party_pool_sync</c>, default OFF, NEEDS-LIVE). See docs/research/party-pool-sync.md.
    ///
    /// Version 43: <see cref="WorldSnapshotPacket"/> gains <c>PageIndex</c>/<c>PageCount</c> —
    /// a full lobby's aggregate entry size that would overrun <see cref="MaxPacketSize"/> now
    /// degrades to several datagrams per tick (graceful pagination, like
    /// <see cref="PacketType.CreatureLiveness"/> already does for its own wider publish)
    /// instead of being silently dropped whole; the flag/item/journal/loot version+digest
    /// tripwires ride on page 0 only, so a later page costs fewer bytes. See
    /// docs/research/lag-audit.md's 2026-08-27 entry.
    ///
    /// Version 44: container-lock explicit arbitration — <see cref="PacketType.LockResponse"/> (56)
    /// is new and <c>InteractionClaimPacket</c> gained <c>RequestSerial</c>. See
    /// docs/research/container-sync.md.
    ///
    /// Version 45: <see cref="PacketType.StoreOp"/> (57) / <see cref="PacketType.StoreAck"/> (58) /
    /// <see cref="PacketType.StoreSyncRequest"/> (59) — the merchant/store stock channel
    /// (<c>store_sync</c>, default OFF, NEEDS-LIVE). See docs/research/store-sync.md.
    ///
    /// Version 46: <see cref="PacketType.PartyPoolSnapshotV2"/>
    /// (60) / <see cref="PacketType.PartyPoolIntentV2"/> (61) / <see cref="PacketType.PartyPoolIntentAckV2"/>
    /// (62) / <see cref="PacketType.StoreSnapshotV2"/> (63) / <see cref="PacketType.StoreIntentV2"/> (64) /
    /// <see cref="PacketType.StoreIntentAckV2"/> (65) — the host-authoritative "absolute snapshot +
    /// client intents" redesign of the party pool and store channels
    /// (<c>party_pool_sync=2</c>/<c>store_sync=2</c>, default OFF, NEEDS-LIVE — mode 1, the
    /// existing op-log, is unchanged). See docs/research/shared-inventory-v2.md.
    ///
    /// Version 47: <see cref="PacketType.HostBye"/> (70) — the host's clean-shutdown broadcast
    /// that lets clients release every lock immediately instead of waiting out the silence
    /// timeout (<c>host_loss_release</c>). Also the flag snapshot became paged (same packet
    /// types, but a v46 peer would misread a paged snapshot). See
    /// docs/research/disconnect-reconnect.md §11 and world-adoption.md (2026-08-29).
    ///
    /// Version 48: <see cref="PhantomDamagePacket"/> gains <c>Attackers</c> — the same
    /// cumulative total split per attacker id, up to <see cref="MaxPhantomAttackerEntries"/>
    /// entries, instead of just the single last-attacker id. No new packet type; the existing
    /// <c>PhantomDamage</c> (31) is extended. See docs/research/peer-damage-ledger.md §7.
    ///
    /// Version 49: <see cref="PacketType.CompanionDamage"/> (71) — a companion
    /// <c>companion_local_combat</c> has released to fight on a client's own engine forwards
    /// its cumulative damage TAKEN to the host, which spends it as real damage on its own
    /// authoritative body. The companion sibling of <see cref="PacketType.EnemyDamage"/>'s
    /// damage-DEALT channel, and the same "cumulative total, never a health sample" shape for
    /// the identical reason (docs/research/peer-damage-ledger.md). <c>companion_damage_sync</c>,
    /// default ON. See docs/research/companion-state-sync.md §13. (48 and 49 landed together
    /// from parallel branches in the 2026-09-02 combat wave.)
    ///
    /// Version 50: <see cref="PlayerState.FlagPartyWiped"/> — a new bit on the existing flags
    /// dword (no packet shape changed), bumped anyway because it changes what a wipe MEANS:
    /// the session now releases the game-over screen only when every participant's WHOLE
    /// PARTY is down, not merely every player character. A v49 peer never publishes the bit,
    /// so a mixed session would hold the suppression forever. See
    /// docs/research/death-handling.md §6.4.
    ///
    /// Version 51: <see cref="PacketType.GalaxyDeparture"/> (72) — the galaxy-map departure
    /// relay. A leader's accept click on the galaxy map is published at the click (the one
    /// moment the frozen interplanetary leg leaves observable) and the follower replays the
    /// same scripted leg on its own engine, arriving in the same fresh Ebon Hawk natively.
    /// Closes docs/limitations.md #15. See docs/research/galaxy-map-and-party-select.md's
    /// 2026-09-03 relay addendum.
    ///
    /// Version 52: <see cref="PacketType.WorldTransferRequest"/> (73) /
    /// <see cref="PacketType.WorldTransferHeader"/> (74) / <see cref="PacketType.WorldTransferChunk"/>
    /// (75) / <see cref="PacketType.WorldTransferChunkStatus"/> (76) /
    /// <see cref="PacketType.WorldTransferComplete"/> (77) — a UDP-only fallback for
    /// <c>auto_adopt_world</c>'s world-transfer, multiplexed onto the ordinary session socket.
    /// Found live 2026-09-10: a UDP-only tunnel/NAT path (playit.gg, in this case) forwards the
    /// session's own UDP traffic fine but silently drops <see cref="KotorCoop.Session.WorldTransfer"/>'s
    /// separate TCP listener, so a joiner whose save did not match the host's could connect but
    /// could never adopt the host's world over such a path — <c>WorldAdoption</c> now probes TCP
    /// with a short timeout first and falls back to this channel only when that probe fails, so
    /// the existing TCP path (faster, simpler, no new failure modes of its own) is unchanged for
    /// everyone it already works for. Off by default (<c>world_transfer_udp_fallback</c>),
    /// NEEDS-LIVE. See docs/research/network-topology.md's UDP world-transfer section.
    ///
    /// Version 53: <c>FlagId</c> widened from 32 to 64 bits in <see cref="FlagOpPacket"/> and
    /// <see cref="FlagSnapshotEntry"/> (+4 bytes per op, +4 per snapshot entry —
    /// <see cref="MaxFlagEntriesPerPacket"/> drops from 106 to 81). Door and placeable keys used
    /// to fold the module name to ONE byte beside a 16-bit object id; object ids restart on every
    /// module load, so over a ~60-module playthrough two modules sharing that byte (a near
    /// certainty) with a reused low id shared one store entry, and the earlier module's "open /
    /// unlocked / searched" opened, unlocked or emptied an unrelated door or chest in the later
    /// one. The key now carries the full 32-bit module-name hash and a 24-bit object id under
    /// the type tag (<c>KotorCoop.Memory.ObjectKeyedFlags</c>); global-variable ids are unchanged
    /// in the low 32 bits. No migration: the store is per-session memory, and an older peer is
    /// refused at Hello (<c>E_PROTO</c>). docs/review-seamless-2026-09-18.md §2, roadmap #47.
    ///
    /// Version 54: <see cref="PlayerState.FlagStealth"/> — a new bit on the existing flags
    /// dword (no packet shape changed), sampled from the creature's own runtime stealth-mode
    /// byte. A peer's instance holds a stealthed player's stand-in out of its enemies' target
    /// selection while the bit is set (<c>stealth_sync</c>, default ON). A v52 peer never
    /// publishes the bit, so a mixed session keeps today's behaviour; bumped so the changelog
    /// names it, like 50. The same wave (2026-09-18) added four <see cref="PeerEffectKind"/>
    /// values (Fear 13, Stunned 14, Poison 15, Disguise 16) — a wider use of the existing
    /// <c>Kind</c> byte, no shape change, no bump of its own. See
    /// docs/research/effect-sync.md §12.
    ///
    /// Version 55: <see cref="PacketType.ClassGrant"/> (78) / <see cref="PacketType.ClassGrantAck"/>
    /// (79) — the class-grant relay. A script's <c>AddMultiClass</c> on one player's character
    /// (Dorak's Jedi path choice, Dantooine) is captured at the VM handler, relayed reliably
    /// under the origin's true (origin, sequence) identity, and applied to every other player's
    /// character through the same engine handler, so the joiner becomes a Jedi too. Off by
    /// default (<c>class_grant_sync</c>), NEEDS-LIVE. Closes docs/review-seamless-2026-09-18.md
    /// blocker #1 statically. See docs/research/class-and-power-grants.md.
    ///
    /// Version 56: <see cref="BehaviourDigest"/> scheme 2 — the four player-count scaling
    /// SETTINGS (<c>enemy_health</c>, <c>enemy_damage</c>, <c>xp_rate</c>, <c>store_stock</c>;
    /// int32 each, 0 = auto) ride Hello/Welcome as host-authoritative keys, so a client adopts
    /// the host's setting and both sides resolve the same percent from the same player count.
    /// No new packet type. See docs/research/player-count-scaling.md.
    ///
    /// Version 58 (57 is another stream's): <see cref="PacketType.EnemyEffect"/> (84) /
    /// <see cref="PacketType.EnemyEffectAck"/> (85) — the client-cast enemy-effect relay. A
    /// non-damage effect (Stasis, Stun, Fear, Whirlwind, Force Push, Slow, Paralysis, Sleep, a
    /// grenade rider) a CLIENT casts on a mirrored enemy is captured at the engine's effect
    /// dispatcher and sent reliably to the host, which replays it on the enemy's real body
    /// (<see cref="EnemyEffectRules"/> names the forwardable subset of
    /// <see cref="PeerEffectKind"/>). Client to host only; the other clients see it through the
    /// ordinary enemy census. Off by default (<c>enemy_effect_sync</c>), NEEDS-LIVE. See
    /// docs/research/enemy-effects.md.
    ///
    /// Version 59: container and materialised-loot packets carry the authoritative module
    /// name. A receiver refuses an op for another module, so an old retransmit cannot delete
    /// an object whose id was reused after a transition. Container-loot history/digests are
    /// full per-module sets while the game bridge uses an acknowledged, fair 24-slot page
    /// mailbox. See docs/review-seamless-2026-09-18.md (2026-09-21 recovery pass).
    ///
    /// Version 60: saved-world GIT digests canonicalize explicit object IDs, preserving each
    /// ID/template association while ignoring runtime save list order. Pristine placement
    /// order remains significant. Older peers use a different digest meaning, so reject that
    /// mixed session at the version handshake instead of reporting a false content mismatch.
    ///
    /// Version 61: save identity uses LASTMODULE to select the active module's stored state,
    /// rather than the first visited module in the archive. Mixed identity schemes are refused.
    ///
    /// Version 62: WorldDigest appends the sampled runtime difficulty level for warning-only
    /// peer agreement. Older peers are refused at the protocol handshake rather than
    /// misreading the new field.
    ///
    /// Version 66 (63-65 are other streams' reservations): <see cref="PacketType.PlacedMineIntent"/>
    /// (100), <see cref="PacketType.PlacedMineIntentAck"/> (101) and
    /// <see cref="PacketType.PlacedMineRoster"/> (102) — party-placed floor-mine replication.
    /// A client reports "I placed a mine" / "a replicated mine is gone here" as reliable intents;
    /// the host owns the canonical set, assigns each mine a session serial, and broadcasts the
    /// set as a level (host snapshot + client intents, never a bidirectional op log). Off by
    /// default (<c>placed_mine_sync</c>), NEEDS-LIVE. See docs/research/mines.md §14.
    ///
    /// Version 67: <see cref="HelloPacket"/> gains a lineage seed
    /// (<c>LineageHostSessionId</c>/<c>LineageHostLoad</c>/<c>LineageClientLoad</c>) — a
    /// reconnecting CLIENT's process-lifetime record of the last point its own
    /// <see cref="SaveLineage"/> agreed with a host, carried across the reconnect gap so a
    /// bare <c>disconnect</c>/<c>reconnect</c> with no load edge either side seeds the fresh
    /// <see cref="SaveLineage"/> instance as already agreed instead of starting it "never
    /// agreed" again. The host seeds the new peer's own <see cref="SaveLineage"/> back the
    /// same way, gated on the carried session id matching its own (a restarted host mints a
    /// new one) and its own current <c>WorldDigest.LoadSerial</c> still matching the carried
    /// checkpoint. Closes docs/review-seamless-2026-09-18.md's "post-save rejoin
    /// compatibility" blocker, live 2026-09-24. See docs/research/session-save.md.
    /// Version 68 is unused. Version 69: <see cref="PlayerState.FlagMenuGuard"/>
    /// — a new bit on the existing flags dword (no packet shape changed, same as 50/54 before
    /// it). While a player has a full-screen menu open, their published state carries this bit
    /// for a bounded window (<c>KotorCoop.Session.MenuGuardRule</c>: capped per opening at
    /// <c>menu_guard_cap_ms</c>, default 12 s; a capped-out opening then holds off the next one
    /// for <c>menu_guard_cooldown_ms</c>, default 20 s) so a peer's engine holds their stand-in
    /// out of target selection and drops any hit that still lands on it — the parity fix for
    /// docs/limitations.md's "Pausing" row (in single-player KOTOR opening a menu freezes
    /// combat; here it only pauses the opener's own game). <c>menu_guard_sync</c>, default ON.
    /// A pre-69 peer never publishes the bit, so a mixed session keeps today's behaviour. No
    /// <c>SharedBlock</c> layout change: the plugin's per-slot flags dword's low 16 bits already
    /// carry every flag bit verbatim (<c>RemoteView.FlagsMask</c>), the same as
    /// <see cref="PlayerState.FlagStealth"/>. See docs/research/effect-sync.md §13.
    /// Version 70: the fixed 32-enemy census cap is
    /// gone. <see cref="EnemySnapshotPacket"/> gains <c>PageIndex</c>/<c>PageCount</c> (the
    /// existing <see cref="PacketType.EnemySnapshot"/>, no new packet type) so a module with
    /// more hostiles than fit one datagram pages across several, all stamped with the same
    /// <c>ServerTimeMs</c> tick id — the identical fix <see cref="WorldSnapshotPacket"/> and
    /// <see cref="CreatureLivenessPacket"/> already shipped for their own channels. <see
    /// cref="MaxEnemies"/> is now a PER-PACKET limit, not a per-cycle one. <see
    /// cref="MaxEnemyDamageEntries"/> widened from 32 to 128 to match the shared-block damage
    /// ledger's new capacity — still comfortably one packet, so that channel needed no paging
    /// of its own. See docs/research/creature-liveness.md's cap-removal section and
    /// docs/review-seamless-2026-09-18.md's "Enemy census beyond 32 candidates" row.
    /// Version 71: <see cref="ClassGrantPacket"/>'s BYTES are unchanged, but what a receiver
    /// does with one is not — one player's exact class is never copied onto another any more
    /// (docs/research/class-and-power-grants.md's 2026-09-25 section): the receiver now checks
    /// whether IT already has a Jedi class and, if not, shows its OWN player a private
    /// class-choice popup (<c>k_coop_jediclass.dlg</c>) instead of force-applying
    /// <see cref="ClassGrantPacket.ClassId"/>. A pre-71 peer would still force-apply the
    /// sender's class — a silent behaviour mismatch this gate would otherwise let two mismatched
    /// builds share bytes right through, so the version is bumped even though nothing on the
    /// wire itself changed.
    /// </summary>
    /// <summary>
    /// Version 72: two new packet types, <see cref="PacketType.CreditGrant"/>/
    /// <see cref="PacketType.CreditGrantAck"/> — a reliable "my conversation just handed my
    /// player N credits" relay for <c>credits_mode = personal</c>
    /// (docs/research/personal-inventory.md §4). Bumped because a pre-72 peer has no handler
    /// registered for either type at all (an unknown <c>PacketType</c> is simply ignored by the
    /// dispatch table, not a hard failure), so the OLDER peer would silently never receive a
    /// quest-reward credit its NEWER partner tried to relay — worth a version gate the same way
    /// every other new wire surface in this family gets one, even though nothing existing
    /// changed shape.
    /// </summary>
    /// <summary>
    /// Version 73: two new packet types, <see cref="PacketType.PartySelectRequest"/>/
    /// <see cref="PacketType.PartySelectResponse"/> — the party-select relay
    /// (docs/research/galaxy-map-and-party-select.md's 2026-09-25 section): a client's own
    /// suppressed <c>CSWGuiPartySelection</c> screen asks the host to open its own copy with
    /// the same parameters, instead of the roster poll converging silently with nobody able to
    /// pick. Bumped for the same reason version 72 was: a pre-73 peer has no handler for either
    /// type, so an OLDER host would never learn a NEWER client wanted to leave at all — a
    /// silent, confusing capability gap worth a version gate even though nothing existing
    /// changed shape.
    /// </summary>
    /// <summary>
    /// Version 74: <see cref="PartySelectRequestPacket"/> gains <c>TriggerId</c> (a live pass
    /// on version 73 found calling <c>CGuiInGame::ShowPartySelection</c> directly on the host
    /// unreliable — see the doc's own 2026-09-25 "SECOND PASS" section — so the host now
    /// re-fires the requester's own causal trigger instead; the new field is what it fires).
    /// Bumped because this CHANGES AN EXISTING PACKET'S SHAPE, unlike 72/73's purely additive
    /// new types: a version-73 sender's <c>TriggerId</c>-less packet would desync a version-74
    /// reader's field offsets for everything read after it. Both builds in any one session
    /// always match (a version mismatch is refused outright at Hello), so there is no partial
    /// compatibility to preserve — the gate exists purely to refuse the stale-build pairing
    /// cleanly instead of silently misreading bytes.
    /// </summary>
    // v75 carries the actual DLG override: scripted scenes need not use an NPC's default.
    // v76 adds the gameplay-content fingerprint to both handshake digests (scheme 2).
    public const byte Version = 76;

    /// <summary>Journal quest tags are resrefs plus a little slack.</summary>
    public const int MaxJournalTagLength = 32;

    /// <summary>
    /// Journal entries per snapshot datagram: tag (up to 33 bytes) + state (4) each, so
    /// 28 stays inside <see cref="MaxPacketSize"/>. A longer journal is sent as several
    /// snapshot packets in one burst.
    /// </summary>
    public const int MaxJournalEntriesPerPacket = 28;

    /// <summary>
    /// Flag entries per snapshot datagram: <c>FlagId</c> 8 + <c>Value</c> 1 + <c>Serial</c> 4 +
    /// <c>OriginPeerId</c> 4 = 17 bytes each (protocol 53 widened the id from 4), after the
    /// 10-byte header + version + count, so 81 is what fits inside <see cref="MaxPacketSize"/>
    /// (it was 106 at 13 bytes). A longer store is sent as several snapshot packets in one
    /// burst, exactly like <see cref="MaxJournalEntriesPerPacket"/>.
    ///
    /// ⚠ THIS USED TO BE A SILENT TRUNCATION, AND IT WAS THE HOST-TO-CLIENT BACKFILL PATH FOR
    /// DOORS. There are no host→client flag OPS — <c>CoopHost.SetFlag</c> applies locally and
    /// says "clients pick them up through the version tripwire" — so this one packet IS the
    /// only way a host-side door open, placeable search or global ever reaches a client that
    /// did not see it happen. Doors and placeables mint keys into the SAME
    /// <c>EventFlagStore</c> as globals and nothing is ever pruned from it, so a session
    /// reaches 106 entries in ordinary play, after which every later change was structurally
    /// undeliverable — and, because the digest is computed over the WHOLE store, the client
    /// then re-requested the same truncated head every 500 ms for the rest of the session.
    /// Live symptom: a door OPEN on the host, CLOSED on the client, forever.
    /// </summary>
    public const int MaxFlagEntriesPerPacket = 81;

    /// <summary>Odyssey resource references are at most 16 characters.</summary>
    public const int MaxResrefLength = 16;

    /// <summary>
    /// Ground items a session tracks at once. 16 is the most that still fits in a single
    /// item snapshot datagram with every string at maximum length: 66 bytes of id, resref,
    /// module and position plus 9 of item identity is 75 each, so a full 16 is 1210 bytes
    /// with the header — inside <see cref="MaxPacketSize"/>, and asserted by a test rather
    /// than by this arithmetic.
    /// </summary>
    public const int MaxGroundItems = 16;

    /// <summary>
    /// Distinct party-pool stack keys a session remembers at once (bounded like
    /// <see cref="MaxGroundItems"/>, not asserted against a snapshot datagram since this
    /// channel has none — see docs/research/party-pool-sync.md). A stash rarely holds more
    /// than a handful of genuinely distinct (resref, identity) combinations; existing keys can
    /// still change quantity past the cap, only a brand-new key is refused.
    /// </summary>
    public const int MaxPartyPoolStacks = 256;

    /// <summary>
    /// Distinct store stack keys (across every store, since <see cref="StoreStackKey"/> already
    /// carries the store's object id) a session remembers at once. Same reasoning and the same
    /// cap value as <see cref="MaxPartyPoolStacks"/> — see docs/research/store-sync.md.
    /// </summary>
    public const int MaxStoreStacks = 256;

    /// <summary>
    /// Party-pool snapshot entries per <see cref="PartyPoolSnapshotPacket"/> datagram
    /// (docs/research/shared-inventory-v2.md, <c>party_pool_sync=2</c>). ~28 bytes each
    /// (a length-prefixed <see cref="MaxResrefLength"/> resref + charges + max charges +
    /// upgrades + flags + a count), so a full 32 stays comfortably inside
    /// <see cref="MaxPacketSize"/> alongside the header/digest/generation/page overhead —
    /// asserted by a test. A pool holding more than 32 distinct stacks in one cycle (up to
    /// <see cref="MaxPartyPoolStacks"/>) rides a second (third, ...) page carrying the same
    /// digest and generation, exactly like <see cref="CreatureLivenessPacket"/>.
    /// </summary>
    public const int MaxPartyPoolSnapshotEntriesPerPacket = 32;

    /// <summary>
    /// Store snapshot entries per <see cref="StoreSnapshotPacket"/> datagram — the same
    /// reasoning as <see cref="MaxPartyPoolSnapshotEntriesPerPacket"/>, one entry wider (each
    /// entry additionally carries its own <c>StoreObjectId</c>) so the per-packet cap is
    /// slightly lower.
    /// </summary>
    public const int MaxStoreSnapshotEntriesPerPacket = 28;

    /// <summary>
    /// Creatures the owner replicates PER PACKET (protocol 70; a per-cycle limit before that).
    /// 40 bytes each (id, health, death, position, facing, animation, one-shot serial + id,
    /// plot, appearance, faction), so a full 32 is 1293 bytes with the header — inside one
    /// datagram with modest slack; the fit is asserted by a test. A module holding more
    /// hostiles than this pages across several <see cref="EnemySnapshotPacket"/> datagrams in
    /// the same tick (<c>EnemyCensusPaging.Paginate</c>, <c>KotorCoop.Session</c>) rather than
    /// truncating — see that type's own <c>PageIndex</c>/<c>PageCount</c> remarks and
    /// docs/research/creature-liveness.md's cap-removal section. The census itself still sorts
    /// in-combat creatures first, then nearest-to-a-player, so the FIRST page a receiver
    /// applies is always the most urgent one even if a later page is lost.
    /// </summary>
    public const int MaxEnemies = 32;

    /// <summary>
    /// Enemies one peer can report damage against per datagram (protocol 70: widened from 32
    /// to match <c>SharedBlock.DamageLedgerCapacity</c>'s own growth). 8 bytes each (object id +
    /// cumulative total), so a full 128 is 1082 bytes with the header and the module string —
    /// still comfortably inside one datagram, so this channel needed no paging of its own. It
    /// matches the plugin's own ledger capacity, which is the real limit: the ledger only holds
    /// enemies this instance has actually damaged and not yet seen die, and no surveyed module
    /// places anywhere near 128 concurrent hostiles for one peer to be fighting at once.
    /// </summary>
    public const int MaxEnemyDamageEntries = 128;

    /// <summary>
    /// Per-attacker breakdown entries on one <see cref="PhantomDamagePacket"/> — matches the
    /// plugin's own capture-side capacity (<c>SharedBlock.PhantomAttackerCapacity</c>) and the
    /// applied-side ledger's (<see cref="PhantomAttackerAppliedLedger.Capacity"/>). 8 bytes
    /// each (attacker id + cumulative), so a full 4 is 32 bytes — trivial next to
    /// <see cref="MaxPacketSize"/>; the cap exists to bound the fixed table this rides on, not
    /// the datagram. See docs/research/peer-damage-ledger.md §7.
    /// </summary>
    public const int MaxPhantomAttackerEntries = 4;

    /// <summary>
    /// Creature-liveness entries per <see cref="CreatureLivenessPacket"/> datagram.
    /// 5 bytes each (object id + a one-byte state), so a full 96 plus the header, module
    /// string and digest stays comfortably inside <see cref="MaxPacketSize"/> — asserted by
    /// a test. A module's full creature list can exceed this in one publish cycle; the extra
    /// entries ride a second (third, ...) page carrying the same digest and generation — see
    /// <see cref="CreatureLivenessPacket.PageIndex"/>.
    /// </summary>
    public const int MaxCreatureLivenessEntries = 96;

    /// <summary>
    /// Peer entries per <see cref="WorldSnapshotPacket"/> page. Each entry is up to 152 bytes
    /// (id + a <see cref="MaxNameLength"/> name + a <c>PlayerState</c> whose module and three
    /// equipment resrefs are all at their caps), and page 0 additionally carries the
    /// flag/item/journal/loot version+digest tripwires (which ride on page 0 only — see
    /// <see cref="WorldSnapshotPacket"/>'s remarks), so 5 is sized against page 0's own
    /// tighter budget rather than a later page's roomier one — the fit is asserted by a test
    /// rather than by this arithmetic alone. A full <see cref="MaxPeers"/> lobby needs at most
    /// two pages this way, comfortably inside <see cref="WorldSnapshotPacket.MaxPages"/>. See
    /// docs/research/lag-audit.md's 2026-08-27 entry.
    /// </summary>
    public const int MaxSnapshotEntriesPerPage = 5;

    /// <summary>Party companions the host replicates. KOTOR allows two active at once; a
    /// little headroom costs nothing.</summary>
    public const int MaxPartyMembers = 4;

    /// <summary>
    /// NPC roster slots. NINE, and this one is not a chosen cap — it is the engine's own
    /// bound, asserted in the binary rather than inferred: <c>CSWPartyTable::AddMember</c>
    /// (0x00565620) refuses <c>nNPC &lt; 0 || nNPC &gt;= 9</c> at 0x00565648, and the
    /// availability array at party table +0x30 is <c>int[9]</c>.
    ///
    /// The slot index IS the NPC identity in KOTOR (NPC_BASTILA, NPC_CARTH, ...), which is
    /// why the wire carries a bitmask and no separate id: there is nothing else to name.
    /// </summary>
    public const int PartyRosterSlots = 9;

    /// <summary>
    /// Encounters a client can report standing inside at once. Encounter activate areas are
    /// authored as disjoint polygons and the whole base game has at most 7 in a module
    /// (korr_m34aa), so overlapping four is already generous; the cap exists to bound the
    /// packet, not because a fifth is expected.
    /// </summary>
    public const int MaxEncounterTriggers = 4;

    /// <summary>
    /// Generic triggers a client can report standing inside at once, and the host's own
    /// per-module fired-set it broadcasts back. Matches <see cref="MaxEncounterTriggers"/>'s
    /// reasoning: activate polygons are authored disjoint, so overlapping a handful is
    /// already generous headroom rather than an observed need.
    /// </summary>
    public const int MaxTriggerRelayEntries = 4;

    /// <summary>
    /// Trigger ids the host reports as already-fired for one module, per
    /// <see cref="PacketType.TriggerRelay"/> broadcast. The early-game inventory in
    /// docs/research/trigger-relay.md finds at most 14 generic triggers in any one of the
    /// nine surveyed modules (end_m01aa) and the allowlist pattern narrows that further; 8 is
    /// headroom, not a measured ceiling — a module with more just has its fired-set catch up
    /// over a couple of extra ticks.
    /// </summary>
    public const int MaxTriggerFiredIds = 8;

    /// <summary>
    /// Native-fire entries (own-engine trigger fires, with origin peer) carried per
    /// <see cref="PacketType.TriggerRelay"/> packet, both directions: peer-&gt;host reports at
    /// most this many of ITS OWN recent natively-fired ids (<see cref="SharedBlock"/>'s mirror
    /// region caps at the same figure the plugin's watch region already uses); host-&gt;all
    /// rebroadcasts the merged per-module set, matched 1:1 against an origin-peer-id array of
    /// the same length. Same headroom reasoning as <see cref="MaxTriggerRelayEntries"/>.
    /// </summary>
    public const int MaxTriggerNativeFires = 4;

    /// <summary>
    /// Objects locked at once across the whole session. One conversation at a time is already
    /// the session rule, so this is headroom for the non-dialogue interactions the same
    /// primitive will carry (a container being looted, a workbench in use) rather than a
    /// number anyone is expected to reach.
    /// </summary>
    public const int MaxInteractionLocks = 4;

    /// <summary>
    /// The spectate line carried on the dialogue token.
    ///
    /// Deliberately larger than <see cref="MaxStringLength"/>: real KOTOR dialogue runs to
    /// ~200 characters and the shared 63-byte cap cut the spectator's subtitle mid-word —
    /// observed live as "Trask: That door's locked. I don't have the codes to open it, but
    /// swit". The dialogue token is a single string in a packet of its own, so the extra
    /// bytes cannot crowd out a peer snapshot. Still one length byte, so 255 is the hard
    /// ceiling.
    /// </summary>
    public const int MaxDialogueLineLength = 220;

    /// <summary>magic(2) + version(1) + type(1)</summary>
    public const int HeaderSize = 4;

    /// <summary>
    /// Comfortably inside the smallest common path MTU, so we never fragment. (Ethernet's
    /// 1500 less an IPv4 header and a UDP header leaves 1472; IPv6's guaranteed minimum is
    /// 1280 less 48 = 1232.)
    ///
    /// ⚠ Raised from 1200, which a FULL LOBBY did not fit in. Worst case per snapshot entry
    /// is 152 bytes — id, a 24-char name, and a player state whose module name and three
    /// equipment resrefs are all at their caps — so eight peers plus the header is 1241, and
    /// the whole snapshot was dropped. The guard test claimed to prove otherwise while
    /// quietly measuring a state with no equipment; see FullPeerSnapshotFitsInOneDatagram.
    /// Nothing on the wire encodes this value, so it is a buffer size and nothing more.
    ///
    /// 2026-08-27: raising the cap only ever bought back one growth cycle — the next field
    /// added to <c>PlayerState</c>, <c>MaxNameLength</c>, or <see cref="MaxPeers"/> itself
    /// would have reopened the identical silent-drop failure. <see cref="WorldSnapshotPacket"/>
    /// now pages instead (<see cref="MaxSnapshotEntriesPerPage"/>,
    /// <see cref="WorldSnapshotPacket.MaxPages"/>), the same fix already shipped for
    /// <see cref="PacketType.CreatureLiveness"/>'s wider publish, so a future overrun degrades
    /// to more datagrams per tick rather than reopening this bug. See
    /// docs/research/lag-audit.md.
    /// </summary>
    public const int MaxPacketSize = 1400;

    /// <summary>Default cap for the short identity fields (names, resrefs, tags), chosen so
    /// a full snapshot with every one at maximum still fits a datagram. NOT the absolute
    /// limit — see <see cref="MaxLengthPrefixedString"/>.</summary>
    public const int MaxStringLength = 63;

    /// <summary>What a single length byte can express. The absolute upper bound on any
    /// length-prefixed string, whatever cap the field itself declares.</summary>
    public const int MaxLengthPrefixedString = 255;

    /// <summary>
    /// Per-field caps, chosen so a full <see cref="MaxPeers"/> snapshot with every string
    /// at maximum length still fits in one datagram. Without these the worst case is
    /// ~1265 bytes and every tick would be silently dropped once the session filled up.
    /// </summary>
    public const int MaxNameLength = 24;

    /// <summary>
    /// Odyssey resource references are 16 characters; 32 leaves room without bloating
    /// the snapshot.
    /// </summary>
    public const int MaxModuleLength = 32;

    /// <summary>Object tags identify the NPC a shared conversation is with. Odyssey tags are
    /// authored short; 32 matches the plugin's buffer.</summary>
    public const int MaxTagLength = 32;

    public const int DefaultPort = 27015;

    /// <summary>
    /// State broadcast rate. Raised from 20 Hz after play testing reported the peer lagging:
    /// once the phantom is position-driven rather than walked to a goal, the tick interval is
    /// visible latency rather than just goal freshness, so it is worth spending. The packets
    /// are small and the expensive world-state scanning runs on its own divided-down pass.
    /// </summary>
    public const int DefaultTickRate = 30;

    /// <summary>
    /// How far in the past remote peers are rendered. Remote motion is interpolated
    /// between two received snapshots rather than extrapolated, which trades a fixed
    /// latency for the absence of rubber-banding.
    ///
    /// This is now the dominant term in how far behind a peer looks, so it is set just above
    /// two tick intervals — enough that there is always a later snapshot to interpolate
    /// towards on a LAN, without paying for jitter that a LAN does not have.
    /// </summary>
    public const int DefaultInterpolationDelayMs = 80;

    public const int PeerTimeoutMs = 5000;

    /// <summary>
    /// How long a client waits in Connecting before giving up on a host that never
    /// answers Hello at all (as opposed to answering with a Reject, which is the
    /// wrong-password path and resolves immediately, never through this timeout). A dead
    /// host, a firewalled port or a mistyped address all look identical from here: total
    /// silence. 15 s is long enough that a genuinely slow LAN handshake still lands, short
    /// enough that a player is not left staring at "connecting..." for a full minute.
    /// </summary>
    public const int DefaultConnectTimeoutMs = 15000;

    public const int MaxPeers = 8;

    /// <summary>
    /// Bytes per <see cref="PacketType.WorldTransferChunk"/> datagram (the last chunk of a
    /// transfer carries however many bytes remain, which is smaller). Comfortably under
    /// <see cref="MaxPacketSize"/> once the 4-byte header, <c>RequestId</c> and
    /// <c>ChunkIndex</c> are added (4 + 4 + 2 + 1024 = 1034), leaving headroom for a lower-MTU
    /// path than the LAN case <see cref="MaxPacketSize"/> itself is sized against.
    /// </summary>
    public const int WorldTransferChunkPayloadSize = 1024;

    /// <summary>
    /// The largest transfer the UDP world-transfer fallback (<see cref="PacketType.WorldTransferRequest"/>
    /// et al.) will attempt, in bytes. A real save measures ~654 KB
    /// (<c>KotorCoop.Session.WorldTransfer</c>'s own doc comment), so 8 MiB is over 12x
    /// headroom while still keeping <see cref="MaxWorldTransferChunks"/>' missing-bitmap inside
    /// one datagram — a save that somehow exceeds this refuses the UDP path outright rather
    /// than growing the bitmap past a single packet's worth of chunk-status bookkeeping. The
    /// TCP path (<see cref="MaxFileBytes"/>-style bounds in <c>WorldTransfer</c> itself) has no
    /// such ceiling, so a save this large still adopts fine over a path where TCP reaches the
    /// host at all.
    /// </summary>
    public const int MaxWorldTransferBytes = 8 * 1024 * 1024;

    /// <summary>
    /// <see cref="MaxWorldTransferBytes"/> divided into <see cref="WorldTransferChunkPayloadSize"/>-byte
    /// pieces, rounded up. A <see cref="PacketType.WorldTransferChunkStatus"/> bitmap is this
    /// many bits — 1024 bytes at the cap — which is why <see cref="MaxWorldTransferBytes"/> is
    /// sized the way it is rather than larger.
    /// </summary>
    public const int MaxWorldTransferChunks = (MaxWorldTransferBytes + WorldTransferChunkPayloadSize - 1)
        / WorldTransferChunkPayloadSize;
}
