using System.Diagnostics;
using KotorCoop.Memory;

namespace KotorCoop.Agent;

/// <summary>Small helpers shared by more than one CLI verb, so they live here rather than
/// being duplicated (or owned by whichever verb happened to need them first).</summary>
internal static class CommandSupport
{
    public static ProcessMemory OpenTarget(AgentOptions options, bool writable)
    {
        if (options.Pid is { } pid)
        {
            return ProcessMemory.Attach(pid, writable);
        }

        string? name = options.ProcessName;

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Specify a target with --pid <n> or --process <name>.");
        }

        if (options.Wait && Process.GetProcessesByName(name).Length == 0)
        {
            Console.WriteLine($"waiting for {name} ...");
            Process? found = GameLauncher.WaitForProcess(name, TimeSpan.FromMinutes(5));

            if (found is null)
            {
                throw new InvalidOperationException($"'{name}' did not start within 5 minutes.");
            }

            found.Dispose();
        }

        return ProcessMemory.AttachByName(name, writable);
    }

    public static bool TryResolveGameDirectory(string path, out string gameDirectory)
    {
        gameDirectory = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? string.Empty;
        return gameDirectory.Length > 0 && Directory.Exists(gameDirectory);
    }
}
