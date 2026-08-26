namespace KotorCoop.Plugin;

// Dialogue verbs: SkipLine (forces the current conversation line to finish) and SelectReply
// (commits a reply slot) — both in-process substitutes for OS input the game never receives
// (see SkipLine's own doc for why).
internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/dialogue-skip-desync.md): forces the local player's
    /// OWNED conversation's current line to finish right now, exactly as a real skip
    /// keypress/click would — see <see cref="Odyssey.SkipDialogueLine"/> for the VERIFIED
    /// engine mechanism this calls.
    ///
    /// Built because injected OS input never reaches this game at all (foreground focus
    /// genuinely held, atomic keypress bursts, zero effect) — the same wall <c>usedoor</c>,
    /// <c>useplaceable</c>, <c>attack</c> and every other in-process console verb in this
    /// file were built to route around. There is nothing left to inject: the call below runs
    /// entirely on the game's own thread, inside its own process.
    ///
    /// A no-op is reported as a REFUSAL (not "applied") when there is no active conversation —
    /// distinguishable from "applied but nothing happened because nothing was playing", which
    /// <see cref="Odyssey.SkipDialogueLine"/> also cannot tell apart from a real skip (the
    /// engine's own <c>IsDialogDelay</c> guard makes both cases silently harmless, by design —
    /// see that method's own remarks). The live-test script that exercises this verb rereads
    /// <c>dialogue-node-pub</c>/<c>dialogue-node-drive</c> to tell which one actually happened.
    /// </summary>
    private void SkipLine()
    {
        // Logged BEFORE the write, every call: this is what told a live pass APART "the flag
        // write did nothing" from "there was nothing to skip" (a reply screen showing, not a
        // VO) — DescribeDialogueDrive's dialogAt0x3C/skipFlag fields are exactly IsDialogDelay's
        // own two guards, read raw. Kept rather than trimmed: the same question ("was this call
        // actually inside an active VO wait") is the first thing any future desync report about
        // this verb will need answered from the log alone.
        PluginHost.Log($"dialogue: skipline about to poke — {Odyssey.DescribeDialogueDrive()}");

        if (!Odyssey.SkipDialogueLine(out int nodeBefore, out int entryCount))
        {
            Answer(SharedBlock.CheatStatusNoTarget, "no active conversation here to skip a line in");
            return;
        }

        PluginHost.Log($"dialogue: skipline requested at node {nodeBefore}/{entryCount}");
        Answer(
            SharedBlock.CheatStatusApplied,
            $"skip requested at node {nodeBefore}/{entryCount} (a no-op if nothing was actually playing)");
    }

    /// <summary>
    /// LIVE-TEST PRIMITIVE: commits reply slot <c>block.CheatAmount</c> on the local player's
    /// OWNED conversation — see <see cref="SharedBlock.CheatKindSelectReply"/> for why this
    /// exists (navigating past a reply screen without real OS input, for the skip live-test).
    /// Reuses <see cref="Odyssey.SelectDialogueReply"/> unchanged.
    /// </summary>
    private void SelectReply()
    {
        int slot = block.CheatAmount;
        if (!Odyssey.SelectDialogueReply(slot))
        {
            Answer(SharedBlock.CheatStatusRefused, $"reply slot {slot} not available (no conversation, or out of range)");
            return;
        }

        PluginHost.Log($"dialogue: selectreply committed slot {slot}");
        Answer(SharedBlock.CheatStatusApplied, $"reply slot {slot} committed");
    }
}
