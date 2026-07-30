using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace KotorCoop.Session;

public readonly record struct Datagram(IPEndPoint From, byte[] Buffer, int Length)
{
    public ReadOnlySpan<byte> Span => Buffer.AsSpan(0, Length);
}

/// <summary>
/// A plain UDP socket. Player state is sent unreliably and at a fixed rate: a dropped
/// position update is simply superseded by the next one 50 ms later, so retransmitting
/// it would add latency for no benefit. Only world flags get reliability, and they get
/// it at the application layer where the retry policy can be tuned.
/// </summary>
public sealed class UdpChannel : IDisposable
{
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private readonly Socket _socket;
    private long _packetsSent;
    private long _packetsReceived;
    private long _bytesSent;
    private long _bytesReceived;
    private long _malformed;

    public UdpChannel(int listenPort)
    {
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Without this, an ICMP port-unreachable from a peer that has gone away makes
            // the next ReceiveFrom throw ConnectionReset and kills the receive loop.
            // This is the single most common way a Windows UDP server dies.
            try
            {
                _socket.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            }
            catch (SocketException)
            {
                // Not fatal; the receive loop also swallows ConnectionReset defensively.
            }
        }

        _socket.Bind(new IPEndPoint(IPAddress.Any, listenPort));
        LocalEndPoint = (IPEndPoint)_socket.LocalEndPoint!;
    }

    public IPEndPoint LocalEndPoint { get; }

    public long PacketsSent => Interlocked.Read(ref _packetsSent);

    public long PacketsReceived => Interlocked.Read(ref _packetsReceived);

    public long BytesSent => Interlocked.Read(ref _bytesSent);

    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    public long MalformedPackets => Interlocked.Read(ref _malformed);

    public void NoteMalformed() => Interlocked.Increment(ref _malformed);

    public void Send(IPEndPoint destination, ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return;
        }

        try
        {
            int sent = _socket.SendTo(data, SocketFlags.None, destination);
            Interlocked.Increment(ref _packetsSent);
            Interlocked.Add(ref _bytesSent, sent);
        }
        catch (SocketException)
        {
            // A transient send failure is not worth tearing the session down for.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// Receives one datagram. Returns null when the socket is shutting down. The caller
    /// owns the returned buffer until the next call.
    /// </summary>
    public async Task<Datagram?> ReceiveAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[Protocol.ProtocolConstants.MaxPacketSize];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                SocketReceiveFromResult result =
                    await _socket.ReceiveFromAsync(buffer, SocketFlags.None, from, cancellationToken)
                        .ConfigureAwait(false);

                Interlocked.Increment(ref _packetsReceived);
                Interlocked.Add(ref _bytesReceived, result.ReceivedBytes);

                return new Datagram((IPEndPoint)result.RemoteEndPoint, buffer, result.ReceivedBytes);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // See SIO_UDP_CONNRESET above: a peer vanished. Keep listening.
                continue;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }

        return null;
    }

    public void Dispose()
    {
        try
        {
            _socket.Dispose();
        }
        catch (SocketException)
        {
        }
    }
}
