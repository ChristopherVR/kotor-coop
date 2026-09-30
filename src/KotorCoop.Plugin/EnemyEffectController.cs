namespace KotorCoop.Plugin;

/// <summary>
/// Both halves of the client-cast enemy-effect relay on the plugin side
/// (SharedBlock.EnemyEffect.cs, docs/research/enemy-effects.md, coop.ini
/// <c>enemy_effect_sync</c>):
/// <list type="bullet">
/// <item>MIRRORING SIDE: paces <see cref="EnemyEffectCapture"/>'s queue into the OUT mailbox, one
/// entry at a time, holding the next until the agent echoes the serial it took (or a timeout
/// says the agent is not listening).</item>
/// <item>ENEMY OWNER: applies a client's reported effect to the enemy's REAL body through
/// <see cref="OdysseyEffects.ApplyEffect"/> — the very call
/// <see cref="PeerEffectController"/> makes for a peer's forwarded effect and the
/// <c>applyeffect</c> staging verb makes — after <see cref="EnemyEffectApplyRule"/> has said the
/// target is a live creature that is not one of this instance's own bodies.</item>
/// </list>
/// Runs from the same <see cref="PluginHost.OnFrame"/> schedule the other relay controllers do.
/// </summary>
internal sealed unsafe class EnemyEffectController(SharedBlockView block)
{
    /// <summary>Frames the OUT mailbox waits for the agent's ack before moving on. About a
    /// second at the game's frame rate: long enough for the agent's tick, short enough that a
    /// missing agent does not wedge the queue.</summary>
    private const int OutAckTimeoutFrames = 60;

    /// <summary>Frames an apply the VM refused as busy (a script owns the stack) is retried
    /// before it is given up, so a cast that lands mid-conversation is not lost to one busy
    /// frame.</summary>
    private const int ApplyRetryFrames = 30;

    private readonly EnemyEffectThrottleRule _applyThrottle = new();

    private uint _nextOutSerial;
    private uint _outstandingSerial;
    private int _outstandingFrames;

    private uint _handledInSerial;
    private int _retryFrames;

    private int _logged;

    /// <summary>Module change: object ids are per world, so a queued capture, a remembered
    /// cooldown and an inbound request filed for the OLD world must not act in the new one. The
    /// inbound serial is ADOPTED, not replayed (the same lesson <see cref="PeerEffectController"/>
    /// learnt live, effect-sync.md §11.2).</summary>
    public void Invalidate()
    {
        EnemyEffectCapture.Invalidate();
        _applyThrottle.Clear();
        block.ClearEnemyEffectOut();
        _outstandingSerial = 0;
        _retryFrames = 0;
        _handledInSerial = EnemyEffectMailboxRule.SerialOf(block.GetEnemyEffectIn().Head);
        block.EnemyEffectInApplied = _handledInSerial;
    }

    public void ReleaseSession()
    {
        Invalidate();
        block.ClearEnemyEffectSession();
    }

    public void OnFrame()
    {
        if (!EnemyEffectCapture.Enabled)
        {
            return;
        }

        PublishOut();

        if (block.Command == SharedBlock.CommandSpawnAndDrive)
        {
            ApplyIn();
        }
    }

    private void PublishOut()
    {
        if (_outstandingSerial != 0)
        {
            if (block.EnemyEffectOutAck != _outstandingSerial && ++_outstandingFrames < OutAckTimeoutFrames)
            {
                return;
            }

            _outstandingSerial = 0;
        }

        if (!EnemyEffectCapture.TryDequeue(out uint enemyId, out uint kind, out uint durationMs, out uint magnitude, out ulong world, out uint epoch, out uint load, out uint hostSession, out uint hostLoad, out uint hostEpoch))
        {
            return;
        }

        if (epoch != block.LivenessNativeEpoch || load != block.WorldLoadSerial
            || !Odyssey.TryGetWorldEpoch(out ulong currentWorld) || currentWorld != world) return;
        _nextOutSerial = EnemyEffectMailboxRule.NextSerial(_nextOutSerial);
        block.PublishEnemyEffectOut(
            enemyId,
            EnemyEffectMailboxRule.PackHead(_nextOutSerial, kind),
            EnemyEffectMailboxRule.PackArgs(durationMs, magnitude), epoch, load, hostSession, hostLoad, hostEpoch);

        _outstandingSerial = _nextOutSerial;
        _outstandingFrames = 0;
    }

    private void ApplyIn()
    {
        (uint enemyId, uint head, uint args, uint sequence) = block.GetEnemyEffectIn();
        uint serial = EnemyEffectMailboxRule.SerialOf(head);
        if (serial == 0 || serial == _handledInSerial)
        {
            return;
        }

        if (!block.EnemyEffectInWorldCurrent(sequence))
        {
            Finish(serial, "refused effect from a retired world or session");
            return;
        }
        uint kind = EnemyEffectMailboxRule.KindOf(head);
        uint durationMs = EnemyEffectMailboxRule.ClampDurationMs(EnemyEffectMailboxRule.DurationMsOf(args));
        uint magnitude = EnemyEffectMailboxRule.MagnitudeOf(args);

        nint target = enemyId == 0 ? 0 : Odyssey.ResolveServerObject(enemyId);
        bool isCreature = PhantomHitHook.IsServerCreature(target);
        bool alive = isCreature && Odyssey.GetCurrentHitPoints(target) > 0;
        bool ourBody = isCreature && IsOurBody(enemyId, target);

        EnemyEffectApplyVerdict verdict = EnemyEffectApplyRule.Decide(
            kind, target != 0, isCreature, alive, ourBody, throttleAdmits: true);

        if (verdict == EnemyEffectApplyVerdict.Apply
            && !_applyThrottle.TryAdmit(enemyId, kind, Environment.TickCount64))
        {
            verdict = EnemyEffectApplyVerdict.RefuseThrottled;
        }

        if (verdict != EnemyEffectApplyVerdict.Apply)
        {
            Finish(serial, $"refused kind {kind} on 0x{enemyId:X}: {verdict}");
            return;
        }

        bool applied;
        int failure;
        EnemyEffectCapture.Suppress = true;
        try
        {
            applied = OdysseyEffects.ApplyEffect(enemyId, kind, durationMs, magnitude, out failure);
        }
        finally
        {
            EnemyEffectCapture.Suppress = false;
        }

        // Failures 1 and 4 are "the VM is not ready" (no implementer, a script owns the stack):
        // worth another try next frame, for a bounded number of frames. Anything else is final.
        if (!applied && failure is 1 or 4 && ++_retryFrames < ApplyRetryFrames)
        {
            return;
        }

        _retryFrames = 0;
        Finish(
            serial,
            applied
                ? $"applied kind {kind} to enemy 0x{enemyId:X} ({durationMs}ms, magnitude {magnitude})"
                : $"engine refused kind {kind} on enemy 0x{enemyId:X} (failure {failure})");
    }

    private void Finish(uint serial, string what)
    {
        _handledInSerial = serial;
        block.EnemyEffectInApplied = serial;

        if (_logged < 40)
        {
            _logged++;
            PluginHost.Log($"enemy-effect: {what}");
        }
    }

    /// <summary>The player, a party-roster body or a peer's phantom — never a legitimate target
    /// of a client's enemy effect.</summary>
    private bool IsOurBody(uint objectId, nint creature)
    {
        if (objectId == Odyssey.GetPlayerObjectId()
            || PhantomHitHook.IsWatchedPhantom(objectId)
            || Odyssey.IsPlayerCharacter(creature))
        {
            return true;
        }

        for (int slot = 0; slot < SharedBlock.PartyRosterSlots; slot++)
        {
            if (block.GetLocalPartyRosterObjectId(slot) == objectId)
            {
                return true;
            }
        }

        return false;
    }
}
