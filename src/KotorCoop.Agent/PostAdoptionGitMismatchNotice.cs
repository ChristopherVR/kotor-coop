namespace KotorCoop.Agent;

/// <summary>
/// Gives one bounded recovery hint when the client has loaded the host's transferred save,
/// but both running peers still resolve different object-id maps in that same module. The
/// saved map is evidence of what was transferred; it is not proof that the already-running
/// host engine reloaded that map, so this rule reports only and never changes compatibility or
/// schedules another rejoin.
/// </summary>
public sealed class PostAdoptionGitMismatchNotice
{
    private const long SettleMs = 5_000;
    private const long ExpiryMs = 60_000;
    private readonly object _gate = new();
    private string _module = string.Empty;
    private long _adoptedAtMs;
    private long _managerGeneration;
    private bool _observedAdoptedModule;
    private bool _armed;

    public void Arm(string module, long nowMs, long managerGeneration)
    {
        lock (_gate)
        {
            _module = module ?? string.Empty;
            _adoptedAtMs = nowMs;
            _managerGeneration = managerGeneration;
            _observedAdoptedModule = false;
            _armed = _module.Length > 0;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _module = string.Empty;
            _adoptedAtMs = 0;
            _managerGeneration = 0;
            _observedAdoptedModule = false;
            _armed = false;
        }
    }

    /// <returns>True once the completed adoption is known to leave a same-module map mismatch.</returns>
    public bool Observe(
        string localModule,
        uint localGit,
        string remoteModule,
        uint remoteGit,
        long managerGeneration,
        long nowMs)
    {
        lock (_gate)
        {
            if (!_armed)
            {
                return false;
            }

            if (managerGeneration != _managerGeneration)
            {
                Reset();
                return false;
            }

            long elapsed = Math.Max(0, nowMs - _adoptedAtMs);
            if (elapsed >= ExpiryMs)
            {
                Reset();
                return false;
            }

            bool bothAtAdoptedModule = string.Equals(localModule, _module, StringComparison.OrdinalIgnoreCase)
                && string.Equals(remoteModule, _module, StringComparison.OrdinalIgnoreCase);
            if (!bothAtAdoptedModule)
            {
                // Once both peers have resolved the adopted area, a later transition means this
                // pending notice no longer describes the world they are comparing.
                if (_observedAdoptedModule)
                {
                    Reset();
                }

                return false;
            }

            _observedAdoptedModule = true;

            if (localGit == 0 || remoteGit == 0)
            {
                return false;
            }

            if (localGit == remoteGit)
            {
                Reset();
                return false;
            }

            if (elapsed < SettleMs)
            {
                return false;
            }

            Reset();
            return true;
        }
    }
}
