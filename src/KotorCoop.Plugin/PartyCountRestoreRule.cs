namespace KotorCoop.Plugin;

/// <summary>
/// The decision half of the <c>CSWParty+0x00</c> member-count RESTORE
/// (<c>Odyssey.TryResolveFollowRecord</c>'s <c>allowCountRestore</c> path, round 101/105,
/// <c>party_follow_stall_recovery</c>), separated so it can be unit-tested — the same split
/// <see cref="PartyTogetherClampRule"/> uses for the clamp that writes the same field the other
/// way.
///
/// ⚠ THE CRASH THIS CLOSES (2026-08-29, docs/research/transition-crashes.md's host-warp-crash
/// section, shared-inventory-v2.md §15.6). The restore was pure arithmetic: take
/// <c>CSWPartyTable</c>'s companions-only count, add one for the PC, and write that into
/// <c>CSWParty+0x00</c> if the field currently reads lower. That is the right NUMBER and the
/// wrong INVARIANT, because <c>CSWParty+0x00</c> is not a statement about how many companions the
/// save has — it is the bound every consumer walks the member ARRAY with:
///
/// <code>
/// CSWParty::GetCharacter(i):  i &lt; 0 || i &gt;= [this] -> 0
///                             else GetCreatureByGameObjectID(entry[i].objectId)
/// </code>
///
/// and <c>CSWGuiInGameEquip::UpdatePortraits</c> (0x006B5FEE) guards with
/// <c>cmp [edi],1 / jle skip</c> — it asks for member 1 the moment the count is above 1, and
/// dereferences the answer without a null check. Across a module load the two facts diverge:
/// the party TABLE is save state and survives the transition already saying "one companion",
/// while <c>CSWParty</c>'s array for the NEW module has not been populated yet and member[1]
/// still reads the empty-slot sentinel (<c>0x7F000000</c>). Writing <c>2</c> there in that window
/// hands the incoming module's own GUI construction a count of 2 over an array with one live
/// entry, and the host dies at <c>UpdatePortraits+0x48E</c> — deterministically, 6/6, on any
/// relocating <c>warp</c>.
///
/// Two further things made it a HOST crash specifically rather than a transient:
/// <list type="bullet">
/// <item>the restore was left standing even when the retry it was made for still failed, and the
/// only thing in this codebase that writes the field back DOWN
/// (<c>PartyRosterController.SyncClientPartyTogetherState</c>, <c>party_together_fix</c>) is
/// CLIENT-ONLY — so on a host nothing ever corrected it;</item>
/// <item>the per-slot latch that is supposed to keep an engine field write off the per-frame path
/// was only set when the retry SUCCEEDED, so a failing restore re-armed itself every frame.</item>
/// </list>
///
/// The fix is to stop treating "how many members should exist" as the bound and start using "how
/// many members actually resolve". <paramref name="resolvableMembers"/> is the length of the
/// LEADING run of member slots whose stored client object id resolves to a live creature through
/// the very same call <c>GetCharacter</c> itself uses — measured, not timed, so it is inherently
/// epoch-aware: during a half-built module it simply reads 1 and no raise happens, and the frame
/// the engine finishes populating the array it reads 2 and the restore proceeds exactly as round
/// 101 intended. (<c>PartyFollowTransitionRule</c>'s one-frame epoch skip stays where it is; it
/// covers the frame the epoch first changes, this covers every frame after it until the array is
/// genuinely ready.)
/// </summary>
internal static class PartyCountRestoreRule
{
    /// <summary>
    /// What to write into <c>CSWParty+0x00</c>, or <c>0</c> for "write nothing at all".
    /// </summary>
    /// <param name="currentCount">The field's present value.</param>
    /// <param name="desiredCount">The arithmetic true population —
    /// <c>CSWPartyTable</c>'s companions-only count plus one for the PC.</param>
    /// <param name="resolvableMembers">How many member slots, counting from 0 and stopping at the
    /// first failure, currently resolve to a live creature. This is the hard ceiling: the count
    /// may never exceed it, because every value above it names an array entry a consumer will
    /// dereference NULL.</param>
    /// <param name="capacity">CSWParty's own member-array capacity (3).</param>
    public static int SafeRestoreCount(
        int currentCount, int desiredCount, int resolvableMembers, int capacity)
    {
        if (currentCount < 0 || capacity <= 0)
        {
            return 0;
        }

        // A count this code does not recognise as a count is not one it may write.
        if (desiredCount < 0 || desiredCount > capacity)
        {
            return 0;
        }

        if (resolvableMembers < 0)
        {
            return 0;
        }

        int ceiling = resolvableMembers < capacity ? resolvableMembers : capacity;
        int target = desiredCount < ceiling ? desiredCount : ceiling;

        // Only ever UP, and only when there is genuinely something to gain. Never down: lowering
        // the count is party_together_fix's job and its own (client-only) decision.
        return target > currentCount ? target : 0;
    }
}
