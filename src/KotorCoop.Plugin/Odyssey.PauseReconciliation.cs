// Odyssey.PauseReconciliation.cs - reconciling client/server world-timer pause state against
// the pause-reason masks and the GUI's own pause belief, plus a handful of unrelated NWScript
// dispatch helpers that ended up in this split (spawning/destroying floor items, and the
// phantom/materialised-enemy faction and damage-immunity setup shared by remote-player and
// remote-enemy stand-in bodies).
// See docs/research/shared-state-audit.md for the pause reconciler's measured investigation.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- pause-state reconciliation ---------------------------------------------
    //
    // The pause feature has THREE state holders that only ever move on edges: the two
    // pause-reason masks (client + server), the world timers' paused flags, and the GUI's
    // own belief at CGuiInGame+0xB3C — which is re-derived every frame from the CLIENT
    // world timer's flag, arms the pause panel, and routes the space bar to "unpause"
    // while set. SetPauseState early-outs when the mask bit already agrees, so a single
    // missed edge (a raw mask write, a swallowed loopback message, a timer paused outside
    // the setter) strands the holders in disagreement PERMANENTLY: the banner cannot
    // clear and the space bar's unpause no-ops forever. Measured live on both instances.
    // The cure is to be level-triggered somewhere: this reconciler drives every holder
    // back to what the masks say, through the engine's own idempotent setters.

    /// <summary>CGuiInGame + this = the GUI's own "the game is paused" belief.</summary>
    private const int GuiBelievesPaused = 0xB3C;

    /// <summary>CWorldTimer + this = 1 while paused. A flag, not a counter.</summary>
    private const int WorldTimerPausedFlag = 0x24;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> ServerGetWorldTimerFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004AEDE0;

    /// <summary>
    /// Drives the world timers and the GUI's pause belief back into agreement with the
    /// pause masks. Returns true when something had to be repaired.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool ReconcilePause(out string detail)
    {
        detail = "";

        nint client = GetClient();
        nint server = GetServer();
        nint gui = GetInGameGui();
        if (client == 0 || server == 0 || gui == 0 || *(int*)(gui + 0x108) == 0)
        {
            return false;
        }

        // Never touch anything mid-load: the login chain pauses these timers ON PURPOSE
        // for the duration of a module load, and resumes them itself at presentation.
        nint internalServer = Deref(server + AppServerOffsetInternal);
        if (internalServer == 0
            || *(int*)(internalServer + 0x100B4) != 0     // module load in progress
            || *(ushort*)(internalServer + 0x10008) != 2) // server mode: 2 = world running
        {
            return false;
        }

        int union = GetPauseMask() | GetClientPauseMask();
        bool guiBelieves = *(int*)(gui + GuiBelievesPaused) != 0;
        bool repaired = false;

        // ⚠ THE BANNER IS THE ENGINE'S OWN INTENT, AND IT WINS.
        //
        // The engine's own pause paths (the space handler, the HUD button) move the GUI
        // belief and the world timers but DO NOT SET A MASK BIT. Tearing the banner down
        // whenever a mask disagreed with it reinvented the very bug PauseSanityPatch exists
        // to delete, on our own side — full measured timeline in
        // docs/research/shared-state-audit.md.
        //
        // So when the masks and the banner disagree, ADOPT the banner instead of cancelling
        // it: write the mask the engine forgot. That turns an engine pause into a real,
        // mask-backed pause — which is also what makes it publishable, so a HUD-button pause
        // now reaches the other player like any other.
        //
        // Adoption is deliberately restricted to the case where NO reason bit is held (or
        // only the player's own). A script pause raises its own bit and shows the same
        // banner, and adding the player bit on top of it would leave the game stuck paused
        // after the script cleared its own.
        if (union == 0 && guiBelieves)
        {
            SetPlayerPause(true);
            detail = "ADOPTED an engine pause (banner up, no mask) — now mask-backed and shareable";
            return true;
        }

        if (union == PauseReasonPlayer && !guiBelieves)
        {
            // The mirror image: an engine path cleared the banner but left our mask set.
            // Adopting the unpause is what lets a mouse click release a pause at all — there
            // is no key fallback on that route to notice.
            SetPlayerPause(false);
            detail = "ADOPTED an engine unpause (banner down, mask still held)";
            return true;
        }

        if (union == 0)
        {
            // No banner and no mask, but a timer is still stopped: a genuine stray, from the
            // login chain or a swallowed edge. Resume it.
            nint clientTimer = ClientGetWorldTimerFn(client);
            if (clientTimer != 0 && *(int*)(clientTimer + WorldTimerPausedFlag) != 0)
            {
                UnpauseWorldTimerFn(clientTimer);
                detail += "client timer, ";
                repaired = true;
            }

            nint serverTimer = ServerGetWorldTimerFn(server);
            if (serverTimer != 0 && *(int*)(serverTimer + WorldTimerPausedFlag) != 0)
            {
                UnpauseWorldTimerFn(serverTimer);
                detail += "server timer, ";
                repaired = true;
            }

            if (repaired)
            {
                detail = $"resumed a stray stopped timer ({detail.TrimEnd(',', ' ')}), "
                    + "no banner and no mask held";
            }
        }
        else if ((union & PauseReasonPlayer) != 0 && !guiBelieves)
        {
            // A player pause held ALONGSIDE some other reason, with no banner: the show edge
            // was missed. Restoring the banner is right here — the pause is genuinely held by
            // something we must not clear.
            SetPauseVisualsFn(gui, 1, PauseReasonManual);
            detail = $"restored the banner for a held pause (mask 0x{union:X2})";
            repaired = true;
        }

        return repaired;
    }

    /// <summary>
    /// Sets one pause reason through the engine's own setter, so CWorldTimer actually
    /// stops (a raw bit write would flag the pause without pausing anything). Returns
    /// false when there is no server, or the reason was already set.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool SetPauseReason(int mask)
    {
        if ((GetPauseMask() & mask) != 0)
        {
            return false;
        }

        nint server = GetServer();
        if (server == 0)
        {
            return false;
        }

        SetPauseStateFn(server, mask, 1);
        return true;
    }

    /// <summary>
    /// Puts a real, clickable item on the ground by calling the engine's own NWScript
    /// CreateItemOnFloor(string sTemplate, location lLoc, int bUseAppearAnimation).
    /// Returns the new object id, or <see cref="ObjectInvalid"/> on refusal.
    ///
    /// MAIN THREAD ONLY. Same guard structure as <see cref="SpawnCreature"/>.
    /// </summary>
    public static uint SpawnItemOnFloor(byte* resrefAscii, int resrefLength, Vec3 position, out int failure)
    {
        failure = 0;

        nint implementer = GetImplementer(out nint vm, out nint internalVm);
        if (implementer == 0)
        {
            failure = 1;
            return ObjectInvalid;
        }

        nint table = Deref(implementer + CommandsTableOffset);
        if (table == 0)
        {
            failure = 2;
            return ObjectInvalid;
        }

        if (_routineCreateItemOnFloor < 0)
        {
            _routineCreateItemOnFloor = FindRoutine(table, ExpectedCreateItemOnFloor);
        }

        if (_routineCreateItemOnFloor < 0)
        {
            failure = 3;
            return ObjectInvalid;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 4;
            return ObjectInvalid;
        }

        int stackPointer = *(int*)(internalVm + StackPointerOffset);

        ScriptLocation location = new()
        {
            Position = position,
            Orientation = new Vec3 { X = 1f, Y = 0f, Z = 0f },
        };

        ExoString resref = new() { CString = (nint)resrefAscii, Length = (uint)resrefLength };

        // Push in REVERSE declaration order:
        //   CreateItemOnFloor(string sTemplate, location lLoc, int bUseAppearAnimation)
        if (StackPushInteger(vm, 0) == 0
            || StackPushEngineStructure(vm, EngineStructureLocation, &location) == 0
            || StackPushString(vm, &resref) == 0)
        {
            failure = 5;
            return ObjectInvalid;
        }

        var dispatch = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedCreateItemOnFloor;
        int status = dispatch(implementer, _routineCreateItemOnFloor, 3);
        if (status != 0)
        {
            failure = 6;
            return ObjectInvalid;
        }

        uint objectId = ObjectInvalid;
        if (StackPopObject(vm, &objectId) == 0)
        {
            failure = 7;
            return ObjectInvalid;
        }

        if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
        {
            failure = 8;
        }

        return objectId;
    }

    /// <summary>
    /// Removes an object from the world via the engine's own NWScript
    /// DestroyObject(object oDestroy, float fDelay, int bNoFade, float fDelayUntilFade).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool DestroyObjectById(uint objectId, out int failure)
    {
        failure = 0;

        nint implementer = GetImplementer(out nint vm, out nint internalVm);
        if (implementer == 0)
        {
            failure = 1;
            return false;
        }

        nint table = Deref(implementer + CommandsTableOffset);
        if (table == 0)
        {
            failure = 2;
            return false;
        }

        if (_routineDestroyObject < 0)
        {
            _routineDestroyObject = FindRoutine(table, ExpectedDestroyObject);
        }

        if (_routineDestroyObject < 0)
        {
            failure = 3;
            return false;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 4;
            return false;
        }

        int stackPointer = *(int*)(internalVm + StackPointerOffset);

        // Push in REVERSE declaration order; no fade and no delay, so the item is gone
        // the instant the remote pickup lands rather than lingering as a ghost.
        if (StackPushFloat(vm, 0f) == 0
            || StackPushInteger(vm, 1) == 0
            || StackPushFloat(vm, 0f) == 0
            || StackPushObject(vm, objectId) == 0)
        {
            failure = 5;
            return false;
        }

        var dispatch = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedDestroyObject;

        // This plugin's own destroy is a removal the session decided (a host-side death or
        // pickup relayed here): ScriptDestroyGuardHook must let it through.
        ScriptDestroyGuardHook.Bypass = true;
        int status;
        try
        {
            status = dispatch(implementer, _routineDestroyObject, 4);
        }
        finally
        {
            ScriptDestroyGuardHook.Bypass = false;
        }
        if (status != 0)
        {
            failure = 6;
            return false;
        }

        if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
        {
            failure = 8;
        }

        return true;
    }

    /// <summary>Bit 0x04 of creature+0x9F0: attacking permitted. Cleared on phantoms so a
    /// stray hostile action can never make one swing at this instance's world.</summary>
    private const int CreAttackPermittedBit = 0x04;

    /// <summary>repute.2da row 5 — the Neutral faction, ~50 reputation both ways.</summary>
    private const ushort FactionNeutral = 5;

    /// <summary>The same, for callers choosing between it and <see cref="FactionPlayer"/>.</summary>
    public const int FactionNeutralId = FactionNeutral;

    /// <summary>
    /// Marks a spawned creature as belonging to a remote player.
    ///
    /// The faction is FORCED to Neutral rather than trusted from the blueprint. The stock
    /// `n_commm` ships Neutral, but a mod that edits its faction — or a co-op blueprint
    /// with a different one — would make enemies evaluate the phantom as a real target
    /// and, combined with min-1-hp below, recreate the immortal aggro sink 7.4 removed.
    /// The forced write costs a pointer read and makes the property independent of what is
    /// installed.
    ///
    /// It deliberately does NOT take the local player's faction, which an earlier version
    /// did. Faction 0 is PLAYER, so that made enemies treat the phantom exactly as they
    /// treat a PC — a valid, hostile target. Combined with plot, which zeroes all incoming
    /// damage, the result was an immortal aggro sink: `CreateNewAttackActions` only
    /// releases a target that reads dead, a plot creature's health never moves, so the
    /// first enemy to pick a phantom attacked it forever.
    ///
    /// The current shape (7.4's design): NO plot — plot zeroes every incoming damage
    /// component, so splash damage would visibly bounce off a teammate — but min-1-hp,
    /// so this instance still cannot kill a body only the remote player has authority
    /// over. min1hp alone re-creates the aggro lock (the target never reads dead), which
    /// is why <see cref="PacifyPhantom"/> exists and is called whenever the owning peer
    /// leaves combat or goes down. The attack bit is cleared so the phantom can never
    /// swing back and start a fight of its own.
    /// </summary>
    public static void MarkAsRemotePlayer(nint phantom)
    {
        *(int*)(phantom + ObjPlot) = 0;
        *(int*)(phantom + ObjMinOneHitPoint) = 1;
        *(int*)(phantom + ObjDeadSelectable) = 0;
        *(int*)(phantom + CreMovePermitted) &= ~CreAttackPermittedBit;

        nint stats = Deref(phantom + CreStats);
        if (stats != 0)
        {
            // DWORD, not ushort. The engine reads the whole dword (0x00513FF8,
            // 0x005BFADB); the old 16-bit write only worked because the upper half
            // happened to be zero, and a stale upper half would make the faction id read
            // as garbage — which resolves to reputation 50 and silently un-targets
            // everything (docs/research/combat-participation.md risk 4).
            *(uint*)(stats + StatsFactionId) = FactionNeutral;
        }
    }

    /// <summary>
    /// Marks a body materialised as a stand-in for an enemy that exists only in the owning
    /// peer's instance (see MaterialisedEnemies). Identical discipline to
    /// <see cref="MarkAsRemotePlayer"/> — no plot (plot zeroes incoming damage and the
    /// local player must be able to fight it), min-1-hp (only the owner may kill it), no
    /// selecting the corpse (its loot exists only in the owner's world), no swinging on
    /// its own (its swings are replayed from the owner's copy) — EXCEPT the faction, which
    /// the caller applies from the owner's published value rather than forcing Neutral: a
    /// materialised Sith trooper must read hostile or the fight it brought is invisible.
    /// </summary>

}
