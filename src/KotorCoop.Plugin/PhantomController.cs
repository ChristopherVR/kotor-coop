
namespace KotorCoop.Plugin;

/// <summary>
/// Drives one spawned creature per remote player, once per frame, on the game's thread.
///
/// Every engine call in here is reached from the frame hook, never from the plugin's worker
/// thread. The worker only touches the shared block.
/// </summary>
internal sealed unsafe partial class PhantomController(SharedBlockView block)
{
    // There used to be a WalkUnitsPerSecond constant of 5.4 here, described as bounding how
    // far a phantom may travel per frame. It bounded nothing: the value it fed was computed
    // and never read. The phantom's speed comes entirely from the engine, because movement
    // is an ActionMoveToLocation the AI executes — which is also why the wrong number was
    // harmless. (For reference, had it mattered: speed is not creaturespeed.2da either;
    // GetWalkRate 0x004F1B20 and GetRunRate 0x004F1BE0 read the creature's appearance
    // record, i.e. appearance.2da walkdist/rundist, which for the PC's row 114 is 1.6/3.96.)


    private readonly nint[] _phantoms = new nint[SharedBlock.SlotCapacity];
    private readonly uint[] _phantomIds = new uint[SharedBlock.SlotCapacity];
    private readonly Vec3[] _lastGoal = new Vec3[SharedBlock.SlotCapacity];
    private readonly int[] _repathCooldown = new int[SharedBlock.SlotCapacity];

    /// <summary>The animation last mirrored onto each stationary phantom, so a played
    /// animation is issued once when it appears rather than restarted every frame.</summary>
    private readonly uint[] _lastAnim = new uint[SharedBlock.SlotCapacity];

    /// <summary>Last one-shot sequence applied per slot, so each fires exactly once.</summary>
    private readonly uint[] _lastOneShotSerial = new uint[SharedBlock.SlotCapacity];

    /// <summary>Direct placement instead of a pathfinding walk (coop.ini movement=walk opts
    /// out). This is the seamless mode; the walk is the conservative fallback.</summary>
    private readonly bool _directMovement = !string.Equals(
        PluginConfig.Read("movement"), "walk", StringComparison.OrdinalIgnoreCase);

    /// <summary>Where each phantom's peer was when its speed window opened, and when.</summary>
    private readonly Vec3[] _speedFrom = new Vec3[SharedBlock.SlotCapacity];
    private readonly double[] _speedFromTime = new double[SharedBlock.SlotCapacity];

    /// <summary>The peer's measured planar speed, smoothed over a window.</summary>
    private readonly float[] _peerSpeed = new float[SharedBlock.SlotCapacity];

    /// <summary>
    /// How long a speed sample runs before it is committed. Measuring frame to frame is
    /// useless here: the block only changes when the agent writes it (~30 Hz) while this loop
    /// runs at 60, so half the samples see zero movement and the estimate collapses. A window
    /// spans several updates and yields a steady number.
    /// </summary>
    private const double SpeedWindowSeconds = 0.15;

    private bool _modeLogged;

    /// <summary>True while the phantom is lying in a death pose because its player is at
    /// zero health. Tracked so the pose is set once, not re-issued every step.</summary>
    private readonly bool[] _down = new bool[SharedBlock.SlotCapacity];

    /// <summary>The current/max hit points last written onto each stand-in, so the engine
    /// write happens on change only (per-frame engine writes are latched per object).</summary>
    private readonly int[] _appliedHealth = new int[SharedBlock.SlotCapacity];
    private readonly int[] _appliedMaxHealth = new int[SharedBlock.SlotCapacity];

    /// <summary>The peer's in-combat flag last step, so leaving combat is an edge: that
    /// is when local enemies locked onto the phantom must be released — a min-1-hp body
    /// never reads dead, so nothing lets go on its own (7.4).</summary>
    private readonly bool[] _peerInCombat = new bool[SharedBlock.SlotCapacity];

    /// <summary>FNV hash of the resref last equipped per (phantom, equip slot); 0 = none.</summary>
    private readonly uint[,] _appliedEquip = new uint[SharedBlock.SlotCapacity, SharedBlock.EquipCount];

    /// <summary>Consecutive equip failures per (phantom, equip slot) since the resref last
    /// changed — bounds the retry <see cref="EquipRetryRule"/> otherwise runs forever on a
    /// persistent (not transient) failure. See <see cref="EquipRetryRule.MaxConsecutiveFailures"/>'s
    /// own doc comment for the leak this closes.</summary>
    private readonly int[,] _equipFailures = new int[SharedBlock.SlotCapacity, SharedBlock.EquipCount];

    /// <summary>True for a body that was spawned before the client half of the world had
    /// finished building (<see cref="PhantomSpawnReadinessRule"/>'s wait budget ran out first).
    /// Such a body may render perfectly or may be permanently invisible, and nothing readable
    /// from memory tells the two apart — so it is replaced once the world does come up. Per
    /// BODY: cleared with the slot.</summary>
    private readonly bool[] _provisional = new bool[SharedBlock.SlotCapacity];

    /// <summary>How many provisional bodies this slot has already had replaced. Deliberately
    /// NOT cleared by <see cref="ClearSlot"/> — it exists to stop a destroy/spawn loop on a
    /// rig where the client world never satisfies the gate, and clearing it with the body it
    /// bounds would defeat the bound. Reset by <see cref="Invalidate"/>, i.e. once per
    /// world.</summary>
    private readonly int[] _provisionalReplacements = new int[SharedBlock.SlotCapacity];

    /// <summary>True once the phantom's name has been written to its CLIENT TWIN, which is
    /// what the selection band renders. Spawn writes the server locstring, but the twin
    /// does not exist yet then, so the name is re-asserted once the twin appears.</summary>
    private readonly bool[] _twinNamed = new bool[SharedBlock.SlotCapacity];

    /// <summary>The same latch, one field over, for the peer's LOOK: true once the peer's
    /// appearance row has reached this body's CLIENT TWIN (<c>Odyssey.RebuildClientAppearance</c>
    /// returning true), which is the half that renders. Per BODY — cleared with the slot — so a
    /// re-spawned phantom always re-dresses its new twin. See <see cref="PeerIdentityRule"/>.</summary>
    private readonly bool[] _twinDressed = new bool[SharedBlock.SlotCapacity];

    /// <summary>The last usable appearance row each PEER published, kept across module changes
    /// (unlike everything else here, which is per body) and dropped only when the peer itself
    /// leaves the slot. A peer publishes 0 for about a second either side of a transition while
    /// its own agent re-resolves its player object — exactly the window a phantom is re-spawned
    /// in — and dressing a fresh body from that zero left it wearing the blueprint.</summary>
    private readonly uint[] _appearanceCache = new uint[SharedBlock.SlotCapacity];

    /// <summary>Bounded logging for the identity lines: enough to see a session's spawns and
    /// re-dresses, never one per frame.</summary>
    private int _identityLogged;

    private const int MaxIdentityLines = 40;

    private int _phantomCount;

    private bool _spawnDisabled;

    /// <summary>Forgets everything a slot derived from its body, without touching the engine.</summary>
    private void ClearSlot(int slot)
    {
        _phantoms[slot] = 0;
        _phantomIds[slot] = 0;
        _lastGoal[slot] = default;
        _repathCooldown[slot] = 0;
        _down[slot] = false;
        _appliedHealth[slot] = 0;
        _appliedMaxHealth[slot] = 0;
        _peerInCombat[slot] = false;
        _hostileToLocals[slot] = false;
        _factionKnown[slot] = false;
        _stealthVisualApplied[slot] = false;
        _stealthVisualKnown[slot] = false;
        _smoothing[slot] = false;
        _shownAtTime[slot] = 0;
        _speedFromTime[slot] = 0;
        _peerSpeed[slot] = 0f;
        _lastMovedAt[slot] = 0;
        _lastAnim[slot] = 0;
        _lastOneShotSerial[slot] = 0;
        _twinNamed[slot] = false;
        _provisional[slot] = false;

        // Per BODY, deliberately: the next body this slot gets has a brand-new client twin,
        // built from the blueprint, that has to be dressed again. _appearanceCache is NOT
        // cleared here — that is the PEER's identity, and it outlives any one body.
        _twinDressed[slot] = false;
        _destroyAttempts[slot] = 0;

        for (int j = 0; j < SharedBlock.EquipCount; j++)
        {
            _appliedEquip[slot, j] = 0;
            _equipFailures[slot, j] = 0;
        }

        block.SetPhantomId(slot, 0);

        // Stop attributing damage to a body that no longer exists. Watch also resets the
        // running total, so a slot reused by a later joiner starts from zero instead of
        // inheriting the previous occupant's.
        PhantomHitHook.Watch(slot, 0);
    }

    /// <summary>Recounts live bodies and publishes the figure. Derived, never incremented:
    /// with stable slots the population and the highest occupied index are different
    /// numbers, and only one of them is what the agent's readout means.</summary>
    private void PublishPhantomCount()
    {
        int live = 0;
        for (int i = 0; i < _phantoms.Length; i++)
        {
            if (_phantoms[i] != 0)
            {
                live++;
            }
        }

        _phantomCount = live;
        block.PhantomCount = (uint)live;
    }
}
