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

        if (recover)
        {
            RecoverMineFromActionMenuFn(clientTrigger, 0, clientActor);
            diagnostic = $"queued retail recover-skill request for {(partyPlaced ? "party-placed" : "GIT")} mine 0x{serverMineId:X}; skill roll decides success";
        }
        else
        {
            DisableMineFromActionMenuFn(clientTrigger, 0, 0);
            diagnostic = $"queued retail disable-skill request for {(partyPlaced ? "party-placed" : "GIT")} mine 0x{serverMineId:X}; skill roll decides success";
        }

        return true;
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
