namespace KotorCoop.Session;

/// <summary>
/// The two Steam-facing verbs: <c>steamstatus</c> (read-only) and <c>steaminvite</c> (the
/// explicit send). Both are local-only — neither ever reaches the wire, since a Steam invite
/// is Steam's own delivery, not this mod's protocol. See
/// <c>KotorCoop.Agent.CoopCommandDispatcher.Steam.cs</c> for what a dispatcher does with them
/// and docs/research/steam-support.md for the design.
/// </summary>
public static partial class ConsoleCommand
{
    private static CoopCommand ParseSteamStatus(string[] rest) => new(CoopCommandKind.SteamStatus);

    // A SteamID64 is a 64-bit unsigned integer (the friends-list UI and steamid.io both show
    // it as a plain decimal string, e.g. 76561198000000000) — parsed here, not resolved from a
    // friend's display name: this build has no ISteamFriends name/roster lookup bound (see
    // steam-support.md §10's export list), so the player types the number a friend can copy
    // from their own Steam profile ("Copy Player ID" / the URL's trailing digits).
    private static CoopCommand ParseSteamInvite(string[] rest)
    {
        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.Invalid,
                Error: "steaminvite needs a friend's SteamID64, e.g. steaminvite 76561198000000000 "
                    + "(from their Steam profile: ... -> Copy Player ID, or the digits at the end of "
                    + "their profile URL)");
        }

        if (!ulong.TryParse(rest[0], out ulong steamId) || steamId == 0)
        {
            return new CoopCommand(CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not a SteamID64 (a plain positive number, e.g. 76561198000000000)");
        }

        return new CoopCommand(CoopCommandKind.SteamInvite, Address: steamId.ToString());
    }
}
