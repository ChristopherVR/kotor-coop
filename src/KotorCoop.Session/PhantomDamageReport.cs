using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// One phantom-damage report as currently known for one sender: the legacy scalar total
/// (still the primary staleness gate — see CoopHost/CoopClient's own
/// <c>AcceptIncomingPhantomDamage</c>/<c>HandlePhantomDamage</c>) and last attacker, plus the
/// per-attacker breakdown carried alongside it since protocol version 48. Shared between
/// <see cref="CoopHost"/> and <see cref="CoopClient"/>, which otherwise duplicate this
/// channel's logic deliberately (see their own file headers) — this one shape is small enough,
/// and used identically enough by both, that splitting it would just be two copies of the same
/// four fields.
/// </summary>
internal readonly record struct PhantomDamageReport(
    uint Damage, uint Attacker, IReadOnlyList<PhantomAttackerEntry> Attackers);
