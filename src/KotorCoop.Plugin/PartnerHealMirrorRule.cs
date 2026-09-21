namespace KotorCoop.Plugin;

/// <summary>Pure sender-side validation for partner Heal/Cure mirroring.</summary>
internal static class PartnerHealMirrorRule
{
    internal const ushort EngineHealType = 39;
    internal const uint SpellCure = 10;
    internal const uint SpellHeal = 28;
    internal const float PartnerRangeSquared = 15f * 15f;

    internal static bool ShouldMirror(
        ushort engineType, uint creatorId, uint localPlayerId, uint spellId, uint targetId,
        ReadOnlySpan<uint> localTargets, bool targetIsWatchedPhantom, uint magnitude) =>
        engineType == EngineHealType
        && creatorId != 0
        && creatorId == localPlayerId
        && spellId is SpellCure or SpellHeal
        && targetId != 0
        && !targetIsWatchedPhantom
        && magnitude > 0
        && Contains(localTargets, targetId);

    internal static bool IsSupportedSpell(uint spellId) => spellId is SpellCure or SpellHeal;

    internal static bool IsWithinPartnerRange(Vec3 caster, Vec3 phantom)
    {
        float dx = caster.X - phantom.X;
        float dy = caster.Y - phantom.Y;
        float dz = caster.Z - phantom.Z;
        // k_inc_force.nss uses a strict GetDistanceBetween() < 15.0 guard; keep the
        // native rule's boundary exact so a target at precisely 15m is not mirrored.
        return (dx * dx) + (dy * dy) + (dz * dz) < PartnerRangeSquared;
    }

    private static bool Contains(ReadOnlySpan<uint> values, uint value)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (values[i] == value)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>Testable deduplication for one sender-side cast. A Heal/Cure script applies to
/// several local roster bodies in one invocation; only the first such effect should create a
/// remote Heal per phantom. The generation is advanced by the frame preparation pass, while
/// the phantom id is part of the key so a reused slot cannot inherit a claim from its old body.
/// </summary>
internal sealed class PartnerHealDedupRule
{
    private readonly uint[] _generation;
    private readonly uint[] _spell;
    private readonly uint[] _phantom;

    internal PartnerHealDedupRule(int capacity)
    {
        _generation = new uint[Math.Max(1, capacity)];
        _spell = new uint[_generation.Length];
        _phantom = new uint[_generation.Length];
    }

    internal bool TryClaim(int slot, uint generation, uint spellId, uint phantomId)
    {
        if (slot < 0 || slot >= _generation.Length || generation == 0 || spellId == 0 || phantomId == 0)
        {
            return false;
        }

        if (_generation[slot] == generation && _spell[slot] == spellId && _phantom[slot] == phantomId)
        {
            return false;
        }

        _generation[slot] = generation;
        _spell[slot] = spellId;
        _phantom[slot] = phantomId;
        return true;
    }

    internal void Clear()
    {
        Array.Clear(_generation);
        Array.Clear(_spell);
        Array.Clear(_phantom);
    }
}
