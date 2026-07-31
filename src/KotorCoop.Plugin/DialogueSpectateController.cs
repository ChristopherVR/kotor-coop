namespace KotorCoop.Plugin;

/// <summary>
/// On the instance that owns a conversation, reads the current speaker + line off the live
/// CSWSDialog each frame and publishes them into the shared block, so the OTHER player can be
/// shown what the owner is hearing (through the existing always-on chat overlay). Read-only
/// and defensive: every engine deref is guarded in <see cref="Odyssey.TryReadActiveDialogue"/>,
/// so a null or garbage pointer bails to empty and never faults.
///
/// This is spectate only. The conversation SUPPRESSION lever (stop the non-owner running its
/// own conversation) is deliberately not implemented — it is NEEDS-LIVE.
///
/// Runs on the game's own thread from <see cref="PluginHost"/>'s frame hook.
/// </summary>
internal sealed unsafe class DialogueSpectateController(SharedBlockView block)
{
    private bool _hadDialogue;
    private uint _lastHash;

    public void OnFrame()
    {
        byte* speaker = block.LocalDialogueSpeaker;
        byte* line = block.LocalDialogueLine;

        bool active = Odyssey.TryReadActiveDialogue(
            speaker, SharedBlock.LocalDialogueSpeakerLength,
            line, SharedBlock.LocalDialogueLineLength);

        if (!active)
        {
            // Clear once when the conversation ends, so a stale line is not spectated forever.
            if (_hadDialogue)
            {
                _hadDialogue = false;
                _lastHash = 0;
                *speaker = 0;
                *line = 0;
                block.LocalDialogueSerial++;
            }

            return;
        }

        _hadDialogue = true;

        // Bump the serial only when the pair actually changed, so the agent can read a
        // coherent snapshot without a per-frame string compare on the wire.
        uint hash = Hash(speaker, hash: Hash(line, 2166136261u));
        if (hash != _lastHash)
        {
            _lastHash = hash;
            block.LocalDialogueSerial++;
        }
    }

    /// <summary>FNV-1a over a null-terminated buffer, seeded so speaker+line combine.</summary>
    private static uint Hash(byte* text, uint hash)
    {
        for (int i = 0; text[i] != 0 && i < 256; i++)
        {
            hash = (hash ^ text[i]) * 16777619u;
        }

        return hash;
    }
}
