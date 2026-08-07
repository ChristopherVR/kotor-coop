namespace KotorCoop.Plugin;

/// <summary>
/// Lets a CLIENT set off an encounter.
///
/// Every peer that does not own enemies has its encounters switched off
/// (<c>KotorGameBridge.DisableLocalEncounters</c>): a spawn there would advance that
/// instance's object-id counter and break the id identity the enemy, item, door and
/// placeable channels all key on. The cost was that a client could walk over an ambush
/// trigger and get nothing at all until the host happened to walk over it too.
///
/// Both halves of the fix live here, because they are two ends of one channel:
///
/// - **Watch** (client): each pass, ask the engine's own point-in-polygon test which of the
///   area's encounters contain the local player, and publish that SET into the block. The
///   area keeps the list itself (<c>CSWSArea + 0x1A0</c>, appended by
///   <c>CSWSEncounter::AddToArea</c>), so this is a short walk, not a memory sweep.
/// - **Fire** (owner): for each (encounter, peer) the agent forwards, run the engine's own
///   eligibility decision with that peer's PHANTOM standing in for the player who walked
///   in, and let the engine spawn. One engine ever spawns, so ids stay identical.
///
/// The phantom is what makes this honest rather than a forced spawn. It is a real creature
/// standing where the remote player stands, and since combat participation it is in faction
/// 0 (PLAYER) — so <c>GetAIStateReputation</c> gives the encounter's hostile faction the
/// same answer it would give for a PC. The engine re-checks the polygon, the reputation, the
/// body being alive and the spawn pool. See docs/research/client-encounters.md.
///
/// Kill switch: <c>client_encounters=0</c> in coop.ini.
/// </summary>
internal sealed unsafe class EncounterController(SharedBlockView block)
{
    /// <summary>
    /// Frames between watch passes. The hook runs far above the frame rate and an encounter
    /// polygon is a walking-speed volume — a fifth of a second of latency on entering one is
    /// invisible, and it keeps the polygon tests off most frames.
    /// </summary>
    private const int WatchIntervalFrames = 12;

    /// <summary>Encounters in one area. korr_m34aa has the base game's most, at 7; the cap
    /// only bounds the stack buffer.</summary>
    private const int MaxAreaEncounters = 32;

    private readonly bool _enabled = PluginConfig.ReadBool("client_encounters", true);

    private readonly uint[] _inside = new uint[SharedBlock.EncounterInsideCapacity];

    private int _countdown;
    private int _insideCount;
    private uint _insideSerial;
    private uint _requestSerial;
    private bool _verified;
    private bool _verifyLogged;
    private bool _offLogged;
    private int _refusalsLogged;

    /// <summary>
    /// Module change: every encounter pointer and id belongs to the old area. The published
    /// set is dropped so the agent cannot forward a stale id, and the request serial is
    /// re-latched so a request written for the previous module is not replayed against a
    /// same-numbered encounter in the new one (the counter resets — Handoff 4.8).
    /// </summary>
    public void Invalidate()
    {
        _insideCount = 0;
        _requestSerial = block.EncounterRequestSerial;
        block.SetEncountersInside([], ++_insideSerial);
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
                PluginHost.Log("encounters: client triggering disabled by coop.ini (client_encounters=0)");
            }

            return;
        }

        if (!EnsureVerified())
        {
            return;
        }

        // Fire first: a request that arrived this tick should not wait a frame behind a
        // watch pass that is throttled anyway.
        HandleRequests();
        HandleWatch();
    }

    private bool EnsureVerified()
    {
        if (_verified)
        {
            return true;
        }

        bool ok = Odyssey.VerifyEncounterCalls(out string diagnostic);

        if (!_verifyLogged)
        {
            _verifyLogged = true;
            PluginHost.Log(ok
                ? $"encounters: {diagnostic}"
                : $"encounters: REFUSED — {diagnostic}; client-triggered encounters are off");
        }

        _verified = ok;
        return ok;
    }

    // ---- the watch (client side) --------------------------------------------------

    private void HandleWatch()
    {
        if (!block.EncounterWatchEnabled)
        {
            // The owner's own engine fires its encounters; it has nothing to ask for. Clear
            // once so an authority handover cannot leave a stale set behind.
            if (_insideCount != 0)
            {
                _insideCount = 0;
                block.SetEncountersInside([], ++_insideSerial);
            }

            return;
        }

        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = WatchIntervalFrames;

        // A conversation moves the camera and the participants around and is the one time a
        // player's position is not their own; the enemy drive skips for the same reason.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        nint player = (nint)block.PlayerCreature;
        if (player == 0)
        {
            return;
        }

        Vec3 position = Odyssey.GetPosition(player);
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y))
        {
            return;
        }

        Span<uint> area = stackalloc uint[MaxAreaEncounters];
        int found = Odyssey.ListAreaEncounters(area);

        Span<uint> inside = stackalloc uint[SharedBlock.EncounterInsideCapacity];
        int count = 0;

        for (int i = 0; i < found && count < inside.Length; i++)
        {
            nint encounter = Odyssey.ResolveEncounter(area[i]);
            if (encounter != 0 && Odyssey.IsInsideEncounter(encounter, position))
            {
                inside[count++] = area[i];
            }
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
        block.SetEncountersInside(inside[..count], ++_insideSerial);

        PluginHost.Log(count == 0
            ? "encounters: left every activate area"
            : $"encounters: standing in {count} activate area(s), first 0x{inside[0]:X}"
                + " — asking the enemy owner to fire");
    }

    /// <summary>Order matters as well as membership: the set comes out of the area's list in
    /// a fixed order, so a plain positional compare is exact and needs no allocation.</summary>
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

    // ---- firing (owner side) ------------------------------------------------------

    private void HandleRequests()
    {
        uint serial = block.EncounterRequestSerial;
        if (serial == _requestSerial)
        {
            return;
        }

        _requestSerial = serial;

        uint count = block.EncounterRequestCount;
        for (uint i = 0; i < count; i++)
        {
            (uint encounterId, uint peerId) = block.GetEncounterRequest((int)i);
            if (encounterId is 0 or Odyssey.ObjectInvalid || peerId == 0)
            {
                continue;
            }

            Fire(encounterId, peerId);
        }
    }

    private void Fire(uint encounterId, uint peerId)
    {
        uint phantomId = FindPhantom(peerId);
        if (phantomId == 0)
        {
            Refuse(encounterId, Odyssey.EncounterFireResult.NoPhantom,
                $"peer {peerId} has no body here yet");
            return;
        }

        Odyssey.EncounterFireResult result = Odyssey.FireEncounter(encounterId, phantomId);

        if (result == Odyssey.EncounterFireResult.Started)
        {
            block.EncountersFired++;
            block.EncounterStatus = (uint)result;
            PluginHost.Log(
                $"encounters: started 0x{encounterId:X} for peer {peerId} (phantom 0x{phantomId:X})");
            return;
        }

        // "Not armed" is the overwhelmingly common answer and it is not a problem: an
        // encounter fires once and stays started, while the requesting client keeps
        // standing in the polygon and keeps re-reporting it. Counting it as a refusal
        // would make the counter meaningless.
        if (result == Odyssey.EncounterFireResult.NotArmed)
        {
            return;
        }

        Refuse(encounterId, result, $"phantom 0x{phantomId:X}");
    }

    private void Refuse(uint encounterId, Odyssey.EncounterFireResult result, string detail)
    {
        block.EncountersRefused++;
        block.EncounterStatus = (uint)result;

        // Capped: a client standing in a polygon the engine declines re-sends every tick,
        // and an uncapped line per tick would bury the log.
        if (_refusalsLogged < 10)
        {
            _refusalsLogged++;
            PluginHost.Log($"encounters: 0x{encounterId:X} not fired ({result}, {detail})");
        }
    }

    /// <summary>The body this instance is driving for <paramref name="peerId"/>. Peer slots
    /// and phantom-id slots are the same index — the agent writes both.</summary>
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
