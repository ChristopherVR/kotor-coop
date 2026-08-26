using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>The LAN server browser: listening, publishing to the in-game panel, and the
/// `servers` console listing.</summary>
public sealed partial class CoopCommandDispatcher
{
    /// <summary>Listens for co-op servers on the LAN so `servers` can list them and
    /// `connect #n` can dial one without an IP. Started on the first RunAsync tick.</summary>
    private readonly ServerDiscovery _browser = new();
    private bool _browsing;

    /// <summary>The servers listed by the last `servers` command, so `connect #n` resolves.</summary>
    private IReadOnlyList<ServerInfo> _lastServerList = [];

    /// <summary>
    /// Keeps the in-game browser panel fed with what the LAN listener has heard, for as long
    /// as the agent is alive — not only while it is sitting at the menu.
    ///
    /// The menu loop publishes this before you connect; nothing did afterwards, so the F9
    /// panel went empty the moment you joined a session and stayed that way, which reads as
    /// "the browser is broken" rather than "the list is not being refreshed". The publish
    /// itself is cheap: it signature-checks and returns without writing when the list has
    /// not changed, which is the common case.
    /// </summary>
    private void PublishBrowserList()
    {
        if (_kotor is null)
        {
            return;
        }

        _lastServerList = _browser.Servers;
        _kotor.PublishBrowserServers(
            [.. _lastServerList.Select(s =>
                (s.Name, s.Address, (byte)s.PlayerCount, (byte)s.MaxPlayers, s.Locked, s.Module, s.Faces))],
            passwordHeld: _password.Length > 0);
    }

    /// <summary>Posts the LAN server list to the overlay, numbered for `connect #n`.</summary>
    private void ListServers()
    {
        _lastServerList = _browser.Servers;
        if (_lastServerList.Count == 0)
        {
            Reply("no co-op servers found on the LAN (host one, or connect by IP)");
            return;
        }

        Reply($"servers on the LAN ({_lastServerList.Count}) - connect #n to join:");
        for (int i = 0; i < _lastServerList.Count; i++)
        {
            ServerInfo s = _lastServerList[i];
            Reply($"  {i + 1}) {s.Name}  {s.Address}  ({s.PlayerCount}/{s.MaxPlayers})");
        }
    }
}
