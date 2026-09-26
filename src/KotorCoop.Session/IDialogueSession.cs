// Conversation status/spectate token, and items a peer's own conversation just granted.
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IDialogueSession
{
    /// <summary>
    /// Announces this instance's local conversation status. <paramref name="active"/> true
    /// means the local player has entered a conversation; the session runs it through the
    /// host's arbiter. Speaker/line are the spectate payload (empty until the plugin can read
    /// the live conversation). Cheap to call every tick.
    /// </summary>
    void PublishDialogue(
        bool active, string speakerName, string line,
        string partnerTag = "", uint replySerial = 0, int replyIndex = 0, byte[]? pickLog = null,
        int nodeIndex = DialogueState.NoNode, uint nodeSerial = 0, bool forced = false);

    /// <summary>The authoritative conversation token — who, if anyone, is mid-dialogue.</summary>
    DialogueState GetDialogue();

    /// <summary>Publishes the actual scripted DLG override as well as its partner.</summary>
    void PublishDialogueWithResref(
        bool active, string speakerName, string line, string partnerTag,
        uint replySerial, int replyIndex, byte[]? pickLog, int nodeIndex,
        uint nodeSerial, bool forced, string dialogResref) =>
        PublishDialogue(active, speakerName, line, partnerTag, replySerial, replyIndex,
            pickLog, nodeIndex, nodeSerial, forced);

    /// <summary>
    /// Announces that THIS peer's own conversation just placed <paramref name="items"/> into
    /// the local speaker's inventory (docs/research/dialogue-consequences.md §3), delivered
    /// reliably to every OTHER peer so their instance can give the same items. Symmetric like
    /// <see cref="PublishContainerLoot"/>: <c>CoopHost</c> broadcasts directly; <c>CoopClient</c>
    /// sends to the host, which relays to every other client under this peer's TRUE (origin,
    /// sequence) identity rather than re-attributing it, so a receiver can tell "a peer granted
    /// this" from "I am seeing my own report echo back" — see <see cref="DrainDialogueGrants"/>.
    /// A no-op when <paramref name="items"/> is empty.
    /// </summary>
    void PublishDialogueGrant(string partnerTag, IReadOnlyList<MaterialisedLootItem> items) { }

    /// <summary>
    /// Every dialogue-grant packet received from ANOTHER peer since the last drain, oldest
    /// first, for the agent to apply through the same give-loot cheat channel the materialised-
    /// loot path already uses. A grant whose origin is THIS peer's own id — the harmless echo
    /// <see cref="PublishDialogueGrant"/>'s own doc names — is filtered out here rather than
    /// left for the caller to notice, because applying it a second time is not idempotent the
    /// way a container-loot echo is (giving an item mints a fresh one every call).
    /// </summary>
    IReadOnlyList<DialogueGrantPacket> DrainDialogueGrants() => [];
}
