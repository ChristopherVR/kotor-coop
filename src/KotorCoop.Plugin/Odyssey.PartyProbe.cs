// Read-only view of the SERVER party table the way the engine's own transition code walks
// it (docs/research/transition-crashes.md, 2026-09-03 "the two-companion host warp crash").
//
// CSWPartyTable::PositionParty (0x565B00, called from MovePlayerToArea when the client's
// area message arrives after a module move) loops `[table+0x00]` members, reads the NPC slot
// index from `[table+0x04+4*i]`, resolves it through GetNPCObject (0x564700 - returns
// `[table+0x0C+4*slot]` unchanged whenever it is not OBJECT_INVALID) and then
// CServerExoApp::GetCreatureByGameObjectID (0x4AE770), and calls CSWSObject::SetPosition on
// the result WITHOUT a null test. Any member whose slot id is not a live creature at that
// moment is `SetPosition(NULL)` -> access violation at 0x4AA984. This probe prints exactly
// that walk so a transition log shows what the engine is about to dereference.
using System.Text;

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // CServerExoApp::GetCreatureByGameObjectID(this, objectId) -> CSWSCreature* or NULL. The
    // engine's OWN typed getter, used here deliberately and only for the probe, because it is
    // the call PositionParty makes: the probe must report what the engine sees, not what our
    // generic resolver sees. (Odyssey.ResolveServerObject's doc comment explains why nothing
    // else in the plugin resolves through it.)
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, nint> GetCreatureByGameObjectIdEngineFn =
        (delegate* unmanaged[Thiscall]<nint, uint, nint>)0x004AE770;

    /// <summary>
    /// One line describing the server party table as PositionParty walks it:
    /// <c>count N ctrl C avail 0xMASK members [i:slot S id 0xID creature|object|none] slots [S=0xID ...]</c>
    /// (<c>avail</c> is the nine +0x30 recruited flags as a bit mask, bit = slot).
    /// Empty when there is no server or table. Pure read.
    /// </summary>
    public static string DescribeServerPartyTable()
    {
        nint server = GetServer();
        nint table = GetPartyTable();
        if (server == 0 || table == 0)
        {
            return string.Empty;
        }

        int count = *(int*)(table + PartyMemberCount);
        uint available = 0;
        for (int slot = 0; slot < PartyRosterSlots; slot++)
        {
            if (*(int*)(table + PartyAvailable + (slot * 4)) != 0)
            {
                available |= 1u << slot;
            }
        }

        var text = new StringBuilder(160);
        text.Append("count ").Append(count)
            .Append(" ctrl ").Append(*(int*)(table + PartyControlledNpc))
            .Append(" avail 0x").Append(available.ToString("X3"))
            .Append(" members [");
        int walk = Math.Clamp(count, 0, PartyMaxCompanions);
        for (int i = 0; i < walk; i++)
        {
            int slot = *(int*)(table + PartyMemberSlots + (i * 4));
            text.Append(i).Append(":slot ").Append(slot);
            if (slot is >= 0 and < PartyRosterSlots)
            {
                uint id = *(uint*)(table + PartyObjectIds + (slot * 4));
                text.Append(" id 0x").Append(id.ToString("X")).Append(' ').Append(DescribeResolution(server, id));
            }
            else
            {
                text.Append(" (slot out of range)");
            }

            if (i + 1 < walk)
            {
                text.Append(", ");
            }
        }

        text.Append("] slots [");
        bool any = false;
        for (int slot = 0; slot < PartyRosterSlots; slot++)
        {
            uint id = *(uint*)(table + PartyObjectIds + (slot * 4));
            if (id == ObjectInvalid)
            {
                continue;
            }

            if (any)
            {
                text.Append(' ');
            }

            any = true;
            text.Append(slot).Append("=0x").Append(id.ToString("X")).Append(':').Append(DescribeResolution(server, id));
        }

        return text.Append(']').ToString();
    }

    private static string DescribeResolution(nint server, uint id)
    {
        if (id is 0 or ObjectInvalid)
        {
            return "none";
        }

        if (GetCreatureByGameObjectIdEngineFn(server, id) != 0)
        {
            return "creature";
        }

        return ResolveServerObject(id) != 0 ? "OBJECT-NOT-CREATURE" : "UNRESOLVED";
    }
}
