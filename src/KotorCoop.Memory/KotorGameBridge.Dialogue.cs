// Dialogue and interaction-lock sync: whether this player is in a conversation or minigame,
// reading the local conversation for a follower to mirror, interaction locks, and telling the
// plugin which conversation a peer owns so it can be followed locally.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    /// <summary>CClientExoAppInternal + this = input class. 3 is dialog.</summary>
    private const int ClientInputClass = 0x9C;

    private const int InputClassDialog = 3;

    public string LocalDialogueResref => _pluginBlock != 0
        && _memory.TryReadAsciiString(_pluginBlock + BlockLocalDialogueResref,
            BlockDialogueResrefLength, out string resref) ? resref : string.Empty;

    /// <summary>
    /// True while this player is in a conversation.
    ///
    /// Matters for combat, not just presentation: the dialogue system disarms every
    /// hostile creature in the area on every dialog tick
    /// (<c>CSWSDialog::ClearAllPlayerHostileActions</c>, 0x005A0940). Since enemies are
    /// host-authoritative, publishing enemy state while talking would export that
    /// pacification to a peer who is still mid-fight.
    /// </summary>
    public bool IsInDialogue =>
        EngineGlobals.TryGetClientInternal(_memory, out nint clientInternal)
        && ((_memory.TryReadInt32(clientInternal + ClientInputClass, out int inputClass)
                && inputClass == InputClassDialog)
            || (_memory.TryReadPointer(clientInternal + InGameGuiOffset, out nint gui)
                && gui != 0 && _memory.TryReadInt32(gui + 0xB4, out int active) && active != 0));

    /// <summary>
    /// Gate for the Pazaak half of <see cref="IsInMinigame"/> — coop.ini <c>pazaak_detect</c>,
    /// default OFF (NEEDS-LIVE per docs/research/minigames.md's item-8 dated section; see
    /// <see cref="EngineGlobals.TryIsInPazaak"/>'s own doc comment for why). Set from
    /// <c>CoopConfig.PazaakDetect</c> in <c>AgentComposition.Compose</c>, the same place
    /// <c>SetChatOverlayConfig</c> pushes other coop.ini-only settings onto this bridge —
    /// deliberately NOT threaded through <c>AgentSettings</c>/<c>CoopAgent</c>, since the
    /// wire flag this feeds is computed here, not decided by the pump loop.
    /// </summary>
    public bool PazaakDetectEnabled { get; set; }

    /// <summary>
    /// True while the local player is in the engine's own minigame mode: swoop race, forced
    /// turret sequence (<see cref="EngineGlobals.TryIsInMiniGame"/>), or — when
    /// <see cref="PazaakDetectEnabled"/> opts in — a running Pazaak hand
    /// (<see cref="EngineGlobals.TryIsInPazaak"/>, docs/research/minigames.md §4: a
    /// <c>CSWGuiPazaakGame</c> GUI panel, not a <c>CSWCModule::GetMiniGame</c> state, so it
    /// needs its own read rather than showing up in the first check). Pazaak rides the exact
    /// same <see cref="PlayerState.FlagInMinigame"/> wire bit swoop/turret already sets — no
    /// protocol change, because the flag was always "is this player meaningfully absent from
    /// the shared world", not "is a swoop race specifically running". A read failure (no
    /// client world yet) reports false — the same "not in a minigame" default every other
    /// presence-style flag in <see cref="ReadLocalFlags"/> uses.
    /// </summary>
    public bool IsInMinigame =>
        (EngineGlobals.TryIsInMiniGame(_memory, out bool inMiniGame) && inMiniGame)
        || (PazaakDetectEnabled
            && EngineGlobals.TryIsInPazaak(_memory, out bool inPazaak)
            && inPazaak);

    /// <summary>
    /// Reads the speaker + line the plugin has published for the local player's current
    /// conversation (dialogue spectate). Empty until the plugin resolves a line; base-game
    /// TLK-only lines read as empty, which the caller treats as "nothing to spectate".
    /// </summary>
    public bool TryReadLocalDialogue(out string speaker, out string line)
    {
        speaker = string.Empty;
        line = string.Empty;

        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryReadAsciiString(
            _pluginBlock + BlockLocalDialogueSpeaker, BlockLocalDialogueSpeakerLength, out speaker);
        ok &= _memory.TryReadAsciiString(
            _pluginBlock + BlockLocalDialogueLine, BlockLocalDialogueLineLength, out line);

        speaker ??= string.Empty;
        line ??= string.Empty;
        return ok;
    }

    /// <summary>
    /// Reads what a spectating peer needs to run the same conversation: the tag of the NPC
    /// the local player is talking to, the reply they last picked (serial + slot), and the
    /// pick LOG — recent picks each stamped with the line they were made on, opaque here.
    /// </summary>
    public bool TryReadLocalDialogueFollow(
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

        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryReadAsciiString(
            _pluginBlock + BlockLocalDialoguePartnerTag, BlockDialoguePartnerTagLength, out partnerTag);
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockLocalDialogueReplySerial, out replySerial);
        ok &= _memory.TryReadInt32(_pluginBlock + BlockLocalDialogueReplyIndex, out replyIndex);

        // ⚠ The serial FIRST, then the node — the mirror image of the plugin's write order,
        // which is what makes the pair safe to read without a lock. The plugin writes node
        // then serial; reading serial then node means a value torn between the two reads is
        // seen as an OLD serial with a NEW node, which the follower simply does not act on,
        // rather than a new serial pointing at a node that has not landed yet.
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockLocalDialogueNodeSerial, out nodeSerial);
        ok &= _memory.TryReadInt32(_pluginBlock + BlockLocalDialogueNode, out nodeIndex);

        byte[] log = new byte[DialoguePickLogBytes];
        if (_memory.TryRead(_pluginBlock + BlockLocalDialoguePicks, log))
        {
            pickLog = log;
        }

        // Item 1 (docs/research/dialogue-sync.md's 2026-08-23 section): latched by the plugin
        // for the whole conversation's duration, so a torn read here at worst holds the
        // previous conversation's bit for one tick — the same tolerance every other field on
        // this call already has.
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockLocalDialogueForced, out uint forcedRaw);
        forced = forcedRaw != 0;

        partnerTag ??= string.Empty;
        return ok;
    }

    /// <summary>
    /// Reads the object the local player is interacting with, so the session can claim it on
    /// their behalf. An empty tag means "nothing claimed" and is the ordinary case.
    /// </summary>
    public bool TryReadLocalInteraction(out string tag, out uint kind, out bool active, out uint targetId)
    {
        tag = string.Empty;
        kind = 0;
        active = false;
        targetId = 0;

        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryReadAsciiString(
            _pluginBlock + BlockLocalInteractionTag, BlockInteractionTagLength, out tag);
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockLocalInteractionKind, out kind);
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockLocalInteractionActive, out uint live);
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockLocalInteractionTargetId, out targetId);

        tag ??= string.Empty;
        active = live != 0;
        return ok;
    }

    /// <summary>
    /// Hands the plugin every object currently held, by anyone. The COUNT is written LAST:
    /// the plugin acts on it, and it must never see a count that includes an entry whose tag
    /// has not landed yet — the same ordering rule as the dialogue active flag below.
    /// </summary>
    public bool WriteInteractionLocks(IReadOnlyList<InteractionLock> locks)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        int count = Math.Min(locks?.Count ?? 0, BlockInteractionLockCapacity);
        bool ok = true;

        for (int i = 0; i < count; i++)
        {
            InteractionLock entry = locks![i];
            nint slot = _pluginBlock + BlockInteractionLocks + (i * BlockInteractionLockStride);
            nint targetIdSlot = _pluginBlock + BlockInteractionLockTargetIds
                + (i * BlockInteractionLockTargetIdStride);

            ok &= _memory.TryWrite(slot, entry.PeerId);
            ok &= _memory.TryWrite(slot + 4, (uint)entry.Kind);
            ok &= _memory.TryWriteAsciiString(slot + 8, entry.Tag ?? string.Empty, BlockInteractionTagLength);
            ok &= _memory.TryWrite(targetIdSlot, entry.TargetId);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockInteractionLockCount, (uint)count);
        return ok;
    }

    /// <summary>
    /// Reads the plugin's own pending explicit container-lock arbitration request, if any. The
    /// serial is read FIRST, then the target id — the mirror image of the plugin's own write
    /// order (target id first, serial last, see <c>SharedBlock.Dialogue.cs</c>'s remarks), which
    /// is what makes the pair safe to read without a lock: a value torn between the two reads is
    /// seen as an OLD serial paired with a NEW id, and the caller's own "have I already
    /// forwarded this serial" check simply treats it as nothing new yet, rather than acting on a
    /// serial/id pair that never actually existed together.
    /// </summary>
    public bool TryReadContainerLockRequest(out uint targetId, out uint requestSerial)
    {
        targetId = 0;
        requestSerial = 0;

        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryReadUInt32(_pluginBlock + BlockLocalContainerLockRequestSerial, out requestSerial);
        ok &= _memory.TryReadUInt32(_pluginBlock + BlockLocalContainerLockRequestTargetId, out targetId);
        return ok;
    }

    /// <summary>
    /// Hands the plugin the arbiter's answer to one request. The granted bit is written LAST,
    /// after the serial it answers — same ordering rule as every other agent-&gt;plugin pair in
    /// this file (e.g. <see cref="WriteRemoteDialogue"/>'s active flag): the plugin must never
    /// see a matching serial with a stale granted bit still sitting behind it.
    /// </summary>
    public bool WriteContainerLockResponse(uint requestSerial, bool granted)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryWrite(_pluginBlock + BlockContainerLockResponseSerial, requestSerial);
        ok &= _memory.TryWrite(_pluginBlock + BlockContainerLockResponseGranted, granted ? 1u : 0u);
        return ok;
    }

    /// <summary>
    /// Tells the plugin which conversation a peer owns, so it can open the same one locally
    /// and follow the owner's picks. The active flag is written LAST: the plugin acts on it,
    /// and it must never see "follow a conversation" before it knows whose.
    /// </summary>
    public bool WriteRemoteDialogue(
        bool active, string partnerTag, uint replySerial, int replyIndex, byte[]? pickLog,
        int nodeIndex, uint nodeSerial, bool forced)
        => WriteRemoteDialogueWithResref(active, partnerTag, replySerial, replyIndex,
            pickLog, nodeIndex, nodeSerial, forced, string.Empty);

    public bool WriteRemoteDialogueWithResref(
        bool active, string partnerTag, uint replySerial, int replyIndex, byte[]? pickLog,
        int nodeIndex, uint nodeSerial, bool forced, string dialogResref)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = _memory.TryWriteAsciiString(
            _pluginBlock + BlockRemoteDialoguePartnerTag, partnerTag ?? string.Empty, BlockDialoguePartnerTagLength);

        if (pickLog is { Length: DialoguePickLogBytes })
        {
            ok &= _memory.TryWrite(_pluginBlock + BlockRemoteDialoguePicks, pickLog);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockRemoteDialogueReplyIndex, (uint)replyIndex);
        ok &= _memory.TryWrite(_pluginBlock + BlockRemoteDialogueReplySerial, replySerial);

        // Node before its serial, so the plugin never sees a bumped serial pointing at a node
        // that has not arrived — the same ordering rule as the active flag below, and for the
        // same reason: the serial is what the far side acts on.
        ok &= _memory.TryWrite(_pluginBlock + BlockRemoteDialogueNode, (uint)nodeIndex);
        ok &= _memory.TryWrite(_pluginBlock + BlockRemoteDialogueNodeSerial, nodeSerial);

        // Item 1 (docs/research/dialogue-sync.md's 2026-08-23 section): written BEFORE the
        // active flag below, same reasoning as node/serial — the plugin's auto-join check
        // (InteractionLockController.TryForceOpen) reads this alongside RemoteDialogueActive,
        // so it must never see "active" before "forced" has landed for that same conversation.
        ok &= _memory.TryWrite(_pluginBlock + BlockRemoteDialogueForced, forced ? 1u : 0u);
        ok &= _memory.TryWriteAsciiString(_pluginBlock + BlockRemoteDialogueResref,
            dialogResref ?? string.Empty, BlockDialogueResrefLength);

        ok &= _memory.TryWrite(_pluginBlock + BlockRemoteDialogueActive, active ? 1u : 0u);
        return ok;
    }
}
