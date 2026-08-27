using System.Net;
using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Real host, real client, real sockets, real <see cref="CoopAgent"/> pump loop on each side —
/// the full production shape for the companion-equipment channel
/// (docs/research/companion-state-sync.md, roadmap item 12), minus only the actual game
/// process (a <see cref="FakeBridge"/> stands in for <c>KotorGameBridge</c>).
///
/// This exercises the SESSION layer the 2026-08-28 mid-session-re-equip gap runs through —
/// <c>PartyPump.PumpParty</c>'s host-side read/publish, the wire (<c>PartySnapshotPacket</c>'s
/// equip fields), the client-side apply — end to end, and pins that NONE of that layer holds
/// a re-equip back once the bridge reports it (record-equality dirty check, wire codec,
/// <c>EquipApplyDecision</c>-adjacent "should this even be handed to the plugin" gating).
///
/// It does NOT reproduce the actual root cause the live round found — that lives entirely
/// inside <c>KotorGameBridge.ReadEquippedResref</c> (KotorGameBridge.Items.cs), which this
/// fake bridge does not implement at all (a fake IS the replacement for that code, by design;
/// see <c>CompanionEquipResrefCachePrecedenceTests.cs</c> for the test that fails without the
/// real fix and passes with it). This test's job is the complementary one: prove the pipeline
/// ABOVE that bridge has no propagation gap of its own once the bridge does its job — a
/// mid-session change to what the bridge reports must reach the client within a handful of
/// ticks, not eventually.
/// </summary>
public class CompanionGearLoopbackTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private const string Module = "end_m01aa";

    /// <summary>
    /// Stands in for <c>KotorGameBridge</c>'s party-equipment surface only. Host side: reports
    /// whatever <see cref="HostEquippedRightWeapon"/> currently holds — mutated mid-test to
    /// simulate a HOST player re-equipping the companion through the `equip` cheat, the exact
    /// live repro docs/research/companion-state-sync.md's 2026-08-28 round used. Client side:
    /// records every apply so the test can assert what actually reached "the plugin".
    /// </summary>
    private sealed class FakeBridge : IGameBridge
    {
        public string HostEquippedRightWeapon { get; set; } = "g_w_blstrpstl001"; // 16 chars

        public bool? LastApplyEquipment { get; private set; }

        public IReadOnlyList<PartyMemberState> LastAppliedMembers { get; private set; } = [];

        public string Description => "companion-gear-loopback-fake";

        public bool IsAttached => true;

        public bool CanWriteRemotes => true;

        public bool SupportsPartySync => true;

        public bool TryReadLocalState(out PlayerState state)
        {
            state = new PlayerState(Vector3f.Zero, 0f, Module, 20, 0);
            return true;
        }

        public bool TryWriteRemotePeers(IReadOnlyList<RemotePeer> peers) => true;

        public List<PartyMemberState> ReadPartyMembers(bool includeEquipment = false) =>
        [
            new PartyMemberState(
                0, new Vector3f(5f, 6f, -1f), 0.5f, 0x2712, 20,
                EquippedBody: includeEquipment ? "g_a_clothes01" : string.Empty,
                EquippedRightWeapon: includeEquipment ? HostEquippedRightWeapon : string.Empty,
                EquippedLeftWeapon: string.Empty),
        ];

        public int TryWritePartyMembers(IReadOnlyList<PartyMemberState> members) =>
            TryWritePartyMembers(members, applyEquipment: false);

        public int TryWritePartyMembers(IReadOnlyList<PartyMemberState> members, bool applyEquipment)
        {
            LastApplyEquipment = applyEquipment;
            LastAppliedMembers = members;
            return members.Count;
        }

        public void Dispose()
        {
        }
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout, Action pollExtra)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            pollExtra();
            if (condition())
            {
                return true;
            }

            await Task.Delay(15).ConfigureAwait(false);
        }

        return false;
    }

    [Fact]
    public async Task AMidSessionReEquipOnTheHostReachesTheClientWithinAFewTicks()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        FakeBridge hostBridge = new();
        FakeBridge clientBridge = new();

        AgentSettings agentSettings = new() { HeavyPumpIntervalMs = 0, SyncCompanionGear = true };
        CoopAgent hostAgent = new(hostBridge, host, agentSettings);
        CoopAgent clientAgent = new(clientBridge, client, agentSettings);

        using CancellationTokenSource pumpCts = new();
        Task hostPump = Task.Run(async () =>
        {
            while (!pumpCts.IsCancellationRequested)
            {
                hostAgent.PumpOnce();
                await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
            }
        });
        Task clientPump = Task.Run(async () =>
        {
            while (!pumpCts.IsCancellationRequested)
            {
                clientAgent.PumpOnce();
                await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
            }
        });

        try
        {
            Assert.True(
                await WaitForAsync(() => client.IsConnected, Timeout, () => { }),
                "host/client never connected");

            // The gear the companion is already wearing at connect time.
            Assert.True(
                await WaitForAsync(
                    () => clientBridge.LastApplyEquipment == true
                        && clientBridge.LastAppliedMembers.Any(
                            m => m.SlotIndex == 0 && m.EquippedRightWeapon == "g_w_blstrpstl001"),
                    Timeout,
                    () => { }),
                $"the client never received the companion's starting gear; last saw "
                + $"'{clientBridge.LastAppliedMembers.FirstOrDefault().EquippedRightWeapon}'");

            // The HOST re-equips mid-session — docs/research/companion-state-sync.md's exact
            // live repro (`equip 0x127 g_w_vbrdblswd01 4`), modelled here as the bridge's next
            // read simply reporting the new gear, the same shape a real re-equip landing in the
            // engine's own inventory slot would produce.
            hostBridge.HostEquippedRightWeapon = "g_w_vbrdblswd01";

            Assert.True(
                await WaitForAsync(
                    () => clientBridge.LastAppliedMembers.Any(
                        m => m.SlotIndex == 0 && m.EquippedRightWeapon == "g_w_vbrdblswd01"),
                    Timeout,
                    () => { }),
                $"the re-equip never reached the client; last saw "
                + $"'{clientBridge.LastAppliedMembers.FirstOrDefault().EquippedRightWeapon}'");
        }
        finally
        {
            pumpCts.Cancel();
            await Task.WhenAll(hostPump, clientPump);

            await cts.CancelAsync();
            await Task.WhenAll(hostTask, clientTask);
        }
    }
}
