using System.Text.Json;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

internal sealed record ItemContinuationFile(int Version, uint HostSessionId, uint PeerId,
    uint Nonce, uint NextItemSequence, ItemSenderState Sender)
{
    private static string SessionPath(string path, uint host, uint peer, uint nonce) =>
        $"{path}.{host:X8}.{peer:X8}.{nonce:X8}";

    internal static ItemContinuation? ReadFor(string? path, uint host, uint peer, uint nonce) =>
        string.IsNullOrEmpty(path) ? null : Read(SessionPath(path, host, peer, nonce));

    internal static ItemContinuation? Read(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        // A configured but damaged journal cannot safely mint previously used IDs.
        ItemContinuationFile saved;
        try
        {
            saved = JsonSerializer.Deserialize<ItemContinuationFile>(File.ReadAllText(path))
                ?? throw new InvalidDataException("empty item continuation journal");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("corrupt item continuation journal", error);
        }
        if (saved.Version != 1 || saved.HostSessionId == 0 || saved.PeerId == 0 || saved.Nonce == 0
            || saved.NextItemSequence is 0 or > 0x10000 || saved.Sender is null
            || saved.Sender.Pending is null || saved.Sender.NextSequence == 0
            || saved.Sender.Pending.Length > 4096
            || saved.Sender.Pending.Select(p => p?.OpSequence).Distinct().Count() != saved.Sender.Pending.Length
            || saved.Sender.Pending.Any(p => p is null || p.Item is null || p.OriginPeerId != saved.PeerId || p.OpSequence == 0
                || p.OpSequence >= saved.Sender.NextSequence || !Enum.IsDefined(p.Kind)))
            throw new InvalidDataException("invalid item continuation journal");
        return new(saved.HostSessionId, saved.PeerId, saved.Nonce, saved.NextItemSequence,
            ReliableItemSender.Restore(saved.Sender));
    }

    internal static void Write(string path, ItemContinuation state)
    {
        ItemContinuationFile saved = new(1, state.HostSessionId, state.PeerId, state.Nonce,
            state.NextItemSequence, state.Sender.Capture());
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(saved);
        // Preserve each host's namespace when visiting a different still-running host.
        WriteAtomic(SessionPath(path, state.HostSessionId, state.PeerId, state.Nonce), data);
        WriteAtomic(path, data);
    }

    private static void WriteAtomic(string path, byte[] data)
    {
        string temporary = path + ".tmp";
        using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        {
            stream.Write(data);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }
}
