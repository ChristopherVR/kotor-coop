using System.Reflection;
using KotorCoop.Session;

namespace KotorCoop.Agent;

public sealed partial class CoopCommandDispatcher
{
    private bool _supportReportRunning;
    private readonly Queue<string> _playerHelp = new();
    private long _nextHelpLine;
    private void QueuePlayerHelp(string text)
    {
        foreach (string line in PlayerHelpText.Wrap(text)) _playerHelp.Enqueue(line);
    }
    private void DrainPlayerHelp()
    {
        if (_playerHelp.Count == 0 || Environment.TickCount64 < _nextHelpLine) return;
        Reply(_playerHelp.Dequeue());
        _nextHelpLine = Environment.TickCount64 + 2500;
    }

    private void ConnectionHelpCommand()
    {
        _playerHelp.Clear();
        _nextHelpLine = 0;
        if (manager.Current is CoopHost host)
        {
            QueuePlayerHelp("You are hosting. Friends on your home network can select you in Co-op > Connect.");
            QueuePlayerHelp("For internet play, choose Copy connection address. An address does not prove internet access.");
            QueuePlayerHelp(host.Reachability == ReachabilityVerdict.AdditionalNatSuspected
                ? "Another router may block internet joining. Check upstream forwarding or use a tunnel."
                : "Internet access is unconfirmed. Ask your friend to try the copied address.");
            QueuePlayerHelp("If joining fails, allow hosting in setup. Your router may need forwarding or a tunnel.");
        }
        else
        {
            QueuePlayerHelp(manager.Current is null
                ? "Not connected. Use main-menu Co-op to pick a host or Connect to an address."
                : $"Connection: {PlainLanguage.Reject(manager.Detail)}");
            QueuePlayerHelp("Ask your friend to load a save and choose Host. Both need the same co-op release.");
            QueuePlayerHelp("Then choose Retry last connection. Internet play may need router forwarding or a tunnel.");
        }
        QueuePlayerHelp("Press F9 for Retry, Start/resume hosting, Leave co-op or Create support report.");
        QueuePlayerHelp("While connected, load the save named Co-op. A crash can lose unsaved progress.");
    }

    private void SupportReportCommand()
    {
        if (_supportReportRunning)
        {
            QueuePlayerHelp("Your support report is still being created. Nothing is uploaded.");
            return;
        }
        if (!Directory.Exists(gameDirectory))
        {
            QueuePlayerHelp("Could not find this game's folder. Use KotorCoop.CollectLogs.cmd in the game folder.");
            return;
        }
        _playerHelp.Clear();
        _nextHelpLine = 0;
        _supportReportRunning = true;
        QueuePlayerHelp("Creating a support report in the background. Nothing is uploaded.");
        // File compression cannot stall the network/heartbeat loop. Only the completion
        // callback touches dispatcher state or the game mailbox, on its owning tick.
        _ = Task.Run(() =>
        {
            string message;
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string folder = Directory.Exists(desktop) ? desktop : gameDirectory;
                string name = $"kotorcoop-support-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}.zip";
                string path = Path.Combine(folder, name);
                string version = typeof(LogsCommand).Assembly
                    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? typeof(LogsCommand).Assembly.GetName().Version?.ToString() ?? "unknown";
                int count = LogsCommand.WriteBundle(gameDirectory, path, version);
                message = count > 0 ? $"Support report saved in {(folder == desktop ? "Desktop" : "the game folder")}:\n{name}" : "No co-op logs found. Run the game once and try again.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                message = "Could not write the support report. Check free disk space and folder permissions, then retry.";
            }
            Post(() =>
            {
                _supportReportRunning = false;
                QueuePlayerHelp(message);
                QueuePlayerHelp("Review READ-ME-FIRST.txt before sharing. Chat and names may remain. Nothing uploaded.");
            });
        });
    }
}
