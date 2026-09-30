using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Client-side fetch for the UDP world-transfer fallback (<see cref="PacketType.WorldTransferRequest"/>
/// et al.) — the UDP counterpart of <see cref="WorldTransfer.FetchAsync"/>, used only when
/// <see cref="WorldAdoption"/>'s quick TCP-connect probe finds <see cref="WorldTransfer"/>'s
/// listener genuinely unreachable (a UDP-only tunnel/NAT path, found live 2026-09-10). See
/// docs/research/network-topology.md's UDP world-transfer section.
///
/// Fully self-contained, like <see cref="WorldTransfer.FetchAsync"/> is: opens its own ephemeral
/// <see cref="UdpChannel"/> (port 0, exactly like <see cref="CoopClient"/>'s own socket), and
/// does not touch any live co-op session — by the time <see cref="WorldAdoption"/> fetches, this
/// install's own co-op socket has already been closed (see <c>SessionManager.RejoinAfterLoadAsync</c>'s
/// remarks), so there is no existing session channel to ride even if one were still open.
/// </summary>
public static class WorldTransferUdp
{
    /// <summary>How often the initial request is resent while no header has arrived yet.</summary>
    private const int RequestRetryIntervalMs = 500;

    /// <summary>How often the client reports its missing-chunk bitmap once a transfer is in
    /// progress — frequent enough that <see cref="CoopHost"/>'s own per-transfer
    /// <c>LastActivityMs</c> never goes stale mid-transfer, per that class's remarks.</summary>
    private const int StatusReportIntervalMs = 250;

    /// <summary>Finite deadline allowing a128 MiB first pass plus bounded resends.</summary>
    private const int OverallTimeoutMs = 240_000;

    /// <summary>How often the poll loop wakes to check timers and drive sends — a plain
    /// poll-driven state machine, the same shape every other reliable channel in this codebase
    /// uses, just running standalone instead of inside a CoopHost/CoopClient tick.</summary>
    private const int PollIntervalMs = 50;

    public static async Task<FetchedWorld> FetchAsync(
        IPEndPoint host, uint passwordHash, CancellationToken cancellationToken)
    {
        using UdpChannel channel = new(0);
        uint requestId = (uint)Random.Shared.Next(1, int.MaxValue);

        using CancellationTokenSource timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(OverallTimeoutMs);
        CancellationToken ct = timeoutCts.Token;

        Lock gate = new();
        bool refused = false;
        bool noWorldAvailable = false;
        string? refusalMessage = null;
        WorldTransferUdpReceiver? receiver = null;

        // One background reader — the only caller of channel.ReceiveAsync, so there is never a
        // second concurrent receive racing it on the same socket. Everything it learns is
        // handed to the poll loop below through the fields above, under gate.
        Task receiveLoop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                Datagram? datagram;
                try
                {
                    datagram = await channel.ReceiveAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (datagram is not { } received || !received.From.Equals(host))
                {
                    continue; // stray traffic from anyone but the host we asked
                }

                switch (PacketCodec.PeekType(received.Span))
                {
                    case PacketType.WorldTransferHeader:
                        if (PacketCodec.ReadWorldTransferHeader(received.Span) is { } h
                            && h.RequestId == requestId)
                        {
                            lock (gate)
                            {
                                if (receiver is null && !refused)
                                {
                                    if (h.Status == WorldTransferUdpStatus.Ok)
                                    {
                                        try
                                        {
                                            receiver = new WorldTransferUdpReceiver(
                                                h.RequestId, h.SaveIdentity, h.TotalBytes, h.TotalChunks);
                                        }
                                        catch (InvalidDataException ex)
                                        {
                                            refused = true;
                                            refusalMessage = ex.Message;
                                        }
                                    }
                                    else
                                    {
                                        refused = true;
                                        noWorldAvailable = h.Status == WorldTransferUdpStatus.NoWorld;
                                        refusalMessage = h.Status switch
                                        {
                                            WorldTransferUdpStatus.BadPassword =>
                                                "world transfer (UDP): wrong password",
                                            WorldTransferUdpStatus.NoWorld =>
                                                "world transfer (UDP): the host has no world to send yet",
                                            _ => $"world transfer (UDP): refused (status {h.Status})",
                                        };
                                    }
                                }
                            }
                        }

                        break;

                    case PacketType.WorldTransferChunk:
                        if (PacketCodec.ReadWorldTransferChunk(received.Span) is { } chunk
                            && chunk.RequestId == requestId)
                        {
                            lock (gate)
                            {
                                receiver?.ReceiveChunk((int)chunk.ChunkIndex, chunk.Payload);
                            }
                        }

                        break;
                }
            }
        }, ct);

        byte[] sendBuffer = new byte[ProtocolConstants.MaxPacketSize];

        void SendRequest()
        {
            int length = PacketCodec.Write(sendBuffer, new WorldTransferRequestPacket(requestId, passwordHash));
            channel.Send(host, sendBuffer.AsSpan(0, length));
        }

        try
        {
            SendRequest();
            long lastRequestMs = SessionClock.NowMs;
            long lastStatusMs = 0;

            while (true)
            {
                await Task.Delay(PollIntervalMs, ct).ConfigureAwait(false);
                long now = SessionClock.NowMs;

                bool localRefused;
                bool localNoWorld;
                string? localMessage;
                WorldTransferUdpReceiver? localReceiver;
                lock (gate)
                {
                    localRefused = refused;
                    localNoWorld = noWorldAvailable;
                    localMessage = refusalMessage;
                    localReceiver = receiver;
                }

                if (localRefused)
                {
                    throw new WorldTransferRefusedException(localMessage!, localNoWorld);
                }

                if (localReceiver is null)
                {
                    if (now - lastRequestMs > RequestRetryIntervalMs)
                    {
                        SendRequest();
                        lastRequestMs = now;
                    }

                    continue;
                }

                if (localReceiver.IsComplete)
                {
                    IReadOnlyList<WorldFile> files = WorldTransfer.ParsePayload(localReceiver.Payload);
                    int completeLength = PacketCodec.Write(
                        sendBuffer, new WorldTransferCompletePacket(requestId, Success: true));
                    channel.Send(host, sendBuffer.AsSpan(0, completeLength));

                    return new FetchedWorld(files, localReceiver.SaveIdentity);
                }

                if (now - lastStatusMs > StatusReportIntervalMs)
                {
                    // At the128 MiB limit this is at most33 bounded datagrams, never one
                    // oversized bitmap. Repeated pages tolerate loss and reordering.
                    for (int first = 0; first < localReceiver.TotalChunks;
                        first += ProtocolConstants.WorldTransferStatusPageChunks)
                    {
                        byte[] bitmap;
                        lock (gate) bitmap = localReceiver.MissingBitmap(first, ProtocolConstants.WorldTransferStatusPageChunks);
                        int statusLength = PacketCodec.Write(sendBuffer,
                            new WorldTransferChunkStatusPacket(requestId, bitmap, (uint)first));
                        channel.Send(host, sendBuffer.AsSpan(0, statusLength));
                    }
                    lastStatusMs = now;
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own OverallTimeoutMs fired, not the caller's token — report it the same way
            // WorldTransfer's TCP path reports every other transport failure.
            throw new InvalidDataException(
                "world transfer (UDP): timed out - no reply from the host");
        }
        finally
        {
            timeoutCts.Cancel();
            try
            {
                await receiveLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
