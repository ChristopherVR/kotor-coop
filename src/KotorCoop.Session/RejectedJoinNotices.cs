using System.Net;

namespace KotorCoop.Session;

/// <summary>
/// What a HOST gets to hear about a join it turned away. Before this, a player on a different
/// co-op build got a typed <c>E_PROTO</c> Reject (<see cref="CoopHost.HandleVersionMismatch"/>)
/// and the host printed nothing at all — docs/status-engineering.md's own "known gap" on that
/// row — so from the host's chair a friend "just couldn't connect" with no clue that the fix
/// was a matching download. The same silence covered a wrong password.
///
/// Packet-free: the host already has the facts at the moment it writes the Reject; this only
/// queues a line for the agent's per-tick announcer to drain (<c>CoopHost.DrainRejectedJoinNotices</c>)
/// through the same stdout/console/overlay trio a <c>W_CFG</c> warning uses. Rate-limited per
/// source ADDRESS (not endpoint — a rejected client keeps re-Helloing from the same socket,
/// and a restarted one picks a new ephemeral port) so a client retrying every second says it
/// once per <see cref="RepeatIntervalMs"/>, not once per Hello.
/// </summary>
public sealed class RejectedJoinNotices
{
    /// <summary>How long one address stays quiet after being announced. Long enough that a
    /// client stuck retrying does not spam the overlay; short enough that a friend who fixes
    /// their install and fails again for a NEW reason is heard again.</summary>
    public const long RepeatIntervalMs = 30_000;

    /// <summary>Bounds on both tables, so a flood of distinct source addresses (a scanner) can
    /// neither grow the rate-limit table without limit nor queue more lines than a screen
    /// could show.</summary>
    public const int MaxTrackedAddresses = 64;
    public const int MaxPendingLines = 16;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, long> _lastAnnouncedMs = [];
    private readonly Queue<string> _addressOrder = new();
    private readonly Queue<string> _pending = new();

    /// <summary>
    /// The pure rate-limit decision: true when <paramref name="address"/> has never been
    /// announced, or was announced at least <paramref name="intervalMs"/> ago — and in either
    /// case records <paramref name="nowMs"/> against it. A second call inside the interval is
    /// false and leaves the recorded time alone, so the window is measured from the last line
    /// actually shown, not from the last attempt.
    /// </summary>
    public static bool ShouldAnnounce(IDictionary<string, long> lastAnnouncedMs, string address, long nowMs, long intervalMs)
    {
        if (lastAnnouncedMs.TryGetValue(address, out long last) && nowMs - last < intervalMs)
        {
            return false;
        }

        lastAnnouncedMs[address] = nowMs;
        return true;
    }

    /// <summary>A Hello carrying a protocol version this host does not speak.</summary>
    public void NoteVersionMismatch(IPEndPoint from, byte peerVersion, byte ourVersion, long nowMs)
    {
        string age = peerVersion < ourVersion ? "an older" : "a newer";
        Note(
            from,
            $"a player at {from.Address} on {age} build tried to join (v{peerVersion} vs your v{ourVersion}) "
            + "- both sides need the same kotor-coop version",
            nowMs);
    }

    /// <summary>A Hello whose password hash did not match this session's.</summary>
    public void NoteWrongPassword(IPEndPoint from, long nowMs) =>
        Note(from, $"a player at {from.Address} tried to join with the wrong password", nowMs);

    private void Note(IPEndPoint from, string line, long nowMs)
    {
        string address = from.Address.ToString();
        lock (_gate)
        {
            if (!ShouldAnnounce(_lastAnnouncedMs, address, nowMs, RepeatIntervalMs))
            {
                return;
            }

            if (!_addressOrder.Contains(address))
            {
                _addressOrder.Enqueue(address);
                while (_addressOrder.Count > MaxTrackedAddresses)
                {
                    _lastAnnouncedMs.Remove(_addressOrder.Dequeue());
                }
            }

            _pending.Enqueue(line);
            while (_pending.Count > MaxPendingLines)
            {
                _pending.Dequeue();
            }
        }
    }

    /// <summary>Every line queued since the last drain, oldest first; empties the queue.</summary>
    public IReadOnlyList<string> Drain()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return [];
            }

            List<string> lines = [.. _pending];
            _pending.Clear();
            return lines;
        }
    }
}
