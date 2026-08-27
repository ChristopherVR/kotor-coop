using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Guards the verb-table refactor in ConsoleCommand.*.cs: every non-sentinel
/// <see cref="CoopCommandKind"/> that <see cref="ConsoleCommand.Parse"/> can produce must be
/// reachable through some verb line, i.e. the declarative registry has a row for it. A kind
/// dropped from the table (or a new one never wired up) fails here instead of silently
/// becoming unreachable.
/// </summary>
public class ConsoleCommandVerbTableCoverageTests
{
    /// <summary>One well-formed line per producible kind, chosen to parse successfully (not
    /// hit an Invalid branch) so the resulting Kind is the real one.</summary>
    private static readonly Dictionary<CoopCommandKind, string> SampleLineByKind = new()
    {
        [CoopCommandKind.Connect] = "connect 10.0.0.5",
        [CoopCommandKind.Host] = "host",
        [CoopCommandKind.Disconnect] = "disconnect",
        [CoopCommandKind.Reconnect] = "reconnect",
        [CoopCommandKind.Status] = "status",
        [CoopCommandKind.Say] = "say hello",
        [CoopCommandKind.Clear] = "clear",
        [CoopCommandKind.Servers] = "servers",
        [CoopCommandKind.Help] = "help",
        [CoopCommandKind.Password] = "password hunter2",
        [CoopCommandKind.Warp] = "warp danm13",
        [CoopCommandKind.Who] = "who",
        [CoopCommandKind.Where] = "where",
        [CoopCommandKind.Census] = "census",
        [CoopCommandKind.ResolveObject] = "resolve 0x35",
        [CoopCommandKind.FireScript] = "firescript 0x35",
        [CoopCommandKind.MoveNear] = "movenear 0x35",
        [CoopCommandKind.NetSim] = "netsim status",
        [CoopCommandKind.LagStats] = "lagstats",
        [CoopCommandKind.PublicAddress] = "public_address",
        [CoopCommandKind.God] = "god on",
        [CoopCommandKind.TopUp] = "topup",
        [CoopCommandKind.StandUp] = "standup",
        [CoopCommandKind.Slay] = "slay",
        [CoopCommandKind.Teleport] = "tp 1 2",
        [CoopCommandKind.GoTo] = "goto Malak",
        [CoopCommandKind.DropItem] = "dropitem g_w_blstrpstl001",
        [CoopCommandKind.Cheats] = "cheats",
        [CoopCommandKind.StageMovie] = "moviewarp show",
        [CoopCommandKind.Items] = "items",
        [CoopCommandKind.InspectItem] = "item 0x131",
        [CoopCommandKind.SetItem] = "setitem 0x131",
        [CoopCommandKind.UseDoor] = "usedoor",
        [CoopCommandKind.GiveLoot] = "giveloot 0x35 g_w_blstrpstl001",
        [CoopCommandKind.ApplyEffect] = "applyeffect stun",
        [CoopCommandKind.Effects] = "effects",
        [CoopCommandKind.MoviePlay] = "movieplay 01c",
        [CoopCommandKind.DestroyObject] = "destroyobject 0x35",
        [CoopCommandKind.OpenDoor] = "opendoor",
        [CoopCommandKind.UsePlaceable] = "useplaceable",
        [CoopCommandKind.ClosePlaceable] = "closeplaceable",
        [CoopCommandKind.Leader] = "leader pc",
        [CoopCommandKind.Recruit] = "recruit 2",
        [CoopCommandKind.SkipLine] = "skipline",
        [CoopCommandKind.SelectReply] = "selectreply 0",
        [CoopCommandKind.WalkTo] = "walkto 1 2",
        [CoopCommandKind.LoadGame] = "loadgame 0",
        [CoopCommandKind.QuitToMenu] = "quittomenu",
        [CoopCommandKind.ScriptGive] = "scriptgive g_w_blstrpstl001",
        [CoopCommandKind.LootTake] = "loottake",
        [CoopCommandKind.PoolAdd] = "pooladd g_w_blstrpstl001",
        [CoopCommandKind.PoolRemove] = "poolremove g_w_blstrpstl001",
        [CoopCommandKind.GrantXp] = "grantxp me 500",
        [CoopCommandKind.Equip] = "equip me g_w_blstrpstl001",
        [CoopCommandKind.GrantGold] = "grantgold 500",
        [CoopCommandKind.SetGlobal] = "setglobal K_KOTOR_MASTER 15",
        [CoopCommandKind.Pazaak] = "pazaak",
        [CoopCommandKind.OpenStore] = "openstore kebla_yurt_shop",
        [CoopCommandKind.PartySelectOk] = "partyselect ok",
        [CoopCommandKind.Faction] = "faction 0x14",
        [CoopCommandKind.PazaakEnd] = "pazaak end",
        [CoopCommandKind.Buy] = "buy Larrim_Store g_w_blstrpstl001",
        [CoopCommandKind.Sell] = "sell Larrim_Store g_w_blstrpstl001",
    };

    /// <summary>Sentinel kinds that are outcomes of parsing (or its absence), not tied to one
    /// verb's table row, so they are deliberately excluded from <see cref="SampleLineByKind"/>.</summary>
    private static readonly CoopCommandKind[] SentinelKinds =
    [
        CoopCommandKind.None, CoopCommandKind.Invalid, CoopCommandKind.Unknown,
    ];

    [Fact]
    public void EveryNonSentinelKindHasASampleLine()
    {
        foreach (CoopCommandKind kind in Enum.GetValues<CoopCommandKind>())
        {
            if (SentinelKinds.Contains(kind))
            {
                continue;
            }

            Assert.True(
                SampleLineByKind.ContainsKey(kind),
                $"{kind} has no sample line in this test - the verb table (or this test) is missing a row for it");
        }
    }

    [Theory]
    [MemberData(nameof(SampleLines))]
    public void SampleLineParsesToItsKind(CoopCommandKind kind, string line)
    {
        CoopCommand parsed = ConsoleCommand.Parse(line);
        Assert.Equal(kind, parsed.Kind);
    }

    public static IEnumerable<object[]> SampleLines()
    {
        foreach ((CoopCommandKind kind, string line) in SampleLineByKind)
        {
            yield return [kind, line];
        }
    }
}
