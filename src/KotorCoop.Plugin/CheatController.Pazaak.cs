namespace KotorCoop.Plugin;

// The `pazaak` staging cheat: starts a hand through Odyssey.StartPazaak so pazaak_detect can be
// verified with a real running game, not just the negative (never-started) case. See
// docs/research/minigames.md's 2026-08-27 addendum.
internal sealed unsafe partial class CheatController
{
    /// <summary>CSWSObject + this = the object id, following <see cref="CreatureFinder"/>'s own
    /// convention (its private ObjectIdOffset, redeclared here per this codebase's established
    /// "duplicate a small offset per file" rule — see EngineHandles.cs's header comment).</summary>
    private const int PazaakOpponentObjectIdOffset = 0x04;

    /// <summary>
    /// Starts a hand against <see cref="SharedBlock.CheatTarget"/> (an object id) or
    /// <see cref="SharedBlock.CheatText"/> (a tag) — both empty means the engine's own default
    /// opponent (<c>OBJECT_INVALID</c>). <see cref="SharedBlock.CheatAmount"/> is the pazaak-deck
    /// index (default 0 when the field is 0).
    /// </summary>
    private void Pazaak()
    {
        uint requestedId = block.CheatTarget;
        string text = ReadCheatText();
        int deck = block.CheatAmount;

        uint opponent = Odyssey.ObjectInvalid;
        string who = "the engine's own default opponent";

        if (requestedId != 0)
        {
            opponent = requestedId;
            who = $"0x{requestedId:X}";
        }
        else if (text.Length > 0)
        {
            byte* tag = block.CheatText;
            int length = 0;
            while (length < SharedBlock.CheatTextLength - 1 && tag[length] != 0)
            {
                length++;
            }

            nint creature = CreatureFinder.FindByTag(new ReadOnlySpan<byte>(tag, length));
            if (creature == 0)
            {
                Answer(SharedBlock.CheatStatusNoTarget, $"no creature tagged '{text}' found here");
                return;
            }

            opponent = *(uint*)(creature + PazaakOpponentObjectIdOffset);
            who = $"'{text}' (0x{opponent:X})";
        }

        if (!Odyssey.StartPazaak(deck, opponent, out string reason))
        {
            Answer(SharedBlock.CheatStatusRefused, $"pazaak refused: {reason}");
            return;
        }

        bool running = Odyssey.IsPazaakRunning();
        PluginHost.Log(
            $"pazaak: StartPazaakGame(deck {deck}, opponent {who}) called - running now: {running}");

        Answer(
            running ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            running
                ? $"pazaak hand started (deck {deck}, opponent {who})"
                : $"StartPazaakGame called but CPazaakData is still null (deck {deck} may be "
                    + "invalid, or another gate refused it - see the plugin log)");
    }
}
