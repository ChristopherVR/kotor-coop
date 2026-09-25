namespace KotorCoop.Plugin;

// jedichoice - DEV VERB / LIVE-TEST PRIMITIVE for the Jedi-choice popup
// (docs/research/class-and-power-grants.md's 2026-09-25 section, JediChoicePromptController):
// manufactures a local "someone's Jedi choice happened" request, exactly the shape a peer's
// real relay would deliver, so the popup can be exercised on ONE instance without a second
// player or the Dantooine conversation. No network traffic: this only bumps the same
// ClassGrantRequestSerial field the agent bumps for a real peer grant: JediChoicePromptController
// itself does not know or care who wrote it. Local only.
internal sealed unsafe partial class CheatController
{
    private void JediChoice()
    {
        uint next = block.ClassGrantRequestSerial + 1;
        block.ClassGrantRequestClass = 0; // unused by the prompt flow; kept for wire shape only
        block.ClassGrantRequestSerial = next;

        PluginHost.Log($"jedichoice: manufactured local request #{next} - watch for the class-choice prompt (or an immediate AlreadyHas ack if this character already has a Jedi class)");
        Answer(SharedBlock.CheatStatusApplied, $"jedichoice: request #{next} filed - watch the plugin log");
    }
}
