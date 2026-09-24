// Odyssey.Spells.Client.cs - the CLIENT-driven route for `cast` (docs/research/effect-sync.md
// §14.11), added after §14.9's live evidence showed the server-side VM route
// (Odyssey.CastSpellAtObject, ExecuteCommandCastSpell) never spends Force points for a
// player-controlled creature even while it is actively fighting: every static path this repo
// traced into a REAL promotion (AIActionCombat, StartCombatRound) needs state the raw VM push
// never establishes. This file instead calls the exact function the retail radial-menu/action
// bar itself calls - CSWCCreature::PerformCastSpell (0x0060EF10) - which builds and sends a
// CSWCMessage::SendPlayerToServerInput_CastSpell network message that HandlePlayerToServerInputMessage
// (0x005254C0) receives and dispatches through its own subtype table, doing whatever extra
// server-side setup (ActionManager(creature,1) - ComputeAIState + a conditional ClearAllActions -
// confirmed at the dispatch entry, 0x005261AE-0x005261BF) the raw VM push skips entirely.
//
// Same pattern as Odyssey.MineActions.cs's TryQueueMineAction: verify-bytes-or-refuse on the
// called address, translate both the caster and the target through the CLIENT id space
// (ServerToClientObjectIdFn), and confirm the caster IS the client's own controlled creature -
// PerformCastSpell has no actor parameter at all, it always acts on whichever creature the
// LOCAL client currently controls, so casting for anyone else through this route is not
// possible and must be refused rather than silently acting on the wrong creature.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    private const int SpellClientObjectIdOffset = 0x04;
    private const nint SpellClientCreatureVtable = 0x0074F5F8;

    /// <summary>
    /// CSWCCreature::PerformCastSpell 0x0060EF10, __stdcall, 7 stack args (ret 0x1C): the
    /// exact function the retail radial-menu Force-power click and CSWGuiTutorialBox's own
    /// tutorial-mode Force-power demo (the only two call sites in the binary,
    /// <c>tools/xrefs.py 0x60ef10</c>) both call. No `this`/actor parameter - it always
    /// operates on the client's own currently-controlled creature (read internally via the
    /// same app-manager global every other client call in this codebase uses).
    ///
    /// Argument order VERIFIED from CSWGuiTutorialBox::PerformForcePower's own call
    /// (0x006AA2C0-0x006AA341, the only non-degenerate caller): pushes are
    /// (nSpell, targetObjectId, pLocationVector3, metaMagicOrDomainByte, 0, 0, 0), traced by
    /// hand through the reversed-push stdcall convention this build uses everywhere else in
    /// this codebase (last pushed = first stack arg). The location vector is dereferenced
    /// UNCONDITIONALLY inside <c>SendPlayerToServerInput_CastSpell</c> (0x006776A0,
    /// `mov ebx,[esp+0x30]; fld [ebx+8]` with no null check), so a valid pointer to three
    /// floats is required even for an object-targeted cast; the tutorial caller always builds
    /// one on its own stack and zeroes it, matched here.
    /// </summary>
    private static readonly delegate* unmanaged[Stdcall]<int, uint, float*, int, int, int, int, int> PerformCastSpellFn =
        (delegate* unmanaged[Stdcall]<int, uint, float*, int, int, int, int, int>)0x0060EF10;

    /// <summary>Every byte of <c>CSWCCreature::PerformCastSpell</c> (0x0060EF10-0x0060EF4E,
    /// 62 bytes): the whole function body, not just a prologue, since it is short enough to
    /// check outright.</summary>
    private static ReadOnlySpan<byte> PerformCastSpellBytes =>
    [
        0xA1, 0xFC, 0x39, 0x7A, 0x00, 0x8B, 0x48, 0x04, 0xE8, 0xD3, 0xE7, 0xFD, 0xFF, 0x85,
        0xC0, 0x74, 0x2A, 0x8B, 0x4C, 0x24, 0x08, 0x8B, 0x54, 0x24, 0x0C, 0x51, 0x8B, 0x4C,
        0x24, 0x20, 0x52, 0x8B, 0x54, 0x24, 0x20, 0x51, 0x8B, 0x4C, 0x24, 0x20, 0x52, 0x8B,
        0x54, 0x24, 0x20, 0x51, 0x8B, 0x4C, 0x24, 0x18, 0x52, 0x51, 0x8B, 0xC8, 0xE8, 0x55,
        0x87, 0x06, 0x00, 0xC2, 0x1C, 0x00,
    ];

    private static bool VerifyPerformCastSpellBytes()
    {
        const nint address = 0x0060EF10;
        if (!NativeMethods.IsReadable(address, (nuint)PerformCastSpellBytes.Length))
        {
            return false;
        }

        byte* actual = (byte*)address;
        for (int i = 0; i < PerformCastSpellBytes.Length; i++)
        {
            if (actual[i] != PerformCastSpellBytes[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Casts <paramref name="spellId"/> at <paramref name="targetObjectId"/> through the SAME
    /// client entry point a real radial-menu Force-power click uses, rather than the raw VM
    /// push <see cref="CastSpellAtObject"/> uses. <paramref name="casterObjectId"/> MUST be the
    /// local player - <c>PerformCastSpell</c> has no actor argument, so this refuses (does not
    /// silently retarget) when it is not.
    ///
    /// Returns false with a diagnostic on any precondition failure (no client app, caster is
    /// not the local player, target does not resolve, code bytes do not match the verified
    /// build) - every one of those is a reason to fall back to the VM route, not a report that
    /// the cast itself was refused. On success the native call was made; PerformCastSpell gives
    /// no reliable success/failure signal of its own (mines.md §14.10 documents the identical
    /// property for PerformSkill), so a true return means "sent", not "landed".
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool CastSpellAtObjectViaClient(
        uint casterObjectId, int spellId, uint targetObjectId, int metaMagic, out string diagnostic)
    {
        diagnostic = string.Empty;

        if (spellId < 0 || casterObjectId == 0 || targetObjectId == 0)
        {
            diagnostic = "invalid spell id, caster id, or target id";
            return false;
        }

        nint client = GetClient();
        if (client == 0)
        {
            diagnostic = "no client application object (menu, or not yet in-game)";
            return false;
        }

        nint clientActor = GetClientPlayerCreatureFn(client);
        if (!ReadableSpellObject(clientActor, SpellClientObjectIdOffset + 4)
            || *(nint*)clientActor != SpellClientCreatureVtable)
        {
            diagnostic = "client has no controlled creature";
            return false;
        }

        uint expectedClientCasterId = ServerToClientObjectIdFn(client, casterObjectId);
        uint clientActorId = *(uint*)(clientActor + SpellClientObjectIdOffset);
        if (expectedClientCasterId == 0 || expectedClientCasterId == ObjectInvalid
            || clientActorId != expectedClientCasterId)
        {
            diagnostic = "caster is not the local player's own controlled creature "
                + "(PerformCastSpell has no actor argument, it can only cast for the client's own creature)";
            return false;
        }

        uint clientTargetId = ServerToClientObjectIdFn(client, targetObjectId);
        if (clientTargetId == 0)
        {
            diagnostic = "target does not resolve to a client-side object id";
            return false;
        }

        if (!VerifyPerformCastSpellBytes())
        {
            diagnostic = "PerformCastSpell bytes do not match the verified build";
            return false;
        }

        float* location = stackalloc float[3];
        location[0] = 0f;
        location[1] = 0f;
        location[2] = 0f;

        PerformCastSpellFn(spellId, clientTargetId, location, metaMagic, 0, 0, 0);
        diagnostic = $"sent client-side cast request for spell {spellId} at client id 0x{clientTargetId:X} "
            + "(same route the radial-menu click uses; the engine gives no send-time success signal)";
        return true;
    }

    private static bool ReadableSpellObject(nint pointer, nuint size) =>
        pointer != 0 && NativeMethods.IsReadable(pointer, size);
}
