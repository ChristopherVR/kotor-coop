namespace KotorCoop.Session;

/// <summary>
/// The live-cast staging verbs: <c>cast</c> and <c>grantpower</c> (docs/research/effect-sync.md
/// §14). Built because <c>applyeffect</c> stamps a synthetic creator (0x7F000000, OBJECT_SELF
/// invalid) that partner_buffs/enemy_effect_sync's capture hooks refuse — those hooks require
/// the effect's creator to be the local player, which only a REAL cast produces.
/// </summary>
public static partial class ConsoleCommand
{
    /// <summary>
    /// Friendly names for the spells.2da rows this pass's live recipes actually exercise
    /// (Heal/Cure/Force Valor for <c>partner_buffs</c>, Stun/Fear/Force Push/Stasis for
    /// <c>enemy_effect_sync</c>) — VERIFIED against this install's spells.2da
    /// (<c>python tools/kotorfiles.py 2da spells.2da</c>). A bare number is accepted for any
    /// other row; this table exists so the live recipes do not have to be typed as magic
    /// numbers.
    /// </summary>
    private static readonly Dictionary<string, int> SpellAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["heal"] = 28,
        ["cure"] = 10,
        ["valor"] = 22,
        ["forcevalor"] = 22,
        ["knightvalor"] = 33,
        ["mastervalor"] = 36,
        ["stun"] = 46,
        ["fear"] = 16,
        ["horror"] = 30,
        ["forcepush"] = 23,
        ["push"] = 23,
        ["stasis"] = 29,
        ["whirlwind"] = 27,
        ["choke"] = 9,
    };

    /// <summary>The highest row this install's spells.2da holds (132 rows, 0-131) — a sanity
    /// ceiling for a mistyped number, not a claim that every mod ships the same table.</summary>
    private const int MaxSpellRow = 131;

    /// <summary>The sum of every K1 metamagic bit (empower 2, extend 4, maximize 8, quicken
    /// 16, silent 32, still 64) — the highest value <c>nMetaMagic</c> can sensibly hold.</summary>
    private const int MaxMetaMagic = 126;

    private static bool TryParseSpellId(string token, out int spellId)
    {
        if (SpellAliases.TryGetValue(token, out spellId))
        {
            return true;
        }

        return int.TryParse(token, out spellId) && spellId >= 0 && spellId <= MaxSpellRow;
    }

    /// <summary>
    /// Parses <c>cast &lt;spell id|name&gt; &lt;target: me|0xid&gt; [metamagic]</c> — the
    /// live-test primitive that makes the LOCAL PLAYER cast a spells.2da row through the
    /// engine's own action queue (<c>Odyssey.CastSpellAtObject</c>), exactly as a player's
    /// radial-menu cast does: real animation, real Force-point cost, the caster stamped as the
    /// PC. See <see cref="CoopCommandKind.Cast"/>.
    /// </summary>
    private static CoopCommand ParseCast(string[] rest)
    {
        const string usage = "cast <spell id|name> <target: me|0xid> [metamagic]  "
            + "(names: heal, cure, valor/forcevalor, stun, fear, forcepush, stasis; "
            + "metamagic is a bitmask 0-126, default 0)";

        if (rest.Length is < 2 or > 3)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        if (!TryParseSpellId(rest[0], out int spellId))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not a spell id (0-{MaxSpellRow}) or a known name - {usage}");
        }

        string targetToken = IsSelfWord(rest[1]) ? LocalPlayerObjectIdToken : rest[1];
        if (!TryParseObjectId(targetToken, out _))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[1]}' is not an object id (try 0x35, or 'me' for yourself)");
        }

        int metaMagic = 0;
        if (rest.Length == 3 && (!int.TryParse(rest[2], out metaMagic) || metaMagic is < 0 or > MaxMetaMagic))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[2]}' is not a metamagic bitmask (0-{MaxMetaMagic})");
        }

        return new CoopCommand(
            CoopCommandKind.Cast, Address: targetToken, Amount: spellId, EffectMagnitude: (uint)metaMagic);
    }

    /// <summary>
    /// Parses <c>grantpower &lt;spell id|name&gt; [classSlot]</c> — grants the local player a
    /// Force power through the engine's own <c>CSWSCreatureStats::AddKnownSpell</c>, for a save
    /// whose class doesn't already know the power a <c>cast</c> live test needs. See
    /// <see cref="CoopCommandKind.GrantPower"/>.
    /// </summary>
    private static CoopCommand ParseGrantPower(string[] rest)
    {
        const string usage = "grantpower <spell id|name> [classSlot]  (classSlot: 0 or 1, default 0)";

        if (rest.Length is < 1 or > 2)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        if (!TryParseSpellId(rest[0], out int spellId))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not a spell id (0-{MaxSpellRow}) or a known name - {usage}");
        }

        int classSlot = 0;
        if (rest.Length == 2 && (!int.TryParse(rest[1], out classSlot) || classSlot is not (0 or 1)))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"'{rest[1]}' is not a class slot (0 or 1)");
        }

        return new CoopCommand(CoopCommandKind.GrantPower, Amount: spellId, EffectMagnitude: (uint)classSlot);
    }
}
