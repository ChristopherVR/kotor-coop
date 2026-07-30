namespace KotorCoop.Protocol;

/// <summary>
/// A session password, reduced to a 32-bit hash for the join handshake. This is a keep-
/// -strangers-out gate for a LAN/direct co-op session, not a security boundary: the hash
/// is replayable and the traffic is otherwise plain. The point is only that a host with a
/// password admits a client that knows it and rejects one that does not — so the hash is
/// deterministic and travels in Hello.
/// </summary>
public static class SessionPassword
{
    /// <summary>Hash of no password — 0, so "no password set" and "empty password" both
    /// mean an open session and always match.</summary>
    public const uint None = 0;

    /// <summary>FNV-1a-32 of the UTF-16 code units, or <see cref="None"/> for empty.</summary>
    public static uint Hash(string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return None;
        }

        uint h = 2166136261;
        foreach (char c in password)
        {
            h = (h ^ (byte)c) * 16777619;
            h = (h ^ (byte)(c >> 8)) * 16777619;
        }

        // Fold 0 back to 1 so a password that happens to hash to 0 is not read as "open".
        return h == None ? 1u : h;
    }
}
