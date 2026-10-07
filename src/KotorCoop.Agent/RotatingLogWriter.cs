using System.Text;
using KotorCoop.Plugin;

namespace KotorCoop.Agent;

/// <summary>
/// The agent's Console.Out/Error when the plugin launched it (<c>--console-log</c>): appends to
/// the log file through the agent's OWN handle and rotates it mid-session with the same
/// size/generation rules as the plugin log (<see cref="LogRotation"/>, default 8 MB x 3).
///
/// Why the agent and not the plugin: the plugin hands the child an inherited raw file handle, and
/// whatever the child writes through it lands in whichever file that handle names, even after a
/// rename. Only the process that writes can close the file, shift the generations and reopen at
/// the original path. The launcher opens its fallback handle with FILE_SHARE_DELETE so the
/// rename here is not blocked by it.
///
/// Thread-safe (the dashboard pump, the join flow and the session all print). Best-effort: a
/// failed rotation is retried after another megabyte rather than on every write, and a failed
/// write is swallowed — logging must never take the agent down.
/// </summary>
public sealed class RotatingLogWriter : TextWriter
{
    private static readonly UTF8Encoding Utf8 = new(false);

    private readonly string _path;
    private readonly long _capBytes;
    private readonly int _keep;
    private readonly object _gate = new();
    private FileStream? _stream;
    private long _length;
    private long _nextCheck;

    public RotatingLogWriter(string path, int capMb, int keepGenerations)
    {
        _path = path;
        _capBytes = LogRotation.CapBytes(capMb);
        _keep = LogRotation.ClampKeepGenerations(keepGenerations);
        Open();
        _nextCheck = _capBytes;
    }

    public override Encoding Encoding => Utf8;

    /// <summary>Test seam: how many bytes the live file currently holds.</summary>
    internal long Length
    {
        get { lock (_gate) { return _length; } }
    }

    public override void Write(char value) => Write(value.ToString());

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        lock (_gate)
        {
            try
            {
                if (_stream is null)
                {
                    Open();
                }

                byte[] bytes = Utf8.GetBytes(value);
                _stream?.Write(bytes, 0, bytes.Length);
                _stream?.Flush();
                _length += bytes.Length;

                if (_length > _nextCheck)
                {
                    Rotate();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // logging is best effort
            }
        }
    }

    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));

    public override void Flush()
    {
        lock (_gate)
        {
            try { _stream?.Flush(); } catch (IOException) { }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_gate)
            {
                _stream?.Dispose();
                _stream = null;
            }
        }

        base.Dispose(disposing);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int which);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int SetStdHandle(int which, nint handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int CloseHandle(nint handle);

    /// <summary>
    /// Closes the stdout/stderr handle the plugin's launcher inherited into this process. It
    /// names the same file, and an open handle follows the file through renames: left open it
    /// would end up pinned to the oldest generation and block the rotation that overwrites it.
    /// Call once, after Console.SetOut/SetError, so nothing reads the closed handle. No-op off
    /// Windows and never throws.
    /// </summary>
    public static void ReleaseInheritedStdHandles()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            nint outHandle = GetStdHandle(-11);
            nint errHandle = GetStdHandle(-12);
            _ = SetStdHandle(-11, 0);
            _ = SetStdHandle(-12, 0);
            if (outHandle != 0 && outHandle != -1)
            {
                _ = CloseHandle(outHandle);
            }

            if (errHandle != 0 && errHandle != -1 && errHandle != outHandle)
            {
                _ = CloseHandle(errHandle);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // best effort
        }
    }

    private void Open()
    {
        _stream = new FileStream(_path, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);
        _length = _stream.Length;
    }

    private void Rotate()
    {
        _stream?.Dispose();
        _stream = null;
        try
        {
            LogRotation.ApplyShiftPlan(LogRotation.ShiftPlan(_path, _keep));
            _nextCheck = _capBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Could not move it (something holds it without share-delete): keep appending and
            // look again after another megabyte instead of retrying on every line.
            _nextCheck = _length + 1024 * 1024;
        }

        Open();
    }
}
