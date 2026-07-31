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
    public async Task ASaveIdentityMismatchDegradesToIdIndependentChannelsWhenAllowed()
    {
        // Two deliberately different saves under --allow-different-save: the id-keyed
        // channels (enemies/doors/items/party) must go, while the id-independent ones
        // (phantoms, appearance, equipment, journal) keep the pair playing together.
        using CoopHost host = new(new SessionSettings
        {
            PlayerName = "Revan", Port = 0, TickRate = 30, AllowDifferentSave = true,
        });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30, AllowDifferentSave = true },
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
            ContentCompatibility.AllowDifferentSave = false;
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

    [Fact]
    public async Task ClientEnemyReportsReachTheHost()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        List<EnemyState> report = [new(0x14, 3, 15, false)];

        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
            client.ReportEnemies("end_m01aa", report);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // The host must see the client's floor for its own module...
        Assert.True(
            await WaitForAsync(
                () => host.GetEnemyReports("end_m01aa").Any(e => e.ObjectId == 0x14 && e.CurrentHealth == 3),
                Timeout,
                Publish),
            "the client's enemy report never reached the host");

        // ...and never for a different module, where the same ids name strangers.
        Assert.DoesNotContain(host.GetEnemyReports("tar_m02aa"), e => e.ObjectId == 0x14);

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
}
