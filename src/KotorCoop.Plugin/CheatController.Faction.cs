namespace KotorCoop.Plugin;

// The `faction <id> [vsId]` diagnostic verb: reads the engine's own hostility verdict
// (GetAIStateReputation, both directions) plus each side's raw faction_id dword, read-only.
// See docs/research/combat-participation.md §10 — built to settle round 89's own open
// question ("does the client's own local player read hostile the way the host's does") with
// exact reads, on demand, instead of ad-hoc logging.
internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// Reports <c>CheatTarget</c> (0 = local player) versus <c>CheatAmount</c> (0 = local
    /// player) — both <c>GetAIStateReputation</c> directions and each side's raw faction id.
    /// Never writes anything.
    /// </summary>
    private void Faction(uint playerId)
    {
        uint targetId = block.CheatTarget == 0 ? playerId : block.CheatTarget;
        uint vsId = (uint)block.CheatAmount == 0 ? playerId : (uint)block.CheatAmount;

        nint target = Odyssey.ResolveServerObject(targetId);
        if (target == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, $"faction: no object 0x{targetId:X} to read");
            return;
        }

        nint vs = Odyssey.ResolveServerObject(vsId);
        if (vs == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, $"faction: no object 0x{vsId:X} to compare against");
            return;
        }

        int targetFaction = Odyssey.GetCreatureFaction(target);
        int vsFaction = Odyssey.GetCreatureFaction(vs);
        int targetVsVs = Odyssey.GetAIStateReputation(target, vsId);
        int vsVsTarget = Odyssey.GetAIStateReputation(vs, targetId);

        string detail = $"0x{targetId:X}(faction {targetFaction}) vs 0x{vsId:X}(faction {vsFaction}): "
            + $"GetAIStateReputation(0x{targetId:X},0x{vsId:X})={targetVsVs} "
            + $"GetAIStateReputation(0x{vsId:X},0x{targetId:X})={vsVsTarget} (0=neutral,1=friendly,2=hostile)";

        PluginHost.Log($"faction: {detail}");
        Answer(SharedBlock.CheatStatusApplied, detail);
    }
}
