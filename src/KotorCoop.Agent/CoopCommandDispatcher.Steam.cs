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

        if (CurrentSteamConnectString() is not { } connectString)
        {
            steam.Publish(null);
            return;
        }

        steam.Publish(connectString);
    }

    /// <summary>
    /// The rich-presence <c>connect</c> string for whatever this install is hosting right now,
    /// or null when it is not hosting. Shared by the passive publish (<see
    /// cref="MaintainSteamPresence"/>) and the explicit <c>steaminvite</c> command
    /// (<see cref="SteamInviteCommand"/>) so both always agree with each other and with
    /// <see cref="HandleSteamJoinRequest"/>'s own parser on what a friend actually receives.
    /// </summary>
    private string? CurrentSteamConnectString()
    {
        if (manager.Current is not CoopHost host)
        {
            return null;
        }

        System.Net.IPEndPoint endpoint = host.PublicEndPoint ?? host.ListenEndPoint;
        return SteamConnectStringRule.Format(endpoint.Address.ToString(), endpoint.Port, _password);
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
            Address: host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}",
            Password: password.Length > 0 ? password : null));
    }

    /// <summary>
    /// <c>steamstatus</c>: the one place a player can see whether the whole feature is actually
    /// working, rather than reading log lines. Every branch is a plain state report, never an
    /// error — off/absent/inactive are all normal for the large majority of players (see the
    /// class doc comment's three-things-must-be-true list).
    /// </summary>
    private void SteamStatusCommand(CoopCommand _)
    {
        if (steam is null)
        {
            Reply("steam: steam_invites is off in coop.ini");
            return;
        }

        if (!steam.IsActive)
        {
            // TryActivate() already logged the specific reason (no DLL / Steam not running /
            // app not owned) the first time something asked; repeat the one-line summary here
            // rather than the detail, since that log line already told the full story once.
            Reply("steam: not active - see kotorcoop-agent.log for why (no steam_api64.dll, "
                + "Steam not running, or this account does not own KOTOR on Steam are the "
                + "normal reasons)");
            return;
        }

        string who = steam.SteamId64 == 0 ? "unknown (ISteamUser did not bind)" : steam.SteamId64.ToString();

        Reply(CurrentSteamConnectString() is not null
            ? $"steam: active, SteamID {who}, hosting - friends can Join Game or receive a steaminvite"
            : $"steam: active, SteamID {who}, not hosting - nothing to invite anyone to yet");
    }

    /// <summary>
    /// <c>steaminvite &lt;steamid64&gt;</c>: the explicit push, for a friend who is not looking
    /// at your profile right now. Reuses exactly the connect string the passive publish would
    /// show them (<see cref="CurrentSteamConnectString"/>), so the two paths can never disagree
    /// about what a friend ends up connecting to.
    /// </summary>
    private void SteamInviteCommand(CoopCommand command)
    {
        if (steam is null)
        {
            Reply("steam: steam_invites is off in coop.ini");
            return;
        }

        if (!steam.IsActive)
        {
            Reply("steam: not active - see kotorcoop-agent.log for why");
            return;
        }

        if (CurrentSteamConnectString() is not { } connectString)
        {
            Reply("steam: not hosting - host first, then steaminvite");
            return;
        }

        if (!ulong.TryParse(command.Address, out ulong steamId) || steamId == 0)
        {
            // The console parser (ConsoleCommand.ParseSteamInvite) already validates this, so
            // reaching here with a bad value means a caller other than the console (blockpoke,
            // a future dashboard button) skipped that check - report rather than throw.
            Reply($"steam: '{command.Address}' is not a SteamID64");
            return;
        }

        Reply(steam.Invite(steamId, connectString)
            ? $"steam: invited {steamId}"
            : $"steam: could not invite {steamId} (Steam refused the invite - check the SteamID, "
                + "and that you are both friends on Steam)");
    }
}
