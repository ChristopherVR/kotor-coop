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
    /// How often the combat channels run: enemy presence, phantom damage both ways, and the
    /// party mirror. These used to ride the heavy pass, which meant a mirrored enemy moved
    /// on one-second-old truth — it covered a full second of running in one hop, and the
    /// plugin's smoother turned that hop into a visible glide across the room. Forwarded
    /// damage waited the same second. None of these reads is a world scan on the scale the
    /// heavy gate exists for: the enemy census is one pass over the creature table, the
    /// rest are shared-block reads, so a few times a second is affordable where the
    /// hundreds-of-milliseconds world-state passes are not.
    /// </summary>
    public int CombatPumpIntervalMs { get; init; } = 150;

    /// <summary>
    /// When true, remote players in a different module are not written into the game.
    /// KOTOR has exactly one module loaded at a time, so a peer on Taris has no
    /// meaningful position in your copy of the Endar Spire.
    /// </summary>
    public bool FilterByModule { get; init; } = true;

    /// <summary>
    /// While a peer's <c>PlayerState.FlagInMinigame</c> is set (a swoop race or a forced
    /// turret sequence), park their phantom instead of mirroring it — the same "not
    /// meaningfully in this world right now" treatment <see cref="FilterByModule"/> already
    /// gives a peer standing somewhere else. coop.ini <c>minigame_hide_peer</c> (default on).
    /// </summary>
    public bool HidePeersInMinigame { get; init; } = true;

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

    /// <summary>
    /// When true, an item a reward node places into the LOCAL conversation owner's inventory
    /// is diffed and published so the other peer's engine receives the same item through the
    /// give-loot cheat channel — docs/research/dialogue-consequences.md §3. coop.ini
    /// <c>dialogue_item_sync</c> (default on).
    /// </summary>
    public bool DialogueItemSync { get; init; } = true;

    /// <summary>
    /// When true, the dialogue channel (<see cref="CoopAgent.PumpDialogue"/>) runs on the
    /// real-time (every-poll-tick) path instead of the <see cref="CombatPumpIntervalMs"/>
    /// cadence, and an unchanged read is deduped against a resend rather than republished on
    /// a fixed timer — see <see cref="KotorCoop.Protocol.DialogueTickGate"/> and
    /// docs/research/dialogue-join-latency.md. coop.ini <c>dialogue_fast_join</c> (default off,
    /// so a live pass can A/B it against the existing cadence).
    /// </summary>
    public bool DialogueFastJoin { get; init; }
    /// When true, a follower acts on a host's <see cref="TransitionAnnouncePacket"/> the
    /// instant it arrives — starting its own warp toward the announced destination without
    /// waiting for the host's published <c>PlayerState.Module</c> to change or for
    /// <see cref="ModuleSettleMs"/> to elapse. Off by default, live-verification pending:
    /// docs/research/transition-smoothness.md's live-test script is what turns this on for a
    /// measured run. When off, or whenever no usable announce has arrived, the existing
    /// host-module-based follow (above) is unaffected — it is the fallback this was built to
    /// have, not a path this flag disables. coop.ini <c>transition_announce</c>.
    /// </summary>
    public bool TransitionAnnounceEnabled { get; init; }

    /// <summary>
    /// When true, a CLIENT's own engine-native door crossing (the player walking into a
    /// linked door, as opposed to a session-commanded warp) is refused early — before the
    /// load starts — whenever <see cref="FollowHostModule"/> is on and the host has not been
    /// seen transitioning recently. Off by default, live-verification pending (the
    /// cancellation itself is best-effort — see <c>EngineHandles.TryClearTransitionPending</c>
    /// — safe either way, but unproven). coop.ini <c>client_door_refusal</c>.
    /// </summary>
    public bool ClientDoorRefusalEnabled { get; init; }

    /// <summary>
    /// Turns a module resref into the name shown on screen ("Taris - Lower City" rather than
    /// "tar_m03aa"), for the "following &lt;host&gt; to &lt;module&gt;" line
    /// <see cref="TransitionAnnounceEnabled"/>'s fast path posts. Null (the default) leaves the
    /// resref as-is — <see cref="CoopAgent"/> is deliberately game-agnostic and has no reader
    /// for a game's files of its own; <c>KotorCoop.Agent.Program</c> wires this to
    /// <c>KotorCoop.Memory.GameFiles.ModuleNames.TryRead</c>, which does. A failed lookup must
    /// still show something, so the delegate is expected to fall back to its own input.
    /// </summary>
    public Func<string, string>? ModuleDisplayName { get; init; }
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

    /// <summary>TEMPORARY (live stall investigation): throttles the CanWriteRemotes=false
    /// diagnostic to once/sec.</summary>
    private long _lastCanWriteRemotesDiagMs;

    private long _lastHeavyPumpMs;
    private long _lastCombatPumpMs;
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

    /// <summary>How long a pump tick takes, averaged over the last 100, and the worst of
    /// them — against the budget one tick has. Surfaced for the readout rather than printed,
    /// because the readout owns the console.</summary>
    public double PumpAverageMs { get; private set; }

    public double PumpPeakMs { get; private set; }

    public double PumpBudgetMs { get; private set; }

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
                    // ⚠ PUBLISHED, never printed. This used to Console.WriteLine straight
                    // onto a screen the dashboard redraws in place, so the line appeared at
                    // the cursor and was painted over a fraction of a second later — seen as
                    // the terminal "flickering, showing a [pump] log then disappearing".
                    // Anything worth saying while the readout owns the console has to go
                    // through the readout.
                    PumpAverageMs = sumMs / ticks;
                    PumpPeakMs = maxMs;
                    PumpBudgetMs = budgetMs;
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
            // TEMPORARY DIAGNOSTIC (live stall investigation) - throttled to once/sec so it
            // doesn't flood the log while stuck here every tick.
            long diagNowMs = Environment.TickCount64;
            if (diagNowMs - _lastCanWriteRemotesDiagMs >= 1000)
            {
                _lastCanWriteRemotesDiagMs = diagNowMs;
                Log?.Invoke("stall diag: CanWriteRemotes=false, blocked before the combat pump - retrying plugin block lookup on the bridge's own cadence");
            }

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

        // A peer inside the engine's own minigame mode (swoop race, forced turret sequence)
        // is not meaningfully standing in this module either, even though their published
        // Module string still names it — same "park, do not mirror" treatment as the module
        // filter above, and the same idiom: simply absent from what TryWriteRemotePeers
        // writes, which is what already deactivates a slot for a peer elsewhere. See
        // PlayerState.FlagInMinigame and docs/research/minigames.md.
        visible = settings.HidePeersInMinigame
            ? [.. visible.Where(p => !p.State.IsInMinigame)]
            : visible;

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

        // BEFORE PumpTravel, and that ordering is the whole point: a follow decided this tick
        // must carry the cinematic announced this tick. Below it, every announcement would be
        // one tick late — which happens to work in the field, because a movie buys seconds of
        // slack, and is exactly the kind of accidental correctness that breaks silently the
        // first time a transition carries no movie.
        PumpTransitionMovies(local);

        // Same ordering rule, for the same reason: PumpTravel's fast path must see this
        // tick's announce, not last tick's.
        PumpTransitionAnnounce(local);

        // Pause and travel are cheap flag checks and must feel immediate.
        PumpTravel(local, peers);
        PumpPause(local, visible);

        // Deliberately NOT inside PumpPause: that returns early when pause sharing is
        // switched off, and combat state is not an optional courtesy — it decides which
        // ruleset each engine is running.
        PumpSharedCombat(visible);

        // Death is arbitrated every tick too: a rescue window measured in seconds cannot
        // wait for the ~5 Hz heavy pass, and a swallowed game-over must be answered before
        // the engine tries again on the next frame.
        // ⚠ ALL peers, not `visible`. `visible` has already been filtered to the local
        // module, so a peer one module away is absent rather than standing - and "everyone is
        // down" then becomes true while they are alive and on their way. Two players hid this
        // because the client follows the host's module; three do not.
        PumpDeath(local, peers);

        // Client-only, every tick: keep the player's input locked to their own PC (a cheap id
        // write) so an attempted switch to a shared, host-owned companion reverts within a frame
        // rather than waiting for the ~5 Hz heavy party pass. Host owns the companions; it is free
        // to control them.
        if (settings.SyncParty && bridge.SupportsPartySync && !session.IsEnemyAuthority
            && (caps & SessionCapabilities.Party) != 0)
        {
            bridge.LockControlToPlayer();
        }

        // --- combat path: a few times a second -------------------------------------------
        //
        // Enemy presence, damage in both directions, and the party mirror. Fast enough that
        // a mirrored enemy's movement reads as movement (at the heavy pass's 1 Hz it read as
        // a lurch covering a second of running per hop, smoothed into a glide), and that a
        // hit taken through a phantom lands in the same beat of the fight it happened in —
        // but still gated, because the enemy census is a creature-table scan and does not
        // belong on the 30 Hz position path.
        long combatNowMs = Environment.TickCount64;
        if (combatNowMs - _lastCombatPumpMs >= settings.CombatPumpIntervalMs)
        {
            _lastCombatPumpMs = combatNowMs;
            PumpEnemies(local, visible, caps);
            PumpCreatureLiveness(local, caps);
            PumpPhantomDamage(caps);
            PumpPeerEffects(caps);
            PumpMaterialisedLoot(local.Module);
            PumpParty(caps);
            PumpPartyRoster(caps);

            // ⚠ DIALOGUE RODE HERE TOO, ONCE. It is the same kind of traffic — small block
            // reads, no world scan — and sharing this 150 ms gate with the enemy census (which
            // genuinely needs it) was the reason a followed conversation felt second-hand: the
            // owner's partner tag, line and node crossed agent→wire at this cadence and
            // wire→block at this SAME cadence again on the far side, so a line the owner had
            // already heard could take up to two gates' worth to reach the follower's screen —
            // see docs/research/dialogue-join-latency.md for the measured numbers. Left as the
            // fallback here (settings.DialogueFastJoin off) so turning the flag off is a true
            // rollback to exactly this cadence; the fast path below is where it moves to when
            // the flag is on.
            if (!settings.DialogueFastJoin)
            {
                PumpDialogue(local, caps);
            }

            PumpInteractionLocks(local);
            PumpBarks(local);
        }

        // Dialogue, on the fast path: every poll tick (as fast as the position write above),
        // gated internally by DialogueTickGate rather than by this method's own cadence — a
        // genuine change is acted on the very next tick, and an unchanged read costs nothing
        // beyond the block read itself. coop.ini dialogue_fast_join=1. See
        // docs/research/dialogue-join-latency.md for why this is the biggest single latency
        // win available in the join path, and DialogueTickGate for the dedup rule.
        if (settings.DialogueFastJoin)
        {
            PumpDialogue(local, caps);
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
        PumpProgression(caps);
        PumpWorldState(caps);
        PumpJournal(caps);
        PumpDoors(caps);
        PumpPlaceables(caps);
        PumpContainerLoot(caps);
    }

    /// <summary>The bark serial this agent has already published, per plugin ring slot, so a
    /// line is sent once however long it sits in the block.</summary>
    private readonly Dictionary<uint, uint> _publishedBarks = [];

    /// <summary>
    /// Carries NPC barks both ways. A bark never enters the dialogue system, so before this
    /// one player heard the utility droid beep and everybody else got silence.
    ///
    /// Keyed on the SPEAKER's object id rather than a ring index: the plugin's ring slot for
    /// a given line is an implementation detail, while "the last serial seen from this
    /// creature" is the thing that decides whether a line is new.
    /// </summary>
    private void PumpBarks(in PlayerState local)
    {
        if (!bridge.CanWriteRemotes || IsFallbackModuleName(local.Module) || local.Module.Length == 0)
        {
            return; // mid-transition: the ids a bark names are about to be reassigned
        }

        foreach (BarkRecord bark in bridge.ReadBarks())
        {
            if (_publishedBarks.TryGetValue(bark.Speaker, out uint published) && published == bark.Serial)
            {
                continue;
            }

            _publishedBarks[bark.Speaker] = bark.Serial;
            session.PublishBark(local.Module, bark.Speaker, bark.Text, bark.SoundResRef, bark.Flags, bark.Serial);
        }

        foreach (BarkPacket bark in session.DrainBarks(local.Module))
        {
            bridge.TryWriteBark(bark.Speaker, bark.Text, bark.SoundResRef, bark.Flags, bark.Serial);
        }
    }

    // ---- transition cinematics -------------------------------------------------------
    //
    // Both players should see the same film. The follower is not gated — its move-to-module
    // queue is empty, and the engine plays what is in the queue — so all that has to travel
    // is the names. See docs/research/cinematics.md §3.2.2 and §9.

    /// <summary>The cinematic serial this agent has already announced, so the same
    /// transition is published once however long its names sit in the block.</summary>
    private uint _announcedCinematicSerial;

    /// <summary>How many more ticks to repeat the current announcement. Cheap redundancy in
    /// place of an ack: the channel is unreliable by design, and a single dropped datagram
    /// would otherwise cost the follower its cinematic. There are seconds of slack between
    /// the announcement and the follow, so a handful of ticks is ample.</summary>
    private int _announceRepeatsLeft;

    private const int AnnounceRepeats = 5;

    /// <summary>
    /// The movies a peer's transitions have announced since this agent last travelled —
    /// ACCUMULATED, not replaced.
    ///
    /// The accumulation is the whole point on the canonical chain. Endar Spire → Taris is two
    /// transitions through the stunt module STUNT_00, and the follow logic correctly refuses
    /// to enter a `stunt*` module. So the follower declines the first transition (whose movie
    /// is <c>01c</c>), declines nothing at the second, and finally warps to <c>tar_m02af</c>
    /// (whose movie is <c>01f</c>) — and the transition it actually makes must carry BOTH.
    ///
    /// Bounded by three things, none of which is a timeout: it is spent when a travel request
    /// is issued, cleared when this player is already with the host (nothing is pending), and
    /// capped at the engine's own six. A timeout was considered and rejected — the stunt
    /// vision it exists to span runs for a minute or more, so any timeout short enough to
    /// bound a hoard is also short enough to drop <c>01c</c> before <c>01f</c> arrives, which
    /// is precisely the case the accumulation was written for.
    /// </summary>
    private readonly List<string> _pendingFollowMovies = [];

    /// <summary>
    /// Whether we have observed the host NOT in our module at least once since
    /// <see cref="_pendingFollowMovies"/> started filling from empty.
    ///
    /// The host announces a movie the instant its own engine accepts it onto the transition
    /// queue — which is BEFORE the host's main thread blocks to play it, and therefore before
    /// the host's published <c>PlayerState.Module</c> has any chance to change. For the entire
    /// window between "announced" and "the host's module update finally lands" (which can be
    /// the whole length of the movie, since a blocked main thread publishes nothing), this
    /// player and the host still read as being in the SAME module. Comparing OUR OWN module
    /// against a snapshot of itself cannot tell that window apart from a later, genuine
    /// reunion — we rarely move in between, so the snapshot trivially still matches either
    /// way. What actually distinguishes them is whether the HOST was ever seen elsewhere in
    /// between: if not, "together" just means the host has not left yet; if so, a later
    /// "together" means it came back and the movies are truly stale. Measured live: the
    /// "pending for the next follow" and "dropping ... we are already where the host is" lines
    /// landed in the same logged second, before the host had been seen away even once.
    /// </summary>
    private bool _pendingFollowMoviesSawHostAway;

    /// <summary>The last cinematic serial taken from each peer, so a repeated announcement is
    /// accumulated once rather than five times.</summary>
    private readonly Dictionary<uint, uint> _seenCinematicSerials = [];

    /// <summary>
    /// The most recent moment ANY signal from the host that "the host is transitioning right
    /// now" was seen — a <see cref="TransitionAnnouncePacket"/> or a
    /// <see cref="TransitionMoviePacket"/>, whichever arrives. Feeds
    /// <see cref="ClientDoorRefusalRule"/>: a client crossing the SAME door the host just led
    /// through must not be refused, and this is how the arm decision tells that case apart
    /// from a genuinely premature crossing.
    ///
    /// Initialised far in the past rather than <see cref="long.MinValue"/> deliberately:
    /// <c>SessionClock.NowMs - _lastHostTransitionSignalAtMs</c> is plain unchecked
    /// subtraction, and NowMs sitting near zero against MinValue overflows. A million seconds
    /// (~11.5 days) is "never happened" for any session that has ever run, with no overflow
    /// risk against any realistic clock value.
    /// </summary>
    private long _lastHostTransitionSignalAtMs = -1_000_000_000L;

    /// <summary>
    /// How long <see cref="_lastHostTransitionSignalAtMs"/> keeps disarming the client-door
    /// refusal after the host's last transition signal. Generous rather than tight: the
    /// signal is a fade window or a movie announce, but the host's OWN transition (movie
    /// included) can run well past 25 s (docs/research/cinematics.md's 01c measured
    /// 15,531-25,000 ms), and disarming the refusal a moment too early defeats the whole
    /// feature on exactly the case it exists for.
    /// </summary>
    private const int HostTransitionSignalWindowMs = 45_000;

    // ---- transition announce (the destination, sent before transition-movies' film) --

    /// <summary>One pending host announce, or null when none is outstanding — replaced
    /// wholesale by a fresher one, never merged (unlike the movie accumulation, there is only
    /// ever one live destination).</summary>
    private readonly record struct PendingHostAnnounce(string Module, string Waypoint, uint Serial);

    private PendingHostAnnounce? _pendingAnnounce;

    /// <summary>The transition-announce serial this agent has already published, so a
    /// standing destination is not re-announced every tick.</summary>
    private uint _announcedTransitionSerial;

    /// <summary>Same redundancy shape as <see cref="_announceRepeatsLeft"/>, for the
    /// transition-announce channel.</summary>
    private int _announceRepeatsLeftForTransition;

    /// <summary>The last transition-announce serial taken from each peer.</summary>
    private readonly Dictionary<uint, uint> _seenTransitionAnnounceSerials = [];

    /// <summary>
    /// Announces our own imminent transition's destination (the plugin's
    /// <c>TransitionAnnounceWatch</c> resolves it; this only relays it), and takes the host's.
    /// Gated on <see cref="AgentSettings.TransitionAnnounceEnabled"/> so a disabled feature
    /// puts nothing new on the wire at all — the live-test script's before/after comparison
    /// depends on that.
    /// </summary>
    private void PumpTransitionAnnounce(in PlayerState local)
    {
        if (!settings.TransitionAnnounceEnabled)
        {
            return;
        }

        if (bridge.TryReadTransitionAnnounce(out uint serial, out string module, out string waypoint))
        {
            if (serial != _announcedTransitionSerial)
            {
                _announcedTransitionSerial = serial;
                _announceRepeatsLeftForTransition = AnnounceRepeats;
                Log?.Invoke($"announcing transition to '{module}'"
                    + (waypoint.Length > 0 ? $" at waypoint '{waypoint}'" : string.Empty));
            }

            if (_announceRepeatsLeftForTransition > 0)
            {
                _announceRepeatsLeftForTransition--;
                session.PublishTransitionAnnounce(module, waypoint, serial);
            }
        }

        foreach (TransitionAnnouncePacket packet in session.DrainTransitionAnnounce())
        {
            if (packet.PeerId == CoopHost.HostPeerId)
            {
                _lastHostTransitionSignalAtMs = SessionClock.NowMs;
            }

            if (_seenTransitionAnnounceSerials.TryGetValue(packet.PeerId, out uint seen) && seen == packet.Serial)
            {
                continue;
            }

            _seenTransitionAnnounceSerials[packet.PeerId] = packet.Serial;

            // Only the HOST is ever followed (PumpTravel's own rule) — an announce from
            // another client names a destination nobody here is meant to chase.
            if (packet.PeerId != CoopHost.HostPeerId || packet.Module.Length == 0)
            {
                continue;
            }

            _pendingAnnounce = new PendingHostAnnounce(packet.Module, packet.Waypoint, packet.Serial);
            Log?.Invoke($"host announced transition to '{packet.Module}'"
                + (packet.Waypoint.Length > 0 ? $" at waypoint '{packet.Waypoint}'" : string.Empty));
        }
    }

    private void PumpTransitionMovies(in PlayerState local)
    {
        // Announce our own. Deliberately NOT gated on the module being readable: this fires
        // on the frame a transition is being set up, which is exactly when the module label
        // is least trustworthy, and the names do not depend on it.
        if (bridge.TryReadTransitionMovies(out uint serial, out string[] movies))
        {
            if (serial != _announcedCinematicSerial)
            {
                _announcedCinematicSerial = serial;
                _announceRepeatsLeft = AnnounceRepeats;
                Log?.Invoke($"announcing {movies.Length} transition movie(s): {string.Join(", ", movies)}");
            }

            if (_announceRepeatsLeft > 0)
            {
                _announceRepeatsLeft--;
                session.PublishTransitionMovies(local.Module, serial, movies);
            }
        }

        // Take everyone else's.
        foreach (TransitionMoviePacket packet in session.DrainTransitionMovies())
        {
            if (packet.PeerId == CoopHost.HostPeerId)
            {
                // Same "the host is transitioning right now" signal a TransitionAnnounce
                // gives — see _lastHostTransitionSignalAtMs's own doc. A movie-carrying
                // transition with no announce (the feature disabled, or a dropped datagram)
                // must still disarm the client-door refusal for the door the host just used.
                _lastHostTransitionSignalAtMs = SessionClock.NowMs;
            }

            if (_seenCinematicSerials.TryGetValue(packet.PeerId, out uint seen) && seen == packet.Serial)
            {
                continue;
            }

            _seenCinematicSerials[packet.PeerId] = packet.Serial;
            AccumulateFollowMovies(packet.Movies);
        }
    }

    /// <summary>Adds names to the pending set, in order, without duplicates and without
    /// exceeding the engine's own six-movie queue.</summary>
    private void AccumulateFollowMovies(string[] movies)
    {
        if (_pendingFollowMovies.Count == 0)
        {
            _pendingFollowMoviesSawHostAway = false;
        }

        foreach (string movie in movies)
        {
            if (movie.Length == 0 || _pendingFollowMovies.Contains(movie, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_pendingFollowMovies.Count >= TransitionMoviePacket.MaxMovies)
            {
                Log?.Invoke(
                    $"dropping transition movie '{movie}': already holding "
                    + $"{TransitionMoviePacket.MaxMovies}, the engine's own queue limit");
                return;
            }

            _pendingFollowMovies.Add(movie);
        }

        Log?.Invoke($"transition movies pending for the next follow: {string.Join(", ", _pendingFollowMovies)}");
    }

    /// <summary>
    /// Carries hits BOTH ways for combat participation: what each peer's phantom has taken
    /// in this game goes out to that peer, and what this player owes for hits taken on
    /// their own phantom elsewhere goes into the plugin to be applied for real.
    ///
    /// This is what makes enemies dangerous to everyone rather than only to whoever owns
    /// them. Running totals, so a dropped datagram costs a tick rather than a hit.
    /// </summary>
    /// <summary>
    /// One item still owed to a target, queued for the automatic give-loot path to submit
    /// through the same arbitrated cheat channel a human's <c>giveloot</c> console command
    /// uses — see <see cref="TryDequeuePendingLoot"/>.
    ///
    /// <paramref name="TargetObjectId"/> carries TWO different id spaces depending on where the
    /// give came from, and <c>CheatController.GiveLoot</c> is what tells them apart: a
    /// materialised-loot give (docs/research/enemy-materialisation.md §6) carries the OWNER's
    /// enemy object id, translated locally via <c>MaterialisedEnemies.ToLocal</c>; a
    /// dialogue-grant give (docs/research/dialogue-consequences.md §3) carries the well-known
    /// local-player sentinel <c>SharedBlock.CheatTargetLocalPlayer</c>, which needs no
    /// translation because "the local player" means the same thing on every instance.
    /// </summary>
    public readonly record struct PendingMaterialisedLootGive(uint TargetObjectId, MaterialisedLootItem Item);

    /// <summary>
    /// A materialised enemy's real corpse can carry several items, and an ambush can kill
    /// several enemies in one wave — bounded generously past any single real encounter, so a
    /// burst is queued rather than dropped, but a queue that never stops growing (a stuck
    /// drain, a malformed peer) must not grow unbounded.
    /// </summary>
    private const int MaxPendingLoot = 256;

    private readonly Queue<PendingMaterialisedLootGive> _pendingLoot = new();

    /// <summary>The local module <see cref="PumpMaterialisedLoot"/> last issued
    /// <see cref="ICoopSession.ResetMaterialisedLootDedup"/> for. Owner enemy ids are
    /// reassigned per module load — the client-side dedup set that keys on them
    /// (<c>CoopClient._materialisedLootSeen</c>) would otherwise remember an id from the last
    /// module forever and silently swallow a same-numbered enemy's real loot here. Tracked
    /// separately from <see cref="_deathCensusModule"/>: that one is the OWNER-authority side's
    /// own census bookkeeping, seeded from a live census; this side has no census to seed from,
    /// it only needs to know when to tell the session to forget.</summary>
    private string _materialisedLootDedupModule = string.Empty;

    /// <summary>
    /// Takes the materialised-loot packets a connected host has published since the last
    /// pump and queues one give-request per item. This is deliberately the ONLY writer of
    /// <see cref="_pendingLoot"/> — actually applying an item to the game engine needs the
    /// same single-slot cheat channel a human typing <c>giveloot</c> at the console uses
    /// (docs/research/enemy-materialisation.md §6 item 3), and that channel has exactly one
    /// request in flight at a time. Rather than build a second, parallel engine-write path
    /// that could race the first, this queue is drained from OUTSIDE the session/agent pump
    /// entirely — by whatever already arbitrates that single channel (the console command
    /// dispatcher's own pending-cheat-serial check) — one request per its own tick, so an
    /// automatic give can never collide with, or be silently overwritten by, a human's.
    ///
    /// Also resets the client's dedup set on a module change (see
    /// <see cref="_materialisedLootDedupModule"/>). Gated on a clean, non-fallback module read
    /// so a brief transition-window fallback label never fires a spurious reset; a no-op on the
    /// host, which never receives this channel (<see cref="ICoopSession.ResetMaterialisedLootDedup"/>
    /// defaults to nothing there).
    /// </summary>
    private void PumpMaterialisedLoot(string module)
    {
        if (module.Length > 0 && !IsFallbackModuleName(module)
            && !string.Equals(module, _materialisedLootDedupModule, StringComparison.OrdinalIgnoreCase))
        {
            _materialisedLootDedupModule = module;
            session.ResetMaterialisedLootDedup();
        }

        foreach (MaterialisedLootPacket packet in session.DrainMaterialisedLoot())
        {
            Log?.Invoke($"materialised loot: received {packet.Items.Count} item(s) for owner 0x{packet.OwnerEnemyId:X}, queueing");

            foreach (MaterialisedLootItem item in packet.Items)
            {
                if (_pendingLoot.Count >= MaxPendingLoot)
                {
                    Log?.Invoke(
                        $"dropping materialised loot '{item.Resref}' for owner 0x{packet.OwnerEnemyId:X}: "
                        + $"already holding {MaxPendingLoot} queued gives, the drain has fallen behind");
                    return;
                }

                _pendingLoot.Enqueue(new PendingMaterialisedLootGive(packet.OwnerEnemyId, item));
            }
        }

        // The receiving half of the dialogue item-grant channel: a peer's conversation put
        // something in THEIR inventory, relayed here, still owed a give to ours. Rides the
        // SAME pending-give queue and the SAME single-slot cheat channel as materialised loot
        // — see PendingMaterialisedLootGive's own doc for the two id spaces it carries.
        foreach (DialogueGrantPacket grant in session.DrainDialogueGrants())
        {
            Log?.Invoke(
                $"dialogue grant: received {grant.Items.Count} item(s) from peer {grant.OriginPeerId} "
                + $"('{grant.PartnerTag}'), queueing");

            foreach (MaterialisedLootItem item in grant.Items)
            {
                if (_pendingLoot.Count >= MaxPendingLoot)
                {
                    Log?.Invoke(
                        $"dropping dialogue grant '{item.Resref}' from peer {grant.OriginPeerId}: "
                        + $"already holding {MaxPendingLoot} queued gives, the drain has fallen behind");
                    return;
                }

                _pendingLoot.Enqueue(new PendingMaterialisedLootGive(LocalPlayerObjectId, item));
            }
        }
    }

    /// <summary>
    /// The well-known object id of THIS instance's own local player creature —
    /// <c>0x7FFFFFFF</c>, VERIFIED live and identical on every install
    /// (docs/re/object-model.md: "Player object id is exactly 0x7FFFFFFF"). Mirrors
    /// <c>SharedBlock.CheatTargetLocalPlayer</c> in the plugin, which cannot be referenced
    /// from here — <c>KotorCoop.Plugin</c> is a NativeAOT win-x86 library nothing may
    /// reference — so the two sides agree by VALUE, the same way every other
    /// <c>0x7FFFFFFF</c> reference in this codebase already does.
    /// </summary>
    private const uint LocalPlayerObjectId = 0x7FFFFFFF;

    /// <summary>Resref counts of the local PC's inventory the last time
    /// <see cref="PumpDialogueItemGrants"/> sampled it, for the diff against the next sample.
    /// Empty means "no baseline yet" (conversation just started, or sampling is off).</summary>
    private List<(string Resref, ItemIdentity Identity)> _dialogueGrantBaseline = [];

    private bool _dialogueGrantOwning;

    private long _dialogueGrantLastSampleMs;

    /// <summary>How often the PC's inventory is actually re-swept while a conversation runs.
    /// <see cref="IGameBridge.ReadPossessedItems"/>'s own doc warns it is a full world memory
    /// sweep, "not free" — this is independent of, and much slower than, the combat-cadence
    /// tick <see cref="PumpDialogue"/> (and therefore this method) rides on, so a long
    /// conversation that grants mid-way is still caught without sweeping every ~150 ms.
    /// </summary>
    private const long DialogueGrantSampleIntervalMs = 1000;

    /// <summary>
    /// Diffs the local PC's possessed items across the window this instance owns a
    /// conversation, and publishes whatever appeared as a dialogue grant —
    /// docs/research/dialogue-consequences.md §3.2. Sampled at conversation start, every
    /// <see cref="DialogueGrantSampleIntervalMs"/> while it continues, and once more at the
    /// end, so a reward mid-conversation is caught rather than only a reward on the final node.
    ///
    /// ⚠ ONLY WHILE THIS INSTANCE OWNS THE CONVERSATION. A spectator never runs
    /// <c>HandleReply</c>, so its own inventory cannot have changed from the conversation —
    /// diffing there would be noise at best, and at worst would double-detect the owner's own
    /// grant once it arrives back over the wire (which <see cref="ICoopSession.DrainDialogueGrants"/>
    /// already filters by origin, but there is no reason to also generate the false positive).
    /// </summary>
    private void PumpDialogueItemGrants(bool ownsActiveConversation, string partnerTag)
    {
        if (!settings.DialogueItemSync)
        {
            return;
        }

        if (!ownsActiveConversation)
        {
            if (_dialogueGrantOwning)
            {
                // The conversation just ended: one last diff so a reward on the FINAL node
                // (the common case — most reward scripts run on the closing entry) is caught,
                // then the baseline is cleared so the next conversation starts fresh.
                SampleAndPublishDialogueGrant(partnerTag);
            }

            _dialogueGrantOwning = false;
            _dialogueGrantBaseline = [];
            return;
        }

        if (!_dialogueGrantOwning)
        {
            // Just started owning a conversation: snapshot now, publish nothing yet (there is
            // nothing to diff against on the very first sample).
            _dialogueGrantOwning = true;
            _dialogueGrantLastSampleMs = Environment.TickCount64;
            _dialogueGrantBaseline = [.. bridge.ReadPossessedItems(LocalPlayerIdSet)
                .Select(i => (i.Resref, i.Identity))];
            return;
        }

        long nowMs = Environment.TickCount64;
        if (nowMs - _dialogueGrantLastSampleMs < DialogueGrantSampleIntervalMs)
        {
            return;
        }

        _dialogueGrantLastSampleMs = nowMs;
        SampleAndPublishDialogueGrant(partnerTag);
    }

    /// <summary>Single-element set handed to <see cref="IGameBridge.ReadPossessedItems"/> every
    /// sample. A field rather than a fresh allocation per call — the method is on a cadence
    /// hot enough for that to matter over a long conversation.</summary>
    private static readonly HashSet<uint> LocalPlayerIdSet = [LocalPlayerObjectId];

    private void SampleAndPublishDialogueGrant(string partnerTag)
    {
        List<(string Resref, ItemIdentity Identity)> after =
            [.. bridge.ReadPossessedItems(LocalPlayerIdSet).Select(i => (i.Resref, i.Identity))];

        List<MaterialisedLootItem> gained = DialogueItemDiff.Compute(_dialogueGrantBaseline, after);
        _dialogueGrantBaseline = after;

        if (gained.Count == 0)
        {
            return;
        }

        Log?.Invoke($"dialogue grant: {gained.Count} new item(s) in the local speaker's inventory "
            + $"('{partnerTag}'): {string.Join(", ", gained.Select(i => i.Resref))}");
        session.PublishDialogueGrant(partnerTag, gained);
    }

    /// <summary>
    /// Called by whatever already arbitrates the single-slot staging-cheat channel (the
    /// command dispatcher's pending-cheat-serial check), at most once per ITS tick, so an
    /// automatic give-loot request is submitted through the exact same call path a human's
    /// typed <c>giveloot</c> takes and can never be filed alongside one in flight. Returns
    /// false with nothing queued to give.
    /// </summary>
    public bool TryDequeuePendingLoot(out PendingMaterialisedLootGive next)
    {
        return _pendingLoot.TryDequeue(out next);
    }

    private void PumpPhantomDamage(SessionCapabilities caps)
    {
        if ((caps & SessionCapabilities.Enemies) == 0)
        {
            return;
        }

        // Outbound: ask the bridge WHO is in slot i.
        //
        // ⚠ It used to index the peer list by slot number, with a comment claiming the two
        // orderings matched. They did not. The list written into the block is filtered by
        // module and masked by capability first, so it is a subsequence of GetRemotePeers();
        // slots are also sticky per peer, so a departure leaves a hole. Either alone is
        // enough to bill the wrong player for a hit — and to bill someone standing in a
        // different area entirely. Unreachable with two players (one remote peer means one
        // slot); routine with three.
        for (int slot = 0; slot < bridge.RemoteSlotCapacity; slot++)
        {
            uint peerId = bridge.PeerInSlot(slot);
            if (peerId == 0)
            {
                continue;
            }

            if (!bridge.TryReadPhantomHit(slot, out uint phantomId, out uint cumulative, out uint attacker)
                || phantomId == 0
                || cumulative == 0)
            {
                continue;
            }

            session.PublishPhantomDamage(peerId, cumulative, attacker);
        }

        // Inbound: hand the plugin the running total. Level state, written every tick —
        // the plugin tracks what it has already applied, so this cannot double-count.
        //
        // Skipped entirely while THIS player is in a minigame: they cannot be hit in a
        // turret, and there is no engine body standing in the shared world to apply it to.
        // The total already owed is not cleared here — the session's ledger keeps it, and
        // catching up once the write resumes is the same "no double-count, no drop" the
        // level-state design already gives every other consumer of GetIncomingPhantomDamage.
        // A hit taken elsewhere DURING a minigame therefore still lands once the player is
        // back in the world; see docs/research/minigames.md for why forgiving it outright
        // was left out of this pass.
        (uint owed, uint from) = session.GetIncomingPhantomDamage();
        if (owed > 0 && !bridge.IsInMinigame)
        {
            bridge.TryWriteIncomingDamage(owed, from);
        }
    }

    /// <summary>
    /// The non-damage sibling of <see cref="PumpPhantomDamage"/>: stun, paralyze, knockdown,
    /// slow, frightened, sleep, choke, Force push/whirlwind landing on a peer's phantom here,
    /// forwarded to the player it stands for. See docs/research/effect-sync.md.
    ///
    /// Unlike phantom damage this is a small EVENT ring, not a running total — a single global
    /// high-water mark on the outbound side is enough dedup: the hook's serial only ever
    /// increases, and a burst larger than the ring's 8 entries between two pumps is exactly
    /// the "one crowd-control tick lost" cost the channel accepts by design (unreliable, like
    /// barks).
    /// </summary>
    private uint _publishedPeerEffectSerial;

    private void PumpPeerEffects(SessionCapabilities caps)
    {
        if ((caps & SessionCapabilities.Enemies) == 0)
        {
            return;
        }

        // Outbound: PeerEffectRules.SelectNew is the pure "what changed" decision (unit-tested
        // in isolation); everything below it is just resolving WHO to send it to, the same
        // linkage PumpPhantomDamage relies on — TryReadPhantomHit publishes every watched
        // slot's phantom id each frame, hit or not.
        List<PeerEffectRecord> captured = bridge.ReadPeerEffectsOut();
        if (captured.Count > 0)
        {
            uint[] serials = new uint[captured.Count];
            for (int i = 0; i < captured.Count; i++)
            {
                serials[i] = captured[i].Serial;
            }

            (IReadOnlyList<uint> newIndices, uint newWatermark) =
                PeerEffectRules.SelectNew(serials, _publishedPeerEffectSerial);
            _publishedPeerEffectSerial = newWatermark;

            if (newIndices.Count > 0)
            {
                Dictionary<uint, uint> phantomToPeer = [];
                for (int slot = 0; slot < bridge.RemoteSlotCapacity; slot++)
                {
                    uint peerId = bridge.PeerInSlot(slot);
                    if (peerId != 0 && bridge.TryReadPhantomHit(slot, out uint phantomId, out _, out _)
                        && phantomId != 0)
                    {
                        phantomToPeer[phantomId] = peerId;
                    }
                }

                foreach (uint index in newIndices)
                {
                    PeerEffectRecord effect = captured[(int)index];
                    if (phantomToPeer.TryGetValue(effect.PhantomId, out uint peerId))
                    {
                        session.PublishPeerEffect(
                            peerId, (PeerEffectKind)effect.Kind, effect.DurationMs, effect.Magnitude);
                    }
                }
            }
        }

        // Inbound: hand the plugin whatever a peer's hook captured landing on OUR phantom
        // over there, oldest first, so it can replay them on the real character here.
        foreach (PeerEffectPacket effect in session.DrainPeerEffects())
        {
            bridge.TryWritePeerEffectIn(
                effect.FromPeerId, (uint)effect.Kind, effect.DurationMs, effect.Magnitude, effect.Sequence);
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
            // Hold the last snapshot rather than publishing while the host itself is in a
            // minigame — same reasoning as PumpEnemies: whatever the engine reports for the
            // companions during a swoop/turret sequence is not the shared-world party.
            if (bridge.IsInMinigame)
            {
                return;
            }

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
    /// Keeps the companion ROSTER — who is recruited and who is walking with the player — the
    /// same on both instances. <see cref="PumpParty"/> carries where the companions are; this
    /// carries which companions exist, and it is the one that was missing.
    ///
    /// The failure it fixes: a conversation that recruits or dismisses a companion calls the
    /// engine's AddPartyMember / RemovePartyMember, and those ran on whichever instance was
    /// executing the dialogue's scripts. The other instance's party table never heard, and the
    /// roster is the KEY the companion mirror resolves by — PartyPresenceController looks up
    /// "the host's NPC slot 2" in its OWN party table — so after a divergence the mirror is
    /// either driving nothing or driving the wrong body.
    ///
    /// Host-authoritative, matching every other companion channel. The host publishes a level;
    /// a client hands that level to its plugin, which reconciles through the engine's own
    /// roster calls on the frame hook. Runs on the combat pump rather than the 30 Hz tick: a
    /// roster changes a few times per playthrough, and each reconciliation is a VM dispatch.
    /// </summary>
    private void PumpPartyRoster(SessionCapabilities caps)
    {
        if (!settings.SyncParty || !bridge.SupportsPartySync || (caps & SessionCapabilities.Party) == 0)
        {
            // ⚠ CLEAR ON THE WAY OUT, do not just stop writing. A level the plugin is still
            // holding from before the channel was disabled would keep being reconciled against
            // forever — the exact "a stale authority outlives the thing that authorised it"
            // failure the lease channels are shaped to avoid.
            bridge.WriteHostPartyRoster(false, PartyRoster.Empty);
            return;
        }

        if (session.IsEnemyAuthority)
        {
            // The host: publish its own table, and never reconcile against anyone. Writing the
            // cleared word here is what makes "am I being told a roster" the plugin's whole
            // test — it does not need to know which end of the session it is on.
            if (bridge.TryReadLocalPartyRoster(out PartyRoster local))
            {
                session.PublishPartyRoster(local);
            }

            bridge.WriteHostPartyRoster(false, PartyRoster.Empty);
            return;
        }

        // A client. TryGetPartyRoster is false until the first level has arrived, and that
        // distinction is load-bearing: an empty roster and an unheard-of one are the same two
        // masks, and applying the second would dismiss the player's whole party.
        bridge.WriteHostPartyRoster(
            session.TryGetPartyRoster(out PartyRoster host), host);
    }

    /// <summary>The local gold reading the last pass, so a change made BY THE GAME can be told
    /// from one this agent wrote. -1 until the first observation, which only baselines.</summary>
    private long _lastObservedGold = -1;

    /// <summary>This instance's contribution to the shared purse: everything its own engine
    /// has put in and taken out since the ledger started.</summary>
    private uint _goldEarned;
    private uint _goldSpent;

    /// <summary>Bumped whenever the totals above restart, so the host can tell a RESET from a
    /// stale datagram. Seeded from the clock because an agent re-attaching mid-session must
    /// not reuse the epoch its predecessor's totals were counted in.</summary>
    private readonly uint _progressionGeneration = unchecked((uint)Environment.TickCount64);

    /// <summary>What each peer's ledger had already been spent against this purse, so an
    /// increase is applied once. Keyed by peer, valued by (generation, earned, spent).</summary>
    private readonly Dictionary<uint, (uint Generation, uint Earned, uint Spent)> _appliedProgression = [];

    /// <summary>
    /// Shares progression. The host's party table IS the purse; clients report what their own
    /// engine put in and took out, the host spends each increase against its table, and the
    /// resulting balance is broadcast absolutely for everyone to mirror.
    ///
    /// ⚠ GOLD AND EXPERIENCE DO NOT TRAVEL THE SAME WAY, and the asymmetry is the whole
    /// design. Gold is the one shared value that goes DOWN, so it cannot be merged as a level
    /// — max would resurrect anything anyone spent, and last-writer-wins is what stopped a
    /// client buying anything at all (their purchase was restored by the host's next
    /// snapshot). It travels as a cumulative ledger instead, the shape the damage channel
    /// already proved.
    ///
    /// Experience is the opposite. It cannot be a ledger, because both engines ALREADY grant
    /// kill XP independently: a client's mirrored enemy dies through the engine's real damage
    /// pipeline (<c>KillCreature</c> applies a lethal effect), so <c>OnApplyDeath</c> and
    /// <c>ApplyDeathExperience</c> run on the mirror exactly as on the owner. Forwarding a
    /// delta would count every kill in the game twice. It is absolute and merges by max, which
    /// carries the thing that genuinely diverges — script-granted XP, from a conversation or a
    /// stealth award only one instance ran — and leaves the rest alone.
    /// </summary>
    private void PumpProgression(SessionCapabilities caps)
    {
        if (!settings.SyncProgression
            || !bridge.SupportsProgressionSync
            || (caps & SessionCapabilities.Progression) == 0)
        {
            return;
        }

        if (!bridge.TryReadProgression(out uint gold, out uint experience))
        {
            return;
        }

        ObserveLocalGold(gold);

        if (session.IsEnemyAuthority)
        {
            PumpProgressionHost(gold, experience);
            return;
        }

        // A client: report our own contribution, then mirror whatever the host makes of it.
        session.ReportProgression(_progressionGeneration, _goldEarned, _goldSpent, experience);

        if (session.TryGetProgression(out uint hostGold, out uint hostExperience))
        {
            // Never below our own XP: a max on both ends means neither instance can pull the
            // other backwards while a report is in flight.
            if (bridge.TryWriteProgression(hostGold, Math.Max(hostExperience, experience)))
            {
                _lastObservedGold = hostGold;
            }
        }
    }

    /// <summary>
    /// The host: spend every client's outstanding contribution against the party table, then
    /// publish the result.
    /// </summary>
    private void PumpProgressionHost(uint gold, uint experience)
    {
        long purse = gold;
        uint mergedExperience = experience;

        foreach (PeerProgression report in session.GetProgressionReports())
        {
            mergedExperience = Math.Max(mergedExperience, report.Experience);

            if (!_appliedProgression.TryGetValue(report.PeerId, out (uint Generation, uint Earned, uint Spent) applied)
                || applied.Generation != report.Generation)
            {
                // First sight of this peer's ledger, or the ledger restarted. Adopt the
                // totals WITHOUT spending them: a re-attached agent re-reports everything it
                // ever counted, and applying that as new activity would hand the party a
                // second copy of a session's earnings.
                _appliedProgression[report.PeerId] = (report.Generation, report.GoldEarned, report.GoldSpent);
                continue;
            }

            // Only increases. A total that went backwards inside one generation is a
            // reordered datagram, and the next one supersedes it.
            long earned = report.GoldEarned > applied.Earned ? report.GoldEarned - applied.Earned : 0;
            long spent = report.GoldSpent > applied.Spent ? report.GoldSpent - applied.Spent : 0;

            if (earned == 0 && spent == 0)
            {
                continue;
            }

            purse += earned - spent;

            _appliedProgression[report.PeerId] = (
                report.Generation,
                Math.Max(applied.Earned, report.GoldEarned),
                Math.Max(applied.Spent, report.GoldSpent));

            Log?.Invoke(
                $"purse: peer {report.PeerId} contributed +{earned} -{spent}, party gold {gold} -> {purse}");
        }

        // The party cannot owe money: the engine's gold is unsigned, and a negative write
        // would wrap to something enormous.
        purse = Math.Clamp(purse, 0, uint.MaxValue);

        if ((purse != gold || mergedExperience != experience)
            && bridge.TryWriteProgression((uint)purse, mergedExperience))
        {
            // Our own write is not the game earning or spending; re-baseline so the next
            // observation measures only what the engine did.
            _lastObservedGold = purse;
        }

        session.PublishProgression((uint)purse, mergedExperience);
    }

    /// <summary>
    /// Turns the local gold READING into ledger entries. The engine has no "gold changed"
    /// event, so a change is inferred from the level — which means our own writes have to be
    /// excluded, or applying the purse would read as the party earning it all over again.
    /// Every write above re-baselines for exactly that reason.
    /// </summary>
    private void ObserveLocalGold(uint gold)
    {
        if (_lastObservedGold < 0)
        {
            // First sight: baseline only. Everything the party already had belongs to the
            // save, not to anyone's contribution — counting it as "earned" would have the
            // host add a whole playthrough's takings to its own purse.
            _lastObservedGold = gold;
            return;
        }

        long delta = gold - _lastObservedGold;
        _lastObservedGold = gold;

        if (delta > 0)
        {
            _goldEarned += (uint)delta;
        }
        else if (delta < 0)
        {
            _goldSpent += (uint)-delta;
        }
    }

    /// <summary>Token serial of the last dialogue line surfaced to the local overlay, so a
    /// peer's line is posted once per line rather than every tick.</summary>
    private uint _lastSpectateSerial;

    /// <summary>When the local player was last genuinely in a conversation, and who with —
    /// held across the engine's between-entries flicker (see PumpDialogue).</summary>
    private long _dialogueLastActiveMs;
    private string _dialogueHeldPartnerTag = string.Empty;

    /// <summary>The last node the local conversation was really on, held across the engine's
    /// between-entries flicker so a line change does not read as "position unknown".</summary>
    private int _dialogueHeldNode = DialogueState.NoNode;

    private uint _dialogueHeldNodeSerial;

    /// <summary>
    /// Governs PumpDialogue's work on the <see cref="AgentSettings.DialogueFastJoin"/> path:
    /// a genuine change (a new line, a new node, the conversation starting/ending, the peer's
    /// token moving) is acted on the very next poll tick, and an unchanged read is not
    /// re-published every tick just because the tick loop ran — see
    /// <see cref="KotorCoop.Protocol.DialogueTickGate"/>. The heartbeat matches the legacy
    /// cadence this replaces, so a late joiner or a lost packet still converges no slower than
    /// it always did.
    /// </summary>
    private readonly DialogueTickGate _dialogueTickGate = new(heartbeatIntervalMs: 150);

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
    /// <summary>
    /// Claims whatever the local player is interacting with, and hands back every lock anyone
    /// holds so the plugin can hold the objects IT owns still.
    ///
    /// Both directions run every tick, deliberately. The claim is a lease that expires unless
    /// refreshed, and the lock list is a level that is replaced wholesale — neither has an
    /// edge that could be dropped, which is what makes a client dying mid-conversation
    /// harmless rather than a permanently frozen NPC.
    /// </summary>
    private void PumpInteractionLocks(in PlayerState local)
    {
        bridge.TryReadLocalInteraction(out string tag, out uint kind, out bool active);

        // Sent even when the tag is empty: that is how the session learns which module the
        // locks are scoped to, and a lock names a tag, which only means anything in one.
        session.PublishInteractionClaim(
            local.Module, tag ?? string.Empty, (InteractionKind)kind, active);

        // ⚠ OUR OWN LOCK IS FILTERED OUT HERE, so the plugin can hold everything it is given
        // without needing to know its own peer id.
        //
        // It would be actively wrong to send it: the object WE are interacting with is already
        // frozen locally by the engine's own dialogue system, and re-clearing its action queue
        // every pass would be fighting the conversation rather than helping it.
        IReadOnlyList<InteractionLock> all = session.GetInteractionLocks();
        List<InteractionLock> theirs = [];
        foreach (InteractionLock held in all)
        {
            if (held.PeerId != session.LocalPeerId)
            {
                theirs.Add(held);
            }
        }

        bridge.WriteInteractionLocks(theirs);
    }

    private void PumpDialogue(in PlayerState local, SessionCapabilities caps)
    {
        if ((caps & SessionCapabilities.Dialogue) == 0)
        {
            return;
        }

        // What the local player is hearing and doing, straight from the plugin's read.
        bridge.TryReadLocalDialogue(out string speaker, out string line);
        bridge.TryReadLocalDialogueFollow(
            out string partnerTag, out uint replySerial, out int replyIndex, out byte[] pickLog,
            out int nodeIndex, out uint nodeSerial);

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

            // ⚠ The NODE is held across the flicker for the same reason as the tag, and it
            // matters more. The engine drops its "conversation active" flag for a few frames
            // BETWEEN ENTRIES, and during that gap the plugin reads no conversation and
            // publishes -1. Letting that reach the wire would tell the follower "I no longer
            // know where I am" on every single line change — precisely at the moment the
            // owner has just moved to a new node and the follower most needs to be sent
            // there. Hold the last real node instead; the serial only advances when the node
            // genuinely changed, so holding cannot re-drive anything.
            if (nodeIndex >= 0)
            {
                _dialogueHeldNode = nodeIndex;
                _dialogueHeldNodeSerial = nodeSerial;
            }
        }

        bool active = rawInDialogue
            || (_dialogueLastActiveMs > 0 && (nowMs - _dialogueLastActiveMs) < DialogueLingerMs);
        if (!active)
        {
            _dialogueHeldPartnerTag = string.Empty;
            _dialogueHeldNode = DialogueState.NoNode;
            _dialogueHeldNodeSerial = 0;
        }

        // ⚠ THE DEBOUNCE ABOVE ALWAYS RUNS, EVEN WHEN THE GATE BELOW SAYS "NOTHING TO DO".
        // _dialogueLastActiveMs/_dialogueHeldPartnerTag/_dialogueHeldNode are this instance's
        // own bookkeeping and must track the engine every tick regardless of whether anything
        // is published this tick, or the linger window would be measured against a stale
        // sample instead of the clock it is supposed to run on.
        //
        // On the fast path, an UNCHANGED fingerprint before the heartbeat is due costs nothing
        // beyond the reads already taken above — see DialogueTickGate. Off the fast path this
        // is a no-op (the flag guards it), so the legacy cadence in PumpOnce is the only gate
        // and turning dialogue_fast_join off is a true rollback.
        if (settings.DialogueFastJoin)
        {
            long fingerprint = HashCode.Combine(
                active, speaker, line, active ? _dialogueHeldPartnerTag : string.Empty,
                replySerial, replyIndex, active ? _dialogueHeldNode : DialogueState.NoNode);
            fingerprint = HashCode.Combine(
                fingerprint, active ? _dialogueHeldNodeSerial : 0u, followingPeer,
                token.Serial, token.NodeSerial, token.ReplySerial);

            if (!_dialogueTickGate.ShouldPump(nowMs, fingerprint))
            {
                return;
            }
        }

        session.PublishDialogue(
            active, speaker, line,
            active ? _dialogueHeldPartnerTag : string.Empty, replySerial, replyIndex, pickLog,
            active ? _dialogueHeldNode : DialogueState.NoNode,
            active ? _dialogueHeldNodeSerial : 0);

        // The item-grant channel needs "do WE own an active conversation right now", which is
        // exactly what `active` already means (debounced across the engine's between-entries
        // flicker) — computed once here rather than re-derived, so the two never drift apart.
        PumpDialogueItemGrants(active, active ? _dialogueHeldPartnerTag : string.Empty);

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
            followingPeer ? token.PickLog : null,
            followingPeer ? token.NodeIndex : DialogueState.NoNode,
            followingPeer ? token.NodeSerial : 0);

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
    /// Carries the quest journal both ways: local changes (advances, legitimate decreases,
    /// and removals — see docs/research/quest-sync.md) are announced to the session, and
    /// anything the session knows that this game does not is applied through the engine's
    /// own SetState (or, for a removal, the removal path), which refreshes the journal GUI.
    /// This plus the globals channel is what makes a quest one quest instead of two parallel
    /// ones.
    /// </summary>
    private void PumpJournal(SessionCapabilities caps)
    {
        if (!bridge.SupportsJournalSync || (caps & SessionCapabilities.Journal) == 0)
        {
            return;
        }

        // ⚠ ORDER MATTERS: reading the local journal comes FIRST, so the bridge's shadow
        // is primed from the game before any store entry is offered for applying. On a
        // fresh join the store fills from the host's snapshot while this game is still
        // loading; offering those entries against an unprimed shadow replayed the whole
        // journal through the engine, and the engine's SetState shows the "Journal Entry
        // Added" box even for a no-op — the popup every player saw on load-in. The bridge
        // refuses applies until it has read the journal once, and skips any entry the
        // game already has; a GENUINE change from a peer still applies and still shows
        // the popup, deliberately — the quest really moved, and the engine offers no way
        // to apply it silently (SetState has no feedback flag).
        foreach (JournalChange change in bridge.CollectChangedJournal())
        {
            if (change.Removed)
            {
                session.RemoveJournalEntry(change.QuestTag);
            }
            else
            {
                session.SetJournalEntry(change.QuestTag, change.State);
            }
        }

        foreach (KeyValuePair<string, int> entry in session.Journal.Snapshot())
        {
            // The bridge keeps its own shadow and skips entries the game already has, so
            // this is a cheap no-op almost every tick.
            bridge.TryApplyJournalEntry(entry.Key, entry.Value);
        }

        // The removal twin: any tag the session knows as gone that this game has not been
        // told about yet. Small and rare (a handful of tombstones for a whole playthrough),
        // so re-offering the full list every tick is cheap — same shape as the live-entry
        // loop above.
        foreach (string tag in session.Journal.RemovedTags())
        {
            bridge.TryRemoveJournalEntry(tag);
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

        // EITHER player may end a shared pause. A peer that released one it did not start
        // says so with FlagPauseReleased, because a release cannot be inferred from the
        // absence of FlagPaused — a mirroring player never had that flag set.
        uint peerReleases = 0;
        for (int i = 0; i < peersHere.Count; i++)
        {
            if (peersHere[i].State.HasReleasedPause)
            {
                peerReleases++;
            }
        }

        if (peerReleases > 0 && !_peerPauseReleaseSeen)
        {
            _peerPauseReleaseSeen = true;
            _remotePauseReleaseSerial++;

            if (bridge.TrySignalRemotePauseRelease(_remotePauseReleaseSerial))
            {
                Log?.Invoke("a peer ended the shared pause; releasing ours too");
            }
        }
        else if (peerReleases == 0)
        {
            // Re-arm only once the flag has cleared, so one release ends one pause however
            // many ticks the peer keeps announcing it.
            _peerPauseReleaseSeen = false;
        }
    }

    /// <summary>Serial handed to the plugin; a change is the release.</summary>
    private uint _remotePauseReleaseSerial;

    /// <summary>Whether a peer's release flag is currently being announced, so the level on
    /// the wire is consumed as a single edge.</summary>
    private bool _peerPauseReleaseSeen;

    /// <summary>Last value written to the plugin, so the block is only touched on an edge.</summary>
    private bool _sessionCombatHeld;

    /// <summary>
    /// Publishes "a remote peer in your module is fighting" so the plugin can put this
    /// engine into combat too.
    ///
    /// Deliberately NOT range-gated the way the shared pause is. Combat state is not a
    /// courtesy to whoever is standing next to you: it changes round timing, what the HUD
    /// shows, whether abilities queue as combat actions and whether auto-pause fires. Two
    /// players in the same module during the same fight must run the same ruleset, whether
    /// they are ten units apart or across the room.
    ///
    /// The local player is excluded by construction — <paramref name="peersHere"/> is remote
    /// peers only — because the plugin treats this as a claim it imposes and later releases,
    /// and a host fighting alone would otherwise force itself out of its own fight.
    /// </summary>
    private void PumpSharedCombat(IReadOnlyList<RemotePeer> peersHere)
    {
        if (!bridge.SupportsCombatSync)
        {
            return;
        }

        bool anyPeerFighting = false;
        for (int i = 0; i < peersHere.Count; i++)
        {
            if (peersHere[i].State.IsInCombat)
            {
                anyPeerFighting = true;
                break;
            }
        }

        if (anyPeerFighting != _sessionCombatHeld && bridge.TrySetSessionInCombat(anyPeerFighting))
        {
            _sessionCombatHeld = anyPeerFighting;
            Log?.Invoke(anyPeerFighting
                ? "a peer here is fighting; entering combat with them"
                : "the peers here have left combat");
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
    private void PumpDeath(in PlayerState local, IReadOnlyList<RemotePeer> peers)
    {
        if (!bridge.SupportsDeathHandling)
        {
            return;
        }

        // A wipe needs every player down — including this one. A peer in another module is
        // not part of this fight, but it is also not a corpse: it can still come back for
        // us, so it counts as standing. That is why this takes the UNFILTERED list.
        //
        // ⚠ A peer with NO MODULE is a different thing again: it has not loaded a world at
        // all (sitting at the main menu, or its agent has not resolved its player yet). It
        // cannot be down and it cannot come and rescue anybody, so it is not a participant.
        // Counting it as a standing player is how a full party wipe became impossible: an
        // unpublished avatar is all zeroes, all zeroes is finite, finite is "plausible", and
        // a plausible undowned peer holds the game-over suppression on forever.
        bool everyPeerDown = true;
        int participants = 0;

        for (int i = 0; i < peers.Count; i++)
        {
            if (peers[i].State.Module.Length == 0)
            {
                continue;
            }

            participants++;

            if (!peers[i].State.IsDowned)
            {
                everyPeerDown = false;
                break;
            }
        }

        bool allDown = local.IsDowned && participants > 0 && everyPeerDown;

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

        for (int i = 0; i < peers.Count; i++)
        {
            RemotePeer peer = peers[i];
            if (peer.State.IsDowned || !peer.IsInModule(local.Module))
            {
                continue;
            }

            float distance = peer.State.Position.DistanceTo(local.Position);
            if (distance <= ReviveRangeUnits)
            {
                _reviveRequested = true;
                bridge.RequestRevive(ReviveHitPoints);

                // The distance and both positions are part of the record: a rescue fired
                // at 76 units on tat_m20aa and was undiagnosable without them.
                Log?.Invoke(
                    $"{peer.Name} revived you ({distance:F1} units: "
                    + $"they {peer.State.Position.X:F1},{peer.State.Position.Y:F1} "
                    + $"you {local.Position.X:F1},{local.Position.Y:F1})");
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
        // Computed and re-sent every tick, ahead of every early return below: the plugin has
        // no notion of host/client and simply trusts whatever this says right now, so a stale
        // "armed" left behind by an early return here would outlive whatever justified it.
        bool isClient = !session.IsEnemyAuthority;
        bool hostRecentlyTransitioning =
            SessionClock.NowMs - _lastHostTransitionSignalAtMs < HostTransitionSignalWindowMs;
        bridge.TryArmClientDoorRefusal(ClientDoorRefusalRule.ShouldArm(
            isClient, settings.FollowHostModule, settings.ClientDoorRefusalEnabled, hostRecentlyTransitioning));

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

        // ---- the fast path: an announced transition, ahead of the settle debounce below ---
        //
        // "Announced transitions" (docs/research/transition-smoothness.md): the host confirmed
        // its own load began BEFORE its published module had any chance to change, so this can
        // race ahead of everything below it — the settle window this player's own last
        // transition opened, and the "does the host's published module already differ from
        // ours" check the slow path uses instead. Both exist to protect against firing on
        // stale or unreliable information; an announce is neither, and racing them is the
        // entire point (both loading screens overlapping instead of running back to back).
        //
        // Gated on the coop.ini flag: when off, _pendingAnnounce is never populated at all
        // (PumpTransitionAnnounce's own gate), so this is always a no-op and every following
        // module change is decided purely by the pre-existing path below, unchanged.
        if (settings.TransitionAnnounceEnabled && TryFollowAnnouncedTransition(local, peers))
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

        RemotePeer? host = FindHostPeer(peers);
        string target = host?.State.Module ?? string.Empty;

        if (target.Length == 0 || host!.IsInModule(local.Module))
        {
            // Together (or the host is unreadable): clear the debounce so the next real
            // separation acts immediately.
            _lastTravelRequest = string.Empty;

            // And drop any accumulated cinematic — but ONLY if the host was actually seen
            // somewhere else at some point since we started accumulating. The host announces a
            // movie the instant its engine accepts it onto the transition queue, which is
            // BEFORE its main thread blocks to play it and therefore before its published
            // module has any chance to change; for that whole window "together" just means the
            // host has not left yet, not that the movies are stale. Only having actually
            // observed the host away in between means this "together" is a genuine reunion and
            // these really are leftovers from an earlier, already-resolved transition.
            if (_pendingFollowMovies.Count > 0 && _pendingFollowMoviesSawHostAway)
            {
                Log?.Invoke(
                    $"dropping {_pendingFollowMovies.Count} pending transition movie(s): "
                    + "we are already where the host is");
                _pendingFollowMovies.Clear();
                _pendingFollowMoviesSawHostAway = false;
            }

            return;
        }

        // We just established the host is NOT in our module — regardless of what happens next
        // (a stunt module, an unreadable name, or a real follow), this is the observation that
        // makes a LATER "together" trustworthy rather than the same announce-before-departure
        // race the guard above exists to ignore.
        if (_pendingFollowMovies.Count > 0)
        {
            _pendingFollowMoviesSawHostAway = true;
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

        // The cinematic the host's transitions announced on the way here rides with the warp:
        // TravelController queues these immediately before Odyssey.TravelToModule, which is
        // the same path a movie was measured playing on for real (15,531 ms, run A).
        string[] movies = [.. _pendingFollowMovies];

        if (bridge.TryRequestTravel(target, string.Empty, movies))
        {
            _lastTravelRequest = target;
            _travelRetryAtMs = now + settings.FollowRetryMs;

            // Spent, not kept. A retry of the SAME follow re-sends them (the request is
            // rewritten from scratch each time), but they must not survive into a later,
            // unrelated transition.
            _pendingFollowMovies.Clear();
            _pendingFollowMoviesSawHostAway = false;

            Log?.Invoke(movies.Length > 0
                ? $"following the host to '{target}' with {movies.Length} transition movie(s): "
                  + string.Join(", ", movies)
                : $"following the host to '{target}'");
        }
    }

    private static RemotePeer? FindHostPeer(IReadOnlyList<RemotePeer> peers)
    {
        for (int i = 0; i < peers.Count; i++)
        {
            if (peers[i].PeerId == CoopHost.HostPeerId)
            {
                return peers[i];
            }
        }

        return null;
    }

    /// <summary>
    /// The fast path itself: consumes <see cref="_pendingAnnounce"/> if it names somewhere
    /// worth going, through the SAME standing gates the slow path applies to whatever module
    /// it is about to warp to (stunt, fallback name, the retry debounce) — an announced
    /// destination skips the WAIT, never the safety checks. Returns true when it has fully
    /// handled this tick's travel decision (consumed the announce, whether or not that meant
    /// actually warping) so the slow path below is skipped; false hands the tick to it
    /// unchanged.
    /// </summary>
    private bool TryFollowAnnouncedTransition(in PlayerState local, IReadOnlyList<RemotePeer> peers)
    {
        if (_pendingAnnounce is not { } announce)
        {
            return false;
        }

        if (TransitionAnnounceRule.Decide(true, announce.Module, local.Module) != TransitionFollowPath.Announced)
        {
            // Named our own module (or decoded to nothing): spent, not kept — the same rule
            // that clears the movie accumulation on a genuine reunion.
            _pendingAnnounce = null;
            return false;
        }

        // The stunt gate and the fallback-name guard apply to an announced destination
        // exactly as they do to the host's published module in the slow path below — an
        // announcement is earlier information, not different rules.
        if (announce.Module.StartsWith("stunt", StringComparison.OrdinalIgnoreCase)
            || IsFallbackModuleName(announce.Module))
        {
            _pendingAnnounce = null;
            return false;
        }

        long now = SessionClock.NowMs;
        if (string.Equals(announce.Module, _lastTravelRequest, StringComparison.OrdinalIgnoreCase)
            && now < _travelRetryAtMs)
        {
            // Already following this exact destination — the slow path (or a previous
            // announce) already fired it. Consumed, no second request.
            _pendingAnnounce = null;
            return true;
        }

        // Same cinematic-riding rule as the slow path: whatever the host's transitions
        // announced on the way here rides with this warp too.
        string[] movies = [.. _pendingFollowMovies];

        if (!bridge.TryRequestTravel(announce.Module, announce.Waypoint, movies))
        {
            return true; // consumed either way: retrying the same stale announce next tick
                         // would just be the slow path's job now
        }

        _lastTravelRequest = announce.Module;
        _travelRetryAtMs = now + settings.FollowRetryMs;
        _pendingFollowMovies.Clear();
        _pendingFollowMoviesSawHostAway = false;
        _pendingAnnounce = null;

        Log?.Invoke(
            $"following the host to '{announce.Module}' IMMEDIATELY (announced transition "
            + $"serial {announce.Serial}, not waiting for the module-change settle)"
            + (movies.Length > 0
                ? $" with {movies.Length} transition movie(s): " + string.Join(", ", movies)
                : string.Empty));

        // The on-screen line (task 2a). Best-effort: PostChatLine returns false, harmlessly,
        // on a bridge with no overlay — the warp itself does not depend on it.
        string hostName = FindHostPeer(peers)?.Name is { Length: > 0 } name ? name : "the host";
        string display = settings.ModuleDisplayName?.Invoke(announce.Module) ?? announce.Module;
        bridge.PostChatLine($"following {hostName} to {display}");

        return true;
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
    /// Carries door state both ways on the same reliable flag channel the quest globals use.
    /// A door open for one player and shut for the other is the most visible desync co-op can
    /// produce. Door values are USUALLY monotonic (closed to open, locked to unlocked,
    /// anything to destroyed), but not always — a script can swing one shut again — so this
    /// announces every genuine local change unconditionally rather than gating on an
    /// "advanced" check: <see cref="ICoopSession.SetFlag"/> mints a fresh, outranking serial
    /// per change (see <c>EventFlagStore</c>), so it already no-ops correctly on a value the
    /// store already agrees with, and never refuses a real local change in either direction.
    /// </summary>
    private void PumpDoors(SessionCapabilities caps)
    {
        if (!bridge.SupportsDoorSync || (caps & SessionCapabilities.Doors) == 0)
        {
            return;
        }

        foreach (KeyValuePair<uint, sbyte> change in bridge.CollectChangedDoorState())
        {
            // CollectChangedDoorState already only reports a genuine local transition (its
            // own shadow-diff), so every entry here is worth announcing — no separate gate.
            session.SetFlag(change.Key, change.Value);
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
            session.SetFlag(change.Key, change.Value);
        }

        bridge.TryWritePlaceableStates(session.Flags.Snapshot());
    }

    /// <summary>
    /// Keeps shared containers (chests, corpses) from duplicating loot. SYMMETRIC on both
    /// sides — a client taking an item from a chest is exactly as real as the host taking one,
    /// so both detect their own removals and publish them, and both re-declare the FULL merged
    /// set (own plus every peer's) to their own game.
    ///
    /// This used to be host-only: only the host ever called
    /// <see cref="ICoopSession.PublishContainerLoot"/>, so a client emptying a container told
    /// nobody and the host's copy stayed full — a straightforward duplication bug. The wire and
    /// the session-side store (<c>ContainerLootStore</c>) were already fully symmetric on both
    /// <c>CoopHost</c> and <c>CoopClient</c> — client→host was never missing, only this pump
    /// was one-sided.
    ///
    /// The apply-back half matters just as much as the publish half: before this fix the host
    /// branch returned immediately after publishing and never called
    /// <see cref="IGameBridge.TryWriteContainerLoot"/> at all, so a removal a CLIENT reported
    /// was recorded in the host's session store (ready to relay to other clients) but never
    /// actually deleted from the host's own copy of the container — the host could loot the
    /// same item again.
    ///
    /// Nothing here can echo. <c>ContainerLootStore.Add</c> (both host and client) reports a
    /// pair already in its set as not-new, so: (1) applying a peer's removal via
    /// <see cref="IGameBridge.TryWriteContainerLoot"/> deletes the item from this instance's
    /// container, which this instance's OWN <see cref="IGameBridge.CollectContainerLootRemovals"/>
    /// scan would otherwise rediscover as a fresh local removal a moment later — but the store
    /// already holds the pair, so the re-publish is silently swallowed; and (2) a client's own
    /// removal that the host relays back down (see <c>CoopHost.HandleContainerLoot</c>) is
    /// recognised the same way and never re-broadcast. Shares the Doors capability gate because
    /// it rests on the same object-id-across-the-same-save assumption doors do.
    /// </summary>
    private void PumpContainerLoot(SessionCapabilities caps)
    {
        if (!bridge.SupportsContainerLoot || (caps & SessionCapabilities.Doors) == 0)
        {
            return;
        }

        // Detect whatever THIS instance's player removed locally and announce it — the host's
        // own scan and a client's own scan are treated identically now.
        foreach (ContainerLootRemoval removal in bridge.CollectContainerLootRemovals())
        {
            session.PublishContainerLoot(removal.ContainerId, removal.ItemId);
        }

        // Re-declare the merged set — everyone's removals, this instance's own plus every peer
        // reported through the session — so each side's copy of every shared container
        // converges. Idempotent: an already-removed item resolves to a no-op.
        bridge.TryWriteContainerLoot(session.ContainerLootRemovals);
    }

    /// <summary>
    /// Carries KOTOR's own world state — the quest and story globals every script branches
    /// on — in both directions. This is what separates two players sharing a map from two
    /// players sharing a game.
    ///
    /// Both directions converge by per-key Lamport clock (see <c>EventFlagStore</c>), not a
    /// bare value comparison: a global can legitimately go DOWN (a kill counter decremented,
    /// a boolean cleared back to false — docs/research/quest-sync.md's module scan found
    /// 1,457 `SetGlobalNumber` call sites; direction is not recoverable from the bytecode
    /// alone, but the game data proves the operation is common), and a value-only "higher
    /// wins" policy refused those forever while also fighting the local engine's own write
    /// every tick (this loop used to gate the announce on an "advanced" check — see git
    /// history — which meant a genuine local decrease was never even applied back to
    /// <see cref="session"/>'s own store, so the very next apply-back write below pushed the
    /// STALE higher value back into the game). <see cref="ICoopSession.SetFlag"/> now mints a
    /// fresh serial per genuine local change and always wins locally, so it is called
    /// unconditionally for every entry <see cref="IGameBridge.CollectChangedWorldState"/>
    /// reports — that method's own shadow-diff already guarantees each one is a real local
    /// transition, not an echo.
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
            session.SetFlag(change.Key, change.Value);
        }

        // Apply anything the session knows that the game does not. The bridge updates its
        // own shadow copy as it writes, so the next scan does not see this as local.
        foreach (KeyValuePair<uint, sbyte> flag in session.Flags.Snapshot())
        {
            bridge.TryApplyWorldState(flag.Key, flag.Value);
        }
    }

    /// <summary>Owner-authority side only: enemy ids read as alive on the last clean census in
    /// <see cref="_deathCensusModule"/>. An id that drops out of this set with a
    /// <c>Dead</c> reading is a genuine death; one that drops out silently (out of census
    /// range, cap-shed) is not — see <see cref="DetectMaterialisedDeaths"/>.</summary>
    private readonly HashSet<uint> _knownAliveEnemies = [];

    /// <summary>The module <see cref="_knownAliveEnemies"/> was built for. Object ids are
    /// reassigned per module load, so a module change must clear the set rather than reading
    /// last module's ids as freshly dead this module.</summary>
    private string _deathCensusModule = string.Empty;

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

        // ⚠ The module label must be read in the same breath as the world data it labels.
        // `local.Module` is RETAINED across a failed read, and the census and damage
        // channels read or write the LIVE world — so around a module transition the two
        // can belong to different worlds. Measured on the tar_m03aa warp: for a few
        // seconds after arrival the owner published a fresh Taris census labelled
        // END_M01AA, which sailed through the module gate of a client genuinely standing
        // on the Endar Spire and drove Taris ids onto its creatures. A skewed or
        // unreadable label skips the world-labelled operations below — they are levels,
        // so the next clean tick republishes them. (The encounter-set publish is NOT
        // gated on this: it must keep superseding with an empty set on a fallback name.)
        string censusModule = bridge.TryReadCurrentModule(out string liveModule)
            ? liveModule
            : local.Module;
        bool moduleClean = censusModule.Length > 0
            && !IsFallbackModuleName(censusModule)
            && string.Equals(censusModule, local.Module, StringComparison.OrdinalIgnoreCase);

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
            // The owner's own engine fires its own encounters; only the peers that had
            // theirs disarmed need the watch.
            bridge.SetEncounterWatch(false);
            PumpEncounterRequests(local);
            bridge.SetTriggerWatch(false);
            PumpTriggerRequests(local);

            // Census and damage carry the module label; a dirty label skips them (see the
            // same-breath note above).
            if (!moduleClean)
            {
                return;
            }

            // Hold the last snapshot rather than publishing while in a conversation. The
            // dialogue system wipes every hostile's action queue in this instance on every
            // tick, so what we would send is a pacified fight — and the peer, who is not
            // talking, would have their own fight go quiet for the duration.
            //
            // The same holds for a minigame: whatever the engine's creature census reports
            // during a swoop race or a turret sequence is not the shared-world fight, and
            // publishing it would send every peer's mirror chasing a fight that is not
            // happening. See PlayerState.FlagInMinigame and docs/research/minigames.md.
            if (!bridge.IsInDialogue && !bridge.IsInMinigame)
            {
                List<EnemyState> ownedEnemies = bridge.ReadEnemies(otherPlayers);
                session.PublishEnemies(censusModule, ownedEnemies);
                DetectMaterialisedDeaths(censusModule, ownedEnemies);
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
                _peerDamage.Merge(censusModule, session.GetEnemyDamage(censusModule)));

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

        // Disarmed does not have to mean unreachable. The plugin watches which activate
        // areas this player is standing in and the owner fires them on our behalf, so
        // walking into an ambush works for whoever walks in — see
        // docs/research/client-encounters.md.
        bridge.SetEncounterWatch(true);
        session.PublishEncounterTriggers(
            local.Module,
            // "areaXX" means the module name was unreadable, which means a transition is
            // mid-flight — the same guard PumpTravel needs. Reporting then would name this
            // area's encounter ids under a module string that matches nothing, and the ids
            // themselves are about to be reassigned. Report an empty set instead of
            // reporting nothing, so a set published a moment ago is superseded rather than
            // left to age out on the host.
            IsFallbackModuleName(local.Module) ? [] : bridge.ReadEnteredEncounters());

        // Generic triggers, same shape, same fallback-module guard. The plugin's own watch
        // already excludes traps, area transitions and anything failing the coop.ini
        // allowlist pattern, so everything the bridge hands back here is a candidate story
        // trigger. One more filter happens here, purely to save bandwidth: a trigger id the
        // host has already told us it fired (GetFiredTriggerIds) is dropped before it is
        // even reported again — the host's own store would no-op it anyway, but there is no
        // reason to keep asking.
        bridge.SetTriggerWatch(true);
        if (IsFallbackModuleName(local.Module))
        {
            session.PublishTriggerEntries(local.Module, []);
        }
        else
        {
            IReadOnlyList<uint> entered = bridge.ReadEnteredTriggers();
            if (entered.Count > 0)
            {
                IReadOnlyList<uint> alreadyFired = session.GetFiredTriggerIds(local.Module);
                entered = alreadyFired.Count == 0
                    ? entered
                    : [.. entered.Where(id => !alreadyFired.Contains(id))];
            }

            session.PublishTriggerEntries(local.Module, entered);
        }

        // ROUND 41 DIAGNOSTIC: settling whether the moduleClean gate is what's silently
        // skipping the exclusion filter downstream. Temporary.
        Log?.Invoke(
            $"local combat diag (agent): moduleClean={moduleClean} censusModule='{censusModule}' "
            + $"local.Module='{local.Module}'");

        // The enemy apply and the damage report are world-labelled; a dirty label skips
        // them (see the same-breath note above).
        if (!moduleClean)
        {
            return;
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
        //
        // ROUND 39: the plugin's own local_combat exclusion filters here, not before — the
        // list itself must still exist upstream (the owner keeps publishing normally; only
        // THIS instance's own presence array omits it), so a bounded timeout or the fight
        // ending cleanly resumes normal mirroring on the very next pump with no separate
        // "un-exclude" round trip. Genuinely absent from the array is round 37/38's proven
        // mechanism; filtering here (not skipping DriveOne per-creature) is what makes that
        // true rather than an approximation of it.
        IReadOnlyList<EnemyState> enemies = session.GetEnemies(local.Module);
        uint excludedId = bridge.ReadLocalCombatExcludedId();
        if (excludedId != 0)
        {
            List<EnemyState> filtered = new(enemies.Count);
            foreach (EnemyState enemy in enemies)
            {
                if (enemy.ObjectId != excludedId)
                {
                    filtered.Add(enemy);
                }
            }

            // ROUND 41 DIAGNOSTIC: the plugin still sees the excluded id in its own driven
            // list despite this filter existing. Confirm whether this branch even runs, and
            // with what values, before assuming the bug is somewhere else. Temporary.
            Log?.Invoke(
                $"local combat diag (agent): excludedId=0x{excludedId:X} enemies.Count={enemies.Count} "
                + $"filtered.Count={filtered.Count} (removed {enemies.Count - filtered.Count})");

            enemies = filtered;
        }

        bool wrote = bridge.TryWriteEnemies(enemies);
        if (excludedId != 0)
        {
            Log?.Invoke($"local combat diag (agent): TryWriteEnemies returned {wrote}, wrote {enemies.Count} enemies");
        }

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

    /// <summary>
    /// Owner-authority side: notices which enemies just died and captures their real corpses'
    /// actual loot for <see cref="ICoopSession.PublishMaterialisedLoot"/> — the automatic wire
    /// docs/research/enemy-materialisation.md §6 was still missing. A death is an id dropping
    /// out of <see cref="_knownAliveEnemies"/> WITH a <c>Dead</c> reading, not merely dropping
    /// out of the census — the census sheds by distance and cap, and a creature that is merely
    /// far away must never be read as a kill.
    ///
    /// One <see cref="IGameBridge.ReadPossessedItems"/> sweep for however many died on this
    /// tick, not one per creature — see that method's own doc for why a per-death call would
    /// be a real cost against an ambush wave dying together.
    /// </summary>
    private void DetectMaterialisedDeaths(string module, IReadOnlyList<EnemyState> enemies)
    {
        if (!string.Equals(module, _deathCensusModule, StringComparison.OrdinalIgnoreCase))
        {
            // A fresh module: ids are reassigned per load, so anything remembered from the
            // last one names a different creature now (or nothing at all). Seed from this
            // census rather than reading every currently-alive id as freshly dead.
            _deathCensusModule = module;
            _knownAliveEnemies.Clear();
            foreach (EnemyState enemy in enemies)
            {
                if (!enemy.Dead)
                {
                    _knownAliveEnemies.Add(enemy.ObjectId);
                }
            }

            return;
        }

        HashSet<uint>? newlyDead = null;
        foreach (EnemyState enemy in enemies)
        {
            if (enemy.Dead)
            {
                if (_knownAliveEnemies.Remove(enemy.ObjectId))
                {
                    (newlyDead ??= []).Add(enemy.ObjectId);
                }
            }
            else
            {
                _knownAliveEnemies.Add(enemy.ObjectId);
            }
        }

        if (newlyDead is null)
        {
            return;
        }

        Log?.Invoke($"materialised loot: {newlyDead.Count} enemy death(s) noticed this pass: "
            + string.Join(", ", newlyDead.Select(id => $"0x{id:X}")));

        Dictionary<uint, List<MaterialisedLootItem>> byOwner = [];
        foreach ((uint possessor, string resref, ItemIdentity identity) in
            bridge.ReadPossessedItems(newlyDead))
        {
            if (resref.Length == 0)
            {
                continue;
            }

            if (!byOwner.TryGetValue(possessor, out List<MaterialisedLootItem>? items))
            {
                items = [];
                byOwner[possessor] = items;
            }

            items.Add(new MaterialisedLootItem(resref, identity));
        }

        foreach (uint deadId in newlyDead)
        {
            if (byOwner.TryGetValue(deadId, out List<MaterialisedLootItem>? items) && items.Count > 0)
            {
                session.PublishMaterialisedLoot(deadId, items);
                Log?.Invoke($"materialised loot: captured {items.Count} item(s) off 0x{deadId:X}'s corpse, publishing");
            }
            else
            {
                Log?.Invoke($"materialised loot: 0x{deadId:X} died with nothing to capture");
            }
        }
    }

    // ---- creature liveness reconciliation ---------------------------------------------
    //
    // USER-REPORTED CONCERN: "we don't want instances of NPCs alive in one host and not
    // another." Today creature death is host-authoritative for the 32-slot combat census
    // (CombatController mirrors owner health and kills), but several paths leave a body alive
    // on one side and dead/gone on the other: beyond the census cap, killed by SCRIPT
    // (DestroyObject, a cutscene death, SetPlotFlag then a kill), killed while the peers were
    // in different modules or disconnected, or killed by the client's own engine locally under
    // local_combat before the host's engine confirmed. See docs/research/creature-liveness.md.

    /// <summary>Owner-authority side only: last known state per id in
    /// <see cref="_livenessCensusModule"/>, from the full (never capped, never distance-shed)
    /// area scan — unlike the 32-slot combat census, an id that drops out of THIS scan really
    /// is gone, not merely far from a player.</summary>
    private readonly Dictionary<uint, CreatureLivenessState> _livenessKnown = [];

    private string _livenessCensusModule = string.Empty;

    /// <summary>Owner-authority side only: ids reported Gone, and when this pass first
    /// noticed. Retained past detection so a lost broadcast cycle does not cost a peer the
    /// verdict forever; see <see cref="LivenessGoneRetentionMs"/>.</summary>
    private readonly Dictionary<uint, long> _livenessGoneSince = [];

    private long _lastLivenessScanMs;

    /// <summary>How often the full-area liveness scan actually runs. Matches the design doc's
    /// "once per N seconds (2s) per module" — independent of, and much less frequent than, the
    /// 150ms combat cadence this method is called on, because <see cref="IGameBridge.ReadCreatureLiveness"/>
    /// walks every creature in the module, not a shared-block read.</summary>
    private const int LivenessScanIntervalMs = 2000;

    /// <summary>How long a "gone" verdict is still worth reporting after the cycle that first
    /// detected it. Generous headroom past a few missed 2-second broadcast cycles without
    /// holding every destroyed id in the module for the rest of the session.</summary>
    private const long LivenessGoneRetentionMs = 15_000;

    /// <summary>
    /// Moves creature-liveness state in whichever direction this peer is responsible for —
    /// the same owner/mirror split <see cref="PumpEnemies"/> uses, but deliberately NOT gated
    /// on <c>IsInDialogue</c>/<c>IsInMinigame</c> on the owner side: those are exactly when a
    /// SCRIPT is most likely to kill or destroy something, and catching that is this channel's
    /// whole reason to exist. The 32-slot combat census pauses there because a paused census
    /// would drive a pacified fight onto a peer; this channel drives nothing at all, it only
    /// reports and (on the mirror) corrects a body that has fallen out of step.
    /// </summary>
    private void PumpCreatureLiveness(in PlayerState local, SessionCapabilities caps)
    {
        if (!bridge.SupportsEnemySync || (caps & SessionCapabilities.Enemies) == 0)
        {
            return;
        }

        // Same same-breath module discipline PumpEnemies uses: the label and the data it
        // describes must come from the same read, or a stale label can name entries that
        // belong to a different world entirely — see that method's own note on the
        // tar_m03aa warp this guards against.
        string censusModule = bridge.TryReadCurrentModule(out string liveModule) ? liveModule : local.Module;
        bool moduleClean = censusModule.Length > 0
            && !IsFallbackModuleName(censusModule)
            && string.Equals(censusModule, local.Module, StringComparison.OrdinalIgnoreCase);

        if (!moduleClean)
        {
            return;
        }

        if (session.IsEnemyAuthority)
        {
            long nowMs = Environment.TickCount64;
            if (nowMs - _lastLivenessScanMs < LivenessScanIntervalMs)
            {
                return;
            }

            _lastLivenessScanMs = nowMs;
            PublishCreatureLiveness(censusModule, nowMs);
            return;
        }

        // Mirror side: hand whatever the owner published for THIS module to the plugin's
        // reconcile controller. Ordered so a truncation at the block's capacity never drops
        // the entries that actually need acting on (dead/gone) ahead of the ones that merely
        // confirm agreement (alive) — see LivenessBlockPage's own doc.
        IReadOnlyList<CreatureLivenessEntry> received = session.GetCreatureLiveness(censusModule);
        if (received.Count > 0)
        {
            (List<CreatureLivenessEntry> page, int nextCursor) =
                LivenessBlockPage.Select(received, SharedBlockLivenessCapacity, _livenessBlockCursor);
            _livenessBlockCursor = nextCursor;
            bridge.TryWriteCreatureLiveness(page);
        }

        session.ReportLivenessAnomalyCount(bridge.ReadLivenessAnomalyCount());
    }

    /// <summary>Mirrors <c>SharedBlock.LivenessCapacity</c> in the plugin — kept as a plain
    /// literal here rather than a cross-project reference, the same "shared by value, not by
    /// reference" rule the plugin's own block-layout constants already follow.</summary>
    private const int SharedBlockLivenessCapacity = 96;

    /// <summary>Rotates the "alive" tail <see cref="LivenessBlockPage"/> hands the plugin
    /// across pumps when the accumulated set does not fit the block's capacity in one pass.</summary>
    private int _livenessBlockCursor;

    /// <summary>
    /// Owner-authority side: scans the full area, derives Gone for anything that dropped out
    /// of the scan without ever going through it, and publishes the whole picture.
    /// </summary>
    private void PublishCreatureLiveness(string censusModule, long nowMs)
    {
        if (!string.Equals(censusModule, _livenessCensusModule, StringComparison.OrdinalIgnoreCase))
        {
            // Ids are reassigned per module load: anything remembered from the last one
            // names a different creature now, or nothing.
            _livenessCensusModule = censusModule;
            _livenessKnown.Clear();
            _livenessGoneSince.Clear();
        }

        List<CreatureLivenessEntry> scan = bridge.ReadCreatureLiveness();

        HashSet<uint> seenThisPass = new(scan.Count);
        foreach (CreatureLivenessEntry entry in scan)
        {
            seenThisPass.Add(entry.ObjectId);
            _livenessKnown[entry.ObjectId] = entry.State;
            _livenessGoneSince.Remove(entry.ObjectId);
        }

        // Anything remembered but not seen this pass is destroyed. Unlike the 32-slot combat
        // census, this scan is never capped or distance-shed, so an id that disappears from
        // it really is gone rather than merely far from a player.
        List<uint>? newlyGone = null;
        foreach (uint id in _livenessKnown.Keys)
        {
            if (seenThisPass.Contains(id) || _livenessGoneSince.ContainsKey(id))
            {
                continue;
            }

            (newlyGone ??= []).Add(id);
        }

        if (newlyGone is not null)
        {
            foreach (uint id in newlyGone)
            {
                _livenessGoneSince[id] = nowMs;
            }

            Log?.Invoke($"liveness: {newlyGone.Count} creature(s) newly gone this pass: "
                + string.Join(", ", newlyGone.Select(id => $"0x{id:X}")));
        }

        // A "gone" verdict past its retention window has almost certainly been delivered by
        // now; holding it forever would grow these tables without bound over a long session
        // in a module that sees a lot of destruction.
        if (_livenessGoneSince.Count > 0)
        {
            List<uint>? expired = null;
            foreach ((uint id, long since) in _livenessGoneSince)
            {
                if (nowMs - since > LivenessGoneRetentionMs)
                {
                    (expired ??= []).Add(id);
                }
            }

            if (expired is not null)
            {
                foreach (uint id in expired)
                {
                    _livenessGoneSince.Remove(id);
                    _livenessKnown.Remove(id);
                }
            }
        }

        List<CreatureLivenessEntry> full = new(scan.Count + _livenessGoneSince.Count);
        full.AddRange(scan);
        foreach (uint id in _livenessGoneSince.Keys)
        {
            full.Add(new CreatureLivenessEntry(id, CreatureLivenessState.Gone));
        }

        session.PublishCreatureLiveness(censusModule, full);
    }

    /// <summary>Owner side: every peer's cumulative damage, folded into one monotonic total
    /// per enemy for our own engine to spend.</summary>
    private readonly PeerDamageLedger _peerDamage = new();

    /// <summary>
    /// Owner side of client-triggered encounters: hand the plugin whatever encounters
    /// clients say they are standing in, so this instance's engine can decide and spawn.
    ///
    /// Only ids and peer ids cross — the decision stays with the engine, which re-tests the
    /// polygon against that peer's phantom. Written every pump rather than on a change: the
    /// list is a level, the plugin's apply is idempotent (the engine refuses an encounter
    /// that has already started), and a level costs nothing to repeat.
    /// </summary>
    private void PumpEncounterRequests(in PlayerState local)
    {
        // Mid-transition (see IsFallbackModuleName): our own ids are about to be reassigned,
        // so nothing a peer names can be resolved safely. Falls through to the drain below
        // so the request region is still cleared once.
        IReadOnlyList<EncounterFireRequest> requests = IsFallbackModuleName(local.Module)
            ? []
            : session.GetEncounterFireRequests(local.Module);
        if (requests.Count == 0 && !_encounterRequestsOutstanding)
        {
            return;
        }

        // One more write after the last request drains, so the region does not keep
        // offering a peer's stale encounter once they have walked out.
        _encounterRequestsOutstanding = requests.Count > 0;

        bridge.TryRequestEncounterFire([.. requests.Select(r => (r.EncounterId, r.PeerId))]);

        if (requests.Count > 0 && _encounterRequestsLogged != requests.Count)
        {
            _encounterRequestsLogged = requests.Count;
            Log?.Invoke($"firing {requests.Count} encounter(s) a peer walked into");
        }
        else if (requests.Count == 0)
        {
            _encounterRequestsLogged = 0;
        }
    }

    private bool _encounterRequestsOutstanding;

    /// <summary>
    /// Owner side of TriggerRelay: hand the plugin whatever GENERIC triggers clients say they
    /// are standing in, so this instance's engine runs the same OnEnter script against that
    /// peer's phantom that the client's own engine already ran against its real character.
    /// Same shape as <see cref="PumpEncounterRequests"/>, with one difference: idempotence is
    /// NOT free here (a trigger's OnEnter carries no engine one-shot flag the way an
    /// encounter's <c>started</c> field does), so <see cref="TriggerRelayStore.Collect"/>
    /// already removed anything this host has fired before — every request handed to the
    /// bridge here is genuinely new.
    /// </summary>
    private void PumpTriggerRequests(in PlayerState local)
    {
        IReadOnlyList<TriggerFireRequest> requests = IsFallbackModuleName(local.Module)
            ? []
            : session.GetTriggerFireRequests(local.Module);
        if (requests.Count == 0 && !_triggerRequestsOutstanding)
        {
            return;
        }

        _triggerRequestsOutstanding = requests.Count > 0;

        bridge.TryRequestTriggerFire([.. requests.Select(r => (r.TriggerId, r.PeerId))]);

        if (requests.Count > 0 && _triggerRequestsLogged != requests.Count)
        {
            _triggerRequestsLogged = requests.Count;
            Log?.Invoke($"firing {requests.Count} generic trigger(s) a peer walked into");
        }
        else if (requests.Count == 0)
        {
            _triggerRequestsLogged = 0;
        }
    }

    private bool _triggerRequestsOutstanding;

    private int _triggerRequestsLogged;
    private int _encounterRequestsLogged;

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
