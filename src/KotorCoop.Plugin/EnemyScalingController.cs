using KotorCoop.Protocol;

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
/// <b>Saved baselines</b> (shared block version 76, docs/research/enemy-health-persistence.md):
/// a scaled maximum is written into the save, so health scaling is GATED per world
/// generation. <see cref="BeginWorld"/> opens every world in
/// <see cref="EnemyScalingHealthMode.Pending"/>: no health write of any kind until the agent's
/// verdict arrives through <see cref="EnemyScalingImportGate"/>. Fresh (the agent proved no
/// creature here was ever scaled) scales as before; Import seeds the verified saved baselines
/// into <see cref="ScaledObjectLatch"/>, so those creatures are never multiplied again and are
/// only reconciled from their ORIGINAL maximum when the percentage differs
/// (<see cref="EnemyScalingReconcileRule"/>: ratio preserved, a corpse never resurrected, the
/// live stored maximum checked first); Refuse, a failed import or a stored maximum that does
/// not match its baseline leaves health exactly as loaded for the rest of the world. The latch
/// is exported page by page for the agent's sidecar capture.
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

    /// <summary>Latch capacity per module epoch; a module never holds this many hostiles.
    /// Equal to the baseline channel's capacity, so every latch fits one export.</summary>
    private const int LatchCapacity = EnemyScalingBaselineChannel.LatchCapacity;

    /// <summary>Scaled creatures logged per epoch, so the log names the first few and then
    /// counts the rest.</summary>
    private const int LogPerEpoch = 6;

    /// <summary>The retail new-game module. A world that follows an unannounced teardown with
    /// no load request is only classified as a new game when it is this module.</summary>
    private static ReadOnlySpan<byte> NewGameModule => "end_m01aa"u8;

    private readonly ScaledObjectLatch _scaled = new(LatchCapacity);
    private readonly uint[] _targets = new uint[1 + SharedBlock.PartyRosterSlots];
    private readonly EnemyScalingImportGate _importGate = new();
    private readonly byte[] _importPayload = new byte[EnemyScalingBaselineChannel.ImportPayloadLength];
    private readonly EnemyScalingLatchEntry[] _exportPage = new EnemyScalingLatchEntry[EnemyScalingBaselineChannel.ExportRecordsPerPage];
    private readonly byte[] _module = new byte[EnemyScalingBaselineChannel.ModuleNameCapacity];

    private int _scanCountdown;
    private int _targetCountdown;
    private int _loggedThisEpoch;
    private int _scaledThisEpoch;
    private int _reconciledThisEpoch;
    private bool _mismatchLogged;
    private int _lastHealthPercent = EnemyScalingRule.Vanilla;

    /// <summary>When the current world came up. See <see cref="UnresolvedImportGraceMs"/>.</summary>
    private long _worldUpAtMs;

    /// <summary>How long an Imported world waits for a session to resolve the percent before a
    /// zero (unresolved) word is taken as solo play's vanilla. Live 2026-09-24: a joiner's
    /// HostReload imported the host's 150% baselines while its agent was still reconnecting,
    /// reconciled them to 100% (the zero word), and back to 150% 1.5 s later.</summary>
    private const long UnresolvedImportGraceMs = 10_000;
    private int _lastDamagePercent = EnemyScalingRule.Vanilla;

    private uint _worldGeneration;
    private EnemyScalingWorldOrigin _origin;
    private uint _originProvenance;
    private uint _originFlags;
    private uint _worldLoadSerial;
    private EnemyScalingHealthMode _mode;
    private uint _exportGeneration;
    private uint _previousWorldGeneration;
    private uint _previousFinalExport;
    private uint _exportSequence;
    private int _exportPageIndex;
    private bool _exportDirty;
    private uint _lastImportSequence;
    private uint _saveSequence;

    /// <summary>The world-up page itself carries export generation 1; every later change of
    /// the latch or mode in the same world advances it.</summary>
    private uint _firstPagePublishedFor;

    /// <summary>The world generation this controller last opened (0 before the first world).
    /// Published with every native save request.</summary>
    public uint WorldGeneration => _worldGeneration;

    /// <summary>This world's health gate, for the status line.</summary>
    public EnemyScalingHealthMode Mode => _mode;

    /// <summary>Any world-owned invalidation: the next frame rescans and refreshes its damage
    /// targets. The latch is NOT released here — only <see cref="BeginWorld"/> does that,
    /// because <c>InvalidateAll</c> also runs for "transition flushed but not taken", where
    /// the same creatures stay standing with their scaled maxima and a cleared latch would
    /// multiply every one of them a second time.</summary>
    public void Invalidate()
    {
        _scanCountdown = 0;
        _targetCountdown = 0;
        PhantomHitHook.WatchDamageScaleTargets([]);
    }

    /// <summary>
    /// A new world came up (a new engine world epoch). Every id is stale: the latch is
    /// released, the generation advances, health is gated Pending until the agent's verdict,
    /// and the first export page announces the world, its origin and the final export
    /// generation of the world before it. <paramref name="newGameCandidate"/> is PluginHost's
    /// "unannounced teardown (or first world), no load request since the last world-up, load
    /// observers installed"; it becomes <see cref="EnemyScalingWorldOrigin.NewGame"/> only in
    /// the retail start module, and <see cref="EnemyScalingWorldOrigin.Unknown"/> otherwise.
    /// </summary>
    public void BeginWorld(
        EnemyScalingWorldOrigin origin, bool newGameCandidate, uint provenance, uint originFlags, uint worldLoadSerial)
    {
        _previousWorldGeneration = _worldGeneration;
        _previousFinalExport = _exportGeneration;
        _worldGeneration = unchecked(_worldGeneration + 1);
        if (_worldGeneration == 0)
        {
            _worldGeneration = 1;
        }

        _scaled.Clear();
        _importGate.Reset(_worldGeneration);
        _mode = EnemyScalingHealthMode.Pending;
        _worldUpAtMs = Environment.TickCount64;
        _exportGeneration = 1;
        _exportPageIndex = 0;
        _exportDirty = true;
        _scanCountdown = 0;
        _targetCountdown = 0;
        _loggedThisEpoch = 0;
        _scaledThisEpoch = 0;
        _reconciledThisEpoch = 0;
        _mismatchLogged = false;

        Array.Clear(_module);
        byte* moduleBuffer = stackalloc byte[SharedBlock.ModuleNameLength];
        int moduleLength = Odyssey.ReadCurrentModuleName(moduleBuffer, SharedBlock.ModuleNameLength);
        bool moduleKnown = moduleLength > 0 && moduleLength <= _module.Length;
        if (moduleKnown)
        {
            new ReadOnlySpan<byte>(moduleBuffer, moduleLength).CopyTo(_module);
        }

        if (origin is not (EnemyScalingWorldOrigin.Load or EnemyScalingWorldOrigin.Transition))
        {
            origin = newGameCandidate && moduleKnown
                && new ReadOnlySpan<byte>(moduleBuffer, moduleLength).SequenceEqual(NewGameModule)
                    ? EnemyScalingWorldOrigin.NewGame
                    : EnemyScalingWorldOrigin.Unknown;
        }

        _origin = origin;
        _originProvenance = origin == EnemyScalingWorldOrigin.Load ? provenance : 0;
        _originFlags = originFlags;
        _worldLoadSerial = worldLoadSerial;

        PluginHost.Log($"scaling-baseline: world {_worldGeneration} '{ModuleText()}' up ({origin}"
            + (origin == EnemyScalingWorldOrigin.Load ? $", load request {provenance}" : string.Empty)
            + "); enemy health gated until the agent's verdict");
        PumpExport();
    }

    /// <summary>Game thread, at the entry of the native SaveGame (via PluginHost): publishes
    /// the exact request for the agent's settled-archive capture. No engine call.</summary>
    public void PublishSaveRequest(int number, string name)
    {
        if (number < 0 || string.IsNullOrEmpty(name) || name.Length > EnemyScalingBaselineChannel.SaveNameCapacity)
        {
            return;
        }

        Span<byte> bytes = stackalloc byte[name.Length];
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c is < (char)0x20 or > (char)0x7E)
            {
                return;
            }

            bytes[i] = (byte)c;
        }

        _saveSequence = block.PublishEnemyBaselineSaveRequest(
            _saveSequence, number, _worldGeneration, DateTime.UtcNow.ToFileTimeUtc(), bytes);
    }

    public void OnFrame()
    {
        PumpImport();

        uint word = block.ScalingCombatWord;
        int healthPercent = EnemyScalingRule.HealthPercent(word);
        int damagePercent = EnemyScalingRule.DamagePercent(word);

        if (healthPercent != _lastHealthPercent || damagePercent != _lastDamagePercent)
        {
            PluginHost.Log($"scaling: enemy health {healthPercent}%, enemy damage {damagePercent}% (agent word 0x{word:X8})");
            _lastHealthPercent = healthPercent;
            _lastDamagePercent = damagePercent;

            // A raised health percent should reach creatures already latched at the old one?
            // Not for a first sighting in this world: the latch is per epoch by design (a second
            // write would compound), so a percent that rises mid-module applies to creatures
            // first seen from now on. IMPORTED (saved) creatures are the exception — they are
            // reconciled from their original maximum on the next scan. A module change
            // re-primes everything.
        }

        PhantomHitHook.SetDamagePercent(damagePercent);

        if (_targetCountdown-- <= 0)
        {
            _targetCountdown = TargetIntervalFrames;
            RefreshDamageTargets();
        }

        // Pending/Refused worlds write nothing. A vanilla percent still scans an Imported world,
        // because a saved scaled creature must come back to its original maximum.
        bool unresolvedImport = _mode == EnemyScalingHealthMode.Imported
            && (word & 0xFFFF) == 0
            && Environment.TickCount64 - _worldUpAtMs < UnresolvedImportGraceMs;
        if (_mode is EnemyScalingHealthMode.Fresh or EnemyScalingHealthMode.Imported
            && (healthPercent != EnemyScalingRule.Vanilla || _mode == EnemyScalingHealthMode.Imported)
            && !unresolvedImport
            && _scanCountdown-- <= 0)
        {
            _scanCountdown = ScanIntervalFrames;
            ScaleHitPoints(healthPercent);
        }

        PumpExport();
    }

    /// <summary>Consumes the agent's verdict pages for this world generation. Every page is
    /// acknowledged (ignored ones too) so the agent's sender never stalls.</summary>
    private void PumpImport()
    {
        if (!block.TryReadEnemyBaselineImport(_importPayload, out uint sequence) || sequence == _lastImportSequence)
        {
            return;
        }

        _lastImportSequence = sequence;
        EnemyScalingImportStep step = _mode == EnemyScalingHealthMode.Pending
            ? _importGate.Accept(_importPayload)
            : EnemyScalingImportStep.Ignored;
        block.EnemyBaselineImportAck = sequence;

        switch (step)
        {
            case EnemyScalingImportStep.Complete:
                ApplyVerdict(_importGate.Verdict, _importGate.Entries);
                break;

            case EnemyScalingImportStep.Failed:
                SetMode(EnemyScalingHealthMode.Refused, "the agent's baseline import contradicted itself");
                break;
        }
    }

    private void ApplyVerdict(EnemyScalingVerdict verdict, ReadOnlySpan<EnemyScalingLatchEntry> entries)
    {
        switch (verdict)
        {
            case EnemyScalingVerdict.Fresh:
                SetMode(EnemyScalingHealthMode.Fresh, "no creature here carries a scaled maximum");
                return;

            case EnemyScalingVerdict.Import:
                foreach (EnemyScalingLatchEntry entry in entries)
                {
                    if (!_scaled.AddImported(entry.ObjectId, entry.BaseMaximum, entry.AppliedPercent))
                    {
                        SetMode(EnemyScalingHealthMode.Refused,
                            $"imported baseline 0x{entry.ObjectId:X} did not fit the latch ({_scaled.Count}/{_scaled.Capacity})");
                        return;
                    }
                }

                SetMode(EnemyScalingHealthMode.Imported, $"{entries.Length} saved baseline(s) imported, never re-multiplied");
                return;

            default:
                SetMode(EnemyScalingHealthMode.Refused, "the agent could not prove this world's saved health");
                return;
        }
    }

    private void SetMode(EnemyScalingHealthMode mode, string why)
    {
        if (_mode == mode)
        {
            return;
        }

        _mode = mode;
        _scanCountdown = 0;
        _exportDirty = true;
        PluginHost.Log($"scaling-baseline: world {_worldGeneration} '{ModuleText()}' -> {mode} ({why})");
    }

    /// <summary>Publishes the latch: a changed latch (or mode) starts a new export generation
    /// at page 0; an acknowledged page advances to the next page, cycling so an agent that
    /// (re)starts mid-export still sees every page of the current generation. A single-page
    /// export is left in place once acknowledged.</summary>
    private void PumpExport()
    {
        if (_worldGeneration == 0)
        {
            return;
        }

        int pageCount = EnemyScalingBaselineChannel.ExportPageCount(_scaled.Count);
        if (_exportDirty)
        {
            _exportDirty = false;
            if (_exportSequence != 0 && !IsFirstPageOfWorld())
            {
                _exportGeneration = unchecked(_exportGeneration + 1);
            }

            _exportPageIndex = 0;
        }
        else if (_exportSequence != 0 && block.EnemyBaselineExportAck == _exportSequence && pageCount > 1)
        {
            _exportPageIndex = (_exportPageIndex + 1) % pageCount;
        }
        else
        {
            return;
        }

        int start = _exportPageIndex * EnemyScalingBaselineChannel.ExportRecordsPerPage;
        int count = Math.Clamp(_scaled.Count - start, 0, EnemyScalingBaselineChannel.ExportRecordsPerPage);
        for (int i = 0; i < count; i++)
        {
            int index = start + i;
            _exportPage[i] = new EnemyScalingLatchEntry(_scaled.IdAt(index), _scaled.BaseAt(index), _scaled.PercentAt(index));
        }

        bool complete = _mode is EnemyScalingHealthMode.Fresh or EnemyScalingHealthMode.Imported && !_scaled.Overflowed;
        ushort flags = (ushort)((complete ? EnemyScalingBaselineChannel.ExportFlagSnapshotComplete : 0)
            | (_scaled.Overflowed ? EnemyScalingBaselineChannel.ExportFlagOverflow : 0));
        _exportSequence = block.PublishEnemyBaselinePage(
            _exportSequence, _worldGeneration, _origin, _originProvenance, _originFlags, _worldLoadSerial,
            _mode, _exportGeneration, _previousWorldGeneration, _previousFinalExport,
            _exportPageIndex, pageCount, flags, ModuleBytes(), _exportPage.AsSpan(0, count));
        _firstPagePublishedFor = _worldGeneration;
    }

    private bool IsFirstPageOfWorld() => _firstPagePublishedFor != _worldGeneration;

    private ReadOnlySpan<byte> ModuleBytes()
    {
        int length = Array.IndexOf(_module, (byte)0);
        return _module.AsSpan(0, length < 0 ? _module.Length : length);
    }

    private string ModuleText() => System.Text.Encoding.ASCII.GetString(ModuleBytes());

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
        uint playerId = Odyssey.GetPlayerObjectId();
        if (playerId is 0 or Odyssey.ObjectInvalid)
        {
            return;
        }

        nint* creatures = stackalloc nint[CreatureCapacity];
        int count = Odyssey.CollectCreaturesByObjectTable(creatures, CreatureCapacity);

        byte* tagBuf = stackalloc byte[64];

        for (int i = 0; i < count && _mode is EnemyScalingHealthMode.Fresh or EnemyScalingHealthMode.Imported; i++)
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

            int latched = _scaled.IndexOf(objectId);
            if (latched >= 0)
            {
                // Scaled at first sighting this world, or imported from a verified save: never
                // multiplied again. An imported one only follows a changed percentage, from its
                // original maximum; a contradiction refuses the world and ends the scan.
                if (_scaled.IsImported(latched) && !ReconcileImported(creature, objectId, latched, percent))
                {
                    return;
                }

                continue;
            }

            if (percent == EnemyScalingRule.Vanilla || _scaled.IsFull)
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
                // leaves its current numbers unchanged. Retain the pre-scale maximum so the
                // baseline export can distinguish that result from an already-scaled maximum.
                _scaled.Add(objectId, max, percent);
                _exportDirty = true;
                continue;
            }

            // The engine's own ExecuteCommandSetMaxHitPoints order: the stored max first, then
            // the current-hp setter so everything derived from it is rebuilt by the engine.
            *(ushort*)(creature + Odyssey.ObjMaxHitPoints) = (ushort)newMax;
            Odyssey.SetCurrentHitPoints(creature, newCurrent);
            _scaled.Add(objectId, max, percent);
            _scaledThisEpoch++;
            _exportDirty = true;

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

    /// <summary>
    /// An imported (saved, already scaled) creature. Before its first write its live stored
    /// maximum must equal the baseline scaled by the recorded percentage — anything else means
    /// the import is not proof about this world, and the whole world is refused (returns false,
    /// the scan stops). Then, only when the percentage differs, the maximum is recomputed
    /// from the ORIGINAL maximum and current health keeps its ratio; a dead creature is never
    /// written. Same write order as a first sighting (stored maximum, then the engine's own
    /// current-hp setter). NEEDS-LIVE: the reconciliation write itself.
    /// </summary>
    private bool ReconcileImported(nint creature, uint objectId, int latched, int percent)
    {
        int baseMaximum = _scaled.BaseAt(latched);
        int applied = _scaled.PercentAt(latched);
        int stored = Odyssey.GetMaxHitPoints(creature);
        int current = Odyssey.GetCurrentHitPoints(creature);
        int expected = EnemyScalingBaselineRule.ScaleMaximum(baseMaximum, applied);

        if (!_scaled.IsVerified(latched))
        {
            if (stored != expected)
            {
                if (!_mismatchLogged)
                {
                    _mismatchLogged = true;
                    PluginHost.Log($"scaling-baseline: imported 0x{objectId:X} stores max {stored}, its baseline says "
                        + $"{expected} ({baseMaximum} at {applied}%)");
                }

                SetMode(EnemyScalingHealthMode.Refused, "a live stored maximum contradicts its saved baseline");
                return false;
            }

            _scaled.MarkVerified(latched);
        }

        if (applied == percent)
        {
            return true;
        }

        switch (EnemyScalingReconcileRule.Rescale(baseMaximum, applied, percent, current, stored, out int newCurrent, out int newMax))
        {
            case EnemyScalingRescaleResult.Write:
                *(ushort*)(creature + Odyssey.ObjMaxHitPoints) = (ushort)newMax;
                Odyssey.SetCurrentHitPoints(creature, newCurrent);
                _scaled.SetPercent(latched, percent);
                _exportDirty = true;
                if (_reconciledThisEpoch++ < LogPerEpoch)
                {
                    PluginHost.Log($"scaling-baseline: saved enemy 0x{objectId:X} hp {current}/{stored} -> {newCurrent}/{newMax} "
                        + $"({applied}% -> {percent}% of original {baseMaximum}), read back "
                        + $"{Odyssey.GetCurrentHitPoints(creature)}/{Odyssey.GetMaxHitPoints(creature)}");
                }

                return true;

            case EnemyScalingRescaleResult.Unchanged:
                _scaled.SetPercent(latched, percent);
                _exportDirty = true;
                return true;

            case EnemyScalingRescaleResult.Dead:
                // A corpse keeps its saved numbers and its recorded percentage: never resurrected.
                return true;

            default:
                SetMode(EnemyScalingHealthMode.Refused, $"imported 0x{objectId:X} no longer matches its baseline");
                return false;
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
