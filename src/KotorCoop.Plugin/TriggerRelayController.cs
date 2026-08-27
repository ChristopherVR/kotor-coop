namespace KotorCoop.Plugin;

/// <summary>
/// Lets a CLIENT'S generic-trigger OnEnter also run on the enemy owner's engine, so a
/// scripted event's object-id-consuming side effects (most commonly a plot actor's
/// <c>CreateObject</c>/<c>DestroyObject</c>) happen on both instances instead of drifting them
/// apart. Parallel to <see cref="EncounterController"/> in shape, different in WHY:
///
/// - An encounter spawn consumes ids, so a client's own encounters are disarmed outright and
///   the owner's engine is the ONLY one that ever fires one.
/// - A generic trigger's OnEnter is NOT disarmed on a client — its own engine already fires
///   it correctly for its own real character. The gap is narrower: that firing happens on
///   ONE instance only, and a script commonly gated on <c>GetEnteringObject() == GetFirstPC()</c>
///   never fires on the OTHER instance's phantom of that same player, because a phantom is
///   placed by a direct SetPosition write and never generates the subarea-entered event the
///   real engine needs (the exact mechanical reason client-encounters.md gives for why a
///   phantom never triggers an encounter either).
///
/// Both halves live here, same split as EncounterController:
///
/// - **Watch** (the non-enemy-authority peer): ask the engine's own point-in-polygon test
///   which of the area's triggers contain the local player, filter to ones whose OnEnter
///   script matches the coop.ini allowlist and are not a trap or area transition, and
///   publish that set.
/// - **Fire** (the enemy owner): for each (trigger, peer) the agent forwards, run the SAME
///   RunScript call the engine's own EventHandler makes, with that peer's PHANTOM standing in
///   as OBJECT_SELF's entering-object cache.
///
/// Kill switch: <c>trigger_relay=0</c> in coop.ini (default ON — live-verified 2026-08-21 on
/// end_m01ab: the story trigger `end_trig_carth02`'s relay fired correctly with the fired-set
/// dedup holding across four separate re-visits, and the negative control (`k_trg_stealth`)
/// was confirmed geometrically inside the local player's position yet never entered the watch's
/// "inside" set at all, exactly as the allowlist ordering (relay-eligible, then geometry, then
/// pattern) predicts; see docs/research/trigger-relay.md's live-pass section). Allowlist:
/// <c>trigger_relay_patterns</c> (default <c>k_p*_*</c>), comma-separated globs matched
/// against the OnEnter script resref.
/// </summary>
internal sealed unsafe class TriggerRelayController(SharedBlockView block)
{
    /// <summary>Same cadence as <see cref="EncounterController"/>'s watch — a trigger polygon
    /// is a walking-speed volume, so a fifth of a second of latency entering one is invisible.
    /// </summary>
    private const int WatchIntervalFrames = 12;

    /// <summary>The whole base game's biggest module in the early-game inventory
    /// (docs/research/trigger-relay.md) has 14 generic trigger placements; this only bounds
    /// the stack buffer, not the feature.</summary>
    private const int MaxAreaTriggers = 32;

    private const string DefaultPatterns = "k_p*_*";

    /// <summary>coop.ini <c>trigger_relay</c>, overridable per session by the host — see
    /// <see cref="EffectiveBehaviourFlag"/>.</summary>
    private readonly EffectiveBehaviourFlag _triggerRelay = new(
        "trigger_relay", SharedBlock.OverrideBitTriggerRelay, PluginConfig.ReadBool("trigger_relay", true));
    private readonly string[] _patterns = ParsePatterns(PluginConfig.Read("trigger_relay_patterns"));

    private readonly uint[] _inside = new uint[SharedBlock.TriggerInsideCapacity];

    private int _countdown;
    private int _insideCount;
    private uint _insideSerial;
    private uint _requestSerial;
    private bool _verified;
    private bool _verifyLogged;
    private bool _offLogged;
    private int _refusalsLogged;

    // ---- native fire observation (protocol 36) -------------------------------------

    /// <summary>Trigger ids this instance's own engine has already been OBSERVED natively
    /// firing this module — checked before either resolving the trigger or publishing it, so a
    /// capture is never re-processed. Distinct from <see cref="_nativeFiredPublished"/>: an id
    /// can land here (permanently, this module) without ever reaching the published list, if
    /// it failed eligibility/allowlist or arrived once the list was already at capacity.
    /// </summary>
    private readonly HashSet<uint> _nativeFiredThisModule = [];

    /// <summary>The bounded set actually published to <see cref="SharedBlock.OffsetTriggerNativeFired"/>
    /// this module — capacity <see cref="SharedBlock.TriggerNativeFiredCapacity"/> (4). A 5th
    /// distinct native fire in one module visit is not relayed to other peers (it still fires
    /// correctly on THIS engine, natively, completely unaffected — only the RELAY to other
    /// peers is capped), the same headroom trade <see cref="_inside"/>'s own capacity already
    /// accepts for the watch direction.</summary>
    private readonly List<uint> _nativeFiredPublished = [];

    private uint _nativeFiredSerial;
    private uint _lastDrainedNativeSequence;
    private bool _nativeHookInstalled;
    private bool _nativeHookInstallLogged;

    private static string[] ParsePatterns(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [DefaultPatterns];
        }

        string[] parts = raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts : [DefaultPatterns];
    }

    /// <summary>Module change: every trigger pointer and id belongs to the old area.</summary>
    public void Invalidate()
    {
        _insideCount = 0;
        _requestSerial = block.TriggerRequestSerial;
        block.SetTriggersInside([], ++_insideSerial);

        _nativeFiredThisModule.Clear();
        _nativeFiredPublished.Clear();
        block.SetTriggerNativeFired([], ++_nativeFiredSerial);
        TriggerFireHook.Invalidate();
    }

    public void OnFrame()
    {
        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        if (!_triggerRelay.Resolve(block))
        {
            if (!_offLogged)
            {
                _offLogged = true;
                PluginHost.Log("triggers: relay disabled by coop.ini (trigger_relay=0)");
            }

            return;
        }

        if (!EnsureVerified())
        {
            return;
        }

        // Fire first, same reasoning EncounterController gives: a request that arrived this
        // tick should not wait behind a throttled watch pass.
        HandleRequests();
        HandleWatch();

        // Separate from the verify-bytes gate above: a failure to install the NEW native-fire
        // observer must not take down the EXISTING watch/request relay it shares this
        // controller with — see docs/research/trigger-relay.md's protocol-36 section.
        HandleNativeFireObservation();
    }

    private bool EnsureVerified()
    {
        if (_verified)
        {
            return true;
        }

        bool ok = OdysseyTriggers.VerifyTriggerCalls(out string diagnostic);

        if (!_verifyLogged)
        {
            _verifyLogged = true;
            PluginHost.Log(ok
                ? $"triggers: {diagnostic}"
                : $"triggers: REFUSED — {diagnostic}; trigger relay is off");
        }

        _verified = ok;
        return ok;
    }

    // ---- the watch (non-enemy-authority side) --------------------------------------

    private void HandleWatch()
    {
        if (!block.TriggerWatchEnabled)
        {
            if (_insideCount != 0)
            {
                _insideCount = 0;
                block.SetTriggersInside([], ++_insideSerial);
            }

            return;
        }

        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = WatchIntervalFrames;

        // Dialogue moves the camera and the participants; the encounter watch skips for the
        // same reason and so does this one.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        if (block.PlayerCreature == 0)
        {
            return;
        }

        nint player = Odyssey.GetPlayerCreaturePointer();
        if (player == 0)
        {
            return;
        }

        Vec3 position = Odyssey.GetPosition(player);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
        {
            return;
        }

        Span<uint> area = stackalloc uint[MaxAreaTriggers];
        int found = OdysseyTriggers.ListAreaTriggers(area);

        Span<uint> inside = stackalloc uint[SharedBlock.TriggerInsideCapacity];
        int count = 0;

        for (int i = 0; i < found && count < inside.Length; i++)
        {
            nint trigger = OdysseyTriggers.ResolveTrigger(area[i]);
            if (trigger == 0 || !OdysseyTriggers.IsRelayEligible(trigger))
            {
                continue;
            }

            if (!OdysseyTriggers.IsInsideTrigger(trigger, position))
            {
                continue;
            }

            string scriptName = OdysseyTriggers.PeekScriptOnEnter(trigger);
            if (!MatchesAnyPattern(scriptName))
            {
                continue;
            }

            inside[count++] = area[i];
        }

        if (Unchanged(inside, count))
        {
            return;
        }

        for (int i = 0; i < count; i++)
        {
            _inside[i] = inside[i];
        }

        _insideCount = count;
        block.SetTriggersInside(inside[..count], ++_insideSerial);

        PluginHost.Log(count == 0
            ? "triggers: left every relay-eligible polygon"
            : $"triggers: standing in {count} generic trigger(s), first 0x{inside[0]:X}"
                + " — asking the enemy owner to run its OnEnter too");
    }

    private bool MatchesAnyPattern(string scriptName)
    {
        if (scriptName.Length == 0)
        {
            return false;
        }

        foreach (string pattern in _patterns)
        {
            if (OdysseyTriggers.MatchesPattern(scriptName, pattern))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Order matters as well as membership — same reasoning as
    /// <see cref="EncounterController.Unchanged"/>.</summary>
    private bool Unchanged(ReadOnlySpan<uint> inside, int count)
    {
        if (count != _insideCount)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (_inside[i] != inside[i])
            {
                return false;
            }
        }

        return true;
    }

    // ---- firing (enemy-owner side) -------------------------------------------------

    private void HandleRequests()
    {
        uint serial = block.TriggerRequestSerial;
        if (serial == _requestSerial)
        {
            return;
        }

        _requestSerial = serial;

        uint count = block.TriggerRequestCount;
        for (uint i = 0; i < count; i++)
        {
            (uint triggerId, uint peerId) = block.GetTriggerRequest((int)i);
            if (triggerId is 0 or Odyssey.ObjectInvalid || peerId == 0)
            {
                continue;
            }

            Fire(triggerId, peerId);
        }
    }

    private void Fire(uint triggerId, uint peerId)
    {
        uint phantomId = FindPhantom(peerId);
        if (phantomId == 0)
        {
            Refuse(triggerId, OdysseyTriggers.TriggerFireResult.NoPhantom,
                $"peer {peerId} has no body here yet");
            return;
        }

        OdysseyTriggers.TriggerFireResult result = OdysseyTriggers.FireTrigger(triggerId, phantomId);

        if (result == OdysseyTriggers.TriggerFireResult.Fired)
        {
            block.TriggersFired++;
            block.TriggerStatus = (uint)result;
            PluginHost.Log(
                $"triggers: fired OnEnter of 0x{triggerId:X} for peer {peerId} (phantom 0x{phantomId:X})");
            return;
        }

        Refuse(triggerId, result, $"phantom 0x{phantomId:X}");
    }

    private void Refuse(uint triggerId, OdysseyTriggers.TriggerFireResult result, string detail)
    {
        block.TriggersRefused++;
        block.TriggerStatus = (uint)result;

        // Capped, same reasoning as EncounterController.Refuse: an uncapped line per tick
        // would bury the log if a request keeps arriving for a trigger that keeps refusing.
        if (_refusalsLogged < 10)
        {
            _refusalsLogged++;
            PluginHost.Log($"triggers: 0x{triggerId:X} not fired ({result}, {detail})");
        }
    }

    /// <summary>The body this instance is driving for <paramref name="peerId"/>. Identical to
    /// <see cref="EncounterController.FindPhantom"/> — peer slots and phantom-id slots are
    /// the same index.</summary>
    private uint FindPhantom(uint peerId)
    {
        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            RemoteView remote = block.GetRemote(i);
            if (remote.Active && remote.PeerId == peerId && remote.Health > 0)
            {
                return block.GetPhantomId(i);
            }
        }

        return 0;
    }

    // ---- native fire observation (protocol 36 — the reverse relay direction) --------

    /// <summary>
    /// Installs <see cref="TriggerFireHook"/> (once) and drains whatever it has captured since
    /// the last pass: any REAL entry, by ANY creature, into ANY generic trigger that fired for
    /// real this frame. Filtered here, on the main thread where every primitive it needs
    /// (<see cref="Odyssey.GetPlayerObjectId"/>, <see cref="OdysseyTriggers.ResolveTrigger"/>,
    /// the eligibility and allowlist checks) is safe to call, down to exactly the same shape
    /// <see cref="HandleWatch"/> already publishes for a currently-standing report: only MY OWN
    /// real character's entry, only an eligible trigger, only one matching the allowlist.
    /// </summary>
    private void HandleNativeFireObservation()
    {
        if (!_nativeHookInstalled)
        {
            _nativeHookInstalled = TriggerFireHook.Install(out string diagnostic);

            if (!_nativeHookInstallLogged)
            {
                _nativeHookInstallLogged = true;
                PluginHost.Log(_nativeHookInstalled
                    ? $"triggers: {diagnostic}"
                    : $"triggers: REFUSED — {diagnostic}");
            }

            if (!_nativeHookInstalled)
            {
                return;
            }
        }

        uint sequence = TriggerFireHook.Sequence;
        if (sequence == _lastDrainedNativeSequence)
        {
            return;
        }

        uint localPlayerId = Odyssey.GetPlayerObjectId();
        if (localPlayerId == 0)
        {
            // No world loaded (or a transition mid-flight): leave the sequence where it is and
            // try again next frame. The ring only holds the last few captures regardless, so a
            // fire that happens during this window is bounded loss, not a stuck state — the
            // same trade every other "cannot read the world right now" guard in this codebase
            // already accepts.
            return;
        }

        uint newCount = sequence - _lastDrainedNativeSequence;
        if (newCount > TriggerFireHook.Capacity)
        {
            newCount = (uint)TriggerFireHook.Capacity;
        }

        bool changed = false;

        for (uint i = 0; i < newCount; i++)
        {
            int index = (int)(sequence - newCount + i);
            TriggerFireHook.Read(index, out uint triggerId, out uint enteringObjectId);

            if (triggerId == 0 || enteringObjectId != localPlayerId)
            {
                // Not my own real character entering — a companion, an NPC, a hostile
                // creature crossing the same polygon — out of scope, matching the watch's own
                // "only the local player" semantics exactly.
                continue;
            }

            if (!_nativeFiredThisModule.Add(triggerId))
            {
                continue;
            }

            nint trigger = OdysseyTriggers.ResolveTrigger(triggerId);
            if (trigger == 0 || !OdysseyTriggers.IsRelayEligible(trigger))
            {
                continue;
            }

            string scriptName = OdysseyTriggers.PeekScriptOnEnter(trigger);
            if (!MatchesAnyPattern(scriptName))
            {
                continue;
            }

            if (_nativeFiredPublished.Count >= SharedBlock.TriggerNativeFiredCapacity)
            {
                // See _nativeFiredPublished's own doc comment: a 5th distinct id this module
                // visit still fired correctly, natively, on this engine — only the relay to
                // other peers is capped.
                continue;
            }

            _nativeFiredPublished.Add(triggerId);
            changed = true;

            PluginHost.Log(
                $"triggers: observed my own engine natively fire OnEnter of 0x{triggerId:X} "
                + "— publishing for other peers");
        }

        _lastDrainedNativeSequence = sequence;

        if (changed)
        {
            Span<uint> span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_nativeFiredPublished);
            block.SetTriggerNativeFired(span, ++_nativeFiredSerial);
        }
    }
}
