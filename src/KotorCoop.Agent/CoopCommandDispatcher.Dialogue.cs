using KotorCoop.Memory;
using KotorCoop.Session;

namespace KotorCoop.Agent;

// The `startdialog` dev verb. See docs/research/dialogue-sync.md's 2026-08-28 addendum.
public sealed partial class CoopCommandDispatcher
{
    /// <summary>
    /// LIVE-TEST PRIMITIVE (docs/research/dialogue-sync.md's 2026-08-28 addendum): starts a
    /// conversation between the local player and the NPC named by <paramref name="npcToken"/>
    /// (an object id or a tag), through <see cref="CheatKinds.StartDialog"/>. Same field
    /// packing convention as <c>CoopCommandDispatcher.Store.cs</c>'s <c>Buy</c>/<c>Sell</c> —
    /// see <c>SharedBlock.CheatKindStartDialog</c>'s own doc comment: a numeric
    /// <paramref name="npcToken"/> sends it as <c>Target</c> (with
    /// <paramref name="dialogResref"/>, which may be empty, as the bare <c>Text</c>); a tag
    /// sends <c>Target=0</c> and packs <c>"token"</c> or <c>"token|dialogResref"</c> into
    /// <c>Text</c> instead.
    /// </summary>
    private void StartDialog(string npcToken, string dialogResref)
    {
        if (npcToken.Length == 0)
        {
            Reply("startdialog: no npc tag or id given");
            return;
        }

        if (ConsoleCommand.TryParseObjectId(npcToken, out uint objectId) && objectId != 0)
        {
            SendCheat(new CheatRequest(CheatKinds.StartDialog, Target: objectId, Text: dialogResref));
            return;
        }

        string text = dialogResref.Length == 0 ? npcToken : $"{npcToken}|{dialogResref}";
        SendCheat(new CheatRequest(CheatKinds.StartDialog, Text: text));
    }
}
