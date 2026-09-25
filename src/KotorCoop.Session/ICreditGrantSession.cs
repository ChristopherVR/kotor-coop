// The credits-reward relay (docs/research/personal-inventory.md §4): "my own conversation just
// handed my local player N credits", relayed reliably so the peer's own purse receives the
// same amount even though credits_mode = personal has otherwise stopped merging gold.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Deliberately a SEPARATE interface from <see cref="IDialogueSession"/> rather than a new
/// parameter on <see cref="IDialogueSession.PublishDialogueGrant"/> — changing that method's
/// signature would ripple into every test fixture that overrides it (including one another
/// pass owns concurrently — see docs/research/personal-inventory.md §4's own remarks). Both
/// members default to a no-op/empty result, so any existing <see cref="ICoopSession"/>
/// implementer (real or a test fake) keeps compiling and behaving exactly as before without
/// touching this interface at all.
/// </summary>
public interface ICreditGrantSession
{
    /// <summary>
    /// Announces that THIS peer's own conversation just handed the local player
    /// <paramref name="amount"/> credits, delivered reliably to every OTHER peer so their
    /// engine's own purse receives the same amount. Symmetric like
    /// <see cref="IDialogueSession.PublishDialogueGrant"/>: <c>CoopHost</c> broadcasts directly;
    /// <c>CoopClient</c> sends to the host, which relays to every other client under this
    /// peer's TRUE (origin, sequence) identity. A no-op for <paramref name="amount"/> &lt;= 0.
    /// </summary>
    void PublishCreditGrant(int amount) { }

    /// <summary>Every credit-grant packet received from ANOTHER peer since the last drain,
    /// oldest first, for the agent to apply directly to the local purse (no cheat-channel round
    /// trip needed — a gold write is already a direct bridge primitive, see
    /// <c>Pumps.CreditGrantPump</c>). A grant whose origin is THIS peer's own id is filtered out
    /// before it ever reaches here, the same as <see cref="IDialogueSession.DrainDialogueGrants"/>.
    /// </summary>
    IReadOnlyList<CreditGrantPacket> DrainCreditGrants() => [];
}
