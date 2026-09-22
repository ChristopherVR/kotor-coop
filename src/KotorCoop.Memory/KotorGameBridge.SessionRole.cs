using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    private const int BlockLocalSessionRole = 0x4E44;
    /// <inheritdoc />
    public void SetLocalSessionRole(bool sessionActive, bool isHost)
    {
        if (_pluginBlock == 0)
        {
            return;
        }

        uint role = LocalSessionRoleRule.Encode(sessionActive, isHost);
        _memory.TryWrite(_pluginBlock + BlockLocalSessionRole, role);
    }
}
