using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>Per-agent load-serial baseline for shared party-pool safety. A real load edge is
/// sticky on a client until SessionManager replaces its CoopAgent after adoption; ordinary
/// save-identity divergence has no effect. On a host, only a fresh post-edge local read releases
/// the temporary hold.</summary>
public readonly record struct PartyPoolLoadGateState(
    bool HasLocalSerial,
    uint LocalSerial,
    bool HasRemoteSerial,
    uint RemoteSerial,
    bool ClientHeldAfterLoad,
    bool HasInitialAlignedWorld)
{
    public static PartyPoolLoadGateState Initial => new(false, 0, false, 0, false, false);
}

public static class PartyPoolLoadGateRule
{
    public static (PartyPoolLoadGateState Next, bool HoldClient) ObserveClient(
        PartyPoolLoadGateState current,
        uint localLoadSerial,
        WorldDigest remoteHost,
        bool localWorldReadable)
    {
        bool localEdge = current.HasLocalSerial && current.LocalSerial != localLoadSerial;
        bool remoteKnown = !remoteHost.IsEmpty;
        bool remoteEdge = remoteKnown && current.HasRemoteSerial
            && current.RemoteSerial != remoteHost.LoadSerial;
        bool hostKnownUnavailable = remoteKnown && string.IsNullOrEmpty(remoteHost.Module);
        bool held = current.ClientHeldAfterLoad || localEdge || remoteEdge;

        PartyPoolLoadGateState next = current with
        {
            HasLocalSerial = true,
            LocalSerial = localLoadSerial,
            HasRemoteSerial = current.HasRemoteSerial || remoteKnown,
            RemoteSerial = remoteKnown ? remoteHost.LoadSerial : current.RemoteSerial,
            ClientHeldAfterLoad = held,
        };

        // Shared-pool object IDs are unsafe until this particular agent has established its
        // first same-world baseline. Unlike ordinary save divergence later in play, an empty
        // startup digest cannot safely authorize an initial reconcile from an old snapshot.
        return (next, held || !current.HasInitialAlignedWorld || !localWorldReadable || hostKnownUnavailable);
    }

    public static PartyPoolLoadGateState ObserveInitialWorldAlignment(
        PartyPoolLoadGateState current,
        WorldDigest local,
        WorldDigest remoteHost)
    {
        if (current.HasInitialAlignedWorld
            || string.IsNullOrWhiteSpace(local.Module)
            || string.IsNullOrWhiteSpace(remoteHost.Module)
            || local.SaveIdentity == 0
            || remoteHost.SaveIdentity == 0
            || local.SaveIdentity != remoteHost.SaveIdentity
            || !string.Equals(local.Module, remoteHost.Module, StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        return current with { HasInitialAlignedWorld = true };
    }

    public static (PartyPoolLoadGateState Next, bool HoldHost) ObserveHost(
        PartyPoolLoadGateState current,
        uint localLoadSerial,
        bool postEdgeWorldReady)
    {
        bool edge = current.HasLocalSerial && current.LocalSerial != localLoadSerial;
        bool pending = current.ClientHeldAfterLoad || edge;
        if (pending && postEdgeWorldReady)
        {
            pending = false;
        }

        PartyPoolLoadGateState next = current with
        {
            HasLocalSerial = true,
            LocalSerial = localLoadSerial,
            ClientHeldAfterLoad = pending,
        };
        return (next, pending || !postEdgeWorldReady);
    }

    /// <summary>A fresh peer admission after the host has loaded is released only after its
    /// first digest proves a ready matching module and save. Existing peer connections remain
    /// quarantined until disconnect/re-admission because wire intents carry no load epoch.</summary>
    public static bool PeerMatchesReadyWorld(WorldDigest host, WorldDigest peer) =>
        !string.IsNullOrWhiteSpace(host.Module)
        && string.Equals(host.Module, peer.Module, StringComparison.OrdinalIgnoreCase)
        && host.SaveIdentity != 0
        && peer.SaveIdentity == host.SaveIdentity;
}
