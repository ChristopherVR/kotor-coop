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
        if (!HasPlayer)
        {
            return false;
        }

        DoQuickSaveFn();
        return true;
    }
}
