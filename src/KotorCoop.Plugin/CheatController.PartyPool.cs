using System.Text;
using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

// The shared party item-pool staging cheats: PartyPoolAdd / PartyPoolRemove. Reuses the exact
// same request fields SetItem/GiveLoot already carry (Text = resref, Amount = count,
// Charges/MaxCharges/Upgrades/ItemFlags = the rest of the stack identity) — no SharedBlock
// layout change, no version bump. See docs/research/party-pool-sync.md.
internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// Adds <see cref="SharedBlock.CheatAmount"/> units of a resref+identity stack to the
    /// shared party pool, through <see cref="Odyssey.AddItemToPartyPool"/>. This is the
    /// automatic apply half of the sync channel (docs/research/party-pool-sync.md) — the same
    /// arbitrated single-slot channel a human's own console verb would use, submitted by
    /// <c>CoopCommandDispatcher</c>'s pending-apply drain, one at a time.
    /// </summary>
    private void PartyPoolAdd(uint playerId)
    {
        if (!PartyPoolRequestIsCurrent("add"))
        {
            return;
        }

        byte* text = block.CheatText;
        int length = 0;
        while (length < SharedBlock.CheatTextLength - 1 && text[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, "party pool add: no resref given");
            return;
        }

        int count = block.CheatAmount;
        if (count <= 0)
        {
            Answer(SharedBlock.CheatStatusRefused, "party pool add: needs a positive count");
            return;
        }

        string resref = Encoding.ASCII.GetString(text, length);
        uint charges = block.CheatItemCharges;
        uint maxCharges = block.CheatItemMaxCharges;
        uint upgrades = block.CheatItemUpgrades;
        uint wireFlags = block.CheatItemFlags;

        bool applied = Odyssey.AddItemToPartyPool(
            playerId, text, length, count,
            upgrades == SharedBlock.CheatFieldUnchanged ? 0 : upgrades,
            charges == SharedBlock.CheatFieldUnchanged ? 0 : charges,
            maxCharges == SharedBlock.CheatFieldUnchanged ? 0 : maxCharges,
            wireFlags == SharedBlock.CheatFieldUnchanged ? 0 : wireFlags,
            out uint itemId, out Odyssey.ItemIdentityFields after);

        if (!applied)
        {
            PluginHost.Log($"party pool: add '{resref}' x{count} failed — {Odyssey.LastPartyPoolNote}");
            Answer(SharedBlock.CheatStatusEngineRefused, $"'{resref}' add failed: {Odyssey.LastPartyPoolNote}");
            return;
        }

        PluginHost.Log(
            $"party pool: added '{resref}' (0x{itemId:X}) x{after.StackSize}, "
            + $"charges {after.Charges}/{after.MaxCharges}, upgrades 0x{after.Upgrades:X}");
        Answer(SharedBlock.CheatStatusApplied, $"added '{resref}' x{count} to the party pool");
    }

    /// <summary>
    /// Removes <see cref="SharedBlock.CheatAmount"/> units of a resref+identity stack from the
    /// shared party pool, through <see cref="Odyssey.RemovePartyPoolStack"/>. Reads no live
    /// item's identity beforehand (unlike <c>SetItem</c>) — the caller is asking to remove
    /// exactly the stack it already named, not a delta from a read.
    ///
    /// Unlike <see cref="PartyPoolAdd"/>, an unspecified field is passed through as
    /// <see cref="SharedBlock.CheatFieldUnchanged"/> RATHER than coerced to 0 — a REMOVE has no
    /// engine field to leave alone (there is nothing being written), so the sentinel instead
    /// reaches <see cref="Odyssey.RemovePartyPoolStack"/> as a WILDCARD: "match any value for
    /// this field" (see <c>Odyssey.RemovePartyPoolStack</c>'s own remarks). A wire-
    /// driven apply (<c>CoopCommandDispatcher.DrainAutomaticPartyPool</c>) always supplies
    /// concrete fields from a scanned key, so this only changes the behaviour of a human's own
    /// bare <c>poolremove &lt;resref&gt;</c>, which previously demanded an exact match against
    /// literal zeros — refusing an ambient-script deposit whose real Charges/Upgrades/Flags
    /// are not zero (docs/research/party-pool-sync.md §19.2/§20).
    /// </summary>
    private void PartyPoolRemove()
    {
        if (!PartyPoolRequestIsCurrent("remove"))
        {
            return;
        }

        byte* text = block.CheatText;
        int length = 0;
        while (length < SharedBlock.CheatTextLength - 1 && text[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, "party pool remove: no resref given");
            return;
        }

        int count = block.CheatAmount;
        if (count <= 0)
        {
            Answer(SharedBlock.CheatStatusRefused, "party pool remove: needs a positive count");
            return;
        }

        string resref = Encoding.ASCII.GetString(text, length);
        uint charges = block.CheatItemCharges;
        uint maxCharges = block.CheatItemMaxCharges;
        uint upgrades = block.CheatItemUpgrades;
        uint wireFlags = block.CheatItemFlags;

        // Passed through RAW, not coerced to 0: SharedBlock.CheatFieldUnchanged reaches
        // RemovePartyPoolStack as a per-field WILDCARD (see this method's own remarks) rather
        // than a demand for an exact-zero match nothing but a freshly-constructed item ever has.
        bool applied = Odyssey.RemovePartyPoolStack(
            text, length, count, charges, maxCharges, upgrades, wireFlags, out int removed);

        if (!applied)
        {
            PluginHost.Log($"party pool: remove '{resref}' x{count} failed — {Odyssey.LastPartyPoolNote}");
            Answer(
                SharedBlock.CheatStatusEngineRefused, $"'{resref}' remove failed: {Odyssey.LastPartyPoolNote}");
            return;
        }

        PluginHost.Log($"party pool: removed '{resref}' x{removed} (asked for {count})");
        Answer(SharedBlock.CheatStatusApplied, $"removed '{resref}' x{removed} from the party pool");
    }

    /// <summary>Pool requests are the only cheats that use Target and X as a mailbox scope
    /// fence. Check this before reading the request text or touching a repository: queued work
    /// from an earlier loaded world must never mutate a same-module replacement.</summary>
    private bool PartyPoolRequestIsCurrent(string operation)
    {
        float marker = block.CheatPosition.X;
        uint requestSerial = block.CheatTarget;
        uint currentSerial = block.WorldLoadSerial;
        if (PartyPoolMailboxFence.IsCurrent(marker, requestSerial, currentSerial))
        {
            return true;
        }

        string reason = marker != PartyPoolMailboxFence.Marker
            ? "request has no world-load fence"
            : $"request belongs to world load {requestSerial}, current load is {currentSerial}";
        Answer(
            SharedBlock.CheatStatusRefused,
            $"party pool {operation}: {reason}; no change was made");
        return false;
    }
}
