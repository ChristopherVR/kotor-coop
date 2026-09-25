namespace KotorCoop.Plugin;

/// <summary>
/// coop.ini <c>stealth_partner_visual</c> (<c>translucent</c> | <c>hidden</c> | <c>off</c>,
/// default <c>translucent</c> since 2026-09-25) — how a stealthed peer's stand-in renders on
/// OTHER players' screens. Replaces the old boolean <c>stealth_visual_sync</c> (default OFF,
/// which only ever meant "hidden" — the sole visual behaviour that key's ON value produced; see
/// docs/research/effect-sync.md §12.9/§12.10). Kept pure — no coop.ini file access, no engine
/// pointers — so the parsing and migration are unit-testable without a running game or a
/// 64-bit test host (the plugin itself is NativeAOT x86 and cannot be referenced directly; see
/// tests/KotorCoop.Tests/KotorCoop.Tests.csproj's own note on dependency-free plugin files).
///
/// <list type="bullet">
/// <item><see cref="Kind.Translucent"/> — the retail look: the engine's own client-side
/// stealth shimmer (<c>Odyssey.SetStealthVisualState</c>, <c>CSWCCreature::SetStealthState</c>),
/// applied without touching the server-side detection-relevant field. The peer stays visible,
/// tinted, exactly like a real party member's own stealth.</item>
/// <item><see cref="Kind.Hidden"/> — today's (pre-2026-09-25) only behaviour: the server-side
/// <c>CSWSCreature::SetStealthMode</c> (<c>Odyssey.SetStealthMode</c>), which pushes the
/// stand-in through the same detection-relevance broadcast gate a real stealthed enemy uses —
/// an observer who has not "detected" it is never told it exists, so it fully vanishes.</item>
/// <item><see cref="Kind.Off"/> — no engine call at all. The stand-in stays fully, normally
/// visible while its owner is stealthed; only <c>stealth_sync</c>'s targeting release still
/// applies (a separate, already-shipped key — see <c>PhantomTargetRule</c>).</item>
/// </list>
/// </summary>
internal static class StealthVisualMode
{
    public const string KeyName = "stealth_partner_visual";

    /// <summary>The key this replaces. Still read, for the one-version migration in
    /// <see cref="Resolve"/> — never written, and never listed in coop.ini.example again.</summary>
    public const string LegacyKeyName = "stealth_visual_sync";

    public enum Kind
    {
        Translucent = 0,
        Hidden = 1,
        Off = 2,
    }

    /// <summary>
    /// Parses one of the three spellings (case/space-insensitive: leading/trailing
    /// whitespace is trimmed, the compare is ordinal-ignore-case). False — with
    /// <paramref name="kind"/> left at <see cref="Kind.Translucent"/> — for anything else,
    /// including an empty string; <see cref="Resolve"/> is what decides what "did not parse"
    /// should fall back to for each caller.
    /// </summary>
    public static bool TryParse(string? raw, out Kind kind)
    {
        switch (raw?.Trim().ToLowerInvariant())
        {
            case "translucent":
                kind = Kind.Translucent;
                return true;
            case "hidden":
                kind = Kind.Hidden;
                return true;
            case "off":
            case "none":
                kind = Kind.Off;
                return true;
            default:
                kind = Kind.Translucent;
                return false;
        }
    }

    /// <summary>
    /// The full migration rule, given the two RAW coop.ini values (each <c>null</c> only when
    /// the key is genuinely ABSENT from the file — callers must use a plain <c>Read</c>, not a
    /// typed <c>ReadBool</c>, for <paramref name="legacyRaw"/> so "absent" and "explicitly
    /// false" stay distinguishable):
    ///
    /// <list type="number">
    /// <item><paramref name="newRaw"/> present → it wins outright, parsed via
    /// <see cref="TryParse"/>. A misspelled value becomes <see cref="Kind.Translucent"/> (the
    /// new default) rather than silently falling through to the legacy key — a typo in the NEW
    /// key must not resurrect the OLD one's meaning.</item>
    /// <item><paramref name="newRaw"/> absent, <paramref name="legacyRaw"/> present → reproduces
    /// the old key's meaning exactly, bit for bit: truthy (<c>1</c>/<c>true</c>/<c>yes</c>/
    /// <c>on</c>) → <see cref="Kind.Hidden"/> (the only thing <c>stealth_visual_sync = 1</c> ever
    /// did); anything else it was explicitly set to → <see cref="Kind.Off"/> (exactly what a
    /// false <c>stealth_visual_sync</c> already meant: no engine call at all). An existing
    /// coop.ini that sets this key keeps behaving identically after the upgrade.</item>
    /// <item>Neither key present → <see cref="Kind.Translucent"/>, the new product default
    /// (2026-09-25 decision: prefer the retail-shaped translucent look over the old
    /// cautious-default OFF).</item>
    /// </list>
    /// </summary>
    public static Kind Resolve(string? newRaw, string? legacyRaw)
    {
        if (newRaw is not null)
        {
            TryParse(newRaw, out Kind kind);
            return kind;
        }

        if (legacyRaw is not null)
        {
            return IsTruthy(legacyRaw) ? Kind.Hidden : Kind.Off;
        }

        return Kind.Translucent;
    }

    /// <summary>The same truthy set <c>PluginConfig.ReadBool</c>/<c>CoopConfig.IsTruthy</c> use,
    /// duplicated rather than shared — this file must stay dependency-free (see the class
    /// doc comment), the same reason <c>PluginConfig</c> keeps its own copy instead of
    /// referencing <c>CoopConfig</c>'s.</summary>
    private static bool IsTruthy(string value) =>
        value == "1"
        || value.Equals("true", System.StringComparison.OrdinalIgnoreCase)
        || value.Equals("yes", System.StringComparison.OrdinalIgnoreCase)
        || value.Equals("on", System.StringComparison.OrdinalIgnoreCase);
}
