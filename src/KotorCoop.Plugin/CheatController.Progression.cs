using System.Text;

namespace KotorCoop.Plugin;

// LIVE-TEST PRIMITIVES for the companion-XP and companion-equipment sync channels
// (docs/research/companion-state-sync.md's own live test plan §5): GrantXp, Equip. Neither
// verb is part of either sync channel itself — they exist only because this codebase had no
// way to trigger a script-granted XP award aimed at ONE creature, or a real re-equip of a
// specific creature, on demand. Same authority shape as every other staging cheat here: local
// only, follows the request's own target field rather than guessing.
internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// Adds <see cref="SharedBlockView.CheatAmount"/> (signed) to the named creature's XP,
    /// clamped at 0 — simulating a conversation/stealth-award/<c>GivePlotXP</c> script grant
    /// aimed at one creature only, the case <c>companion_xp_sync</c> exists for (kill XP already
    /// agrees between instances without this channel, per companion-state-sync.md §1.2).
    /// <see cref="SharedBlockView.CheatTarget"/> 0 means the local player.
    /// </summary>
    private void GrantXp(uint playerId)
    {
        uint targetId = block.CheatTarget == 0 ? playerId : block.CheatTarget;
        nint creature = Odyssey.ResolveServerObject(targetId);

        if (creature == 0)
        {
            Answer(SharedBlock.CheatStatusNoTarget, $"no object 0x{targetId:X} to grant xp to");
            return;
        }

        uint before = Odyssey.GetExperience(creature);
        long after = before + (long)block.CheatAmount;
        if (after < 0)
        {
            after = 0;
        }

        Odyssey.SetExperience(creature, (uint)after);
        Answer(SharedBlock.CheatStatusApplied, $"xp on 0x{targetId:X}: {before} -> {after}");
    }

    /// <summary>
    /// Re-equips the named creature with a real item through <c>Odyssey.EquipItemFromTemplate</c>
    /// — the exact call the phantom equipment channel already ships, aimed here at an ordinary
    /// party-roster companion body, the one case companion-state-sync.md §2.2 names as
    /// INFERRED-not-VERIFIED (the call takes a bare object id and never inspects what kind of
    /// body it is, but had never been exercised against a live companion before this verb).
    /// <see cref="SharedBlockView.CheatTarget"/> 0 means the local player;
    /// <see cref="SharedBlockView.CheatAmount"/> is the NWScript inventory slot (0/default = the
    /// right-weapon slot, the visible case the live test plan actually asks for).
    /// </summary>
    private void Equip()
    {
        byte* text = block.CheatText;
        int length = 0;
        while (length < SharedBlock.CheatTextLength - 1 && text[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, "equip: no resref given");
            return;
        }

        uint targetId = block.CheatTarget == 0 ? Odyssey.GetPlayerObjectId() : block.CheatTarget;
        int slot = block.CheatAmount > 0 ? block.CheatAmount : Odyssey.InventorySlotRightWeapon;
        string resref = Encoding.ASCII.GetString(text, length);

        bool applied = Odyssey.EquipItemFromTemplate(targetId, text, length, slot, out int failure);

        if (!applied)
        {
            PluginHost.Log($"equip: '{resref}' on 0x{targetId:X} slot {slot} failed - failure {failure}");
            Answer(
                SharedBlock.CheatStatusEngineRefused,
                $"equip '{resref}' on 0x{targetId:X} failed (failure {failure})");
            return;
        }

        PluginHost.Log($"equip: '{resref}' equipped on 0x{targetId:X} slot {slot}");
        Answer(SharedBlock.CheatStatusApplied, $"equipped '{resref}' on 0x{targetId:X} slot {slot}");
    }
}
