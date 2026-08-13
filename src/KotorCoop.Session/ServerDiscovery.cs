using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace KotorCoop.Session;

/// <summary>A co-op server as heard on the LAN, for the in-game browser.
/// <paramref name="Locked"/> means the host set a password; <paramref name="Module"/> is the
/// module the host is currently standing in ("" at the menu / unknown), so a browsing player
/// can see the game is in progress and where before joining.</summary>
public sealed record ServerInfo(
    IPEndPoint EndPoint, string Name, int PlayerCount, int MaxPlayers, long LastSeenMs,
    bool Locked = false, string Module = "", IReadOnlyList<string>? Portraits = null)
{
    /// <summary>The address a client dials to join, as text.</summary>
    public string Address => $"{EndPoint.Address}:{EndPoint.Port}";

    /// <summary>The host party's portrait resrefs, PC first — the faces the browser draws.
    /// Empty from a host too old to send them.</summary>
    public IReadOnlyList<string> Faces => Portraits ?? [];
}

/// <summary>
/// The advertisement a host broadcasts and a browsing client reads. Deliberately tiny and
/// hand-coded, like the game packets: the port is the GAME port (the discovery datagram's
/// own source port is ephemeral), so a listener pairs the sender's address with this port to
/// know where to connect.
///
/// Version 2 appends a flags byte (bit 0: password required) and the host's current module,
/// both AFTER the v1 fields, so a v1 reader that stops at the name still parses a v2 beacon
/// and a v2 reader treats a short (v1) datagram as unlocked/unknown-module.
///
/// Version 3 appends the host party's portrait resrefs the same way — a count byte then that
/// many 16-byte resrefs — so the browser can show WHO you would be joining. Every version
/// reads every later one: the tail is optional at each step, and a reader that runs out of
/// datagram simply keeps the defaults for everything it did not reach.
/// </summary>
public readonly record struct ServerBeacon(
    byte Version, ushort GamePort, byte PlayerCount, byte MaxPlayers, string Name,
    bool Locked = false, string Module = "", IReadOnlyList<string>? Portraits = null)
{
    /// <summary>'K','D' — distinct from the session channel's magic so stray traffic is ignored.</summary>
    public const ushort Magic = 0x4B44;

    public const int MaxNameLength = 40;

    /// <summary>Odyssey module names are resrefs (16 chars).</summary>
    public const int MaxModuleLength = 16;

    /// <summary>A CResRef is a fixed char[16].</summary>
    public const int ResRefLength = 16;

    /// <summary>The PC plus the two companions KOTOR's active party holds.</summary>
    public const int MaxPortraits = 3;

    private const byte FlagLocked = 1;

    /// <summary>Largest datagram any version can produce.</summary>
    public const int MaxBytes =
        8 + MaxNameLength + 2 + MaxModuleLength + 1 + (MaxPortraits * ResRefLength);

    /// <summary>The portraits carried, never null and never longer than the cap.</summary>
    public IReadOnlyList<string> Faces => Portraits ?? [];

    public int Write(Span<byte> buffer)
    {
        if (buffer.Length < 8)
        {
            return 0;
        }

        byte[] name = Encoding.UTF8.GetBytes(Name.Length > MaxNameLength ? Name[..MaxNameLength] : Name);
        int nameLen = Math.Min(name.Length, MaxNameLength);

        byte[] module = Encoding.UTF8.GetBytes(
            Module.Length > MaxModuleLength ? Module[..MaxModuleLength] : Module);
        int moduleLen = Math.Min(module.Length, MaxModuleLength);

        if (buffer.Length < 8 + nameLen + 2 + moduleLen)
        {
            return 0;
        }

        buffer[0] = (byte)(Magic >> 8);
        buffer[1] = (byte)(Magic & 0xFF);
        buffer[2] = Version;
        buffer[3] = (byte)(GamePort >> 8);
        buffer[4] = (byte)GamePort;
        buffer[5] = PlayerCount;
        buffer[6] = MaxPlayers;
        buffer[7] = (byte)nameLen;
        name.AsSpan(0, nameLen).CopyTo(buffer[8..]);

        int at = 8 + nameLen;
        buffer[at++] = Locked ? FlagLocked : (byte)0;
        buffer[at++] = (byte)moduleLen;
        module.AsSpan(0, moduleLen).CopyTo(buffer[at..]);
        at += moduleLen;

        // v3 tail. Written even when empty (a zero count), so a v3 reader can tell "this host
        // sends no portraits" from "this datagram stopped before the portraits" — the second
        // is a v2 host and the first is a v3 host with nothing to show.
        IReadOnlyList<string> faces = Faces;
        int portraitCount = Math.Min(faces.Count, MaxPortraits);

        if (buffer.Length < at + 1 + (portraitCount * ResRefLength))
        {
            return at; // no room: still a valid v2 datagram
        }

        buffer[at++] = (byte)portraitCount;

        for (int i = 0; i < portraitCount; i++)
        {
            Span<byte> cell = buffer.Slice(at, ResRefLength);
            cell.Clear();

            // Fixed-width and NUL-padded, exactly as the engine stores a CResRef; anything
            // longer is a malformed resref and is cut rather than allowed to shift the tail.
            byte[] face = Encoding.UTF8.GetBytes(faces[i]);
            face.AsSpan(0, Math.Min(face.Length, ResRefLength)).CopyTo(cell);
            at += ResRefLength;
        }

        return at;
    }

    public static ServerBeacon? Read(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8 || (ushort)((buffer[0] << 8) | buffer[1]) != Magic)
        {
            return null;
        }

        byte version = buffer[2];
        ushort gamePort = (ushort)((buffer[3] << 8) | buffer[4]);
        byte players = buffer[5];
        byte maxPlayers = buffer[6];
        int nameLen = buffer[7];

        if (nameLen > MaxNameLength || buffer.Length < 8 + nameLen)
        {
            return null;
        }

        string name = Encoding.UTF8.GetString(buffer.Slice(8, nameLen));

        // The v2 tail is optional: a v1 beacon (or a truncated datagram) reads as an
        // unlocked server in an unknown module rather than being rejected.
        bool locked = false;
        string module = string.Empty;
        int at = 8 + nameLen;
        if (buffer.Length >= at + 2)
        {
            locked = (buffer[at] & FlagLocked) != 0;
            int moduleLen = buffer[at + 1];
            if (moduleLen <= MaxModuleLength && buffer.Length >= at + 2 + moduleLen)
            {
                module = Encoding.UTF8.GetString(buffer.Slice(at + 2, moduleLen));
                at += 2 + moduleLen;
            }
            else
            {
                // A module length the datagram cannot back is malformed, and everything after
                // it is unlocatable — stop rather than guess where the v3 tail begins.
                at = buffer.Length;
            }
        }

        // The v3 tail is optional in the same way: a v2 host simply has no portraits.
        List<string>? portraits = null;
        if (buffer.Length > at)
        {
            int portraitCount = buffer[at++];

            // ⚠ Zero stays NULL rather than becoming an empty list. The count byte is always
            // WRITTEN — a later version appending another field needs the portrait section to
            // have a known end, or its field would be read as the count — but "no portraits"
            // and "no portrait section" mean the same thing to every reader, and collapsing
            // them is what lets a beacon compare equal to itself across a round trip.
            if (portraitCount is > 0 and <= MaxPortraits
                && buffer.Length >= at + (portraitCount * ResRefLength))
            {
                portraits = new List<string>(portraitCount);
                for (int i = 0; i < portraitCount; i++)
                {
                    // NUL-padded fixed cells: cut at the first NUL, or the resref carries
                    // padding into a texture name that then resolves to nothing.
                    ReadOnlySpan<byte> cell = buffer.Slice(at, ResRefLength);
                    int end = cell.IndexOf((byte)0);
                    portraits.Add(Encoding.UTF8.GetString(end < 0 ? cell : cell[..end]));
                    at += ResRefLength;
                }
            }
        }

        return new ServerBeacon(version, gamePort, players, maxPlayers, name, locked, module, portraits);
    }
}

/// <summary>
/// The live set of servers heard on the LAN, keyed by (address, game port). Pure and
/// thread-safe so the collect/expire behaviour can be unit-tested without a socket: a server
/// unheard-from for <see cref="ExpiryMs"/> drops off the list.
/// </summary>
public sealed class ServerList
{
    public const long ExpiryMs = 6000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, ServerInfo> _servers = [];

    /// <summary>Records (or refreshes) a server heard from <paramref name="from"/>.</summary>
    public void Observe(IPEndPoint from, in ServerBeacon beacon, long nowMs)
    {
        IPEndPoint endpoint = new(from.Address, beacon.GamePort);
        string key = endpoint.ToString();

        lock (_gate)
        {
            _servers[key] = new ServerInfo(
                endpoint, beacon.Name, beacon.PlayerCount, beacon.MaxPlayers, nowMs,
                beacon.Locked, beacon.Module, beacon.Portraits);
        }
    }

    /// <summary>The servers still fresh at <paramref name="nowMs"/>, newest-seen first.</summary>
    public IReadOnlyList<ServerInfo> Snapshot(long nowMs)
    {
        lock (_gate)
        {
            // Drop the stale entries so the list does not grow without bound.
            if (_servers.Count > 0)
            {
                List<string>? dead = null;
                foreach ((string key, ServerInfo info) in _servers)
                {
                    if (nowMs - info.LastSeenMs > ExpiryMs)
                    {
                        (dead ??= []).Add(key);
                    }
                }

                if (dead is not null)
                {
                    foreach (string key in dead)
                    {
                        _servers.Remove(key);
                    }
                }
            }

            return [.. _servers.Values.OrderByDescending(s => s.LastSeenMs)];
        }
    }
}

/// <summary>
/// LAN presence for co-op: a host broadcasts a <see cref="ServerBeacon"/> every second so
/// browsing clients on the same network can list it without anyone typing an IP. The two
/// halves are independent — a host advertises, a browsing menu listens — and both are optional
/// (direct connect by IP still works with neither running).
/// </summary>
public sealed class ServerDiscovery : IDisposable
{
    public const int DiscoveryPort = 27016;
    private const int AdvertiseIntervalMs = 1000;
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private readonly int _port;
    private readonly ServerList _list = new();
    private readonly CancellationTokenSource _cts = new();
    private Task _advertise = Task.CompletedTask;
    private Task _listen = Task.CompletedTask;

    /// <summary><paramref name="port"/> lets a test use an isolated port; production uses the
    /// well-known <see cref="DiscoveryPort"/>.</summary>
    public ServerDiscovery(int port = DiscoveryPort) => _port = port;

    /// <summary>The servers currently visible on the LAN. Empty until listening has run.</summary>
    public IReadOnlyList<ServerInfo> Servers => _list.Snapshot(Environment.TickCount64);

    /// <summary>
    /// Starts broadcasting this host's presence. <paramref name="playerCount"/> and
    /// <paramref name="module"/> are read each beacon so the list shows a live count and
    /// where the host currently is; <paramref name="locked"/> tells browsers a password
    /// will be asked for before they dial.
    /// </summary>
    public void StartAdvertising(
        string name, int gamePort, byte maxPlayers, Func<int> playerCount,
        bool locked = false, Func<string>? module = null,
        Func<IReadOnlyList<string>>? portraits = null)
    {
        _advertise = Task.Run(
            () => AdvertiseLoopAsync(
                name, gamePort, maxPlayers, playerCount, locked, module, portraits, _cts.Token));
    }

    /// <summary>Starts listening for host beacons and building the server list.</summary>
    public void StartListening()
    {
        _listen = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task AdvertiseLoopAsync(
        string name, int gamePort, byte maxPlayers, Func<int> playerCount,
        bool locked, Func<string>? module, Func<IReadOnlyList<string>>? portraits,
        CancellationToken token)
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            EnableBroadcast = true,
        };

        IPEndPoint broadcast = new(IPAddress.Broadcast, _port);
        byte[] buffer = new byte[ServerBeacon.MaxBytes];

        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromMilliseconds(AdvertiseIntervalMs));
            do
            {
                int count = Math.Clamp(playerCount(), 0, 255);
                string where;
                try
                {
                    where = module?.Invoke() ?? string.Empty;
                }
                catch
                {
                    where = string.Empty; // a bridge read mid-transition must not kill the loop
                }

                IReadOnlyList<string> faces;
                try
                {
                    faces = portraits?.Invoke() ?? [];
                }
                catch
                {
                    faces = []; // same rule as the module read: never kill the loop
                }

                ServerBeacon beacon = new(
                    ProtocolConstantsVersion, (ushort)gamePort, (byte)count, maxPlayers, name,
                    locked, where, faces);
                int length = beacon.Write(buffer);
                if (length > 0)
                {
                    try
                    {
                        await socket.SendToAsync(buffer.AsMemory(0, length), broadcast, token).ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        // A transient network error (no route, interface down) must not kill
                        // the loop; the next tick tries again.
                    }
                }
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        using Socket socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }
            catch (SocketException)
            {
            }
        }

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, _port));
        }
        catch (SocketException)
        {
            return; // another listener already holds the port; direct connect still works.
        }

        byte[] buffer = new byte[ServerBeacon.MaxBytes];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);

        try
        {
            while (!token.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try
                {
                    result = await socket.ReceiveFromAsync(buffer, any, token).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    continue; // ICMP unreachable etc.; keep listening.
                }

                if (ServerBeacon.Read(buffer.AsSpan(0, result.ReceivedBytes)) is { } beacon
                    && result.RemoteEndPoint is IPEndPoint from)
                {
                    _list.Observe(from, beacon, Environment.TickCount64);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Beacon format version; bump with any wire change. Version 2 appended the
    /// locked flag and the host's module, version 3 the party's portrait resrefs — all
    /// back-compatibly (readers of any version parse beacons of any version).</summary>
    private const byte ProtocolConstantsVersion = 3;

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            Task.WhenAll(_advertise, _listen).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Best-effort shutdown.
        }

        _cts.Dispose();
    }
}
