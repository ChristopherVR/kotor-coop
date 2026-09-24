using System.Text;

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    private const nint MineClientTriggerVtable = 0x00754440;
    private const nint MineClientCreatureVtable = 0x0074F5F8;
    private const nint MineServerCreatureVtable = 0x007470C8;
    private const nint MineClientAppVtable = 0x0074E6D8;
    private const int MineActionObjectIdOffset = 0x04;
    private const int MineActionAreaIdOffset = 0x08C;
    private const int MineActionTriggerGateOffset = 0x2BC;
    private const int MineActionTriggerDisarmableOffset = 0x2C4;
    private const int MineActionTriggerPlacedByPartyOffset = 0x314;

    /// <summary>CSWSObject position (Vector, three floats), shared by every server object
    /// including a trigger and a creature. Read for the range gate, mines.md §14.8.</summary>
    private const int MineActionPositionOffset = 0x090;

    /// <summary>CSWSObject object-type byte (mines.md §14.10: <c>CSWSObject::RunActions
    /// 0x0057F4A0</c> reads this once per call and refuses to dispatch a queued Disarm/Recover
    /// action at all unless it equals <c>5</c> — read-only diagnostic, never enforced here.</summary>
    private const int MineActionActorTypeByteOffset = 0x008;

    /// <summary>CSWSCreature's own commandable flag (<c>Odyssey.ObjCommandable</c>'s offset,
    /// duplicated here because this file only reads it for the diagnostic string). Zero makes
    /// both <c>CSWSCreature::UseSkill 0x004FBE40</c> and <c>AddMineActions 0x004F9DA0</c>
    /// silently return without queuing anything (mines.md §14.10).</summary>
    private const int MineActionActorCommandableOffset = 0x0E8;

    /// <summary>The two-phase per-tick marker <c>AIActionDisarmMine</c>/<c>AIActionRecoverMine</c>
    /// write on the acting creature (mines.md §14.8/§14.10): zero on the first tick (which only
    /// starts the timing bar and returns), non-zero once the roll/fast-path tick has run.</summary>
    private const int MineActionActorTickOffset = 0x97C;

    /// <summary>CSWSCreature's own stats pointer (mines.md §14.10). Read-only: this is the same
    /// field <c>AddMineActions 0x004F9DA0</c> and <c>GetCanUseSkill 0x005AF880</c> dereference
    /// as <c>[creature+0xA74]</c>.</summary>
    private const int MineActionActorStatsOffset = 0xA74;

    /// <summary>Signed per-skill rank byte array on <c>CSWSCreatureStats</c> (mines.md §14.10),
    /// confirmed against <c>CSWSCreatureStats::GetSkillRank 0x005AA570</c>
    /// (<c>movsx edi, byte ptr [ranks+skillIndex]</c>, where ranks = the <c>byte*</c> at stats+0x168). Index with a 0-based skills.2da
    /// row (Demolitions = row 1).</summary>
    private const int MineActionSkillRankArrayOffset = 0x168;

    /// <summary>The skills.2da row for Demolitions (<c>skills.2da</c> row 1, `SKILL_DEMOLITIONS`).
    /// `AIActionRecoverMine`/`AIActionDisarmMine`'s own skill-check pipeline uses this same row
    /// for both mine actions (mines.md §14.10).</summary>
    private const int DemolitionsSkillIndex = 1;

    /// <summary>The loaded ruleset singleton (mines.md §14.10). `GetCanUseSkill 0x005AF880`
    /// reads `[0x7A3A28]` directly for both the skill-definition table and the per-class
    /// skill-availability table.</summary>
    private const nint MineActionRulesetGlobal = 0x7A3A28;

    /// <summary>Skill-definition array base, `ruleset+0xB4` (mines.md §14.10). One 0x20-byte
    /// record per skills.2da row, indexed directly by skill id.</summary>
    private const int MineActionSkillDefArrayOffset = 0xB4;

    private const int MineActionSkillDefStride = 0x20;

    /// <summary>Flags dword inside each skill-definition record (mines.md §14.10). Bit 2 is
    /// written straight from skills.2da's `Untrained` column by the ruleset loader (confirmed by
    /// disassembling the writer at `0x005507A6`-`0x005507B3`, reached from the `"Untrained"`
    /// string literal xref): `flags = (flags &amp; ~2) | (Untrained ? 2 : 0)`. When this bit is
    /// CLEAR (`Untrained=0` — Demolitions, Stealth and Security in retail `skills.2da`),
    /// `GetCanUseSkill` requires the creature's OWN rank in that skill to be strictly greater than
    /// 0 (`0x005AF924`-`0x005AF93A`) — a class-skill/creator/party fast path never enters into
    /// this check, because it runs inside `AddMineActions`, strictly before the action is ever
    /// queued, let alone ticked.</summary>
    private const int MineActionSkillDefFlagsOffset = 0x1C;

    private const int MineActionSkillDefUntrainedBit = 2;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, nint, void> DisableMineFromActionMenuFn =
        (delegate* unmanaged[Thiscall]<nint, int, nint, void>)0x00691900;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, nint, void> RecoverMineFromActionMenuFn =
        (delegate* unmanaged[Thiscall]<nint, int, nint, void>)0x00691950;

    private static ReadOnlySpan<byte> DisableMineEntryBytes =>
    [
        0xA1, 0xFC, 0x39, 0x7A, 0x00, 0x83, 0xEC, 0x0C, 0x56, 0x8B, 0xF1, 0x8B,
        0x48, 0x04, 0xE8, 0xDD, 0xBD, 0xF5, 0xFF, 0x85, 0xC0, 0x74, 0x2C, 0x8B,
        0x56, 0x04, 0x8D, 0x4C, 0x24, 0x04, 0x51, 0x52, 0x6A, 0x00, 0x6A, 0x01,
        0x8B, 0xC8, 0xC7, 0x44, 0x24, 0x14, 0x00, 0x00, 0x00, 0x00, 0xC7, 0x44,
        0x24, 0x18, 0x00, 0x00, 0x00, 0x00, 0xC7, 0x44, 0x24, 0x1C, 0x00, 0x00,
        0x00, 0x00, 0xE8, 0xCD, 0x61, 0xFE, 0xFF, 0x5E, 0x83, 0xC4, 0x0C, 0xC2,
        0x08, 0x00
    ];

    private static ReadOnlySpan<byte> RecoverMineEntryBytes =>
    [
        0x8B, 0x41, 0x04, 0x8B, 0x4C, 0x24, 0x08, 0x50, 0x6A, 0x65, 0x6A, 0x01,
        0xE8, 0x9F, 0xD4, 0xF7, 0xFF, 0xC2, 0x08, 0x00
    ];

    /// <summary>
    /// Sends the game's own disable/recover action for a deterministic GIT mine. This runs
    /// on the frame thread, validates both server and client object identity, then calls the
    /// same client action-menu callback the native UI installs. The skill roll, range check,
    /// destruction, and any recovery reward remain engine-owned.
    /// </summary>
    public static bool TryQueueMineAction(
        uint serverMineId,
        uint localServerPlayerId,
        string expectedModule,
        uint expectedWorldLoadSerial,
        uint currentWorldLoadSerial,
        bool recover,
        out string diagnostic,
        bool partyPlaced = false)
    {
        diagnostic = string.Empty;
        if (string.IsNullOrWhiteSpace(expectedModule)
            || expectedModule.Length > 16
            || expectedModule.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
        {
            diagnostic = "request has no valid source module";
            return false;
        }

        if (expectedWorldLoadSerial != currentWorldLoadSerial)
        {
            diagnostic = $"request belongs to world load {expectedWorldLoadSerial}, current load is {currentWorldLoadSerial}";
            return false;
        }

        byte* currentModuleBytes = stackalloc byte[SharedBlock.ModuleNameLength];
        int currentModuleLength = ReadCurrentModuleName(currentModuleBytes, SharedBlock.ModuleNameLength);
        if (currentModuleLength <= 0 || currentModuleLength >= SharedBlock.ModuleNameLength)
        {
            diagnostic = "current module is unavailable";
            return false;
        }

        string currentModule = Encoding.ASCII.GetString(currentModuleBytes, currentModuleLength);
        string? scopeRefusal = MineActionRule.ScopeRefusal(
            expectedModule, expectedWorldLoadSerial, currentModule, currentWorldLoadSerial);
        if (scopeRefusal is not null)
        {
            diagnostic = scopeRefusal;
            return false;
        }

        // A party-placed mine (placedmineaction) has a dynamic id outside the GIT range; its
        // caller resolved it from a session serial through PartyPlacedMineIdentityMap.
        if (serverMineId == 0 || (!partyPlaced && serverMineId > 0xFFFF))
        {
            diagnostic = "target id is outside the deterministic GIT range 1..0xFFFF";
            return false;
        }

        nint serverTrigger = ResolveServerObject(serverMineId);
        if (!ReadableObject(serverTrigger, 0x318))
        {
            diagnostic = "server mine id does not resolve to a readable object";
            return false;
        }

        bool serverTriggerIsTrigger = *(nint*)serverTrigger == 0x00749A38;
        if (!serverTriggerIsTrigger)
        {
            diagnostic = "target is not a server-side trigger";
            return false;
        }

        if (*(uint*)(serverTrigger + MineActionObjectIdOffset) != serverMineId)
        {
            diagnostic = "server object id does not match the requested mine id";
            return false;
        }

        bool isMine = *(int*)(serverTrigger + MineActionTriggerGateOffset) != 0;
        bool isGit = *(int*)(serverTrigger + MineActionTriggerPlacedByPartyOffset) == 0;
        bool disarmable = *(int*)(serverTrigger + MineActionTriggerDisarmableOffset) != 0;
        uint serverAreaId = *(uint*)(serverTrigger + MineActionAreaIdOffset);

        nint client = GetClient();
        if (!LooksLikeClientApp(client))
        {
            diagnostic = "client application object failed the native class/readability check";
            return false;
        }

        uint clientMineId = ServerToClientObjectIdFn(client, serverMineId);
        nint clientTrigger = clientMineId is 0 or ObjectInvalid
            ? 0
            : ClientGetGameObjectFn(client, clientMineId);
        bool clientTargetMatches = ReadableObject(clientTrigger, 0x14C)
            && *(nint*)clientTrigger == MineClientTriggerVtable
            && *(uint*)(clientTrigger + MineActionObjectIdOffset) == clientMineId;

        nint clientActor = GetClientPlayerCreatureFn(client);
        bool actorPointerValid = ReadableObject(clientActor, 0x44C)
            && *(nint*)clientActor == MineClientCreatureVtable;
        uint clientActorId = actorPointerValid ? *(uint*)(clientActor + MineActionObjectIdOffset) : 0;

        nint serverActor = localServerPlayerId is 0 or ObjectInvalid
            ? 0
            : ResolveServerObject(localServerPlayerId);
        bool serverActorValid = ReadableObject(serverActor, 0xAC8)
            && *(nint*)serverActor == MineServerCreatureVtable
            && *(uint*)(serverActor + MineActionObjectIdOffset) == localServerPlayerId;
        bool targetIsCurrentArea = serverActorValid
            && *(uint*)(serverActor + MineActionAreaIdOffset) == serverAreaId;
        uint clientActorIdExpected = serverActorValid
            ? ServerToClientObjectIdFn(client, localServerPlayerId)
            : 0;
        bool localActorMatches = actorPointerValid
            && serverActorValid
            && clientActorIdExpected != 0
            && clientActorId == clientActorIdExpected;

        // VERIFIED(static) mines.md §14.8: the retail action's own range gate is a fixed 1.0-unit
        // sphere around the trigger's position (CSWSTrigger::InTrigger 0x0058CE40), the same test
        // OnEnterMine uses for a walk-on. Checked here so a too-far actor gets an honest refusal
        // instead of a "queued" response the native side silently refuses every tick.
        bool actorWithinRange = false;
        if (serverActorValid)
        {
            float tx = *(float*)(serverTrigger + MineActionPositionOffset);
            float ty = *(float*)(serverTrigger + MineActionPositionOffset + 4);
            float tz = *(float*)(serverTrigger + MineActionPositionOffset + 8);
            float ax = *(float*)(serverActor + MineActionPositionOffset);
            float ay = *(float*)(serverActor + MineActionPositionOffset + 4);
            float az = *(float*)(serverActor + MineActionPositionOffset + 8);
            actorWithinRange = MineActionRule.WithinRecoverDisarmRange(ax - tx, ay - ty, az - tz);
        }

        MineActionRule.Facts facts = new(
            ServerTargetFound: serverTrigger != 0,
            ServerTargetIsTrigger: serverTriggerIsTrigger,
            TargetIsMine: isMine,
            TargetIsGit: partyPlaced ? !isGit : isGit,
            TargetIsCurrentArea: targetIsCurrentArea,
            ActorWithinRange: actorWithinRange,
            Disarmable: disarmable,
            ClientTargetMatches: clientTargetMatches,
            LocalActorMatches: localActorMatches);
        string? refusal = MineActionRule.Refusal(facts);
        if (refusal is not null)
        {
            diagnostic = refusal;
            return false;
        }

        if (!VerifyMineActionBytes(recover, out diagnostic))
        {
            return false;
        }

        // mines.md §14.10: the retail message pump gives this call NO way to observe whether
        // AddMineActions/RunActions actually queued or ticked the action - CSWSCreature::UseSkill
        // (0x004FBE40) returns 1 unconditionally regardless of what its callees decide, so a
        // "queued" response here has never meant "will do something." These reads are read-only
        // (no engine state is written) and exist only so the next live attempt can see which
        // precondition was false instead of guessing from a silent 20-second timeout.
        string actorState = DescribeActorGateState();

        if (recover)
        {
            RecoverMineFromActionMenuFn(clientTrigger, 0, clientActor);
            diagnostic = $"queued retail recover-skill request for {(partyPlaced ? "party-placed" : "GIT")} mine 0x{serverMineId:X}; skill roll decides success{actorState}";
        }
        else
        {
            DisableMineFromActionMenuFn(clientTrigger, 0, 0);
            diagnostic = $"queued retail disable-skill request for {(partyPlaced ? "party-placed" : "GIT")} mine 0x{serverMineId:X}; skill roll decides success{actorState}";
        }

        return true;

        string DescribeActorGateState()
        {
            if (!serverActorValid)
            {
                return string.Empty;
            }

            int commandable = *(int*)(serverActor + MineActionActorCommandableOffset);
            byte actorType = *(byte*)(serverActor + MineActionActorTypeByteOffset);
            int tick = *(int*)(serverActor + MineActionActorTickOffset);
            bool lockOwned = PlayerCommandableLock.IsOwned;

            // mines.md §14.10: this is exactly the object-id field PerformSkill/UseSkill reads
            // off the CLIENT trigger ("mov eax,[ecx+4]" on the trigger passed as `this`) and
            // sends as the message's target id - a direct read, no native call, no message
            // decode replayed. It is expected to equal clientMineId (already checked above via
            // ClientTargetMatches); surfaced here so a mismatch is visible without cross-
            // referencing two log lines.
            uint wireTargetId = *(uint*)(clientTrigger + MineActionObjectIdOffset);

            string demolitions = DescribeDemolitionsGate();

            return $" [actor gate: commandable={commandable}, type={actorType}(expect 5), "
                + $"tick={tick}, commandableLockOwned={lockOwned}, wireTargetId=0x{wireTargetId:X}, "
                + $"trapDisarmable={disarmable}{demolitions}]";
        }

        string DescribeDemolitionsGate()
        {
            // mines.md §14.10: AddMineActions 0x004F9DA0 calls GetCanUseSkill(stats, 1) for
            // every recover/disarm attempt, BEFORE the action is ever queued - the creator/party
            // fast path inside AIActionRecoverMine/AIActionDisarmMine never gets a chance to run
            // if this gate refuses. Replicated here as a direct, call-free read of the same two
            // structures GetCanUseSkill 0x005AF880 itself reads, not an emulation of its full
            // logic (the class-skill-list branch for an Untrained=1 skill is not replicated;
            // Demolitions is Untrained=0 in retail skills.2da, so only the rank>0 branch applies).
            // CSWSCreatureStats+0x168 is `byte* skill_ranks` (tools/q.py offsets), a POINTER to
            // the rank array, not the array itself. Live 2026-09-25 the rig Soldier's field read
            // 0x1812FC88 and the ranks behind it [0,0,0,0,0,0,0,4].
            nint stats = *(nint*)(serverActor + MineActionActorStatsOffset);
            if (!ReadableObject(stats, MineActionSkillRankArrayOffset + sizeof(nint)))
            {
                return ", demolitionsRank=<stats unreadable>";
            }

            nint ranks = *(nint*)(stats + MineActionSkillRankArrayOffset);
            if (!ReadableObject(ranks, DemolitionsSkillIndex + 1))
            {
                return ", demolitionsRank=<rank array unreadable>";
            }

            sbyte rank = *(sbyte*)(ranks + DemolitionsSkillIndex);

            nint ruleset = *(nint*)MineActionRulesetGlobal;
            if (!ReadableObject(ruleset, MineActionSkillDefArrayOffset + 4))
            {
                return $", demolitionsRank={rank}, untrainedAllowed=<ruleset unreadable>";
            }

            nint skillDefArray = *(nint*)(ruleset + MineActionSkillDefArrayOffset);
            nint skillDef = skillDefArray + (DemolitionsSkillIndex * MineActionSkillDefStride);
            if (!ReadableObject(skillDef, MineActionSkillDefFlagsOffset + 4))
            {
                return $", demolitionsRank={rank}, untrainedAllowed=<skill table unreadable>";
            }

            bool untrainedAllowed = (*(int*)(skillDef + MineActionSkillDefFlagsOffset) & MineActionSkillDefUntrainedBit) != 0;

            // Retail skills.2da has Demolitions at Untrained=0, so untrainedAllowed should read
            // false on an unmodded install and GetCanUseSkill's rank>0 branch is the one that
            // actually runs (verdict below is exact for that branch). If untrainedAllowed reads
            // true here (a modded skills.2da), GetCanUseSkill instead walks the actor's classes
            // against a per-class skill-availability table this diagnostic does not replicate -
            // the verdict is left unknown rather than guessed.
            string verdict = untrainedAllowed
                ? "unknown (Untrained=1 path uses a class-skill-list check not replicated here)"
                : (rank > 0).ToString();

            return $", demolitionsRank={rank}, untrainedAllowed={untrainedAllowed}, canUseDemolitions={verdict}";
        }
    }

    private static bool ReadableObject(nint pointer, nuint size) =>
        pointer != 0 && NativeMethods.IsReadable(pointer, size);

    private static bool LooksLikeClientApp(nint client)
    {
        if (!ReadableObject(client, 8) || *(nint*)client != MineClientAppVtable)
        {
            return false;
        }

        nint clientInternal = *(nint*)(client + 4);
        return ReadableObject(clientInternal, 0x4D0);
    }

    private static bool VerifyMineActionBytes(bool recover, out string diagnostic)
    {
        ReadOnlySpan<byte> expected = recover ? RecoverMineEntryBytes : DisableMineEntryBytes;
        byte* actual = (byte*)(recover ? (nint)0x00691950 : (nint)0x00691900);
        if (!NativeMethods.IsReadable((nint)actual, (nuint)expected.Length))
        {
            diagnostic = "native mine action entry is unreadable";
            return false;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            if (actual[i] != expected[i])
            {
                diagnostic = $"native mine action entry mismatch at +0x{i:X}";
                return false;
            }
        }

        diagnostic = string.Empty;
        return true;
    }
}
