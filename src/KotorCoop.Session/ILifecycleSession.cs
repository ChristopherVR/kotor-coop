// Session run loop and the stats snapshot the dashboard/status command reads.
namespace KotorCoop.Session;

public interface ILifecycleSession
{
    SessionStats GetStats();

    Task RunAsync(CancellationToken cancellationToken);
}
