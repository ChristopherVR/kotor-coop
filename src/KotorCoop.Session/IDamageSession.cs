// Client-dealt enemy damage, phantom damage taken, and non-damage status effects replayed
// between a player and their phantom on other instances. See docs/research/effect-sync.md.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IDamageSession
{
    /// <summary>
    /// Client only: report how much damage this instance has DEALT to each enemy, as a
    /// running cumulative total. Without this a client's fighting is cosmetic — its copies
    /// are pinned at 1 hp and only the owner's engine can actually hurt anything.
    ///
    /// This replaced reporting HEALTH, and the difference matters: health is a level that
    /// has to be sampled, and the owner's mirror wrote its own value back over the client's
    /// copy between samples, so most of a lone player's fight never crossed the wire.
    /// <paramref name="generation"/> changes whenever the sender's totals restart, which is
    /// how the owner tells a reset from a stale datagram.
    /// </summary>
    void ReportEnemyDamage(string module, uint generation, IReadOnlyList<EnemyDamageEntry> damage)
    {
    }

    /// <summary>
    /// Host only: every client's latest damage report for the given module, fresh ones
    /// only, one row per (peer, enemy). The host turns each increase into real damage on
    /// its own copy of that creature.
    /// </summary>
    IReadOnlyList<PeerEnemyDamage> GetEnemyDamage(string module) => [];

    /// <summary>
    /// Tells the peer that owns <paramref name="targetPeerId"/> how much damage their
    /// phantom has taken here in total, so their real character can take it too. Sent every
    /// tick as a running total rather than per hit: the receiver applies the difference, so
    /// a lost datagram costs a tick of staleness and a duplicate costs nothing.
    ///
    /// <paramref name="attackers"/> (protocol version 48) is the same total split per
    /// attacker id, up to <see cref="ProtocolConstants.MaxPhantomAttackerEntries"/> entries —
    /// null (the default) is treated as empty by every implementation, which degrades to the
    /// legacy last-attacker-only wire shape. See docs/research/peer-damage-ledger.md §7.
    /// </summary>
    void PublishPhantomDamage(
        uint targetPeerId, uint cumulativeDamage, uint attackerId,
        IReadOnlyList<PhantomAttackerEntry>? attackers = null)
    {
    }

    /// <summary>
    /// The running total of damage this player owes for hits taken on their phantom in
    /// other instances, and who last landed one. (0, 0) when nobody has hit them.
    /// </summary>
    (uint CumulativeDamage, uint AttackerId) GetIncomingPhantomDamage() => (0, 0);

    /// <summary>
    /// The same running total as <see cref="GetIncomingPhantomDamage"/>, split per attacker
    /// id and merged across every sender that has hit this player's phantom elsewhere — see
    /// <see cref="PhantomAttackerMerge"/>. Empty when nobody has hit them, or from a session
    /// that has not been upgraded to carry the breakdown; the plugin falls back to the legacy
    /// scalar total in that case. See docs/research/peer-damage-ledger.md §7.
    /// </summary>
    IReadOnlyList<PhantomAttackerEntry> GetIncomingPhantomAttackers() => [];

    /// <summary>
    /// Tells the peer that owns <paramref name="targetPeerId"/> that a non-damage effect
    /// (stun, paralyze, knockdown, slow, frightened, sleep, choke, Force push/whirlwind)
    /// landed on their phantom here, so it can be replayed on their real character. The
    /// non-damage sibling of <see cref="PublishPhantomDamage"/> — see
    /// docs/research/effect-sync.md.
    ///
    /// Unlike phantom damage this is a discrete EVENT, not a running total: queued for the
    /// next tick's send and gone once sent, exactly like <see cref="PublishBark"/>.
    /// </summary>
    void PublishPeerEffect(uint targetPeerId, PeerEffectKind kind, uint durationMs, uint magnitude) { }

    /// <summary>Effects a peer's hook captured landing on THIS player's phantom, since the
    /// last call, oldest first. Draining, so each is handed out exactly once — the same
    /// discipline <see cref="DrainBarks"/> uses, and for the same reason: these are discrete
    /// events, not a level to be sampled.</summary>
    IReadOnlyList<PeerEffectPacket> DrainPeerEffects() => [];
}
