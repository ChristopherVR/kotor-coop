using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// The one place a drop is turned into a shared ground item, whether it was asked for by the
/// dashboard's D key, by the drop file, or by the <c>dropitem</c> console verb.
///
/// It lives on its own because there are two front doors and only one correct answer behind
/// them, and because the answer is a SENTENCE rather than a boolean: every refusal here has
/// to name what it saw. That is not decoration — the drop path is how item identity is
/// verified, the verification runs unattended, and a refusal that says nothing is
/// indistinguishable from a drop that never happened. Live on 2026-08-15 that ambiguity cost
/// a whole pass: `drop #131` answered "not a readable item here" for a ground body the
/// plugin log had announced creating four seconds earlier, and the message named neither
/// which half failed nor what the sweep had seen.
/// </summary>
public static class ItemDropRequest
{
    /// <summary>How far in front of the player a dropped item lands, in metres.</summary>
    private const float DropDistance = 1.6f;

    /// <summary>What a drop did. <paramref name="ItemId"/> is 0 for every refusal, so a
    /// caller never has to read the sentence to find out whether anything happened.</summary>
    public readonly record struct Outcome(uint ItemId, string Note)
    {
        public bool Dropped => ItemId != 0;
    }

    /// <summary>
    /// Announces <paramref name="request"/> as a shared ground item at the local player's
    /// feet, and returns the sentence describing what happened.
    ///
    /// <paramref name="request"/> is either a blueprint resref or <c>#&lt;hex&gt;</c> naming a
    /// LIVE item in this instance by engine object id. Only the second form can exercise item
    /// identity: a conjured blueprint has no instance to read a stack size or an upgrade mask
    /// off, and a caller that only holds a blueprint name must say "unknown" rather than
    /// assert a stock identity — asserting one is indistinguishable from having genuinely read
    /// a stock item, and would strip an upgraded weapon on the far side.
    ///
    /// The <c>#</c> form does not remove the item from wherever it is. Duplicating an item
    /// locally is a cheaper price than needing the game to have a drop UI it does not have.
    /// </summary>
    public static Outcome Drop(SessionManager manager, IGameBridge bridge, string request)
    {
        if (manager.Agent is not { } agent)
        {
            return Refused("drop: not connected");
        }

        PlayerState local = agent.LastLocalState;
        if (!local.IsPlausible || !agent.LastReadSucceeded)
        {
            return Refused("drop refused: local player not readable");
        }

        string resref = request;
        ItemIdentity identity = ItemIdentity.Unknown;

        // Two spellings of the same thing, both HEX: "#131" is the form the drop file has
        // always used and docs/live-verification.md records, and "0x131" is the form every
        // log line in this project prints. A bare number is deliberately NOT an id — it is
        // also a legal resref, and guessing between them would drop something other than what
        // was asked for.
        if (ConsoleCommand.TryParseItemId(request, out uint objectId))
        {
            if (bridge is not KotorGameBridge live)
            {
                return Refused("dropping a live item by id needs --kotor");
            }

            // ⚠ THE IDENTITY READ RUNS FIRST, and the two failures are reported separately.
            // The old order asked for the resref first and joined the two with `||`, so a
            // resref miss short-circuited — and the resref lookup is a pure cache read while
            // the identity read is the one that can refresh the cache. An item spawned after
            // the agent attached was therefore unknown for ever, and the single message
            // covering both halves made that look like a bad object id.
            if (!live.TryReadItemIdentity(objectId, out identity, out string why, force: true))
            {
                return Refused($"drop: {why}");
            }

            if (!live.TryGetItemResref(objectId, out string liveResref, force: true))
            {
                return Refused(
                    $"drop: 0x{objectId:X} IS an item here (stack {identity.StackSize}, "
                    + $"charges {identity.Charges}/{identity.MaxCharges}, "
                    + $"upgrades 0x{identity.Upgrades:X}) but its tag is unreadable, so there "
                    + "is no blueprint name to send with it");
            }

            resref = liveResref;
        }

        // In front of the player, along their facing, rather than at a fixed world offset:
        // the camera sits behind the character, so a world-axis offset drops the item
        // off-screen as often as not. Facing is the angle of the orientation vector, so
        // forward is (cos, sin) of it.
        Vector3f at = new(
            local.Position.X + (MathF.Cos(local.Facing) * DropDistance),
            local.Position.Y + (MathF.Sin(local.Facing) * DropDistance),
            local.Position.Z);

        uint itemId = agent.Session.DropItem(resref, local.Module, at, identity);
        if (itemId == 0)
        {
            return Refused($"drop refused: {resref} (not connected, or ground full)");
        }

        return new Outcome(
            itemId,
            identity.IsKnown
                ? $"dropped {resref} as item 0x{itemId:X} (stack {identity.StackSize}, "
                  + $"charges {identity.Charges}/{identity.MaxCharges}, "
                  + $"upgrades 0x{identity.Upgrades:X}, flags 0x{identity.Flags:X})"
                : $"dropped {resref} as item 0x{itemId:X} (no identity — a blueprint drop "
                  + "carries none, which is the fail-safe, not a fault)");
    }

    /// <summary>A refusal: no item, and a sentence saying why not.</summary>
    private static Outcome Refused(string note) => new(0, note);
}
