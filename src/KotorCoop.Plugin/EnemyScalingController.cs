namespace KotorCoop.Plugin;

/// <summary>
/// Applies the plugin's half of player-count scaling (docs/research/player-count-scaling.md):
/// coop.ini <c>enemy_health</c> and <c>enemy_damage</c>, resolved to percents by the agent and
/// read out of <see cref="SharedBlockView.ScalingCombatWord"/>.
///
/// <b>Health</b>: every hostile, non-plot, living creature that is not a player body gets its
/// STORED max hit points (CSWSObject+0xE0) and its current hit points scaled ONCE per object
/// id per module epoch, on the first pass that sees it — the module's own GIT creatures on
/// the epoch flip, encounter/script spawns within the next scan. The write order is the
/// engine's own <c>ExecuteCommandSetMaxHitPoints</c> sequence (Odyssey.Core.cs's
/// <c>ObjCurrentHitPoints</c> remarks: it writes +0xE0, then calls
/// <c>CSWSObject::SetCurrentHitPoints</c>), so the current-hp setter rebuilds whatever the
/// engine derives from the field — nothing is left poked without its follow-up. For a
/// non-PC creature the engine's own <c>CSWSCreature::GetMaxHitPoints</c> IS the stored field
/// (<see cref="MaxHitPointsRule.ForNonPc"/>), so the health bar, the "is it hurt" AI tests and
/// the mirror's clamp (<see cref="MirrorHealthRule"/>, via <see cref="CombatController"/>) all
/// read the scaled max. NEEDS-LIVE: this is the one engine write in the mod that raises a
/// stored max; the key ships at 100 until the checklist in the doc has run.
///
/// Runs on EVERY instance, not just the host: a client scales its own copies (native by
/// shared id, or a materialised stand-in) by the same percent — the setting is
/// host-authoritative on the wire, so the numbers agree — which is what keeps the mirror's
/// max clamp from cutting the host's scaled value down to vanilla. The host's copy stays the
/// authority for CURRENT health exactly as before; this only sets the ceiling both copies
/// share, and <see cref="MirrorHealthRule"/>'s lower-absolute leg pulls out any transient.
///
/// <b>Damage</b>: the percent is handed to <see cref="PhantomHitHook"/> every pass, along
/// with the set of player bodies (the local PC and the local party roster) a hostile hit on
/// which is scaled at the detour. See <see cref="PhantomHitHook.WatchDamageScaleTargets"/>.
///
/// MAIN THREAD ONLY, from the epoch-valid section of <c>PluginHost.OnFrame</c>, BEFORE
/// <see cref="CombatController"/> so a first-sighting max write precedes the same frame's
/// mirror clamp.
/// </summary>
internal sealed unsafe class EnemyScalingController(SharedBlockView block)
{
    /// <summary>Frames between hit-point scans. A creature spawned between two scans fights
    /// at vanilla for at most this long; each scan walks the engine's bounded live object table,
    /// kept off the per-frame path.</summary>
    private const int ScanIntervalFrames = 90;

    /// <summary>Frames between refreshes of the damage-target set. Cheap (ten block reads),
    /// but a party change is not a per-frame event either.</summary>
    private const int TargetIntervalFrames = 30;

    private const int CreatureCapacity = 512;

    /// <summary>Latch capacity per module epoch; a module never holds this many hostiles.</summary>
    private const int LatchCapacity = 256;

    /// <summary>Scaled creatures logged per epoch, so the log names the first few and then
    /// counts the rest.</summary>
    private const int LogPerEpoch = 6;

    private readonly ScaledObjectLatch _scaled = new(LatchCapacity);
    private readonly uint[] _targets = new uint[1 + SharedBlock.PartyRosterSlots];

    private int _scanCountdown;
    private int _targetCountdown;
    private int _loggedThisEpoch;
    private int _scaledThisEpoch;
    private int _lastHealthPercent = EnemyScalingRule.Vanilla;
    private int _lastDamagePercent = EnemyScalingRule.Vanilla;

    /// <summary>A world just came up (or went away): every id is stale, every latch is
    /// released, and the next frame scans. Called on the epoch flip alongside every other
    /// per-module invalidation.</summary>
    public void Invalidate()
    {
        _scaled.Clear();
        _scanCountdown = 0;
        _targetCountdown = 0;
        _loggedThisEpoch = 0;
        _scaledThisEpoch = 0;
        PhantomHitHook.WatchDamageScaleTargets([]);
    }

    public void OnFrame()
    {
        uint word = block.ScalingCombatWord;
        int healthPercent = EnemyScalingRule.HealthPercent(word);
        int damagePercent = EnemyScalingRule.DamagePercent(word);

        if (healthPercent != _lastHealthPercent || damagePercent != _lastDamagePercent)
        {
            PluginHost.Log($"scaling: enemy health {healthPercent}%, enemy damage {damagePercent}% (agent word 0x{word:X8})");
            _lastHealthPercent = healthPercent;
            _lastDamagePercent = damagePercent;

            // A raised health percent should reach creatures already latched at the old one?
            // No: the latch is per epoch by design (a second write would compound), so a
            // percent that rises mid-module applies to creatures first seen from now on. A
            // module change re-primes everything.
        }

        PhantomHitHook.SetDamagePercent(damagePercent);

        if (_targetCountdown-- <= 0)
        {
            _targetCountdown = TargetIntervalFrames;
            RefreshDamageTargets();
        }

        if (healthPercent == EnemyScalingRule.Vanilla)
        {
            return;
        }

        if (_scanCountdown-- > 0)
        {
            return;
        }

        _scanCountdown = ScanIntervalFrames;
        ScaleHitPoints(healthPercent);
    }

    /// <summary>The bodies a hostile hit on which counts as "damage to a player": the local
    /// PC and every live local party roster body. Phantoms and released companions are
    /// watched by the hook's own slots already.</summary>
    private void RefreshDamageTargets()
    {
        int count = 0;

        uint playerId = Odyssey.GetPlayerObjectId();
        if (playerId is not (0 or Odyssey.ObjectInvalid))
        {
            _targets[count++] = playerId;
        }

        for (int slot = 0; slot < SharedBlock.PartyRosterSlots; slot++)
        {
            uint id = block.GetLocalPartyRosterObjectId(slot);
            if (id is not (0 or Odyssey.ObjectInvalid) && Odyssey.ResolveServerObject(id) != 0)
            {
                _targets[count++] = id;
            }
        }

        PhantomHitHook.WatchDamageScaleTargets(_targets.AsSpan(0, count));
    }

    private void ScaleHitPoints(int percent)
    {
        if (_scaled.IsFull)
        {
            return;
        }

        uint playerId = Odyssey.GetPlayerObjectId();
        if (playerId is 0 or Odyssey.ObjectInvalid)
        {
            return;
        }

        nint* creatures = stackalloc nint[CreatureCapacity];
        int count = Odyssey.CollectCreaturesByObjectTable(creatures, CreatureCapacity);

        byte* tagBuf = stackalloc byte[64];

        for (int i = 0; i < count && !_scaled.IsFull; i++)
        {
            nint creature = creatures[i];

            // ⚠ A vtable match is NOT a live object (GhostSweepController's first live run
            // crashed exactly there): only a candidate the engine's own object table maps
            // back to this very pointer is real.
            uint objectId = *(uint*)(creature + 0x04);
            if (objectId is 0 or Odyssey.ObjectInvalid || Odyssey.ResolveServerObject(objectId) != creature)
            {
                continue;
            }

            if (_scaled.Contains(objectId))
            {
                continue;
            }

            // Never a player body: the PC (its stored max is 0 anyway), our own party, a
            // peer's phantom. Never a plot creature (damage is zeroed on it regardless) and
            // never a corpse.
            if (objectId == playerId || IsLocalPartyBody(objectId) || PhantomHitHook.IsWatchedPhantom(objectId)
                || Odyssey.IsPlayerCharacter(creature) || Odyssey.IsPlot(creature))
            {
                continue;
            }

            int current = Odyssey.GetCurrentHitPoints(creature);
            int max = Odyssey.GetMaxHitPoints(creature);
            if (current <= 0 || max <= 0)
            {
                continue;
            }

            // The engine's own hostility question (1 friendly / 2 hostile / 0 neutral):
            // bystanders, merchants and story NPCs keep their numbers.
            if (Odyssey.GetAIStateReputation(creature, playerId) != 2)
            {
                continue;
            }

            if (!EnemyScalingRule.ScaledHitPoints(current, max, percent, out int newCurrent, out int newMax))
            {
                // This object has been considered at this percent even when integer rounding
                // leaves its current numbers unchanged. Retain the pre-scale maximum so a
                // future persistence export can distinguish that result from an already-scaled
                // maximum; this in-epoch latch is not a successful-load baseline import.
                _scaled.Add(objectId, max, percent);
                continue;
            }

            // The engine's own ExecuteCommandSetMaxHitPoints order: the stored max first, then
            // the current-hp setter so everything derived from it is rebuilt by the engine.
            *(ushort*)(creature + Odyssey.ObjMaxHitPoints) = (ushort)newMax;
            Odyssey.SetCurrentHitPoints(creature, newCurrent);
            _scaled.Add(objectId, max, percent);
            _scaledThisEpoch++;

            if (_loggedThisEpoch < LogPerEpoch)
            {
                _loggedThisEpoch++;
                Odyssey.ReadObjectTag(creature, tagBuf, 64);
                string tag = ReadAscii(tagBuf, 64);
                int readBack = Odyssey.GetCurrentHitPoints(creature);
                int maxBack = Odyssey.GetMaxHitPoints(creature);
                PluginHost.Log(
                    $"scaling: enemy 0x{objectId:X} '{tag}' hp {current}/{max} -> {newCurrent}/{newMax} "
                    + $"({percent}%), read back {readBack}/{maxBack}"
                    + (_loggedThisEpoch == LogPerEpoch ? " (further enemies this module counted, not logged)" : string.Empty));
            }
        }
    }

    private bool IsLocalPartyBody(uint objectId)
    {
        for (int slot = 0; slot < SharedBlock.PartyRosterSlots; slot++)
        {
            if (block.GetLocalPartyRosterObjectId(slot) == objectId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Enemies scaled since the last epoch flip, for the status line.</summary>
    public int ScaledThisEpoch => _scaledThisEpoch;

    private static string ReadAscii(byte* buffer, int capacity)
    {
        int length = 0;
        while (length < capacity && buffer[length] != 0)
        {
            length++;
        }

        return System.Text.Encoding.ASCII.GetString(buffer, length);
    }
}
