// Odyssey.Combat.AttackClient.cs - the CLIENT-driven route for `attack` (docs/research/
// combat-participation.md §16), added alongside Odyssey.Spells.Client.cs after live testing
// showed a player-controlled creature's raw VM ActionAttackObject (Odyssey.AttackTarget) lands
// exactly one hit and then goes silent: the queued action never sustains its own combat round
// the way a real click's SUSTAINED per-tick client input does. This calls the exact function a
// real click-to-attack uses - CSWCCreature::PerformAttack (0x0060EEE0) - which sends a
// CSWCMessage::SendPlayerToServerInput_Attack network message that HandlePlayerToServerInputMessage
// receives and re-dispatches every time the client re-sends it (a real click keeps re-issuing
// this as long as the mouse is held/the target is still being attacked; nothing here changes
// that - see the live recipe in combat-participation.md §16 for re-issuing this verb on a
// timer rather than expecting one call to sustain a fight).

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    private const int AttackClientObjectIdOffset = 0x04;
    private const nint AttackClientCreatureVtable = 0x0074F5F8;

    /// <summary>
    /// CSWCCreature::PerformAttack 0x0060EEE0, __stdcall, one stack arg (ret 4): the target
    /// object id. No `this`/actor parameter, same as <see cref="PerformCastSpellFn"/> - it
    /// always orders the client's own currently-controlled creature to attack. VERIFIED as the
    /// entire function body (33 bytes, 0x0060EEE0-0x0060EF01): it resolves the message channel
    /// (<c>GetSWCMessage 0x005ED6F0</c>) and, if one exists, calls
    /// <c>CSWCMessage::SendPlayerToServerInput_Attack(channel, 0, targetId)</c> unconditionally.
    /// </summary>
    private static readonly delegate* unmanaged[Stdcall]<uint, int> PerformAttackFn =
        (delegate* unmanaged[Stdcall]<uint, int>)0x0060EEE0;

    private static ReadOnlySpan<byte> PerformAttackBytes =>
    [
        0xA1, 0xFC, 0x39, 0x7A, 0x00, 0x8B, 0x48, 0x04, 0xE8, 0x03, 0xE8, 0xFD, 0xFF, 0x85,
        0xC0, 0x74, 0x0E, 0x8B, 0x4C, 0x24, 0x04, 0x6A, 0x00, 0x51, 0x8B, 0xC8, 0xE8, 0x91,
        0x8B, 0x06, 0x00, 0xC2, 0x04,
    ];

    private static bool VerifyPerformAttackBytes()
    {
        const nint address = 0x0060EEE0;
        if (!NativeMethods.IsReadable(address, (nuint)PerformAttackBytes.Length))
        {
            return false;
        }

        byte* actual = (byte*)address;
        for (int i = 0; i < PerformAttackBytes.Length; i++)
        {
            if (actual[i] != PerformAttackBytes[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Orders the local player to attack <paramref name="targetObjectId"/> through the SAME
    /// client entry point a real click-to-attack uses, rather than the raw VM push
    /// <see cref="AttackTarget"/> uses. <paramref name="attackerObjectId"/> MUST be the local
    /// player - <c>PerformAttack</c> has no actor argument, so this refuses rather than
    /// silently acting on the wrong creature when it is not.
    ///
    /// Returns false with a diagnostic on any precondition failure (no client app, attacker is
    /// not the local player, target does not resolve, code bytes do not match the verified
    /// build) - every one of those is a reason to fall back to the VM route. On success the
    /// native call was made; like PerformCastSpell/PerformSkill, PerformAttack gives no
    /// reliable success signal of its own, so a true return means "sent", not "landed", and a
    /// real fight needs this re-issued periodically the same way a held-down mouse click would
    /// (see the live recipe).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool AttackTargetViaClient(uint attackerObjectId, uint targetObjectId, out string diagnostic)
    {
        diagnostic = string.Empty;

        if (attackerObjectId == 0 || targetObjectId == 0)
        {
            diagnostic = "invalid attacker or target id";
            return false;
        }

        nint client = GetClient();
        if (client == 0)
        {
            diagnostic = "no client application object (menu, or not yet in-game)";
            return false;
        }

        nint clientActor = GetClientPlayerCreatureFn(client);
        if (!ReadableAttackObject(clientActor, AttackClientObjectIdOffset + 4)
            || *(nint*)clientActor != AttackClientCreatureVtable)
        {
            diagnostic = "client has no controlled creature";
            return false;
        }

        uint expectedClientAttackerId = ServerToClientObjectIdFn(client, attackerObjectId);
        uint clientActorId = *(uint*)(clientActor + AttackClientObjectIdOffset);
        if (expectedClientAttackerId == 0 || expectedClientAttackerId == ObjectInvalid
            || clientActorId != expectedClientAttackerId)
        {
            diagnostic = "attacker is not the local player's own controlled creature "
                + "(PerformAttack has no actor argument, it can only order the client's own creature)";
            return false;
        }

        uint clientTargetId = ServerToClientObjectIdFn(client, targetObjectId);
        if (clientTargetId == 0)
        {
            diagnostic = "target does not resolve to a client-side object id";
            return false;
        }

        if (!VerifyPerformAttackBytes())
        {
            diagnostic = "PerformAttack bytes do not match the verified build";
            return false;
        }

        PerformAttackFn(clientTargetId);
        diagnostic = $"sent client-side attack request for client id 0x{clientTargetId:X} "
            + "(same route a real click-to-attack uses; re-issue periodically to sustain the fight)";
        return true;
    }

    private static bool ReadableAttackObject(nint pointer, nuint size) =>
        pointer != 0 && NativeMethods.IsReadable(pointer, size);
}
