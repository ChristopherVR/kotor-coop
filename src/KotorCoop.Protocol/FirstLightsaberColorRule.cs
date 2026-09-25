namespace KotorCoop.Protocol;

/// <summary>
/// Zhar's saber-construction reward (docs/research/class-and-power-grants.md §11) creates
/// exactly ONE lightsaber, on the builder's own engine, and immediately equips it into a weapon
/// slot within the SAME conversation node (<c>ActionEquipItem</c>, queued via
/// <c>AssignCommand</c>, inside the still-open Zhar dialogue). Live-confirmed 2026-09-25: because
/// the item never sits as a backpack row long enough for the generic dialogue/script item-grant
/// diff to see it as "a new item", that channel published NOTHING for this reward — the original
/// receiver-side substitution this file used to hold (a <c>LootPump</c> hook reacting to a
/// <c>DialogueGrantPacket</c> that, in practice, never arrives for this specific reward) was
/// dead code and has been removed. See <c>KotorCoop.Session.Pumps.FirstLightsaberBuildPump</c>
/// for the detection (watching the SAME <c>PlayerState.EquippedRightWeapon</c>/
/// <c>EquippedLeftWeapon</c> fields every tick, which cannot miss an equip the way an inventory
/// diff can) and delivery (the existing world-progression flag channel — no new packet) this
/// file's constants and pure lookups now serve.
///
/// <c>k_pdan_saber16</c> keys the colour off the shared <c>DAN_PATH_STATE</c> global, not either
/// PC's class — harmless in single-player (both come from the SAME dialogue choice) but wrong
/// once two players can hold different Jedi classes, which is the whole reason this exists: every
/// OTHER player builds their OWN saber, locally, from their OWN class, never a copy of the
/// builder's colour.
/// </summary>
public static class FirstLightsaberColorRule
{
    /// <summary>
    /// A session-wide fact ("a party member has built their first lightsaber"), not tied to any
    /// real KOTOR global variable or module object. Reuses the SAME 64-bit "object-keyed" address
    /// space <c>KotorCoop.Memory.ObjectKeyedFlags</c> documents for doors/placeables/mine
    /// triggers (a tag byte in bits 63..56, so the value is always &gt; <see cref="uint.MaxValue"/>
    /// and can never collide with a real global-variable flag id, which lives entirely in the low
    /// 32 bits) without this dependency-free protocol project needing to reference
    /// <c>KotorCoop.Memory</c> for it. Tag <c>'S'</c> (0x53) — <c>'D'</c> doors, <c>'P'</c>
    /// placeables and <c>'T'</c> trap triggers are already taken. The module-hash and object-id
    /// fields are both zero: this fact is not scoped to any one module. Needs no protocol version
    /// bump — <c>EventFlagStore</c>'s key space is opaque to the wire (17-byte flag entries,
    /// 8-byte id) exactly like adding a new object-keyed tag byte already is.
    /// </summary>
    public const ulong FirstSaberBuiltFlagId = 0x53UL << 56;

    /// <summary>
    /// The three starter single-blade sabers Zhar's construction script (<c>k_pdan_saber16</c>)
    /// can create, and the <c>classes.2da</c> row each is tied to — VERIFIED by static decode of
    /// the shipped script against the sha256-matched GOG resources (2026-09-25),
    /// docs/research/class-and-power-grants.md §11.1. Class ids match
    /// <c>k_pdan_makejedi</c>'s own <c>AddMultiClass</c> calls (already documented in §3/§4).
    /// </summary>
    public static readonly IReadOnlyDictionary<int, string> ResrefForClass = new Dictionary<int, string>
    {
        [3] = "g_w_lghtsbr01", // Guardian - blue
        [5] = "g_w_lghtsbr04", // Sentinel - yellow
        [4] = "g_w_lghtsbr03", // Consular - green
    };

    private static readonly HashSet<string> CanonicalResrefs =
        new(ResrefForClass.Values, StringComparer.OrdinalIgnoreCase);

    /// <summary>True for exactly the three resrefs in <see cref="ResrefForClass"/> — used both
    /// to DETECT a build (a canonical resref just appeared in the local player's own weapon
    /// slot) and to check "does this player already have a first saber" (re-checked live every
    /// tick, never a one-shot latch, so a resync/rejoin/late join is naturally idempotent).
    /// </summary>
    public static bool IsCanonicalFirstSaber(string resref) =>
        resref.Length > 0 && CanonicalResrefs.Contains(resref);

    /// <summary>
    /// The retail colour this player's OWN class implies, or null if none of their current
    /// class ids is one of the three Jedi classes <see cref="ResrefForClass"/> knows about (no
    /// Jedi class yet, or an unrecognised one) — in which case there is nothing to build.
    /// </summary>
    /// <param name="classIds">This player's own current class ids, in slot order (0-2 entries,
    /// <c>classes.2da</c> rows) — <c>IPlayerBridge.ReadLocalClassIds</c>.</param>
    public static string? ResrefForClasses(IReadOnlyList<int> classIds)
    {
        foreach (int classId in classIds)
        {
            if (ResrefForClass.TryGetValue(classId, out string? resref))
            {
                return resref;
            }
        }

        return null;
    }
}
