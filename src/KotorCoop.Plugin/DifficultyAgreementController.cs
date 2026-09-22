namespace KotorCoop.Plugin;

/// <summary>
/// Samples the local runtime difficulty into the plugin-agent block. This is read-only:
/// it never adopts or changes the setting. See docs/research/difficulty-agreement.md.
/// </summary>
internal static unsafe class DifficultyAgreementController
{
    private const int ClientOptionsDifficultyOffset = 0x04;

    /// <summary>CClientExoApp::GetClientOptions, verified at 0x005ED700.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetClientOptionsFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED700;

    public static void Sample(SharedBlock* block)
    {
        if (block is null)
        {
            return;
        }

        // Invalidate first so an interrupted/torn publication is unknown, never the previous
        // setting paired with a new session state.
        block->DifficultyKnown = 0;

        nint client = EngineHandles.GetClient();
        if (!LooksLikeDataPointer(client))
        {
            return;
        }

        nint options = GetClientOptionsFn(client);
        if (!LooksLikeDataPointer(options))
        {
            return;
        }

        byte level = *(byte*)(options + ClientOptionsDifficultyOffset);
        if (level > 2)
        {
            return;
        }

        block->DifficultyLevel = level;
        block->DifficultyKnown = 1;
    }

    private static bool LooksLikeDataPointer(nint pointer) => pointer is > 0x10000 and < 0x7FFF0000;
}
