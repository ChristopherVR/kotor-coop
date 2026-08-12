using System.Net;
using System.Net.Sockets;
using System.Text;
using KotorCoop.Memory;

namespace KotorCoop.Session;

/// <summary>One file of a transferred save, as it sits in the save directory.</summary>
public readonly record struct WorldFile(string Name, byte[] Data);

/// <summary>
/// Ships the host's world to a joining player: the save directory itself, over TCP, beside
/// the UDP session.
///
/// WHY THIS EXISTS. Every object-id-keyed channel in this mod — enemies, doors, placeables,
/// container loot, encounters — rests on both engines having dealt the same object ids,
/// which they only do when both loaded the same save (see <c>SaveDigest</c>). Until now that
/// was an assumption the session could check and refuse on; this is the machinery that makes
/// it TRUE instead. A joiner adopts the host's world and carries only their own character
/// into it (<c>PlayerIfo</c>), so the save-identity digests match by construction rather than
/// by luck.
///
/// WHY TCP, NEXT TO THE UDP SESSION. A save is ~650 KB — three orders of magnitude past a
/// datagram, and the one payload in this protocol that is neither a level nor an event but a
/// bulk object that must arrive exactly once, complete, before anything else can happen.
/// Reusing the retransmit-until-acked machinery would mean reinventing ordering, windowing
/// and flow control on top of it for a transfer that happens once per session. The listener
/// binds the SAME port number as the session, so a player who has forwarded a port for co-op
/// has already forwarded this.
///
/// The transfer is deliberately dumb: no compression (a save is mostly incompressible ERF
/// payload and the link is usually a LAN), no resume (a failed transfer is retried whole,
/// and 650 KB is under a second on anything modern), and no delta (there is no prior state
/// to delta against — that is the entire point).
/// </summary>
public static class WorldTransfer
{
    /// <summary>Frames both directions, so a stray connection from something else on the
    /// port is rejected without reading a payload.</summary>
    private const ushort Magic = 0x574B; // 'KW' little-endian: Kotor World

    /// <summary>Bumped when the framing changes. Independent of the session protocol
    /// version: the two negotiate separately and a mismatch here is not fatal to a session
    /// that has already agreed on everything else.</summary>
    private const byte Version = 1;

    private const byte StatusOk = 0;
    private const byte StatusBadMagic = 1;
    private const byte StatusBadVersion = 2;
    private const byte StatusBadPassword = 3;
    private const byte StatusNoWorld = 4;

    /// <summary>
    /// Files that never travel. The screenshot is a third of a save's bytes and is pure
    /// GUI decoration for a slot the mod loads programmatically; sending it would be the
    /// single largest cost in the transfer for no effect on the world.
    /// </summary>
    private static readonly string[] Excluded = ["Screen.tga"];

    // Bounds. A receiver must never let a hostile or broken sender allocate freely, and
    // these are all an order of magnitude past what a real save needs (measured: 4 files,
    // 584 KB largest, 654 KB total without the screenshot).
    private const int MaxFiles = 16;
    private const int MaxNameLength = 64;
    private const int MaxFileBytes = 16 * 1024 * 1024;
    private const int MaxTotalBytes = 48 * 1024 * 1024;

    /// <summary>How long either side waits on a stalled socket. Generous: a save crossing a
    /// slow link is legitimate, and the joiner is sitting at a menu, not in a world.</summary>
    private const int TimeoutMs = 60_000;

    /// <summary>Reads a save directory into the set of files that travel.</summary>
    public static IReadOnlyList<WorldFile> ReadSaveDirectory(string saveDirectory)
    {
        List<WorldFile> files = [];

        foreach (FileInfo file in new DirectoryInfo(saveDirectory).GetFiles())
        {
            if (Excluded.Contains(file.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            files.Add(new WorldFile(file.Name, File.ReadAllBytes(file.FullName)));
        }

        return files;
    }

    /// <summary>
    /// Writes a received world into <paramref name="saveDirectory"/>, replacing whatever was
    /// there, and neutralises any pifo the host's save carried.
    ///
    /// ⚠ The pifo step is not optional. <c>LoadPCAutoSave</c> exports the staged save and
    /// copies <c>&lt;export&gt;\pifo</c> over <c>TEMP:pifo</c>, so a host's AUTOSAVE — which
    /// does contain one — would overwrite the joiner's carried character with the host's and
    /// bring both players up as the same person. See <c>PlayerIfo.TryNeutralisePifo</c>.
    /// </summary>
    public static void WriteSaveDirectory(string saveDirectory, IReadOnlyList<WorldFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (Directory.Exists(saveDirectory))
        {
            Directory.Delete(saveDirectory, recursive: true);
        }

        Directory.CreateDirectory(saveDirectory);

        foreach (WorldFile file in files)
        {
            byte[] data = file.Data;

            if (string.Equals(file.Name, "SAVEGAME.sav", StringComparison.OrdinalIgnoreCase))
            {
                // Copy before patching: the caller's buffer may be the host's own live list.
                data = [.. data];
                PlayerIfo.TryNeutralisePifo(data);
            }

            File.WriteAllBytes(Path.Combine(saveDirectory, SafeName(file.Name)), data);
        }
    }

    /// <summary>
    /// Serves <paramref name="world"/> to joiners until cancelled. One connection at a time,
    /// which is right for the traffic: joins are rare, a transfer is under a second, and a
    /// queue is simpler than concurrency for something that runs once per player.
    /// </summary>
    public static async Task ServeAsync(
        IReadOnlyList<WorldFile> world,
        int port,
        uint passwordHash,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(world);

        using TcpListener listener = new(IPAddress.Any, port);
        listener.Start();
        log?.Invoke($"world transfer listening on :{port} ({world.Count} file(s), {TotalBytes(world):N0} bytes)");

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    await ServeOneAsync(client, world, passwordHash, log, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
                {
                    // One joiner's broken transfer must never take the listener down with
                    // it; they can simply try again.
                    log?.Invoke($"world transfer failed: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task ServeOneAsync(
        TcpClient client,
        IReadOnlyList<WorldFile> world,
        uint passwordHash,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        client.ReceiveTimeout = TimeoutMs;
        client.SendTimeout = TimeoutMs;

        await using NetworkStream stream = client.GetStream();

        byte[] request = new byte[8];
        await ReadExactlyAsync(stream, request, cancellationToken).ConfigureAwait(false);

        ushort magic = (ushort)(request[0] | (request[1] << 8));
        byte version = request[2];
        uint offered = ReadU32(request, 4);

        byte status = magic != Magic ? StatusBadMagic
            : version != Version ? StatusBadVersion
            : offered != passwordHash ? StatusBadPassword
            : world.Count == 0 ? StatusNoWorld
            : StatusOk;

        byte[] header = [(byte)(Magic & 0xFF), (byte)(Magic >> 8), Version, status];
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);

        if (status != StatusOk)
        {
            log?.Invoke($"world transfer refused (status {status})");
            return;
        }

        byte[] count = [(byte)world.Count, (byte)(world.Count >> 8)];
        await stream.WriteAsync(count, cancellationToken).ConfigureAwait(false);

        foreach (WorldFile file in world)
        {
            byte[] name = Encoding.Latin1.GetBytes(file.Name);
            byte[] prefix = new byte[6];
            prefix[0] = (byte)name.Length;
            prefix[1] = (byte)(name.Length >> 8);
            WriteU32(prefix, 2, (uint)file.Data.Length);

            await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(name, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(file.Data, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        log?.Invoke($"world sent ({TotalBytes(world):N0} bytes)");
    }

    /// <summary>
    /// Fetches the host's world. Throws <see cref="InvalidDataException"/> on a refusal or a
    /// malformed reply, so a caller can report exactly why a join could not adopt.
    /// </summary>
    public static async Task<IReadOnlyList<WorldFile>> FetchAsync(
        IPEndPoint host,
        uint passwordHash,
        CancellationToken cancellationToken)
    {
        using TcpClient client = new();
        client.ReceiveTimeout = TimeoutMs;
        client.SendTimeout = TimeoutMs;

        await client.ConnectAsync(host, cancellationToken).ConfigureAwait(false);
        await using NetworkStream stream = client.GetStream();

        byte[] request = new byte[8];
        request[0] = (byte)(Magic & 0xFF);
        request[1] = (byte)(Magic >> 8);
        request[2] = Version;
        WriteU32(request, 4, passwordHash);
        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);

        byte[] header = new byte[4];
        await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false);

        if ((ushort)(header[0] | (header[1] << 8)) != Magic)
        {
            throw new InvalidDataException("world transfer: the reply is not from a co-op host");
        }

        if (header[3] != StatusOk)
        {
            throw new InvalidDataException(header[3] switch
            {
                StatusBadVersion => "world transfer: the host runs a different transfer version",
                StatusBadPassword => "world transfer: wrong password",
                StatusNoWorld => "world transfer: the host has no world to send yet",
                _ => $"world transfer: refused (status {header[3]})",
            });
        }

        byte[] countBytes = new byte[2];
        await ReadExactlyAsync(stream, countBytes, cancellationToken).ConfigureAwait(false);
        int count = countBytes[0] | (countBytes[1] << 8);

        if (count is <= 0 or > MaxFiles)
        {
            throw new InvalidDataException($"world transfer: implausible file count {count}");
        }

        List<WorldFile> files = new(count);
        long total = 0;

        for (int i = 0; i < count; i++)
        {
            byte[] prefix = new byte[6];
            await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);

            int nameLength = prefix[0] | (prefix[1] << 8);
            uint size = ReadU32(prefix, 2);

            if (nameLength is <= 0 or > MaxNameLength)
            {
                throw new InvalidDataException($"world transfer: implausible name length {nameLength}");
            }

            if (size > MaxFileBytes)
            {
                throw new InvalidDataException($"world transfer: file {i} claims {size:N0} bytes");
            }

            total += size;
            if (total > MaxTotalBytes)
            {
                throw new InvalidDataException($"world transfer: total exceeds {MaxTotalBytes:N0} bytes");
            }

            byte[] name = new byte[nameLength];
            await ReadExactlyAsync(stream, name, cancellationToken).ConfigureAwait(false);

            byte[] data = new byte[size];
            await ReadExactlyAsync(stream, data, cancellationToken).ConfigureAwait(false);

            files.Add(new WorldFile(SafeName(Encoding.Latin1.GetString(name)), data));
        }

        return files;
    }

    /// <summary>
    /// Checks that a received name is already a bare file name, and REFUSES anything else.
    ///
    /// ⚠ The sender is remote, and a name is the one field of this protocol that becomes a
    /// PATH. Without this, "..\..\swkotor.exe" would be a remote host writing wherever it
    /// liked on a joiner's disk.
    ///
    /// Refusing rather than sanitising is deliberate. Reducing the name to its last
    /// component would also be safe — the file would land inside the save slot — but a name
    /// that needed reducing did not come from a save directory, and a sender that offers one
    /// has nothing else worth trusting either. Every name a real save uses (SAVEGAME.sav,
    /// GLOBALVARS.res, PARTYTABLE.res, savenfo.res) passes through untouched.
    /// </summary>
    private static string SafeName(string name)
    {
        if (name.Length == 0 || name is "." or ".."
            || name.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !string.Equals(Path.GetFileName(name), name, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"world transfer: refusing the file name '{name}'");
        }

        return name;
    }

    private static long TotalBytes(IReadOnlyList<WorldFile> world)
    {
        long total = 0;
        foreach (WorldFile file in world)
        {
            total += file.Data.Length;
        }

        return total;
    }

    /// <summary>Fills the buffer or throws. A short read is a truncated transfer, and a
    /// half-written save is worse than no save.</summary>
    private static async Task ReadExactlyAsync(
        NetworkStream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = await stream
                .ReadAsync(buffer.AsMemory(read), cancellationToken)
                .ConfigureAwait(false);

            if (n == 0)
            {
                throw new InvalidDataException(
                    $"world transfer: the connection closed after {read} of {buffer.Length} bytes");
            }

            read += n;
        }
    }

    private static uint ReadU32(byte[] buffer, int offset) =>
        (uint)(buffer[offset] | (buffer[offset + 1] << 8)
            | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24));

    private static void WriteU32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
