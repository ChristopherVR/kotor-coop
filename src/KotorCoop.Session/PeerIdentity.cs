namespace KotorCoop.Session;

/// <summary>
/// A player's identity on the wire, deliberately separate from where they are on the network.
///
/// The host already keys a peer slot on the client's nonce rather than its address
/// (<c>CoopHost.HandleHello</c> matches <c>p.Nonce == hello.ClientNonce</c> before it falls
/// back to the endpoint), which is what lets a client that rebinds to a new ephemeral port
/// reclaim its slot — same peer id, same name, its phantom never doubled. That mechanism was
/// only ever as durable as the nonce, and the nonce was minted per <see cref="CoopClient"/>
/// instance: a process restart, or the console's own <c>reconnect</c> (which builds a fresh
/// client through <c>SessionManager.Connect</c>), produced a NEW random value and therefore a
/// NEW peer slot, with the old one lingering until it timed out.
///
/// This makes the value survive the process. It is a plain 32-bit number in a file beside the
/// game, minted once at random and read back afterwards, so:
///
/// <list type="bullet">
/// <item>a crash-and-relaunch rejoins as the same player rather than as a stranger;</item>
/// <item>the session has a name for a peer that does not decay when the address does — the
/// prerequisite for anything that routes to a peer other than by its current endpoint (a
/// relay, a rendezvous, a direct channel negotiated after the fact).</item>
/// </list>
///
/// ⚠ It is an IDENTITY, not a CREDENTIAL. Anyone can send any nonce; the password is the
/// stranger-gate. Two installs that share a directory share a file and therefore an identity,
/// which is why <see cref="SessionSettings.PeerIdentity"/> defaults to 0 ("mint a fresh one
/// per session") and only a caller with a real install directory opts in. Two instances on one
/// machine already run from two separate game directories, which is exactly the granularity
/// this keys on.
/// </summary>
public static class PeerIdentity
{
    /// <summary>The file, beside the game executable, next to <c>coop.ini</c>.</summary>
    public const string FileName = "coop.id";

    /// <summary>
    /// The stable identity for an install directory, minting and persisting one on first use.
    ///
    /// Never returns 0 — 0 is the "no stable identity, mint per session" sentinel — and never
    /// throws: a read-only or missing directory falls back to a fresh random value, which is
    /// exactly the behaviour this replaces. A losable identity is a degraded session, not a
    /// broken one.
    /// </summary>
    public static uint ForDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return Mint();
        }

        return ForFile(Path.Combine(directory, FileName));
    }

    /// <summary>
    /// The identity recorded in <paramref name="path"/>, writing a freshly minted one if the
    /// file is absent or unreadable as a value.
    /// </summary>
    public static uint ForFile(string path)
    {
        if (TryRead(path, out uint existing))
        {
            return existing;
        }

        uint minted = Mint();

        try
        {
            string? parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            File.WriteAllText(path, minted.ToString("X8"));
        }
        catch (Exception)
        {
            // A directory we cannot write to costs stability, not the session: the caller
            // gets a value that works for this run and is re-minted on the next one.
        }

        return minted;
    }

    /// <summary>
    /// Reads an identity out of a file. Separate from <see cref="ForFile"/> so the parse can be
    /// tested without a write, and so a caller that must not create files can ask.
    /// </summary>
    public static bool TryRead(string path, out uint identity)
    {
        identity = 0;

        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            // Hex, one line, whitespace-tolerant. Written by us, but read as if a player had
            // edited it — the file is next to coop.ini and looks editable, because it is.
            string text = File.ReadAllText(path).Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                text = text[2..];
            }

            if (uint.TryParse(
                    text,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out uint parsed)
                && parsed != 0)
            {
                identity = parsed;
                return true;
            }
        }
        catch (Exception)
        {
            // Unreadable is the same as absent: mint a new one rather than fail the session.
        }

        return false;
    }

    /// <summary>A fresh identity. Never 0, and never negative when it lands in an int field.</summary>
    public static uint Mint() => (uint)Random.Shared.Next(1, int.MaxValue);
}
