namespace KotorCoop.Plugin;

/// <summary>Shared by the native browser and the in-world F9 overlay. Only fixed,
/// known commands can cross the command mailbox from a menu action.</summary>
internal static class CoopMenuActions
{
    public const int Count = 6;
    public const int VisibleRows = 5;
    public static int WindowStart(int total, int selected) =>
        Math.Clamp(selected - VisibleRows + 1, 0, Math.Max(0, total - VisibleRows));
    public static string Caption(int index) => index switch
    {
        0 => "Retry last connection",
        1 => "Copy connection address",
        2 => "Connection help",
        3 => "Create support report",
        4 => "Start/resume hosting",
        5 => "Leave co-op",
        _ => string.Empty,
    };
    public static string Command(int index) => index switch
    {
        0 => "reconnect",
        1 => "public_address",
        2 => "connectionhelp",
        3 => "supportreport",
        4 => "host",
        5 => "disconnect",
        _ => string.Empty,
    };
    public static string Marker(int index) => "\u0001action-" + index;
    public static int FindMarker(string? marker)
    {
        for (int i = 0; i < Count; i++)
            if (marker == Marker(i)) return i;
        return -1;
    }

    /// <summary>Discovery republishes on beacon timing and can reorder hosts. An action
    /// retains its action index; a host retains its endpoint. Only a lost identity requires
    /// releasing Enter before the replacement row can be confirmed.</summary>
    public static (int Index, bool Preserved) ResolveSelection(
        string? hostAddress, int actionIndex, IReadOnlyList<string> hosts)
    {
        if (actionIndex >= 0 && actionIndex < Count)
            return (hosts.Count + actionIndex, true);
        if (!string.IsNullOrEmpty(hostAddress))
        {
            for (int i = 0; i < hosts.Count; i++)
                if (string.Equals(hosts[i], hostAddress, StringComparison.OrdinalIgnoreCase))
                    return (i, true);
        }
        return (0, false);
    }
}
