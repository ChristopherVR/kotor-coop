using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// Client-triggered encounters and generic story triggers, both directions: a peer standing in
/// an encounter or trigger polygon whose owner (always the enemy authority today) needs to fire
/// it on that peer's behalf, and protocol 36's reverse relay — a trigger any peer's engine
/// already fired natively for its own real character, replayed on every other engine that has
/// not yet been asked to run it too. Called from within <see cref="CombatPump.PumpEnemies"/>,
/// which decides who (owner or mirror) needs which half this tick. See
/// docs/research/client-encounters.md and docs/research/trigger-relay.md.
/// </summary>
internal sealed class TriggerPump(IGameBridge bridge, ICoopSession session, Action<string> log)
{
    private bool _encounterRequestsOutstanding;
    private int _encounterRequestsLogged;

    /// <summary>
    /// Owner side of client-triggered encounters: hand the plugin whatever encounters
    /// clients say they are standing in, so this instance's engine can decide and spawn.
    ///
    /// Only ids and peer ids cross — the decision stays with the engine, which re-tests the
    /// polygon against that peer's phantom. Written every pump rather than on a change: the
    /// list is a level, the plugin's apply is idempotent (the engine refuses an encounter
    /// that has already started), and a level costs nothing to repeat.
    /// </summary>
    public void PumpEncounterRequests(in PlayerState local)
    {
        // Mid-transition (see ModuleNameGuard): our own ids are about to be reassigned,
        // so nothing a peer names can be resolved safely. Falls through to the drain below
        // so the request region is still cleared once.
        IReadOnlyList<EncounterFireRequest> requests = ModuleNameGuard.IsFallbackModuleName(local.Module)
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
            log($"firing {requests.Count} encounter(s) a peer walked into");
        }
        else if (requests.Count == 0)
        {
            _encounterRequestsLogged = 0;
        }
    }

    private bool _triggerRequestsOutstanding;
    private int _triggerRequestsLogged;
    private int _nativeTriggerRequestsLogged;

    /// <summary>
    /// Every generic-trigger fire request this pump owes its own plugin, from BOTH directions,
    /// combined into the one write the bridge's fixed-size request region can carry per tick:
    ///
    /// - the OLD, host-only half (<see cref="ICoopSession.GetTriggerFireRequests"/>): whatever
    ///   GENERIC triggers clients say they are standing in, so THIS instance's engine (always
    ///   the host, today) runs the same OnEnter script against that peer's phantom the client's
    ///   own engine already ran against its real character. Idempotence is NOT free here (a
    ///   trigger's OnEnter carries no engine one-shot flag), so
    ///   <see cref="TriggerRelayStore.Collect"/> already removed anything fired before — every
    ///   entry here is genuinely new.
    /// - the NEW half, symmetric across every peer (<see cref="PumpNativeTriggerFires"/>):
    ///   triggers a DIFFERENT peer's engine already fired NATIVELY (its own real character,
    ///   observed by the plugin's <c>TriggerFireHook</c>), that this engine has not yet been
    ///   asked to also run — the reverse direction docs/research/trigger-relay.md's symmetry
    ///   pass closes: previously only a client's presence ever reached the host this way, never
    ///   the host's own presence reaching a client.
    ///
    /// Called from BOTH peer types every pump; the old half is a no-op on a client
    /// (<see cref="ICoopSession.GetTriggerFireRequests"/>'s own default returns empty there).
    /// </summary>
    public void PumpTriggerFiring(in PlayerState local)
    {
        IReadOnlyList<TriggerFireRequest> watchRequests = ModuleNameGuard.IsFallbackModuleName(local.Module)
            ? []
            : session.GetTriggerFireRequests(local.Module);

        IReadOnlyList<TriggerFireRequest> nativeRequests = PumpNativeTriggerFires(local);

        bool anyOutstanding = watchRequests.Count > 0 || nativeRequests.Count > 0;
        if (!anyOutstanding && !_triggerRequestsOutstanding)
        {
            return;
        }

        _triggerRequestsOutstanding = anyOutstanding;

        List<(uint TriggerId, uint PeerId)> combined = new(watchRequests.Count + nativeRequests.Count);
        foreach (TriggerFireRequest r in watchRequests)
        {
            combined.Add((r.TriggerId, r.PeerId));
        }

        foreach (TriggerFireRequest r in nativeRequests)
        {
            combined.Add((r.TriggerId, r.PeerId));
        }

        bridge.TryRequestTriggerFire(combined);

        if (watchRequests.Count > 0 && _triggerRequestsLogged != watchRequests.Count)
        {
            _triggerRequestsLogged = watchRequests.Count;
            log($"firing {watchRequests.Count} generic trigger(s) a peer walked into");
        }
        else if (watchRequests.Count == 0)
        {
            _triggerRequestsLogged = 0;
        }

        if (nativeRequests.Count > 0 && _nativeTriggerRequestsLogged != nativeRequests.Count)
        {
            _nativeTriggerRequestsLogged = nativeRequests.Count;
            log($"firing {nativeRequests.Count} natively-fired generic trigger(s) for another peer's phantom");
        }
        else if (nativeRequests.Count == 0)
        {
            _nativeTriggerRequestsLogged = 0;
        }
    }

    private string _nativeFireHandledModule = string.Empty;
    private readonly HashSet<uint> _nativeFireHandledIds = [];

    /// <summary>
    /// Both halves of protocol 36's reverse relay, every peer: publish whatever THIS engine
    /// natively fired for its own real character since last pump (already allowlist- and
    /// eligibility-filtered by the plugin), and hand back the OTHER peers' native fires this
    /// engine has not yet been asked to run too — deduplicated per module, since
    /// <see cref="ICoopSession.GetNativeTriggerFireRequests"/> is a resent LEVEL and asking the
    /// plugin to re-fire the SAME trigger every tick forever would run its OnEnter script every
    /// tick forever (a trigger's OnEnter has no engine one-shot flag to fall back on).
    /// </summary>
    private IReadOnlyList<TriggerFireRequest> PumpNativeTriggerFires(in PlayerState local)
    {
        if (ModuleNameGuard.IsFallbackModuleName(local.Module))
        {
            return [];
        }

        IReadOnlyList<uint> ownNativeFires = bridge.ReadNativeFiredTriggers();
        if (ownNativeFires.Count > 0)
        {
            session.PublishNativeTriggerFires(local.Module, ownNativeFires);
        }

        if (!string.Equals(_nativeFireHandledModule, local.Module, StringComparison.OrdinalIgnoreCase))
        {
            _nativeFireHandledModule = local.Module;
            _nativeFireHandledIds.Clear();
        }

        IReadOnlyList<TriggerFireRequest> known = session.GetNativeTriggerFireRequests(local.Module);
        if (known.Count == 0)
        {
            return [];
        }

        List<TriggerFireRequest> fresh = new(known.Count);
        foreach (TriggerFireRequest request in known)
        {
            // Peer 0 never names a real peer (see TriggerRelayPacket's own doc comment); my
            // own origin means my own engine already ran this for real — nothing to relay to
            // myself.
            if (request.PeerId == 0 || request.PeerId == session.LocalPeerId)
            {
                continue;
            }

            if (!_nativeFireHandledIds.Add(request.TriggerId))
            {
                continue;
            }

            fresh.Add(request);
        }

        return fresh;
    }
}
