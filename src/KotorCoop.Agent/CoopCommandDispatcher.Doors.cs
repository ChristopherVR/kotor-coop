using KotorCoop.Memory;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>The door/placeable staging verbs: usedoor, opendoor, useplaceable, closeplaceable.
/// All four resolve their token the same way: an object id if the parser recognises one,
/// otherwise the raw text (a tag), resolved inside the game because the agent's own census may
/// be a frame or two stale.</summary>
public sealed partial class CoopCommandDispatcher
{
    /// <summary>
    /// Asks the plugin to make the local player use a door — the ENGINE-initiated half of the
    /// cinematic test (docs/research/cinematics.md §8).
    ///
    /// One request, no follow-up: unlike <c>moviewarp</c> there is nothing to sequence, because
    /// the movie was queued by a SEPARATE earlier command and is already sitting in the
    /// engine's own transition queue. That separation is the point — the plugin must not be
    /// the thing that starts this transition, or the flush window under test never opens.
    /// </summary>
    private void UseDoor(string token)
    {
        if (ConsoleCommand.TryParseObjectId(token, out uint objectId) && objectId != 0)
        {
            SendCheat(new CheatRequest(CheatKinds.UseDoor, Target: objectId));
            return;
        }

        SendCheat(new CheatRequest(CheatKinds.UseDoor, Text: token));
    }

    /// <summary>The same dispatch shape as <see cref="UseDoor"/>, for an ordinary door — see
    /// <c>CoopCommandKind.OpenDoor</c>.</summary>
    private void OpenDoor(string token)
    {
        if (ConsoleCommand.TryParseObjectId(token, out uint objectId) && objectId != 0)
        {
            SendCheat(new CheatRequest(CheatKinds.OpenDoor, Target: objectId));
            return;
        }

        SendCheat(new CheatRequest(CheatKinds.OpenDoor, Text: token));
    }

    /// <summary>The container-sync live-test primitive: the same dispatch shape as
    /// <see cref="OpenDoor"/>, for a placeable. See <c>CoopCommandKind.UsePlaceable</c> and
    /// docs/research/container-sync.md.</summary>
    private void UsePlaceable(string token)
    {
        if (ConsoleCommand.TryParseObjectId(token, out uint objectId) && objectId != 0)
        {
            SendCheat(new CheatRequest(CheatKinds.UsePlaceable, Target: objectId));
            return;
        }

        SendCheat(new CheatRequest(CheatKinds.UsePlaceable, Text: token));
    }

    /// <summary>The close-side counterpart to <see cref="UsePlaceable"/>, added when the
    /// container-sync live-verify pass found no way to close what <c>useplaceable</c> opened
    /// — see <c>CoopCommandKind.ClosePlaceable</c> and docs/research/container-sync.md.</summary>
    private void ClosePlaceable(string token)
    {
        if (ConsoleCommand.TryParseObjectId(token, out uint objectId) && objectId != 0)
        {
            SendCheat(new CheatRequest(CheatKinds.ClosePlaceable, Target: objectId));
            return;
        }

        SendCheat(new CheatRequest(CheatKinds.ClosePlaceable, Text: token));
    }
}
