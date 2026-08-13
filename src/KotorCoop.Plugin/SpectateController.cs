namespace KotorCoop.Plugin;

/// <summary>
/// A downed player watches their partner instead of their own corpse.
///
/// Stock KOTOR answers a downed PC by swapping control to a living companion
/// (ChangeCharacter), and on the host that still happens and is the right experience. On
/// a CLIENT that swap is deliberately suppressed — the companions are host-driven mirrors
/// and controlling one would fight its own drive — so before this controller a downed
/// client stared at its body until rescue. Now the camera follows a living peer's
/// phantom, through the engine's own handoff (<see cref="Odyssey.SetCameraFollowServerObject"/>,
/// the same CSWCModule::SetCameraFollow every engine control change funnels through), and
/// returns the moment the player is back up.
///
/// ⚠ A LEVEL, RE-ASSERTED, NOT AN EDGE. SetCameraFollow silently no-ops while the client
/// is in combat mode, and dialogue or cutscene cameras can take the follow back at any
/// time — a single fire-and-forget call would be lost exactly when the fight is busiest.
/// Re-asserting every pass converges; the engine reuses the installed follow behaviour,
/// so the repeated call allocates nothing.
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

    /// <summary>Frames between passes. The camera handoff is one engine call and the down
    /// state changes on the scale of seconds; half a second of latency entering spectate
    /// is invisible next to the death animation.</summary>
    private const int IntervalFrames = 30;

    private int _countdown;
    private bool _spectating;
    private uint _watchedPhantom;

    /// <summary>Module change: the phantom ids are the old world's, and the engine's own
    /// area attach hands the camera to the player as part of the load — nothing to
    /// restore, just forget.</summary>
    public void Invalidate()
    {
        _spectating = false;
        _watchedPhantom = 0;
        _countdown = 0;
    }

    public void OnFrame()
    {
        if (!_enabled || block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;

        // The dialogue system owns the camera while a conversation shows — including the
        // spectated copy of a PEER's conversation — and fighting it would jerk the view
        // between two masters twice a second.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        nint controlled = Odyssey.GetPlayerCreaturePointer();
        bool downed = controlled != 0 && Odyssey.IsDowned(controlled);

        if (!downed)
        {
            StopSpectating("back up — the camera is yours again");
            return;
        }

        uint phantom = FindLivingPeerPhantom();
        if (phantom == 0)
        {
            // Everyone else is down or gone; the corpse view is honest now.
            StopSpectating("nobody left standing to watch");
            return;
        }

        if (Odyssey.SetCameraFollowServerObject(phantom)
            && (!_spectating || phantom != _watchedPhantom))
        {
            _spectating = true;
            _watchedPhantom = phantom;
            PluginHost.Log($"spectate: down — watching the player behind phantom 0x{phantom:X}");
        }
    }

    private void StopSpectating(string reason)
    {
        if (!_spectating)
        {
            return;
        }

        _spectating = false;
        _watchedPhantom = 0;

        if (Odyssey.RestoreCameraToControlledCreature())
        {
            PluginHost.Log($"spectate: {reason}");
        }
    }

    /// <summary>The first slot whose peer is alive and whose phantom body resolves. Peer
    /// slots and phantom-id slots share an index — the agent writes both.</summary>
    private uint FindLivingPeerPhantom()
    {
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
                return id;
            }
        }

        return 0;
    }
}
