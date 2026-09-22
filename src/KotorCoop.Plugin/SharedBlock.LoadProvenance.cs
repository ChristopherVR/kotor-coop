namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // Version 74, native save-load selection provenance. The 0x4E50..0x4F00 tail was
    // unused after the v73 role word at 0x4E44; the 0x4F00 block boundary is unchanged.
    // Generation is the seqlock: odd while being written, even and nonzero when published.
    public const int OffsetLoadProvenanceGeneration = 0x4E50;
    public const int OffsetLoadProvenanceSlot = 0x4E54;
    public const int OffsetLoadProvenanceSource = 0x4E58;
    public const int OffsetLoadProvenanceStatus = 0x4E5C;
    public const int OffsetLoadProvenanceWorldLoadSerial = 0x4E60;
    public const int OffsetLoadProvenanceNameLength = 0x4E64;
    public const int OffsetLoadProvenanceName = 0x4E68;
    public const int LoadProvenanceNameCapacity = 64;

    // Retail CSWGuiSaveLoadEntry layout, verified against docs/research/native-browser-panel.md §3a.
    public const int LoadEntrySaveNumberOffset = 0x1C8;
    public const int LoadEntrySaveNameOffset = 0x1D8;
}
