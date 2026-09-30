namespace KotorCoop.Protocol;

// Peer-dealt damage: cumulative damage against a host-owned enemy, and cumulative
// damage landed on a peer's phantom.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, EnemyDamagePacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.EnemyDamage);
        w.WriteUInt32(packet.PeerId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteUInt32(packet.Generation);
        w.WriteUInt32(packet.Incarnation);

        int count = Math.Min(packet.Damage.Count, ProtocolConstants.MaxEnemyDamageEntries);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            EnemyDamageEntry entry = packet.Damage[i];
            w.WriteUInt32(entry.ObjectId);
            w.WriteUInt32(entry.CumulativeDamage);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static EnemyDamagePacket? ReadEnemyDamage(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.EnemyDamage)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        string module = r.ReadString();
        uint generation = r.ReadUInt32();
        uint incarnation = r.ReadUInt32();
        byte count = r.ReadUInt8();

        if (r.Failed || count > ProtocolConstants.MaxEnemyDamageEntries)
        {
            return null;
        }

        List<EnemyDamageEntry> damage = new(count);
        for (int i = 0; i < count; i++)
        {
            uint objectId = r.ReadUInt32();
            uint cumulative = r.ReadUInt32();

            if (r.Failed)
            {
                return null;
            }

            damage.Add(new EnemyDamageEntry(objectId, cumulative));
        }

        return new EnemyDamagePacket(peerId, module, generation, damage, incarnation);
    }

    public static int Write(Span<byte> buffer, PhantomDamagePacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PhantomDamage);
        w.WriteUInt32(packet.FromPeerId);
        w.WriteUInt32(packet.TargetPeerId);
        w.WriteUInt32(packet.CumulativeDamage);
        w.WriteUInt32(packet.AttackerId);

        // Appended (protocol version 48): the per-attacker breakdown behind the scalar total
        // above. Capped rather than truncated silently out of range — see ReadPhantomDamage.
        int attackerCount = Math.Min(packet.Attackers.Count, ProtocolConstants.MaxPhantomAttackerEntries);
        w.WriteUInt8((byte)attackerCount);

        for (int i = 0; i < attackerCount; i++)
        {
            PhantomAttackerEntry entry = packet.Attackers[i];
            w.WriteUInt32(entry.AttackerId);
            w.WriteUInt32(entry.CumulativeDamage);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static PhantomDamagePacket? ReadPhantomDamage(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PhantomDamage)
        {
            return null;
        }

        uint from = r.ReadUInt32();
        uint target = r.ReadUInt32();
        uint cumulative = r.ReadUInt32();
        uint attacker = r.ReadUInt32();
        byte attackerCount = r.ReadUInt8();

        if (r.Failed || attackerCount > ProtocolConstants.MaxPhantomAttackerEntries)
        {
            return null;
        }

        List<PhantomAttackerEntry> attackers = new(attackerCount);
        for (int i = 0; i < attackerCount; i++)
        {
            uint id = r.ReadUInt32();
            uint entryCumulative = r.ReadUInt32();

            if (r.Failed)
            {
                return null;
            }

            attackers.Add(new PhantomAttackerEntry(id, entryCumulative));
        }

        return r.Failed ? null : new PhantomDamagePacket(from, target, cumulative, attacker, attackers);
    }
}
