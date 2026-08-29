using KotorCoop.Session;
using KotorCoop.Steam;

namespace KotorCoop.Agent;

/// <summary>
/// Steam's friends-list "Join Game", both directions: publish what a friend would join while
/// this install is hosting, and turn an incoming join request into an ordinary connect.
///
/// Both halves are no-ops unless three separate things are true (coop.ini
/// <c>steam_invites</c> is on, a player-supplied <c>steam_api64.dll</c> loaded, and Steam is
/// running with app 32370 owned) — see <see cref="SteamInviteService"/> and
/// docs/research/steam-support.md §12. Nothing in this file can fail a tick: the tick loop's
/// own catch is the last line of defence, but the service swallows native faults itself and
/// goes permanently quiet rather than throwing into the session.
/// </summary>
public sealed partial class CoopCommandDispatcher
{
    /// <summary>
    /// Publishes the current session as a Steam rich-presence <c>connect</c> string, or clears
    /// it when this install is not hosting.
    ///
    /// ⚠ THE ADDRESS MUST BE THE ONE A FRIEND CAN ACTUALLY REACH. A rich-presence string is
    /// read by someone on the internet, so the LAN listen address is the wrong answer whenever
    /// STUN has found a public one — a friend handed <c>192.168.1.20</c> gets a connection
    /// that can only ever time out, and would have no way to tell that from the mod being
    /// broken. So: the public endpoint when discovery has produced one, the listen endpoint
    /// otherwise (correct for a LAN game, and honest about being nothing better).
    ///
    /// Called every 150 ms tick; <see cref="SteamInviteService.Publish"/> is a no-op unless
    /// the string actually changed, which is what keeps that safe.
    /// </summary>
    private void MaintainSteamPresence()
    {
        if (steam is null)
        {
            return;
        }

        if (manager.Current is not CoopHost host)
        {
            steam.Publish(null);
            return;
        }

        System.Net.IPEndPoint endpoint = host.PublicEndPoint ?? host.ListenEndPoint;

        steam.Publish(SteamConnectStringRule.Format(
            endpoint.Address.ToString(), endpoint.Port, _password));
    }

    /// <summary>
    /// Pumps Steam's callbacks and turns a "Join Game" into the same connect a player picking
    /// a row in the server browser would produce — including arming the one-shot menu
    /// auto-load, so an invite accepted at the main menu ends with the player in the world
    /// rather than staring at the menu with a live socket behind it.
    /// </summary>
    private void PumpSteamJoinRequests()
    {
        steam?.Pump(HandleSteamJoinRequest);
    }

    /// <summary>
    /// ⚠ UNTRUSTED INPUT. <paramref name="connectString"/> was written by another Steam user's
    /// client and arrives without any of this session's own validation having touched it. It is
    /// parsed by the total, I/O-free <see cref="SteamConnectStringRule"/> and rejected loudly
    /// if it is not one of ours; only then does it become an address and a password.
    /// </summary>
    private void HandleSteamJoinRequest(string connectString)
    {
        if (!SteamConnectStringRule.TryParse(connectString, out string host, out int port, out string password))
        {
            Reply("steam: ignored a join request that was not a co-op connect string");
            return;
        }

        Reply($"steam: joining {host}:{port} from a friend's invite");

        DispatchFromGame(new CoopCommand(
            CoopCommandKind.Connect,
            Address: $"{host}:{port}",
            Password: password.Length > 0 ? password : null));
    }
}
