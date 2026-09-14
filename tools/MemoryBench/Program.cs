using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using KotorCoop.Memory;

// Synthetic microbenchmarks, not gameplay frame-time measurements. The scalar search
// and array-backed ASCII reader below preserve the pre-review baseline for comparison.
Console.WriteLine($"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}");
byte[] bytes = new byte[32 * 1024 * 1024];
BytePattern pattern = BytePattern.Parse("60 91 73 00");
foreach (string shape in new[] { "zero-filled", "random", "dense near-miss" })
{
    if (shape == "random") new Random(42).NextBytes(bytes);
    else Array.Fill(bytes, shape == "zero-filled" ? (byte)0 : (byte)0x60);
    bytes[^4] = 0x60;
    bytes[^3] = 0x91;
    bytes[^2] = 0x73;
    bytes[^1] = 0;
    if (ScalarIndex(bytes, pattern) != pattern.IndexIn(bytes)) throw new Exception("Search results differ.");
    double before = Measure(() => ScalarIndex(bytes, pattern), 5);
    double after = Measure(() => pattern.IndexIn(bytes), 5);
    Console.WriteLine($"32 MiB {shape}: scalar {before:F2} ms, vector {after:F2} ms ({before / after:F2}x)");
}

unsafe
{
    byte* field = (byte*)NativeMemory.AllocZeroed(16);
    try
    {
        using ProcessMemory memory = ProcessMemory.Attach(Environment.ProcessId);
        memory.TryWriteAsciiString((nint)field, "end_m01aa", 16);
        Func<int> before = () => OldReadAscii(memory, (nint)field).Length;
        Func<int> after = () => memory.TryReadAsciiString((nint)field, 16, out string value) ? value.Length : -1;
        if (before() != after()) throw new Exception("String results differ.");
        Console.WriteLine($"16-byte ASCII read: array {Allocated(before):F0} B/call, stack {Allocated(after):F0} B/call");
    }
    finally
    {
        NativeMemory.Free(field);
    }
}

static int ScalarIndex(ReadOnlySpan<byte> bytes, BytePattern pattern)
{
    for (int i = 0; i <= bytes.Length - pattern.Length; i++)
    {
        if (bytes[i] == 0x60 && pattern.MatchesAt(bytes, i)) return i;
    }
    return -1;
}

static string OldReadAscii(ProcessMemory memory, nint address)
{
    byte[] buffer = new byte[16];
    if (!memory.TryRead(address, buffer)) throw new Exception("Read failed.");
    int end = Array.IndexOf(buffer, (byte)0);
    if (end < 0) end = buffer.Length;
    for (int i = 0; i < end; i++)
    {
        if (buffer[i] < 0x20 || buffer[i] > 0x7E) return string.Empty;
    }
    return Encoding.ASCII.GetString(buffer, 0, end);
}

static double Measure(Func<int> action, int count)
{
    for (int i = 0; i < 3; i++) action();
    double[] elapsed = new double[count];
    for (int i = 0; i < count; i++)
    {
        long start = Stopwatch.GetTimestamp();
        GC.KeepAlive(action());
        elapsed[i] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }
    Array.Sort(elapsed);
    return elapsed[count / 2];
}

static double Allocated(Func<int> action)
{
    const int iterations = 10000;
    for (int i = 0; i < 1000; i++) action();
    long before = GC.GetAllocatedBytesForCurrentThread();
    int total = 0;
    for (int i = 0; i < iterations; i++) total += action();
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    GC.KeepAlive(total);
    return allocated / (double)iterations;
}
