namespace KotorCoop.Plugin;

internal static class MineActionRule
{
    /// <summary>
    /// The retail recover/disarm range gate, VERIFIED(static) 2026-09-24
    /// (docs/research/mines.md §14.8): <c>AIActionDisarmMine 0x00519570</c>'s own "range check"
    /// (<c>0x004F6000</c>, called at 0x005195F1) resolves to a squared-distance comparison inside
    /// <c>CSWSTrigger::InTrigger 0x0058CE40</c> against <c>[0x007A206C]&#178; = 1.0&#178;</c> — the SAME
    /// fixed sphere mines.md §2 already documents for detonation, NOT the cosmetic 0.25f literal
    /// (<c>0x3E800000</c>) that function also carries (added to a UI-only float at <c>0x73E798</c>
    /// and never fed back into the InTrigger call). <c>AIActionRecoverMine 0x00518C40</c> shares
    /// the identical call shape. The diagnostic never checked this before 2026-09-24: it invoked
    /// the retail action-menu entry point regardless of the actor's distance from the mine, so an
    /// actor standing more than one unit away got a "queued" response that could never do
    /// anything — the native gate silently refuses every tick, forever, with no feedback this
    /// side of the wire could see.
    /// </summary>
    public const float RecoverDisarmRadius = 1.0f;

    /// <summary>True when the actor's position is within the retail range gate of the mine's
    /// position (squared distance, so callers never take an avoidable square root).</summary>
    public static bool WithinRecoverDisarmRange(float dx, float dy, float dz) =>
        ((dx * dx) + (dy * dy) + (dz * dz)) < RecoverDisarmRadius * RecoverDisarmRadius;

    internal readonly record struct Facts(
        bool ServerTargetFound,
        bool ServerTargetIsTrigger,
        bool TargetIsMine,
        bool TargetIsGit,
        bool TargetIsCurrentArea,
        bool ActorWithinRange,
        bool Disarmable,
        bool ClientTargetMatches,
        bool LocalActorMatches);

    public static bool TryParseRequest(string text, out bool recover, out string module)
    {
        recover = false;
        module = string.Empty;
        int separator = text.IndexOf('|');
        if (separator <= 0 || separator == text.Length - 1)
        {
            return false;
        }

        string action = text[..separator];
        module = text[(separator + 1)..];
        if (action is not ("recover" or "disarm")
            || module.Length > 16
            || module.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
        {
            module = string.Empty;
            return false;
        }

        recover = action == "recover";
        return true;
    }

    public static string? Refusal(Facts facts)
    {
        if (!facts.ServerTargetFound)
        {
            return "server mine id does not resolve";
        }

        if (!facts.ServerTargetIsTrigger)
        {
            return "target is not a trigger";
        }

        if (!facts.TargetIsMine)
        {
            return "target is not a floor mine";
        }

        if (!facts.TargetIsGit)
        {
            return "party-placed mines are not supported by this diagnostic";
        }

        if (!facts.TargetIsCurrentArea)
        {
            return "mine is not in the current area";
        }

        if (!facts.ActorWithinRange)
        {
            return "actor is more than 1.0 unit from the mine (the retail range gate, mines.md §14.8) - walk onto it first";
        }

        if (!facts.Disarmable)
        {
            return "mine is not disarmable";
        }

        if (!facts.ClientTargetMatches)
        {
            return "client-side trigger identity did not match the server mine";
        }

        if (!facts.LocalActorMatches)
        {
            return "controlled client creature did not match the local player identity";
        }

        return null;
    }

    public static string? ScopeRefusal(
        string expectedModule,
        uint expectedWorldLoadSerial,
        string currentModule,
        uint currentWorldLoadSerial)
    {
        if (!expectedModule.Equals(currentModule, StringComparison.OrdinalIgnoreCase))
        {
            return $"request was issued in {expectedModule}, current module is {currentModule}";
        }

        return expectedWorldLoadSerial == currentWorldLoadSerial
            ? null
            : $"request belongs to world load {expectedWorldLoadSerial}, current load is {currentWorldLoadSerial}";
    }
}
