namespace KotorCoop.Protocol;

// The host's authoritative party companions: live presence each tick, and the
// available/in-party roster masks.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, PartySnapshotPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PartySnapshot);
        w.WriteInt64(packet.ServerTimeMs);
        w.WriteUInt32(packet.HostSessionId); w.WriteUInt32(packet.HostLoadSerial); w.WriteUInt32(packet.HostNativeEpoch);

        int count = Math.Min(packet.Members.Count, ProtocolConstants.MaxPartyMembers);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            PartyMemberState m = packet.Members[i];
            w.WriteUInt8(m.SlotIndex);
            w.WriteUInt32(m.OccupantGeneration);
            w.WriteVector3(m.Position);
            w.WriteSingle(m.Facing);
            w.WriteUInt32(m.Animation);
            w.WriteInt32(m.Health);
            w.WriteUInt32(m.OneShotSerial);
            w.WriteUInt16(m.OneShotAnimation);

            // Companion gear (2026-08-27, docs/research/companion-state-sync.md): empty
            // strings when companion_gear_sync is off or the slot is unreadable, same
            // "absent = leave it alone" convention PlayerState's own equip fields use.
            w.WriteString(m.EquippedBody, ProtocolConstants.MaxResrefLength);
            w.WriteString(m.EquippedRightWeapon, ProtocolConstants.MaxResrefLength);
            w.WriteString(m.EquippedLeftWeapon, ProtocolConstants.MaxResrefLength);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static PartySnapshotPacket? ReadPartySnapshot(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PartySnapshot)
        {
            return null;
        }

        long serverTime = r.ReadInt64();
        uint hostSession = r.ReadUInt32(), hostSerial = r.ReadUInt32(), hostEpoch = r.ReadUInt32();
        byte count = r.ReadUInt8();

        if (r.Failed || count > ProtocolConstants.MaxPartyMembers)
        {
            return null;
        }

        List<PartyMemberState> members = new(count);
        for (int i = 0; i < count; i++)
        {
            byte slot = r.ReadUInt8();
            uint occupantGeneration = r.ReadUInt32();
            Vector3f position = r.ReadVector3();
            float facing = r.ReadSingle();
            uint animation = r.ReadUInt32();
            int health = r.ReadInt32();
            uint oneShotSerial = r.ReadUInt32();
            ushort oneShotAnimation = r.ReadUInt16();
            string equippedBody = r.ReadString(ProtocolConstants.MaxResrefLength);
            string equippedRightWeapon = r.ReadString(ProtocolConstants.MaxResrefLength);
            string equippedLeftWeapon = r.ReadString(ProtocolConstants.MaxResrefLength);

            if (r.Failed || slot >= ProtocolConstants.PartyRosterSlots || members.Any(member => member.SlotIndex == slot))
            {
                return null;
            }

            members.Add(new PartyMemberState(
                slot, position, facing, animation, health, oneShotSerial, oneShotAnimation,
                equippedBody, equippedRightWeapon, equippedLeftWeapon, occupantGeneration));
        }

        return new PartySnapshotPacket(serverTime, members, hostSession, hostSerial, hostEpoch);
    }

    public static int Write(Span<byte> buffer, PartyRosterPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PartyRoster);
        w.WriteInt64(packet.ServerTimeMs);
        w.WriteUInt32(packet.Roster.HostSessionId); w.WriteUInt32(packet.Roster.HostLoadSerial); w.WriteUInt32(packet.Roster.HostNativeEpoch);
        w.WriteUInt32(packet.Roster.RefreshSaveIdentity); w.WriteUInt32(packet.Roster.RefreshRevision);
        w.WriteUInt16(packet.Roster.AvailableMask);
        w.WriteUInt16(packet.Roster.PartyMask);

        // docs/research/dialogue-consequences.md, "stale mirrored body": one
        // object id per slot, always the full nine words even though most are 0 — fixed-size
        // and unconditional, the same shape the two masks above already use, so there is no
        // separate "is this field present" bit to get wrong on either side.
        for (int slot = 0; slot < ProtocolConstants.PartyRosterSlots; slot++)
        {
            w.WriteUInt32(packet.Roster.ObjectIdFor(slot));
            PartyOccupantIdentity occupant = packet.Roster.OccupantFor(slot);
            w.WriteUInt32(occupant.Generation);
            w.WriteUInt8(occupant.Replaced ? (byte)1 : (byte)0);
            w.WriteString(occupant.Template, ProtocolConstants.MaxResrefLength);
            w.WriteString(occupant.CacheFingerprint, 64);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static PartyRosterPacket? ReadPartyRoster(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PartyRoster)
        {
            return null;
        }

        long serverTimeMs = r.ReadInt64();
        uint hostSession = r.ReadUInt32(), hostSerial = r.ReadUInt32(), hostEpoch = r.ReadUInt32();
        uint refreshSave = r.ReadUInt32(), refreshRevision = r.ReadUInt32();
        ushort available = r.ReadUInt16();
        ushort party = r.ReadUInt16();

        uint[] objectIds = new uint[ProtocolConstants.PartyRosterSlots];
        PartyOccupantIdentity[] occupants = new PartyOccupantIdentity[ProtocolConstants.PartyRosterSlots];
        for (int slot = 0; slot < ProtocolConstants.PartyRosterSlots; slot++)
        {
            objectIds[slot] = r.ReadUInt32();
            uint occupantGeneration = r.ReadUInt32();
            byte replaced = r.ReadUInt8();
            string template = r.ReadString(ProtocolConstants.MaxResrefLength);
            string fingerprint = r.ReadString(64);
            if (fingerprint.Length != 0 && (fingerprint.Length != 64 || fingerprint.Any(c => !((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F'))))) return null;
            if (replaced > 1 || (template.Length > 0 && !ModSupportProfile.IsResref(template))
                || (occupantGeneration == 0 && (replaced != 0 || template.Length != 0))) return null;
            occupants[slot] = new(occupantGeneration, template, replaced != 0, fingerprint);
        }

        if (r.Failed)
        {
            return null;
        }

        // ⚠ BITS ABOVE THE NINTH TAKE THE WHOLE PACKET DOWN, they are not masked off.
        //
        // A slot index is fed straight to the engine's own AddPartyMember/RemovePartyMember,
        // which validate it (0x00565648: `cmp edi,9 / jge refuse`) — but the reason to refuse
        // HERE is that a tenth bit means the sender is not the build it claims to be, and the
        // nine bits we would keep are then no more trustworthy than the one we threw away.
        // Quietly masking would apply a roster we have evidence against.
        if ((available & ~PartyRoster.SlotMask) != 0 || (party & ~PartyRoster.SlotMask) != 0)
        {
            return null;
        }

        // A companion cannot be in the party without having been recruited; the engine will not
        // produce this and will not accept it (AddMember tests +0x30 at 0x00565651 before it
        // touches anything). Dropping it stops a corrupt level from making a client spend every
        // pass attempting an add its own engine refuses.
        if ((party & ~available) != 0)
        {
            return null;
        }

        return new PartyRosterPacket(serverTimeMs, new PartyRoster(available, party, objectIds, occupants, hostSession, hostSerial, hostEpoch, refreshSave, refreshRevision));
    }
}
