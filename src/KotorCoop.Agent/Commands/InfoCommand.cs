using KotorCoop.Memory;

namespace KotorCoop.Agent;

/// <summary>The `agent info` verb: modules and private-committed memory of the attached process.</summary>
internal static class InfoCommand
{
    public static int Run(AgentOptions options)
    {
        using ProcessMemory memory = CommandSupport.OpenTarget(options, writable: false);

        Console.WriteLine($"pid       {memory.ProcessId}");
        Console.WriteLine($"name      {memory.ProcessName}");
        Console.WriteLine($"bitness   {(memory.Is32Bit ? "32-bit (WOW64)" : "64-bit")}, pointer size {memory.PointerSize}");
        Console.WriteLine();
        Console.WriteLine("modules:");

        foreach (ModuleRegion module in memory.GetModules().Take(40))
        {
            Console.WriteLine($"  {module.Name,-24} base 0x{module.BaseAddress:X8}  size 0x{module.Size:X}");
        }

        long privateBytes = 0;
        int privateRegions = 0;

        foreach (MemoryRegion region in memory.EnumerateRegions(ScanScope.PrivateCommitted))
        {
            privateBytes += region.Size;
            privateRegions++;
        }

        Console.WriteLine();
        Console.WriteLine($"private committed: {privateRegions} regions, {privateBytes / (1024.0 * 1024.0):F1} MB (this is the scan surface)");
        return 0;
    }
}
