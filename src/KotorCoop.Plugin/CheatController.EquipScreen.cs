namespace KotorCoop.Plugin;

// The `showgui equip [partyIndex]` DEV-ONLY LIVE-TEST PRIMITIVE (2026-09-25,
// docs/research/companion-state-sync.md's dated follow-up to §14): opens THIS instance's own
// Equipment screen through the retail engine's own ShowSWInGameGui/SetCharacter pair
// (Odyssey.ShowEquipScreenForPartyMember), showing a chosen party member, so
// CompanionEquipSuppressionController's own close ("the host equips companions") can finally be
// exercised on a rig that cannot press a key or click a portrait arrow.
internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// <see cref="SharedBlock.CheatAmount"/> is the party index (0 = this instance's own
    /// controlled PC/leader, 1-2 = the active companions in CSWParty order; default/blank 0 —
    /// <see cref="SharedBlockView.CheatAmount"/> reads 0 when the field was never set).
    /// </summary>
    private void ShowGuiEquip()
    {
        int partyIndex = block.CheatAmount;

        bool ok = Odyssey.ShowEquipScreenForPartyMember(
            partyIndex, out nint panel, out nint subject, out string detail);

        PluginHost.Log($"showgui equip {partyIndex}: {detail} (panel 0x{panel:X}, subject 0x{subject:X})");

        Answer(
            ok ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            $"showgui equip {partyIndex}: {detail} (panel 0x{panel:X}, subject 0x{subject:X})");
    }
}
