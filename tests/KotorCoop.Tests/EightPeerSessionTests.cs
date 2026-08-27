using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// A real host and SEVEN real clients over loopback UDP — a full <see
/// cref="ProtocolConstants.MaxPeers"/> lobby, the exact scenario P0-2 exists for. With
/// <see cref="WorldSnapshotPacket.MaxEntriesPerPage"/> at 5, eight peers cannot fit in one
/// <see cref="WorldSnapshotPacket"/> page, so this is the end-to-end proof that
/// <see cref="WorldSnapshotPaging"/> (host) and <see cref="WorldSnapshotAssembler"/> (client)
/// actually cooperate over the wire, not just in isolated unit tests. See
/// docs/research/lag-audit.md.
/// </summary>
public class EightPeerSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout, Action? each = null)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            each?.Invoke();

            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    [Fact]
    public async Task AllEightPeersSeeEachOthersPositionsAcrossSeveralPages()
    {
        string[] names = ["Revan", "Malak", "Bastila", "Carth", "Jolee", "Juhani", "Mission", "Zaalbar"];

        using CoopHost host = new(new SessionSettings { PlayerName = names[0], Port = 0, TickRate = 30 });
        IPEndPoint hostEndPoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);

        List<CoopClient> clients = [.. names.Skip(1)
            .Select(name => new CoopClient(new SessionSettings { PlayerName = name, TickRate = 30 }, hostEndPoint))];

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(20));
        List<Task> tasks = [host.RunAsync(cts.Token), .. clients.Select(c => c.RunAsync(cts.Token))];

        // One distinct, plausible position per peer so a mixed-up assignment would be caught.
        PlayerState HostState() => new(new Vector3f(0f, 0f, 0f), 0f, "end_m01aa", 100, 0);
        PlayerState ClientState(int index) =>
            new(new Vector3f(index * 10f, index * 20f, 0f), index, "end_m01aa", 100, 0);

        void Publish()
        {
            host.PublishLocalState(HostState());
            for (int i = 0; i < clients.Count; i++)
            {
                clients[i].PublishLocalState(ClientState(i + 1));
            }
        }

        try
        {
            Assert.True(
                await WaitForAsync(() => clients.All(c => c.IsConnected), Timeout, Publish),
                "all seven clients must be admitted to a full 8-peer lobby");

            // Distinct ids for everyone — a collision here would alias two players onto one
            // entry in every per-peer dictionary in the session.
            HashSet<uint> ids = [CoopHost.HostPeerId, .. clients.Select(c => c.LocalPeerId)];
            Assert.Equal(8, ids.Count);

            Assert.True(
                await WaitForAsync(() => host.GetRemotePeers().Count == clients.Count, Timeout, Publish),
                "the host must see all seven clients");

            // Every client must see every OTHER peer (six other clients + the host), each at
            // its own real position — not the origin, and not another peer's position. This is
            // only reachable if entries spread across more than one page still all arrive:
            // eight peers at five entries per page needs two pages every tick.
            bool AllPositionsConverged()
            {
                foreach (CoopClient client in clients)
                {
                    IReadOnlyList<RemotePeer> seen = client.GetRemotePeers();
                    if (seen.Count != clients.Count)
                    {
                        return false;
                    }

                    if (!seen.Any(p => p.PeerId == CoopHost.HostPeerId && p.State.Position == HostState().Position))
                    {
                        return false;
                    }

                    for (int i = 0; i < clients.Count; i++)
                    {
                        CoopClient other = clients[i];
                        if (other == client)
                        {
                            continue;
                        }

                        if (!seen.Any(p => p.PeerId == other.LocalPeerId
                            && p.State.Position == ClientState(i + 1).Position))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            Assert.True(
                await WaitForAsync(AllPositionsConverged, Timeout, Publish),
                "every one of the 8 peers must see every other peer's own real position");

            // The host actually paged — this is not a coincidence of a lucky single-page fit.
            Assert.True(
                await WaitForAsync(() => host.GetStats().WorldSnapshotPagesSent >= 4, Timeout, Publish),
                $"expected the host to have sent several pages per tick, got "
                + $"{host.GetStats().WorldSnapshotPagesSent}");
        }
        finally
        {
            await cts.CancelAsync();
            await Task.WhenAll(tasks.Select(SwallowCancel));
            foreach (CoopClient client in clients)
            {
                client.Dispose();
            }
        }
    }

    private static async Task SwallowCancel(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
