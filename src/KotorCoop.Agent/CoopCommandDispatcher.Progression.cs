using KotorCoop.Memory;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// The companion-XP/companion-gear staging verbs: grantxp, equip
/// (docs/research/companion-state-sync.md's own live test plan §5). Both take an explicit
/// object id or `me`, matching <see cref="ApplyEffect"/>'s own target parsing exactly.
/// </summary>
public sealed partial class CoopCommandDispatcher
{
    /// <summary>Adds a signed XP delta to the named creature — 0/`me` means the local player —
    /// simulating a script-granted award aimed at one creature for testing
    /// <c>companion_xp_sync</c>'s merge.</summary>
    private void GrantXp(CoopCommand command)
    {
        uint target = 0;
        if (!string.IsNullOrEmpty(command.Address) && command.Address != "0"
            && !ConsoleCommand.TryParseObjectId(command.Address, out target))
        {
            Reply($"grantxp: '{command.Address}' is not an object id (try 0x35, or 'me')");
            return;
        }

        SendCheat(new CheatRequest(CheatKinds.GrantXp, Target: target, Amount: command.Amount));
    }

    /// <summary>Re-equips the named creature with a real item — 0/`me` means the local player —
    /// through the same <c>Odyssey.EquipItemFromTemplate</c> call the phantom equipment channel
    /// already ships, for testing <c>companion_gear_sync</c> against a live companion body.
    /// </summary>
    private void Equip(CoopCommand command)
    {
        uint target = 0;
        if (!string.IsNullOrEmpty(command.Address) && command.Address != "0"
            && !ConsoleCommand.TryParseObjectId(command.Address, out target))
        {
            Reply($"equip: '{command.Address}' is not an object id (try 0x35, or 'me')");
            return;
        }

        SendCheat(new CheatRequest(
            CheatKinds.Equip, Target: target, Text: command.Password ?? string.Empty, Amount: command.Amount));
    }

    /// <summary>
    /// Adds a signed delta straight to the party wallet, through the SAME agent-side field
    /// read/write the shipped <c>progression_sync</c> channel already uses
    /// (<c>KotorGameBridge.TryReadProgression</c>/<c>TryWriteProgression</c>) — no plugin
    /// round trip. Simulates a vendor trade so <c>PartyPump.PumpProgression</c>'s own
    /// local-gold-change detection organically picks it up, the way it would a real purchase.
    /// </summary>
    private void GrantGold(int delta)
    {
        if (!bridge.TryReadProgression(out uint gold, out uint experience))
        {
            Reply("grantgold: no player loaded (not in a module yet)");
            return;
        }

        long after = gold + (long)delta;
        if (after < 0)
        {
            after = 0;
        }

        if (!bridge.TryWriteProgression((uint)after, experience))
        {
            Reply($"grantgold: write refused (gold {gold} -> {after})");
            return;
        }

        Reply($"gold {gold} -> {after}");
    }
}
