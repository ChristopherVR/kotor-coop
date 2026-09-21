using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — this peer's own damage report for companions <c>companion_local_combat</c> has
/// released to fight locally. See CoopHost.CompanionDamage.cs and
/// docs/research/companion-state-sync.md §13.
/// </summary>
public sealed partial class CoopClient
{
    public void ReportCompanionDamage(uint generation, IReadOnlyList<CompanionDamageEntry> damage)
    {
        if (_localPeerId == 0 || damage.Count == 0)
        {
            return;
        }

        // Unreliable and re-sent every tick, like ReportEnemyDamage: safe because the totals
        // are cumulative, so a lost datagram costs one tick of staleness and a duplicate
        // applies nothing extra.
        // Caller's thread, not the tick thread — see CoopClient._apiBuffer.
        SendFromCaller(
            new CompanionDamagePacket(_localPeerId, generation, damage),
            static (buffer, packet) => PacketCodec.Write(buffer, packet));
    }

    /// <summary>Only the host applies a peer's companion damage; a client never does.</summary>
    public IReadOnlyList<PeerCompanionDamage> GetCompanionDamage() => [];
}
