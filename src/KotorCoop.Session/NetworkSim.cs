namespace KotorCoop.Session;

/// <summary>Which half of the link a fault applies to, from the point of view of the
/// channel that owns the simulator.</summary>
public enum NetSimDirection
{
    /// <summary>Both halves — a real cable pull.</summary>
    Both = 0,

    /// <summary>Only what this end sends. The peer's traffic still arrives, which is the
    /// asymmetric case a NAT or firewall change produces.</summary>
    Outbound = 1,

    /// <summary>Only what this end receives.</summary>
    Inbound = 2,
}

/// <summary>
/// A fault injector that sits at the transport seam, so every channel in the session — the
/// unreliable avatar stream, the reliable stores, the acks, the handshake — goes through it
/// without any of them knowing.
///
/// WHY THIS EXISTS. The only disconnect ever tested unattended was killing the host AGENT
/// and restarting it, which is a process restart: sockets close, the OS says so, and both
/// ends get a clean edge to react to. A real internet drop looks nothing like that. The
/// sockets stay open, packets simply stop arriving, both ends keep talking into a hole, and
/// then it all comes back — possibly from a different source port. Every bug in this area
/// (a peer rejoining as a stranger, a cumulative total banked twice, a reliable op retired
/// unsent while nobody was listening) lives in that second shape and none of them in the
/// first, so the second shape has to be reproducible on demand.
///
/// OFF BY DEFAULT AND FREE WHEN OFF. Every channel holds one of these permanently; the send
/// and receive paths test one volatile bool and carry on. Nothing is allocated, no timer
/// runs, and the rules are swapped as one immutable record so a console thread changing them
/// mid-flight cannot be observed half-applied by a socket thread.
/// </summary>
public sealed class NetworkSim
{
    /// <summary>
    /// The whole configuration, swapped atomically. A record rather than loose fields so a
    /// reader can never see (say) a new blackout deadline against the old direction.
    /// </summary>
    private sealed record Rules(
        double LossPercent, int LatencyMs, int JitterMs, long BlackoutUntilMs, NetSimDirection Direction,
        int PatternDrops = 0, int PatternWindow = 0, double DuplicatePercent = 0)
    {
        public static readonly Rules None = new(0, 0, 0, 0, NetSimDirection.Both);

        /// <summary>True when nothing is configured and no blackout can still be running.</summary>
        public bool IsIdleAt(long nowMs) =>
            LossPercent <= 0 && LatencyMs <= 0 && JitterMs <= 0 && nowMs >= BlackoutUntilMs
            && PatternDrops <= 0 && DuplicatePercent <= 0;

        public bool Covers(bool outbound) => Direction switch
        {
            NetSimDirection.Outbound => outbound,
            NetSimDirection.Inbound => !outbound,
            _ => true,
        };
    }

    private readonly Lock _gate = new();

    private Rules _rules = Rules.None;

    /// <summary>
    /// The source of randomness for loss/jitter judgements. Defaults to
    /// <see cref="System.Random.Shared"/> — the thread-safe shared instance every production
    /// caller gets exactly as before this field existed. A test that needs a reproducible
    /// sequence (a fixed loss/jitter pattern instead of "probably about right") passes a seeded
    /// <see cref="System.Random"/> to the constructor instead; <see cref="System.Random.Shared"/>
    /// is safe to call from multiple threads at once, a plain seeded instance is not, so an
    /// injected one should only be exercised from a single thread, which is exactly how a test
    /// drives it.
    /// </summary>
    private readonly Random _random;

    public NetworkSim(Random? random = null) => _random = random ?? Random.Shared;

    /// <summary>
    /// The fast-path gate. False means the channel does not even read the rules. Set true by
    /// any configuration change and cleared again the first time a packet is judged after
    /// everything has lapsed, so a finished blackout costs nothing forever after.
    /// </summary>
    private volatile bool _armed;

    private long _droppedOutbound;
    private long _droppedInbound;
    private long _delayed;
    private long _duplicated;

    /// <summary>
    /// Per-direction datagram counters, and the ONLY state <see cref="SetLossPattern"/> needs:
    /// the pattern is a function of "how many datagrams has this direction judged", so it does
    /// not consult <see cref="_random"/> at all and two threads racing the same direction still
    /// get a well-defined, bounded result (each <see cref="Interlocked.Increment(ref long)"/>
    /// hands out a distinct index; only WHICH thread gets which index is unordered, and the
    /// pattern's guarantee — at most <c>drops</c> losses in any run of <c>window</c> — holds
    /// either way).
    /// </summary>
    private long _outboundJudged;
    private long _inboundJudged;

    /// <summary>Whether the channel needs to consult this at all.</summary>
    public bool Armed => _armed;

    public long DroppedOutbound => Interlocked.Read(ref _droppedOutbound);

    public long DroppedInbound => Interlocked.Read(ref _droppedInbound);

    public long DelayedPackets => Interlocked.Read(ref _delayed);

    /// <summary>How many datagrams were sent a second time by <see cref="SetDuplication"/>.</summary>
    public long DuplicatedPackets => Interlocked.Read(ref _duplicated);

    /// <summary>Percentage of packets to discard, 0..100, in whichever direction(s) the
    /// current direction covers.</summary>
    public void SetLoss(double percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        Update(r => r with { LossPercent = percent });
    }

    /// <summary>
    /// DETERMINISTIC loss, as an alternative to <see cref="SetLoss"/>'s coin flip: drop the
    /// first <paramref name="dropsPerWindow"/> datagrams of every <paramref name="windowSize"/>
    /// this end judges, counted per direction.
    ///
    /// ⚠ THIS EXISTS BECAUSE A PERCENTAGE HAS AN UNBOUNDED TAIL AND A TEST CANNOT HAVE ONE.
    /// A 30% coin flip will, eventually, eat every retransmission of one op inside whatever
    /// window a test is willing to wait — rarely, but "rarely" multiplied by every CI run is
    /// a permanently red build nobody trusts (this is exactly what made
    /// <c>HeavyPacketLossStillConverges</c> a known-flaky test). The same nominal loss rate
    /// applied as a PATTERN keeps the property under test — a link that eats three datagrams
    /// in ten, in both directions, must still converge — while capping the worst case: no run
    /// of more than <paramref name="dropsPerWindow"/> consecutive losses is possible in one
    /// direction, so the number of retransmit attempts an op needs is bounded rather than
    /// merely probable.
    ///
    /// Pass 0 drops to go back to whatever <see cref="SetLoss"/> says. The two are exclusive:
    /// while a pattern is set it is the only loss rule consulted, so a test cannot
    /// accidentally stack a coin flip on top of it.
    /// </summary>
    public void SetLossPattern(int dropsPerWindow, int windowSize)
    {
        windowSize = Math.Max(1, windowSize);
        dropsPerWindow = Math.Clamp(dropsPerWindow, 0, windowSize);
        Update(r => r with { PatternDrops = dropsPerWindow, PatternWindow = windowSize });
    }

    /// <summary>
    /// Sends <paramref name="percent"/> of outgoing datagrams twice. A real link duplicates
    /// (a retransmitting NAT, a route flap, a hairpinned broadcast), and every dedup rule in
    /// this codebase — <c>EventFlagStore</c>'s applied-op ring, the loot stores' (container,
    /// item) pairs — exists because of it, so a soak that never duplicates never exercises
    /// them.
    /// </summary>
    public void SetDuplication(double percent) =>
        Update(r => r with { DuplicatePercent = Math.Clamp(percent, 0, 100) });

    /// <summary>
    /// Whether this outgoing datagram should also be sent a second time. Called by
    /// <see cref="UdpChannel.Send"/> AFTER the datagram survived <see cref="DropOutbound"/>:
    /// a duplicate of something the link ate is not a duplicate, it is a delivery.
    /// </summary>
    public bool DuplicateOutbound()
    {
        Rules rules = Volatile.Read(ref _rules);

        if (rules.DuplicatePercent <= 0 || !rules.Covers(outbound: true))
        {
            return false;
        }

        if (_random.NextDouble() * 100.0 >= rules.DuplicatePercent)
        {
            return false;
        }

        Interlocked.Increment(ref _duplicated);
        return true;
    }

    /// <summary>Adds a fixed delay plus up to <paramref name="jitterMs"/> of random extra.
    /// Jitter reorders packets, which is exactly what it does on a real link.
    ///
    /// ⚠ ONLY THE OUTBOUND HALF REORDERS. An outbound delay hands the datagram to a
    /// fire-and-forget task (<see cref="UdpChannel.Send"/>), so two packets held for different
    /// amounts of time genuinely swap places. An INBOUND delay is awaited inside the single
    /// receive loop, so it holds up everything behind it — head-of-line blocking, not
    /// reordering. Simulate reordering with outbound jitter.</summary>
    public void SetLatency(int latencyMs, int jitterMs = 0)
    {
        latencyMs = Math.Clamp(latencyMs, 0, 60_000);
        jitterMs = Math.Clamp(jitterMs, 0, 60_000);
        Update(r => r with { LatencyMs = latencyMs, JitterMs = jitterMs });
    }

    /// <summary>
    /// Drops EVERYTHING for a window — the cable-pull. The deadline is absolute against
    /// <see cref="SessionClock"/>, so it needs no timer and ends on its own even if nothing
    /// is being sent.
    /// </summary>
    public void StartBlackout(int durationMs, NetSimDirection direction = NetSimDirection.Both) =>
        Update(r => r with
        {
            BlackoutUntilMs = SessionClock.NowMs + Math.Max(0, durationMs),
            Direction = direction,
        });

    /// <summary>Ends any blackout early, leaving loss/latency alone.</summary>
    public void EndBlackout() => Update(r => r with { BlackoutUntilMs = 0 });

    /// <summary>Back to a perfect link.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            Volatile.Write(ref _rules, Rules.None);
            _armed = false;
        }
    }

    /// <summary>Judges one outgoing datagram. True means the caller must silently discard it.</summary>
    public bool DropOutbound(long nowMs) => Drop(nowMs, outbound: true);

    /// <summary>Judges one incoming datagram. True means the caller must silently discard it.</summary>
    public bool DropInbound(long nowMs) => Drop(nowMs, outbound: false);

    /// <summary>
    /// How long to hold this datagram before it goes on, 0 for "send it now". Counted here
    /// so the status line can show whether the delay path is actually being exercised.
    /// </summary>
    public int DelayMs()
    {
        Rules rules = Volatile.Read(ref _rules);
        if (rules.LatencyMs <= 0 && rules.JitterMs <= 0)
        {
            return 0;
        }

        int delay = rules.LatencyMs + (rules.JitterMs > 0 ? _random.Next(rules.JitterMs + 1) : 0);
        if (delay > 0)
        {
            Interlocked.Increment(ref _delayed);
        }

        return delay;
    }

    /// <summary>One line describing the current faults, for the console reply.</summary>
    public string Describe()
    {
        Rules rules = Volatile.Read(ref _rules);
        long now = SessionClock.NowMs;

        if (rules.IsIdleAt(now))
        {
            return "netsim off (clean link)";
        }

        List<string> parts = [];

        if (now < rules.BlackoutUntilMs)
        {
            parts.Add($"blackout {(rules.BlackoutUntilMs - now) / 1000.0:F1}s left ({Half(rules.Direction)})");
        }

        if (rules.PatternDrops > 0)
        {
            parts.Add(
                $"loss {rules.PatternDrops}-in-{rules.PatternWindow} fixed pattern ({Half(rules.Direction)})");
        }
        else if (rules.LossPercent > 0)
        {
            parts.Add($"loss {rules.LossPercent:F0}% ({Half(rules.Direction)})");
        }

        if (rules.DuplicatePercent > 0)
        {
            parts.Add($"duplicate {rules.DuplicatePercent:F0}%");
        }

        if (rules.LatencyMs > 0 || rules.JitterMs > 0)
        {
            parts.Add($"latency {rules.LatencyMs}ms +/-{rules.JitterMs}ms");
        }

        return $"netsim {string.Join(", ", parts)} - dropped {DroppedOutbound} out / {DroppedInbound} in";
    }

    private static string Half(NetSimDirection direction) => direction switch
    {
        NetSimDirection.Outbound => "outbound",
        NetSimDirection.Inbound => "inbound",
        _ => "both ways",
    };

    private bool Drop(long nowMs, bool outbound)
    {
        Rules rules = Volatile.Read(ref _rules);

        if (rules.IsIdleAt(nowMs))
        {
            // Everything has lapsed: shut the gate so the hot path stops looking. Done here
            // rather than on a timer precisely because there is no timer.
            _armed = false;
            return false;
        }

        if (!rules.Covers(outbound))
        {
            return false;
        }

        bool drop = nowMs < rules.BlackoutUntilMs || LossRuleDrops(rules, outbound);

        if (!drop)
        {
            return false;
        }

        if (outbound)
        {
            Interlocked.Increment(ref _droppedOutbound);
        }
        else
        {
            Interlocked.Increment(ref _droppedInbound);
        }

        return true;
    }

    /// <summary>
    /// The loss half of <see cref="Drop"/>: the deterministic pattern when one is configured
    /// (it wins outright — see <see cref="SetLossPattern"/>), otherwise the percentage coin
    /// flip. The pattern's counter is advanced ONLY when a pattern is configured, so arming
    /// one mid-session starts from a clean window rather than wherever a percentage happened
    /// to leave things.
    /// </summary>
    private bool LossRuleDrops(Rules rules, bool outbound)
    {
        if (rules.PatternDrops > 0)
        {
            long index = outbound
                ? Interlocked.Increment(ref _outboundJudged) - 1
                : Interlocked.Increment(ref _inboundJudged) - 1;

            return index % rules.PatternWindow < rules.PatternDrops;
        }

        return rules.LossPercent > 0 && _random.NextDouble() * 100.0 < rules.LossPercent;
    }

    /// <summary>Swaps the rules under a lock (writers are rare — a console line) and arms the
    /// fast path. Readers never take the lock.</summary>
    private void Update(Func<Rules, Rules> change)
    {
        lock (_gate)
        {
            Rules updated = change(Volatile.Read(ref _rules));
            Volatile.Write(ref _rules, updated);
            _armed = !updated.IsIdleAt(SessionClock.NowMs);
        }
    }
}
