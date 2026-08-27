// Dialogue and interaction-lock sync: conversation/minigame presence, following a
// peer's conversation, and interaction locks.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface IDialogueBridge
{
    /// <summary>
    /// True while the local player is in a conversation. Enemy state must not be published
    /// during one: the dialogue system disarms every hostile in this instance, and a peer
    /// mirroring that would see its own fight go quiet.
    /// </summary>
    bool IsInDialogue => false;

    /// <summary>
    /// True while the local player is inside the engine's own minigame mode (a swoop race
    /// or a forced turret sequence — <c>CSWCModule::GetMiniGame</c>, see
    /// docs/research/minigames.md). They are not meaningfully in the shared world for the
    /// duration: enemy/party presence this instance would publish is not the real fight,
    /// and damage forwarded from a peer should not land on someone who cannot be hit.
    /// </summary>
    bool IsInMinigame => false;

    /// <summary>
    /// Reads what the local player is currently hearing in a conversation, for the
    /// dialogue-spectate payload. Empty when not in a conversation, or when the plugin could
    /// not resolve the live line. Default is "no dialogue read" for bridges without a plugin.
    /// </summary>
    bool TryReadLocalDialogue(out string speaker, out string line)
    {
        speaker = string.Empty;
        line = string.Empty;
        return false;
    }

    /// <summary>
    /// Reads what a spectating peer needs to run the SAME conversation locally: the tag of
    /// the NPC the local player is talking to, WHERE that conversation currently is (the
    /// dialogue entry index — this is what the follower is driven by), and the reply they
    /// last picked (diagnostic). Default is "nothing to follow" for bridges without a plugin,
    /// with the node reported as unknown rather than as node 0.
    /// </summary>
    bool TryReadLocalDialogueFollow(
        out string partnerTag, out uint replySerial, out int replyIndex, out byte[] pickLog,
        out int nodeIndex, out uint nodeSerial, out bool forced)
    {
        partnerTag = string.Empty;
        replySerial = 0;
        replyIndex = 0;
        pickLog = [];
        nodeIndex = -1;
        nodeSerial = 0;
        forced = false;
        return false;
    }

    /// <summary>
    /// Tells the game which conversation a peer owns and which node it is on, so this
    /// instance runs the same one and is driven to the same line without being able to
    /// choose. <paramref name="forced"/> (2026-08-23, docs/research/dialogue-sync.md's Item 1):
    /// whether the owner's conversation is one the vanilla engine itself forces regardless of
    /// distance — see <see cref="TryReadLocalDialogueFollow"/>'s own remarks. Default false.
    /// </summary>
    bool WriteRemoteDialogue(
        bool active, string partnerTag, uint replySerial, int replyIndex, byte[]? pickLog,
        int nodeIndex, uint nodeSerial, bool forced) => false;

    /// <summary>The object the local player is interacting with, so the session can claim it
    /// on their behalf. Default is "nothing claimed". <paramref name="targetId"/> is the
    /// placeable object id an <see cref="KotorCoop.Protocol.InteractionKind.Container"/> claim
    /// carries (docs/research/container-sync.md); every other kind keys on
    /// <paramref name="tag"/> instead and leaves this 0.</summary>
    bool TryReadLocalInteraction(out string tag, out uint kind, out bool active, out uint targetId)
    {
        tag = string.Empty;
        kind = 0;
        active = false;
        targetId = 0;
        return false;
    }

    /// <summary>Hands the game every object currently held by anyone, so it can hold the ones
    /// it owns still. Default false.</summary>
    bool WriteInteractionLocks(IReadOnlyList<InteractionLock> locks) => false;

    /// <summary>
    /// Reads a pending explicit container-lock arbitration request the plugin's deferral state
    /// machine just published — closes the container-loot race (docs/roadmap-seamless.md Tier 1
    /// item 4, docs/research/container-sync.md's <c>container_lock_arbitration</c> section). A
    /// zero <paramref name="requestSerial"/> means nothing is pending. Default "nothing
    /// pending" for bridges without a plugin.
    /// </summary>
    bool TryReadContainerLockRequest(out uint targetId, out uint requestSerial)
    {
        targetId = 0;
        requestSerial = 0;
        return false;
    }

    /// <summary>Hands the plugin the arbiter's answer to the request named by
    /// <paramref name="requestSerial"/>. Default false.</summary>
    bool WriteContainerLockResponse(uint requestSerial, bool granted) => false;
}
