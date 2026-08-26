using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>
/// One staging cheat, as the agent hands it to the plugin.
///
/// A flat record rather than a verb-per-method because the plugin side is a single
/// serial-edged slot in the shared block: adding a cheat is a new <paramref name="Kind"/>
/// constant on both sides, not a new channel. <paramref name="Text"/> carries whichever
/// string the kind needs (a creature tag, a blueprint resref) and is capped by the block's
/// own 32-byte field.
/// </summary>
public readonly record struct CheatRequest(
    uint Kind,
    uint Target = 0,
    int Amount = 0,
    float X = 0f,
    float Y = 0f,
    float Z = 0f,
    string Text = "",
    uint Charges = CheatKinds.Unchanged,
    uint MaxCharges = CheatKinds.Unchanged,
    uint Upgrades = CheatKinds.Unchanged,
    uint ItemFlags = CheatKinds.Unchanged);
