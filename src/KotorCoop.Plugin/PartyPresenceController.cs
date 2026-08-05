using System.Diagnostics;

namespace KotorCoop.Plugin;

/// <summary>
/// Drives this instance's party companions to wherever the host's engine has them.
///
/// The host owns the companions — its own AI runs them and the agent streams their state.
/// This instance holds solo mode (the agent's job) so the local party AI issues no follow
/// orders, and this controller places each companion body every frame with the seamless
/// recipe, replacing the agent's old external position writes. Those writes never touched
/// the client twin, so the rendered companion trailed the engine's 512 ms ease and stepped
/// once per network tick — the same defect the phantom had before
/// docs/research/realtime-creature-movement.md, solved the same way.
///
/// Companions are keyed by NPC roster slot (identical across two instances of the same
/// save); each is resolved against THIS instance's party table, because runtime object
/// ids are not a reliable cross-instance key for party members.
///
/// Kill switch: <c>party_drive=0</c> in coop.ini (the agent then keeps only solo mode and
/// nothing moves the local copies — visibly worse, but safe).
/// </summary>
internal sealed unsafe class PartyPresenceController(SharedBlockView block)
{
    // The APP_MANAGER -> server -> internal walk, same chain the agent's EngineGlobals
    // uses (no ASLR; the plugin runs inside the image these are absolute in).
    private const nint AppManagerPtr = 0x007A39FC;
    private const int AppServerOffset = 0x08;
    private const int InternalOffset = 0x04;

    /// <summary>CServerExoAppInternal + this = the embedded CSWPartyTable.</summary>
    private const int PartyTableOffset = 0x1B770;

    /// <summary>CSWPartyTable + this = int[9] spawned object id per roster slot.</summary>
    private const int PartyObjectIds = 0x0C;

    private const int PartyNpcSlots = 9;

    private readonly bool _enabled = PluginConfig.ReadBool("party_drive", true);

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly uint[] _trackedId = new uint[SharedBlock.PartyPresenceCapacity];
    private readonly nint[] _creature = new nint[SharedBlock.PartyPresenceCapacity];
    private readonly uint[] _appliedOneShot = new uint[SharedBlock.PartyPresenceCapacity];
    private readonly bool[] _oneShotPrimed = new bool[SharedBlock.PartyPresenceCapacity];

    private readonly MotionSmoother _smoother = new(
        SharedBlock.PartyPresenceCapacity,
        Math.Clamp(PluginConfig.ReadFloat("smooth_ms", 60f), 0f, 400f) / 1000f);

    private int _drivenLogged = -1;

    public void Invalidate()
    {
        for (int i = 0; i < SharedBlock.PartyPresenceCapacity; i++)
        {
            Forget(i);
        }

        block.PartyPresenceCount = 0;
        _drivenLogged = -1;
    }

    public void OnFrame()
    {
        if (!_enabled || block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        // A local conversation owns the stage — companions are positioned by the dialogue
        // system itself while one runs.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        uint count = Math.Min(block.PartyPresenceCount, (uint)SharedBlock.PartyPresenceCapacity);
        if (count == 0)
        {
            return;
        }

        double now = _clock.Elapsed.TotalSeconds;
        nint player = (nint)block.PlayerCreature;
        int driven = 0;

        for (int i = 0; i < count; i++)
        {
            if (DriveOne(i, block.GetPartyPresence(i), now, player))
            {
                driven++;
            }
        }

        if (driven != _drivenLogged)
        {
            _drivenLogged = driven;
            PluginHost.Log($"party drive: mirroring {driven} of the host's companions");
        }
    }

    private bool DriveOne(int slot, PartyPresenceView member, double now, nint player)
    {
        if (member.RosterSlotPlusOne is 0 or > PartyNpcSlots)
        {
            Forget(slot);
            return false;
        }

        if (!float.IsFinite(member.Position.X) || !float.IsFinite(member.Position.Y)
            || !float.IsFinite(member.Position.Z)
            || (member.Position.X == 0f && member.Position.Y == 0f))
        {
            return false;
        }

        uint objectId = ResolveRosterObjectId((int)member.RosterSlotPlusOne - 1);
        if (objectId is 0 or Odyssey.ObjectInvalid)
        {
            return false;
        }

        nint creature = Resolve(slot, objectId);
        if (creature == 0 || creature == player)
        {
            return false;
        }

        // Same reasoning as the enemy drive: the local AI (and any leftover follow
        // action) must never contest the writes, so re-claim every pass.
        Odyssey.ClaimCreature(creature);

        // Health travels in the same slot so there is exactly one writer for the body.
        if (member.Health > 0 && Odyssey.GetCurrentHitPoints(creature) != member.Health)
        {
            Odyssey.SetCurrentHitPoints(creature, member.Health);
        }

        if (!_oneShotPrimed[slot])
        {
            _oneShotPrimed[slot] = true;
            _appliedOneShot[slot] = member.OneShotSerial;
        }
        else if (member.OneShotSerial != _appliedOneShot[slot])
        {
            _appliedOneShot[slot] = member.OneShotSerial;
            if (member.OneShotAnimation != 0)
            {
                Odyssey.PlayOneShotAnimation(creature, (ushort)member.OneShotAnimation);
            }
        }

        (Vec3 shown, float speed, bool moving) = _smoother.Step(slot, member.Position, now);
        Odyssey.DriveEnemy(creature, shown, member.Facing, speed, moving, member.Animation);
        return true;
    }

    /// <summary>This instance's object id for a roster slot, from its OWN party table.</summary>
    private static uint ResolveRosterObjectId(int rosterSlot)
    {
        nint appManager = *(nint*)AppManagerPtr;
        if (appManager == 0)
        {
            return 0;
        }

        nint server = *(nint*)(appManager + AppServerOffset);
        if (server == 0)
        {
            return 0;
        }

        nint serverInternal = *(nint*)(server + InternalOffset);
        if (serverInternal == 0)
        {
            return 0;
        }

        return *(uint*)(serverInternal + PartyTableOffset + PartyObjectIds + (rosterSlot * 4));
    }

    /// <summary>Same cache-and-revalidate as the enemy drive: id and object type must
    /// still match every frame, or the pointer is stale reused memory.</summary>
    private nint Resolve(int slot, uint objectId)
    {
        nint cached = _creature[slot];
        if (cached != 0 && _trackedId[slot] == objectId
            && *(uint*)(cached + 0x04) == objectId
            && *(int*)(cached + 0x08) == 5)
        {
            return cached;
        }

        Forget(slot);

        nint creature = Odyssey.ResolveServerObject(objectId);
        if (creature == 0 || *(int*)(creature + 0x08) != 5)
        {
            return 0;
        }

        _creature[slot] = creature;
        _trackedId[slot] = objectId;
        return creature;
    }

    private void Forget(int slot)
    {
        if (_creature[slot] != 0)
        {
            Odyssey.ForgetGait(_creature[slot]);
        }

        _creature[slot] = 0;
        _trackedId[slot] = 0;
        _appliedOneShot[slot] = 0;
        _oneShotPrimed[slot] = false;
        _smoother.Reset(slot);
    }
}
