namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // Agent-published local session role, version 73. The host-party-roster valid bit is not
    // a role signal: it is capability- and first-snapshot-dependent, so false cannot prove
    // that this process is the host. This word is instead written atomically every agent
    // dispatcher tick, and cleared by ClearCoopState on disconnect.
    public const int OffsetLocalSessionRole = 0x4E44;

    public const uint LocalSessionRoleValid = 1u << 0;
    public const uint LocalSessionRoleHost = 1u << 1;
}
