namespace KotorCoop.Plugin;

// The `partyselect ok` staging cheat: confirms the native party-selection screen with its
// current selection, through the panel's own OK/Done handler (Odyssey.ConfirmPartySelect).
// See docs/research/galaxy-map-and-party-select.md's 2026-08-28 addendum.
internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// Confirms the currently-open <c>CSWGuiPartySelection</c> screen with whatever is
    /// currently selected on it — the same effect a real Done/Accept click has. Meant for the
    /// HOST (the only side with a real, unsuppressed copy of the screen); a CLIENT normally has
    /// nothing to confirm at all, since <see cref="PartySelectSuppressionController"/> tears its
    /// own copy down before a player ever sees it — that is reported as a refusal here rather
    /// than a false "applied".
    /// </summary>
    private void PartySelectOk()
    {
        if (!Odyssey.ConfirmPartySelect(out string detail))
        {
            Answer(SharedBlock.CheatStatusRefused, $"partyselect ok: {detail}");
            return;
        }

        PluginHost.Log($"partyselect: {detail}");
        Answer(SharedBlock.CheatStatusApplied, $"partyselect ok: {detail}");
    }
}
