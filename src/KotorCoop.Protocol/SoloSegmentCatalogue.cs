namespace KotorCoop.Protocol;

/// <summary>
/// Which modules are story-forced "solo segment" content — the roster is stripped by script
/// and the fight that follows was designed for one player, not two. The base set is the exact
/// two modules docs/research/star-forge-finale.md §3.3/§3.5 traced out of the real compiled
/// scripts:
///
///   sta_m45ac — the shield-generator floor and Bastila's chamber. `k_psta_door.ncs`, at the
///     door tagged `k45_door_bast1` (its twin `k45_door_bast2` on the far side of the same
///     chamber), fires NINE consecutive `RemovePartyMember` calls — every possible companion,
///     unconditionally — the instant the door opens. Same-module event, no `StartNewModule`.
///   sta_m45ad — Malak's chamber, reached through the ordinary door `k45_door_malak`. Not a
///     second solo-gating moment; a continuation of the same forced-solo state sta_m45ac's door
///     started, carried across an ordinary door crossing rather than a stunt-module funnel.
///
/// (Note: an earlier draft of this feature's brief named `sta_m45ab` as "the Bastila chamber" —
/// that module is actually the droid-horde production floor, an ordinary full-party fight per
/// the research doc's §3.4. The catalogue below uses the doc's own verified modules, not the
/// draft's typo.)
///
/// Kept in exactly one place, deliberately: nothing else in this codebase is allowed to grow
/// its own copy of "which modules are forced-solo" — the same discipline
/// docs/research/party-reconciliation.md's roster layout and docs/research/minigames.md's kind
/// enum already keep for their own single sources of truth. Extendable at runtime, without a
/// code change, through coop.ini <c>solo_segment_modules</c> (a comma-separated list of extra
/// module resrefs appended to the base set) — for a future mod, a different story beat, or a
/// live-test module substituted in ahead of the real Star Forge save being reachable.
/// </summary>
public sealed class SoloSegmentCatalogue
{
    /// <summary>The two modules docs/research/star-forge-finale.md §3.3/§3.5 verified by
    /// reading the compiled scripts directly. See this class's own doc comment.</summary>
    public static readonly IReadOnlyList<string> BaseModules = ["sta_m45ac", "sta_m45ad"];

    /// <summary>Modules where a script hands the PLAYER over to a companion with
    /// <c>SwitchPlayerCharacter</c> (<c>CSWPartyTable+0xF0</c> goes from -1 to the slot): the
    /// Leviathan brig, where the rescuer is played while the real PC sits in a cell
    /// (<c>k_plev_transform</c> / <c>k_plev_freeparty</c>, both in <c>lev_m40aa</c> —
    /// docs/research/leviathan.md §3.3-3.4, §5.3 #1). The trigger is the +0xF0 edge, NOT the
    /// roster count, so it is a separate list from <see cref="BaseModules"/>.</summary>
    public static readonly IReadOnlyList<string> ControlledNpcBaseModules = ["lev_m40aa"];

    private readonly HashSet<string> _modules;

    public SoloSegmentCatalogue(IEnumerable<string>? extraModules = null)
    {
        _modules = new HashSet<string>(BaseModules, StringComparer.OrdinalIgnoreCase);

        if (extraModules is null)
        {
            return;
        }

        foreach (string module in extraModules)
        {
            if (!string.IsNullOrWhiteSpace(module))
            {
                _modules.Add(module.Trim());
            }
        }
    }

    /// <summary>Every module this catalogue currently recognises, base set plus any extras —
    /// diagnostic/test use only; <see cref="Contains"/> is the real query.</summary>
    public IReadOnlyCollection<string> Modules => _modules;

    public bool Contains(string module) => !string.IsNullOrEmpty(module) && _modules.Contains(module);

    /// <summary>True when <paramref name="module"/> is one where a +0xF0 edge (a script taking a
    /// companion under player control) begins a solo segment.</summary>
    public static bool ContainsControlledNpcModule(string module) =>
        !string.IsNullOrEmpty(module)
        && ControlledNpcBaseModules.Contains(module, StringComparer.OrdinalIgnoreCase);

    /// <summary>Parses coop.ini's <c>solo_segment_modules</c> value: comma-separated, blank
    /// entries and surrounding whitespace dropped. A null/blank/whitespace-only value parses to
    /// an empty list, which leaves the catalogue at exactly its base set — the same "absent
    /// means default" convention every other coop.ini list-shaped key in this codebase uses
    /// (e.g. <c>TriggerRelayController</c>'s own pattern watch list).</summary>
    public static IReadOnlyList<string> ParseExtraModules(string? csv) =>
        string.IsNullOrWhiteSpace(csv)
            ? []
            : [.. csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
