// Staging cheats: every one of them ends in an engine call, so all of them go through the
// plugin's cheat-request channel.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ---- staging cheats --------------------------------------------------------

    /// <summary>Every staging cheat ends in an engine call, so all of them need the plugin.</summary>
    public bool SupportsCheats => _pluginBlock != 0;

    /// <inheritdoc />
    public bool TryRequestCheat(CheatRequest request, out uint serial)
    {
        serial = 0;
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryWrite(_pluginBlock + BlockCheatKind, request.Kind);
        ok &= _memory.TryWrite(_pluginBlock + BlockCheatTarget, request.Target);
        ok &= _memory.TryWrite(_pluginBlock + BlockCheatAmount, request.Amount);
        ok &= _memory.TryWrite(_pluginBlock + BlockCheatX, request.X);
        ok &= _memory.TryWrite(_pluginBlock + BlockCheatY, request.Y);
        ok &= _memory.TryWrite(_pluginBlock + BlockCheatZ, request.Z);
        ok &= _memory.TryWriteAsciiString(_pluginBlock + BlockCheatText, request.Text ?? string.Empty, BlockCheatTextLength);

        // The four item-identity fields (setitem only). Written unconditionally, so a kind
        // that does not use them still leaves them in a defined state rather than whatever
        // the previous request left behind.
        ok &= _memory.TryWrite(_pluginBlock + BlockCheatCharges, request.Charges);
        ok &= _memory.TryWrite(_pluginBlock + BlockCheatMaxCharges, request.MaxCharges);
        ok &= _memory.TryWrite(_pluginBlock + BlockCheatUpgrades, request.Upgrades);
        ok &= _memory.TryWrite(_pluginBlock + BlockCheatItemFlags, request.ItemFlags);

        // Blank the previous answer BEFORE arming the serial, so a caller that polls fast
        // cannot read the last request's detail and believe it belongs to this one.
        ok &= _memory.TryWrite(_pluginBlock + BlockCheatStatus, 0);
        ok &= _memory.TryWriteAsciiString(_pluginBlock + BlockCheatDetail, string.Empty, BlockCheatDetailLength);

        // Serial last, and continued from whatever the block already holds: the plugin
        // outlives the agent, so restarting the count from zero would replay whichever
        // request a previous agent left behind. Same rule as travel.
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockCheatSerial, out uint previous);
        serial = previous + 1;
        ok &= _memory.TryWrite(_pluginBlock + BlockCheatSerial, serial);
        return ok;
    }

    /// <inheritdoc />
    public bool TryReadCheatAnswer(out uint applied, out int status, out string detail)
    {
        applied = 0;
        status = 0;
        detail = string.Empty;

        if (_pluginBlock == 0)
        {
            return false;
        }

        if (!_memory.TryReadUInt32(_pluginBlock + BlockCheatApplied, out applied)
            || !_memory.TryReadInt32(_pluginBlock + BlockCheatStatus, out status))
        {
            return false;
        }

        _memory.TryReadAsciiString(_pluginBlock + BlockCheatDetail, BlockCheatDetailLength, out detail);
        return true;
    }

}
