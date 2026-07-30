using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// Applies the session's quest-journal truth to this instance: when a peer's journal has
/// an entry ahead of ours, the agent writes (tag, state) into the shared block and this
/// controller advances it through CSWSJournal::SetState on the game's thread — the call
/// that also refreshes the journal GUI. One request at a time; journal changes are rare
/// and the agent holds the queue.
/// </summary>
internal sealed unsafe class JournalController(SharedBlockView block)
{
    private int _logged;

    /// <summary>
    /// Requests survive a module change on purpose: the journal is party state, not area
    /// state, and a request that raced a transition applies on the next stable frame.
    /// </summary>
    public void Invalidate()
    {
    }

    public void OnFrame()
    {
        uint serial = block.JournalSerial;
        if (serial == block.JournalApplied)
        {
            return;
        }

        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        byte* tag = block.JournalTag;
        int length = 0;
        while (length < SharedBlock.JournalTagLength - 1 && tag[length] != 0)
        {
            length++;
        }

        bool ok = Odyssey.SetJournalState(tag, length, block.JournalState, out int failure);
        block.JournalStatus = ok ? 0 : failure;
        block.JournalApplied = serial;

        if (_logged < 20)
        {
            _logged++;
            string name = Encoding.ASCII.GetString(tag, length);
            PluginHost.Log(ok
                ? $"journal: '{name}' -> state {block.JournalState} on the session's authority"
                : $"journal: '{name}' refused (failure {failure})");
        }
    }
}
