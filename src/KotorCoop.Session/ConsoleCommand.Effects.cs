using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>The peer-effect staging verbs: applyeffect, effects.</summary>
public static partial class ConsoleCommand
{
    /// <summary>The local player's own object id on every instance (docs/re/object-model.md:
    /// "Player object id is exactly 0x7FFFFFFF") — what <c>me</c>/<c>self</c> resolves to for
    /// <c>applyeffect</c>, so a player can smoke-test a constructor against their own
    /// character without knowing their own id.</summary>
    private const string LocalPlayerObjectIdToken = "0x7FFFFFFF";

    /// <summary>Duration an <c>applyeffect</c> line gets when it names none — long enough to
    /// actually be felt (a few seconds), not a measured figure. Mirrors the same fallback
    /// <c>PhantomEffectHook.FallbackDurationMs</c> uses on the capture side, for the same
    /// reason: most of these effects are meant to be felt, not applied as a permanent flag
    /// flip.</summary>
    private const uint DefaultApplyEffectDurationMs = 6000;

    /// <summary>
    /// Parses <c>applyeffect &lt;kind&gt; [target] [durationMs] [magnitude]</c> — the staging
    /// verb that runs a peer-effect constructor directly, through the same
    /// <c>OdysseyEffects.ApplyEffect</c> path the receiving side calls. Aimed at a peer's
    /// phantom (the default — a blank target) this IS the live test docs/research/effect-sync.md
    /// has never been able to run; aimed at an explicit id it is a constructor smoke test.
    /// </summary>
    private static CoopCommand ParseApplyEffect(string[] rest)
    {
        const string usage =
            "applyeffect <kind> [target] [durationMs] [magnitude]  (kind: stun, paralyze, "
            + "knockdown, slow, frightened, sleep, choke, forcepush, whirlwind, heal, "
            + "temporaryhitpoints, confused; target: object id, 'me', or blank for the "
            + "nearest phantom)";

        if (rest.Length == 0 || rest.Length > 4)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        if (!Enum.TryParse(rest[0], ignoreCase: true, out PeerEffectKind kind) || kind == PeerEffectKind.None)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"'{rest[0]}' is not an effect kind - {usage}");
        }

        string? target = null;
        if (rest.Length > 1 && rest[1].Length > 0)
        {
            string raw = IsSelfWord(rest[1]) ? LocalPlayerObjectIdToken : rest[1];

            if (!TryParseObjectId(raw, out _))
            {
                return new CoopCommand(
                    CoopCommandKind.Invalid,
                    Error: $"'{rest[1]}' is not an object id (try 0x35, or 'me' for yourself)");
            }

            target = raw;
        }

        uint durationMs = DefaultApplyEffectDurationMs;
        if (rest.Length > 2 && !uint.TryParse(rest[2], out durationMs))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"'{rest[2]}' is not a duration in milliseconds");
        }

        uint magnitude = 0;
        if (rest.Length > 3 && !uint.TryParse(rest[3], out magnitude))
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: $"'{rest[3]}' is not a magnitude");
        }

        return new CoopCommand(
            CoopCommandKind.ApplyEffect,
            Address: target,
            Amount: (int)kind,
            EffectDurationMs: durationMs,
            EffectMagnitude: magnitude);
    }

    private static CoopCommand ParseEffects(string[] rest) =>
        rest.Length > 0
            ? new CoopCommand(CoopCommandKind.Invalid, Error: "effects takes no arguments")
            : new CoopCommand(CoopCommandKind.Effects);
}
