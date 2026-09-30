namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    /// <summary>Native-written completed required-file I/O: sequence, epoch, load serial,
    /// packed party, exact directory leaf[96], then nine occupant rows[24].</summary>
    public const int OffsetNativeSaveCompletion = 0xC000;
    public const int NativeSaveCompletionSize = 364;
}
