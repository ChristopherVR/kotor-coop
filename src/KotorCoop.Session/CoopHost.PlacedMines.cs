using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — party-placed floor-mine replication (docs/research/mines.md §14): the canonical
/// set lives here (<see cref="PlacedMineAuthority"/>). A client's intents are acked, deduplicated
/// and applied to it; the set goes out to every client as a roster level on every change and
/// once a second regardless, so a dropped datagram heals on its own.
/// </summary>
public sealed partial class CoopHost
{
    private const int PlacedMineRosterResendMs = 1000;

    private readonly PlacedMineAuthority _placedMines = new();
    private readonly Queue<string> _placedMineNotes = new();
    private readonly HashSet<(uint Origin, uint Sequence)> _placedMineIntentsSeen = [];
    private readonly Queue<(uint Origin, uint Sequence)> _placedMineIntentsSeenOrder = new();
    private uint _placedMineRosterSentGeneration;
    private long _placedMineRosterSentMs;

    public bool IsPlacedMineAuthority => true;

    public void SetPlacedMineHostWorld(uint moduleHash, uint worldGeneration)
    {
        if (_placedMines.SetWorld(moduleHash, worldGeneration))
        {
            Note("host world changed - the canonical party-placed mines of the old world were dropped");
        }
    }

    public void PublishPlacedMine(uint captureSerial, uint moduleHash, PlacedMineRecord record) =>
        Place(HostPeerId, captureSerial, moduleHash, record);

    public void PublishPlacedMineGone(uint mineSerial)
    {
        if (_placedMines.Remove(mineSerial))
        {
            Note($"session mine #{mineSerial} gone on the host - removed from the canonical set");
        }
    }

    public PlacedMineRosterPacket? CurrentPlacedMineRoster => _placedMines.Snapshot();

    public IReadOnlyList<string> DrainPlacedMineNotes()
    {
        lock (_placedMineNotes)
        {
            if (_placedMineNotes.Count == 0)
            {
                return [];
            }

            List<string> notes = [.. _placedMineNotes];
            _placedMineNotes.Clear();
            return notes;
        }
    }

    private void Place(uint owner, uint captureSerial, uint moduleHash, PlacedMineRecord record)
    {
        PlacedMinePlaceResult result = _placedMines.Place(owner, captureSerial, moduleHash, record, out uint serial);
        string who = owner == HostPeerId ? "the host" : $"peer {owner}";
        Note(result switch
        {
            PlacedMinePlaceResult.Accepted => $"{who}'s placement #{captureSerial} is session mine #{serial} (row {record.TrapType}, "
                + $"DC {record.DetectDc}/{record.DisarmDc}, at {record.X:F2},{record.Y:F2},{record.Z:F2})",
            PlacedMinePlaceResult.Duplicate => $"{who}'s placement #{captureSerial} repeated - still session mine #{serial}",
            PlacedMinePlaceResult.WrongModule => $"{who}'s placement #{captureSerial} refused: not in the host's module (the host's copy must own detonation)",
            PlacedMinePlaceResult.Full => $"{who}'s placement #{captureSerial} refused: {PlacedMineRosterPacket.MaxEntries} party mines already active here",
            PlacedMinePlaceResult.NoWorld => $"{who}'s placement #{captureSerial} refused: the host has no world yet",
            _ => $"{who}'s placement #{captureSerial} refused: invalid record",
        });
    }

    private void Note(string text)
    {
        lock (_placedMineNotes)
        {
            if (_placedMineNotes.Count < 64)
            {
                _placedMineNotes.Enqueue(text);
            }
        }
    }

    private void BroadcastPlacedMineRoster(long now, List<PeerConnection> recipients)
    {
        if (recipients.Count == 0)
        {
            return;
        }

        uint generation = _placedMines.Generation;
        if (generation == _placedMineRosterSentGeneration && now - _placedMineRosterSentMs < PlacedMineRosterResendMs)
        {
            return;
        }

        PlacedMineRosterPacket roster = _placedMines.Snapshot();
        int length = PacketCodec.Write(_sendBuffer, roster);
        if (length == 0)
        {
            return;
        }

        foreach (PeerConnection peer in recipients)
        {
            _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
        }

        _placedMineRosterSentGeneration = generation;
        _placedMineRosterSentMs = now;
    }

    /// <summary>
    /// A client's placement or removal report: ack it unconditionally (a duplicate means our
    /// ack was lost), then apply it once. A client can only report for itself.
    /// </summary>
    private void HandlePlacedMineIntent(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPlacedMineIntent(span) is not { } intent)
        {
            _channel.NoteMalformed();
            return;
        }

        int length = PacketCodec.Write(
            _replyBuffer, new PlacedMineIntentAckPacket(HostPeerId, intent.OriginPeerId, intent.OpSequence));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }

        if (intent.OriginPeerId == HostPeerId)
        {
            return;
        }

        lock (_gate)
        {
            if (!_peers.ContainsKey(intent.OriginPeerId)
                || !_placedMineIntentsSeen.Add((intent.OriginPeerId, intent.OpSequence)))
            {
                return;
            }

            _placedMineIntentsSeenOrder.Enqueue((intent.OriginPeerId, intent.OpSequence));
            while (_placedMineIntentsSeenOrder.Count > 1024)
            {
                _placedMineIntentsSeen.Remove(_placedMineIntentsSeenOrder.Dequeue());
            }
        }

        switch (intent.Kind)
        {
            case PlacedMineIntentKind.Placed:
                Place(intent.OriginPeerId, intent.CaptureSerial, intent.ModuleHash, intent.Record);
                break;

            case PlacedMineIntentKind.Gone:
                if (_placedMines.Remove(intent.MineSerial))
                {
                    Note($"session mine #{intent.MineSerial} gone on peer {intent.OriginPeerId} - removed from the canonical set");
                }

                break;
        }
    }
}
