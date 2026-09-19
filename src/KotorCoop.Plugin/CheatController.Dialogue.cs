using System.Text;

namespace KotorCoop.Plugin;

// Dialogue verbs: SkipLine (forces the current conversation line to finish), SelectReply
// (commits a reply slot) — both in-process substitutes for OS input the game never receives
// (see SkipLine's own doc for why) — and StartDialog (2026-08-28: queues a real
// ActionStartConversation on the player, see that method's own doc comment).
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
    /// LIVE-TEST PRIMITIVE (2026-08-29, docs/research/dialogue-skip-desync.md): presses skip
    /// on the dialogue panel THROUGH THE ENGINE'S OWN INPUT DISPATCH, which is the only route
    /// that passes through <see cref="DialogueSkipHook"/> — the gag that stops a SPECTATOR
    /// advancing somebody else's conversation.
    ///
    /// ⚠ WHY <c>skipline</c> COULD NOT TEST THAT HOOK, measured live and recorded here because
    /// it looks like it should have: <see cref="SkipLine"/> writes the owner's
    /// <c>CSWSObject+0x1F4</c> directly and never enters <c>CSWGuiDialog::HandleInputEvent</c>,
    /// so the hook never runs; and on the spectator — the instance the hook is entirely about —
    /// it refuses before writing anything, because a spectator owns no conversation for
    /// <c>TryGetActiveDialogue</c> to resolve. Two different reasons, same useless result.
    ///
    /// <see cref="DialogueSkipHook.SwallowedCount"/> is the oracle, and it is reported both
    /// BEFORE and AFTER the call in one answer line: a spectator must see it step by one with
    /// nothing advancing on either instance, an owner must see it flat with the line advancing
    /// normally. Reading it after the call is safe and exact — the detour runs synchronously
    /// inside the call below, on this thread.
    /// </summary>
    private void SkipKey()
    {
        uint before = DialogueSkipHook.SwallowedCount;

        // Logged before the call for the same reason SkipLine logs its probe: this is the one
        // line that says what the panel state was when the key went in.
        PluginHost.Log($"dialogue: skipkey about to dispatch - {Odyssey.DescribeDialogueDrive()}");

        if (!Odyssey.SendDialogueSkipKey(out int nodeBefore))
        {
            Answer(SharedBlock.CheatStatusNoTarget, "no dialogue panel here to send a skip key to");
            return;
        }

        uint after = DialogueSkipHook.SwallowedCount;
        bool swallowed = after != before;

        PluginHost.Log(
            $"dialogue: skipkey dispatched at node {nodeBefore}; swallowed {before} -> {after} "
            + $"({(swallowed ? "SWALLOWED - spectator" : "passed through - owner")})");

        Answer(
            SharedBlock.CheatStatusApplied,
            $"skip key sent at node {nodeBefore}; swallowed {before} -> {after} "
            + $"({(swallowed ? "swallowed" : "passed through")})");
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

    /// <summary>CSWSObject + this = the object id, following <see cref="CreatureFinder"/>'s own
    /// convention — redeclared here per this codebase's established "duplicate a small offset
    /// per file" rule (see CheatController.Pazaak.cs's identical PazaakOpponentObjectIdOffset
    /// and EngineHandles.cs's own header comment on the precedent).</summary>
    private const int NpcObjectIdOffset = 0x04;

    /// <summary>
    /// LIVE-TEST PRIMITIVE (2026-08-28, docs/research/dialogue-sync.md's 2026-08-28 addendum):
    /// starts a conversation between the LOCAL PLAYER and an NPC through
    /// <see cref="Odyssey.StartConversation"/> — the engine's own <c>ActionStartConversation</c>
    /// VM routine, queued on the player exactly as a real click would be, so the real
    /// action-queue -&gt; <c>BeginConversation</c> -&gt; <c>StartDialog</c> chain runs and the
    /// existing dialogue hooks (<c>DialogueGateHook</c>/<c>DialogStateWatchHook</c>) observe it
    /// normally — see <see cref="Odyssey.StartConversation"/>'s own header comment for why this
    /// goes through the VM routine table rather than a hand-crafted <c>StartDialog</c> call.
    ///
    /// See <see cref="SharedBlock.CheatKindStartDialog"/> for the field packing:
    /// <see cref="SharedBlock.CheatTarget"/> nonzero names the NPC by object id and
    /// <see cref="SharedBlock.CheatText"/> is then the optional dialog resref override (empty =
    /// the NPC's own default); <see cref="SharedBlock.CheatTarget"/> zero means the NPC was a
    /// tag, packed in <see cref="SharedBlock.CheatText"/> as <c>"tag"</c> or
    /// <c>"tag|dlg-resref"</c>.
    /// </summary>
    private void StartDialog(uint playerId)
    {
        uint npcId = block.CheatTarget;

        byte* text = block.CheatText;
        int totalLength = 0;
        while (totalLength < SharedBlock.CheatTextLength - 1 && text[totalLength] != 0)
        {
            totalLength++;
        }

        string dialogResref;
        string what;

        if (npcId != 0)
        {
            dialogResref = totalLength > 0 ? Encoding.ASCII.GetString(text, totalLength) : string.Empty;
            what = $"0x{npcId:X}";
        }
        else
        {
            int pipe = -1;
            for (int i = 0; i < totalLength; i++)
            {
                if (text[i] == (byte)'|')
                {
                    pipe = i;
                    break;
                }
            }

            int tagLength = pipe < 0 ? totalLength : pipe;
            if (tagLength == 0)
            {
                Answer(SharedBlock.CheatStatusRefused, "startdialog: no npc tag or id given");
                return;
            }

            string tag = Encoding.ASCII.GetString(text, tagLength);
            ReadOnlySpan<byte> tagSpan = new(text, tagLength);
            nint npc = CreatureFinder.FindByTag(tagSpan);

            // 2026-09-19: a conversation can also live on a DOOR or a PLACEABLE (Manaan's airlocks are
            // three locked doors carrying man28_airlock.dlg — docs/research/manaan-hrakert-rift.md), which the
            // creature sweep never finds. ActionStartConversation takes any object id, so fall back to the
            // door and placeable sweeps (all three share the object id at +0x04).
            if (npc == 0)
            {
                npc = CreatureFinder.FindDoorByTag(tagSpan);
            }

            if (npc == 0)
            {
                npc = CreatureFinder.FindPlaceableByTag(tagSpan);
            }

            if (npc == 0)
            {
                Answer(SharedBlock.CheatStatusNoTarget, $"no npc, door or placeable tagged '{tag}' found here");
                return;
            }

            npcId = *(uint*)(npc + NpcObjectIdOffset);
            what = $"'{tag}' (0x{npcId:X})";

            dialogResref = pipe < 0
                ? string.Empty
                : Encoding.ASCII.GetString(text + pipe + 1, totalLength - pipe - 1);
        }

        if (!Odyssey.StartConversation(playerId, npcId, dialogResref, out int failure))
        {
            Answer(SharedBlock.CheatStatusEngineRefused, $"startdialog {what} refused (failure {failure})");
            return;
        }

        string resrefNote = dialogResref.Length > 0 ? $" (dlg '{dialogResref}')" : " (npc's own default dlg)";
        PluginHost.Log($"startdialog: ActionStartConversation queued on the player, targeting {what}{resrefNote}");
        Answer(SharedBlock.CheatStatusApplied, $"conversation with {what} queued{resrefNote}");
    }
}
