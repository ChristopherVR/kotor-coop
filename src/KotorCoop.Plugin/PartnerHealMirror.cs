namespace KotorCoop.Plugin;

/// <summary>
/// Decides whether a type-39 effect is the local player's Force Heal/Cure cast that should
/// also reach a nearby remote player. The engine's spell scripts walk the local CSWParty;
/// this small sender-side mirror supplies the missing remote target through the existing
/// PeerEffect Heal event. It is deliberately behind the existing <c>partner_buffs</c> switch
/// (default off until a real cast is watched).
/// </summary>
internal static unsafe class PartnerHealMirror
{
    private const int EffectCreator = 0x18;
    private const int EffectSpellId = 0x1C;

    private static readonly bool Enabled = PluginConfig.ReadBool("partner_buffs", true);

    private static readonly uint[] _localTargets = new uint[SharedBlock.PartyRosterSlots + 1];
    private static readonly uint[] _phantoms = new uint[SharedBlock.SlotCapacity];
    private static readonly PartnerHealDedupRule Dedup = new(SharedBlock.SlotCapacity);

    private static uint _localPlayerId;
    private static int _localTargetCount;
    private static int _phantomCount;
    private static uint _generation;

    /// <summary>
    /// Refreshes the next engine-frame's sender-side target set. This runs on the game's
    /// frame thread, so engine position reads stay outside the effect hook.
    /// </summary>
    internal static void PrepareNextFrame(SharedBlockView block)
    {
        _generation++;
        if (_generation == 0)
        {
            Dedup.Clear();
            // Generation zero is reserved by PartnerHealDedupRule as "no frame". Start
            // the wrapped generation at one so this prepared frame remains eligible.
            _generation = 1;
        }

        _localPlayerId = 0;
        _localTargetCount = 0;
        _phantomCount = 0;
        Array.Clear(_localTargets);
        Array.Clear(_phantoms);

        if (!Enabled)
        {
            return;
        }

        nint player = Odyssey.GetPlayerCreaturePointer();
        if (player == 0)
        {
            return;
        }

        _localPlayerId = *(uint*)(player + 0x04);
        if (_localPlayerId == 0)
        {
            return;
        }

        _localTargets[_localTargetCount++] = _localPlayerId;
        for (int slot = 0; slot < SharedBlock.PartyRosterSlots; slot++)
        {
            uint id = block.GetLocalPartyRosterObjectId(slot);
            if (id != 0 && !ContainsTarget(id))
            {
                _localTargets[_localTargetCount++] = id;
            }
        }

        Vec3 caster = Odyssey.GetPosition(player);
        int remoteCount = (int)Math.Min(block.RemoteCount, (uint)SharedBlock.SlotCapacity);
        for (int slot = 0; slot < remoteCount; slot++)
        {
            RemoteView remote = block.GetRemote(slot);
            uint phantom = block.GetPhantomId(slot);
            if (!remote.Active || phantom == 0
                || !PartnerHealMirrorRule.IsWithinPartnerRange(caster, remote.Position))
            {
                continue;
            }

            _phantoms[_phantomCount++] = phantom;
        }
    }

    private static bool ContainsTarget(uint id)
    {
        for (int i = 0; i < _localTargetCount; i++)
        {
            if (_localTargets[i] == id)
            {
                return true;
            }
        }

        return false;
    }

    internal static void Invalidate()
    {
        _localPlayerId = 0;
        _localTargetCount = 0;
        _phantomCount = 0;
        _generation = 0;
        Array.Clear(_localTargets);
        Array.Clear(_phantoms);
        Dedup.Clear();
    }

    /// <summary>Called from the already-installed generic effect hook.</summary>
    internal static void Consider(nint effect, uint targetId)
    {
        if (!Enabled || effect == 0 || _localPlayerId == 0 || _phantomCount == 0)
        {
            return;
        }

        ushort engineType = *(ushort*)(effect + 0x08);
        uint creatorId = *(uint*)(effect + EffectCreator);
        uint spellId = *(uint*)(effect + EffectSpellId);

        if (!PartnerHealMirrorRule.IsSupportedSpell(spellId)
            || !PartnerHealMirrorRule.ShouldMirror(
                engineType, creatorId, _localPlayerId, spellId, targetId,
                _localTargets.AsSpan(0, _localTargetCount), PhantomHitHook.IsWatchedPhantom(targetId),
                1))
        {
            return;
        }

        if (!PhantomEffectHook.TryClassify(effect, out uint kind, out uint durationMs, out uint magnitude)
            || kind != PeerEffectKindValues.Heal || magnitude == 0)
        {
            return;
        }

        for (int i = 0; i < _phantomCount; i++)
        {
            uint phantom = _phantoms[i];
            if (phantom == 0 || !Dedup.TryClaim(i, _generation, spellId, phantom))
            {
                continue;
            }

            PhantomEffectHook.Capture(phantom, kind, durationMs, magnitude);
        }
    }
}
