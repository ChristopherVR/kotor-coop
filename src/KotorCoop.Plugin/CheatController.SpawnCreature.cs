using System.Text;

namespace KotorCoop.Plugin;

// The `spawncreature <template> [count] [x y [z]]` (alias `spawn`) dev-console verb
// (docs/research/creature-liveness.md's 2026-09-03 checklist item 6: "a `spawncreature`-style
// debug verb, following Odyssey.SpawnCreature's existing call path" — needed to synthesise a
// population large enough to stress tools/liveness-freshness-probe.ps1 past this game's own
// reachable content ceiling; also used to stage a fight anywhere for combat-round testing, e.g.
// docs/research/peer-damage-ledger.md §9.5).
internal sealed unsafe partial class CheatController
{
    /// <summary>The highest accepted spawn count. Must match
    /// <c>KotorCoop.Session.SpawnCreatureVerbRule.MaxCount</c> — the session-side parser already
    /// refuses anything outside 1-32, but a stale or hand-crafted request must not be trusted
    /// blind (same reasoning <c>PopModal</c>/<c>Buy</c>/<c>Sell</c> already apply to their own
    /// <c>CheatAmount</c>).</summary>
    private const int MaxSpawnCount = 32;

    /// <summary>How many spawned ids the concise answer names before falling back to a count —
    /// the console answer channel is 64 bytes (<see cref="SharedBlock.CheatDetailLength"/>) and
    /// truncates without complaint, so the full id list always goes to the log instead (same
    /// convention <c>Resolve</c>/<c>Census</c> already establish).</summary>
    private const int AnswerIdLimit = 5;

    /// <summary>
    /// DEV-ONLY TEST PRIMITIVE: spawns <c>count</c> ordinary hostile creatures from a .utc
    /// template resref through the engine's own CreateObject path
    /// (<see cref="Odyssey.SpawnCreature"/>) — the SAME call <c>MaterialisedEnemies</c>'s and
    /// <c>PhantomController</c>'s own stand-in spawns already make, just reachable from a console
    /// line now. Faction, hostility and every other property come entirely from the template
    /// itself; nothing here forces anything the blueprint did not already say.
    ///
    /// Anchored next to the LOCAL PLAYER by default, or on <see cref="SharedBlockView.CheatPosition"/>
    /// when the line named an explicit destination — the same origin-sentinel convention
    /// <see cref="MoveNear"/> already uses (<see cref="Vec3.IsOrigin"/>: a real explicit
    /// destination is never exactly (0,0,0) either). Every creature after the first is spread out
    /// from that one anchor point by <see cref="SpawnCreatureGridRule"/>, so a 32-creature spawn
    /// does not stack every body on one exact spot.
    ///
    /// ⚠ LOCAL ONLY, ALWAYS. This verb has no forward path: on a CLIENT the spawned bodies are
    /// ordinary local objects this instance's own engine created, exactly as
    /// <see cref="Odyssey.SpawnCreature"/> always produces — they are not registered with any
    /// peer-sync channel, so a peer never learns they exist. The intended use is on the HOST,
    /// where a spawned creature is population like any other the host's own module already owns.
    /// </summary>
    private void SpawnCreature(nint player)
    {
        byte* text = block.CheatText;
        int length = 0;
        while (length < SharedBlock.CheatTextLength - 1 && text[length] != 0)
        {
            length++;
        }

        if (length == 0)
        {
            Answer(SharedBlock.CheatStatusRefused, "spawncreature: no template given");
            return;
        }

        string template = Encoding.ASCII.GetString(text, length);

        // `@template`: spawn the way a script does, through the routine table, so the
        // host-spawns-only hook sees it (ScriptCreateObjectHook).
        bool asScript = template.StartsWith('@');
        if (asScript)
        {
            template = template[1..];
        }

        int count = Math.Clamp(block.CheatAmount > 0 ? block.CheatAmount : 1, 1, MaxSpawnCount);

        Vec3 explicitPosition = block.CheatPosition;
        bool isExplicit = !Vec3.IsOrigin(explicitPosition);
        Vec3 anchor = isExplicit ? explicitPosition : Odyssey.GetPosition(player);
        Vec3 facing = new() { X = 1f, Y = 0f, Z = 0f };

        int spawned = 0;
        int lastFailure = 0;
        var ids = new List<uint>(count);

        for (int i = 0; i < count; i++)
        {
            Vec3 destination = SpawnCreatureGridRule.OffsetFor(anchor, i);
            uint objectId = Odyssey.SpawnCreature(template, destination, facing, asScript, out int failure);

            if (objectId != Odyssey.ObjectInvalid && failure == 0)
            {
                spawned++;
                ids.Add(objectId);
            }
            else
            {
                lastFailure = failure;
            }
        }

        string how = isExplicit ? "at the given position" : "near the player";

        // Full ledger to the log - unbounded, this is the record a rig script actually reads.
        StringBuilder logIds = new();
        foreach (uint id in ids)
        {
            logIds.Append($"0x{id:X} ");
        }

        PluginHost.Log(
            $"spawncreature: {spawned}/{count} '{template}' spawned {how} - ids: {logIds}"
            + (spawned < count ? $" (last failure {lastFailure} on the rest)" : string.Empty));

        if (spawned == 0)
        {
            Answer(
                SharedBlock.CheatStatusEngineRefused,
                $"spawncreature: 0/{count} {template} spawned (failure {lastFailure})");
            return;
        }

        // Short summary for the answer channel - see AnswerIdLimit's own doc comment.
        StringBuilder answerIds = new();
        for (int i = 0; i < ids.Count && i < AnswerIdLimit; i++)
        {
            if (i > 0)
            {
                answerIds.Append(' ');
            }

            answerIds.Append($"0x{ids[i]:X}");
        }

        if (ids.Count > AnswerIdLimit)
        {
            answerIds.Append($" +{ids.Count - AnswerIdLimit} more");
        }

        string summary = $"spawned {spawned}/{count} {template}: {answerIds}"
            + (spawned < count ? $", failure {lastFailure} on the rest" : string.Empty);

        Answer(SharedBlock.CheatStatusApplied, summary);
    }
}
