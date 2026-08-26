using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>Carries NPC barks both ways. A bark never enters the dialogue system, so before
/// this one player heard the utility droid beep and everybody else got silence.</summary>
internal sealed class BarkPump(IGameBridge bridge, ICoopSession session)
{
    /// <summary>The bark serial this agent has already published, per plugin ring slot, so a
    /// line is sent once however long it sits in the block.
    ///
    /// Keyed on the SPEAKER's object id rather than a ring index: the plugin's ring slot for
    /// a given line is an implementation detail, while "the last serial seen from this
    /// creature" is the thing that decides whether a line is new.
    /// </summary>
    private readonly Dictionary<uint, uint> _publishedBarks = [];

    public void PumpBarks(in PlayerState local)
    {
        if (!bridge.CanWriteRemotes || ModuleNameGuard.IsFallbackModuleName(local.Module) || local.Module.Length == 0)
        {
            return; // mid-transition: the ids a bark names are about to be reassigned
        }

        foreach (BarkRecord bark in bridge.ReadBarks())
        {
            if (_publishedBarks.TryGetValue(bark.Speaker, out uint published) && published == bark.Serial)
            {
                continue;
            }

            _publishedBarks[bark.Speaker] = bark.Serial;
            session.PublishBark(local.Module, bark.Speaker, bark.Text, bark.SoundResRef, bark.Flags, bark.Serial);
        }

        foreach (BarkPacket bark in session.DrainBarks(local.Module))
        {
            bridge.TryWriteBark(bark.Speaker, bark.Text, bark.SoundResRef, bark.Flags, bark.Serial);
        }
    }
}
