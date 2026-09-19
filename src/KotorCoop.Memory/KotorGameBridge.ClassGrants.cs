// The agent's half of the class-grant relay (KotorCoop.Plugin's SharedBlock.ClassGrant.cs,
// shared block version 68, region 0x4C20..0x4C38): read the plugin's "a script just added a
// class to my player" edge, file "add this class to your player" requests, and read the ack.
// Same three-part shape as the galaxy-map departure relay in KotorGameBridge.Travel.cs. See
// docs/research/class-and-power-grants.md.
namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    /// <inheritdoc />
    public bool TryReadClassGrant(out uint serial, out int classId)
    {
        serial = 0;
        classId = -1;

        // Serial first, class second - the reverse of the plugin's write order - so a torn read
        // pairs a serial with the class written before it, never a newer one.
        return _pluginBlock != 0
            && _memory.TryReadUInt32(_pluginBlock + BlockClassGrantSerial, out serial)
            && serial != 0
            && _memory.TryReadInt32(_pluginBlock + BlockClassGrantClass, out classId);
    }

    /// <inheritdoc />
    public bool TryRequestClassGrant(int classId)
    {
        if (_pluginBlock == 0 || classId is < 0 or > 8)
        {
            return false;
        }

        bool ok = _memory.TryWrite(_pluginBlock + BlockClassGrantRequestClass, classId);

        // Serial last, so the plugin never observes a half-written request. Continue from
        // whatever the block already holds, the same convention TryRequestGalaxyDepart uses.
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockClassGrantRequestSerial, out uint serial);
        ok &= _memory.TryWrite(_pluginBlock + BlockClassGrantRequestSerial, serial + 1);
        return ok;
    }

    /// <inheritdoc />
    public bool TryReadClassGrantStatus(out uint applied, out uint status)
    {
        applied = 0;
        status = 0;

        return _pluginBlock != 0
            && _memory.TryReadUInt32(_pluginBlock + BlockClassGrantApplied, out applied)
            && _memory.TryReadUInt32(_pluginBlock + BlockClassGrantStatus, out status);
    }
}
