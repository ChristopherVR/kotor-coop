using System.Text;

namespace KotorCoop.Plugin;

/// <summary>Diagnostic outcomes for the mid-module movie relay's receiver half, logged and
/// echoed to the agent via <see cref="SharedBlockView.MidModulePlayStatus"/>.</summary>
internal enum MidModulePlayStatusCode : uint
{
    None = 0,
    Played = 1,
    Deferred = 2,
    Ignored = 3,
    RefusedUnsafe = 4,
    EngineRefused = 5,
    BadName = 6,
}

/// <summary>
/// Both halves of the mid-module movie relay docs/research/trigger-relay.md §6 shipped only
/// the pure DECISION for (<see cref="MovieRelayQueue"/>):
///
/// <list type="bullet">
/// <item><b>Detection</b> (this instance's own script or menu click): poll
///   <see cref="MoviePlayHook"/>, exclude a resref the engine's own transition queue already
///   carries (that channel already announces it — see docs/research/cinematics.md), and
///   publish what is left for the session to relay.</item>
/// <item><b>Receiver</b> (a peer's announcement arrived): feed <see cref="MovieRelayQueue"/>
///   the engine state only the plugin can read (idle / in dialogue / in combat / the
///   transition queue), then act on its verdict — play the movie RIGHT NOW through
///   <see cref="OdysseyMovies.PlayMovieNow"/>, defer it onto this instance's OWN next
///   transition through the SAME guarded write <c>moviewarp queue</c> already uses
///   (<see cref="Odyssey.QueueTransitionMovie"/>, gated by <see cref="MovieStaging.Decide"/>),
///   or ignore it.</item>
/// </list>
///
/// The agent does the CHEAP filtering before ever writing a request into the block — same
/// module as the announcer, a movie name that survived parsing — so this controller never has
/// to resolve "which module is the announcer in" itself; see
/// <c>CoopAgent.PumpMidModuleMovies</c>.
///
/// Kill switch: coop.ini <c>movie_relay=0</c> — default OFF, and as of 2026-08-22 that is no
/// longer "unverified," it is "verified broken": a live pass calling this controller's own
/// <c>PlayNow</c> function directly (through the <c>movieplay</c> test verb) hung the main
/// thread INDEFINITELY for two different real movies (4+ minutes and 60+ seconds, never
/// returned). See <see cref="OdysseyMovies.PlayMovieNow"/>'s own doc and
/// docs/research/cinematics.md §10.8 before flipping this on.
/// </summary>
internal sealed unsafe class MidModuleMovieController(SharedBlockView block)
{
    private readonly bool _enabled = PluginConfig.ReadBool("movie_relay", false);

    /// <summary>The last <see cref="MoviePlayHook.Sequence"/> value this controller has already
    /// turned into a publish attempt (successful or excluded).</summary>
    private uint _lastHookSerial;

    private uint _appliedRequestSerial;
    private bool _offLogged;

    /// <summary>Module change: every resref this controller might still be holding named
    /// something in the OLD world's transition queue, and a stale request would either replay
    /// a movie that no longer means anything or race the new module's own load.</summary>
    public void Invalidate()
    {
        _lastHookSerial = 0;

        // Don't replay whatever request was pending across the transition — the new module
        // may not even be the one the request named itself for.
        _appliedRequestSerial = block.MidModulePlayRequestSerial;

        MoviePlayHook.Invalidate();
    }

    public void OnFrame()
    {
        if (block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        if (!_enabled)
        {
            if (!_offLogged)
            {
                _offLogged = true;
                PluginHost.Log("movie-relay: disabled by coop.ini (movie_relay=0)");
            }

            return;
        }

        HandleCapture();
        HandleRequest();
    }

    // ---- detection: publish what this instance's own script/menu-click played --------

    private void HandleCapture()
    {
        uint hookSerial = MoviePlayHook.Sequence;
        if (hookSerial == _lastHookSerial)
        {
            return;
        }

        _lastHookSerial = hookSerial;

        Span<byte> buffer = stackalloc byte[MoviePlayHook.NameCapacity];
        int length = MoviePlayHook.ReadLatest(buffer, out uint capturedSerial);
        if (length <= 0 || capturedSerial != hookSerial)
        {
            return; // a slot the ring already overwrote before we got to it, or an empty name
        }

        string resRef = Encoding.ASCII.GetString(buffer[..length]);

        if (IsAlreadyQueuedByTransition(resRef))
        {
            PluginHost.Log(
                $"movie-relay: '{resRef}' is already on the transition queue - the cinematic "
                + "channel already announces it, not relaying it twice");
            return;
        }

        block.PublishMidModuleMovie(resRef);
        PluginHost.Log($"movie-relay: captured mid-module PlayMovie('{resRef}') - published for the session");
    }

    /// <summary>
    /// The exclusion docs/research/cinematics.md's design asks for: a resref
    /// <see cref="MoviePlayHook"/> captured is never fired via a DIFFERENT engine function from
    /// the transition queue (see that hook's own header comment), so this is defence in depth
    /// against a coincidence — a script's mid-module movie happening to share a name with one
    /// already queued for the NEXT transition — rather than a case this hook can actually
    /// produce on its own. Gathers the queue's resrefs and hands the actual decision to
    /// <see cref="MovieRelayQueue.IsAlreadyQueued"/> — pure, tested, no engine underneath it.
    /// </summary>
    private static bool IsAlreadyQueuedByTransition(string resRef)
    {
        int queued = Odyssey.QueuedTransitionMovieCount();
        if (queued <= 0)
        {
            return false;
        }

        Span<string> queuedResRefs = new string[queued];
        int count = 0;

        byte* nameBuf = stackalloc byte[32];
        for (int i = 0; i < queued; i++)
        {
            int length = Odyssey.ReadQueuedTransitionMovie(i, nameBuf, 32);
            if (length <= 0)
            {
                continue;
            }

            string stored = Encoding.ASCII.GetString(nameBuf, length);
            if (MovieStaging.TryStripToResRef(stored, out string queuedResRef))
            {
                queuedResRefs[count++] = queuedResRef;
            }
        }

        return MovieRelayQueue.IsAlreadyQueued(resRef, queuedResRefs[..count]);
    }

    // ---- receiver: act on a peer's announcement ---------------------------------------

    private void HandleRequest()
    {
        uint serial = block.MidModulePlayRequestSerial;
        if (serial == 0 || serial == _appliedRequestSerial)
        {
            return;
        }

        _appliedRequestSerial = serial;

        string resRef = ReadFixedAscii(block.MidModulePlayRequestResRef, SharedBlock.MidModuleMovieNameLength);
        if (!MovieStaging.IsMovieName(resRef))
        {
            block.MidModulePlayApplied = serial;
            block.MidModulePlayStatus = (uint)MidModulePlayStatusCode.BadName;
            PluginHost.Log($"movie-relay: refused a malformed request ('{resRef}')");
            return;
        }

        bool isIdle = Odyssey.HasServer && !Odyssey.IsAreaTransitionPending() && !Odyssey.IsMoviePlaying();
        bool inDialogue = Odyssey.IsConversationActive;
        bool inCombat = Odyssey.IsPlayerInCombat;
        int alreadyQueued = Odyssey.QueuedTransitionMovieCount();

        // sameModule/hasMovieName are already true by construction: CoopAgent.PumpMidModuleMovies
        // only ever writes a request for the local module, with a resref that survived parsing.
        MovieRelayAction action = MovieRelayQueue.Decide(
            sameModule: true,
            hasMovieName: true,
            isIdle: isIdle,
            inDialogue: inDialogue,
            inCombat: inCombat,
            alreadyQueued: alreadyQueued);

        switch (action)
        {
            case MovieRelayAction.PlayNow:
                PlayNow(resRef, alreadyQueued);
                break;

            case MovieRelayAction.DeferToNextTransition:
                DeferToNextTransition(resRef, alreadyQueued);
                break;

            default:
                PluginHost.Log($"movie-relay: ignoring '{resRef}' (not for this module, or nothing to act on)");
                block.MidModulePlayStatus = (uint)MidModulePlayStatusCode.Ignored;
                break;
        }

        block.MidModulePlayApplied = serial;
    }

    private void PlayNow(string resRef, int alreadyQueued)
    {
        MovieStagingVerdict verdict = MovieStaging.DecidePlayNow(
            Odyssey.HasClient, Odyssey.IsAreaTransitionPending(), Odyssey.IsMoviePlaying(), alreadyQueued);

        if (verdict != MovieStagingVerdict.Stage)
        {
            block.MidModulePlayStatus = (uint)MidModulePlayStatusCode.RefusedUnsafe;
            PluginHost.Log(
                $"movie-relay: not playing '{resRef}' now - {MovieStaging.Explain(verdict, alreadyQueued)}");
            return;
        }

        byte* buffer = stackalloc byte[MovieStaging.MaxNameLength];
        int length = Math.Min(resRef.Length, MovieStaging.MaxNameLength);
        for (int i = 0; i < length; i++)
        {
            buffer[i] = (byte)resRef[i];
        }

        PluginHost.Log($"movie-relay: playing '{resRef}' now - a peer's mid-module movie");
        bool ok = OdysseyMovies.PlayMovieNow(buffer, length);
        block.MidModulePlayStatus = (uint)(ok ? MidModulePlayStatusCode.Played : MidModulePlayStatusCode.EngineRefused);
    }

    private void DeferToNextTransition(string resRef, int alreadyQueued)
    {
        MovieStagingVerdict verdict = MovieStaging.Decide(
            hasClient: Odyssey.HasClient,
            transitionPending: Odyssey.IsAreaTransitionPending(),
            moviePlaying: Odyssey.IsMoviePlaying(),
            alreadyQueued: alreadyQueued,
            nameCount: 1);

        if (verdict != MovieStagingVerdict.Stage)
        {
            block.MidModulePlayStatus = (uint)MidModulePlayStatusCode.RefusedUnsafe;
            PluginHost.Log($"movie-relay: not deferring '{resRef}' - {MovieStaging.Explain(verdict, alreadyQueued)}");
            return;
        }

        byte* buffer = stackalloc byte[MovieStaging.MaxNameLength];
        int length = Math.Min(resRef.Length, MovieStaging.MaxNameLength);
        for (int i = 0; i < length; i++)
        {
            buffer[i] = (byte)resRef[i];
        }

        bool ok = Odyssey.QueueTransitionMovie(buffer, length);
        block.MidModulePlayStatus = (uint)(ok ? MidModulePlayStatusCode.Deferred : MidModulePlayStatusCode.EngineRefused);
        PluginHost.Log(ok
            ? $"movie-relay: deferred '{resRef}' onto this instance's own next transition"
            : $"movie-relay: the engine REFUSED '{resRef}' onto the transition queue - no such movie in this install");
    }

    private static string ReadFixedAscii(byte* ptr, int capacity)
    {
        int n = 0;
        while (n < capacity && ptr[n] != 0)
        {
            n++;
        }

        return n == 0 ? string.Empty : Encoding.ASCII.GetString(ptr, n);
    }
}
