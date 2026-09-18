using System.Text;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.MockGame;

/// <summary>
/// Draws the world from the contents of <see cref="GameStateBlock"/>, not from the
/// session. That distinction is the point of the harness: remote avatars only appear
/// here if the agent actually wrote them into this process's memory from outside.
/// </summary>
public sealed class MockRenderer
{
    private const int GridWidth = 72;
    private const int GridHeight = 22;

    private static readonly string[] PeerColours =
    [
        "\u001b[96m", // cyan
        "\u001b[95m", // magenta
        "\u001b[93m", // yellow
        "\u001b[94m", // blue
        "\u001b[91m", // red
        "\u001b[92m", // green
        "\u001b[97m", // white
        "\u001b[90m", // grey
    ];

    private const string Reset = "\u001b[0m";
    private const string LocalColour = "\u001b[1;92m";
    private const string DimColour = "\u001b[90m";

    private readonly StringBuilder _builder = new(8192);
    private readonly bool _canPosition = !Console.IsOutputRedirected;

    public void Render(GameStateBlock block, MockWorld world, ICoopSession? session, CoopAgent? agent, string bannerLine)
    {
        char[,] cells = new char[GridHeight, GridWidth];
        string[,] colours = new string[GridHeight, GridWidth];

        for (int row = 0; row < GridHeight; row++)
        {
            for (int col = 0; col < GridWidth; col++)
            {
                cells[row, col] = ' ';
                colours[row, col] = string.Empty;
            }
        }

        List<RemoteSlotView> remotes = [.. block.ActiveRemotes()];

        UpdateBounds([world.Position, .. remotes.Select(r => r.Position)]);

        for (int i = 0; i < remotes.Count; i++)
        {
            RemoteSlotView remote = remotes[i];
            if (!TryProject(remote.Position, out int row, out int col))
            {
                continue;
            }

            cells[row, col] = (char)('1' + (i % 9));
            colours[row, col] = PeerColours[i % PeerColours.Length];
        }

        if (TryProject(world.Position, out int localRow, out int localCol))
        {
            cells[localRow, localCol] = '@';
            colours[localRow, localCol] = LocalColour;
        }

        _builder.Clear();
        _builder.Append("\u001b[H"); // home, without the flicker of a full clear
        _builder.Append(bannerLine).Append(Reset).AppendLine();

        _builder.Append(DimColour).Append('+').Append('-', GridWidth).Append('+').Append(Reset).AppendLine();

        for (int row = 0; row < GridHeight; row++)
        {
            _builder.Append(DimColour).Append('|').Append(Reset);

            for (int col = 0; col < GridWidth; col++)
            {
                string colour = colours[row, col];
                if (colour.Length > 0)
                {
                    _builder.Append(colour).Append(cells[row, col]).Append(Reset);
                }
                else
                {
                    _builder.Append(cells[row, col]);
                }
            }

            _builder.Append(DimColour).Append('|').Append(Reset).AppendLine();
        }

        _builder.Append(DimColour).Append('+').Append('-', GridWidth).Append('+').Append(Reset).AppendLine();

        AppendLinePadded($"{LocalColour}@{Reset} {world.Module,-10} pos {world.Position} facing {world.Facing:F2} hp {world.Health}");

        if (session is null)
        {
            AppendLinePadded($"{DimColour}standalone (no co-op session){Reset}");
        }
        else
        {
            SessionStats stats = session.GetStats();
            AppendLinePadded($"net  {stats.Status}");
            AppendLinePadded(
                $"     peers {stats.PeerCount}  rtt {stats.RoundTripMs,5:F1}ms  tx {stats.PacketsSent}  rx {stats.PacketsReceived}  bad {stats.PacketsDropped}");

            string flagText = FormatFlags(session);
            AppendLinePadded($"flags v{stats.FlagVersion} {flagText}");

            if (agent is not null)
            {
                string read = agent.LastReadSucceeded ? "ok" : $"FAIL ({agent.ReadFailureCount})";
                string write = agent.Bridge.CanWriteRemotes
                    ? agent.LastWriteSucceeded ? "ok" : "FAIL"
                    : "n/a (read-only profile)";
                AppendLinePadded($"mem  read {read}  write {write}  {agent.Bridge.Description}");
            }

            for (int i = 0; i < remotes.Count; i++)
            {
                RemoteSlotView remote = remotes[i];
                string colour = PeerColours[i % PeerColours.Length];
                AppendLinePadded(
                    $"{colour}{(char)('1' + (i % 9))}{Reset} {remote.Name,-10} pos {remote.Position} facing {remote.Facing:F2} hp {remote.Health}");
            }

            for (int i = remotes.Count; i < 4; i++)
            {
                AppendLinePadded(string.Empty);
            }
        }

        AppendLinePadded($"{DimColour}WASD move  Q/E turn  M change module  F toggle door flag  X take damage  Esc quit{Reset}");

        Write(_builder.ToString());
    }

    private static string FormatFlags(ICoopSession session)
    {
        Dictionary<ulong, sbyte> flags = session.Flags.ToDictionary();
        if (flags.Count == 0)
        {
            return "(none)";
        }

        return string.Join("  ", flags.OrderBy(f => f.Key).Select(f => $"{DemoFlags.Describe(f.Key)}={(f.Value != 0 ? "OPEN" : "SHUT")}"));
    }

    private void AppendLinePadded(string text)
    {
        _builder.Append(text);
        _builder.Append("\u001b[K"); // erase to end of line
        _builder.AppendLine();
    }

    private float _minX;
    private float _minY;
    private float _spanX = MockWorld.Size;
    private float _spanY = MockWorld.Size;

    /// <summary>
    /// Fits the view to whatever is on screen instead of assuming the mock world's own
    /// 0..100 bounds. A peer bridged in from a real game arrives in that game's coordinate
    /// space, which is nowhere near 0..100, and a fixed window would clamp it to the edge
    /// and show none of its movement.
    /// </summary>
    private void UpdateBounds(IEnumerable<Vector3f> points)
    {
        const float minimumSpan = 20f;
        const float padding = 0.1f;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minY = float.MaxValue, maxY = float.MinValue;
        bool any = false;

        foreach (Vector3f p in points)
        {
            if (!p.IsFinite)
            {
                continue;
            }

            any = true;
            minX = MathF.Min(minX, p.X);
            maxX = MathF.Max(maxX, p.X);
            minY = MathF.Min(minY, p.Y);
            maxY = MathF.Max(maxY, p.Y);
        }

        if (!any)
        {
            return;
        }

        float spanX = MathF.Max(maxX - minX, minimumSpan);
        float spanY = MathF.Max(maxY - minY, minimumSpan);

        _minX = minX - (spanX * padding);
        _minY = minY - (spanY * padding);
        _spanX = spanX * (1f + (2f * padding));
        _spanY = spanY * (1f + (2f * padding));
    }

    private bool TryProject(Vector3f position, out int row, out int col)
    {
        row = 0;
        col = 0;

        if (!position.IsFinite)
        {
            return false;
        }

        float nx = Math.Clamp((position.X - _minX) / _spanX, 0f, 1f);
        float ny = Math.Clamp((position.Y - _minY) / _spanY, 0f, 1f);

        col = (int)(nx * (GridWidth - 1));
        row = (int)((1f - ny) * (GridHeight - 1));
        return true;
    }

    private void Write(string frame)
    {
        if (_canPosition)
        {
            Console.Write(frame);
        }
        else
        {
            Console.WriteLine(frame);
        }
    }

    public void PrepareConsole()
    {
        if (!_canPosition)
        {
            return;
        }

        Console.OutputEncoding = Encoding.UTF8;
        Console.CursorVisible = false;
        Console.Clear();
    }

    public void RestoreConsole()
    {
        if (!_canPosition)
        {
            return;
        }

        Console.CursorVisible = true;
        Console.Write(Reset);
        Console.WriteLine();
    }
}

public static class DemoFlags
{
    /// <summary>Stands in for a real world flag, e.g. "the bulkhead on the Endar Spire is open".</summary>
    public const ulong BulkheadDoor = 1001;

    public static string Describe(ulong flagId) => flagId switch
    {
        BulkheadDoor => "bulkhead",
        _ => $"flag{flagId}",
    };
}
