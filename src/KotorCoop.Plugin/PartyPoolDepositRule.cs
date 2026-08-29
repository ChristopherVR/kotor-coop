namespace KotorCoop.Plugin;

/// <summary>
/// What a shared-inventory deposit still has to do to the engine AFTER the engine's own
/// <c>CreateItemOnObject</c> has already run — the decision
/// docs/research/shared-inventory-v2.md §15 found had been made wrong since the channel was
/// written, and the single mechanism behind five live rig rounds of party-pool divergence.
///
/// <b>The fact the old code did not know.</b> <c>CreateItemOnObject(resref, aPartyMember, n)</c>
/// does not hand back a loose body for the caller to file away. It routes through
/// <c>CSWSCreature::AcquireItem</c> 0x005158E0, which calls
/// <c>CItemRepository::AddItem(ppItem, wantMerge=1, 0, markNewItem=1)</c> on the repository
/// <c>CSWSCreature::GetItemRepository(this, 1)</c> 0x004EF770 returns — and for a live party
/// member that IS the shared party pool, not the creature's own <c>+0xA30</c> backpack. So by
/// the time the caller looks:
///
/// <list type="number">
/// <item>the item is ALREADY linked into the party pool, and</item>
/// <item>if the pool already held a stack the engine's own merge predicate 0x00553CF0 accepts
/// (docs/research/item-identity.md §14), <c>AddItem</c>'s <c>wantMerge=1</c> branch merged into
/// THAT stack and wrote its pointer back through <c>ppItem</c> — so the "created" object id
/// handed back is a PRE-EXISTING pool item carrying its own standing units.</item>
/// </list>
///
/// <b>The two damages that caused.</b> <c>AddItemToPartyPool</c> then (a) called
/// <c>ApplyItemIdentity(..., stackSize: count, ...)</c>, which overwrites the stack size
/// outright — so a merge into a stack of <c>k</c> units, asked for <c>n</c>, left <c>n</c>
/// units where <c>k+n</c> belonged, silently destroying <c>k</c> — and (b) linked that same,
/// already-linked item into the pool's id array a SECOND time (the unlink that was supposed to
/// precede it targeted <c>creature+0xA30</c>, a repository the item was never in, so it was a
/// no-op). A repository array holding one id N times reports that id's stack N times to every
/// aggregate scan.
///
/// The pool total a scan reports for such a key is therefore <c>N × S</c>, not a sum — a
/// PRODUCT of "how many deposits have ever been made" and "how many units the last one asked
/// for". That reproduces every number in §14.3's live trace exactly (a client at 1 unit:
/// <c>+1</c> → N=2,S=1 → 2; <c>+2</c> → N=3,S=2 → 6, a raw jump of 4 for an add of 2;
/// <c>+1</c> → N=4,S=1 → 4, a raw jump of −2 for a deposit of +1, published as a REMOVE of 2
/// and applied to the host's canonical engine). It also explains why a run of same-size
/// <c>count=1</c> deposits looked perfectly correct (N×1 grows by exactly 1 each time), which
/// is how §14.1's own live pass signed off on the channel.
///
/// This rule carries the decision so it is testable without a game
/// (<c>PartyPoolDepositRuleTests</c> replays §14.3's trace against both the old behaviour and
/// this one).
/// </summary>
internal static class PartyPoolDepositRule
{
    /// <summary>What the caller must still do to a freshly-created deposit.</summary>
    /// <param name="TargetStackSize">The stack size the deposited item must end up carrying.
    /// For a merge that is the stack's standing units PLUS the requested count — never the
    /// requested count alone.</param>
    /// <param name="KeepEngineIdentity">True when the item is a PRE-EXISTING stack the engine
    /// merged into: its charges/upgrades/flags belong to units that were already there and must
    /// not be rewritten from the request. (They already match: the engine's own merge predicate
    /// compares upgrades, charges and the Stolen bit, so a merge only happens when they do.)</param>
    /// <param name="NeedsLink">True only when the engine did NOT put the body in the target
    /// repository itself — the caller has to unlink it from wherever it landed and link it in.</param>
    /// <param name="DuplicateLinksToDrop">How many extra array links of this same id to unlink,
    /// so a pool a previous build double-linked heals instead of reporting a multiple forever.</param>
    internal readonly record struct Deposit(
        uint TargetStackSize,
        bool KeepEngineIdentity,
        bool NeedsLink,
        int DuplicateLinksToDrop);

    /// <param name="wasAlreadyLinked">Was this object id in the target repository BEFORE the
    /// create? That — not any stack arithmetic — is what identifies a merge, because it is the
    /// only observation that cannot be confused by an engine that merges some other amount than
    /// the caller asked for.</param>
    /// <param name="stackBefore">That pre-existing stack's own size, from the same snapshot.</param>
    /// <param name="linksNow">How many array slots hold this id right now, after the create.</param>
    /// <param name="count">Units requested.</param>
    internal static Deposit ForCreatedItem(bool wasAlreadyLinked, uint stackBefore, int linksNow, int count)
    {
        uint units = count < 1 ? 1u : (uint)count;

        if (linksNow <= 0)
        {
            // The engine filed the body somewhere other than the target repository — the
            // creature was not a live party member, so AcquireItem used its own individual
            // backpack. This is the case the original code was written for, and it is still
            // the caller's job to move it.
            return new Deposit(units, KeepEngineIdentity: false, NeedsLink: true, DuplicateLinksToDrop: 0);
        }

        return new Deposit(
            TargetStackSize: (wasAlreadyLinked ? stackBefore : 0) + units,
            KeepEngineIdentity: wasAlreadyLinked,
            NeedsLink: false,
            DuplicateLinksToDrop: linksNow - 1);
    }
}
