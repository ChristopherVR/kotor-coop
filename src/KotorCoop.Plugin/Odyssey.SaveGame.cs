namespace KotorCoop.Plugin;

// Asking the game to save itself, through the engine's own quicksave path. Used by the
// `save` console verb and by the new-character join (docs/research/world-adoption.md).
internal static unsafe partial class Odyssey
{
    /// <summary>
    /// <c>CClientExoAppInternal::DoQuickSave 0x005F4B50</c> — the whole retail quicksave, and
    /// the smallest save entry point in the binary.
    ///
    /// ⚠ IT TAKES NO ARGUMENTS AND USES NO <c>this</c>, which is why it can be declared Cdecl
    /// even though the symbol database files it under <c>CClientExoAppInternal</c>. Verified by
    /// disassembly: the first thing it does with <c>ecx</c> is overwrite it
    /// (<c>lea ecx,[esp+0xc]</c> at 0x5F4B6D), it reads nothing off <c>this</c>, and it ends in
    /// a bare <c>ret</c> with no stack immediate. Its body is exactly:
    /// two <c>CExoString("QUICKSAVE")</c> (the literal at 0x74E9E0), then
    /// <c>CClientExoApp::SendSaveGameRequest([[0x7A39FC]+4], 0, &amp;name, &amp;description)</c>
    /// at 0x5ED670 — i.e. the ordinary client -&gt; server save message, which the server
    /// handles in <c>HandlePlayerToServerModuleMessage</c> behind
    /// <c>CSWSPlayer::GetIsAllowedToSave</c> (<c>[player+0x3C] != 0</c>) and lands in
    /// <c>CServerExoAppInternal::SaveGame 0x4B58A0</c>, whose directory name is
    /// <c>sprintf("SAVES:%06d - %s", number, name)</c>.
    ///
    /// So this is not a synthesised save: it is the same call a player's quicksave key makes,
    /// with the same gate and the same output.
    /// </summary>
    private static readonly delegate* unmanaged[Cdecl]<void> DoQuickSaveFn =
        (delegate* unmanaged[Cdecl]<void>)0x005F4B50;

    /// <summary>
    /// Asks the engine to quicksave. Returns false — WITHOUT calling anything — when there is
    /// no world, because the save path walks the connected-player list and there is nothing to
    /// gain from finding out what it does with an empty one.
    ///
    /// ⚠ THE SAVE IS NOT DONE WHEN THIS RETURNS. <c>DoQuickSave</c> only sends the request;
    /// the server writes the directory on a later tick. Every caller polls the filesystem for
    /// the result rather than assuming it — see <see cref="NewCharacterCaptureController"/>.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool RequestQuickSave()
    {
        if (!TryGetQuickSaveReadiness(out _))
        {
            return false;
        }

        DoQuickSaveFn();
        return true;
    }

    // Same message entry used by DoQuickSave, with an allocated dedicated slot instead of
    // slot 0. Disassembly: 5F4B94..5F4BA5 pushes description, name, number then calls
    // 5ED670 with ecx=[[7A39FC]+4]; its 5EEA90 thunk / 6774E0 body return with ret 0xC.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, nint, nint, int> SendSaveGameRequestFn =
        (delegate* unmanaged[Thiscall]<nint, int, nint, nint, int>)0x005ED670;

    public static bool RequestCoopSave(int number)
    {
        // A scripted companion may temporarily be the player while the solo flag or
        // module is settling. Never serialize an automatic character/world capture
        // through that handoff; the party table is the engine's authoritative guard.
        if (number < 2 || IsPartyRosterFrozen() || !TryGetQuickSaveReadiness(out _)) return false;
        nint client = GetClient();
        if (client == 0) return false;
        byte* name = stackalloc byte[6] { (byte)'C', (byte)'o', (byte)'-', (byte)'o', (byte)'p', 0 };
        byte* nameStorage = stackalloc byte[8];
        byte* descriptionStorage = stackalloc byte[8];
        CExoStringCtorFn(nameStorage, name);
        CExoStringCtorFn(descriptionStorage, name);
        try
        {
            return SendSaveGameRequestFn(client, number, (nint)nameStorage, (nint)descriptionStorage) != 0;
        }
        finally
        {
            CExoStringDtorFn(descriptionStorage);
            CExoStringDtorFn(nameStorage);
        }
    }

    /// <summary>
    /// Checks the native preconditions needed by both a new quicksave request and a delayed
    /// save-flush reissue. The epoch alone is insufficient: the server can have a readable
    /// world while the player creature is still detached from its area. Retail's later
    /// <c>StallEventSaveGame</c> path calls <c>GetArea(player)</c> and dereferences the result's
    /// localized name without a null check.
    /// </summary>
    internal static bool TryGetQuickSaveReadiness(out string reason)
    {
        if (!TryGetWorldEpoch(out _))
        {
            reason = "world epoch unreadable";
            return false;
        }

        nint player = GetPlayerCreaturePointer();
        if (player == 0)
        {
            reason = "player creature unresolved";
            return false;
        }

        if (GetAreaFn(player) == 0)
        {
            reason = "player area object unresolved";
            return false;
        }

        if (!HasRenderedBody(player))
        {
            reason = "player body is not rendered";
            return false;
        }

        if (IsAreaTransitionPending())
        {
            reason = "area transition pending";
            return false;
        }

        // StallEventSaveGame (4B36C0..4B36D1) walks CSWParty with the same
        // GetCharacter -> GetServerCreature chain as DoPCAutosave. A client
        // companion left behind by an adopted load, or destroyed during the
        // save flush, must not remain under its member-count bound: retail
        // dereferences the unresolved result before testing the server object.
        // This runs again immediately before a held save is re-issued.
        if (TryBoundClientPartyCountByResolvable(out int before, out int resolved, out string entries))
        {
            PluginHost.Log($"party count: bounded {before} -> {resolved} before saving "
                + $"(entries {entries}; StallEventSaveGame walks the array without a null check)");
        }

        reason = string.Empty;
        return true;
    }

    // ---- the server-side save entry, for the save-time flush's re-issue ------------------

    /// <summary>
    /// <c>CServerExoAppInternal::SaveGame 0x004B58A0</c> — the ONE entry every save reaches
    /// (quicksave, the save panel, both autosaves; the call chain is in
    /// <see cref="SaveFlushHook"/>'s header). <c>thiscall</c> with FOUR stack arguments —
    /// <c>ret 0x10</c> at both exits, 0x4B59EE and 0x4B5C4D; the symbol database's
    /// <c>param_size 12</c> is wrong by one — <c>(int number, CExoString* name,
    /// CSWSPlayer* player, unused)</c>, returning 1 for accepted and 0 for not enough disk
    /// space. It only ARMS the save (directory + stall record); the module is stored on the
    /// next server main-loop pass.
    ///
    /// ⚠ Calling it re-enters <see cref="SaveFlushHook"/>'s splice, by design: the hook
    /// passes a request through while <see cref="SaveFlushRule"/> is in its quiet phase,
    /// which is where the rule already is when the host re-issues.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, nint, nint, nint, int> ServerSaveGameFn =
        (delegate* unmanaged[Thiscall]<nint, int, nint, nint, nint, int>)0x004B58A0;

    /// <summary>
    /// Reads the text of an engine <c>CExoString</c> (<c>{char* text; int length}</c>) into a
    /// managed string, bounded by <paramref name="capacity"/> and cut at the first null
    /// (the engine's length field counts the terminator — the CExoString length trap). False
    /// when the pointer or its text pointer does not look like data, or the length is absurd.
    /// </summary>
    public static bool TryReadExoString(nint exoString, int capacity, out string text)
    {
        text = string.Empty;
        if (!LooksLikeDataPointer(exoString))
        {
            return false;
        }

        nint chars = *(nint*)exoString;
        int length = *(int*)(exoString + 4);
        if (length == 0 && chars == 0)
        {
            return true; // the empty string: a null text pointer and no length
        }

        if (!LooksLikeDataPointer(chars) || length < 0 || length > 4096)
        {
            return false;
        }

        int n = 0;
        int limit = Math.Min(length, capacity);
        while (n < limit && *(byte*)(chars + n) != 0)
        {
            n++;
        }

        text = n == 0 ? string.Empty : System.Text.Encoding.ASCII.GetString((byte*)chars, n);
        return true;
    }

    /// <summary>
    /// Re-issues a save that <see cref="SaveFlushHook"/> held: the same call, the same
    /// arguments, a fresh <c>CExoString</c> built from the captured name. Refuses (false)
    /// when there is no server any more or the server object is not the one the request was
    /// made against — a cross-world re-issue is exactly the pointer-poison the transition
    /// rules forbid. Returns the engine's own answer otherwise.
    ///
    /// The <c>player</c> pointer is forwarded as captured: <c>SaveGame</c> reads it only on
    /// its not-enough-disk-space exit, and the server-side player object it names lives for
    /// the whole session (the player list is on <c>CServerExoAppInternal</c>, not the module).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool ReissueServerSaveGame(nint internalServer, int number, string name, nint player, nint arg4)
    {
        nint server = GetServer();
        if (server == 0)
        {
            return false;
        }

        nint current = Deref(server + AppServerOffsetInternal);
        if (current == 0 || current != internalServer)
        {
            return false;
        }

        byte* text = stackalloc byte[SaveFlushHook.NameCapacity + 1];
        int n = 0;
        foreach (char c in name)
        {
            if (n >= SaveFlushHook.NameCapacity)
            {
                break;
            }

            text[n++] = c is > (char)0 and < (char)128 ? (byte)c : (byte)'?';
        }

        text[n] = 0;

        byte* storage = stackalloc byte[8];
        CExoStringCtorFn(storage, text);
        try
        {
            return ServerSaveGameFn(current, number, (nint)storage, player, arg4) != 0;
        }
        finally
        {
            CExoStringDtorFn(storage);
        }
    }
}
