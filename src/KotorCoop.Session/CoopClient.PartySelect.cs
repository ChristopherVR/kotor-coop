using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — the party-select relay: this player's own suppressed party-select screen,
/// asked of the host instead. See IPartySelectSession's own remarks and
/// docs/research/galaxy-map-and-party-select.md's 2026-09-25 relay section.
/// </summary>
public sealed partial class CoopClient
{
    private readonly List<PartySelectResponsePacket> _partySelectResponses = [];

    /// <inheritdoc />
    public void RequestPartySelect(
        string module, string exitScript, int forceNpc1, int forceNpc2, uint triggerId, uint requestSerial)
    {
        if (requestSerial == 0)
        {
            return;
        }

        // NOT _sendBuffer: this is called from the plugin-pump thread while RunAsync's tick
        // loop is encoding its own packets on another thread — see
        // CoopClient.RequestContainerLock's identical remark.
        SendFromCaller(
            new PartySelectRequestPacket(
                _localPeerId, requestSerial, module, exitScript, forceNpc1, forceNpc2, triggerId),
            static (buffer, packet) => PacketCodec.Write(buffer, packet));
    }

    /// <inheritdoc />
    public IReadOnlyList<PartySelectResponsePacket> DrainPartySelectResponses()
    {
        lock (_gate)
        {
            if (_partySelectResponses.Count == 0)
            {
                return [];
            }

            PartySelectResponsePacket[] drained = [.. _partySelectResponses];
            _partySelectResponses.Clear();
            return drained;
        }
    }

    private void HandlePartySelectResponse(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPartySelectResponse(span) is not { } response)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            _partySelectResponses.Add(response);
        }
    }
}
