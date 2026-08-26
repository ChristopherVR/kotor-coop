namespace KotorCoop.Protocol;

// The host's authoritative combat census, broadcast each tick.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, EnemySnapshotPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.EnemySnapshot);
        w.WriteInt64(packet.ServerTimeMs);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);

        int count = Math.Min(packet.Enemies.Count, ProtocolConstants.MaxEnemies);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            EnemyState enemy = packet.Enemies[i];
            w.WriteUInt32(enemy.ObjectId);
            w.WriteUInt16(enemy.CurrentHealth);
            w.WriteUInt16(enemy.MaxHealth);
            w.WriteBool(enemy.Dead);
            w.WriteVector3(enemy.Position);
            w.WriteSingle(enemy.Facing);
            w.WriteUInt32(enemy.Animation);
            w.WriteUInt32(enemy.OneShotSerial);
            w.WriteUInt16(enemy.OneShotAnimation);
            w.WriteBool(enemy.Plot);
            w.WriteUInt16(enemy.Appearance);
            w.WriteUInt16(enemy.Faction);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static EnemySnapshotPacket? ReadEnemySnapshot(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.EnemySnapshot)
        {
            return null;
        }

        long serverTime = r.ReadInt64();
        string module = r.ReadString();
        byte count = r.ReadUInt8();

        if (r.Failed || count > ProtocolConstants.MaxEnemies)
        {
            return null;
        }

        List<EnemyState> enemies = new(count);
        for (int i = 0; i < count; i++)
        {
            uint objectId = r.ReadUInt32();
            ushort current = r.ReadUInt16();
            ushort max = r.ReadUInt16();
            bool dead = r.ReadBool();
            Vector3f position = r.ReadVector3();
            float facing = r.ReadSingle();
            uint animation = r.ReadUInt32();
            uint oneShotSerial = r.ReadUInt32();
            ushort oneShotAnimation = r.ReadUInt16();
            bool plot = r.ReadBool();
            ushort appearance = r.ReadUInt16();
            ushort faction = r.ReadUInt16();

            if (r.Failed)
            {
                return null;
            }

            enemies.Add(new EnemyState(
                objectId, current, max, dead, position, facing, animation,
                oneShotSerial, oneShotAnimation, plot, appearance, faction));
        }

        return new EnemySnapshotPacket(serverTime, module, enemies);
    }
}
