using System.Net;
using System.Reflection;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// CoopHost and CoopClient each replaced their old switch-on-<see cref="PacketType"/> dispatcher
/// with a <c>Dictionary&lt;PacketType, PacketHandler&gt;</c> built once in the constructor. These
/// tests pin two things reflection can see without touching the wire: every <see cref="PacketType"/>
/// the old switch handled is still registered, and a type neither class handles (a datagram this
/// instance was never meant to receive) is simply absent from the table rather than throwing —
/// the same silent "default: break;" the switch used to fall through to.
/// </summary>
public class PacketHandlerRegistryTests
{
    private static readonly PacketType[] HostHandledTypes =
    [
        PacketType.Hello, PacketType.StateUpdate, PacketType.FlagOp, PacketType.FlagSyncRequest,
        PacketType.ItemOp, PacketType.ItemSyncRequest, PacketType.ContainerLoot,
        PacketType.ContainerLootAck, PacketType.LootSyncRequest, PacketType.MaterialisedLootAck,
        PacketType.JournalOp, PacketType.JournalSyncRequest, PacketType.EnemyDamage,
        PacketType.ProgressionReport, PacketType.CompanionXpReport, PacketType.WorldDigest, PacketType.PhantomDamage,
        PacketType.PeerEffect, PacketType.EncounterTrigger, PacketType.TriggerRelay,
        PacketType.MidModuleMovie, PacketType.DialogueRequest, PacketType.Chat, PacketType.Bark,
        PacketType.TransitionMovies, PacketType.TransitionAnnounce, PacketType.InteractionClaim,
        PacketType.DialogueGrant, PacketType.DialogueGrantAck, PacketType.Ping, PacketType.Bye,
        PacketType.PartyPoolOp, PacketType.PartyPoolAck, PacketType.PartyPoolSyncRequest,
        PacketType.StoreOp, PacketType.StoreAck, PacketType.StoreSyncRequest,
        // v2 (host-authoritative absolute snapshot + client intents, docs/research/
        // shared-inventory-v2.md): the host only ever RECEIVES an intent (never a snapshot or
        // an ack — it originates both of those itself).
        PacketType.PartyPoolIntentV2, PacketType.StoreIntentV2,
        PacketType.None,
    ];

    private static readonly PacketType[] ClientHandledTypes =
    [
        PacketType.Welcome, PacketType.Reject, PacketType.WorldSnapshot, PacketType.FlagAck,
        PacketType.FlagSnapshot, PacketType.ItemAck, PacketType.JournalAck,
        PacketType.ContainerLootAck, PacketType.DialogueGrant, PacketType.DialogueGrantAck,
        PacketType.JournalSnapshot, PacketType.EnemySnapshot, PacketType.WorldDigest,
        PacketType.PartySnapshot, PacketType.PartyRoster, PacketType.DialogueSnapshot,
        PacketType.TriggerRelay, PacketType.PhantomDamage, PacketType.PeerEffect, PacketType.Bark,
        PacketType.TransitionMovies, PacketType.TransitionAnnounce, PacketType.MidModuleMovie,
        PacketType.InteractionLock, PacketType.LockResponse, PacketType.Chat, PacketType.ProgressionSnapshot,
        PacketType.CompanionXpSnapshot,
        PacketType.ItemSnapshot, PacketType.ContainerLoot, PacketType.MaterialisedLoot,
        PacketType.CreatureLiveness, PacketType.Pong,
        PacketType.PartyPoolOp, PacketType.PartyPoolAck,
        PacketType.StoreOp, PacketType.StoreAck,
        // v2 (host-authoritative absolute snapshot + client intents, docs/research/
        // shared-inventory-v2.md): a client only ever RECEIVES a snapshot or an ack (never an
        // intent — it originates those itself, and the host never relays one onward).
        PacketType.PartyPoolSnapshotV2, PacketType.PartyPoolIntentAckV2,
        PacketType.StoreSnapshotV2, PacketType.StoreIntentAckV2,
        // host -> client only: a clean-shutdown announcement (PacketType.HostBye's own doc
        // comment). The host never receives one, so HostHandledTypes above is unchanged.
        PacketType.HostBye,
        PacketType.None,
    ];

    private static IReadOnlyDictionary<PacketType, object> ReadHandlers(object session)
    {
        FieldInfo? field = session.GetType().GetField(
            "_handlers", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);

        object? value = field!.GetValue(session);
        Assert.NotNull(value);

        // The dictionary's value type is a private nested delegate, so we can only see it as
        // System.Collections through the non-generic IDictionary — enough to check keys.
        var dict = (System.Collections.IDictionary)value!;
        Dictionary<PacketType, object> copy = [];
        foreach (System.Collections.DictionaryEntry entry in dict)
        {
            copy[(PacketType)entry.Key] = entry.Value!;
        }

        return copy;
    }

    [Fact]
    public void HostRegistersExactlyTheTypesItsOldSwitchHandled()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });

        IReadOnlyDictionary<PacketType, object> handlers = ReadHandlers(host);

        foreach (PacketType type in HostHandledTypes)
        {
            Assert.True(handlers.ContainsKey(type), $"CoopHost no longer registers {type}");
        }

        Assert.Equal(HostHandledTypes.Length, handlers.Count);
    }

    [Fact]
    public void ClientRegistersExactlyTheTypesItsOldSwitchHandled()
    {
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", Port = 0, TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, 1));

        IReadOnlyDictionary<PacketType, object> handlers = ReadHandlers(client);

        foreach (PacketType type in ClientHandledTypes)
        {
            Assert.True(handlers.ContainsKey(type), $"CoopClient no longer registers {type}");
        }

        Assert.Equal(ClientHandledTypes.Length, handlers.Count);
    }

    [Fact]
    public void HostIgnoresAPacketTypeItNeverHandled()
    {
        // Welcome is a client-received packet; the host's old switch fell through its
        // 'default' arm for it. The registry must reproduce that silent no-op.
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });

        IReadOnlyDictionary<PacketType, object> handlers = ReadHandlers(host);

        Assert.False(handlers.ContainsKey(PacketType.Welcome));
    }

    [Fact]
    public void ClientIgnoresAPacketTypeItNeverHandled()
    {
        // Hello is a host-received packet; the client's old switch fell through its
        // 'default' arm for it. The registry must reproduce that silent no-op.
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", Port = 0, TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, 1));

        IReadOnlyDictionary<PacketType, object> handlers = ReadHandlers(client);

        Assert.False(handlers.ContainsKey(PacketType.Hello));
    }
}
