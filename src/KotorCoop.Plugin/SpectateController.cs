namespace KotorCoop.Plugin;

/// <summary>
/// A downed player watches their partner instead of their own corpse — and can cycle
/// through everyone worth watching.
///
/// Stock KOTOR answers a downed PC by swapping control to a living companion
/// (ChangeCharacter), and on the host that still happens and is the right experience. On
/// a CLIENT that swap is deliberately suppressed — the companions are host-driven mirrors
/// and controlling one would fight its own drive — so before this controller a downed
/// client stared at its body until rescue. Now the camera follows a living target,
/// through the engine's own handoff (<see cref="Odyssey.SetCameraFollowServerObject"/>,
/// the same CSWCModule::SetCameraFollow every engine control change funnels through), and
/// returns the moment the player is back up.
///
/// THE ROSTER, in cycle order: every living peer's phantom (slot order), then every
/// living mirrored companion. The companions matter more than they look: when the HOST
/// goes down, the engine swaps them onto a companion and their PHANTOM lies on the floor
/// — the companion mirror is what the host is actually playing, so it is what a
/// spectator wants to watch. Tab cycles (edge-triggered, foreground-gated so two
/// instances on one machine do not both react); the list is rebuilt every pass, so a
/// target that dies or despawns simply stops being listed and the camera snaps to the
/// first survivor.
///
/// ⚠ A LEVEL, RE-ASSERTED, NOT AN EDGE. SetCameraFollow silently no-ops while the client
/// is in combat mode, and dialogue or cutscene cameras can take the follow back at any
/// time — a single fire-and-forget call would be lost exactly when the fight is busiest.
/// Re-asserting every pass converges; the engine reuses the installed follow behaviour,
/// so the repeated call allocates nothing.
///
/// ⚠ DIALOGUE OWNS THE CAMERA, AND THIS CONTROLLER DEFERS TO IT COMPLETELY. While a
/// conversation shows here (the player's own, or the spectated copy of a peer's), every
/// pass returns before touching anything: no assert, no cycle, and — deliberately — no
/// RESTORE either. A revive that lands mid-conversation leaves <c>_watching</c> set, so
/// the give-back runs on the first pass AFTER the dialogue camera has torn down, instead
/// of yanking the view out from under it. This mirrors the engine's own discipline:
/// CSWCCreature::ReAttachCamera refuses to move the camera while the dialogue camera
/// behaviours (0x106D/0x106E) are installed.
///
/// The gate reads the CONTROLLED creature, not the co-op down flag alone: if the engine
/// swapped the (host) player onto a companion, the controlled body is not the downed one
/// and they are still playing — spectate must not steal that camera.
///
/// Kill switch: <c>spectate_on_down=0</c> in coop.ini.
/// </summary>
internal sealed unsafe class SpectateController(SharedBlockView block)
{
    private readonly bool _enabled = PluginConfig.ReadBool("spectate_on_down", true);

    /// <summary>Frames between camera passes. The handoff is one engine call and the down
    /// state changes on the scale of seconds; half a second of latency entering spectate
    /// is invisible next to the death animation. The cycle KEY is sampled every frame
    /// regardless — an edge that lands between passes must not be lost.</summary>
    private const int IntervalFrames = 30;

    /// <summary>VK_TAB. On a client Tab's stock meaning (switch party member) is already
    /// locked out by the control lock, so while down it is free to mean "watch the next
    /// one"; on the host, spectate only ever runs when no companion is left to switch to,
    /// so the stock meaning is a no-op there too.</summary>
    private const int CycleKey = 0x09;

    private int _countdown;
    private uint _watching;
    private bool _cycleKeyWasDown;
    private bool _advanceQueued;

    /// <summary>Module change: the ids are the old world's, and the engine's own area
    /// attach hands the camera to the player as part of the load — nothing to restore,
    /// just forget.</summary>
    public void Invalidate()
    {
        _watching = 0;
        _countdown = 0;
        _advanceQueued = false;
    }

    public void OnFrame()
    {
        if (!_enabled || block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        // The key edge is sampled EVERY frame and latched. The camera pass runs on a
        // cadence, and an edge that fired between passes would otherwise vanish — a
        // dropped input reads as "the cycle key sometimes does nothing".
        bool keyDown = NativeMethods.IsKeyDownHere(CycleKey);
        bool keyEdge = keyDown && !_cycleKeyWasDown;
        _cycleKeyWasDown = keyDown;
        if (keyEdge && _watching != 0)
        {
            _advanceQueued = true;
        }

        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;

        // Dialogue owns the camera — spectated conversations included. Defer EVERYTHING,
        // the restore too (see the class remarks); the level converges afterwards.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        nint controlled = Odyssey.GetPlayerCreaturePointer();
        bool downed = controlled != 0 && Odyssey.IsDowned(controlled);

        if (!downed)
        {
            StopSpectating("back up — the camera is yours again");
            _advanceQueued = false;
            return;
        }

        Span<uint> candidates = stackalloc uint[SharedBlock.SlotCapacity + SharedBlock.PartyPresenceCapacity];
        int count = CollectCandidates(candidates);

        uint pick = SpectateCycle.Pick(candidates[..count], _watching, _advanceQueued);
        _advanceQueued = false;

        if (pick == 0)
        {
            // Everyone else is down or gone; the corpse view is honest now.
            StopSpectating("nobody left standing to watch");
            return;
        }

        if (Odyssey.SetCameraFollowServerObject(pick) && pick != _watching)
        {
            _watching = pick;
            PluginHost.Log($"spectate: watching 0x{pick:X} — Tab cycles ({count} to watch)");
        }
    }

    private void StopSpectating(string reason)
    {
        if (_watching == 0)
        {
            return;
        }

        _watching = 0;

        if (Odyssey.RestoreCameraToControlledCreature())
        {
            PluginHost.Log($"spectate: {reason}");
        }
    }

    /// <summary>
    /// The ordered roster: living peers' phantoms first (peer slots and phantom-id slots
    /// share an index — the agent writes both), then living mirrored companions. Every
    /// entry is re-validated against the live world THIS pass; nothing is cached, so a
    /// body that died or despawned is simply absent and the cycle heals around it.
    /// </summary>
    private int CollectCandidates(Span<uint> candidates)
    {
        int count = 0;

        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            RemoteView remote = block.GetRemote(i);
            if (!remote.Active || remote.Health <= 0)
            {
                continue;
            }

            uint id = block.GetPhantomId(i);
            if (id != 0 && Odyssey.ResolveServerObject(id) != 0)
            {
                candidates[count++] = id;
            }
        }

        // Bounded by the published COUNT, not the capacity: the epoch clear zeroes the
        // count and leaves the slot bytes, so capacity-walking would resurrect stale
        // companions from the previous world.
        int partyCount = (int)Math.Min(block.PartyPresenceCount, (uint)SharedBlock.PartyPresenceCapacity);
        for (int i = 0; i < partyCount; i++)
        {
            PartyPresenceView member = block.GetPartyPresence(i);
            if (member.RosterSlotPlusOne == 0 || member.Health <= 0)
            {
                continue;
            }

            uint id = Odyssey.GetPartyRosterObjectId((int)member.RosterSlotPlusOne - 1);
            if (id is not 0 and not Odyssey.ObjectInvalid && Odyssey.ResolveServerObject(id) != 0)
            {
                candidates[count++] = id;
            }
        }

        return count;
    }
}
