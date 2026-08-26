using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — The three cinematic-relay channels — transition movies, the faster transition-
/// announce (where a follower should go), and mid-module PlayMovie calls. Same
/// shape and reliability throughout: a lost announcement costs a cinematic, never
/// consistency.
/// </summary>
public sealed partial class CoopClient
{
    // ---- transition cinematics -----------------------------------------------------
    //
    // Same shape as barks, same reliability, and the same reason: a lost announcement costs
    // a cinematic, never consistency. Arrival in the module is the rendezvous and the travel
    // channel already guarantees it.

    private readonly Queue<TransitionMoviePacket> _movieOutbox = new();
    private readonly Queue<TransitionMoviePacket> _movieInbox = new();

    public void PublishTransitionMovies(string module, uint serial, string[] movies)
    {
        lock (_gate)
        {
            _movieOutbox.Enqueue(new TransitionMoviePacket(_localPeerId, module, serial, movies));
        }
    }

    public IReadOnlyList<TransitionMoviePacket> DrainTransitionMovies()
    {
        lock (_gate)
        {
            if (_movieInbox.Count == 0)
            {
                return [];
            }

            List<TransitionMoviePacket> ready = [];
            while (_movieInbox.Count > 0)
            {
                ready.Add(_movieInbox.Dequeue());
            }

            return ready;
        }
    }

    private void FlushTransitionMovies()
    {
        while (true)
        {
            TransitionMoviePacket movies;
            lock (_gate)
            {
                if (_movieOutbox.Count == 0)
                {
                    return;
                }

                movies = _movieOutbox.Dequeue();
            }

            int length = PacketCodec.Write(_sendBuffer, movies);
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    // ---- transition announce -----------------------------------------------------------
    //
    // Same shape as the cinematic channel above, faster: not the film, but where a follower
    // should go, sent the instant this instance's own engine has committed to a transition.

    private readonly Queue<TransitionAnnouncePacket> _announceOutbox = new();
    private readonly Queue<TransitionAnnouncePacket> _announceInbox = new();

    public void PublishTransitionAnnounce(string module, string waypoint, uint serial)
    {
        lock (_gate)
        {
            _announceOutbox.Enqueue(new TransitionAnnouncePacket(_localPeerId, module, waypoint, serial));
        }
    }

    public IReadOnlyList<TransitionAnnouncePacket> DrainTransitionAnnounce()
    {
        lock (_gate)
        {
            if (_announceInbox.Count == 0)
            {
                return [];
            }

            List<TransitionAnnouncePacket> ready = [];
            while (_announceInbox.Count > 0)
            {
                ready.Add(_announceInbox.Dequeue());
            }

            return ready;
        }
    }

    private void FlushTransitionAnnounce()
    {
        while (true)
        {
            TransitionAnnouncePacket announce;
            lock (_gate)
            {
                if (_announceOutbox.Count == 0)
                {
                    return;
                }

                announce = _announceOutbox.Dequeue();
            }

            int length = PacketCodec.Write(_sendBuffer, announce);
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    // ---- mid-module movie relay ---------------------------------------------------------
    //
    // Same shape as the cinematic channel above, same reliability, and the same reason: a
    // script's own PlayMovie call with no transition involved. A lost announcement costs a
    // cinematic, never consistency.

    private readonly Queue<MidModuleMoviePacket> _midModuleMovieOutbox = new();
    private readonly Queue<MidModuleMoviePacket> _midModuleMovieInbox = new();

    public void PublishMidModuleMovie(string module, uint serial, string movie)
    {
        lock (_gate)
        {
            _midModuleMovieOutbox.Enqueue(new MidModuleMoviePacket(_localPeerId, module, serial, movie));
        }
    }

    public IReadOnlyList<MidModuleMoviePacket> DrainMidModuleMovies()
    {
        lock (_gate)
        {
            if (_midModuleMovieInbox.Count == 0)
            {
                return [];
            }

            List<MidModuleMoviePacket> ready = [];
            while (_midModuleMovieInbox.Count > 0)
            {
                ready.Add(_midModuleMovieInbox.Dequeue());
            }

            return ready;
        }
    }

    private void FlushMidModuleMovies()
    {
        while (true)
        {
            MidModuleMoviePacket movie;
            lock (_gate)
            {
                if (_midModuleMovieOutbox.Count == 0)
                {
                    return;
                }

                movie = _midModuleMovieOutbox.Dequeue();
            }

            int length = PacketCodec.Write(_sendBuffer, movie);
            if (length > 0)
            {
                _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }

    private void HandleTransitionMovies(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadTransitionMovies(span) is { } movies)
        {
            lock (_gate)
            {
                _movieInbox.Enqueue(movies);
            }
        }
    }

    private void HandleTransitionAnnounce(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadTransitionAnnounce(span) is { } announce)
        {
            lock (_gate)
            {
                _announceInbox.Enqueue(announce);
            }
        }
    }

    private void HandleMidModuleMovie(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadMidModuleMovie(span) is { } midModuleMovie)
        {
            lock (_gate)
            {
                _midModuleMovieInbox.Enqueue(midModuleMovie);
            }
        }
    }
}
