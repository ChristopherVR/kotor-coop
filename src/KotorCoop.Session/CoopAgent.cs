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

        PumpItems(local);
        PumpEnemies();
        PumpWorldState();
        PumpDoors();
    }

    /// <summary>
    /// Carries door state both ways on the same reliable, max-merged flag channel the
    /// quest globals use. A door open for one player and shut for the other is the most
    /// visible desync co-op can produce; door values are monotonic (closed to open,
    /// locked to unlocked, anything to destroyed), so the merge semantics fit exactly.
    /// </summary>
    private void PumpDoors()
    {
        if (!bridge.SupportsDoorSync)
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
    private void PumpWorldState()
    {
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
    private void PumpEnemies()
    {
        if (!bridge.SupportsEnemySync)
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
    }

    /// <summary>
    /// Reconciles the session's ground-item truth with the game: the bridge materialises
    /// whatever the store says is on the ground, and anything the game reports as picked
    /// up locally is announced back so it vanishes for everyone else too.
    /// </summary>
    private void PumpItems(in PlayerState local)
    {
        if (!bridge.SupportsGroundItems)
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
