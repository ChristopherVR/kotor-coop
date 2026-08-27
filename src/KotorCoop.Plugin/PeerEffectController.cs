namespace KotorCoop.Plugin;

/// <summary>
/// Moves the peer-effect channel both ways: publishes what <see cref="PhantomEffectHook"/>
/// captured landing on our phantoms into the OUT ring for the agent to forward, and applies
/// what a peer's hook captured landing on THIS player's phantom over there through
/// <see cref="OdysseyEffects"/>. The non-damage sibling of <see cref="IncomingDamageController"/>.
/// See docs/research/effect-sync.md.
///
/// Gated ON by default (<c>coop.ini</c> <c>effect_sync=1</c>) since the 2026-08-22 live pass:
/// stun, slow, frightened, choke, forcepush and whirlwind all round-tripped cleanly in BOTH
/// directions (host phantom and client phantom), plus local smoke tests, with no crash and no
/// regression in <see cref="PhantomHitHook"/>'s damage forwarding. See
/// docs/research/effect-sync.md's "Live pass (2026-08-22)" section for the full evidence —
/// including the three still-capture-only kinds (knockdown, paralyze, sleep), which correctly
/// refuse rather than guess at an unverified constructor signature. Set <c>effect_sync=0</c> to
/// go back to off if a regression turns up.
/// </summary>
internal sealed unsafe class PeerEffectController(SharedBlockView block)
{
    private const int IntervalFrames = 5;

    /// <summary>coop.ini <c>effect_sync</c>, overridable per session by the host — see
    /// <see cref="EffectiveBehaviourFlag"/>.</summary>
    private readonly EffectiveBehaviourFlag _effectSync = new(
        "effect_sync", SharedBlock.OverrideBitEffectSync, PluginConfig.ReadBool("effect_sync", true));

    private int _countdown;

    /// <summary>The hook's own monotonic sequence last time this ran a publish pass — a
    /// single scalar is enough because <see cref="PhantomEffectHook"/>'s ring is round-robin
    /// with one shared counter, not per-slot.</summary>
    private uint _publishedSequence;

    /// <summary>Highest serial applied per IN ring slot, so a re-read of the same entry
    /// (nothing new written yet) is never replayed twice.</summary>
    private readonly uint[] _appliedSerial = new uint[SharedBlock.PeerEffectCapacity];

    private int _logged;

    /// <summary>A module change reassigns every object id, so a remembered capture or apply
    /// watermark would name a different creature (or replay stale state onto a fresh one).</summary>
    public void Invalidate()
    {
        PhantomEffectHook.Invalidate();
        _publishedSequence = 0;
        Array.Clear(_appliedSerial);

        for (int i = 0; i < SharedBlock.PeerEffectCapacity; i++)
        {
            block.SetPeerEffectOut(i, 0, 0, 0, 0, 0);
        }
    }

    public void OnFrame()
    {
        if (!_effectSync.Resolve(block) || block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;

        PublishCaptured();
        ApplyIncoming();
    }

    /// <summary>Republishes every OUT ring slot whenever the hook's sequence has moved —
    /// unconditionally, like <see cref="BarkController"/> does for barks: the agent decides
    /// what is new by each slot's own serial, and a session that has not started yet simply
    /// never reads it.</summary>
    private void PublishCaptured()
    {
        uint sequence = PhantomEffectHook.Sequence;
        if (sequence == _publishedSequence)
        {
            return;
        }

        for (int i = 0; i < PhantomEffectHook.Capacity; i++)
        {
            PhantomEffectHook.Read(i, out uint phantomId, out uint kind, out uint durationMs, out uint magnitude, out uint serial);

            if (serial == 0 || phantomId == 0)
            {
                continue;
            }

            block.SetPeerEffectOut(i, phantomId, kind, durationMs, magnitude, serial);
        }

        _publishedSequence = sequence;
    }

    /// <summary>Replays whatever a peer's hook captured landing on our phantom over there, on
    /// the real local character here.</summary>
    private void ApplyIncoming()
    {
        uint playerId = Odyssey.GetPlayerObjectId();
        if (playerId == 0)
        {
            return;
        }

        for (int i = 0; i < SharedBlock.PeerEffectCapacity; i++)
        {
            (uint originPeerId, uint kind, uint durationMs, uint magnitude, uint serial) = block.GetPeerEffectIn(i);

            if (serial == 0 || serial == _appliedSerial[i])
            {
                continue;
            }

            _appliedSerial[i] = serial;

            if (originPeerId == 0)
            {
                continue;
            }

            bool applied = OdysseyEffects.ApplyEffect(playerId, kind, durationMs, magnitude, out int failure);

            if (_logged < 40)
            {
                _logged++;
                PluginHost.Log(applied
                    ? $"effect: applied kind {kind} from peer 0x{originPeerId:X} "
                        + $"(duration {durationMs}ms, magnitude {magnitude})"
                    : $"effect: refused kind {kind} from peer 0x{originPeerId:X} (failure {failure}) — "
                        + (failure == -1
                            ? "no apply-side constructor for this kind yet (see effect-sync.md)"
                            : "engine call refused; see the failure-code comments in OdysseyEffects"));
            }
        }
    }
}
