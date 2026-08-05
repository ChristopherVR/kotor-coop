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
    bool Locked = false, string Module = "")
{
    /// <summary>The address a client dials to join, as text.</summary>
    public string Address => $"{EndPoint.Address}:{EndPoint.Port}";
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
/// </summary>
public readonly record struct ServerBeacon(
    byte Version, ushort GamePort, byte PlayerCount, byte MaxPlayers, string Name,
    bool Locked = false, string Module = "")
{
    /// <summary>'K','D' — distinct from the session channel's magic so stray traffic is ignored.</summary>
    public const ushort Magic = 0x4B44;

    public const int MaxNameLength = 40;

    /// <summary>Odyssey module names are resrefs (16 chars).</summary>
    public const int MaxModuleLength = 16;

    private const byte FlagLocked = 1;

    /// <summary>Largest datagram either version can produce.</summary>
    public const int MaxBytes = 8 + MaxNameLength + 2 + MaxModuleLength;

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
        return at + moduleLen;
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
            }
        }

        return new ServerBeacon(version, gamePort, players, maxPlayers, name, locked, module);
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
                beacon.Locked, beacon.Module);
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
        bool locked = false, Func<string>? module = null)
    {
        _advertise = Task.Run(
            () => AdvertiseLoopAsync(name, gamePort, maxPlayers, playerCount, locked, module, _cts.Token));
    }

    /// <summary>Starts listening for host beacons and building the server list.</summary>
    public void StartListening()
    {
        _listen = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    private async Task AdvertiseLoopAsync(
        string name, int gamePort, byte maxPlayers, Func<int> playerCount,
        bool locked, Func<string>? module, CancellationToken token)
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

                ServerBeacon beacon = new(
                    ProtocolConstantsVersion, (ushort)gamePort, (byte)count, maxPlayers, name,
                    locked, where);
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
    /// locked flag and the host's module, back-compatibly (readers of either version parse
    /// beacons of either version).</summary>
    private const byte ProtocolConstantsVersion = 2;

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
