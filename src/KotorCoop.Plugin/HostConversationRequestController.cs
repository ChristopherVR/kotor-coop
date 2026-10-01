using System.Text;
using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// The host half of the host-start conversation relay (SharedBlock.HostConversation.cs): a
/// joiner's engine refused a host-only conversation start (<see cref="DialogueGateHook"/>), the
/// agent relayed it, and this starts the SAME conversation for the host's own player. The host
/// is authoritative, so the joiner then watches through the ordinary auto-join / spectate
/// machinery (the host owns the conversation token).
///
/// Starts through <see cref="Odyssey.StartDialogueWith"/> with <c>forced</c> set - the same
/// path the auto-join takes, whose forced branch replays <c>ActionStartConversation</c> with
/// ignore-start-range, so the host's player need not be near the target (the joiner was the
/// one standing there). Creatures, doors and placeables are all resolved by tag
/// (<see cref="Odyssey.FindDialoguePartnerByTag"/>): the Manaan airlock is a door.
///
/// The host's own <see cref="DialogueGateHook"/> never refuses this: the host-only refusal is
/// gated on <c>JoinerIsClient</c>, which is false on a host, and a forced start returns before
/// any gate. Only conversations in <see cref="HostLedConversationRule"/> are ever started -
/// the list is re-checked here, so a stale or hostile packet cannot start an arbitrary one.
///
/// If the host is busy (already in a conversation, in a minigame or combat, or the target has
/// not loaded yet) the request is retried for <see cref="RetryWindowMs"/> and then dropped with
/// a log line.
/// </summary>
internal sealed class HostConversationRequestController(SharedBlockView block)
{
    /// <summary>How long a request keeps retrying while the host is busy.</summary>
    internal const long RetryWindowMs = 5_000;

    private const long RetryEveryMs = 250;

    private uint _appliedSerial;

    private uint _pendingSerial;
    private uint _pendingPeer;
    private string _pendingTag = string.Empty;
    private string _pendingResref = string.Empty;
    private long _deadlineMs;
    private long _nextTryMs;
    private string _lastBusyReason = string.Empty;

    /// <summary>Module change: a request filed for the old world must not be acted on in the new one.</summary>
    public void Invalidate()
    {
        _appliedSerial = block.HostConversationHostSerial;
        _pendingSerial = 0;
    }

    public void OnFrame()
    {
        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        uint serial = block.HostConversationHostSerial;
        if (serial != 0 && serial != _appliedSerial)
        {
            _appliedSerial = serial;
            Accept(serial);
        }

        if (_pendingSerial == 0)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now < _nextTryMs)
        {
            return;
        }

        _nextTryMs = now + RetryEveryMs;

        string? busy = BusyReason();
        if (busy is null && TryStart())
        {
            PluginHost.Log($"host conversation relay: started {Describe()} for peer {_pendingPeer}");
            _pendingSerial = 0;
            return;
        }

        _lastBusyReason = busy ?? "target not found or the engine did not start it yet";
        if (now >= _deadlineMs)
        {
            PluginHost.Log($"host conversation relay: gave up on {Describe()} for peer {_pendingPeer} "
                + $"after {RetryWindowMs} ms ({_lastBusyReason})");
            _pendingSerial = 0;
        }
    }

    private void Accept(uint serial)
    {
        string tag = block.HostConversationHostTag;
        string resref = block.HostConversationHostResref;
        uint peer = block.HostConversationHostPeerId;

        if (!HostLedConversationRule.IsHostLed(resref, tag))
        {
            PluginHost.Log($"host conversation relay: refused request #{serial} from peer {peer} - "
                + $"'{tag}'/'{resref}' is not a host-led conversation");
            return;
        }

        if (_pendingSerial != 0)
        {
            PluginHost.Log($"host conversation relay: replacing pending {Describe()} with request #{serial}");
        }

        _pendingSerial = serial;
        _pendingPeer = peer;
        _pendingTag = tag;
        _pendingResref = resref;
        _deadlineMs = Environment.TickCount64 + RetryWindowMs;
        _nextTryMs = 0;
        _lastBusyReason = string.Empty;
    }

    private string Describe() => $"'{_pendingTag}'" + (_pendingResref.Length > 0 ? $" ({_pendingResref})" : string.Empty);

    /// <summary>Why the host cannot start a conversation right now, or null when it can. The
    /// same facts the auto-join uses: already talking, in combat, in a minigame.</summary>
    private static string? BusyReason()
    {
        if (!Odyssey.IsInGame)
        {
            return "no HUD (loading)";
        }

        if (Odyssey.IsConversationActive)
        {
            return "the host is already in a conversation";
        }

        if (MinigameController.LocalInMinigame)
        {
            return "the host is in a minigame";
        }

        if (Odyssey.IsPlayerInCombat)
        {
            return "the host is in combat";
        }

        return null;
    }

    private unsafe bool TryStart()
    {
        byte[] tag = Encoding.ASCII.GetBytes(_pendingTag);
        nint target = Odyssey.FindDialoguePartnerByTag(tag);
        if (target == 0)
        {
            return false;
        }

        // The same queued ActionStartConversation the `startdialog` dev verb uses, ignoring the
        // start range: the host's player is not necessarily near the target. This is the path
        // that opens a DOOR's computer panel (the Manaan airlock); the auto-join start did not
        // (live 2026-10-02: the host's relay-started airlock had no panel and no replies).
        nint player = Odyssey.GetPlayerCreaturePointer();
        if (player != 0 && *(uint*)(target + 4) is uint targetId && targetId != 0
            && Odyssey.StartConversation(*(uint*)(player + 4), targetId, _pendingResref, out _,
                ignoreStartRange: true))
        {
            return true;
        }

        return Odyssey.StartDialogueWith(tag, _pendingResref, forced: true, partner: target);
    }
}
