namespace KotorCoop.Session;

/// <summary>
/// The <c>showgui</c> live-test primitive (docs/research/companion-state-sync.md's dated
/// follow-up to §14): opens the local instance's own Equipment screen through the retail
/// engine's own <c>ShowSWInGameGui</c>/<c>SetCharacter</c> pair, showing a chosen party member.
/// Built because the unattended rig cannot press the retail Equip key or click a portrait
/// arrow, so <c>CompanionEquipSuppressionController</c>'s own close on a client had never been
/// seen firing live.
/// </summary>
public static partial class ConsoleCommand
{
    /// <summary>
    /// Parses <c>showgui equip [partyIndex]</c>. <c>equip</c> is the only subcommand today —
    /// the verb is namespaced for a future screen (inventory, character, ...) the same way
    /// <c>galaxymap</c> is namespaced over open/close/select/etc. <c>partyIndex</c> is 0-2
    /// (default 0): 0 is this instance's own controlled PC/leader, 1-2 are the active
    /// companions in CSWParty order. See <see cref="CoopCommandKind.ShowGuiEquip"/>.
    /// </summary>
    private static CoopCommand ParseShowGui(string[] rest)
    {
        const string usage = "showgui equip [partyIndex]  (dev-only: opens this instance's own "
            + "Equipment screen; partyIndex 0-2, default 0 - 0 = your PC, 1-2 = active companions)";

        if (rest.Length is 0 or > 2 || !string.Equals(rest[0], "equip", StringComparison.OrdinalIgnoreCase))
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        int partyIndex = 0;
        if (rest.Length == 2 && (!int.TryParse(rest[1], out partyIndex) || partyIndex is < 0 or > 2))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[1]}' is not a party index 0-2 - {usage}");
        }

        return new CoopCommand(CoopCommandKind.ShowGuiEquip, Amount: partyIndex);
    }
}
