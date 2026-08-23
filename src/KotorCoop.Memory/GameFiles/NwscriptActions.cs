namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// KOTOR 1's NWScript engine-function ("ACTION") routine numbers, for the handful this
/// project has actually needed to resolve. Not a full port of nwscript.nss — just the
/// global-variable family plus the two calls every OnEnter handler's boilerplate opens
/// with, added as the disassembler needed them.
///
/// 578-581 are VERIFIED: confirmed against a public reference (xoreos-tools' KOTOR2
/// function table) and then confirmed to match KOTOR1's own numbering by using them to
/// read two real compiled scripts pulled straight out of this game's own
/// <c>modules\tar_m02ab_s.rim</c> — the argument counts the bytecode itself carries
/// (1 for the Get* pair, 2 for the Set* pair) agree with each routine's real NWScript
/// signature, and the resulting call reads as an unambiguous, idiomatic script. See
/// docs/research/minigames.md, the "An NCS disassembler" round, for the full trace.
///
/// 25 and 217 are INFERRED ONLY from the bytecode's own shape (a zero-argument object
/// getter immediately consumed by a one-argument object-to-bool predicate feeding a
/// JZ gate is the standard compiled form of an OnEnter script's opening
/// <c>if (GetIsPC(GetEnteringObject()))</c> boilerplate) — not independently checked
/// against a published action table. Flagged as such in the name itself so a caller
/// never mistakes them for the same confidence level as 578-581.
/// </summary>
public static class NwscriptActions
{
    public static readonly IReadOnlyDictionary<int, string> KnownActions = new Dictionary<int, string>
    {
        [578] = "GetGlobalBoolean",
        [579] = "SetGlobalBoolean",
        [580] = "GetGlobalNumber",
        [581] = "SetGlobalNumber",
        [25] = "GetEnteringObject (INFERRED)",
        [217] = "GetIsPC (INFERRED)",
    };

    public static string NameFor(int routine) =>
        KnownActions.TryGetValue(routine, out string? name) ? name : $"#{routine}";
}

/// <summary>One ACTION (engine-function) call, with the string constants pushed
/// immediately before it — the idiom that surfaces a global variable's name right next
/// to a GetGlobalBoolean/SetGlobalBoolean-style call.</summary>
public sealed record NcsActionCall(
    int Offset,
    int Routine,
    string ResolvedName,
    int ArgCount,
    IReadOnlyList<string> PrecedingStringConstants)
{
    public override string ToString() =>
        PrecedingStringConstants.Count == 0
            ? $"{Offset,5}: {ResolvedName}(...) [routine {Routine}, {ArgCount} args]"
            : $"{Offset,5}: {ResolvedName}({string.Join(", ", PrecedingStringConstants.Select(s => $"\"{s}\""))}, ...) [routine {Routine}, {ArgCount} args]";
}

/// <summary>Extension helpers over a disassembled instruction stream.</summary>
public static class NcsInstructionsExtensions
{
    /// <summary>
    /// Every <see cref="NcsOpcode.CallEngine"/> instruction in the stream, each paired
    /// with whatever string constants were pushed immediately before it (walking
    /// backwards through contiguous <see cref="NcsOpcode.PushConstant"/> string
    /// instructions, in the order they were originally pushed) — the fast way to spot
    /// "this call touches global FOO" without hand-tracing the stack.
    /// </summary>
    public static IReadOnlyList<NcsActionCall> FindActionCalls(
        this IReadOnlyList<NcsInstruction> instructions,
        IReadOnlyDictionary<int, string>? actionNames = null)
    {
        actionNames ??= NwscriptActions.KnownActions;
        List<NcsActionCall> calls = [];

        for (int i = 0; i < instructions.Count; i++)
        {
            NcsInstruction insn = instructions[i];

            if (insn.Opcode != NcsOpcode.CallEngine)
            {
                continue;
            }

            List<string> preceding = [];

            for (int j = i - 1; j >= 0; j--)
            {
                if (instructions[j].Opcode == NcsOpcode.PushConstant && instructions[j].StringValue is not null)
                {
                    preceding.Insert(0, instructions[j].StringValue!);
                    continue;
                }

                break;
            }

            int routine = insn.ActionRoutine ?? -1;
            string name = actionNames.TryGetValue(routine, out string? known) ? known : $"#{routine}";

            calls.Add(new NcsActionCall(insn.Offset, routine, name, insn.ActionArgCount ?? 0, preceding));
        }

        return calls;
    }
}
