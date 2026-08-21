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

    private readonly bool _enabled = PluginConfig.ReadBool("trigger_relay", true);
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
    }

    public void OnFrame()
    {
        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        if (!_enabled)
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
}
