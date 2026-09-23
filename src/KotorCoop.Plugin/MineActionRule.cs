namespace KotorCoop.Plugin;

internal static class MineActionRule
{
    internal readonly record struct Facts(
        bool ServerTargetFound,
        bool ServerTargetIsTrigger,
        bool TargetIsMine,
        bool TargetIsGit,
        bool TargetIsCurrentArea,
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
