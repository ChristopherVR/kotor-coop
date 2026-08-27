using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Runs a real host and a real client over loopback UDP. No fakes: actual sockets, actual
/// packets, actual tick loops.
/// </summary>
public class SessionIntegrationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

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
    public async Task ClientConnectsAndBothEndsSeeEachOther()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState hostState = new(new Vector3f(10f, 20f, 0f), 1f, "end_m01aa", 100, 0);
        PlayerState clientState = new(new Vector3f(30f, 40f, 0f), 2f, "end_m01aa", 90, 0);

        void Publish()
        {
            host.PublishLocalState(hostState);
            client.PublishLocalState(clientState);
        }

        bool connected = await WaitForAsync(() => client.IsConnected, Timeout, Publish);
        Assert.True(connected, "client never received a Welcome");
        Assert.Equal(2u, client.LocalPeerId);

        bool clientSeesHost = await WaitForAsync(
            () => client.GetRemotePeers().Any(p => p.PeerId == CoopHost.HostPeerId),
            Timeout,
            Publish);
        Assert.True(clientSeesHost, "client never saw the host's avatar");

        bool hostSeesClient = await WaitForAsync(
            () => host.GetRemotePeers().Any(p => p.PeerId == 2),
            Timeout,
            Publish);
        Assert.True(hostSeesClient, "host never saw the client's avatar");

        RemotePeer hostAsSeenByClient = client.GetRemotePeers().First(p => p.PeerId == CoopHost.HostPeerId);
        Assert.Equal("Revan", hostAsSeenByClient.Name);
        Assert.Equal(hostState.Position, hostAsSeenByClient.State.Position);
        Assert.Equal("end_m01aa", hostAsSeenByClient.State.Module);

        RemotePeer clientAsSeenByHost = host.GetRemotePeers().First(p => p.PeerId == 2);
        Assert.Equal("Malak", clientAsSeenByHost.Name);
        Assert.Equal(clientState.Position, clientAsSeenByHost.State.Position);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// The module string is what the peer filter, the enemy-snapshot gate and travel-follow
    /// all compare, so a peer's TRUE module must survive the wire even when the two players
    /// are in different modules. Written while chasing a live session where a host in
    /// tar_m03aa kept a phantom for a client still on end_m01aa — which is only possible if
    /// one side saw the other's module as its own or as empty.
    /// </summary>
    [Fact]
    public async Task ModuleSurvivesTheWireWhenPlayersAreApart()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState hostState = new(new Vector3f(300f, 200f, 8f), 1f, "tar_m03aa", 100, 0);
        PlayerState clientState = new(new Vector3f(20f, 22f, -1f), 2f, "end_m01aa", 90, 0);

        void Publish()
        {
            host.PublishLocalState(hostState);
            client.PublishLocalState(clientState);
        }

        bool bothSeen = await WaitForAsync(
            () => client.GetRemotePeers().Any(p => p.PeerId == CoopHost.HostPeerId)
                && host.GetRemotePeers().Any(p => p.PeerId == 2),
            Timeout,
            Publish);
        Assert.True(bothSeen, "the two ends never saw each other");

        RemotePeer hostAsSeenByClient = client.GetRemotePeers().First(p => p.PeerId == CoopHost.HostPeerId);
        Assert.Equal("tar_m03aa", hostAsSeenByClient.State.Module);

        RemotePeer clientAsSeenByHost = host.GetRemotePeers().First(p => p.PeerId == 2);
        Assert.Equal("end_m01aa", clientAsSeenByHost.State.Module);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task MovementPropagates()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        float x = 0f;
        void Publish()
        {
            host.PublishLocalState(new PlayerState(new Vector3f(x, 0f, 0f), 0f, "end_m01aa", 100, 0));
            client.PublishLocalState(new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 100, 0));
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // Walk the host avatar and confirm the client's view follows it.
        Assert.True(await WaitForAsync(
            () =>
            {
                x += 1f;
                return client.GetRemotePeers()
                    .Any(p => p.PeerId == CoopHost.HostPeerId && p.State.Position.X > 20f);
            },
            Timeout,
            Publish));

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task WorldFlagsSyncInBothDirections()
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

        // Client opens a door: the reliable op must reach the authoritative host.
        client.SetFlag(1001, 1);
        Assert.True(
            await WaitForAsync(() => host.Flags.TryGet(1001, out sbyte v) && v == 1, Timeout, Publish),
            "flag set by the client never reached the host");

        // Host kills a boss: the client detects the version change and resyncs.
        host.SetFlag(2002, 1);
        Assert.True(
            await WaitForAsync(() => client.Flags.TryGet(2002, out sbyte v) && v == 1, Timeout, Publish),
            "flag set by the host never reached the client");

        Assert.True(
            await WaitForAsync(() => client.Flags.Version == host.Flags.Version, Timeout, Publish),
            "flag versions never converged");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task JournalEntriesMergeInBothDirections()
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

        // The client advances a quest: the reliable op must reach the host.
        client.SetJournalEntry("k_swg_bastila", 20);
        Assert.True(
            await WaitForAsync(
                () => host.Journal.Snapshot().Any(e => e.Key == "k_swg_bastila" && e.Value == 20),
                Timeout,
                Publish),
            "journal entry set by the client never reached the host");

        // The host advances further: the client resyncs via the version tripwire, and
        // the merge must never lower what the client already has.
        host.SetJournalEntry("k_swg_bastila", 30);
        host.SetJournalEntry("tat_duelling", 10);
        Assert.True(
            await WaitForAsync(
                () => client.Journal.Snapshot().Any(e => e.Key == "k_swg_bastila" && e.Value == 30)
                    && client.Journal.Snapshot().Any(e => e.Key == "tat_duelling" && e.Value == 10),
                Timeout,
                Publish),
            "journal entries set by the host never reached the client");

        Assert.True(
            await WaitForAsync(() => client.Journal.Version == host.Journal.Version, Timeout, Publish),
            "journal versions never converged");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task ThePartyRosterReachesTheClientAsALevel()
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

        // ⚠ BEFORE ANYTHING IS PUBLISHED THE CLIENT MUST REPORT "NO LEVEL", not an empty
        // roster. The two are the same masks, and applying the second would dismiss the
        // player's whole party the moment they joined.
        Assert.False(client.TryGetPartyRoster(out _), "a client with no level claimed to have one");

        // Trask is recruited and walking (slot 8 in the base game's roster numbering — the
        // value is arbitrary here; what matters is that both masks survive intact).
        host.PublishPartyRoster(new PartyRoster(AvailableMask: 0b1_0000_0000, PartyMask: 0b1_0000_0000));
        Assert.True(
            await WaitForAsync(
                () => client.TryGetPartyRoster(out PartyRoster r) && r.IsInParty(8),
                Timeout,
                Publish),
            "the host's roster never reached the client");

        // He is dismissed. This is the direction an EDGE channel would lose: nothing announces
        // a removal, the next level simply stops containing him.
        host.PublishPartyRoster(new PartyRoster(AvailableMask: 0b1_0000_0000, PartyMask: 0));
        Assert.True(
            await WaitForAsync(
                () => client.TryGetPartyRoster(out PartyRoster r) && !r.IsInParty(8) && r.IsAvailable(8),
                Timeout,
                Publish),
            "the dismissal never reached the client");

        // The host is authoritative and never mirrors anyone's roster, in either direction.
        Assert.False(host.TryGetPartyRoster(out _));
        client.PublishPartyRoster(new PartyRoster(0b1_1111_1111, 0b11));
        Assert.False(host.TryGetPartyRoster(out _));

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task AGlobalsCatalogueMismatchDropsWorldStateBothWays()
    {
        // Two players on different saves (or different global-variable mods) must not fuse
        // each other's quest flags. The install digests match here (same exe etc.), so the
        // drop must come entirely from the in-world catalogue exchange.
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
            host.PublishWorldDigest(new WorldDigest(0x1111_1111, 0, "end_m01aa", 0));  // the two catalogues differ
            client.PublishWorldDigest(new WorldDigest(0x2222_2222, 0, "end_m01aa", 0));
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // Both ends must independently narrow the capability set — world state and doors
        // off, phantoms still on.
        Assert.True(
            await WaitForAsync(
                () => (host.Capabilities & SessionCapabilities.WorldState) == 0
                    && (client.Capabilities & SessionCapabilities.WorldState) == 0,
                Timeout,
                Publish),
            "world state was not dropped on a globals-catalogue mismatch");

        Assert.NotEqual(SessionCapabilities.None, host.Capabilities & SessionCapabilities.Phantoms);
        Assert.NotEqual(SessionCapabilities.None, client.Capabilities & SessionCapabilities.Phantoms);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task MatchingCataloguesKeepTheFullCapabilitySet()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
            // Identical catalogues, identical saves, identical module GITs.
            host.PublishWorldDigest(new WorldDigest(0xABCD_1234, 0xBEEF, "end_m01aa", 0xF00D));
            client.PublishWorldDigest(new WorldDigest(0xABCD_1234, 0xBEEF, "end_m01aa", 0xF00D));
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // Give the digests a couple of exchange rounds, then confirm nothing was dropped.
        Assert.True(await WaitForAsync(
            () => client.Compatibility is not null,
            Timeout,
            Publish));

        for (int i = 0; i < 10; i++)
        {
            Publish();
            await Task.Delay(50);
        }

        Assert.Equal(SessionCapabilities.All, host.Capabilities);
        Assert.Equal(SessionCapabilities.All, client.Capabilities);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task ASaveIdentityMismatchDegradesToIdIndependentChannels()
    {
        // Two deliberately different saves, with nobody demanding identical ones: the
        // id-keyed channels (enemies/doors/items/party) must go, while the id-independent
        // ones (phantoms, appearance, equipment, journal) keep the pair playing together.
        using CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan", Port = 0, TickRate = 30,
        });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        try
        {
            PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
            void Publish()
            {
                host.PublishLocalState(state);
                client.PublishLocalState(state);
                host.PublishWorldDigest(new WorldDigest(0xABCD, 0x1111_1111, "end_m01aa", 0));
                client.PublishWorldDigest(new WorldDigest(0xABCD, 0x2222_2222, "end_m01aa", 0));
            }

            Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

            Assert.True(
                await WaitForAsync(
                    () => (host.Capabilities & SessionCapabilities.Enemies) == 0
                        && (client.Capabilities & SessionCapabilities.Enemies) == 0,
                    Timeout,
                    Publish),
                "the id-keyed channels were not dropped on a save-identity mismatch");

            foreach (SessionCapabilities caps in (SessionCapabilities[])[host.Capabilities, client.Capabilities])
            {
                Assert.Equal(SessionCapabilities.None, caps & SessionCapabilities.Doors);
                Assert.Equal(SessionCapabilities.None, caps & SessionCapabilities.GroundItems);
                Assert.Equal(SessionCapabilities.None, caps & SessionCapabilities.Party);
                Assert.NotEqual(SessionCapabilities.None, caps & SessionCapabilities.Phantoms);
                Assert.NotEqual(SessionCapabilities.None, caps & SessionCapabilities.Appearance);
                Assert.NotEqual(SessionCapabilities.None, caps & SessionCapabilities.Equipment);
                Assert.NotEqual(SessionCapabilities.None, caps & SessionCapabilities.Journal);
            }
        }
        finally
        {
            // The compare policy is a process-wide static (set from each session's
            // settings during its handshake); put the default back for the other tests.
            ContentCompatibility.RequireIdenticalSave = false;
        }

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task AStrictHostCarriesOutItsSaveRefusalInsteadOfGoingQuiet()
    {
        // ⚠ THE BUG THIS PINS. A world-time refusal used to be applied by narrowing the
        // peer's capabilities to nothing — and because the session takes the NARROWEST set
        // across all peers, that switched every channel off for everyone while the refused
        // peer stayed connected, forever, never told why. A refusal must be carried out:
        // the peer is dropped and given the reason in the same typed form Hello uses.
        using CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan", Port = 0, TickRate = 30, RequireIdenticalSave = true,
        });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30, RequireIdenticalSave = true },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        try
        {
            PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
            void Publish()
            {
                host.PublishLocalState(state);
                client.PublishLocalState(state);
                host.PublishWorldDigest(new WorldDigest(0xABCD, 0x1111_1111, "end_m01aa", 0));
                client.PublishWorldDigest(new WorldDigest(0xABCD, 0x2222_2222, "end_m01aa", 0));
            }

            Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

            Assert.True(
                await WaitForAsync(
                    () => client.GetStats().Status.Contains("E_SAVE", StringComparison.Ordinal),
                    Timeout,
                    Publish),
                "the strict host never told the client why it was refused");

            Assert.Empty(host.GetRemotePeers());
        }
        finally
        {
            // The compare policy is a process-wide static (set from each session's settings
            // during its handshake); put the default back for the other tests.
            ContentCompatibility.RequireIdenticalSave = false;
        }

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task AModuleGitMismatchDropsIdChannelsThereAndRestoresInTheNextModule()
    {
        // One module's GIT differs (a mod edited its placements): enemies/doors/items go
        // dark THERE, phantoms and chat stay. Walking into a module whose GITs agree must
        // bring the channels back — the mismatch is per-module, not per-session.
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        // Same save, same globals; only tar_m03aa's GIT disagrees.
        WorldDigest hostWorld = new(0xABCD, 0xBEEF, "tar_m03aa", 0x1111);
        WorldDigest clientWorld = new(0xABCD, 0xBEEF, "tar_m03aa", 0x2222);

        PlayerState state = new(Vector3f.Zero, 0f, "tar_m03aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
            host.PublishWorldDigest(hostWorld);
            client.PublishWorldDigest(clientWorld);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        Assert.True(
            await WaitForAsync(
                () => (host.Capabilities & SessionCapabilities.Enemies) == 0
                    && (client.Capabilities & SessionCapabilities.Enemies) == 0,
                Timeout,
                Publish),
            "the modded module's GIT mismatch never dropped the id-keyed channels");

        Assert.Equal(SessionCapabilities.None, host.Capabilities & SessionCapabilities.Doors);
        Assert.Equal(SessionCapabilities.None, client.Capabilities & SessionCapabilities.GroundItems);
        Assert.NotEqual(SessionCapabilities.None, host.Capabilities & SessionCapabilities.Phantoms);
        Assert.NotEqual(SessionCapabilities.None, client.Capabilities & SessionCapabilities.WorldState);

        // Both players move on to a module whose GITs agree.
        hostWorld = hostWorld with { Module = "tar_m04aa", ModuleGit = 0x3333 };
        clientWorld = clientWorld with { Module = "tar_m04aa", ModuleGit = 0x3333 };

        Assert.True(
            await WaitForAsync(
                () => (host.Capabilities & SessionCapabilities.Enemies) != 0
                    && (client.Capabilities & SessionCapabilities.Enemies) != 0,
                Timeout,
                Publish),
            "the id-keyed channels were not restored in the next (agreeing) module");

        Assert.Equal(SessionCapabilities.All, host.Capabilities);
        Assert.Equal(SessionCapabilities.All, client.Capabilities);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task ATransientSaveIdentityMismatchNeverDropsCombat()
    {
        // Round 53's live crossing (docs/research/shared-state-audit.md §2): a
        // [W_SAVE]-style disagreement flashed for a moment right at an ordinary module
        // transition — one side's save-identity digest updates before the other's, exactly
        // like a real crossing — and must never be honored long enough to silence enemies.
        // SaveIdentitySettleTests exercises the debounce itself with an injected clock; this
        // is the same shape end to end, through real sockets and both real session objects.
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        WorldDigest agreeing = new(0xABCD, 0xBEEF, "end_m01aa", 0);
        WorldDigest hostCrossed = new(0xABCD, 0x1111_1111, "tar_m10aa", 0);
        WorldDigest clientNotYetCrossed = new(0xABCD, 0x2222_2222, "end_m01aa", 0);

        void PublishAgreeing()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
            host.PublishWorldDigest(agreeing);
            client.PublishWorldDigest(agreeing);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, PublishAgreeing));
        Assert.True(
            await WaitForAsync(
                () => (host.Capabilities & SessionCapabilities.Enemies) != 0
                    && (client.Capabilities & SessionCapabilities.Enemies) != 0,
                Timeout,
                PublishAgreeing),
            "the pair never reached a stable, fully-shared baseline before the crossing");

        // The crossing: the host lands in the new module first, the client is still mid-hop
        // one tick behind — a real disagreement by the letter of the check, held for well
        // under the settle window, then resolved.
        DateTime crossingDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1500);
        bool everDropped = false;
        while (DateTime.UtcNow < crossingDeadline)
        {
            host.PublishLocalState(state with { Module = "tar_m10aa" });
            client.PublishLocalState(state);
            host.PublishWorldDigest(hostCrossed);
            client.PublishWorldDigest(clientNotYetCrossed);

            if ((host.Capabilities & SessionCapabilities.Enemies) == 0
                || (client.Capabilities & SessionCapabilities.Enemies) == 0)
            {
                everDropped = true;
            }

            await Task.Delay(20);
        }

        Assert.False(everDropped, "a single-crossing save-identity disagreement dropped enemies before it settled");

        // The client catches up and both sides agree on the new module — a real crossing
        // completing, well inside the settle window.
        WorldDigest clientCrossed = hostCrossed;
        PlayerState clientState = state with { Module = "tar_m10aa" };

        void PublishCrossed()
        {
            host.PublishLocalState(state with { Module = "tar_m10aa" });
            client.PublishLocalState(clientState);
            host.PublishWorldDigest(hostCrossed);
            client.PublishWorldDigest(clientCrossed);

            if ((host.Capabilities & SessionCapabilities.Enemies) == 0
                || (client.Capabilities & SessionCapabilities.Enemies) == 0)
            {
                everDropped = true;
            }
        }

        DateTime settleDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(4);
        while (DateTime.UtcNow < settleDeadline)
        {
            PublishCrossed();
            await Task.Delay(20);
        }

        Assert.False(everDropped, "the crossing was still honored as a mismatch after both sides agreed");
        Assert.NotEqual(SessionCapabilities.None, host.Capabilities & SessionCapabilities.Enemies);
        Assert.NotEqual(SessionCapabilities.None, client.Capabilities & SessionCapabilities.Enemies);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task TheHostsPartyReachesTheClient()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        List<PartyMemberState> party =
        [
            new PartyMemberState(0, new Vector3f(5f, 6f, -1f), 0.5f, 0x2712, 20),
        ];

        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
            host.PublishParty(party);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        Assert.True(
            await WaitForAsync(
                () => client.GetPartyMembers().Any(m => m.SlotIndex == 0 && m.Position.X == 5f),
                Timeout,
                Publish),
            "the host's party never reached the client");

        // A client is never authoritative for the party, so its own publish is dropped.
        client.PublishParty([new PartyMemberState(7, Vector3f.Zero, 0f, 0, 0)]);
        Assert.DoesNotContain(host.GetPartyMembers(), m => m.SlotIndex == 7);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// Roadmap item 12 (docs/research/companion-state-sync.md): a companion's real equipped
    /// gear rides <see cref="PartyMemberState"/>'s own equip fields alongside its position, so
    /// this only needs to prove the wire carries them — the SAME channel
    /// <see cref="TheHostsPartyReachesTheClient"/> already proves for position/health.
    /// </summary>
    [Fact]
    public async Task CompanionEquipmentRidesThePartySnapshotHostToClient()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        List<PartyMemberState> party =
        [
            new PartyMemberState(
                0, new Vector3f(5f, 6f, -1f), 0.5f, 0x2712, 20,
                EquippedBody: "g_a_clothes01", EquippedRightWeapon: "g_w_dblsbr01", EquippedLeftWeapon: ""),
        ];

        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
            host.PublishParty(party);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        Assert.True(
            await WaitForAsync(
                () => client.GetPartyMembers().Any(
                    m => m.SlotIndex == 0 && m.EquippedRightWeapon == "g_w_dblsbr01"),
                Timeout,
                Publish),
            "the companion's equipped gear never reached the client");

        PartyMemberState mirrored = client.GetPartyMembers().First(m => m.SlotIndex == 0);
        Assert.Equal("g_a_clothes01", mirrored.EquippedBody);
        Assert.Equal(string.Empty, mirrored.EquippedLeftWeapon);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task ClientEnemyDamageReachesTheHost()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        List<EnemyDamageEntry> report = [new(0x14, 12)];

        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
            client.ReportEnemyDamage("end_m01aa", 3, report);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // The host must see the client's damage for its own module...
        Assert.True(
            await WaitForAsync(
                () => host.GetEnemyDamage("end_m01aa")
                    .Any(d => d.ObjectId == 0x14 && d.CumulativeDamage == 12 && d.Generation == 3),
                Timeout,
                Publish),
            "the client's damage report never reached the host");

        // ...and never for a different module, where the same ids name strangers.
        Assert.DoesNotContain(host.GetEnemyDamage("tar_m02aa"), d => d.ObjectId == 0x14);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task TwoClientsSeeEachOtherThroughTheHost()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient malak = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));
        using CoopClient bastila = new(
            new SessionSettings { PlayerName = "Bastila", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task malakTask = malak.RunAsync(cts.Token);
        Task bastilaTask = bastila.RunAsync(cts.Token);

        PlayerState state = new(new Vector3f(5f, 5f, 0f), 0f, "end_m01aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(state);
            malak.PublishLocalState(state);
            bastila.PublishLocalState(state);
        }

        Assert.True(await WaitForAsync(() => malak.IsConnected && bastila.IsConnected, Timeout, Publish));
        Assert.NotEqual(malak.LocalPeerId, bastila.LocalPeerId);

        // The star topology means these two never talk directly; the host relays.
        Assert.True(
            await WaitForAsync(
                () => malak.GetRemotePeers().Any(p => p.PeerId == bastila.LocalPeerId),
                Timeout,
                Publish),
            "clients did not see each other through the relay");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, malakTask, bastilaTask);
    }

    /// <summary>
    /// docs/research/dialogue-consequences.md §3: a CLIENT's conversation grants an item, the
    /// host relays it to every OTHER client, and — the case that would double-give the item if
    /// it went wrong — the reporting client never sees its own report echoed back as something
    /// to apply.
    /// </summary>
    [Fact]
    public async Task ADialogueGrantFromAClientReachesTheOtherClientButNotItself()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient malak = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));
        using CoopClient bastila = new(
            new SessionSettings { PlayerName = "Bastila", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task malakTask = malak.RunAsync(cts.Token);
        Task bastilaTask = bastila.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(state);
            malak.PublishLocalState(state);
            bastila.PublishLocalState(state);
        }

        Assert.True(await WaitForAsync(() => malak.IsConnected && bastila.IsConnected, Timeout, Publish));

        MaterialisedLootItem[] items = [new MaterialisedLootItem(
            "g_w_blstrpstl001", new ItemIdentity(1, 0, 0, 0, ItemIdentity.FlagKnown))];
        malak.PublishDialogueGrant("carth", items);

        // The host's own player needs it too - it never ran malak's HandleReply either.
        List<DialogueGrantPacket> atHost = [];
        Assert.True(
            await WaitForAsync(
                () =>
                {
                    if (atHost.Count == 0)
                    {
                        atHost.AddRange(host.DrainDialogueGrants());
                    }

                    return atHost.Count > 0;
                },
                Timeout,
                Publish),
            "the host never received the relay/local-apply queue entry");

        Assert.All(atHost, grant => Assert.Equal(malak.LocalPeerId, grant.OriginPeerId));
        Assert.Contains(atHost, grant => grant.Items.Any(i => i.Resref == "g_w_blstrpstl001"));

        // Bastila is the other real peer and must receive it through the relay too.
        List<DialogueGrantPacket> atBastila = [];
        Assert.True(
            await WaitForAsync(
                () =>
                {
                    if (atBastila.Count == 0)
                    {
                        atBastila.AddRange(bastila.DrainDialogueGrants());
                    }

                    return atBastila.Count > 0;
                },
                Timeout,
                Publish),
            "bastila never received the relayed grant");

        Assert.All(atBastila, grant => Assert.Equal(malak.LocalPeerId, grant.OriginPeerId));
        Assert.Contains(atBastila, grant => grant.Items.Any(i => i.Resref == "g_w_blstrpstl001"));

        // Malak reported this himself; it must never come back to him as something to apply.
        for (int i = 0; i < 5; i++)
        {
            Publish();
            await Task.Delay(50);
            Assert.Empty(malak.DrainDialogueGrants());
        }

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, malakTask, bastilaTask);
    }

    [Fact]
    public async Task TheDialogueTokenIsHeldByOneOwnerAtATime()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient malak = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));
        using CoopClient bastila = new(
            new SessionSettings { PlayerName = "Bastila", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(cts.Token);
        Task malakTask = malak.RunAsync(cts.Token);
        Task bastilaTask = bastila.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);

        bool malakTalking = false;
        bool bastilaTalking = false;
        void Publish()
        {
            host.PublishLocalState(state);
            malak.PublishLocalState(state);
            bastila.PublishLocalState(state);
            malak.PublishDialogue(malakTalking, "Trask", "");
            bastila.PublishDialogue(bastilaTalking, "Carth", "");
        }

        Assert.True(await WaitForAsync(() => malak.IsConnected && bastila.IsConnected, Timeout, Publish));

        // Malak enters a conversation: the token becomes his everywhere.
        malakTalking = true;
        Assert.True(
            await WaitForAsync(() => host.GetDialogue().IsOwnedBy(malak.LocalPeerId), Timeout, Publish),
            "the host never granted the token to the first speaker");
        Assert.True(
            await WaitForAsync(() => bastila.GetDialogue().IsOwnedBy(malak.LocalPeerId), Timeout, Publish),
            "the other client never saw the token owner");

        // Bastila tries to talk too: she is refused while Malak holds it.
        bastilaTalking = true;
        for (int i = 0; i < 15; i++)
        {
            Publish();
            await Task.Delay(20);
        }

        Assert.True(host.GetDialogue().IsOwnedBy(malak.LocalPeerId), "the token changed hands while still held");

        // Malak leaves: now Bastila (still asking) is granted it.
        malakTalking = false;
        Assert.True(
            await WaitForAsync(() => host.GetDialogue().IsOwnedBy(bastila.LocalPeerId), Timeout, Publish),
            "the token was not handed to the waiting speaker after release");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, malakTask, bastilaTask);
    }

    [Fact]
    public async Task TheDialogueTokenIsReleasedWhenTheOwnerLeaves()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient malak = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource hostCts = new(Timeout + TimeSpan.FromSeconds(20));
        // The client runs on its own token so we can stop it mid-session to model a peer
        // that walked away (or crashed) mid-conversation.
        using CancellationTokenSource malakCts = new();
        Task hostTask = host.RunAsync(hostCts.Token);
        Task malakTask = malak.RunAsync(malakCts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(state);
            malak.PublishLocalState(state);
            malak.PublishDialogue(true, "Trask", "");
        }

        Assert.True(await WaitForAsync(() => malak.IsConnected, Timeout, Publish));
        Assert.True(
            await WaitForAsync(() => host.GetDialogue().IsOwnedBy(malak.LocalPeerId), Timeout, Publish),
            "the token was never granted");

        // The owner goes away; the host must free the token (cleanly via Bye, or via peer
        // timeout), or a crash mid-conversation would wedge everyone else.
        await malakCts.CancelAsync();
        await malakTask;

        Assert.True(
            await WaitForAsync(() => !host.GetDialogue().Active, TimeSpan.FromSeconds(12),
                () => host.PublishLocalState(state)),
            "the token was not released after the owner left");

        await hostCts.CancelAsync();
        await hostTask;
    }

    [Fact]
    public async Task AClientStopsFollowingWhenTheHostVanishesMidConversation()
    {
        // The mirror image of the test above: there, the conversation's OWNER left. Here
        // the HOST dies while owning a conversation, and the client must stop reporting
        // that a peer holds the token — the agent re-publishes that flag every tick, so a
        // stale one leaves the spectator locked out of its own replies forever. Found
        // live: the host was killed mid-conversation and the client still read
        // "remote: active 1" twenty seconds later.
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient malak = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30, PeerTimeoutMs = 1000 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource hostCts = new();
        using CancellationTokenSource malakCts = new(Timeout + TimeSpan.FromSeconds(20));
        Task hostTask = host.RunAsync(hostCts.Token);
        Task malakTask = malak.RunAsync(malakCts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(state);
            malak.PublishLocalState(state);
            host.PublishDialogue(true, "Trask", "");
        }

        Assert.True(await WaitForAsync(() => malak.IsConnected, Timeout, Publish));
        Assert.True(
            await WaitForAsync(
                () => malak.GetDialogue().IsHeldByAnotherThan(malak.LocalPeerId), Timeout, Publish),
            "the client never saw the host's conversation");

        // The host dies without a Bye — the crash case, not the polite one.
        await hostCts.CancelAsync();
        await hostTask;

        Assert.True(
            await WaitForAsync(
                () => !malak.GetDialogue().Active,
                TimeSpan.FromSeconds(12),
                () => malak.PublishLocalState(state)),
            "the client kept following a conversation whose host was gone");

        await malakCts.CancelAsync();
        await malakTask;
    }

    [Fact]
    public async Task ProgressionMirrorsFromHostToClient()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        uint gold = 1000;
        uint xp = 5000;
        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
            host.PublishProgression(gold, xp);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // The client has none until the first snapshot arrives (so it never mirrors zero).
        Assert.True(
            await WaitForAsync(
                () => client.TryGetProgression(out uint g, out uint x) && g == 1000 && x == 5000,
                Timeout,
                Publish),
            "the host's gold/xp never reached the client");

        // A later change propagates as the new absolute truth.
        gold = 1750;
        xp = 6200;
        Assert.True(
            await WaitForAsync(
                () => client.TryGetProgression(out uint g, out uint x) && g == 1750 && x == 6200,
                Timeout,
                Publish),
            "an updated progression total did not propagate");

        // A client is never authoritative: its own publish is dropped, host keeps its truth.
        client.PublishProgression(9u, 9u);
        Assert.False(host.TryGetProgression(out _, out _));

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// Roadmap item 11 (docs/research/companion-state-sync.md): the companion sibling of
    /// <see cref="ProgressionMirrorsFromHostToClient"/> — the host's per-slot snapshot reaches
    /// the client, and a client's own report reaches the host (the merge arithmetic itself is
    /// <see cref="CompanionExperienceMerge"/>'s own unit tests' job, not this one's).
    /// </summary>
    [Fact]
    public async Task CompanionExperienceMirrorsFromHostAndAPeerReportReachesTheHost()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        const ushort mask = 0b1; // slot 0 only
        uint[] hostXp = new uint[ProtocolConstants.PartyRosterSlots];
        hostXp[0] = 3000;

        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
            host.PublishCompanionExperience(mask, hostXp);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        Assert.True(
            await WaitForAsync(
                () => client.TryGetCompanionExperience(out ushort m, out uint[] x)
                    && (m & 1) != 0 && x[0] == 3000,
                Timeout,
                Publish),
            "the host's companion experience never reached the client");

        uint[] clientXp = new uint[ProtocolConstants.PartyRosterSlots];
        clientXp[0] = 5000;

        Assert.True(
            await WaitForAsync(
                () => host.GetCompanionExperienceReports().Any(
                    r => r.PeerId == client.LocalPeerId && r.Xp[0] == 5000),
                Timeout,
                () =>
                {
                    Publish();
                    client.ReportCompanionExperience(mask, clientXp);
                }),
            "the client's companion-experience report never reached the host");

        // A client is never authoritative for the shared truth: its own publish is dropped.
        client.PublishCompanionExperience(0xFF, new uint[ProtocolConstants.PartyRosterSlots]);
        Assert.False(host.TryGetCompanionExperience(out _, out _));

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task ChatRelaysBetweenTwoClientsThroughTheHost()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient malak = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));
        using CoopClient bastila = new(
            new SessionSettings { PlayerName = "Bastila", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(cts.Token);
        Task malakTask = malak.RunAsync(cts.Token);
        Task bastilaTask = bastila.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(state);
            malak.PublishLocalState(state);
            bastila.PublishLocalState(state);
        }

        Assert.True(await WaitForAsync(() => malak.IsConnected && bastila.IsConnected, Timeout, Publish));

        // Malak says something: the host and the OTHER client receive it; Malak does not get
        // his own line back (he echoes it locally).
        malak.SendChat("regroup at the door");

        List<ChatMessage> bastilaHeard = [];
        List<ChatMessage> hostHeard = [];
        List<ChatMessage> malakHeard = [];
        Assert.True(
            await WaitForAsync(
                () =>
                {
                    bastilaHeard.AddRange(bastila.DrainChat());
                    hostHeard.AddRange(host.DrainChat());
                    malakHeard.AddRange(malak.DrainChat());
                    return bastilaHeard.Any(m => m.Text == "regroup at the door")
                        && hostHeard.Any(m => m.Text == "regroup at the door");
                },
                Timeout,
                Publish),
            "the chat line did not reach the host and the other client");

        Assert.All(bastilaHeard, m => Assert.Equal("Malak", m.Name));
        Assert.DoesNotContain(malakHeard, m => m.Text == "regroup at the door");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, malakTask, bastilaTask);
    }

    [Fact]
    public async Task GarbageTrafficDoesNotDisturbTheHost()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // Anything can arrive on a UDP port. None of it may take the session down.
        // The host binds 0.0.0.0, which is not a valid destination, so target loopback.
        IPEndPoint hostTarget = new(IPAddress.Loopback, host.ListenEndPoint.Port);

        using System.Net.Sockets.UdpClient noise = new();
        for (int i = 0; i < 40; i++)
        {
            byte[] junk = new byte[Random.Shared.Next(1, 400)];
            Random.Shared.NextBytes(junk);
            await noise.SendAsync(junk, junk.Length, hostTarget);
        }

        Assert.True(
            await WaitForAsync(
                () => client.GetRemotePeers().Any(p => p.PeerId == CoopHost.HostPeerId),
                Timeout,
                Publish),
            "session did not survive malformed traffic");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// An upgraded, half-spent stack dropped by a CLIENT has to reach the host as that item
    /// and not as a blueprint name — over real sockets, through the reliable item op, and
    /// then back down in the host's authoritative snapshot without the return trip flattening
    /// it. That round trip is the whole reason the store refuses to forget an identity: the
    /// op carries it and the snapshot that follows does not have to.
    /// </summary>
    [Fact]
    public async Task ADroppedItemCarriesItsIdentityToTheHostAndBack()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(new Vector3f(10f, 20f, 0f), 0f, "end_m01aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        ItemIdentity identity = new(
            StackSize: 20,
            Charges: 7,
            MaxCharges: 50,
            Upgrades: 0b101,
            Flags: ItemIdentity.FlagKnown | ItemIdentity.FlagIdentified);

        uint itemId = client.DropItem(
            "g_w_lghtsbr01", "end_m01aa", new Vector3f(11f, 21f, 0f), identity);
        Assert.NotEqual(0u, itemId);

        Assert.True(
            await WaitForAsync(() => host.Items.TryGet(itemId, out _), Timeout, Publish),
            "the host never received the drop");

        host.Items.TryGet(itemId, out GroundItem? atHost);
        Assert.Equal(identity, atHost!.Identity);

        // The host now re-publishes the ground each time a client resyncs. Its snapshot is
        // authoritative about WHICH items exist; it must not walk the identity back off the
        // client's own copy either.
        Assert.True(
            await WaitForAsync(
                () => client.Items.TryGet(itemId, out GroundItem? echoed)
                    && echoed is not null && echoed.Identity.IsKnown,
                Timeout,
                Publish),
            "the client's own copy lost its identity");

        client.Items.TryGet(itemId, out GroundItem? atClient);
        Assert.Equal(identity, atClient!.Identity);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// docs/research/creature-liveness.md: a body dead on the host must converge to dead on a
    /// mirroring client — the wire half of the reconciliation channel. The plugin-side
    /// dispatch (kill through CombatController's ordinary path) cannot run outside the game,
    /// but the state that DRIVES exactly one such dispatch is this accumulated view, keyed by
    /// object id — so "exactly once" is asserted here as "exactly one entry for this id, no
    /// matter how many broadcast cycles or pages it rode".
    /// </summary>
    [Fact]
    public async Task ADeadIdOnTheHostConvergesToDeadOnTheClientExactlyOnce()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        const string Module = "tar_m03aa";
        PlayerState hostState = new(new Vector3f(10f, 20f, 0f), 1f, Module, 100, 0);
        PlayerState clientState = new(new Vector3f(12f, 22f, 0f), 2f, Module, 90, 0);

        CreatureLivenessEntry[] entries =
        [
            new CreatureLivenessEntry(0x14, CreatureLivenessState.Alive),
            new CreatureLivenessEntry(0x35, CreatureLivenessState.Dead),
        ];

        void Publish()
        {
            host.PublishLocalState(hostState);
            client.PublishLocalState(clientState);
            host.PublishCreatureLiveness(Module, entries);
        }

        bool converged = await WaitForAsync(
            () => client.GetCreatureLiveness(Module)
                .Any(e => e.ObjectId == 0x35 && e.State == CreatureLivenessState.Dead),
            Timeout,
            Publish);
        Assert.True(converged, "the client never learned 0x35 was dead");

        Assert.Single(client.GetCreatureLiveness(Module), e => e.ObjectId == 0x35);
        Assert.Contains(
            client.GetCreatureLiveness(Module),
            e => e.ObjectId == 0x14 && e.State == CreatureLivenessState.Alive);

        // A second identical cycle must not duplicate the entry — the dictionary is keyed by
        // object id, so re-publishing the same state converges to the same single entry.
        host.PublishCreatureLiveness(Module, entries);
        await Task.Delay(500);
        Publish();
        await Task.Delay(500);
        Assert.Single(client.GetCreatureLiveness(Module), e => e.ObjectId == 0x35);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// The transition-window rule (docs/research/transition-window-races.md): a snapshot
    /// taken in one module is not stale in another, it is WRONG — the same object ids name
    /// different creatures there. Proves the packet genuinely reaches the client (so an empty
    /// result cannot be mistaken for "never arrived") AND that querying under the client's own,
    /// different module correctly withholds it.
    /// </summary>
    [Fact]
    public async Task LivenessIsWithheldWhenQueriedUnderADifferentModuleFromTheOnesPublished()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        const string HostModule = "tar_m03aa";
        const string ClientModule = "end_m01aa";
        PlayerState hostState = new(new Vector3f(300f, 200f, 8f), 1f, HostModule, 100, 0);
        PlayerState clientState = new(new Vector3f(20f, 22f, -1f), 2f, ClientModule, 90, 0);

        CreatureLivenessEntry[] entries = [new CreatureLivenessEntry(0x35, CreatureLivenessState.Dead)];

        void Publish()
        {
            host.PublishLocalState(hostState);
            client.PublishLocalState(clientState);
            host.PublishCreatureLiveness(HostModule, entries);
        }

        bool arrived = await WaitForAsync(
            () => client.GetCreatureLiveness(HostModule).Any(e => e.ObjectId == 0x35),
            Timeout,
            Publish);
        Assert.True(arrived, "the packet never reached the client at the transport layer");

        // The client's own agent queries under ITS OWN module. The ids in a snapshot taken
        // somewhere else are not stale here, they are wrong — the gate must withhold them.
        Assert.Empty(client.GetCreatureLiveness(ClientModule));

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// PlayerState.FlagAway rides the existing Flags dword (Version 39), so this is a plain
    /// wire round trip rather than a new channel — but it is the one thing a codec-level
    /// round-trip test cannot prove: that RemotePeer, as the OTHER side's session actually
    /// hands it out, carries the bit through real sockets and real packets. See
    /// docs/research/disconnect-reconnect.md.
    /// </summary>
    [Fact]
    public async Task AnAwayPeerIsSeenAsAwayByTheOtherSide()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState hostState = new(new Vector3f(10f, 20f, 0f), 1f, "end_m01aa", 100, 0);
        PlayerState clientAwayState = new(
            new Vector3f(30f, 40f, 0f), 2f, "end_m01aa", 90, 0, Flags: PlayerState.FlagAway);

        void Publish()
        {
            host.PublishLocalState(hostState);
            client.PublishLocalState(clientAwayState);
        }

        bool hostSeesClientAway = await WaitForAsync(
            () => host.GetRemotePeers().Any(p => p.PeerId == 2 && p.State.IsAway),
            Timeout,
            Publish);
        Assert.True(hostSeesClientAway, "the host never saw the client's away flag");

        // Not incidental to IsAway alone: the round trip must not corrupt the rest of the
        // state riding alongside the flag.
        RemotePeer clientAsSeenByHost = host.GetRemotePeers().First(p => p.PeerId == 2);
        Assert.Equal(clientAwayState.Position, clientAsSeenByHost.State.Position);
        Assert.False(clientAsSeenByHost.State.IsInMinigame);

        // Clearing it must round-trip too — a stuck bit would leave a returned player parked
        // forever.
        PlayerState clientBackState = clientAwayState with { Flags = 0 };
        void PublishBack()
        {
            host.PublishLocalState(hostState);
            client.PublishLocalState(clientBackState);
        }

        bool hostSeesClientBack = await WaitForAsync(
            () => host.GetRemotePeers().Any(p => p.PeerId == 2 && !p.State.IsAway),
            Timeout,
            PublishBack);
        Assert.True(hostSeesClientBack, "the host never saw the client's away flag clear");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }
}
