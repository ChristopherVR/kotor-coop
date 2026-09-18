namespace KotorCoop.Memory;

/// <summary>
/// The shared 64-bit key layout for every world flag that names an OBJECT in a MODULE rather
/// than a global variable — doors (<see cref="DoorFlags"/>) and placeables
/// (<see cref="PlaceableFlags"/>). Protocol 53 widened the flag id from 32 to 64 bits for this.
///
/// Layout (64 bits, high to low):
/// <list type="bullet">
/// <item>bits 63..56 — an 8-bit type tag (<c>'D'</c> 0x44 doors, <c>'P'</c> 0x50 placeables).</item>
/// <item>bits 55..24 — the FULL 32-bit FNV-1a of the module name, case-folded.</item>
/// <item>bits 23..0 — the object id (24 bits; the bridge already refuses ids above 0xFFFF).</item>
/// </list>
///
/// ⚠ WHY THE MODULE PART IS 32 BITS AND NOT THE 8 IT USED TO BE. Object ids restart on every
/// module load, so the module part is the ONLY thing keeping "door 0x4A on the Endar Spire"
/// apart from "door 0x4A in the Lower City". The old 32-bit key folded the module hash to a
/// single byte: over a ~60-module playthrough two modules sharing that byte is a near
/// certainty (the birthday bound passes 99% by 60 draws from 256), and once two modules
/// collide any door or chest that happens to reuse a low object id shares one store entry —
/// the entry the store still holds from the earlier module then opens, unlocks or marks
/// searched an unrelated object in the later one the moment the player walks in. It was never
/// seen live only because no rig run ever crossed more than three modules
/// (docs/review-seamless-2026-09-18.md §2, roadmap #47). A 32-bit hash over the game's 117
/// shipped module names is collision-free (asserted by <c>ObjectKeyedFlagsTests</c> over the
/// whole shipped list) and stays so with any plausible number of modded modules added.
///
/// Global-variable flag ids are untouched and live entirely in the LOW 32 bits (type in bits
/// 23..16, slot index below — <c>GlobalVariableTable.MakeFlagId</c>), so an object-keyed key,
/// whose type tag sits in the top byte, is never mistaken for one: <see cref="IsObjectKeyed"/>
/// is simply "anything above <see cref="uint.MaxValue"/>", and the bridge's world-state apply
/// refuses such a key before it ever casts to <c>GlobalVariableType</c>.
///
/// Migration: there is none and none is needed — the store is in-memory per session, nothing
/// persists a flag id to disk, and a peer on the 32-bit layout is refused at Hello by the
/// protocol-version check (<c>E_PROTO</c>) before it can exchange a single flag.
/// </summary>
public static class ObjectKeyedFlags
{
    private const int TagShift = 56;
    private const int ModuleShift = 24;
    private const ulong ModuleMask = 0xFFFF_FFFFUL;
    private const ulong ObjectIdMask = 0x00FF_FFFFUL;

    public static ulong MakeKey(byte typeTag, uint moduleHash, uint objectId) =>
        ((ulong)typeTag << TagShift) | ((ulong)moduleHash << ModuleShift) | (objectId & ObjectIdMask);

    public static byte TagOf(ulong flagId) => (byte)(flagId >> TagShift);

    public static uint ModuleHashOf(ulong flagId) => (uint)((flagId >> ModuleShift) & ModuleMask);

    public static uint ObjectIdOf(ulong flagId) => (uint)(flagId & ObjectIdMask);

    /// <summary>True for any door/placeable key; false for every global-variable flag id,
    /// which lives in the low 32 bits only.</summary>
    public static bool IsObjectKeyed(ulong flagId) => flagId > uint.MaxValue;

    /// <summary>True when <paramref name="flagId"/> is an object-keyed flag belonging to the
    /// module whose hash is <paramref name="moduleHash"/> — the predicate a per-module prune
    /// (<c>EventFlagStore.Prune</c>) or the bridge's "this module only" filter needs.</summary>
    public static bool BelongsToModule(ulong flagId, uint moduleHash) =>
        IsObjectKeyed(flagId) && ModuleHashOf(flagId) == moduleHash;

    /// <summary>FNV-1a (32-bit) over the module name, case-folded so both instances agree
    /// however the engine happens to capitalise the resref.</summary>
    public static uint HashModule(string moduleName)
    {
        uint hash = 2166136261;
        foreach (char c in moduleName)
        {
            hash = (hash ^ (byte)char.ToLowerInvariant(c)) * 16777619;
        }

        return hash;
    }
}
