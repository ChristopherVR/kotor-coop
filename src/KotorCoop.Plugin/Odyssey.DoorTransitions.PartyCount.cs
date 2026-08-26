// Odyssey.DoorTransitions.PartyCount.cs - part 5: party member counting/gathering
// helpers used by the follow-recovery logic above.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary>
    /// CClientExoApp::GetCreatureByGameObjectID 0x005ED5B0 — <c>__thiscall</c> on the CLIENT
    /// (<see cref="GetClient"/>), one stack arg (a CLIENT-side object id), returns the creature
    /// pointer or 0. This is the SAME call <c>CSWParty::Update</c> itself makes (round 73 disasm,
    /// see <see cref="PartyUpdateFn"/>) to resolve the leader record's own stored id
    /// (<c>party+0x24</c>) into a creature pointer before reading its position.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, nint> GetCreatureByClientIdFn =
        (delegate* unmanaged[Thiscall]<nint, uint, nint>)0x005ED5B0;

    /// <summary>
    /// ROUND 73 DIAGNOSTIC: direct visibility into the PARTY-LEVEL fields
    /// <c>CSWParty::Update</c> itself reads and writes, to tell "the function isn't running"
    /// apart from "it runs but its own leader lookup or position offset isn't what this round's
    /// disasm assumed": <c>party+0x24</c> (the leader record's own stored CLIENT object id),
    /// <c>party+0x2B0..0x2B8</c> (the reference position the function refreshes from the
    /// leader), and <c>party+0x2BC</c> (the elapsed-time accumulator its first three
    /// instructions maintain — a monotonically increasing value while
    /// <see cref="PumpPartyUpdate"/> is genuinely being called each frame, regardless of
    /// whether anything else in the function's body does what this round's disasm expects).
    /// Also resolves the leader id through <see cref="GetCreatureByClientIdFn"/> — the SAME
    /// call the function itself makes — and reads that creature's <c>+0x24/+0x28/+0x2C</c>,
    /// the exact offsets <c>CSWParty::Update</c> subtracts from <c>party+0x2B0..0x2B8</c> (NOT
    /// necessarily the same as <c>CSWSObject</c>'s usual <c>+0x90</c> world-position field —
    /// this round's live data is what settles whether they agree).
    /// </summary>
    public static bool TryGetPartyReferenceState(out PartyRefState state)
    {
        state = default;

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        state.LeaderRecordId = *(uint*)(party + 0x24);
        state.ReferencePos = *(Vec3*)(party + 0x2b0);
        state.TimeAccumulator = *(float*)(party + 0x2bc);

        nint leaderCreature = GetCreatureByClientIdFn(client, state.LeaderRecordId);
        state.LeaderCreaturePtr = leaderCreature;
        if (leaderCreature != 0)
        {
            state.LeaderFieldPos = *(Vec3*)(leaderCreature + 0x24);
        }

        state.Valid = true;
        return true;
    }

    public struct PartyRefState
    {
        public bool Valid;
        public uint LeaderRecordId;
        public Vec3 ReferencePos;
        public float TimeAccumulator;
        public nint LeaderCreaturePtr;
        public Vec3 LeaderFieldPos;
    }

    /// <summary>
    /// CServerExoApp::GetPartyTable 0x004AEE70 — <c>mov eax,[ecx+4]; add eax,0x1B770; ret</c>.
    /// A bare <c>ret</c>: no arguments, no dereference past <c>this</c>. The table is EMBEDDED
    /// in CServerExoAppInternal, so this can never be null while the server exists.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetPartyTableFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004AEE70;

    /// <summary>
    /// CSWPartyTable::PositionParty 0x00565B00 — <c>__thiscall</c>, NO arguments (bare
    /// <c>ret</c> at 0x00565D1C). The engine's own "put the party where it belongs": for each
    /// member it computes a formation offset around the player
    /// (<c>CSWParty::GetWorldFormationPosition</c> data at party+0xD4), snaps it to something
    /// walkable with <c>CServerExoApp::ComputeSafeLocation 0x004AEB60</c> and a 10 m search
    /// radius, then writes it with <c>CSWSObject::SetPosition 0x004CD1A0</c> and
    /// <c>SetOrientation 0x004CD170</c>. On the way in it calls
    /// <c>CSWParty::ResetAllFollowData 0x00637890</c> with the player's position; on the way
    /// out, <c>UnstealthParty(0)</c> and it clears <c>pt_solomode</c> (+0x190).
    ///
    /// Its ONE caller in the whole binary is
    /// <c>CServerExoAppInternal::MovePlayerToArea 0x004B3D10</c> at 0x004B3E9E — i.e. this is
    /// exactly the party collection a module transition performs, which is why it is the right
    /// call for "gather the party to me" and not a reimplementation of one.
    ///
    /// ⚠ It reads the player creature unconditionally (<c>GetPlayerCreatureId</c> →
    /// <c>GetGameObject</c> → <c>[creature+0x9C]</c>), so it must never be called without a
    /// player, and it reads <c>CClientExoApp::GetSWParty</c>, so it must never be called
    /// without a party object either. <see cref="GatherParty"/> checks both.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> PositionPartyFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x00565B00;

    /// <summary>
    /// CSWPartyTable + this = a one-shot "skip the positioning" flag. PositionParty tests it
    /// at 0x00565B6E and, when it is non-zero, jumps STRAIGHT to the exit — which clears it
    /// and does nothing else. So a call made while it is set positions nobody and the NEXT
    /// call works. <see cref="GatherParty"/> reads it first rather than discovering that by
    /// having its first attempt silently do nothing.
    /// </summary>
    private const int PartyTableSkipPositioning = 0x10C;

    /// <summary>CSWPartyTable + 0 = how many members the party holds; +4 begins their ids.
    /// Read out of PositionParty's own loop bound (<c>cmp [ebx], ebp; jle</c> at 0x00565B7A
    /// and <c>lea ebp,[ebx+4]</c> at 0x00565BAC).</summary>
    private const int PartyTableMemberCount = 0x00;

    /// <summary>How many members the party table currently holds, or 0.</summary>
    public static int GetPartyMemberCount()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint table = GetPartyTableFn(server);
        if (table == 0)
        {
            return 0;
        }

        int count = *(int*)(table + PartyTableMemberCount);
        return count is > 0 and <= 16 ? count : 0;
    }

    /// <summary>
    /// Same read as <see cref="GetPartyMemberCount"/>, but distinguishes "read 0 because the
    /// table legitimately holds no companions" from "read 0 because there is no server/table to
    /// ask" (main menu, mid-transition). <see cref="TryClampClientPartyCount"/> needs that
    /// distinction — it must never clamp CSWParty toward a value that was really "unknown".
    /// </summary>
    public static bool TryGetPartyTableMemberCount(out int count)
    {
        count = 0;

        nint server = GetServer();
        if (server == 0)
        {
            return false;
        }

        nint table = GetPartyTableFn(server);
        if (table == 0)
        {
            return false;
        }

        int raw = *(int*)(table + PartyTableMemberCount);
        if (raw is < 0 or > 16)
        {
            return false;
        }

        count = raw;
        return true;
    }

    /// <summary>
    /// CSWParty (the CLIENT-SIDE struct <see cref="GetSWPartyFn"/> resolves — <c>[client+4]</c>
    /// then <c>+0x270</c> — a DIFFERENT structure from CSWPartyTable/<see cref="GetPartyTableFn"/>,
    /// which lives on the server side at <c>CServerExoAppInternal+0x1B770</c>) + this = its own
    /// member count. Read out of <c>CSWParty::IsPartyTogether</c>'s own loop bound
    /// (<c>mov eax,[edi]; test eax,eax; jle 0x635449</c> at 0x006353A8/0x006353B6, where
    /// <c>edi</c> is the <c>this</c> IsPartyTogether was thiscall'd with — i.e. exactly the
    /// pointer <see cref="GetSWPartyFn"/> returns). A non-zero read here means the loop runs and
    /// each surviving entry's position is diffed against the player's — see
    /// <see cref="IsPartyTogether"/>'s own doc comment for what a failing entry does.
    ///
    /// Expected to move in step with CSWPartyTable's own count — <c>RemovePartyMember</c>'s VM
    /// handler at 0x00541C00 calls back into THIS struct (0x00541C64/0x00541C6C/0x00541C85) on
    /// every table-side remove that resolves a live object — but measured live to diverge on a
    /// co-op client: CSWParty kept a stale, higher count after CSWPartyTable's own read 0
    /// (docs/research/party-reconciliation.md, transition-smoothness.md §8.5). That divergence
    /// is exactly what makes a real door refuse every client-side crossing, including one with
    /// no real companions at all.
    /// </summary>
    private const int SwPartyMemberCount = 0x00;

    /// <summary>CSWParty's own member count, or -1 when there is no client or no party object to
    /// ask (see <see cref="SwPartyMemberCount"/>).</summary>
    public static int GetClientPartyMemberCount()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return -1;
        }

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return -1;
        }

        return *(int*)(party + SwPartyMemberCount);
    }

    /// <summary>
    /// Clamps CSWParty's own member count DOWN to <paramref name="maxCount"/> when it currently
    /// reads higher. NEVER raises it — this only removes staleness the engine's own remove path
    /// was supposed to clear and measured live not to (<see cref="SwPartyMemberCount"/>); it can
    /// never invent a member <see cref="IsPartyTogether"/> would then check a garbage position
    /// for. Refuses to touch a count outside 0..16 — a struct that does not look like a member
    /// count is not one this was verified to understand.
    ///
    /// MAIN THREAD ONLY (a raw field write, same rule as every other engine poke in this file).
    /// </summary>
    public static bool TryClampClientPartyCount(int maxCount, out int before)
    {
        before = -1;

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        int* countPtr = (int*)(party + SwPartyMemberCount);
        before = *countPtr;

        if (before <= maxCount || before is < 0 or > 16)
        {
            return false;
        }

        *countPtr = maxCount;
        return true;
    }

    /// <summary>
    /// Teleports every party companion into formation around the local player, through the
    /// engine's own <c>PositionParty</c> — the same call a module transition makes.
    ///
    /// This exists for one reason. A KOTOR door transition is gated on
    /// <see cref="IsPartyTogether"/> (0x0058C228), and a staging verb that moves only the PC
    /// leaves the companions where they were and the gate then cancels the transition into
    /// <c>k_trg_transfail</c> — silently. Moving the party with the engine's own routine gets
    /// the walkmesh snap, the formation and the follow-data reset for free, which is exactly
    /// what a hand-rolled position write would get wrong.
    ///
    /// Returns false, without calling anything, when the preconditions PositionParty
    /// dereferences are not met.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool GatherParty(out int members, out bool skipFlagWasSet)
    {
        members = 0;
        skipFlagWasSet = false;

        nint server = GetServer();
        if (server == 0 || GetPlayerCreatureFn(server) == 0)
        {
            return false;
        }

        nint client = GetClient();
        if (client == 0 || GetSWPartyFn(client) == 0)
        {
            return false;
        }

        nint table = GetPartyTableFn(server);
        if (table == 0)
        {
            return false;
        }

        members = *(int*)(table + PartyTableMemberCount);
        if (members is < 0 or > 16)
        {
            return false;
        }

        skipFlagWasSet = *(int*)(table + PartyTableSkipPositioning) != 0;

        PositionPartyFn(table);

        // The flag was set, so that call only cleared it and moved nobody (0x00565B6E). The
        // second call is the one that positions the party — deterministic, not a retry: the
        // branch is read out of the function, and the flag is provably clear now.
        if (skipFlagWasSet)
        {
            PositionPartyFn(table);
        }

        return true;
    }

    /// <summary>True when this door is a module transition, by the engine's own definition.</summary>

}
