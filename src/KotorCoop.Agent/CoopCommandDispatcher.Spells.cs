using KotorCoop.Memory;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>The live-cast staging verbs: cast, grantpower (docs/research/effect-sync.md
/// §14).</summary>
public sealed partial class CoopCommandDispatcher
{
    /// <summary>
    /// Runs <c>Odyssey.CastSpellAtObject</c> on the plugin — the live-test primitive that makes
    /// the LOCAL PLAYER cast a spells.2da row through the engine's own action queue, exactly as
    /// a player's radial-menu cast does. Unlike <c>applyeffect</c> this is a real cast: the
    /// caster is stamped as the local player, so the capture hooks
    /// <c>partner_buffs</c>/<c>enemy_effect_sync</c> need can finally see it.
    /// </summary>
    private void CastSpell(CoopCommand command)
    {
        if (!ConsoleCommand.TryParseObjectId(command.Address ?? string.Empty, out uint target) || target == 0)
        {
            Reply($"cast: '{command.Address}' is not an object id (try 0x35, or 'me')");
            return;
        }

        SendCheat(new CheatRequest(
            CheatKinds.Cast, Target: target, Amount: command.Amount, Charges: command.EffectMagnitude));
    }
}
