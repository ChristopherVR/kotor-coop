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
    private long _versionMismatch;

    // STUN interception (see StunClient/PublicAddressDiscovery): a STUN probe has to be sent
    // from THIS socket so the NAT mapping it observes is the game port's own mapping, not a
    // throwaway one — but the receive loop below is the only reader of this socket, so a STUN
    // reply is pulled out of that same loop rather than requiring a second socket. Only one
    // probe is ever in flight at a time (PublicAddressDiscovery tries one STUN server at a
    // time), so a single waiter slot is enough.
    private readonly Lock _stunGate = new();
    private TaskCompletionSource<byte[]?>? _stunWaiter;
    private byte[]? _stunTransactionId;

    // Reachability self-probe interception (see ReachabilityProbe/ReachabilityCheck): the
    // probe is sent from a throwaway SECOND socket to this host's own public address, and if
    // the router hairpins it, it arrives here — on the game port's listener — exactly as a
    // joiner's traffic would. Same single-slot waiter shape as STUN above, behind the same
    // gate; a probe nobody is waiting for is dropped silently, never handed to the session.
    private TaskCompletionSource<bool>? _probeWaiter;
    private byte[]? _probeNonce;

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

        try
        {
            _socket.Bind(new IPEndPoint(IPAddress.Any, listenPort));
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            // Dispose before rethrowing: the constructor never finishes, so nothing else
            // will ever close this handle otherwise.
            _socket.Dispose();
            throw new PortInUseException(listenPort, ex);
        }

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

    /// <summary>A datagram with our magic and a well-formed known type, but a protocol
    /// version we do not speak — a real peer running different co-op code, not the garbage
    /// <see cref="NoteMalformed"/> counts. Kept separate so a run of these (a lobby full of
    /// mismatched builds) reads as "wrong version", not "wrong version, and also noisy".</summary>
    public long VersionMismatchPackets => Interlocked.Read(ref _versionMismatch);

    public void NoteVersionMismatch() => Interlocked.Increment(ref _versionMismatch);

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

            // A simulated duplicate is the same datagram sent a second time, after the
            // original survived the loss decision. This exercises the session deduplication
            // rules without turning a dropped datagram into a delivery.
            if (Sim.Armed && Sim.DuplicateOutbound())
            {
                int again = _socket.SendTo(data, SocketFlags.None, destination);
                Interlocked.Increment(ref _packetsSent);
                Interlocked.Add(ref _bytesSent, again);
            }
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

            // A blackout also consumes packets that were already delayed in flight.
            if (Sim.BlackedOut(SessionClock.NowMs, outbound: true))
            {
                return;
            }

            int sent = _socket.SendTo(data, SocketFlags.None, destination);
            Interlocked.Increment(ref _packetsSent);
            Interlocked.Add(ref _bytesSent, sent);

            if (Sim.Armed && Sim.DuplicateOutbound())
            {
                int again = _socket.SendTo(data, SocketFlags.None, destination);
                Interlocked.Increment(ref _packetsSent);
                Interlocked.Add(ref _bytesSent, again);
            }
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

                    // Delay is applied on the sender side, where every datagram has its own
                    // fire-and-forget timer and can genuinely reorder. Delaying this single
                    // receive loop would head-of-line block all later packets and double-count
                    // the one-way latency.
                }

                // A STUN reply (see StunClient/SendStunRequestAsync) shares this socket with
                // ordinary game traffic. Hand it to whatever is waiting on it and keep
                // listening — it must never reach the session as a "malformed" game packet,
                // and nothing but the STUN prober ever expects it.
                if (result.ReceivedBytes >= StunClient.HeaderLength
                    && StunClient.LooksLikeStunMessage(buffer.AsSpan(0, result.ReceivedBytes))
                    && TryCompleteStunWaiter(buffer.AsSpan(0, result.ReceivedBytes)))
                {
                    continue;
                }

                // Our own reachability probe coming back in (see SendReachabilityProbeAsync).
                // Dropped whether or not anything is waiting: it is never game traffic.
                if (ReachabilityProbe.LooksLikeProbe(buffer.AsSpan(0, result.ReceivedBytes)))
                {
                    TryCompleteProbeWaiter(buffer.AsSpan(0, result.ReceivedBytes));
                    continue;
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

    /// <summary>
    /// Sends a STUN Binding Request (<see cref="StunClient.BuildBindingRequest"/>) to
    /// <paramref name="stunServer"/> from THIS socket — the game port's own socket, so the NAT
    /// mapping STUN observes is the mapping a joining player's traffic will actually hit — and
    /// waits for the matching response, intercepted out of the ordinary receive loop above.
    /// Bypasses <see cref="Sim"/> deliberately: a STUN probe is infrastructure, not simulated
    /// game traffic, and a live netsim blackout must never look like "STUN is broken".
    ///
    /// Returns null on a send failure, a timeout, or cancellation — never throws for those, so
    /// a caller (<see cref="PublicAddressDiscovery"/>) can simply try the next configured
    /// server.
    /// </summary>
    public async Task<byte[]?> SendStunRequestAsync(
        IPEndPoint stunServer, byte[] request, byte[] transactionId, int timeoutMs, CancellationToken cancellationToken)
    {
        TaskCompletionSource<byte[]?> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stunGate)
        {
            _stunWaiter = tcs;
            _stunTransactionId = transactionId;
        }

        try
        {
            try
            {
                _socket.SendTo(request, SocketFlags.None, stunServer);
            }
            catch (SocketException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            using (timeoutCts.Token.Register(() => tcs.TrySetResult(null)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_stunGate)
            {
                if (ReferenceEquals(_stunWaiter, tcs))
                {
                    _stunWaiter = null;
                    _stunTransactionId = null;
                }
            }
        }
    }

    /// <summary>Matches an incoming STUN-shaped datagram against the pending waiter's
    /// transaction id and completes it. Returns false (leaving the datagram untouched by this
    /// method) when nothing is waiting or the transaction id does not match — a STUN-shaped
    /// datagram with no matching waiter is simply stray traffic, dropped like any other.</summary>
    private bool TryCompleteStunWaiter(ReadOnlySpan<byte> data)
    {
        TaskCompletionSource<byte[]?>? waiter;
        lock (_stunGate)
        {
            if (_stunWaiter is null || _stunTransactionId is null
                || data.Length < 8 + StunClient.TransactionIdLength
                || !data.Slice(8, StunClient.TransactionIdLength).SequenceEqual(_stunTransactionId))
            {
                return false;
            }

            waiter = _stunWaiter;
            _stunWaiter = null;
            _stunTransactionId = null;
        }

        waiter.TrySetResult(data.ToArray());
        return true;
    }

    /// <summary>
    /// Sends one <see cref="ReachabilityProbe"/> from a throwaway second socket to
    /// <paramref name="target"/> — meant to be this host's OWN public IP:port as STUN reported
    /// it — and waits for it to arrive back on THIS socket's receive loop. True: it came back
    /// (the router hairpins, and the port is open end to end). False: nothing arrived within
    /// <paramref name="timeoutMs"/> (many routers simply do not hairpin, so this is "unknown",
    /// not "closed" — see <see cref="ReachabilityRule"/>). Null: the probe could not even be
    /// sent. Never throws for any of those; bypasses <see cref="Sim"/> like a STUN request.
    /// </summary>
    public async Task<bool?> SendReachabilityProbeAsync(IPEndPoint target, int timeoutMs, CancellationToken cancellationToken)
    {
        byte[] probe = ReachabilityProbe.Build(out byte[] nonce);
        TaskCompletionSource<bool> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stunGate)
        {
            _probeWaiter = tcs;
            _probeNonce = nonce;
        }

        try
        {
            try
            {
                using Socket second = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                second.Bind(new IPEndPoint(IPAddress.Any, 0));
                second.SendTo(probe, SocketFlags.None, target);
            }
            catch (SocketException)
            {
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            using (timeoutCts.Token.Register(() => tcs.TrySetResult(false)))
            {
                return await tcs.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_stunGate)
            {
                if (ReferenceEquals(_probeWaiter, tcs))
                {
                    _probeWaiter = null;
                    _probeNonce = null;
                }
            }
        }
    }

    private void TryCompleteProbeWaiter(ReadOnlySpan<byte> data)
    {
        TaskCompletionSource<bool>? waiter;
        lock (_stunGate)
        {
            if (_probeWaiter is null || _probeNonce is null || !ReachabilityProbe.Matches(data, _probeNonce))
            {
                return;
            }

            waiter = _probeWaiter;
            _probeWaiter = null;
            _probeNonce = null;
        }

        waiter.TrySetResult(true);
    }

    public void Dispose()
    {
        lock (_stunGate)
        {
            _stunWaiter?.TrySetResult(null);
            _stunWaiter = null;
            _stunTransactionId = null;
            _probeWaiter?.TrySetResult(false);
            _probeWaiter = null;
            _probeNonce = null;
        }

        try
        {
            _socket.Dispose();
        }
        catch (SocketException)
        {
        }
    }
}
