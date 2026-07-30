using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public sealed record AgentSettings
{
    /// <summary>How often the game's memory is sampled. Matches the network tick by default.</summary>
    public int PollRate { get; init; } = ProtocolConstants.DefaultTickRate;

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
    private long _travelRetryAtMs;

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

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                PumpOnce();
            }
        }
        catch (OperationCanceledException)
        {
        }

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

        // Only filter when both ends actually know their module. A profile that has not
        // located the module-name field reports an empty string, and treating "unknown"
        // as "somewhere else" would hide every peer and look exactly like a broken
        // network layer.
        IReadOnlyList<RemotePeer> visible = settings.FilterByModule && local.Module.Length > 0
            ? [.. peers.Where(p => p.State.Module.Length == 0 || p.IsInModule(local.Module))]
            : peers;

        LastWriteSucceeded = bridge.TryWriteRemotePeers(visible);

        // Each channel is gated on the negotiated capability set: a mod difference that
        // would corrupt a channel disables that channel for the session (a different
        // globals catalogue drops world state; a different save drops enemies/doors/items)
        // while everything unaffected keeps working. Full set until a peer is judged.
        SessionCapabilities caps = session.Capabilities;

        PumpItems(local, caps);
        PumpEnemies(local, caps);
        PumpWorldState(caps);
        PumpJournal(caps);
        PumpDoors(caps);
        PumpTravel(local, peers);
        PumpPause(peers);
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
    private void PumpPause(IReadOnlyList<RemotePeer> peers)
    {
        if (!bridge.SupportsPauseSync)
        {
            return;
        }

        bool anyPeerPaused = false;
        for (int i = 0; i < peers.Count; i++)
        {
            if (peers[i].State.IsPaused)
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

        // Never yank the player out of a conversation; the module mismatch survives
        // until the next pump anyway.
        if (bridge.IsInDialogue)
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
    /// Health is replicated absolutely rather than as damage, because each instance applies
    /// its own difficulty multipliers and resistances to a damage number.
    /// </summary>
    private void PumpEnemies(in PlayerState local, SessionCapabilities caps)
    {
        if (!bridge.SupportsEnemySync || (caps & SessionCapabilities.Enemies) == 0)
        {
            return;
        }

        if (session.IsEnemyAuthority)
        {
            // Hold the last snapshot rather than publishing while in a conversation. The
            // dialogue system wipes every hostile's action queue in this instance on every
            // tick, so what we would send is a pacified fight — and the peer, who is not
            // talking, would have their own fight go quiet for the duration.
            if (!bridge.IsInDialogue)
            {
                session.PublishEnemies(bridge.ReadEnemies());
            }

            // Where a client's damage becomes real: its copies are pinned at 1 hp, so
            // its whole fight would be cosmetic unless the host lowers its own enemies
            // to the client's floor. Min-merge — an enemy is as hurt as ANY instance
            // says it is.
            int floored = bridge.TryApplyEnemyFloors(session.GetEnemyReports(local.Module));
            if (floored > 0)
            {
                Log?.Invoke($"applied a peer's damage to {floored} enemy(ies)");
            }

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

        IReadOnlyList<EnemyState> enemies = session.GetEnemies();
        if (enemies.Count > 0)
        {
            bridge.TryWriteEnemies(enemies);
        }

        // Report our own view so damage dealt here counts. Not during dialogue — the
        // disarm makes the local fight unrepresentative, and health is the only field
        // that matters anyway.
        if (!bridge.IsInDialogue)
        {
            session.ReportEnemies(local.Module, bridge.ReadEnemies());
        }
    }

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
