using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// What a joiner's periodic character capture compares to decide whether anything personal
/// changed since the last one: credits, experience and the player's own items with their stack,
/// charges and upgrades. Each capture is a real save with a single ~300 ms frame, so an idle
/// player should not pay for one every few minutes; a player who looted should not lose it to a
/// crash (2026-10-09: a joiner crash restored the last capture exactly, and everything since was
/// gone). Item object ids are left out on purpose: a rejoin renumbers them.
/// </summary>
public static class PersonalCaptureFingerprint
{
    public static string Compose(uint gold, uint experience, IEnumerable<(string Resref, ItemIdentity Identity)> ownItems)
    {
        IEnumerable<string> rows = ownItems
            .Select(i => $"{i.Resref.ToLowerInvariant()}:{i.Identity.StackSize}:{i.Identity.Charges}/{i.Identity.MaxCharges}:{i.Identity.Upgrades:X}")
            .Order(StringComparer.Ordinal);
        return $"{gold}|{experience}|{string.Join(",", rows)}";
    }
}
