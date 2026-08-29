using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// A joiner who adopts the host's world must end up seeing the host's CURRENT world, not the
/// world as of whenever the host's save was taken.
///
/// THE LIVE SYMPTOM these pin shut: after a new-character join into a session already in
/// progress, a door was OPEN on the host and CLOSED on the client, and stayed that way. Two
/// independent defects produced it, and each gets its own test here because either alone is
/// enough to reproduce it.
///
/// Real sockets, real packets, real tick loops — the same harness
/// <see cref="SessionIntegrationTests"/> uses, for the same reason: the bug lives in what
/// actually crosses the wire.
/// </summary>
public class PostAdoptConvergenceTests
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

    /// <summary>
    /// ⚠ THE DEFECT: the flag snapshot — the ONLY way a host-side change reaches a client that
    /// did not watch it happen, since there are no host→client flag ops — was a single datagram
    /// holding at most 106 entries, taken from the head of an insertion-ordered store that is
    /// never pruned. Doors and placeables mint keys into that same store, so an ordinary
    /// session passes 106 quickly, and everything after it was structurally undeliverable
    /// FOREVER. Worse, the digest is computed over the whole store, so the client could never
    /// stop asking: it re-requested the same truncated head every 500 ms for the rest of the
    /// session.
    ///
    /// This test fills the store past the cap and then does exactly what the live failure did —
    /// the host opens a door LAST, after everything else — and requires the client to see it.
    /// Before the paging fix the door sits at the tail and never arrives.
    /// </summary>
    [Fact]
    public async Task AHostChangeBeyondOneDatagramStillReachesTheClient()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(20));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // A session's worth of world state: comfortably more than one datagram holds.
        int overflow = (ProtocolConstants.MaxFlagEntriesPerPacket * 2) + 7;
        for (int i = 0; i < overflow; i++)
        {
            host.SetFlag(10_000u + (uint)i, 1);
        }

        // THEN the door the player is looking at. Last, so it is at the tail of the store —
        // exactly where the truncation used to drop it.
        const uint doorFlag = 99_999u;
        host.SetFlag(doorFlag, 1);

        Assert.True(
            await WaitForAsync(
                () => client.Flags.TryGet(doorFlag, out sbyte v) && v == 1, Timeout, Publish),
            "the host's door never reached the client: the flag snapshot dropped everything "
                + "past one datagram, which is the whole host-to-client backfill path");

        // And the tripwire settles, rather than the client asking the same unanswerable
        // question every 500 ms for the rest of the session.
        Assert.True(
            await WaitForAsync(() => client.Flags.Version == host.Flags.Version, Timeout, Publish),
            "flag versions never converged");

        Assert.True(
            await WaitForAsync(() => client.Flags.Digest == host.Flags.Digest, Timeout, Publish),
            "flag digests never converged, so the client would re-request forever");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// ⚠ THE SECOND DEFECT, and the one that made the wrong state STICK rather than merely be
    /// missing: <c>EventFlagStore.Set</c> mints the next Lamport serial unconditionally, so a
    /// client that republishes a stale local reading AFTER merging the host's snapshot outranks
    /// the host in both stores. That is precisely what an adopted load used to cause — the
    /// agent's door shadow still described the world the joiner left, the staged save has the
    /// door closed, and the next scan reported a genuine local transition to CLOSED.
    ///
    /// This pins the MECHANISM at the store level (no bridge, no engine): a later local Set
    /// beats an earlier merged host value. The fix is not to change that rule — a client's real
    /// changes must still win — but to stop the client manufacturing a phantom one, which is
    /// what <c>KotorGameBridge.ForgetWorldShadows</c> does and what
    /// <see cref="TheAdoptPathForgetsItsShadows"/> covers.
    /// </summary>
    [Fact]
    public void AStaleLocalRepublishWouldOutrankTheHostsValue()
    {
        EventFlagStore hostStore = new();
        EventFlagStore clientStore = new();

        const uint doorFlag = 4242u;

        // The host opens the door after its save was taken.
        hostStore.Set(doorFlag, 1, originPeerId: 1);

        // The client adopts that world and merges the host's snapshot.
        clientStore.MergeSnapshot(hostStore.SnapshotEntries(), hostStore.Version);
        Assert.True(clientStore.TryGet(doorFlag, out sbyte merged) && merged == 1);

        // Now the stale shadow fires: the client "observes" the staged save's closed door.
        clientStore.Set(doorFlag, 0, originPeerId: 2);

        // It outranks the host's value — which is correct behaviour for a REAL change, and
        // catastrophic for a phantom one. This is why the shadow must be dropped instead.
        Assert.True(clientStore.TryGet(doorFlag, out sbyte after) && after == 0);
        Assert.True(
            clientStore.Version > hostStore.Version,
            "a local Set after a merge must outrank the merged value — that is the rule the "
                + "phantom republish was abusing");
    }

    /// <summary>
    /// The adopt paths must actually call <c>ForgetWorldShadows</c>. Asserted against the
    /// SOURCE rather than through a bridge fake, because the thing that broke was not the
    /// method's behaviour — there was no method — but the fact that nothing on the adopt path
    /// invalidated anything, and a fake bridge would happily pass a test of a call nobody makes.
    /// </summary>
    [Fact]
    public void TheAdoptPathForgetsItsShadows()
    {
        string root = RepositoryRoot();

        foreach (string relative in
            (string[])["src/KotorCoop.Agent/CoopCommandDispatcher.SaveLoad.cs",
                "src/KotorCoop.Agent/NewCharacterJoinFlow.cs"])
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"expected to find {path}");

            string text = File.ReadAllText(path);
            Assert.Contains("ForgetWorldShadows()", text);

            int forget = text.IndexOf("ForgetWorldShadows()", StringComparison.Ordinal);
            int reconnect = text.IndexOf("Reconnect()", StringComparison.Ordinal);

            Assert.True(
                reconnect > forget,
                $"{relative}: the shadows must be dropped BEFORE the socket reopens, or the "
                    + "first scan of the new session diffs the host's staged world against the "
                    + "one we left");
        }
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KotorCoop.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.True(directory is not null, $"no KotorCoop.slnx above {AppContext.BaseDirectory}");
        return directory!.FullName;
    }
}
