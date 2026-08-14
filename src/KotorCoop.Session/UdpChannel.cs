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

    /// <summary>
    /// The fault injector for this channel. Off by default; every channel in the session
    /// passes through here, so arming it partitions the whole peer rather than one feature.
    /// See <see cref="NetworkSim"/> for why the seam is here and not per-channel.
    /// </summary>
    public NetworkSim Sim { get; } = new();

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

        // One volatile bool on the clean path; nothing below runs unless a fault is armed.
        if (Sim.Armed)
        {
            long now = SessionClock.NowMs;
            if (Sim.DropOutbound(now))
            {
                return;
            }

            int delay = Sim.DelayMs();
            if (delay > 0)
            {
                // The span is borrowed from a tick-loop buffer that is about to be reused,
                // so a delayed send has to own its bytes.
                _ = SendLaterAsync(destination, data.ToArray(), delay);
                return;
            }
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
    /// Holds a datagram back to simulate latency. Fire-and-forget: a simulated link losing
    /// a delayed packet at shutdown is exactly what a real one does.
    /// </summary>
    private async Task SendLaterAsync(IPEndPoint destination, byte[] data, int delayMs)
    {
        try
        {
            await Task.Delay(delayMs).ConfigureAwait(false);
            int sent = _socket.SendTo(data, SocketFlags.None, destination);
            Interlocked.Increment(ref _packetsSent);
            Interlocked.Add(ref _bytesSent, sent);
        }
        catch (SocketException)
        {
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

                if (Sim.Armed)
                {
                    long now = SessionClock.NowMs;
                    if (Sim.DropInbound(now))
                    {
                        // Received by the socket, never seen by the session — a packet the
                        // link ate on the way in.
                        continue;
                    }

                    int delay = Sim.DelayMs();
                    if (delay > 0)
                    {
                        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                    }
                }

                Interlocked.Increment(ref _packetsReceived);
                Interlocked.Add(ref _bytesReceived, result.ReceivedBytes);

                return new Datagram((IPEndPoint)result.RemoteEndPoint, buffer, result.ReceivedBytes);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                // See SIO_UDP_CONNRESET above: a peer vanished. Keep listening.
                continue;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                // The socket was disposed with this receive in flight. That is a shutdown,
                // not a fault — and it is the ordinary shape of one whenever teardown races
                // the cancellation token rather than following it, which anything that
                // partitions a link and then stops the session does constantly.
                return null;
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
