using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session.Channels;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — The three cinematic-relay channels — transition movies, the faster transition-
/// announce (where a follower should go, sent before the load starts), and mid-
/// module PlayMovie calls. Same shape and reliability throughout: any instance's
/// engine can be the one that triggered it, everyone else should see the same film,
/// and a lost announcement costs a cinematic, never consistency.
/// </summary>
public sealed partial class CoopHost
{
    // ---- transition cinematics --------------------------------------------------------
    //
    // Same shape and same reliability as barks: any instance can be the one whose engine
    // reached the trigger, everyone else should see the same film, and a lost announcement
    // costs a cinematic rather than consistency.

    private readonly RelayQueue<TransitionMoviePacket> _movies = new();

    public void PublishTransitionMovies(string module, uint serial, string[] movies)
    {
        TransitionMoviePacket packet = new(LocalPeerId, module, serial, movies);
        lock (_gate)
        {
            _movies.EnqueueOutbox(packet, LocalPeerId);
        }
    }

    public IReadOnlyList<TransitionMoviePacket> DrainTransitionMovies()
    {
        lock (_gate)
        {
            return _movies.DrainInbox();
        }
    }

    private void HandleTransitionMovies(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadTransitionMovies(span) is not { } movies)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (!_peers.ContainsKey(movies.PeerId))
            {
                return; // only a known peer, and it cannot announce for another
            }

            _movies.EnqueueInbox(movies);
            _movies.EnqueueOutbox(movies, movies.PeerId); // everyone but the sender
        }
    }

    private void BroadcastTransitionMovies(List<PeerConnection> recipients)
    {
        List<(TransitionMoviePacket Packet, uint ExceptPeerId)> pending;
        lock (_gate)
        {
            pending = _movies.DrainOutbox();
        }

        if (pending.Count == 0)
        {
            return;
        }

        foreach ((TransitionMoviePacket packet, uint except) in pending)
        {
            int length = PacketCodec.Write(_sendBuffer, packet);
            if (length <= 0)
            {
                continue;
            }

            foreach (PeerConnection peer in recipients)
            {
                if (peer.Id != except)
                {
                    _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
                }
            }
        }
    }

    // ---- transition announce -----------------------------------------------------------
    //
    // The faster, earlier sibling of the cinematic channel above: not the film, but where a
    // follower should go, sent the instant this instance's engine has committed to a
    // transition, before the load starts. Same shape, same reliability, same reason: any
    // instance can be the one whose engine reached the door, everyone else should be able to
    // start following immediately, and a lost announcement costs a smoother handoff, never
    // consistency — the pre-existing host-module-based follow is the fallback.

    private readonly RelayQueue<TransitionAnnouncePacket> _announces = new();

    public void PublishTransitionAnnounce(string module, string waypoint, uint serial)
    {
        TransitionAnnouncePacket packet = new(LocalPeerId, module, waypoint, serial);
        lock (_gate)
        {
            _announces.EnqueueOutbox(packet, LocalPeerId);
        }
    }

    public IReadOnlyList<TransitionAnnouncePacket> DrainTransitionAnnounce()
    {
        lock (_gate)
        {
            return _announces.DrainInbox();
        }
    }

    private void HandleTransitionAnnounce(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadTransitionAnnounce(span) is not { } announce)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (!_peers.ContainsKey(announce.PeerId))
            {
                return; // only a known peer, and it cannot announce for another
            }

            _announces.EnqueueInbox(announce);
            _announces.EnqueueOutbox(announce, announce.PeerId); // everyone but the sender
        }
    }

    private void BroadcastTransitionAnnounce(List<PeerConnection> recipients)
    {
        List<(TransitionAnnouncePacket Packet, uint ExceptPeerId)> pending;
        lock (_gate)
        {
            pending = _announces.DrainOutbox();
        }

        if (pending.Count == 0)
        {
            return;
        }

        foreach ((TransitionAnnouncePacket packet, uint except) in pending)
        {
            int length = PacketCodec.Write(_sendBuffer, packet);
            if (length <= 0)
            {
                continue;
            }

            foreach (PeerConnection peer in recipients)
            {
                if (peer.Id != except)
                {
                    _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
                }
            }
        }
    }

    // ---- mid-module movie relay ---------------------------------------------------------
    //
    // A script's own PlayMovie call with no transition involved — the DETECTION half
    // docs/research/trigger-relay.md §6 shipped without a hook for. Same shape and reliability
    // as the cinematic channel above, and the same reason: any instance's engine can be the
    // one that ran the script, everyone else should see the same film, and a lost announcement
    // costs a cinematic rather than consistency.

    private readonly RelayQueue<MidModuleMoviePacket> _midModuleMovies = new();

    public void PublishMidModuleMovie(string module, uint serial, string movie)
    {
        MidModuleMoviePacket packet = new(LocalPeerId, module, serial, movie);
        lock (_gate)
        {
            _midModuleMovies.EnqueueOutbox(packet, LocalPeerId);
        }
    }

    public IReadOnlyList<MidModuleMoviePacket> DrainMidModuleMovies()
    {
        lock (_gate)
        {
            return _midModuleMovies.DrainInbox();
        }
    }

    private void HandleMidModuleMovie(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadMidModuleMovie(span) is not { } movie)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (!_peers.ContainsKey(movie.PeerId))
            {
                return; // only a known peer, and it cannot announce for another
            }

            _midModuleMovies.EnqueueInbox(movie);
            _midModuleMovies.EnqueueOutbox(movie, movie.PeerId); // everyone but the sender
        }
    }

    private void BroadcastMidModuleMovies(List<PeerConnection> recipients)
    {
        List<(MidModuleMoviePacket Packet, uint ExceptPeerId)> pending;
        lock (_gate)
        {
            pending = _midModuleMovies.DrainOutbox();
        }

        if (pending.Count == 0)
        {
            return;
        }

        foreach ((MidModuleMoviePacket packet, uint except) in pending)
        {
            int length = PacketCodec.Write(_sendBuffer, packet);
            if (length <= 0)
            {
                continue;
            }

            foreach (PeerConnection peer in recipients)
            {
                if (peer.Id != except)
                {
                    _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
                }
            }
        }
    }

    // ---- galaxy-map departures ----------------------------------------------------
    //
    // Same any-to-all relay shape as the mid-module movies above, for the same reason: the
    // leader is whichever player clicked accept (follow_lead decides on the RECEIVING side
    // whether that click is one to follow), and the host's own departure has to reach every
    // client while the host's game thread is frozen inside the leg - which it does, because
    // the plugin published it at the click and this agent process never froze.

    private readonly RelayQueue<GalaxyDeparturePacket> _galaxyDepartures = new();

    public void PublishGalaxyDeparture(string module, uint serial, int planet)
    {
        GalaxyDeparturePacket packet = new(LocalPeerId, module, serial, planet);
        lock (_gate)
        {
            _galaxyDepartures.EnqueueOutbox(packet, LocalPeerId);
        }
    }

    public IReadOnlyList<GalaxyDeparturePacket> DrainGalaxyDepartures()
    {
        lock (_gate)
        {
            return _galaxyDepartures.DrainInbox();
        }
    }

    private void HandleGalaxyDeparture(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadGalaxyDeparture(span) is not { } departure)
        {
            _channel.NoteMalformed();
            return;
        }

        lock (_gate)
        {
            if (!_peers.ContainsKey(departure.PeerId))
            {
                return; // only a known peer, and it cannot announce for another
            }

            _galaxyDepartures.EnqueueInbox(departure);
            _galaxyDepartures.EnqueueOutbox(departure, departure.PeerId); // everyone but the sender
        }
    }

    private void BroadcastGalaxyDepartures(List<PeerConnection> recipients)
    {
        List<(GalaxyDeparturePacket Packet, uint ExceptPeerId)> pending;
        lock (_gate)
        {
            pending = _galaxyDepartures.DrainOutbox();
        }

        if (pending.Count == 0)
        {
            return;
        }

        foreach ((GalaxyDeparturePacket packet, uint except) in pending)
        {
            int length = PacketCodec.Write(_sendBuffer, packet);
            if (length <= 0)
            {
                continue;
            }

            foreach (PeerConnection peer in recipients)
            {
                if (peer.Id != except)
                {
                    _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
                }
            }
        }
    }
}
