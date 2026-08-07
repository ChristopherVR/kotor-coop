using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public sealed record AgentSettings
{
    /// <summary>How often the game's memory is sampled. Matches the network tick by default.</summary>
    public int PollRate { get; init; } = ProtocolConstants.DefaultTickRate;

    /// <summary>Mirror a peer's pause into this game (coop.ini pause_sync=0 opts out).</summary>
    public bool SyncPause { get; init; } = true;

    /// <summary>
    /// ⚠ This is a LATENCY knob, not a freshness knob. A world-state pass still costs
    /// hundreds of milliseconds when a cache expires inside it, and it shares this thread
    /// with the position pump — so every pass is a gap in the peer's movement. Measured, those
    /// gaps are what makes a running peer's gait flicker between run and idle, because the
    /// engine only pushes an animation on change and the pump sees no movement across a stall.
    /// A second between passes keeps doors and enemies converging fine while leaving movement
    /// alone the rest of the time.
    /// </summary>
    public int HeavyPumpIntervalMs { get; init; } = 1000;

    /// <summary>
    /// When true, remote players in a different module are not written into the game.
    /// KOTOR has exactly one module loaded at a time, so a peer on Taris has no
    /// meaningful position in your copy of the Endar Spire.
    /// </summary>
    public bool FilterByModule { get; init; } = true;

    /// <summary>
    /// When true, a client automatically travels to whatever module the host is in, the
    /// way a summoned phantom follows the world's owner. The host never follows anyone —
    /// it owns enemies and encounters, so its module is where the shared world happens.
    /// </summary>
    public bool FollowHostModule { get; init; } = true;

    /// <summary>How long to wait before re-requesting the same module, in milliseconds.
    /// A module load takes several seconds; re-sending during one would queue a second
    /// transition on arrival.</summary>
    public int FollowRetryMs { get; init; } = 15_000;

    /// <summary>
    /// How long a module must have been ours before the follow may take us out of it again.
    ///
    /// This is a CRASH guard, not a tuning knob: a transition issued 0.6 s after the last one
    /// completed killed the client in
    /// <c>CSWCMessage::HandleServerToPlayerCreatureUpdate_Add</c>, which dereferences
    /// <c>CClientExoApp::GetModule()</c> with no null check while creature-add messages for
    /// the world we just left are still being serviced. Zero disables it.
    /// </summary>
    public int ModuleSettleMs { get; init; } = 5000;

    /// <summary>
    /// When true, the party companions are reconciled: the host drives them and clients
    /// freeze and mirror. Uses the engine's solo-mode flag, whose full behaviour is
    /// inferred rather than measured, so it is an escape-hatchable default.
    /// </summary>
    public bool SyncParty { get; init; } = true;

    /// <summary>
    /// How near a peer must be for their pause to pause you too, in world units. A pause is
    /// for a situation you are both in; a partner across the module stopping to read their
    /// inventory should not freeze your fight. 0 shares every pause, whatever the distance.
    /// </summary>
    public float PauseShareRangeUnits { get; init; } = 30f;

    /// <summary>
    /// When true, party gold and PC experience are shared: the host publishes its absolute
    /// values and clients overwrite their own, so both parties level and spend together.
    /// Opinionated (it changes a client's own totals), so it is escape-hatchable.
    /// </summary>
    public bool SyncProgression { get; init; } = true;
}

/// <summary>
/// The pump that joins the two halves: read the local player out of the game, hand it to
/// the session, take the session's view of everyone else, and write it back into the game.
///
/// Everything above this class is game-agnostic and everything below it is a JSON offset
/// profile, which is what makes the same binary work against the mock harness and against
/// swkotor.exe.
/// </summary>
public sealed class CoopAgent(IGameBridge bridge, ICoopSession session, AgentSettings settings)
{
    private readonly object _snapshotGate = new();
    private PlayerState _lastLocalState = PlayerState.Empty;
    private IReadOnlyList<RemotePeer> _lastPeers = [];
    private string _lastTravelRequest = string.Empty;

    private long _lastHeavyPumpMs;
    private long _travelRetryAtMs;

    /// <summary>The module we last saw ourselves in, and when we arrived — so a transition is
    /// never issued while the world we just entered is still filling with objects. The flag
    /// distinguishes "we changed module" from "this is the first module we ever saw", which
    /// is where we already were and must not be made to wait.</summary>
    private string _settledModule = string.Empty;
    private long _moduleSettledAtMs;
    private bool _changedModule;

    public IGameBridge Bridge => bridge;

    public ICoopSession Session => session;

    /// <summary>Optional sink for one-off notable events; null means silent.</summary>
    public Action<string>? Log { get; set; }

    public bool LastReadSucceeded { get; private set; }

    public bool LastWriteSucceeded { get; private set; }

    public long ReadFailureCount { get; private set; }

    public PlayerState LastLocalState
    {
        get
        {
            lock (_snapshotGate)
            {
                return _lastLocalState;
            }
        }
    }

    public IReadOnlyList<RemotePeer> LastPeers
    {
        get
        {
            lock (_snapshotGate)
            {
                return _lastPeers;
            }
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Task sessionTask = session.RunAsync(cancellationToken);

        using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(1000.0 / settings.PollRate));

        // Coarse pump-cost telemetry: if a pump takes anywhere near the tick budget it
        // serialises the position loop, so surface the average/peak every ~5s to the log.
        var sw = new System.Diagnostics.Stopwatch();
        double budgetMs = 1000.0 / settings.PollRate;
        long ticks = 0;
        double sumMs = 0, maxMs = 0;

        // One bad tick must not end the session. The pump reads a live game's memory, and a
        // transient failure — a module mid-store, an object freed between two reads — throws
        // exactly when the game is doing something interesting. Killing the agent then is
        // what the players experience as "the other character vanished and never came back":
        // measured live, the joiner's agent silently exited mid-play and the host ran on
        // alone. Log the tick's failure and keep pumping; give up only when it fails
        // continuously for ~10 seconds, which means the game is actually gone.
        int consecutiveFailures = 0;
        int maxConsecutiveFailures = settings.PollRate * 10;

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                sw.Restart();
                try
                {
                    PumpOnce();
                    consecutiveFailures = 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    if (++consecutiveFailures == 1 || consecutiveFailures % settings.PollRate == 0)
                    {
                        Log?.Invoke($"pump tick failed ({consecutiveFailures} in a row): {ex.Message}");
                    }

                    if (consecutiveFailures >= maxConsecutiveFailures)
                    {
                        Log?.Invoke("pump failing continuously; the game is gone, shutting down");
                        break;
                    }
                }

                double ms = sw.Elapsed.TotalMilliseconds;

                ticks++;
                sumMs += ms;
                if (ms > maxMs) maxMs = ms;
                if (ticks >= 100)
                {
                    Console.WriteLine(
                        $"[pump] avg {sumMs / ticks:F1}ms  peak {maxMs:F1}ms  over {ticks} ticks (budget {budgetMs:F0}ms)");
                    ticks = 0; sumMs = 0; maxMs = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }

        // Undo the party freeze so a single-player continuation is normal — leaving solo
        // mode set would give the player companions that will not follow, with no cause.
        bridge.ReleasePartyFreeze();

        await sessionTask.ConfigureAwait(false);
    }

    public void PumpOnce()
    {
        if (bridge.TryReadLocalState(out PlayerState local))
        {
            LastReadSucceeded = true;
            session.PublishLocalState(local);

            lock (_snapshotGate)
            {
                _lastLocalState = local;
            }
        }
        else
        {
            LastReadSucceeded = false;
            ReadFailureCount++;
            local = LastLocalState;
        }

        IReadOnlyList<RemotePeer> peers = session.GetRemotePeers();

        lock (_snapshotGate)
        {
            _lastPeers = peers;
        }

        if (!bridge.CanWriteRemotes)
        {
            LastWriteSucceeded = false;
            return;
        }

        // Each channel is gated on the negotiated capability set: a mod difference that
        // would corrupt a channel disables that channel for the session (a different
        // globals catalogue drops world state; a different save drops enemies/doors/items)
        // while everything unaffected keeps working. Full set until a peer is judged.
        SessionCapabilities caps = session.Capabilities;

        // --- real-time path: player position + animation, EVERY tick --------------------
        // Only filter when both ends actually know their module. A profile that has not
        // located the module-name field reports an empty string, and treating "unknown"
        // as "somewhere else" would hide every peer and look exactly like a broken
        // network layer.
        IReadOnlyList<RemotePeer> visible = settings.FilterByModule && local.Module.Length > 0
            ? [.. peers.Where(p => p.State.Module.Length == 0 || p.IsInModule(local.Module))]
            : peers;

        // Mask the per-avatar fields a mod mismatch makes unsafe BEFORE they reach the
        // game. A different appearance.2da means a replicated row index could paint the
        // wrong body or index off the end (a crash), so the appearance is cleared and the
        // phantom keeps its blueprint look; a cleared row and empty resrefs are exactly
        // what the plugin already treats as "leave it alone".
        visible = MaskAvatarFields(visible, caps);

        LastWriteSucceeded = bridge.TryWriteRemotePeers(visible);

        // Track how long we have been in this module, for the travel settle window below.
        // Only real names count: mid-transition the bridge reports empty or an "areaXX"
        // fallback, and treating those as arrivals would restart the clock on noise.
        if (local.Module.Length > 0
            && !IsFallbackModuleName(local.Module)
            && !string.Equals(local.Module, _settledModule, StringComparison.OrdinalIgnoreCase))
        {
            _changedModule = _settledModule.Length > 0;
            _settledModule = local.Module;
            _moduleSettledAtMs = SessionClock.NowMs;
        }

        // Pause and travel are cheap flag checks and must feel immediate.
        PumpTravel(local, peers);
        PumpPause(local, visible);

        // Death is arbitrated every tick too: a rescue window measured in seconds cannot
        // wait for the ~5 Hz heavy pass, and a swallowed game-over must be answered before
        // the engine tries again on the next frame.
        PumpDeath(local, visible);

        // Client-only, every tick: keep the player's input locked to their own PC (a cheap id
        // write) so an attempted switch to a shared, host-owned companion reverts within a frame
        // rather than waiting for the ~5 Hz heavy party pass. Host owns the companions; it is free
        // to control them.
        if (settings.SyncParty && bridge.SupportsPartySync && !session.IsEnemyAuthority
            && (caps & SessionCapabilities.Party) != 0)
        {
            bridge.LockControlToPlayer();
        }

        // --- heavy world-state path ------------------------------------------------------
        //
        // The syncs below scan the game's memory for creatures, items and doors. One pass
        // costs orders of magnitude more than the position write above, and they share this
        // thread with it — so their cadence decides how fresh a peer's POSITION is.
        //
        // ⚠ This is measured on a WALL CLOCK, not a tick count, and that distinction is the
        // whole bug. Counting ticks ties the scan rate to the send rate, so raising the send
        // rate to reduce latency multiplies the scans instead — at 60 Hz with a divisor of 4
        // the heavy pass ran 15x a second, each pass outlasting its slot, and the loop simply
        // never caught up. Measured end to end: a position took 3.5-7.4 SECONDS to travel
        // from one game's memory to the other's. On a wall clock the scans stay at a fixed
        // few per second no matter how fast state is sent, so the fast path is free to run at
        // whatever rate the tick asks for.
        long nowMs = Environment.TickCount64;
        if (nowMs - _lastHeavyPumpMs < settings.HeavyPumpIntervalMs)
        {
            return;
        }

        _lastHeavyPumpMs = nowMs;

        // Report our in-world content fingerprint (the session rate-limits the wire
        // anyway). The save identity and the current module's GIT digest ride along with
        // the globals catalogue: passing the LIVE module here is what makes the GIT
        // comparison per-module — it re-judges itself on every module change, restoring
        // the id-keyed channels wherever the two installs' GITs agree again.
        session.PublishWorldDigest(new WorldDigest(
            bridge.GlobalsCatalogueDigest,
            bridge.SaveIdentityDigest,
            local.Module,
            bridge.ModuleGitDigest(local.Module)));

        PumpItems(local, caps);
        PumpEnemies(local, visible, caps);
        PumpPhantomDamage(caps);
        PumpParty(caps);
        PumpProgression(caps);
        PumpDialogue(local, caps);
        PumpWorldState(caps);
        PumpJournal(caps);
        PumpDoors(caps);
        PumpPlaceables(caps);
        PumpContainerLoot(caps);
    }

    /// <summary>
    /// Carries hits BOTH ways for combat participation: what each peer's phantom has taken
    /// in this game goes out to that peer, and what this player owes for hits taken on
    /// their own phantom elsewhere goes into the plugin to be applied for real.
    ///
    /// This is what makes enemies dangerous to everyone rather than only to whoever owns
    /// them. Running totals, so a dropped datagram costs a tick rather than a hit.
    /// </summary>
    private void PumpPhantomDamage(SessionCapabilities caps)
    {
        if ((caps & SessionCapabilities.Enemies) == 0)
        {
            return;
        }

        // Outbound: slot i of the plugin's phantom-hit table belongs to peer slot i, which
        // is the same ordering the agent used when it wrote the peers in.
        IReadOnlyList<RemotePeer> peers = session.GetRemotePeers();
        for (int i = 0; i < peers.Count; i++)
        {
            if (!bridge.TryReadPhantomHit(i, out uint phantomId, out uint cumulative, out uint attacker)
                || phantomId == 0
                || cumulative == 0)
            {
                continue;
            }

            session.PublishPhantomDamage(peers[i].PeerId, cumulative, attacker);
        }

        // Inbound: hand the plugin the running total. Level state, written every tick —
        // the plugin tracks what it has already applied, so this cannot double-count.
        (uint owed, uint from) = session.GetIncomingPhantomDamage();
        if (owed > 0)
        {
            bridge.TryWriteIncomingDamage(owed, from);
        }
    }

    /// <summary>
    /// Keeps the party companions single and coherent. The host owns them — its own engine
    /// AI drives Trask/Carth normally, and it just publishes where they are. Every client
    /// freezes its own copies (solo mode) and mirrors the host's, so the companions are one
    /// synchronised set rather than two that drift apart. Host-authoritative, so exactly one
    /// instance's AI is ever in charge.
    /// </summary>
    private void PumpParty(SessionCapabilities caps)
    {
        if (!settings.SyncParty || !bridge.SupportsPartySync || (caps & SessionCapabilities.Party) == 0)
        {
            return;
        }

        if (session.IsEnemyAuthority)
        {
            // The host: publish, do not freeze. Its companions follow through the game's
            // own AI exactly as in single-player.
            session.PublishParty(bridge.ReadPartyMembers());
            return;
        }

        // A client: freeze the local party AI and mirror the host's companions. The freeze
        // is re-asserted inside TryWritePartyMembers every pass. (The input control-lock that
        // stops the client controlling those companions runs every tick in the fast path.)
        bridge.TryWritePartyMembers(session.GetPartyMembers());
    }

    /// <summary>
    /// Shares progression: the host publishes its party gold and PC experience; a client
    /// overwrites its own with the host's absolute values each tick, so the two parties level
    /// and spend together. Absolute (not additive) because every kill grants XP/gold locally
    /// on both instances — adding a client's own on top would double-count.
    /// </summary>
    private void PumpProgression(SessionCapabilities caps)
    {
        if (!settings.SyncProgression
            || !bridge.SupportsProgressionSync
            || (caps & SessionCapabilities.Progression) == 0)
        {
            return;
        }

        if (session.IsEnemyAuthority)
        {
            // The host is authoritative for progression too: publish, never mirror.
            if (bridge.TryReadProgression(out uint gold, out uint experience))
            {
                session.PublishProgression(gold, experience);
            }

            return;
        }

        // A client: mirror the host's totals, once any have arrived.
        if (session.TryGetProgression(out uint hostGold, out uint hostExperience))
        {
            bridge.TryWriteProgression(hostGold, hostExperience);
        }
    }

    /// <summary>Token serial of the last dialogue line surfaced to the local overlay, so a
    /// peer's line is posted once per line rather than every tick.</summary>
    private uint _lastSpectateSerial;

    /// <summary>When the local player was last genuinely in a conversation, and who with —
    /// held across the engine's between-entries flicker (see PumpDialogue).</summary>
    private long _dialogueLastActiveMs;
    private string _dialogueHeldPartnerTag = string.Empty;

    /// <summary>How long a conversation may read inactive before it is reported over.
    /// Longer than any between-entries gap, shorter than feels laggy on a real close.</summary>
    private const long DialogueLingerMs = 1500;

    /// <summary>
    /// Publishes whether the local player is in a conversation (so the session can hold a
    /// single arbitrated dialogue token) together with the spectate payload — the speaker and
    /// line the plugin read off the live conversation. Then, when another peer owns an active
    /// conversation and a real line has arrived, surfaces it on this player's always-on chat
    /// overlay so they can spectate what the owner is hearing.
    ///
    /// The line is empty until the plugin resolves it (base-game TLK-only lines still read as
    /// empty — see docs/research/dialogue-sync.md); the ownership token is useful on its own.
    /// </summary>
    private void PumpDialogue(in PlayerState local, SessionCapabilities caps)
    {
        if ((caps & SessionCapabilities.Dialogue) == 0)
        {
            return;
        }

        // What the local player is hearing and doing, straight from the plugin's read.
        bridge.TryReadLocalDialogue(out string speaker, out string line);
        bridge.TryReadLocalDialogueFollow(
            out string partnerTag, out uint replySerial, out int replyIndex, out byte[] pickLog);

        // Who owns a conversation right now, from the last broadcast. Read FIRST because it
        // gates the local claim below.
        DialogueState token = session.GetDialogue();
        bool followingPeer = token.IsHeldByAnotherThan(session.LocalPeerId);

        // ⚠ A follower's conversation is NOT a claim. While this instance follows a peer's
        // conversation, its own client is genuinely "in a conversation" — the copy we
        // opened. Publishing that as a claim made the roles flip at the end: the owner
        // released, the follower's still-open copy grabbed the token, and the former owner
        // dutifully started following its own conversation back — the follow/close ping-pong
        // in the logs.
        bool rawInDialogue = local.IsInDialogue && !followingPeer;

        // ⚠ DEBOUNCED, because the engine's "conversation active" flag flickers off for a
        // few frames between entries. Publishing those gaps released and re-granted the
        // token several times inside one conversation, and the spectator tore its copy down
        // and reopened it on every flicker. A conversation is only reported over once it
        // has been gone for a moment; the partner tag is held through the gaps too.
        long nowMs = Environment.TickCount64;
        if (rawInDialogue)
        {
            _dialogueLastActiveMs = nowMs;
            if (partnerTag.Length > 0)
            {
                _dialogueHeldPartnerTag = partnerTag;
            }
        }

        bool active = rawInDialogue
            || (_dialogueLastActiveMs > 0 && (nowMs - _dialogueLastActiveMs) < DialogueLingerMs);
        if (!active)
        {
            _dialogueHeldPartnerTag = string.Empty;
        }

        session.PublishDialogue(
            active, speaker, line,
            active ? _dialogueHeldPartnerTag : string.Empty, replySerial, replyIndex, pickLog);

        // The other direction: when a peer owns a conversation, hand the plugin who they are
        // talking to and which reply they last picked, so this instance runs the SAME
        // conversation in the game's own dialogue UI and follows it. Written every tick
        // rather than on the serial edge — the plugin is level-triggered on these fields and
        // a dropped edge would strand a spectator outside the conversation.
        bridge.WriteRemoteDialogue(
            followingPeer,
            followingPeer ? token.PartnerTag : string.Empty,
            followingPeer ? token.ReplySerial : 0,
            followingPeer ? token.ReplyIndex : 0,
            followingPeer ? token.PickLog : null);

        _lastSpectateSerial = token.Serial;
    }

    /// <summary>
    /// Clears the avatar fields the negotiated capabilities forbid, so a mod mismatch
    /// cannot reach the game as a bad appearance row or a wrong-item equip. Returns the
    /// list unchanged when everything is permitted, which is the common case.
    /// </summary>
    private static IReadOnlyList<RemotePeer> MaskAvatarFields(
        IReadOnlyList<RemotePeer> peers, SessionCapabilities caps)
    {
        bool appearance = (caps & SessionCapabilities.Appearance) != 0;
        bool equipment = (caps & SessionCapabilities.Equipment) != 0;

        if (appearance && equipment)
        {
            return peers;
        }

        RemotePeer[] masked = new RemotePeer[peers.Count];
        for (int i = 0; i < peers.Count; i++)
        {
            PlayerState state = peers[i].State;

            if (!appearance)
            {
                state = state with { Appearance = 0 };
            }

            if (!equipment)
            {
                state = state with { EquippedBody = "", EquippedRightWeapon = "", EquippedLeftWeapon = "" };
            }

            masked[i] = peers[i] with { State = state };
        }

        return masked;
    }

    /// <summary>
    /// Carries the quest journal both ways: local advances are announced to the session
    /// (reliably, merge-up), and anything the session knows that this game does not is
    /// applied through the engine's own SetState, which refreshes the journal GUI. This
    /// plus the globals channel is what makes a quest one quest instead of two parallel
    /// ones.
    /// </summary>
    private void PumpJournal(SessionCapabilities caps)
    {
        if (!bridge.SupportsJournalSync || (caps & SessionCapabilities.Journal) == 0)
        {
            return;
        }

        foreach (KeyValuePair<string, int> change in bridge.CollectChangedJournal())
        {
            session.SetJournalEntry(change.Key, change.Value);
        }

        foreach (KeyValuePair<string, int> entry in session.Journal.Snapshot())
        {
            // The bridge keeps its own shadow and skips entries the game already has, so
            // this is a cheap no-op almost every tick.
            bridge.TryApplyJournalEntry(entry.Key, entry.Value);
        }
    }

    private bool _remotePauseHeld;

    /// <summary>
    /// Either player's pause pauses both worlds. Each peer publishes its OWN pause in its
    /// state flags (mirrored pauses are excluded at the source); this side asks the
    /// plugin to hold the engine's pause while any peer's flag is up. The pauser is the
    /// only one who can release it — a local space-bar press against a mirrored pause is
    /// re-asserted by the plugin.
    /// </summary>
    private void PumpPause(in PlayerState local, IReadOnlyList<RemotePeer> peersHere)
    {
        if (!settings.SyncPause || !bridge.SupportsPauseSync)
        {
            return;
        }

        // ⚠ ONLY PEERS IN THE VICINITY. A pause is for the situation you are BOTH in — the
        // fight you are both standing in, the decision you are both making. Freezing a
        // partner on the other side of the module because you stopped to think is not shared
        // pause, it is one player taking the other's game away, and it is the opposite of
        // seamless. Beyond this range the two of you are not interacting, so there is nothing
        // to keep in step and each world runs on its own.
        //
        // Either of you can still release it: PauseController honours a local unpause and
        // does not re-assert (see "released locally"), so whoever is ready to move on can.
        float rangeSquared = settings.PauseShareRangeUnits * settings.PauseShareRangeUnits;

        bool anyPeerPaused = false;
        for (int i = 0; i < peersHere.Count; i++)
        {
            if (!peersHere[i].State.IsPaused)
            {
                continue;
            }

            // Range of 0 shares every pause regardless of distance (coop.ini escape hatch
            // for anyone who wants the old all-or-nothing behaviour back).
            if (settings.PauseShareRangeUnits <= 0f
                || peersHere[i].State.Position.DistanceSquaredTo(local.Position) <= rangeSquared)
            {
                anyPeerPaused = true;
                break;
            }
        }

        if (anyPeerPaused != _remotePauseHeld && bridge.TrySetRemotePause(anyPeerPaused))
        {
            _remotePauseHeld = anyPeerPaused;
            Log?.Invoke(anyPeerPaused ? "a peer paused; pausing with them" : "peer pause released");
        }
    }

    /// <summary>
    /// Co-op death: nobody drops out of the session alone, and nobody is left alive alone in
    /// a wiped one.
    ///
    /// KOTOR downs party members rather than killing them, and stands them up by itself once
    /// the area is clear — that is kept. What this adds is the two-instance half:
    ///
    /// - **Rescue.** A downed player whose peer is standing beside them is revived at once,
    ///   rather than waiting out the engine's own combat-clear timer. That is the co-op
    ///   moment worth having: your partner comes to get you.
    /// - **A wipe is unanimous.** When one instance's whole party falls, its game-over is
    ///   swallowed and this instance simply lies there. Only when EVERY player is down does
    ///   the suppression lift, and then it lifts on all of them, so both players get the
    ///   game's own ending together instead of one being dropped to the main menu mid-fight.
    ///
    /// Both suppressions live in the plugin as inert byte patches until turned on here, so a
    /// game with no session behaves exactly like stock KOTOR.
    /// </summary>
    private void PumpDeath(in PlayerState local, IReadOnlyList<RemotePeer> visible)
    {
        if (!bridge.SupportsDeathHandling)
        {
            return;
        }

        // A wipe needs every player down — including this one. A peer in another module is
        // not part of this fight, but it is also not a corpse: it can still come back for
        // us, so it counts as standing.
        bool everyPeerDown = true;
        for (int i = 0; i < visible.Count; i++)
        {
            if (!visible[i].State.IsDowned)
            {
                everyPeerDown = false;
                break;
            }
        }

        bool allDown = local.IsDowned && visible.Count > 0 && everyPeerDown;

        // Hold the game-over unless the session really is wiped; always hold the control
        // swap (it fights the control lock and hands a client the host's companions).
        if (allDown != _sessionWiped || !_deathSuppressionSet)
        {
            _deathSuppressionSet = true;
            _sessionWiped = allDown;
            // Swap suppression is a CLIENT-side lock, not a global one. It exists so a client
            // cannot take over a companion the host's AI owns — but it blocks Tab and the
            // portraits outright, so applying it to the host too left NOBODY able to switch
            // character (reported from play). The host owns the companions and must stay free
            // to control them.
            bridge.SetDeathSuppression(
                suppressGameOver: !allDown,
                suppressSwap: !session.IsEnemyAuthority);

            if (allDown)
            {
                Log?.Invoke("every player is down; releasing the game-over screen for everyone");
            }
        }

        // The engine tried to end the game here. With the suppression on that is only "this
        // instance's party is down" — worth saying once, so a waiting player knows why they
        // are lying on the floor instead of at the menu.
        if (bridge.TryTakeGameOverAttempt() && !allDown)
        {
            Log?.Invoke("your party is down — a peer can still revive you");
        }

        // The rescue itself: a standing peer close enough to reach us.
        if (!local.IsDowned)
        {
            _reviveRequested = false;
            return;
        }

        if (_reviveRequested || allDown)
        {
            return; // already asked, or there is nobody left standing to do the reviving
        }

        for (int i = 0; i < visible.Count; i++)
        {
            RemotePeer peer = visible[i];
            if (peer.State.IsDowned || !peer.IsInModule(local.Module))
            {
                continue;
            }

            if (peer.State.Position.DistanceTo(local.Position) <= ReviveRangeUnits)
            {
                _reviveRequested = true;
                bridge.RequestRevive(ReviveHitPoints);
                Log?.Invoke($"{peer.Name} revived you");
                return;
            }
        }
    }

    /// <summary>How close a standing peer must be to pick a downed player up. Roughly a
    /// couple of body-lengths — close enough to read as "they came to get you".</summary>
    private const float ReviveRangeUnits = 3.5f;

    /// <summary>Hit points a rescued player stands up with: enough to move and fight back,
    /// little enough that being downed still cost something.</summary>
    private const int ReviveHitPoints = 15;

    private bool _deathSuppressionSet;
    private bool _sessionWiped;
    private bool _reviveRequested;

    /// <summary>
    /// Keeps a client in the host's module. When the host crosses into another area, the
    /// client warps after it through the game's own transition path; a client whose own
    /// door leads somewhere the host is not gets brought back the same way. That is the
    /// Souls-seamless rule — the world follows its owner — and it is one-directional by
    /// construction, so two instances can never chase each other in a loop.
    /// </summary>
    private void PumpTravel(in PlayerState local, IReadOnlyList<RemotePeer> peers)
    {
        if (!settings.FollowHostModule
            || session.IsEnemyAuthority
            || !bridge.SupportsTravel
            || local.Module.Length == 0)
        {
            return;
        }

        // ⚠ AND NOT WHILE WE DO NOT KNOW WHERE WE ARE.
        //
        // "areaXX" is the bridge's fallback when the module NAME is unreadable, and the name
        // is unreadable for exactly one reason: a transition is in progress here. The target
        // side of this method has always rejected that value; the LOCAL side did not, so
        // mid-load we compared a placeholder against the host's real module, concluded we
        // were separated, and fired a second transition into the middle of the first.
        //
        // Measured, twice: two travels 1.1 s apart, then the client dead in
        // CSWCMessage::HandleServerToPlayerCreatureUpdate_Add+0x16D, which dereferences
        // CClientExoApp::GetModule() with no null check while the previous world's
        // creature-add messages are still being serviced.
        if (IsFallbackModuleName(local.Module))
        {
            return;
        }

        // Never yank the player out of a conversation; the module mismatch survives
        // until the next pump anyway.
        if (bridge.IsInDialogue)
        {
            return;
        }

        // ⚠ LET A MODULE SETTLE BEFORE LEAVING IT AGAIN. A transition issued moments after
        // the last one finished killed the client outright, measured:
        //
        //   07:48:17.766  travel: moving to module 'end_m01ab'      (this player's own move)
        //   07:48:19.604  travel: moving to module 'END_M01AA'      (the follow, 1.8 s later)
        //   07:48:20.266  CRASH at CSWCMessage::HandleServerToPlayerCreatureUpdate_Add+0x16D
        //
        // That instruction is `mov ebp,[eax+0x48]` on the result of
        // CClientExoApp::GetModule, dereferenced with no null check — so the client was
        // still being handed creature-add messages for a world it no longer had. The load
        // had already COMPLETED (the module name read fine, which is how the follow fired at
        // all), so gating on "loading" would not have caught it; what overlaps is the burst
        // of object messages that follows a world coming up.
        //
        // Retail never reaches this because its transitions are player-driven and never
        // back-to-back. Follow-the-host can be, and a client walking through its own door is
        // exactly how: it arrives somewhere the host is not and is immediately brought back.
        if (_changedModule && SessionClock.NowMs - _moduleSettledAtMs < settings.ModuleSettleMs)
        {
            return;
        }

        RemotePeer? host = null;
        for (int i = 0; i < peers.Count; i++)
        {
            if (peers[i].PeerId == CoopHost.HostPeerId)
            {
                host = peers[i];
                break;
            }
        }

        string target = host?.State.Module ?? string.Empty;

        if (target.Length == 0 || host!.IsInModule(local.Module))
        {
            // Together (or the host is unreadable): clear the debounce so the next real
            // separation acts immediately.
            _lastTravelRequest = string.Empty;
            return;
        }

        // Stunt modules stage cutscenes. The host will be dumped back into a real module
        // when its scene ends; following it in would trap this player in someone else's
        // cinematic.
        if (target.StartsWith("stunt", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // "areaXX" is the bridge's fallback when the module NAME could not be read (a
        // transition in flight on the host's side). It is an object id, not a module, and
        // asking the engine to load it would fail at best. Wait for the real name.
        if (IsFallbackModuleName(target))
        {
            return;
        }

        long now = SessionClock.NowMs;
        if (string.Equals(target, _lastTravelRequest, StringComparison.OrdinalIgnoreCase)
            && now < _travelRetryAtMs)
        {
            return;
        }

        if (bridge.TryRequestTravel(target))
        {
            _lastTravelRequest = target;
            _travelRetryAtMs = now + settings.FollowRetryMs;
            Log?.Invoke($"following the host to '{target}'");
        }
    }

    /// <summary>True for "area1F"-style names: the bridge's stand-in when the real module
    /// name was unreadable, built from an object id that names nothing on disk.</summary>
    private static bool IsFallbackModuleName(string module)
    {
        if (module.Length <= 4 || !module.StartsWith("area", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (int i = 4; i < module.Length; i++)
        {
            if (!Uri.IsHexDigit(module[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Carries door state both ways on the same reliable, max-merged flag channel the
    /// quest globals use. A door open for one player and shut for the other is the most
    /// visible desync co-op can produce; door values are monotonic (closed to open,
    /// locked to unlocked, anything to destroyed), so the merge semantics fit exactly.
    /// </summary>
    private void PumpDoors(SessionCapabilities caps)
    {
        if (!bridge.SupportsDoorSync || (caps & SessionCapabilities.Doors) == 0)
        {
            return;
        }

        foreach (KeyValuePair<uint, sbyte> change in bridge.CollectChangedDoorState())
        {
            // Same echo guard as the globals: only announce what advanced our own view.
            if (session.Flags.Merge(change.Key, change.Value))
            {
                session.SetFlag(change.Key, change.Value);
            }
        }

        bridge.TryWriteDoorStates(session.Flags.Snapshot());
    }

    /// <summary>
    /// The door channel's twin for placeables (chests, corpses, switches, containers): a
    /// searched chest or unlocked container stays that way for both players. Rides the same
    /// reliable flag store under a different tag, and shares the door capability gate because
    /// it rests on the same object-id-across-the-same-save assumption.
    /// </summary>
    private void PumpPlaceables(SessionCapabilities caps)
    {
        if (!bridge.SupportsPlaceableSync || (caps & SessionCapabilities.Doors) == 0)
        {
            return;
        }

        foreach (KeyValuePair<uint, sbyte> change in bridge.CollectChangedPlaceableState())
        {
            if (session.Flags.Merge(change.Key, change.Value))
            {
                session.SetFlag(change.Key, change.Value);
            }
        }

        bridge.TryWritePlaceableStates(session.Flags.Snapshot());
    }

    /// <summary>
    /// Keeps shared containers (chests, corpses) from duplicating loot. The host detects an
    /// item leaving a container and publishes the (container, item) removal reliably; the
    /// client applies it to its own copy so the item cannot be taken a second time. One
    /// direction only — loot is host-authoritative, exactly like enemies — and it shares the
    /// Doors capability gate because it rests on the same object-id-across-the-same-save
    /// assumption doors do.
    /// </summary>
    private void PumpContainerLoot(SessionCapabilities caps)
    {
        if (!bridge.SupportsContainerLoot || (caps & SessionCapabilities.Doors) == 0)
        {
            return;
        }

        if (session.IsEnemyAuthority)
        {
            // The host: detect items that left a container and announce their removal.
            foreach (ContainerLootRemoval removal in bridge.CollectContainerLootRemovals())
            {
                session.PublishContainerLoot(removal.ContainerId, removal.ItemId);
            }

            return;
        }

        // A client: re-declare the host's known removals to the game so each looted item is
        // deleted from this instance's copy of the container. Idempotent — an already-removed
        // item resolves to a no-op.
        bridge.TryWriteContainerLoot(session.ContainerLootRemovals);
    }

    /// <summary>
    /// Carries KOTOR's own world state — the quest and story globals every script branches
    /// on — in both directions. This is what separates two players sharing a map from two
    /// players sharing a game.
    ///
    /// Both directions merge upwards rather than overwrite (see EventFlagStore.Merge), so
    /// the two instances converge regardless of packet order and neither can drag the other
    /// backwards through a quest.
    /// </summary>
    private void PumpWorldState(SessionCapabilities caps)
    {
        if ((caps & SessionCapabilities.WorldState) == 0)
        {
            return;
        }

        if (!bridge.SupportsWorldState)
        {
            return;
        }

        foreach (KeyValuePair<uint, sbyte> change in bridge.CollectChangedWorldState())
        {
            // Only announce it if it actually advanced our own view; a value the session
            // already knows needs no op, and re-announcing is how echo storms start.
            if (session.Flags.Merge(change.Key, change.Value))
            {
                session.SetFlag(change.Key, change.Value);
            }
        }

        // Apply anything the session knows that the game does not. The bridge updates its
        // own shadow copy as it writes, so the next scan does not see this as local.
        foreach (KeyValuePair<uint, sbyte> flag in session.Flags.Snapshot())
        {
            bridge.TryApplyWorldState(flag.Key, flag.Value);
        }
    }

    /// <summary>
    /// Moves enemy state between the game and the session in whichever direction this peer
    /// is responsible for. Exactly one peer owns enemies; it publishes what its own engine
    /// says, and everyone else applies that and is prevented from killing anything locally.
    ///
    /// The two directions carry deliberately different things. DOWN from the owner comes
    /// health, absolutely, because it is the owner's truth about a creature. UP from a peer
    /// comes DAMAGE DEALT, cumulatively, because a peer's contribution is not a fact about
    /// the creature at all — it is a fact about the peer, and trying to express it as health
    /// meant sampling a number the owner's own mirror kept overwriting.
    /// </summary>
    private void PumpEnemies(
        in PlayerState local, IReadOnlyList<RemotePeer> peersHere, SessionCapabilities caps)
    {
        if (!bridge.SupportsEnemySync || (caps & SessionCapabilities.Enemies) == 0)
        {
            return;
        }

        // Where the other players are, so the census cap sheds by distance to the nearest
        // of us rather than to whoever happens to be publishing. `peersHere` is already
        // module-filtered, which is what makes these positions comparable at all.
        Vector3f[] otherPlayers = new Vector3f[peersHere.Count];
        for (int i = 0; i < peersHere.Count; i++)
        {
            otherPlayers[i] = peersHere[i].State.Position;
        }

        if (session.IsEnemyAuthority)
        {
            // Hold the last snapshot rather than publishing while in a conversation. The
            // dialogue system wipes every hostile's action queue in this instance on every
            // tick, so what we would send is a pacified fight — and the peer, who is not
            // talking, would have their own fight go quiet for the duration.
            if (!bridge.IsInDialogue)
            {
                session.PublishEnemies(local.Module, bridge.ReadEnemies(otherPlayers));
            }

            // Where a peer's damage becomes real. Its copies are pinned at 1 hp, so its
            // whole fight would be cosmetic unless this side spends the damage it has
            // dealt — as REAL engine damage on our own copy, which is also how the kill
            // happens: an enemy whose accumulated damage exceeds its health dies here, with
            // the corpse, loot, XP and OnDeath the engine runs for any other death.
            // Published every pass, even when it is empty. The epoch travels with it, and
            // the plugin needs to hear about a module change BEFORE the first damage in the
            // new module rather than after — otherwise it would spend the new totals against
            // the old module's applied figures, which name different creatures.
            bridge.TryPublishDamageOwed(
                _peerDamage.Epoch,
                _peerDamage.Merge(local.Module, session.GetEnemyDamage(local.Module)));

            return;
        }

        // This peer mirrors the owner's enemies, so its own encounters must never fire:
        // an encounter spawn here would shift the object-id counter and break the
        // id-identity the whole enemy channel keys on. Cheap once done — the bridge
        // remembers the module and the call is a no-op until it changes.
        int disabled = bridge.DisableLocalEncounters();
        if (disabled > 0)
        {
            Log?.Invoke($"disabled {disabled} local encounter(s); the enemy owner's spawns are authoritative");
        }

        // Module-gated: the owner's ids only mean anything while we are where they were
        // read. Apart, this is empty and our own copies are left alone rather than driven
        // onto whatever local creatures share those ids.
        //
        // ⚠ And an empty list is WRITTEN, not skipped. Skipping was right when empty only
        // ever meant "the host has not sent one yet"; with the gate it also means "these ids
        // do not belong here", and leaving the previous snapshot in the shared block would
        // let the plugin keep driving the old module's ids onto the new module's creatures —
        // exactly the thing the gate exists to stop. Writing a count of zero clears them.
        bridge.TryWriteEnemies(session.GetEnemies(local.Module));

        // Report what we have DEALT, so our fight counts.
        //
        // ⚠ Not what we have OBSERVED. This used to send our copies' health and let the
        // owner take min(), and it lost most of every fight: the owner writes its own value
        // back onto our copy several times a second, so a hit had to win a race against the
        // next mirror pass to be sampled at all. Measured — a client attacking one soldier
        // for ninety seconds moved it from 15 hit points to 11 and then never again. A
        // cumulative sum of damage dealt cannot be raced, because nothing ever reads it back
        // out of the game.
        //
        // Not during dialogue, for the same reason the census pauses: the local fight is
        // disarmed and unrepresentative. Nothing is lost — the totals are cumulative, so
        // whatever accrued arrives whole on the next tick that is allowed to send.
        if (!bridge.IsInDialogue)
        {
            (uint generation, List<EnemyDamageEntry> dealt) = bridge.ReadDamageDealt();
            if (dealt.Count > 0)
            {
                session.ReportEnemyDamage(local.Module, generation, dealt);
            }
        }
    }

    /// <summary>Owner side: every peer's cumulative damage, folded into one monotonic total
    /// per enemy for our own engine to spend.</summary>
    private readonly PeerDamageLedger _peerDamage = new();

    /// <summary>
    /// Reconciles the session's ground-item truth with the game: the bridge materialises
    /// whatever the store says is on the ground, and anything the game reports as picked
    /// up locally is announced back so it vanishes for everyone else too.
    /// </summary>
    private void PumpItems(in PlayerState local, SessionCapabilities caps)
    {
        if (!bridge.SupportsGroundItems || (caps & SessionCapabilities.GroundItems) == 0)
        {
            return;
        }

        List<GroundItem> items = session.Items.Snapshot();
        string localModule = local.Module;

        IReadOnlyList<GroundItem> visible = settings.FilterByModule && localModule.Length > 0
            ? [.. items.Where(i => i.Module.Length == 0
                || string.Equals(i.Module, localModule, StringComparison.OrdinalIgnoreCase))]
            : items;

        bridge.TryWriteGroundItems(visible);

        foreach (uint itemId in bridge.CollectVanishedItems())
        {
            session.RemoveItem(itemId);
        }
    }
}
